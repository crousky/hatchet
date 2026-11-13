using System.Diagnostics;
using System.Text;

namespace Factory.Engine.Verification;

public sealed record CommandResult(string Command, int ExitCode, bool TimedOut, string Output, TimeSpan Duration)
{
    public bool Success => ExitCode == 0 && !TimedOut;
}

public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string command, string workingDirectory, TimeSpan timeout, Action<string>? onLine, CancellationToken ct);
}

/// <summary>Runs a shell command with a timeout, output capture and whole-tree kill.</summary>
public sealed class ShellCommandRunner : ICommandRunner
{
    private const int MaxCapturedChars = 400_000;

    // Set by "dotnet run" and the web host; they would change how the target repo builds and runs.
    private static readonly string[] InheritedVariablesToClear =
    [
        "ASPNETCORE_ENVIRONMENT", "ASPNETCORE_URLS", "DOTNET_ENVIRONMENT", "DOTNET_LAUNCH_PROFILE",
        "MSBUILD_EXE_PATH", "MSBuildSDKsPath", "MSBuildExtensionsPath", "MSBuildLoadMicrosoftTargetsReadOnly"
    ];

    private static readonly string[] SecretVariableNames =
    [
        "ANTHROPIC_API_KEY", "OPENAI_API_KEY", "GITHUB_TOKEN", "GH_TOKEN", "NPM_TOKEN", "NUGET_API_KEY",
        "AWS_SESSION_TOKEN", "AWS_SECRET_ACCESS_KEY"
    ];

    private static readonly string[] SecretVariableSuffixes = ["_KEY", "_TOKEN", "_SECRET", "_PASSWORD", "_PAT"];
    private static readonly string[] SecretVariableFragments = ["SECRET", "PASSWORD", "CREDENTIAL"];

    /// <summary>True for variables that look like credentials, which a build or test command must not inherit.</summary>
    internal static bool IsSecretVariable(string name) =>
        SecretVariableNames.Contains(name, StringComparer.OrdinalIgnoreCase)
        || SecretVariableSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase))
        || SecretVariableFragments.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase));

    public async Task<CommandResult> RunAsync(string command, string workingDirectory, TimeSpan timeout, Action<string>? onLine, CancellationToken ct)
    {
        var info = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (OperatingSystem.IsWindows())
        {
            info.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            info.Arguments = $"/d /s /c \"{command}\"";
        }
        else
        {
            info.FileName = "/bin/sh";
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add(command);
        }

        foreach (var name in InheritedVariablesToClear) info.Environment.Remove(name);
        foreach (var name in info.Environment.Keys.Where(IsSecretVariable).ToList()) info.Environment.Remove(name);
        info.Environment["CI"] = "true";
        info.Environment["NO_COLOR"] = "1";
        info.Environment["DOTNET_NOLOGO"] = "1";
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        // Persistent MSBuild and compiler servers inherit the output pipes and would otherwise keep them open after exit.
        info.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        info.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        info.Environment["UseSharedCompilation"] = "false";

        var stopwatch = Stopwatch.StartNew();
        var capture = new OutputCapture(MaxCapturedChars, onLine);
        using var process = new Process { StartInfo = info, EnableRaisingEvents = true };

        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stdoutClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult();
        process.OutputDataReceived += (_, e) => { if (e.Data is null) stdoutClosed.TrySetResult(); else capture.Add(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is null) stderrClosed.TrySetResult(); else capture.Add(e.Data); };

        // On Windows the job kills everything the command started when it closes, however the command ends.
        using var job = ProcessJob.TryCreate();
        process.Start();
        var assigned = job?.TryAssign(process) ?? false;
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(timeout);

        var timedOut = false;
        try
        {
            await exited.Task.WaitAsync(timeoutSource.Token);
            // Background children are not waited for. Without a job (Unix, or assignment failed) they are killed here best-effort.
            if (!assigned) KillTree(process);
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            job?.Dispose();
            ct.ThrowIfCancellationRequested();
            timedOut = true;
            capture.Add($"[Hatchet] Timed out after {timeout.TotalSeconds:0} seconds; the process tree was killed.");
        }

        job?.Dispose();

        // A grandchild can hold the pipes open past exit, so draining is bounded.
        await Task.WhenAny(Task.WhenAll(stdoutClosed.Task, stderrClosed.Task), Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None));

        return new CommandResult(command, timedOut ? -1 : process.ExitCode, timedOut, capture.ToString(), stopwatch.Elapsed);
    }

    private static void KillTree(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private sealed class OutputCapture(int maxChars, Action<string>? onLine)
    {
        private readonly object _gate = new();
        private readonly StringBuilder _buffer = new();
        private bool _dropped;

        public void Add(string line)
        {
            lock (_gate)
            {
                _buffer.Append(line).Append('\n');
                if (_buffer.Length > maxChars)
                {
                    // Keep the tail, where failures and summaries usually are.
                    _buffer.Remove(0, _buffer.Length - maxChars * 3 / 4);
                    _dropped = true;
                }

                // stdout and stderr arrive on separate threads. Calling back under the lock hands listeners one
                // line at a time, in the same order as the captured output.
                onLine?.Invoke(line);
            }
        }

        public override string ToString()
        {
            lock (_gate)
            {
                return _dropped ? $"[earlier output dropped]\n{_buffer}" : _buffer.ToString();
            }
        }
    }
}
