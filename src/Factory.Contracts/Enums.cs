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

/// <summary>What answers an agent's calls: the local model, Claude through the Claude Code CLI, or Gemini through the Antigravity CLI.</summary>
public enum AgentEngine
{
    Local,
    ClaudeCode,
    Antigravity
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
    public const string Edit = "EDIT";
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

    /// <summary>The model Hatchet saves for a last try, when everything else has failed.</summary>
    public const string StrongestModel = "opus";

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

/// <summary>Model ids the Antigravity CLI (<c>agy</c>) accepts when its live list hasn't been read.</summary>
public static partial class AntigravityOptions
{
    public const string DefaultModel = "gemini-3.8-flash-high";

    public static readonly IReadOnlyList<string> Models =
    [
        "gemini-3.8-flash-low",
        "gemini-3.8-flash-medium",
        "gemini-3.8-flash-high",
        "gemini-4-hydrogen-low",
        "gemini-4-hydrogen-medium",
        "gemini-4-hydrogen-high",
        "gemini-4-hydrogen-xhigh",
        "gemini-4-argon-low",
        "gemini-4-argon-medium",
        "gemini-4-argon-high"
    ];

    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:/\-]*$")]
    private static partial System.Text.RegularExpressions.Regex IdPattern();

    public static bool IsValidModelId(string? id) => !string.IsNullOrWhiteSpace(id) && IdPattern().IsMatch(id);

    /// <summary>Label for an Antigravity model id: "gemini-4-argon-high" reads "Gemini 4 Argon · high".</summary>
    public static string ModelLabel(string model)
    {
        if (string.IsNullOrWhiteSpace(model)) return model;
        var parts = model.Split('-', StringSplitOptions.RemoveEmptyEntries).ToList();
        string? effort = null;
        if (parts.Count > 1 && parts[^1] is "low" or "medium" or "high" or "xhigh" or "max")
        {
            effort = parts[^1];
            parts.RemoveAt(parts.Count - 1);
        }
        var name = string.Join(' ', parts.Select(p =>
            p.Equals("gpt", StringComparison.OrdinalIgnoreCase) || p.Equals("oss", StringComparison.OrdinalIgnoreCase)
                ? p.ToUpperInvariant()
                : p.Length > 0 && char.IsLetter(p[0]) ? char.ToUpperInvariant(p[0]) + p[1..] : p));
        return effort is null ? name : $"{name} · {effort}";
    }
}
