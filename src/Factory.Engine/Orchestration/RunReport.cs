using System.Text.Json;
using System.Text.RegularExpressions;
using Factory.Contracts;

namespace Factory.Engine.Orchestration;

/// <summary>What a run did, for whoever picks the work up after it: a person writing the pull request, or the session that planned it.</summary>
/// <param name="Steps">Every subtask in order, with what it changed.</param>
/// <param name="Skipped">The subtasks someone chose to skip, so their work is not in the branch.</param>
/// <param name="Unfinished">The subtasks that are neither done nor skipped: still to build, waiting for a person, or cancelled.</param>
/// <param name="FilesChanged">The files the run's branch changes, against the commit it started from.</param>
/// <param name="UnactionedSuggestions">What the reviewer suggested on a change it passed. Nothing acted on these.</param>
public sealed record RunReport(
    int RunId, string Title, string Status, string? Branch, string? BaseBranch, bool? FinalCheckPassed, string? MergeCommit,
    IReadOnlyList<RunReportStep> Steps, IReadOnlyList<RunReportSkipped> Skipped, IReadOnlyList<RunReportSkipped> Unfinished,
    IReadOnlyList<RunReportFile> FilesChanged, IReadOnlyList<RunReportSuggestion> UnactionedSuggestions);

/// <param name="Summary">The summary the agent that wrote the change gave, or null when nothing was built.</param>
/// <param name="AddedByHatchet">True for the subtask Hatchet adds itself when the final check fails.</param>
public sealed record RunReportStep(
    int Order, string Title, string Status, string? Summary, string? Commit, int FixAttempts, string Tier, bool AddedByHatchet, IReadOnlyList<RunReportFile> Files);

public sealed record RunReportSkipped(int Order, string Title, string Status);

/// <param name="Change">added, modified, deleted or renamed.</param>
public sealed record RunReportFile(string Path, string Change);

public sealed record RunReportSuggestion(int Step, string StepTitle, string Suggestion);

public static partial class RunReportBuilder
{
    /// <summary>
    /// Builds the report from what the run has stored, so it reads the same after the branch is merged or deleted.
    /// It can be asked for at any time: a run that isn't finished reports what it has done so far.
    /// </summary>
    public static RunReport Build(FactoryRun run, IReadOnlyList<FactorySubtask> subtasks, IReadOnlyList<DiffSnapshot> diffs)
    {
        var ordered = subtasks.OrderBy(s => s.Order).ToList();
        var steps = ordered.Select(s => new RunReportStep(
            s.Order, s.Title, s.Status.ToString(),
            // A subtask that was never built has nothing to summarise, and a final-check fix starts with a placeholder.
            s.Status == SubtaskStatus.Completed && !string.IsNullOrWhiteSpace(s.SummaryOfChanges) ? s.SummaryOfChanges.Trim() : null,
            s.CommitSha, s.FixAttempts, SubtaskTiers.Parse(s.Tier) ?? SubtaskTiers.Local, s.IsIntegrationFix,
            CommittedDiff(s, diffs) is { } diff ? FilesIn(diff) : [])).ToList();

        // The whole-run diff exists once the final check has passed. Before that, the commits so far say what changed.
        var whole = diffs.Where(d => d.SubtaskId is null && d.Stage == PipelineStage.Integrate).MaxBy(d => d.Id);
        var files = whole is not null ? FilesIn(whole.UnifiedDiff) : Merge(steps.SelectMany(s => s.Files));

        var suggestions = ordered
            .Where(s => s.Status == SubtaskStatus.Completed)
            .SelectMany(s => SuggestionsOf(s.ReviewJson).Select(text => new RunReportSuggestion(s.Order, s.Title, text)))
            .ToList();

        return new RunReport(run.Id, run.Task.Title, run.Status.ToString(), run.BranchName, run.BaseBranch, run.IntegrationPassed, run.MergeCommit,
            steps,
            ordered.Where(s => s.Status == SubtaskStatus.Skipped).Select(Brief).ToList(),
            ordered.Where(s => s.Status is not (SubtaskStatus.Completed or SubtaskStatus.Skipped)).Select(Brief).ToList(),
            files, suggestions);
    }

