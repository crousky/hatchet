using Factory.Contracts;

namespace Factory.Engine.Llm;

/// <summary>Sends each call to the engine its role is set to. Model listing and context checks are about the local server.</summary>
public sealed class RoutingLlmClient(ILlmClient local, ILlmClient claudeCode) : ILlmClient
{
    public Task<LlmResult> StreamAsync(FactorySettings settings, LlmRequest request, Action<LlmDelta>? onDelta, CancellationToken ct) =>
        (request.Profile.UsesClaude ? claudeCode : local).StreamAsync(settings, request, onDelta, ct);

    public Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, CancellationToken ct) => local.ListModelsAsync(baseUrl, ct);

    public Task<int?> GetContextLengthAsync(string baseUrl, string modelId, CancellationToken ct) => local.GetContextLengthAsync(baseUrl, modelId, ct);
}
