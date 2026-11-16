using Factory.Contracts;

namespace Factory.Engine.Llm;

/// <summary>Sends each call to the engine its role is set to. Model listing and context checks are about the local server.</summary>
public sealed class RoutingLlmClient(ILlmClient local, ILlmClient claudeCode, ILlmClient? antigravity = null) : ILlmClient
{
    private readonly ILlmClient _antigravity = antigravity ?? new AntigravityLlmClient();

    public Task<LlmResult> StreamAsync(FactorySettings settings, LlmRequest request, Action<LlmDelta>? onDelta, CancellationToken ct) =>
        request.Profile.Engine switch
        {
            AgentEngine.Local => local.StreamAsync(settings, request, onDelta, ct),
            AgentEngine.ClaudeCode => claudeCode.StreamAsync(settings, request, onDelta, ct),
            AgentEngine.Antigravity => _antigravity.StreamAsync(settings, request, onDelta, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(request.Profile.Engine), request.Profile.Engine, "Unknown engine")
        };

    public Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, CancellationToken ct) => local.ListModelsAsync(baseUrl, ct);

    public Task<int?> GetContextLengthAsync(string baseUrl, string modelId, CancellationToken ct) => local.GetContextLengthAsync(baseUrl, modelId, ct);
}
