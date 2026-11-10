using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Factory.Contracts;

namespace Factory.Engine.Llm;

public sealed record ClaudeCodeStatus(bool Installed, string? Version, bool LoggedIn, string? Account, string? Error);

/// <summary>
/// Answers agent calls with Claude by running the Claude Code CLI headless (<c>claude -p</c>). It uses whatever
/// account Claude Code is logged in with, so no API key is needed. Tools are switched off, so Claude only
/// answers: patches still go through Hatchet's checks, git and plan approval like the local model's.
/// </summary>
public sealed class ClaudeCodeLlmClient(string workingDirectory) : ILlmClient
{
    public async Task<LlmResult> StreamAsync(FactorySettings settings, LlmRequest request, Action<LlmDelta>? onDelta, CancellationToken ct)
    {
        Directory.CreateDirectory(workingDirectory);
        // The system prompt goes in a file: prompts and schemas together can pass Windows' command-line limit.
        var systemPromptFile = Path.Combine(workingDirectory, $"system-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(systemPromptFile, request.Profile.SystemPrompt, ct);

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
            foreach (var argument in Arguments(request, systemPromptFile)) start.ArgumentList.Add(argument);
            // Hatchet may itself be started from a Claude Code session, and the child must not think it is nested.
            start.Environment.Remove("CLAUDECODE");

            using var process = StartOrThrow(start, settings.ClaudeCodePath);
            using var kill = ct.Register(() => Kill(process));

            var stallTimeout = TimeSpan.FromSeconds(Math.Max(10, settings.LlmStallTimeoutSeconds));
            using var stall = new CancellationTokenSource(stallTimeout);
            using var stallKill = stall.Token.Register(() => Kill(process));

            // Write the prompt while reading the output. Claude Code prints its startup events before it reads
            // stdin, so writing a large prompt first fills both pipes and each side waits on the other forever.
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            var write = WritePromptAsync(process, request.UserPrompt);

            var parser = new ClaudeStreamParser(onDelta);
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
            return parser.Result(process.ExitCode, await stderr);
        }
        finally
        {
            try { File.Delete(systemPromptFile); } catch (IOException) { }
        }
    }

    private static async Task WritePromptAsync(Process process, string prompt)
    {
        try
        {
            await process.StandardInput.WriteAsync(prompt.AsMemory(), CancellationToken.None);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // Claude Code exited before reading the prompt (a login failure, say). Its output says why.
        }
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(ClaudeCodeOptions.Models);

    /// <summary>The command line for one call. The prompt itself goes on stdin.</summary>
    internal static IReadOnlyList<string> Arguments(LlmRequest request, string systemPromptFile) =>
    [
        "-p",
        "--model", request.Profile.ClaudeModel,
        "--effort", request.Profile.ClaudeEffort,
        "--system-prompt-file", systemPromptFile,
        "--json-schema", request.Schema.Json,
        // A plain model call: no tools, MCP servers, skills or saved session.
        "--tools", "",
        "--strict-mcp-config",
        "--disable-slash-commands",
        "--no-session-persistence",
        "--output-format", "stream-json",
        "--verbose",
        "--include-partial-messages"
    ];

    /// <summary>Whether Claude Code is installed and logged in, from <c>claude --version</c> and <c>claude auth status</c>.</summary>
    public static async Task<ClaudeCodeStatus> CheckAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var version = (await RunAsync(path, ["--version"], ct)).Trim();
            var status = JsonNode.Parse(await RunAsync(path, ["auth", "status"], ct));
            var loggedIn = status?["loggedIn"]?.GetValue<bool>() ?? false;
            var account = StringOrNull(status?["email"]) ?? StringOrNull(status?["account"]?["email"]) ?? StringOrNull(status?["authMethod"]);
            return new ClaudeCodeStatus(true, version.Split(' ')[0], loggedIn, loggedIn ? account : null, null);
        }
        catch (LlmException ex)
        {
            return new ClaudeCodeStatus(false, null, false, null, ex.Message);
        }
        catch (JsonException)
        {
            return new ClaudeCodeStatus(true, null, false, null, "Claude Code didn't report its login status.");
        }
    }

    private static async Task<string> RunAsync(string path, string[] arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo(path) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = StartOrThrow(start, path);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var kill = timeout.Token.Register(() => Kill(process));
        var output = await process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        await process.WaitForExitAsync(CancellationToken.None);
        if (timeout.IsCancellationRequested) throw new LlmException("Claude Code didn't answer within 20 seconds.");
        return output;
    }

    internal static Process StartOrThrow(ProcessStartInfo start, string path)
    {
        try
        {
            return Process.Start(start) ?? throw new LlmException($"Could not start Claude Code ({path}).");
        }
        catch (Win32Exception)
        {
            throw new LlmException($"Claude Code isn't installed, or \"{path}\" isn't on PATH. Install it, or set its full path in Settings.");
        }
    }

    internal static void Kill(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (Win32Exception) { }
    }

    internal static Task WritePromptToAsync(Process process, string prompt) => WritePromptAsync(process, prompt);

    private static string? StringOrNull(JsonNode? node) =>
        node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;
}

