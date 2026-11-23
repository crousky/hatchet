using System.Diagnostics;

namespace Hatchet.Tray;

/// <summary>
/// Starts the Hatchet server and stops it, cleanly where it can. Everything it starts runs in one job, so a stop,
/// or the tray going away, leaves nothing behind. Its output goes to the log.
/// </summary>
internal sealed class ServerProcess : IDisposable
{
    private readonly Func<string, ProcessStartInfo> _startInfo;
    private readonly LogFile _log;
    private readonly object _gate = new();

    private Process? _process;
    private ProcessJob? _job;
    private EventWaitHandle? _stopEvent;
    private bool _stopRequested;

    /// <param name="startInfo">How to start the server, given the name of the event that asks it to stop.</param>
    public ServerProcess(Func<string, ProcessStartInfo> startInfo, LogFile log)
    {
        _startInfo = startInfo;
        _log = log;
    }

    /// <summary>The server exited without being asked to. Gives its exit code. Raised on a pool thread.</summary>
    public event Action<int>? ExitedUnexpectedly;

    public bool IsRunning
    {
        get
        {
            lock (_gate) return _process is { HasExited: false };
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_process is { HasExited: false }) return;
            Cleanup();

            var stopName = $@"Local\Hatchet.Stop.{Guid.NewGuid():N}";
            var info = _startInfo(stopName);
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.RedirectStandardInput = true;

            _log.Rotate();
            _log.Note($"Starting: {info.FileName} {string.Join(' ', info.ArgumentList.Select(StartAtLogin.Quote))}");

            _stopEvent = new EventWaitHandle(false, EventResetMode.ManualReset, stopName);
            _job = new ProcessJob();
            _stopRequested = false;

            var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) _log.Write(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) _log.Write(e.Data); };
            process.Exited += (_, _) => OnExited(process);

            try
            {
                process.Start();
            }
            catch
            {
                process.Dispose();
                Cleanup();
                throw;
            }

            try
            {
                // dotnet takes far longer to load than this takes, so its builds and Hatchet itself start inside the job.
                _job.Assign(process);
                process.StandardInput.Close();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                // Outside the job, nothing would stop it, and the Hatchet it went on to start would hold the port.
                _log.Note($"Couldn't put the server in its job, so it was ended: {ex.Message}");
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception killError) when (killError is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Already gone.
                }
                process.Dispose();
                Cleanup();
                throw;
            }

            _process = process;
        }
    }

    /// <summary>
    /// Asks the server to shut down and waits for it, then kills whatever is left. With graceful false, or once the
    /// timeout passes, it is killed straight away.
    /// </summary>
    public async Task StopAsync(bool graceful, TimeSpan timeout)
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            if (process is null) return;
            _stopRequested = true;
            if (graceful && !process.HasExited)
            {
                _log.Note("Asking Hatchet to stop");
                _stopEvent?.Set();
            }
        }

        if (graceful && !process.HasExited)
        {
            using var wait = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _log.Note($"Hatchet didn't stop within {timeout.TotalSeconds:0} seconds, so it was ended");
            }
        }

        lock (_gate)
        {
            if (!ReferenceEquals(process, _process)) return;
            _job?.Terminate();
        }

        // Killing the job ends the process too; this waits for that and for the last of its output.
        await process.WaitForExitAsync().ConfigureAwait(false);
        lock (_gate)
        {
            if (!ReferenceEquals(process, _process)) return;
            _log.Note("Stopped");
            Cleanup();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stopRequested = true;
            _job?.Terminate();
            Cleanup();
        }
    }

    private void OnExited(Process process)
    {
        int code;
        lock (_gate)
        {
            if (!ReferenceEquals(process, _process) || _stopRequested) return;
            code = process.ExitCode;
            _log.Note($"Hatchet exited with code {code}");
            // Whatever it left behind, such as build servers, goes with it.
            _job?.Terminate();
            Cleanup();
        }

        ExitedUnexpectedly?.Invoke(code);
    }

    private void Cleanup()
    {
        _process?.Dispose();
        _process = null;
        _job?.Dispose();
        _job = null;
        _stopEvent?.Dispose();
        _stopEvent = null;
    }
}
