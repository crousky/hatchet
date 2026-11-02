using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Factory.Contracts;
using Factory.Engine.Llm;
using Factory.Engine.Orchestration;

namespace Factory.Tests;

public class LlmClientTests
{
    private sealed class StubHandler(Func<JsonNode, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<JsonNode> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            Requests.Add(body);
            return respond(body);
        }
    }

    private static HttpResponseMessage Sse(params string[] chunks)
    {
        var body = new StringBuilder();
        foreach (var chunk in chunks) body.Append("data: ").Append(chunk).Append("\n\n");
        body.Append("data: [DONE]\n\n");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToString(), Encoding.UTF8, "text/event-stream") };
    }

    private static string Delta(string field, string text) =>
        new JsonObject { ["choices"] = new JsonArray(new JsonObject { ["delta"] = new JsonObject { [field] = text } }) }.ToJsonString();

    private static LlmRequest Request(AgentProfile profile) => new(profile, "do it", LlmSchemas.ReviewVerdict);

    [Fact]
    public async Task Sends_the_stage_profile_and_schema_to_the_configured_single_model()
    {
        var handler = new StubHandler(_ => Sse(Delta("content", "{}")));
        var client = new OpenAiCompatibleLlmClient(new HttpClient(handler));

        await client.StreamAsync(new FactorySettings { ModelId = "qwen3.8" }, Request(AgentProfiles.TaskPlanner), null, default);

        var body = handler.Requests.Single();
        Assert.Equal("qwen3.8", body["model"]!.GetValue<string>());
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.Equal("xhigh", body["reasoning_effort"]!.GetValue<string>());
        Assert.Equal(0.6, body["temperature"]!.GetValue<double>());
        Assert.Equal(0.95, body["top_p"]!.GetValue<double>());
        Assert.Equal(20, body["top_k"]!.GetValue<int>());
        Assert.True(body["preserve_thinking"]!.GetValue<bool>());
        Assert.Equal("json_schema", body["response_format"]!["type"]!.GetValue<string>());
        Assert.Equal("ReviewVerdict", body["response_format"]!["json_schema"]!["name"]!.GetValue<string>());
        Assert.True(body["response_format"]!["json_schema"]!["strict"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Low_effort_uses_instruct_only_sampling()
    {
        var handler = new StubHandler(_ => Sse(Delta("content", "{}")));
        var client = new OpenAiCompatibleLlmClient(new HttpClient(handler));
        var profile = AgentProfiles.CodeGenerator with { ReasoningEffort = ReasoningEfforts.Low };

        await client.StreamAsync(new FactorySettings(), Request(profile), null, default);

        var body = handler.Requests.Single();
        Assert.Equal(0.80, body["top_p"]!.GetValue<double>());
        Assert.Null(body["top_k"]);
        Assert.False(body["preserve_thinking"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(AgentRole.TaskPlanner, "xhigh", 0.6)]
    [InlineData(AgentRole.ContextSpecifier, "medium", 0.4)]
    [InlineData(AgentRole.CodeGenerator, "medium", 0.2)]
    [InlineData(AgentRole.StaticReviewer, "xhigh", 0.3)]
    [InlineData(AgentRole.BugFixer, "xhigh", 0.2)]
    public void Default_profiles_match_the_spec_table(AgentRole role, string effort, double temperature)
    {
        var profile = AgentProfiles.Resolve(new FactorySettings(), role);

        Assert.Equal(effort, profile.ReasoningEffort);
        Assert.Equal(temperature, profile.Temperature);
    }

    [Fact]
    public async Task Streams_content_and_reasoning_fields_separately()
    {
        var handler = new StubHandler(_ => Sse(
            Delta("reasoning_content", "hmm "),
            Delta("reasoning", "ok"),
            Delta("content", "{\"a\""),
            Delta("content", ":1}"),
            """{"choices":[{"delta":{},"finish_reason":"stop"}]}""",
            """{"choices":[],"usage":{"prompt_tokens":11,"completion_tokens":7}}"""));
        var client = new OpenAiCompatibleLlmClient(new HttpClient(handler));
        var deltas = new List<LlmDelta>();

        var result = await client.StreamAsync(new FactorySettings(), Request(AgentProfiles.TaskPlanner), deltas.Add, default);

        Assert.Equal("{\"a\":1}", result.Content);
        Assert.Equal("hmm ok", result.Thinking);
        Assert.Equal(11, result.PromptTokens);
        Assert.Equal(7, result.CompletionTokens);
        Assert.Equal("stop", result.FinishReason);
        Assert.Equal(4, deltas.Count);
    }

    [Fact]
    public async Task Inline_think_tags_split_across_chunks_go_to_thinking()
    {
        var handler = new StubHandler(_ => Sse(
            Delta("content", "<thi"),
            Delta("content", "nk>plan</th"),
            Delta("content", "ink>{\"a\":1}")));
        var client = new OpenAiCompatibleLlmClient(new HttpClient(handler));

        var result = await client.StreamAsync(new FactorySettings(), Request(AgentProfiles.TaskPlanner), null, default);

        Assert.Equal("plan", result.Thinking);
        Assert.Equal("{\"a\":1}", result.Content);
    }

    [Fact]
    public async Task A_400_steps_down_extended_parameters_and_stays_down()
    {
        var handler = new StubHandler(body =>
            body["reasoning_effort"]?.GetValue<string>() == "xhigh"
                ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("invalid reasoning_effort") }
                : Sse(Delta("content", "{}")));
        var client = new OpenAiCompatibleLlmClient(new HttpClient(handler));

        await client.StreamAsync(new FactorySettings(), Request(AgentProfiles.TaskPlanner), null, default);
        await client.StreamAsync(new FactorySettings(), Request(AgentProfiles.TaskPlanner), null, default);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("high", handler.Requests[1]["reasoning_effort"]!.GetValue<string>());
        Assert.Equal("high", handler.Requests[2]["reasoning_effort"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_400_that_is_not_about_parameters_surfaces_and_restores_them()
    {
        var calls = 0;
        var handler = new StubHandler(_ => ++calls <= 3
            ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("context length exceeded") }
            : Sse(Delta("content", "{}")));
        var client = new OpenAiCompatibleLlmClient(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<LlmException>(() => client.StreamAsync(new FactorySettings(), Request(AgentProfiles.TaskPlanner), null, default));
        await client.StreamAsync(new FactorySettings(), Request(AgentProfiles.TaskPlanner), null, default);

        Assert.Contains("context length exceeded", error.Message);
        Assert.Equal("xhigh", handler.Requests[3]["reasoning_effort"]!.GetValue<string>());
    }

    [Fact]
    public async Task Connection_failure_becomes_an_LlmException()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("refused"));
        var client = new OpenAiCompatibleLlmClient(new HttpClient(handler));

        await Assert.ThrowsAsync<LlmException>(() => client.StreamAsync(new FactorySettings(), Request(AgentProfiles.TaskPlanner), null, default));
    }

    [Fact]
    public async Task An_error_event_in_the_stream_becomes_an_LlmException()
    {
        var handler = new StubHandler(_ => Sse("""{"error":{"message":"model crashed"}}"""));
        var client = new OpenAiCompatibleLlmClient(new HttpClient(handler));

        var error = await Assert.ThrowsAsync<LlmException>(() => client.StreamAsync(new FactorySettings(), Request(AgentProfiles.TaskPlanner), null, default));

        Assert.Contains("model crashed", error.Message);
    }
    private sealed class RouteHandler(Dictionary<string, string> routes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(routes.TryGetValue(request.RequestUri!.AbsolutePath, out var json)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    [Fact]
    public async Task Reads_the_loaded_context_length_from_lm_studio()
    {
        var client = new OpenAiCompatibleLlmClient(new HttpClient(new RouteHandler(new()
        {
            ["/api/v0/models"] = """{ "data": [ { "id": "other", "loaded_context_length": 4096 }, { "id": "qwen/qwen3.8-27b", "state": "loaded", "max_context_length": 262144, "loaded_context_length": 8192 } ] }"""
        })));

        Assert.Equal(8192, await client.GetContextLengthAsync("http://localhost:3364/v1", "qwen/qwen3.8-27b", default));
    }

    [Fact]
    public async Task Falls_back_to_ollama_and_to_unknown()
    {
        var client = new OpenAiCompatibleLlmClient(new HttpClient(new RouteHandler(new()
        {
            ["/api/ps"] = """{ "models": [ { "name": "qwen3.8:latest", "model": "qwen3.8:latest", "context_length": 4096 } ] }"""
        })));

        Assert.Equal(4096, await client.GetContextLengthAsync("http://localhost:11434/v1", "qwen3.8", default));
        Assert.Null(await client.GetContextLengthAsync("http://localhost:11434/v1", "not-loaded", default));
    }
}
