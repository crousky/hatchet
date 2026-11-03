using Factory.Contracts;
using Factory.Engine.Orchestration;

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

    /// <summary>"31–71h", "1.5–3h" or "20–45m", rounded to what an estimate can say.</summary>
    public static string Range(TimeSpan low, TimeSpan high)
    {
        if (high.TotalMinutes < 60) return $"{low.TotalMinutes:0}–{Math.Ceiling(high.TotalMinutes):0}m";
        var hours = high.TotalHours < 10 ? "0.#" : "0";
        return low.TotalMinutes < 60
            ? $"{low.TotalMinutes:0}m–{high.TotalHours.ToString(hours)}h"
            : $"{low.TotalHours.ToString(hours)}–{high.TotalHours.ToString(hours)}h";
    }

    /// <summary>What a work estimate is and what it is based on, in a sentence or two.</summary>
    public static string WorkLeft(WorkEstimate estimate) =>
        $"About {Range(estimate.Low, estimate.High)} of working time left for {estimate.SubtasksLeft} subtask{(estimate.SubtasksLeft == 1 ? "" : "s")}, " +
        $"judged by the {estimate.FinishedSubtasks} finished so far, which took {Duration(estimate.AveragePerSubtask)} each on average. " +
        "Time paused or waiting for you doesn't count.";

    /// <summary>"3:40 PM" today, otherwise "Thu 3:40 PM", or the date beyond a week.</summary>
    public static string When(DateTime local) =>
        local.Date == DateTime.Today ? local.ToString("h:mm tt") :
        local.Date < DateTime.Today.AddDays(7) ? local.ToString("ddd h:mm tt") :
        local.ToString("MMM d h:mm tt");

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
