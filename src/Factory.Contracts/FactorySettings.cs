namespace Factory.Contracts;

public sealed class FactorySettings
{
    public const string LmStudioUrl = "http://localhost:1234/v1";
    public const string OllamaUrl = "http://localhost:11434/v1";

    public LlmProvider Provider { get; set; } = LlmProvider.LmStudio;
    public string BaseUrl { get; set; } = LmStudioUrl;
    public string ModelId { get; set; } = "qwen3.8";

    /// <summary>Fixer loops per subtask before the run pauses for a human.</summary>
    public int MaxFixRetries { get; set; } = 3;

    /// <summary>
    /// Failed fix attempts on the local model before the Bug Fixer hands the subtask to Claude through Claude Code.
    /// Local models often guess library APIs; Claude usually knows them. 0 keeps fixes local.
    /// </summary>
    public int EscalateFixesToClaudeAfter { get; set; } = 1;

    /// <summary>
    /// A fix on the local model still running after this many minutes is abandoned and handed to Claude, since a model
    /// that keeps thinking never fails and so never escalates. Applies only when fixes can escalate. 0 sets no limit.
    /// </summary>
    public double LocalFixTimeLimitMinutes { get; set; } = 90;

    /// <summary>
    /// Runs that may work at once when any of their agents use the local model. It serves one request at a time, so
    /// more runs only take turns with it. Runs past the limit wait in line.
    /// </summary>
    public int MaxParallelLocalRuns { get; set; } = 1;

    /// <summary>Runs that may work at once when every agent runs on Claude. Runs past the limit wait in line.</summary>
    public int MaxParallelClaudeRuns { get; set; } = 4;

    /// <summary>Attempts per LLM call, covering transport errors and unparseable output.</summary>
    public int MaxLlmAttempts { get; set; } = 3;

    public int VerifyTimeoutSeconds { get; set; } = 900;

    /// <summary>Abort an LLM call after this long with no streamed data. Local prompt processing can be slow.</summary>
    public int LlmStallTimeoutSeconds { get; set; } = 900;

    public int MaxFileChars { get; set; } = 40_000;
    public int MaxContextChars { get; set; } = 120_000;
    public int MaxDiffChars { get; set; } = 60_000;
    public int MaxLogChars { get; set; } = 24_000;

    public List<AgentProfileOverride> ProfileOverrides { get; set; } = [];

    /// <summary>The Claude Code executable, for roles routed to Claude. A bare name is looked up on PATH.</summary>
    public string ClaudeCodePath { get; set; } = "claude";
}

public sealed class AgentProfileOverride
{
    public AgentRole Role { get; set; }
    public string ReasoningEffort { get; set; } = ReasoningEfforts.Medium;
    public double Temperature { get; set; }
    public AgentEngine Engine { get; set; }
    public string ClaudeModel { get; set; } = ClaudeCodeOptions.DefaultModel;
    public string ClaudeEffort { get; set; } = ClaudeCodeOptions.DefaultEffort;

    /// <summary>A replacement system prompt, or null for the built-in one.</summary>
    public string? SystemPrompt { get; set; }
}
