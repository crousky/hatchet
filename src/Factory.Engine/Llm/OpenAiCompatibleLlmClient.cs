using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Factory.Contracts;

namespace Factory.Engine.Llm;

/// <summary>
/// Streaming client for the OpenAI-compatible <c>/chat/completions</c> endpoint of LM Studio and Ollama.
/// </summary>
public sealed class OpenAiCompatibleLlmClient(HttpClient http) : ILlmClient
{
    // Servers differ in which extended parameters they accept (Ollama rejects reasoning_effort "xhigh").
    // A 400 steps down a level for the rest of the process rather than failing every call.
    private enum Compatibility { Full, EffortCappedAtHigh, Standard }

    private Compatibility _compatibility = Compatibility.Full;

    public async Task<LlmResult> StreamAsync(FactorySettings settings, LlmRequest request, Action<LlmDelta>? onDelta, CancellationToken ct)
    {
        while (true)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var stallTimeout = TimeSpan.FromSeconds(Math.Max(10, settings.LlmStallTimeoutSeconds));
            timeout.CancelAfter(stallTimeout);

            var compatibility = _compatibility;
            var body = BuildRequestBody(settings, request, compatibility);
            using var message = new HttpRequestMessage(HttpMethod.Post, Url(settings.BaseUrl, "chat/completions"))
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
            };
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            try
            {
                using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.StatusCode == HttpStatusCode.BadRequest && compatibility != Compatibility.Standard)
                {
                    _compatibility = compatibility + 1;
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    // Plain parameters got a 400 too, so they were not the cause (an oversized prompt, say).
                    _compatibility = Compatibility.Full;
                    var error = await response.Content.ReadAsStringAsync(timeout.Token);
                    throw new LlmException($"The model server returned HTTP {(int)response.StatusCode}: {Trim(error)}");
                }

                return await ReadStreamAsync(response, timeout, stallTimeout, onDelta, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new LlmException($"The model server sent nothing for {stallTimeout.TotalSeconds:0} seconds.");
            }
            catch (HttpRequestException ex)
            {
                throw new LlmException($"Could not reach the model server at {settings.BaseUrl}: {ex.Message}", ex);
            }
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var response = await http.GetAsync(Url(baseUrl, "models"), timeout.Token);
            response.EnsureSuccessStatusCode();
            var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            return root?["data"]?.AsArray()
                .Select(m => m?["id"]?.GetValue<string>())
                .OfType<string>()
                .ToList() ?? [];
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LlmException($"No response from {baseUrl} within 5 seconds.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException)
        {
            throw new LlmException($"Could not reach {baseUrl}: {ex.Message}", ex);
        }
    }

    public async Task<int?> GetContextLengthAsync(string baseUrl, string modelId, CancellationToken ct)
    {
        // The OpenAI endpoint doesn't report it, so ask LM Studio's own API, then Ollama's.
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return null;
        var root = new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");

        return await ReadNumberAsync(new Uri(root, "api/v0/models"), node => node?["data"]?.AsArray()
                   .FirstOrDefault(m => Same(m?["id"], modelId))?["loaded_context_length"], ct)
               ?? await ReadNumberAsync(new Uri(root, "api/ps"), node => node?["models"]?.AsArray()
                   .FirstOrDefault(m => Same(m?["name"], modelId) || Same(m?["model"], modelId))?["context_length"], ct);

        static bool Same(JsonNode? name, string modelId) =>
            StringOrNull(name) is { } text && (text == modelId || text == modelId + ":latest");
    }

    private async Task<int?> ReadNumberAsync(Uri url, Func<JsonNode?, JsonNode?> select, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var response = await http.GetAsync(url, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            var value = select(JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token)));
            return value?.GetValueKind() == JsonValueKind.Number && value.GetValue<double>() > 0 ? (int)value.GetValue<double>() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException
                                       || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    internal JsonObject BuildRequestBody(FactorySettings settings, LlmRequest request) =>
        BuildRequestBody(settings, request, _compatibility);

    private static JsonObject BuildRequestBody(FactorySettings settings, LlmRequest request, Compatibility compatibility)
    {
        var profile = request.Profile;
        var body = new JsonObject
        {
            ["model"] = settings.ModelId,
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
            ["temperature"] = profile.Temperature,
            ["top_p"] = profile.TopP,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = profile.SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = request.UserPrompt }),
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = request.Schema.Name,
                    ["strict"] = true,
                    ["schema"] = JsonNode.Parse(request.Schema.Json)
                }
            }
        };

        if (compatibility != Compatibility.Standard)
        {
            body["reasoning_effort"] = compatibility == Compatibility.EffortCappedAtHigh && profile.ReasoningEffort == ReasoningEfforts.XHigh
                ? "high"
                : profile.ReasoningEffort;
            body["preserve_thinking"] = profile.ThinkingActive;
            if (profile.TopK is { } topK) body["top_k"] = topK;
        }

        return body;
    }

    private static async Task<LlmResult> ReadStreamAsync(
        HttpResponseMessage response,
        CancellationTokenSource timeout,
        TimeSpan stallTimeout,
        Action<LlmDelta>? onDelta,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var content = new StringBuilder();
        var thinking = new StringBuilder();
        var splitter = new ThinkTagSplitter();
        var promptTokens = 0;
        var completionTokens = 0;
        string? finishReason = null;

        void Emit(LlmDelta delta)
        {
            (delta.IsThinking ? thinking : content).Append(delta.Text);
            onDelta?.Invoke(delta);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            timeout.CancelAfter(stallTimeout);
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var data = line[5..].Trim();
            if (data == "[DONE]") break;
            if (data.Length == 0) continue;

            JsonNode? chunk;
            try { chunk = JsonNode.Parse(data); }
            catch (JsonException) { continue; }

            if (chunk?["error"] is { } error)
                throw new LlmException($"The model server reported an error: {Trim(error["message"]?.ToString() ?? error.ToJsonString())}");

            if (chunk?["usage"] is JsonObject usage)
            {
                promptTokens = usage["prompt_tokens"]?.GetValue<int>() ?? promptTokens;
                completionTokens = usage["completion_tokens"]?.GetValue<int>() ?? completionTokens;
            }

            var choice = chunk?["choices"]?.AsArray().FirstOrDefault();
            if (choice is null) continue;
            if (choice["finish_reason"]?.GetValueKind() == JsonValueKind.String)
                finishReason = choice["finish_reason"]!.GetValue<string>();

            var delta = choice["delta"];
            var reasoning = StringOrNull(delta?["reasoning_content"]) ?? StringOrNull(delta?["reasoning"]) ?? StringOrNull(delta?["thinking"]);
            if (!string.IsNullOrEmpty(reasoning)) Emit(new LlmDelta(reasoning, IsThinking: true));

            var text = StringOrNull(delta?["content"]);
            if (!string.IsNullOrEmpty(text))
            {
                foreach (var part in splitter.Push(text)) Emit(part);
            }
        }

        foreach (var part in splitter.Flush()) Emit(part);

        return new LlmResult(content.ToString(), thinking.ToString(), promptTokens, completionTokens, finishReason, stopwatch.Elapsed);
    }

    private static string? StringOrNull(JsonNode? node) =>
        node?.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : null;

    private static Uri Url(string baseUrl, string path) => new($"{baseUrl.TrimEnd('/')}/{path}");

    private static string Trim(string text) => text.Length <= 500 ? text : text[..500] + "…";
}
