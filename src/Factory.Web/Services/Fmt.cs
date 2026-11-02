using Factory.Contracts;

namespace Factory.Web.Services;

/// <summary>Display text and tones shared across the UI.</summary>
public static class Fmt
{
    /// <summary>"Claude Opus · high" for Claude calls, "xhigh · T 0.6" for the local model.</summary>
    public static string CallParams(string? model, string? effort, double? temperature) => model is not null
        ? $"{ClaudeCodeOptions.ModelLabel(model)} · {effort}"
        : $"{effort} · T {temperature?.ToString("0.0")}";

    public static string Label(RunStatus status) => status switch
    {
        RunStatus.Queued => "Queued",
        RunStatus.Ingesting => "Scanning repo",
        RunStatus.Decomposing => "Planning",
        RunStatus.AwaitingPlanApproval => "Plan review",
        RunStatus.Executing => "Building",
        RunStatus.NeedsHuman => "Needs you",
        RunStatus.Integrating => "Final check",
        RunStatus.ReadyToMerge => "Ready to merge",
        RunStatus.Merged => "Merged",
        RunStatus.Closed => "Closed",
        RunStatus.Failed => "Failed",
        RunStatus.Cancelled => "Cancelled",
        _ => status.ToString()
    };

    public static string Tone(RunStatus status) => status switch
    {
        RunStatus.AwaitingPlanApproval or RunStatus.NeedsHuman => "warn",
        RunStatus.ReadyToMerge or RunStatus.Merged => "good",
        RunStatus.Failed => "bad",
        RunStatus.Closed or RunStatus.Cancelled => "muted",
        _ => "active"
    };

    public static string Label(SubtaskStatus status) => status switch
    {
        SubtaskStatus.NeedsHuman => "Needs you",
        _ => status.ToString()
    };

    public static string Label(AgentRole role) => role switch
    {
        AgentRole.TaskPlanner => "Task Planner",
        AgentRole.ContextSpecifier => "Context Specifier",
        AgentRole.CodeGenerator => "Code Generator",
        AgentRole.StaticReviewer => "Static Reviewer",
        AgentRole.BugFixer => "Bug Fixer",
        _ => role.ToString()
    };

    public static string Icon(AgentRole role) => role switch
    {
        AgentRole.TaskPlanner => "map",
        AgentRole.ContextSpecifier => "file-text",
        AgentRole.CodeGenerator => "hammer",
        AgentRole.StaticReviewer => "eye",
        AgentRole.BugFixer => "wrench",
        _ => "sparkle"
    };

    public static string Tokens(int count) => count switch
    {
        < 1000 => count.ToString(),
        < 1_000_000 => $"{count / 1000.0:0.#}k",
        _ => $"{count / 1_000_000.0:0.##}M"
    };

    public static string Duration(TimeSpan span) =>
        span.TotalSeconds < 60 ? $"{span.TotalSeconds:0.#}s" :
        span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes}m {span.Seconds}s" :
        $"{(int)span.TotalHours}h {span.Minutes}m";

    public static string Duration(long milliseconds) => Duration(TimeSpan.FromMilliseconds(milliseconds));

    public static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        return span.TotalSeconds < 60 ? "just now" :
            span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes}m ago" :
            span.TotalHours < 24 ? $"{(int)span.TotalHours}h ago" :
            span.TotalDays < 14 ? $"{(int)span.TotalDays}d ago" :
            utc.ToLocalTime().ToString("MMM d");
    }

    public static string FolderName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('/', '\\'));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    public static string ShortSha(string? sha) => sha is { Length: > 8 } ? sha[..8] : sha ?? "";

    /// <summary>Live views only need the end of a long stream, which keeps each render small.</summary>
    public static string Tail(string text, int maxChars) =>
        text.Length <= maxChars ? text : "…" + text[^maxChars..];
}
