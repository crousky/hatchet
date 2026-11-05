namespace Factory.Contracts;

/// <summary>
/// How much context the loaded model needs. Hatchet doesn't cap response length, so the context window is the only
/// hard limit, and at high reasoning effort the thinking alone can run to tens of thousands of tokens. A loop is
/// caught by watching the output instead (see RepetitionGuard).
/// </summary>
public static class ContextWindow
{
    /// <summary>Below this, planning and reviews at high effort are usually cut off before they answer.</summary>
    public const int Minimum = 32_768;

    public const int Recommended = 65_536;

    public static bool IsTooSmall(int? contextLength) => contextLength is > 0 and < Minimum;

    /// <summary>
    /// True when a model that filled its window would likely have finished with a bigger one. At the recommended size or
    /// more, an answer that still doesn't fit is too long or never stops, and a bigger window only delays the cut-off.
    /// </summary>
    public static bool BiggerWouldHelp(int contextLength) => contextLength < Recommended * 15 / 16;

    /// <summary>A rough token count for text, at about 3.5 characters a token, which suits code and English prose.</summary>
    public static int EstimateTokens(int characters) => (int)Math.Ceiling(characters / 3.5);

    /// <summary>The room to leave for thinking and the answer at a reasoning effort, when deciding whether a prompt fits.</summary>
    public static int AnswerReserve(string reasoningEffort) => reasoningEffort switch
    {
        ReasoningEfforts.Low => 8_192,
        ReasoningEfforts.Medium => 16_384,
        _ => 32_768
    };

    /// <summary>How to load the model with a bigger window on this server.</summary>
    public static string Advice(LlmProvider provider) => provider switch
    {
        LlmProvider.LmStudio => $"In LM Studio, eject the model and load it again with Context Length set to {Recommended:N0} (at least {Minimum:N0}).",
        LlmProvider.Ollama => $"Restart Ollama with OLLAMA_CONTEXT_LENGTH={Recommended} (at least {Minimum}).",
        _ => $"Load the model with a context window of {Recommended:N0} tokens (at least {Minimum:N0})."
    };
}
