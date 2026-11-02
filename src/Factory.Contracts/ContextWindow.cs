namespace Factory.Contracts;

/// <summary>
/// How much context the loaded model needs. Hatchet never caps response length, so the context window is
/// the only limit, and at high reasoning effort the thinking alone can run to tens of thousands of tokens.
/// </summary>
public static class ContextWindow
{
    /// <summary>Below this, planning and reviews at high effort are usually cut off before they answer.</summary>
    public const int Minimum = 32_768;

    public const int Recommended = 65_536;

    public static bool IsTooSmall(int? contextLength) => contextLength is > 0 and < Minimum;

    /// <summary>How to load the model with a bigger window on this server.</summary>
    public static string Advice(LlmProvider provider) => provider switch
    {
        LlmProvider.LmStudio => $"In LM Studio, eject the model and load it again with Context Length set to {Recommended:N0} (at least {Minimum:N0}).",
        LlmProvider.Ollama => $"Restart Ollama with OLLAMA_CONTEXT_LENGTH={Recommended} (at least {Minimum}).",
        _ => $"Load the model with a context window of {Recommended:N0} tokens (at least {Minimum:N0})."
    };
}
