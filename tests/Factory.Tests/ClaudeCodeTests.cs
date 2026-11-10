using System.Text.Json.Nodes;
using Factory.Contracts;
using Factory.Engine.Llm;
using Factory.Engine.Orchestration;

namespace Factory.Tests;

public class ClaudeCodeTests
{
    private static readonly AgentProfile Planner = AgentProfiles.TaskPlanner with { Engine = AgentEngine.ClaudeCode, ClaudeModel = "opus", ClaudeEffort = "high" };

    private static string Event(JsonObject delta) =>
        new JsonObject { ["type"] = "stream_event", ["event"] = new JsonObject { ["type"] = "content_block_delta", ["delta"] = delta } }.ToJsonString();

    private const string Success = """
        {"type":"result","subtype":"success","is_error":false,"stop_reason":"end_turn","result":"",
         "structured_output":{"passed":true,"issues":[]},
         "usage":{"input_tokens":12,"cache_creation_input_tokens":3000,"cache_read_input_tokens":500,"output_tokens":420}}
        """;

    [Fact]
    public void Streams_thinking_and_the_answer_then_returns_the_structured_output()
    {
        var deltas = new List<LlmDelta>();
        var parser = new ClaudeStreamParser(deltas.Add);

        parser.Accept("""{"type":"system","subtype":"init","tools":["StructuredOutput"]}""");
        parser.Accept(Event(new JsonObject { ["type"] = "thinking_delta", ["thinking"] = "Checking the diff." }));
        parser.Accept(Event(new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = "{\"passed\":" }));
        parser.Accept("not json");
        parser.Accept(Success.ReplaceLineEndings(""));
        var result = parser.Result(0, "");

