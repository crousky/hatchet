using Factory.Contracts;
using Factory.Engine.AdHoc;
using Factory.Engine.Orchestration;

namespace Factory.Web.Services;

/// <summary>Display text and tones shared across the UI.</summary>
public static class Fmt
{
    /// <summary>"Claude Opus · high" for Claude calls, "Gemini 4 Argon · high" for Antigravity, "xhigh · T 0.6" for the local model.</summary>
    public static string CallParams(string? model, string? effort, double? temperature)
    {
        if (model is null) return $"{effort} · T {temperature?.ToString("0.0")}";
        if (!ClaudeCodeOptions.Models.Contains(model)) return ModelLabel(model);
        return $"{ClaudeCodeOptions.ModelLabel(model)} · {effort}";
    }

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

    public static string Label(RequestKind kind) => StatusReportBuilder.KindLabel(kind);

    public static string Icon(RequestKind kind) => kind switch
    {
        RequestKind.StatusReport => "clipboard",
        RequestKind.RunApp => "play",
        RequestKind.RunCommand => "terminal",
        RequestKind.Defect => "bug",
        RequestKind.Change => "wrench",
        _ => "sparkle"
    };

    public static string Label(RequestStatus status) => status switch
    {
        RequestStatus.Running => "Running",
        RequestStatus.Succeeded => "Done",
        RequestStatus.Failed => "Failed",
        RequestStatus.Stopped => "Stopped",
        _ => status.ToString()
    };

    public static string Tone(RequestStatus status) => status switch
    {
        RequestStatus.Running => "active",
        RequestStatus.Succeeded => "good",
        RequestStatus.Failed => "bad",
        _ => "muted"
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

    /// <summary>A cost in US dollars: cents for most, and tenths of a cent under ten cents so a small figure doesn't read as nothing.</summary>
    public static string Usd(double cost) => cost is > 0 and < 0.1 ? $"${cost:0.000}" : $"${cost:0.00}";

    /// <summary>A run's or subtask's tokens in a few words, such as "12.3k local · 4.5k Claude ($0.42)".</summary>
    public static string Usage(RunUsage usage)
    {
        var parts = new List<string>();
        if (usage.LocalTokens > 0 || usage.ClaudeTokens == 0) parts.Add($"{Tokens(usage.LocalTokens)} local");
        if (usage.ClaudeTokens > 0) parts.Add($"{Tokens(usage.ClaudeTokens)} Claude" + (usage.ClaudeCostUsd is > 0 ? $" ({Usd(usage.ClaudeCostUsd.Value)})" : ""));
        return string.Join(" · ", parts);
    }

    /// <summary>One line for each model, for a tooltip.</summary>
    public static string UsageDetail(RunUsage usage) =>
        usage.ByModel.Count == 0
            ? "No model has been called yet."
            : string.Join("\n", usage.ByModel.Select(u =>
                $"{(u.Model is null ? "Local model" : ClaudeCodeOptions.Models.Contains(u.Model) ? ClaudeCodeOptions.ModelLabel(u.Model) : ModelLabel(u.Model))}: {u.PromptTokens:N0} prompt and {u.CompletionTokens:N0} completion tokens in {u.Calls} {(u.Calls == 1 ? "call" : "calls")}"
                + (u.CostUsd is > 0 ? $", {Usd(u.CostUsd.Value)}" : "")))
              + (usage.ClaudeCostUsd is > 0 ? "\nThe cost is Claude Code's own figure at API prices. On a subscription it measures use and is not a bill." : "");

    public static string Tokens(long count) => count switch
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

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.0} MB"
    };

    /// <summary>"31–71h", "1.5–3h" or "20–45m", rounded to what an estimate can say.</summary>
    public static string Range(TimeSpan low, TimeSpan high)
    {
        // Rounded first, so that 59.7 minutes reads as an hour rather than "60m".
        var (lowMinutes, highMinutes) = (Math.Round(low.TotalMinutes), Math.Ceiling(high.TotalMinutes));
        if (highMinutes < 60) return $"{lowMinutes:0}–{highMinutes:0}m";
        var hours = highMinutes < 600 ? "0.#" : "0";
        return lowMinutes < 60
            ? $"{lowMinutes:0}m–{(highMinutes / 60).ToString(hours)}h"
            : $"{(lowMinutes / 60).ToString(hours)}–{(highMinutes / 60).ToString(hours)}h";
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

    /// <summary>"2nd in line for a local-model worker", or the run whose turn it waits for.</summary>
    public static string Queue(QueueSpot spot) => spot.BlockedByRunId is { } blocker
        ? $"Waiting for run #{blocker} to finish, as it has this repository checked out"
        : $"{Ordinal(spot.Position)} in line for a {(spot.Pool == WorkerPool.Claude ? "cloud" : "local-model")} worker";

    public static string Ordinal(int n) => (n % 100) switch
    {
        11 or 12 or 13 => $"{n}th",
        _ => (n % 10) switch { 1 => $"{n}st", 2 => $"{n}nd", 3 => $"{n}rd", _ => $"{n}th" }
    };

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

    /// <summary>Antigravity model ids used only when <c>agy models</c> can't be read.</summary>
    public static readonly IReadOnlyList<(string Value, string Label)> AntigravityModels =
        AntigravityOptions.Models.Select(m => (m, ModelLabel(m))).ToList();

    /// <summary>
    /// The Antigravity models to offer: the ones agy reported when there are any, otherwise the built-in list.
    /// Always includes the one currently chosen.
    /// </summary>
    public static IReadOnlyList<(string Value, string Label)> AntigravityChoices(string? current, IReadOnlyList<(string Value, string Label)>? discovered = null)
    {
        var baseList = discovered is { Count: > 0 } ? discovered : AntigravityModels;
        if (string.IsNullOrWhiteSpace(current) || baseList.Any(m => m.Value == current)) return baseList;
        return [.. baseList, (current, ModelLabel(current))];
    }

    /// <summary>Label for an Antigravity model id: "gemini-4-argon-high" reads "Gemini 4 Argon · high".</summary>
    public static string ModelLabel(string model) => AntigravityOptions.ModelLabel(model);
}