/// <summary>Reads Claude Code's <c>stream-json</c> output: streams thinking and text as they arrive, then takes the structured answer.</summary>
internal sealed class ClaudeStreamParser(Action<LlmDelta>? onDelta)
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly StringBuilder _thinking = new();
    private readonly StringBuilder _text = new();
    private JsonNode? _toolAnswer;
    private JsonObject? _result;

    public bool Finished => _result is not null;

    public void Accept(string line)
    {
        if (line.Length == 0 || line[0] != '{') return;
        JsonNode? message;
        try { message = JsonNode.Parse(line); }
        catch (JsonException) { return; }

        switch (Str(message?["type"]))
        {
            case "stream_event":
                // Claude Code doesn't show Opus's thinking, so say it has started rather than look stuck for minutes.
                if (Str(message?["event"]?["content_block"]?["type"]) == "thinking" && _thinking.Length == 0)
                    onDelta?.Invoke(new LlmDelta("Claude is thinking. Claude Code doesn't stream its thoughts, so the answer appears when it's done.", IsThinking: true));
                var delta = message?["event"]?["delta"];
                switch (Str(delta?["type"]))
                {
                    case "thinking_delta" when Str(delta?["thinking"]) is { Length: > 0 } thinking:
                        _thinking.Append(thinking);
                        onDelta?.Invoke(new LlmDelta(thinking, IsThinking: true));
                        break;
                    case "text_delta" when Str(delta?["text"]) is { Length: > 0 } text:
                        _text.Append(text);
                        onDelta?.Invoke(new LlmDelta(text, IsThinking: false));
                        break;
                    // The structured answer arrives as the input of the StructuredOutput tool.
                    case "input_json_delta" when Str(delta?["partial_json"]) is { Length: > 0 } json:
                        onDelta?.Invoke(new LlmDelta(json, IsThinking: false, IsToolInput: true));
                        break;
                }
                break;

            case "assistant":
                foreach (var block in message?["message"]?["content"]?.AsArray() ?? [])
                {
                    if (Str(block?["type"]) == "tool_use" && Str(block?["name"]) == "StructuredOutput") _toolAnswer = block?["input"];
                }
                break;

            case "result":
                _result = message as JsonObject;
                break;
        }
    }

    public LlmResult Result(int exitCode, string stderr)
    {
        if (_result is null)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? $"it exited with code {exitCode}" : Trim(stderr.Trim());
            throw new LlmException($"Claude Code stopped without an answer: {detail}");
        }

        var resultText = Str(_result["result"]);
        if (_result["is_error"]?.GetValue<bool>() == true || Str(_result["subtype"]) is { } subtype && subtype.StartsWith("error", StringComparison.Ordinal))
            throw new LlmException(Explain(resultText ?? Str(_result["subtype"]) ?? "unknown error"));

        var answer = _result["structured_output"] ?? _toolAnswer;
        var content = answer is not null ? answer.ToJsonString() : resultText ?? _text.ToString();

        var usage = _result["usage"];
        var promptTokens = Int(usage?["input_tokens"]) + Int(usage?["cache_creation_input_tokens"]) + Int(usage?["cache_read_input_tokens"]);
        var finishReason = Str(_result["stop_reason"]) == "max_tokens" ? "length" : "stop";
        return new LlmResult(content, _thinking.ToString(), promptTokens, Int(usage?["output_tokens"]), finishReason, _stopwatch.Elapsed);
    }

    private static string Explain(string error)
    {
        var message = $"Claude Code reported: {Trim(error)}";
        return error.Contains("authenticat", StringComparison.OrdinalIgnoreCase) || error.Contains("log in", StringComparison.OrdinalIgnoreCase)
                                                                               || error.Contains("login", StringComparison.OrdinalIgnoreCase)
            ? $"{message} Run \"claude auth login\" in a terminal, then resume the run."
            : message;
    }

    private static string? Str(JsonNode? node) => node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;

    private static int Int(JsonNode? node) => node?.GetValueKind() == JsonValueKind.Number ? node.GetValue<int>() : 0;

    private static string Trim(string text) => text.Length <= 500 ? text : text[..500] + "…";
}
