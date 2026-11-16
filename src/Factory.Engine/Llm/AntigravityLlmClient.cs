using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Factory.Contracts;

namespace Factory.Engine.Llm;

public sealed record AntigravityStatus(
    bool Installed,
    string? Version,
    bool LoggedIn,
    string? Account,
    double? GeminiQuota,
    double? WeeklyQuota,
    string? Error,
    string? Quota = null);

/// <summary>
/// Answers agent calls with Gemini through the Antigravity CLI (<c>agy</c>). It uses whatever
/// account agy is signed in with, so no API key is needed. Tools are switched off, so Gemini only
/// answers: patches still go through Hatchet's checks, git and plan approval like the local model's.
/// </summary>
public sealed partial class AntigravityLlmClient(string? workingDirectory = null) : ILlmClient
{
    internal static readonly string DefaultWorkingDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hatchet", "antigravity");

    private readonly string _workingDirectory = workingDirectory ?? DefaultWorkingDirectory;

    [GeneratedRegex(@"^[A-Za-z0-9\-]+$")]
    private static partial Regex SafeIdPattern();

    public async Task<LlmResult> StreamAsync(FactorySettings settings, LlmRequest request, Action<LlmDelta>? onDelta, CancellationToken ct)
    {
        var agentsDirectory = Path.Combine(_workingDirectory, ".agents", "agents");
        Directory.CreateDirectory(agentsDirectory);

        // The system prompt goes in an agent file: prompts and schemas together can pass Windows' command-line limit.
        var agentName = $"hatchet-{request.Profile.Role}-{GetHash(request.Profile.SystemPrompt)}";
        var systemPromptFile = Path.Combine(agentsDirectory, $"{agentName}.md");
        var schemaFile = SchemaFile(request, _workingDirectory);
        var denyAllFile = DenyAllFile(_workingDirectory);

        if (!File.Exists(systemPromptFile))
            await WriteIfMissingAsync(systemPromptFile, AgentFile(agentName, request), ct);

        if (!File.Exists(schemaFile))
            await WriteIfMissingAsync(schemaFile, request.Schema.Json, ct);

        if (!File.Exists(denyAllFile))
            await WriteIfMissingAsync(denyAllFile, DenyAllContent, ct);

        string? conversationId = null;
        try
        {
            var executable = ResolveExecutable(settings.AntigravityPath);
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = _workingDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                StandardInputEncoding = new UTF8Encoding(false)
            };
            ClaudeCodeSafety.RejectUnsafeShimArguments(executable, VariableArguments(request, systemPromptFile));
            foreach (var argument in Arguments(request, systemPromptFile, _workingDirectory)) start.ArgumentList.Add(argument);

            using var process = StartOrThrow(start, settings.AntigravityPath);
            using var kill = ct.Register(() => Kill(process));

            var stallTimeout = TimeSpan.FromSeconds(Math.Max(10, settings.LlmStallTimeoutSeconds));
            using var stall = new CancellationTokenSource(stallTimeout);
            using var stallKill = stall.Token.Register(() => Kill(process));

            // Write the prompt while reading the output. agy prints its startup events before it reads
            // stdin, so writing a large prompt first fills both pipes and each side waits on the other forever.
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            var write = WritePromptAsync(process, request.UserPrompt);

            var parser = new AgyStreamParser(onDelta);
            while (await process.StandardOutput.ReadLineAsync(CancellationToken.None) is { } line)
            {
                stall.CancelAfter(stallTimeout);
                parser.Accept(line);
            }
            conversationId = parser.ConversationId;
            await process.WaitForExitAsync(CancellationToken.None);
            await write;
            ct.ThrowIfCancellationRequested();

            if (stall.IsCancellationRequested && !parser.Finished)
                throw new LlmException($"agy sent nothing for {stallTimeout.TotalSeconds:0} seconds.");
            return parser.Result(process.ExitCode, await stderr);
        }
        finally
        {
            DeleteConversation(conversationId);
        }
    }

    /// <summary>The agent file for one role: front matter that leaves only the <c>finish</c> tool, then the system prompt as the body.</summary>
    internal static string AgentFile(string agentName, LlmRequest request) =>
        $"---\nname: {agentName}\ndescription: Hatchet agent for {request.Profile.Role}\ntools: [finish]\nmainAgent: true\nsubagent: false\ncommandExecutionPolicy: off\n---\n\n{request.Profile.SystemPrompt}";

    private static string DenyAllFile(string directory) => Path.Combine(directory, "deny-all.json");

    private const string DenyAllContent =
        "{\"permissions\":{\"deny\":[\"read_file\",\"write_file\",\"read_url\",\"execute_url\",\"command\",\"mcp\"]}}";

    private static string SchemaFile(LlmRequest request, string directory) =>
        Path.Combine(directory, $"schema-{GetHash(request.Schema.Json)}.json");

    // Parallel calls can write the same content-named file at once; the content is identical, so a lost race is harmless.
    private static async Task WriteIfMissingAsync(string path, string content, CancellationToken ct)
    {
        try
        {
            await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), ct);
        }
        catch (IOException) when (File.Exists(path))
        {
        }
    }

    private static void DeleteConversation(string? conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId) || !SafeIdPattern().IsMatch(conversationId)) return;
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity-cli");
            var dbPath = Path.Combine(root, "conversations", $"{conversationId}.db");
            if (File.Exists(dbPath)) File.Delete(dbPath);
            var lockPath = Path.Combine(root, "presence", $"{conversationId}.lock");
            if (File.Exists(lockPath)) File.Delete(lockPath);
            var brainPath = Path.Combine(root, "brain", conversationId);
            if (Directory.Exists(brainPath) && (File.GetAttributes(brainPath) & FileAttributes.ReparsePoint) == 0)
                Directory.Delete(brainPath, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (ArgumentException) { }
    }

    private static async Task WritePromptAsync(Process process, string prompt)
    {
        try
        {
            var json = new JsonObject
            {
                ["event"] = "user",
                ["message"] = new JsonObject
                {
                    ["content"] = prompt
                }
            };
            var line = json.ToJsonString() + "\n";
            await process.StandardInput.WriteAsync(line.AsMemory(), CancellationToken.None);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // agy exited before reading the prompt (a login failure, say). Its output says why.
        }
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(AntigravityOptions.Models);

    /// <summary>The command line for one call. The prompt itself goes on stdin. The agent name is the agent file's name.</summary>
    internal static IReadOnlyList<string> Arguments(LlmRequest request, string systemPromptFile, string? workingDirectory = null)
    {
        var dir = workingDirectory ?? DefaultWorkingDirectory;
        return
        [
            "--model", request.Profile.AntigravityModel,
            "--agent", Path.GetFileNameWithoutExtension(systemPromptFile),
            "--settings", DenyAllFile(dir),
            "--json-schema", SchemaFile(request, dir),
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--disable-slash-commands"
        ];
    }

    /// <summary>The arguments of <see cref="Arguments"/> that carry outside values, for the check on a <c>.cmd</c> shim.</summary>
    internal static IReadOnlyList<string> VariableArguments(LlmRequest request, string systemPromptFile) =>
        [request.Profile.AntigravityModel, Path.GetFileNameWithoutExtension(systemPromptFile)];

    /// <summary>Whether agy is installed and logged in, from <c>agy --version</c> and <c>agy --output-format json -p=/usage</c>.</summary>
    public static async Task<AntigravityStatus> CheckAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var executable = ResolveExecutable(path);
            var version = (await RunAsync(executable, ["--version"], ct)).Trim();
            var usageJson = await RunAsync(executable, ["--output-format", "json", "-p=/usage"], ct);
            return ParseUsage(usageJson, version.Split(' ')[0]);
        }
        catch (LlmException ex)
        {
            return new AntigravityStatus(false, null, false, null, null, null, ex.Message);
        }
        catch (JsonException)
        {
            return new AntigravityStatus(true, null, false, null, null, null, "agy didn't report its login status.");
        }
    }

    public static AntigravityStatus ParseUsage(string usageJson, string version)
    {
        var parsed = JsonNode.Parse(usageJson);
        var usage = (parsed?["result"] as JsonObject) ?? (parsed as JsonObject);
        var status = StringOrNull(usage?["status"]);
        var groups = usage?["command"]?["data"]?["groups"] as JsonArray;
        var response = StringOrNull(usage?["response"]);
        var accounts = usage?["accounts"] as JsonArray;

        var loggedIn = (string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase) && (groups is { Count: > 0 } || !string.IsNullOrWhiteSpace(response)))
                       || accounts is { Count: > 0 };
        var account = StringOrNull(accounts is { Count: > 0 } ? accounts[0]?["email"] : null)
                      ?? StringOrNull(usage?["email"])
                      ?? StringOrNull(usage?["account"]?["email"]);

        double? geminiQuota = DoubleOrNull(usage?["quota"]?["gemini"]?["remaining"]);
        double? weeklyQuota = DoubleOrNull(usage?["quota"]?["weekly"]?["remaining"]);
        var summaries = new List<string>();

        if (groups is { Count: > 0 })
        {
            foreach (var group in groups)
            {
                var groupName = StringOrNull(group?["name"]) ?? "";
                if (groupName.Equals("Gemini Models", StringComparison.OrdinalIgnoreCase)) groupName = "Gemini";
                else if (groupName.Equals("Claude and GPT models", StringComparison.OrdinalIgnoreCase)) groupName = "Claude & GPT";

                var bucketParts = new List<string>();
                foreach (var bucket in group?["buckets"] as JsonArray ?? [])
                {
                    var id = StringOrNull(bucket?["id"]);
                    var window = StringOrNull(bucket?["window"]);
                    var fraction = DoubleOrNull(bucket?["remaining_fraction"]);
                    if (fraction is null) continue;
                    if (id == "gemini-5h") geminiQuota ??= fraction;
                    if (id == "gemini-weekly") weeklyQuota ??= fraction;
                    bucketParts.Add(string.IsNullOrWhiteSpace(window) ? $"{Math.Round(fraction.Value * 100):0}%" : $"{Math.Round(fraction.Value * 100):0}% ({window})");
                }
                if (groupName.Length > 0 && bucketParts.Count > 0)
                    summaries.Add($"{groupName}: {string.Join(", ", bucketParts)}");
            }
        }
        else if (!string.IsNullOrWhiteSpace(response))
        {
            var lines = response.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
            var joined = string.Join(" · ", lines);
            if (joined.Length > 0) summaries.Add(joined.Length <= 240 ? joined : joined[..240] + "…");
        }

        var quotaSummary = summaries.Count > 0 ? string.Join(" · ", summaries) : null;
        return new AntigravityStatus(true, version, loggedIn, loggedIn ? account : null, geminiQuota, weeklyQuota, null, loggedIn ? quotaSummary : null);
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
        if (timeout.IsCancellationRequested) throw new LlmException("agy didn't answer within 20 seconds.");
        return output;
    }

    internal static Process StartOrThrow(ProcessStartInfo start, string path)
    {
        try
        {
            return Process.Start(start) ?? throw new LlmException($"Could not start agy ({path}).");
        }
        catch (Win32Exception)
        {
            throw new LlmException($"agy isn't installed, or \"{path}\" isn't on PATH. Install it, or set its full path in Settings.");
        }
    }

    internal static void Kill(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (Win32Exception) { }
    }

    private static string GetHash(string content)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }

    private static string? StringOrNull(JsonNode? node) =>
        node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;

    private static double? DoubleOrNull(JsonNode? node) =>
        node?.GetValueKind() == JsonValueKind.Number ? node.GetValue<double>() : null;

    /// <summary>Uses the configured path; a bare name that isn't on PATH falls back to the installer's folder.</summary>
    internal static string ResolveExecutable(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) path = "agy";
        if (Path.IsPathRooted(path)) return path;

        var fallbackPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agy", "bin", "agy.exe");
        if (!OperatingSystem.IsWindows() || !File.Exists(fallbackPath)) return path;

        // The configured name wins when it is on PATH.
        var name = path;
        var extensions = Path.HasExtension(name) ? new[] { "" } : new[] { ".exe", ".cmd", ".bat", "" };
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var directory in directories)
        {
            foreach (var extension in extensions)
            {
                try
                {
                    if (File.Exists(Path.Combine(directory.Trim('"'), name + extension))) return path;
                }
                catch (ArgumentException) { }
            }
        }
        return fallbackPath;
    }
}

