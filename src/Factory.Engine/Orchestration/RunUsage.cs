namespace Factory.Engine.Orchestration;

/// <summary>
/// The model calls of one subtask (or of the run itself, when <paramref name="SubtaskId"/> is null) on one model, added up.
/// </summary>
/// <param name="Model">The Claude model's alias, or null for the local model.</param>
/// <param name="CostUsd">What Claude Code said the calls cost. Zero for the local model, and for calls from before cost was recorded.</param>
public sealed record UsageRow(int? SubtaskId, string? Model, int Calls, long PromptTokens, long CompletionTokens, double CostUsd);

/// <param name="Engine"><c>local</c> or <c>claude</c>.</param>
/// <param name="Model">The Claude model's alias, such as opus. Null for the local model, whose id Settings holds.</param>
/// <param name="CostUsd">Claude Code's own figure for these calls, in US dollars. Null for the local model.</param>
public sealed record ModelUsage(string Engine, string? Model, int Calls, long PromptTokens, long CompletionTokens, double? CostUsd)
{
    public long Tokens => PromptTokens + CompletionTokens;
}

/// <summary>What a run, or one subtask of it, has used, with the local model and Claude kept apart: one costs time and the other money.</summary>
/// <param name="ClaudeCostUsd">
/// Claude Code's own figure, in US dollars, or null when no call ran on Claude. It is what the calls would cost at
/// API prices, so on a Claude subscription it measures use and is not a bill.
/// </param>
public sealed record RunUsage(long LocalTokens, long ClaudeTokens, double? ClaudeCostUsd, IReadOnlyList<ModelUsage> ByModel)
{
    public long Tokens => LocalTokens + ClaudeTokens;
}

public static class RunUsageBuilder
{
    public const string Local = "local";
    public const string Claude = "claude";

    /// <summary>The whole run's usage: every subtask's calls and the calls made for the run itself, such as planning.</summary>
    public static RunUsage ForRun(IEnumerable<UsageRow> rows) => Build(rows);

    /// <summary>Each subtask's usage by subtask id. A subtask that has made no call yet is left out.</summary>
    public static IReadOnlyDictionary<int, RunUsage> BySubtask(IEnumerable<UsageRow> rows) =>
        rows.Where(r => r.SubtaskId is not null).GroupBy(r => r.SubtaskId!.Value).ToDictionary(g => g.Key, g => Build(g));

    private static RunUsage Build(IEnumerable<UsageRow> rows)
    {
        var byModel = rows
            .GroupBy(r => r.Model)
            .Select(g => new ModelUsage(g.Key is null ? Local : Claude, g.Key, g.Sum(r => r.Calls), g.Sum(r => r.PromptTokens), g.Sum(r => r.CompletionTokens),
                g.Key is null ? null : Math.Round(g.Sum(r => r.CostUsd), 4)))
            // The local model first, then Claude's by name, so the list reads the same each time.
            .OrderBy(u => u.Engine == Local ? 0 : 1).ThenBy(u => u.Model, StringComparer.Ordinal)
            .ToList();
        var claude = byModel.Where(u => u.Engine == Claude).ToList();
        return new RunUsage(
            byModel.Where(u => u.Engine == Local).Sum(u => u.Tokens),
            claude.Sum(u => u.Tokens),
            claude.Count == 0 ? null : Math.Round(claude.Sum(u => u.CostUsd ?? 0), 4),
            byModel);
    }
}
