using Factory.Contracts;

namespace Factory.Engine.Llm;

public sealed record LlmRequest(AgentProfile Profile, string UserPrompt, LlmSchema Schema);

public sealed record LlmDelta(string Text, bool IsThinking);

public sealed record LlmResult(
    string Content,
    string Thinking,
    int PromptTokens,
    int CompletionTokens,
    string? FinishReason,
    TimeSpan Duration);

public interface ILlmClient
{
    Task<LlmResult> StreamAsync(FactorySettings settings, LlmRequest request, Action<LlmDelta>? onDelta, CancellationToken ct);

    Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, CancellationToken ct);

    /// <summary>The context window the server loaded the model with, or null when the server doesn't say.</summary>
    Task<int?> GetContextLengthAsync(string baseUrl, string modelId, CancellationToken ct) => Task.FromResult<int?>(null);
}

/// <summary>The endpoint could not produce a usable answer. The run pauses rather than fails, so the user can restart the server and resume.</summary>
public sealed class LlmException(string message, Exception? inner = null) : Exception(message, inner);
