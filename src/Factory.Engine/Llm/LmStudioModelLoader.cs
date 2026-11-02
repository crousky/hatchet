using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Factory.Engine.Llm;

/// <summary>
/// Reloads a model in LM Studio with a different context window, through LM Studio's own REST API
/// (<c>/api/v1/models</c>), so fixing a too-small window doesn't mean a trip to LM Studio.
/// </summary>
public sealed class LmStudioModelLoader(HttpClient http)
{
    // The load settings worth keeping from the current instance. Everything else falls back to LM Studio's defaults.
    private static readonly string[] CarriedOver = ["flash_attention", "eval_batch_size", "offload_kv_cache_to_gpu", "num_experts"];

    private sealed record LoadedInstance(string Id, JsonObject Config);

    /// <summary>Unloads every instance of the model and loads it again with the given window. Returns the window LM Studio applied.</summary>
    public async Task<int> ReloadAsync(string baseUrl, string modelId, int contextLength, CancellationToken ct)
    {
        var root = Root(baseUrl);
        var (key, instances) = await FindAsync(root, modelId, ct);
        var previous = instances.FirstOrDefault()?.Config;

        // Unload first: two copies of a large model rarely fit in memory together.
        foreach (var instance in instances)
            await PostAsync(root, "api/v1/models/unload", new JsonObject { ["instance_id"] = instance.Id }, TimeSpan.FromMinutes(1), ct);

        try
        {
            return await LoadAsync(root, key, contextLength, previous, ct);
        }
        catch (LlmException ex) when (previous?["context_length"] is JsonValue old && old.TryGetValue<int>(out var oldLength))
        {
            // A failed load would otherwise leave no model at all, so put back what was there.
            try
            {
                await LoadAsync(root, key, oldLength, previous, CancellationToken.None);
            }
            catch (LlmException)
            {
                throw new LlmException($"LM Studio couldn't load {modelId} with a {contextLength:N0}-token context, or reload it as before. {ex.Message}", ex);
            }
            throw new LlmException($"LM Studio couldn't load {modelId} with a {contextLength:N0}-token context (likely not enough memory), so it was reloaded as before. {ex.Message}", ex);
        }
    }

    private async Task<(string Key, IReadOnlyList<LoadedInstance> Instances)> FindAsync(Uri root, string modelId, CancellationToken ct)
    {
        var listing = await SendAsync(HttpMethod.Get, root, "api/v1/models", null, TimeSpan.FromSeconds(10), ct);
        foreach (var model in listing?["models"]?.AsArray() ?? [])
        {
            var key = model?["key"]?.GetValue<string>();
            var instances = (model?["loaded_instances"]?.AsArray() ?? [])
                .Select(i => new LoadedInstance(i?["id"]?.GetValue<string>() ?? "", i?["config"] as JsonObject ?? []))
                .Where(i => i.Id.Length > 0)
                .ToList();
            if (key is not null && (key == modelId || instances.Any(i => i.Id == modelId))) return (key, instances);
        }
        throw new LlmException($"LM Studio doesn't list {modelId}, so Hatchet can't reload it.");
    }

    private async Task<int> LoadAsync(Uri root, string key, int contextLength, JsonObject? previous, CancellationToken ct)
    {
        var body = new JsonObject { ["model"] = key, ["context_length"] = contextLength, ["echo_load_config"] = true };
        foreach (var name in CarriedOver)
        {
            if (previous?[name] is { } value) body[name] = value.DeepClone();
        }

        // Reading a large model from disk can take minutes.
        var loaded = await PostAsync(root, "api/v1/models/load", body, TimeSpan.FromMinutes(10), ct);
        return loaded?["load_config"]?["context_length"] is JsonValue applied && applied.TryGetValue<int>(out var length) ? length : contextLength;
    }

    private Task<JsonNode?> PostAsync(Uri root, string path, JsonObject body, TimeSpan limit, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, root, path, body, limit, ct);

    private async Task<JsonNode?> SendAsync(HttpMethod method, Uri root, string path, JsonObject? body, TimeSpan limit, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit);
        using var request = new HttpRequestMessage(method, new Uri(root, path));
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        try
        {
            using var response = await http.SendAsync(request, timeout.Token);
            var text = await response.Content.ReadAsStringAsync(timeout.Token);
            if (response.IsSuccessStatusCode) return text.Length == 0 ? null : JsonNode.Parse(text);

            throw new LlmException(response.StatusCode switch
            {
                HttpStatusCode.NotFound when path == "api/v1/models" =>
                    "This version of LM Studio can't load models over its API. Update LM Studio, or change the context length there by hand.",
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    "LM Studio requires an API token for this. Turn off \"Require authentication\" in LM Studio's server settings, or change the context length there by hand.",
                _ => $"LM Studio returned HTTP {(int)response.StatusCode}: {ErrorMessage(text)}"
            });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LlmException($"LM Studio didn't finish within {limit.TotalMinutes:0} minutes.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            throw new LlmException($"Could not reach LM Studio at {root}: {ex.Message}", ex);
        }
    }

    private static string ErrorMessage(string text)
    {
        try
        {
            var error = JsonNode.Parse(text)?["error"];
            var message = error is JsonValue ? error.ToString() : error?["message"]?.ToString();
            if (!string.IsNullOrWhiteSpace(message)) return message;
        }
        catch (JsonException)
        {
        }
        return text.Length <= 300 ? text : text[..300] + "…";
    }

    private static Uri Root(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            ? new Uri(uri.GetLeftPart(UriPartial.Authority) + "/")
            : throw new LlmException($"{baseUrl} isn't a valid server address.");
}
