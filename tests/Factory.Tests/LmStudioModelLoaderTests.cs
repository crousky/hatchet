using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Factory.Engine.Llm;

namespace Factory.Tests;

public class LmStudioModelLoaderTests
{
    private const string Listing = """
        { "models": [
          { "key": "google/gemma", "loaded_instances": [] },
          { "key": "qwen/qwen3.8-27b", "loaded_instances": [
            { "id": "qwen/qwen3.8-27b", "config": { "context_length": 8192, "eval_batch_size": 2048, "parallel": 4, "flash_attention": true, "offload_kv_cache_to_gpu": true } } ] } ] }
        """;

    /// <summary>A fake LM Studio that records each request and answers from a script.</summary>
    private sealed class FakeLmStudio(Func<string, JsonNode?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Path, JsonNode? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(ct));
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add((path, body));
            return respond(path, body);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Unloads_the_model_then_loads_it_with_the_new_window_and_its_old_settings()
    {
        var server = new FakeLmStudio((path, body) => path switch
        {
            "/api/v1/models" => Json(Listing),
            "/api/v1/models/unload" => Json("""{ "instance_id": "qwen/qwen3.8-27b" }"""),
            _ => Json($$"""{ "instance_id": "qwen/qwen3.8-27b", "status": "loaded", "load_config": { "context_length": {{body!["context_length"]}} } }""")
        });

        var applied = await new LmStudioModelLoader(new HttpClient(server)).ReloadAsync("http://localhost:3364/v1", "qwen/qwen3.8-27b", 65536, default);

        Assert.Equal(65536, applied);
        Assert.Equal(["/api/v1/models", "/api/v1/models/unload", "/api/v1/models/load"], server.Requests.Select(r => r.Path));
        Assert.Equal("qwen/qwen3.8-27b", server.Requests[1].Body!["instance_id"]!.GetValue<string>());
        var load = server.Requests[2].Body!;
        Assert.Equal("qwen/qwen3.8-27b", load["model"]!.GetValue<string>());
        Assert.Equal(65536, load["context_length"]!.GetValue<int>());
        Assert.Equal(2048, load["eval_batch_size"]!.GetValue<int>());
        Assert.True(load["flash_attention"]!.GetValue<bool>());
        Assert.Null(load["parallel"]); // Not a load setting the API accepts.
    }

    [Fact]
    public async Task A_failed_load_puts_the_model_back_as_it_was()
    {
        var server = new FakeLmStudio((path, body) => path switch
        {
            "/api/v1/models" => Json(Listing),
            "/api/v1/models/unload" => Json("{}"),
            _ when body!["context_length"]!.GetValue<int>() == 8192 => Json("""{ "status": "loaded" }"""),
            _ => Json("""{ "error": { "message": "Not enough memory" } }""", HttpStatusCode.InternalServerError)
        });

        var error = await Assert.ThrowsAsync<LlmException>(() =>
            new LmStudioModelLoader(new HttpClient(server)).ReloadAsync("http://localhost:3364/v1", "qwen/qwen3.8-27b", 65536, default));

        Assert.Contains("reloaded as before", error.Message);
        Assert.Contains("Not enough memory", error.Message);
        Assert.Equal(8192, server.Requests.Last().Body!["context_length"]!.GetValue<int>());
    }

    [Fact]
    public async Task Explains_unknown_models_and_old_lm_studio_versions()
    {
        var current = new FakeLmStudio((_, _) => Json(Listing));
        var unknown = await Assert.ThrowsAsync<LlmException>(() =>
            new LmStudioModelLoader(new HttpClient(current)).ReloadAsync("http://localhost:1234/v1", "missing", 65536, default));
        Assert.Contains("doesn't list missing", unknown.Message);
        Assert.Single(current.Requests); // Nothing was unloaded.

        var old = new FakeLmStudio((_, _) => Json("{}", HttpStatusCode.NotFound));
        var outdated = await Assert.ThrowsAsync<LlmException>(() =>
            new LmStudioModelLoader(new HttpClient(old)).ReloadAsync("http://localhost:1234/v1", "qwen", 65536, default));
        Assert.Contains("Update LM Studio", outdated.Message);
    }
}
