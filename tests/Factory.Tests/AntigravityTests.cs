using System.Text.Json.Nodes;
using Factory.Contracts;
using Factory.Engine.Llm;
using Factory.Engine.Orchestration;
using Factory.Web.Services;

namespace Factory.Tests;

public class AntigravityTests
{
    private static readonly AgentProfile Planner = AgentProfiles.TaskPlanner with { Engine = AgentEngine.Antigravity, AntigravityModel = "gemini-3.8-flash-high" };

    private const string InitLine = """
        {"event":"init","conversation_id":"b1ca7e1f-3eb4-40f0-afce-30543c245f29","init":{"model":"gemini-3.8-flash-high","agent":"hatchet-test"}}
        """;

    private const string StepLine = """
        {"event":"step_update","step_update":{"conversation_id":"b1ca7e1f-3eb4-40f0-afce-30543c245f29","step_index":1,"state":"DONE","step_type":"agent_response","text_delta":"Checking the diff."}}
        """;

    private const string SuccessLine = """
        {"event":"result","result":{"conversation_id":"b1ca7e1f-3eb4-40f0-afce-30543c245f29","status":"SUCCESS","structured_output":{"passed":true,"issues":[]},"usage":{"input_tokens":12,"output_tokens":420,"thinking_tokens":300,"cache_read_tokens":5}}}
        """;

    [Fact]
    public void Streams_thinking_and_the_answer_then_returns_the_structured_output()
    {
        var deltas = new List<LlmDelta>();
        var parser = new AgyStreamParser(deltas.Add);

        parser.Accept(InitLine.ReplaceLineEndings(""));
        parser.Accept(StepLine.ReplaceLineEndings(""));
        parser.Accept(SuccessLine.ReplaceLineEndings(""));
        var result = parser.Result(0, "");

        Assert.Equal([new LlmDelta("Gemini is working. agy doesn't stream its thoughts, so the answer appears when it's done.", true), new LlmDelta("Checking the diff.", false)], deltas);
        Assert.Equal("{\"passed\":true,\"issues\":[]}", result.Content);
        Assert.Equal(17, result.PromptTokens);
        Assert.Equal(420, result.CompletionTokens);
        Assert.Equal("stop", result.FinishReason);
        Assert.Equal("b1ca7e1f-3eb4-40f0-afce-30543c245f29", parser.ConversationId);
    }

    [Fact]
    public void Falls_back_to_the_response_when_structured_output_is_null()
    {
        var parser = new AgyStreamParser(null);
        parser.Accept("""{"event":"result","result":{"status":"SUCCESS","structured_output":null,"response":"Done","usage":{}}}""");

        Assert.Equal("Done", parser.Result(0, "").Content);
    }

    [Fact]
    public void A_login_failure_says_how_to_log_in()
    {
        var parser = new AgyStreamParser(null);
        parser.Accept("""{"event":"result","result":{"status":"ERROR","error":"Failed to authenticate: OAuth session expired and could not be refreshed"}}""");

        var error = Assert.Throws<LlmException>(() => parser.Result(1, ""));
        Assert.Contains("OAuth session expired", error.Message);
        Assert.Contains("agy", error.Message);
        Assert.Contains("sign in", error.Message);
    }

    [Fact]
    public void Exiting_without_a_result_reports_what_agy_printed()
    {
        var error = Assert.Throws<LlmException>(() => new AgyStreamParser(null).Result(2, "error: unknown option '--foo'\n"));

        Assert.Contains("unknown option '--foo'", error.Message);
    }

    [Fact]
    public void Parses_usage_status_and_quota_buckets_from_agy()
    {
        const string usageJson = """
            {"conversation_id":"","status":"SUCCESS","response":"Gemini Models\tWeekly Limit Remaining\t99%\t2026-11-23T18:38:59Z\n","command":{"name":"usage","data":{"groups":[{"name":"Gemini Models","buckets":[{"id":"gemini-weekly","name":"Weekly Limit Remaining","window":"weekly","remaining_fraction":0.99},{"id":"gemini-5h","name":"Five Hour Limit Remaining","window":"5h","remaining_fraction":0.98}]},{"name":"Gemini 4 Argon","buckets":[{"id":"gemini-argon-wtu-weekly","name":"Gemini 4 Argon Weekly Limit","window":"weekly","remaining_fraction":0.95}]}]}}}
            """;

        var status = AntigravityLlmClient.ParseUsage(usageJson, "1.5.9");

        Assert.True(status.Installed);
        Assert.Equal("1.5.9", status.Version);
        Assert.True(status.LoggedIn);
        Assert.Equal(0.98, status.GeminiQuota);
        Assert.Equal(0.99, status.WeeklyQuota);
        Assert.Equal("Gemini: 99% (weekly), 98% (5h) · Gemini 4 Argon: 95% (weekly)", status.Quota);
    }

    [Fact]
    public void Parses_models_output_and_skips_the_header_line()
    {
        const string output = "Fetching available models...\ngemini-4-argon-high\tGemini 4 Argon (High)\ngemini-3.8-flash-high\tGemini 3.8 Flash (High)\n";

        var models = AntigravityCatalog.Parse(output);

        Assert.Equal([("gemini-4-argon-high", "Gemini 4 Argon (High)"), ("gemini-3.8-flash-high", "Gemini 3.8 Flash (High)")], models);
    }