    private static RunReportSkipped Brief(FactorySubtask subtask) => new(subtask.Order, subtask.Title, subtask.Status.ToString());

    private static string? CommittedDiff(FactorySubtask subtask, IReadOnlyList<DiffSnapshot> diffs) =>
        subtask.CommitSha is null ? null : diffs.Where(d => d.SubtaskId == subtask.Id && d.CommitSha == subtask.CommitSha).MaxBy(d => d.Id)?.UnifiedDiff;

    /// <summary>The suggestions of the last review, which is the one the change passed. A verdict that can't be read has none.</summary>
    private static IReadOnlyList<string> SuggestionsOf(string? reviewJson)
    {
        if (string.IsNullOrWhiteSpace(reviewJson)) return [];
        try
        {
            var verdict = JsonSerializer.Deserialize<ReviewVerdictResponse>(reviewJson, LlmSchemas.SerializerOptions);
            return verdict is { Passed: true, Suggestions: not null }
                ? verdict.Suggestions.Select(s => s.Trim()).Where(s => s.Length > 0).ToList()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The files a unified diff touches, in the diff's order.</summary>
    public static IReadOnlyList<RunReportFile> FilesIn(string unifiedDiff)
    {
        var files = new List<RunReportFile>();
        foreach (var section in FileSections().Split(unifiedDiff).Where(s => s.StartsWith("diff --git ", StringComparison.Ordinal)))
        {
            // Only the lines before the first hunk describe the file. After it, a line of code could look like one of them.
            var header = section.Split('\n').Select(l => l.TrimEnd('\r')).TakeWhile(l => !l.StartsWith("@@", StringComparison.Ordinal)).ToList();
            string? After(string prefix) => header.FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];

            var added = header.Any(l => l.StartsWith("new file mode", StringComparison.Ordinal));
            var deleted = header.Any(l => l.StartsWith("deleted file mode", StringComparison.Ordinal));
            var renamedTo = After("rename to ");
            var path = renamedTo
                ?? (deleted ? Strip(After("--- "), "a/") : Strip(After("+++ "), "b/"))
                // A file with no text change, such as a binary one or a mode change, has no ---/+++ lines.
                ?? FromGitLine(header[0]);
            if (path is null) continue;
            files.Add(new RunReportFile(path, added ? "added" : deleted ? "deleted" : renamedTo is not null ? "renamed" : "modified"));
        }
        return files;
    }

    /// <summary>One entry per file across several commits: added and later changed is still added, and the last word on deletion wins.</summary>
    private static IReadOnlyList<RunReportFile> Merge(IEnumerable<RunReportFile> files)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var file in files)
        {
            if (!merged.TryGetValue(file.Path, out var earlier))
            {
                order.Add(file.Path);
                merged[file.Path] = file.Change;
            }
            else if (file.Change == "deleted") merged[file.Path] = earlier == "added" ? "" : "deleted";
            else if (earlier is "deleted" or "") merged[file.Path] = earlier == "" ? "added" : "modified";
        }
        // A file the run added and then deleted again isn't a change to the branch.
        return order.Where(p => merged[p].Length > 0).Select(p => new RunReportFile(p, merged[p])).ToList();
    }

    private static string? Strip(string? path, string prefix)
    {
        if (path is null) return null;
        var text = Unquote(path.Split('\t')[0]);
        if (text == "/dev/null") return null;
        return text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..] : text;
    }

    private static string? FromGitLine(string line)
    {
        var at = line.LastIndexOf(" b/", StringComparison.Ordinal);
        return at < 0 ? null : Unquote(line[(at + 3)..]);
    }

    private static string Unquote(string path) => path.Length >= 2 && path[0] == '"' && path[^1] == '"' ? path[1..^1] : path;

    [GeneratedRegex(@"(?m)^(?=diff --git )")]
    private static partial Regex FileSections();
}
