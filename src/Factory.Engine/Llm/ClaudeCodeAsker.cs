using System.Diagnostics;
using System.Text;
using Factory.Contracts;

namespace Factory.Engine.Llm;

/// <summary>Changes to how a question is asked: another brief, and more folders it may read, such as one holding screenshots.</summary>
public sealed record AskOptions(string? SystemPrompt = null, IReadOnlyList<string>? ExtraDirectories = null, string? Model = null);

/// <summary>Answers a free-form question about a repository.</summary>
public interface IClaudeAsker
{
    /// <summary>
    /// Asks Claude and returns its answer. <paramref name="onText"/> gets the answer as it streams.
    /// Throws <see cref="LlmException"/> when Claude Code can't answer.
    /// </summary>
    Task<string> AskAsync(FactorySettings settings, string repositoryPath, string context, string question, Action<string> onText, CancellationToken ct, AskOptions? options = null);
}

/// <summary>
/// Asks Claude Code, headless, to answer a question about a repository. It can read the repository's files and
/// search them and nothing else: no edits, no commands, no web. It runs from a folder of Hatchet's own, with the
/// repository added as data, so it never picks up the repository's CLAUDE.md, settings or hooks.
/// </summary>
public sealed class ClaudeCodeAsker(string workingDirectory) : IClaudeAsker
{
    private const string Model = "sonnet";
    private const string Effort = "medium";

    private const string SystemPrompt = """
        You answer questions about a software project for the person who owns it. You can read and search its files and nothing else.
        Hatchet, a tool that builds software in small verified steps, gives you its current status report for the project first.
        Trust the report for what Hatchet is doing, and the files for what the code does. Say so when you can't tell.
        Answer directly and briefly, in plain language. Name files and subtask numbers when they help. Don't suggest edits unless asked.
        """;

    public async Task<string> AskAsync(FactorySettings settings, string repositoryPath, string context, string question, Action<string> onText, CancellationToken ct, AskOptions? options = null)
    {
        Directory.CreateDirectory(workingDirectory);
        var systemPromptFile = Path.Combine(workingDirectory, $"ask-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(systemPromptFile, options?.SystemPrompt ?? SystemPrompt, ct);

        try
        {
            var start = new ProcessStartInfo(settings.ClaudeCodePath)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                StandardInputEncoding = new UTF8Encoding(false)
            };
            foreach (var argument in Arguments(repositoryPath, systemPromptFile, options?.ExtraDirectories, options?.Model)) start.ArgumentList.Add(argument);
            start.Environment.Remove("CLAUDECODE");

            using var process = ClaudeCodeLlmClient.StartOrThrow(start, settings.ClaudeCodePath);
            using var kill = ct.Register(() => ClaudeCodeLlmClient.Kill(process));

            var stallTimeout = TimeSpan.FromSeconds(Math.Max(10, settings.LlmStallTimeoutSeconds));
            using var stall = new CancellationTokenSource(stallTimeout);
            using var stallKill = stall.Token.Register(() => ClaudeCodeLlmClient.Kill(process));

            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            var write = ClaudeCodeLlmClient.WritePromptToAsync(process, $"{context}\n\n---\n\nQuestion: {question}\n");

            var streamed = new StringBuilder();
            var parser = new ClaudeStreamParser(delta =>
            {
                // The arguments of the reads and searches Claude makes aren't part of the answer.
                if (delta.IsThinking || delta.IsToolInput) return;
                streamed.Append(delta.Text);
                onText(delta.Text);
            });
            while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
            {
                stall.CancelAfter(stallTimeout);
                parser.Accept(line);
            }
            await process.WaitForExitAsync(CancellationToken.None);
            await write;
            ct.ThrowIfCancellationRequested();

            if (stall.IsCancellationRequested && !parser.Finished)
                throw new LlmException($"Claude Code sent nothing for {stallTimeout.TotalSeconds:0} seconds.");
            return parser.Result(process.ExitCode, await stderr).Content;
        }
        finally
        {
            try { File.Delete(systemPromptFile); } catch (IOException) { }
        }
    }

    /// <summary>The command line. The context and question go on stdin.</summary>
    internal static IReadOnlyList<string> Arguments(string repositoryPath, string systemPromptFile, IReadOnlyList<string>? extraDirectories = null, string? model = null) =>
    [
        "-p",
        "--model", model ?? Model,
        "--effort", Effort,
        "--system-prompt-file", systemPromptFile,
        // Reading and searching only. Nothing that edits files or runs commands.
        "--tools", "Read,Grep,Glob",
        "--add-dir", repositoryPath,
        .. (extraDirectories ?? []).SelectMany(directory => new[] { "--add-dir", directory }),
        "--strict-mcp-config",
        "--disable-slash-commands",
        "--no-session-persistence",
        "--output-format", "stream-json",
        "--verbose",
        "--include-partial-messages"
    ];
}