        // A tool's arguments are marked, so a reader that only wants the prose, such as a question's live answer, can leave them out.
        Assert.Equal([new LlmDelta("Checking the diff.", true), new LlmDelta("{\"passed\":", false, IsToolInput: true)], deltas);
        Assert.Equal("""{"passed":true,"issues":[]}""", result.Content);
        Assert.Equal("Checking the diff.", result.Thinking);
        Assert.Equal(3512, result.PromptTokens);
        Assert.Equal(420, result.CompletionTokens);
        Assert.Equal("stop", result.FinishReason);
    }

    [Fact]
    public void Says_claude_is_thinking_when_the_thoughts_are_hidden()
    {
        var deltas = new List<LlmDelta>();
        var parser = new ClaudeStreamParser(deltas.Add);

        parser.Accept("""{"type":"stream_event","event":{"type":"content_block_start","content_block":{"type":"thinking","thinking":""}}}""");

        Assert.True(Assert.Single(deltas).IsThinking);
        Assert.Contains("thinking", deltas[0].Text);
    }

    [Fact]
    public void Falls_back_to_the_structured_output_tool_call()
    {
        var parser = new ClaudeStreamParser(null);
        parser.Accept("""{"type":"assistant","message":{"content":[{"type":"tool_use","name":"StructuredOutput","input":{"passed":false,"issues":["x"]}}]}}""");
        parser.Accept("""{"type":"result","subtype":"success","is_error":false,"result":"Done","usage":{}}""");

        Assert.Equal("""{"passed":false,"issues":["x"]}""", parser.Result(0, "").Content);
    }

    [Fact]
    public void A_login_failure_says_how_to_log_in()
    {
        var parser = new ClaudeStreamParser(null);
        parser.Accept("""{"type":"result","subtype":"success","is_error":true,"result":"Failed to authenticate: OAuth session expired and could not be refreshed"}""");

        var error = Assert.Throws<LlmException>(() => parser.Result(1, ""));
        Assert.Contains("OAuth session expired", error.Message);
        Assert.Contains("claude auth login", error.Message);
    }

    [Fact]
    public void Exiting_without_a_result_reports_what_claude_code_printed()
    {
        var error = Assert.Throws<LlmException>(() => new ClaudeStreamParser(null).Result(2, "error: unknown option '--foo'\n"));

        Assert.Contains("unknown option '--foo'", error.Message);
    }

    [Fact]
    public void Runs_claude_headless_with_no_tools_and_the_role_schema()
    {
        var arguments = ClaudeCodeLlmClient.Arguments(new LlmRequest(Planner, "plan it", LlmSchemas.TaskGraph), "system.md").ToList();

        Assert.Equal("-p", arguments[0]);
        Assert.Equal("opus", arguments[arguments.IndexOf("--model") + 1]);
        Assert.Equal("high", arguments[arguments.IndexOf("--effort") + 1]);
        Assert.Equal("system.md", arguments[arguments.IndexOf("--system-prompt-file") + 1]);
        Assert.Equal(LlmSchemas.TaskGraph.Json, arguments[arguments.IndexOf("--json-schema") + 1]);
        Assert.Equal("", arguments[arguments.IndexOf("--tools") + 1]);
        Assert.Contains("--strict-mcp-config", arguments);
        Assert.DoesNotContain("--bare", arguments); // Bare mode ignores the Claude login and wants an API key.
        Assert.DoesNotContain("plan it", arguments); // The prompt goes on stdin.
    }

    [Fact]
    public void Settings_route_a_role_to_claude()
    {
        var settings = new FactorySettings
        {
            ProfileOverrides = [new AgentProfileOverride { Role = AgentRole.StaticReviewer, ReasoningEffort = "xhigh", Temperature = 0.2, Engine = AgentEngine.ClaudeCode, ClaudeModel = "sonnet", ClaudeEffort = "max" }]
        };

        var reviewer = AgentProfiles.Resolve(settings, AgentRole.StaticReviewer);

        Assert.True(reviewer.UsesClaude);
        Assert.Equal("Claude Sonnet · max", reviewer.Describe());
        Assert.False(AgentProfiles.Resolve(settings, AgentRole.TaskPlanner).UsesClaude);
    }

    [Fact]
    public void A_custom_system_prompt_replaces_the_built_in_one_until_cleared()
    {
        var settings = new FactorySettings { ProfileOverrides = [new AgentProfileOverride { Role = AgentRole.BugFixer, ReasoningEffort = "xhigh", Temperature = 0.2, SystemPrompt = "Fix it carefully." }] };
        Assert.Equal("Fix it carefully.", AgentProfiles.Resolve(settings, AgentRole.BugFixer).SystemPrompt);

        settings.ProfileOverrides[0].SystemPrompt = "  ";
        Assert.Equal(AgentProfiles.BugFixer.SystemPrompt, AgentProfiles.Resolve(settings, AgentRole.BugFixer).SystemPrompt);
    }

    [Fact]
    public async Task The_router_sends_each_role_to_its_engine()
    {
        var local = new RecordingClient();
        var claude = new RecordingClient();
        var router = new RoutingLlmClient(local, claude);

        await router.StreamAsync(new FactorySettings(), new LlmRequest(Planner, "", LlmSchemas.TaskGraph), null, default);
        await router.StreamAsync(new FactorySettings(), new LlmRequest(AgentProfiles.CodeGenerator, "", LlmSchemas.FilePatchSet), null, default);

        Assert.Equal(1, claude.Calls);
        Assert.Equal(1, local.Calls);
    }

    [Fact]
    public async Task A_large_prompt_does_not_deadlock_against_startup_output()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TempDirectory();
        // Like Claude Code: a burst of output larger than a pipe buffer, then read stdin, then answer.
        var script = directory.File("fake-claude.cmd");
        File.WriteAllText(script, """
            @echo off
            for /L %%i in (1,1,2000) do echo {"type":"system","subtype":"filler","pad":"................................................"}
            findstr "^" > nul
            echo {"type":"result","subtype":"success","is_error":false,"structured_output":{"passed":true,"issues":[]},"usage":{"input_tokens":7,"output_tokens":3}}
            """.ReplaceLineEndings("\r\n"));
        var settings = new FactorySettings { ClaudeCodePath = script, LlmStallTimeoutSeconds = 60 };
        var prompt = string.Join('\n', Enumerable.Repeat(new string('x', 100), 5000));
        var reviewer = AgentProfiles.StaticReviewer with { Engine = AgentEngine.ClaudeCode };

        var result = await new ClaudeCodeLlmClient(directory.File("work"))
            .StreamAsync(settings, new LlmRequest(reviewer, prompt, LlmSchemas.ReviewVerdict), null, default)
            .WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal("""{"passed":true,"issues":[]}""", result.Content);
        Assert.Equal(7, result.PromptTokens);
    }

    private sealed class RecordingClient : ILlmClient
    {
        public int Calls { get; private set; }

        public Task<LlmResult> StreamAsync(FactorySettings settings, LlmRequest request, Action<LlmDelta>? onDelta, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new LlmResult("{}", "", 0, 0, "stop", TimeSpan.Zero));
        }

        public Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
    }
}