    [Fact]
    public void Runs_agy_with_no_tools_and_the_role_schema()
    {
        var arguments = AntigravityLlmClient.Arguments(new LlmRequest(Planner, "plan it", LlmSchemas.TaskGraph), "system.md").ToList();

        Assert.Equal("--model", arguments[0]);
        Assert.Equal("gemini-3.8-flash-high", arguments[1]);
        Assert.Equal("--agent", arguments[2]);
        Assert.Equal("system", arguments[3]);
        Assert.Equal("--settings", arguments[4]);
        Assert.Equal("--json-schema", arguments[6]);
        Assert.Equal("--input-format", arguments[8]);
        Assert.Equal("stream-json", arguments[9]);
        Assert.Equal("--output-format", arguments[10]);
        Assert.Equal("stream-json", arguments[11]);
        Assert.Equal("--disable-slash-commands", arguments[12]);
        Assert.DoesNotContain("plan it", arguments); // The prompt goes on stdin.
    }

    [Fact]
    public void The_agent_file_leaves_only_finish_and_has_the_system_prompt_as_its_body()
    {
        var content = AntigravityLlmClient.AgentFile("hatchet-test", new LlmRequest(Planner, "plan it", LlmSchemas.TaskGraph));

        Assert.StartsWith("---\n", content);
        var end = content.IndexOf("\n---\n", 3, StringComparison.Ordinal);
        Assert.True(end > 0, "The agent file should close its front matter.");
        var frontMatter = content[..end];
        var body = content[(end + 5)..];

        Assert.Contains("name: hatchet-test", frontMatter);
        Assert.Contains("tools: [finish]", frontMatter);
        Assert.DoesNotContain("plan it", content); // The user prompt goes on stdin.
        Assert.Equal(Planner.SystemPrompt, body.TrimStart());
    }

    [Fact]
    public void Settings_route_a_role_to_antigravity()
    {
        var settings = new FactorySettings
        {
            ProfileOverrides = [new AgentProfileOverride { Role = AgentRole.StaticReviewer, ReasoningEffort = "xhigh", Temperature = 0.2, Engine = AgentEngine.Antigravity, AntigravityModel = "gemini-4-argon-high" }]
        };

        var reviewer = AgentProfiles.Resolve(settings, AgentRole.StaticReviewer);

        Assert.True(reviewer.UsesAntigravity);
        Assert.Equal("Gemini 4 Argon · high", reviewer.Describe());
        Assert.False(AgentProfiles.Resolve(settings, AgentRole.TaskPlanner).UsesAntigravity);
    }

    [Fact]
    public void A_run_where_every_role_uses_antigravity_counts_as_cloud()
    {
        var overrides = Enum.GetValues<AgentRole>()
            .Select(role => new AgentProfileOverride { Role = role, ReasoningEffort = "xhigh", Temperature = 0.2, Engine = AgentEngine.Antigravity, AntigravityModel = "gemini-3.8-flash-high" })
            .ToList();
        var settings = new FactorySettings { ProfileOverrides = overrides };

        var profiles = Enum.GetValues<AgentRole>().Select(role => AgentProfiles.Resolve(settings, role)).ToList();

        Assert.All(profiles, profile =>
        {
            Assert.True(profile.UsesAntigravity);
            Assert.False(profile.IsLocal, "An Antigravity agent runs in the cloud, not on the local model.");
        });
        Assert.Equal(WorkerPool.Claude, FactoryOrchestrator.PoolFor(settings));

        // The defaults still run on the local model, and Claude is cloud too.
        Assert.True(AgentProfiles.CodeGenerator.IsLocal);
        Assert.False((AgentProfiles.CodeGenerator with { Engine = AgentEngine.ClaudeCode }).IsLocal);
    }

    [Fact]
    public async Task The_router_sends_each_role_to_its_engine()
    {
        var local = new RecordingClient();
        var claude = new RecordingClient();
        var antigravity = new RecordingClient();
        var router = new RoutingLlmClient(local, claude, antigravity);

        await router.StreamAsync(new FactorySettings(), new LlmRequest(Planner, "", LlmSchemas.TaskGraph), null, default);
        await router.StreamAsync(new FactorySettings(), new LlmRequest(AgentProfiles.CodeGenerator, "", LlmSchemas.FilePatchSet), null, default);

        Assert.Equal(1, antigravity.Calls);
        Assert.Equal(1, local.Calls);
        Assert.Equal(0, claude.Calls);
    }

    [Fact]
    public async Task A_large_prompt_does_not_deadlock_against_startup_output()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var directory = new TempDirectory();
        // Like Claude Code: a burst of output larger than a pipe buffer, then read stdin, then answer.
        var script = directory.File("fake-agy.cmd");
        File.WriteAllText(script, """
            @echo off
            for /L %%i in (1,1,2000) do echo {"event":"init","pad":"................................................"}
            findstr "^" > nul
            echo {"event":"result","result":{"status":"SUCCESS","structured_output":{"passed":true,"issues":[]},"usage":{"input_tokens":7,"output_tokens":3}}}
            """.ReplaceLineEndings("\r\n"));
        var settings = new FactorySettings { AntigravityPath = script, LlmStallTimeoutSeconds = 60 };
        var prompt = string.Join('\n', Enumerable.Repeat(new string('x', 100), 5000));
        var reviewer = AgentProfiles.StaticReviewer with { Engine = AgentEngine.Antigravity };

        var result = await new AntigravityLlmClient(directory.File("work"))
            .StreamAsync(settings, new LlmRequest(reviewer, prompt, LlmSchemas.ReviewVerdict), null, default)
            .WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal("{\"passed\":true,\"issues\":[]}", result.Content);
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