/// <summary>Reads agy's stream-json output: streams the answer text as it arrives, then takes the structured answer.</summary>
internal sealed class AgyStreamParser(Action<LlmDelta>? onDelta)
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly StringBuilder _text = new();
    private JsonObject? _result;

    public bool Finished => _result is not null;

    public string? ConversationId { get; private set; }

    public bool Accept(string line)
    {
        if (line.Length == 0 || line[0] != '{') return false;
        JsonNode? message;
        try { message = JsonNode.Parse(line); }
        catch (JsonException) { return false; }

        switch (Str(message?["event"]) ?? Str(message?["type"]))
        {
            case "init":
                var init = message?["init"] as JsonObject ?? message as JsonObject;
                ConversationId = Str(message?["conversation_id"]) ?? Str(init?["conversation_id"]) ?? ConversationId;
                onDelta?.Invoke(new LlmDelta("Gemini is working. agy doesn't stream its thoughts, so the answer appears when it's done.", IsThinking: true));
                break;

            case "step_update":
                var step = message?["step_update"] as JsonObject ?? message as JsonObject;
                ConversationId = Str(step?["conversation_id"]) ?? ConversationId;
                if (Str(step?["step_type"]) == "agent_response")
                {
                    var text = Str(step?["text_delta"]) ?? (Str(step?["delta"]?["type"]) == "text_delta" ? Str(step?["delta"]?["text"]) : null);
                    if (text is { Length: > 0 })
                    {
                        _text.Append(text);
                        onDelta?.Invoke(new LlmDelta(text, IsThinking: false));
                    }
                }
                break;

            case "result":
                _result = message?["result"] as JsonObject ?? message as JsonObject;
                ConversationId = Str(_result?["conversation_id"]) ?? Str(message?["conversation_id"]) ?? ConversationId;
                break;
        }

        return _result is not null;
    }

    public LlmResult Result(int exitCode, string stderr)
    {
        if (_result is null)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? $"it exited with code {exitCode}" : Trim(ErrorLine(stderr));
            throw new LlmException($"agy stopped without an answer: {detail}");
        }

        var status = Str(_result["status"]);
        if (status is not null && !string.Equals(status, "SUCCESS", StringComparison.OrdinalIgnoreCase))
        {
            var error = Str(_result["error"]);
            throw new LlmException(Explain(string.IsNullOrWhiteSpace(error) ? $"status {status}" : error));
        }

        var answer = _result["structured_output"] ?? _result["response"];
        var content = answer is null ? _text.ToString()
            : answer.GetValueKind() == JsonValueKind.String ? answer.GetValue<string>()
            : answer.ToJsonString();

        if (string.IsNullOrWhiteSpace(content))
        {
            if (_result["denied_actions"] is JsonArray { Count: > 0 } denied)
            {
                var names = denied.Select(DeniedName).Where(n => n.Length > 0);
                throw new LlmException($"agy refused an action and gave no answer: {string.Join(", ", names)}.");
            }
            var error = Str(_result["error"]);
            throw new LlmException(string.IsNullOrWhiteSpace(error) ? "agy returned no content." : Explain(error));
        }

        var usage = _result["usage"];
        var promptTokens = Int(usage?["input_tokens"]) + Int(usage?["cache_read_tokens"]);
        var completionTokens = Int(usage?["output_tokens"]);
        return new LlmResult(content, string.Empty, promptTokens, completionTokens, "stop", _stopwatch.Elapsed);
    }

    private static string DeniedName(JsonNode? node) =>
        node is null ? ""
        : node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>()
        : Str(node["name"]) ?? Str(node["action"]) ?? node.ToJsonString();

    private static string ErrorLine(string stderr)
    {
        foreach (var line in stderr.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("AGY_ERROR:", StringComparison.Ordinal)) return trimmed;
        }
        return stderr.Trim();
    }

    private static string Explain(string error)
    {
        var message = $"agy reported: {Trim(error)}";
        return error.Contains("authenticat", StringComparison.OrdinalIgnoreCase) || error.Contains("log in", StringComparison.OrdinalIgnoreCase)
                                                                               || error.Contains("login", StringComparison.OrdinalIgnoreCase)
                                                                               || error.Contains("sign in", StringComparison.OrdinalIgnoreCase)
            ? $"{message} Run \"agy\" in a terminal to sign in, then resume the run."
            : message;
    }

    private static string? Str(JsonNode? node) => node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;

    private static int Int(JsonNode? node) => node?.GetValueKind() == JsonValueKind.Number ? node.GetValue<int>() : 0;

    private static string Trim(string text) => text.Length <= 500 ? text : text[..500] + "…";
}
