namespace Factory.Contracts;

public enum RunStatus
{
    Queued,
    Ingesting,
    Decomposing,
    AwaitingPlanApproval,
    Executing,
    NeedsHuman,
    Integrating,
    ReadyToMerge,
    Merged,
    Closed,
    Failed,
    Cancelled
}

public enum SubtaskStatus
{
    Pending,
    Refining,
    Building,
    Verifying,
    Fixing,
    Reviewing,
    NeedsHuman,
    Completed,
    Skipped,
    Cancelled
}

public enum PipelineStage
{
    Ingest,
    Decompose,
    Refine,
    Build,
    Verify,
    Review,
    Fix,
    Integrate
}

public enum AgentRole
{
    TaskPlanner,
    ContextSpecifier,
    CodeGenerator,
    StaticReviewer,
    BugFixer
}

public enum AgentLogKind
{
    Info,
    Warning,
    Error,
    LlmCall,
    Command
}

/// <summary>What answers an agent's calls: the local model, or Claude through the Claude Code CLI.</summary>
public enum AgentEngine
{
    Local,
    ClaudeCode
}

public enum LlmProvider
{
    LmStudio,
    Ollama,
    Custom
}

/// <summary>What the human chose when a run is paused for intervention.</summary>
public enum HumanDecision
{
    /// <summary>Re-run verification against the working tree (use after a manual fix) with a fresh retry budget.</summary>
    Reverify,

    /// <summary>Send the last failure plus optional guidance to the Fixer with a fresh retry budget.</summary>
    RetryFixer,

    /// <summary>Discard the subtask's uncommitted changes and redo Refine + Build.</summary>
    RestartSubtask,

    /// <summary>Discard the subtask's uncommitted changes and advance to the next one.</summary>
    SkipSubtask,

    /// <summary>Resume a run paused outside a subtask (LLM offline, interrupted, integration failure).</summary>
    Resume
}

public static class FileActions
{
    public const string Create = "CREATE";
    public const string Overwrite = "OVERWRITE";
    public const string Delete = "DELETE";
}

public static class ReasoningEfforts
{
    public const string Low = "low";
    public const string Medium = "medium";
    public const string XHigh = "xhigh";

    public static readonly IReadOnlyList<string> All = [Low, Medium, XHigh];
}

/// <summary>Model aliases and effort levels the Claude Code CLI accepts.</summary>
public static class ClaudeCodeOptions
{
    public const string DefaultModel = "opus";
    public const string DefaultEffort = "high";

    public static readonly IReadOnlyList<string> Models = ["opus", "sonnet", "haiku"];
    public static readonly IReadOnlyList<string> Efforts = ["low", "medium", "high", "xhigh", "max"];

    public static string ModelLabel(string model) => model switch
    {
        "opus" => "Claude Opus",
        "sonnet" => "Claude Sonnet",
        "haiku" => "Claude Haiku",
        _ => model
    };
}
