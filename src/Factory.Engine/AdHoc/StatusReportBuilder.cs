using System.Text;
using Factory.Contracts;
using Factory.Engine.Orchestration;

namespace Factory.Engine.AdHoc;

/// <summary>One run's state, as the report needs it.</summary>
public sealed record RunReportInput(
    FactoryRun Run,
    IReadOnlyList<FactorySubtask> Subtasks,
    IReadOnlyList<string> RecentEvents,
    WorkEstimate? Estimate);

/// <summary>What a status report is built from. Gathered by the caller, so building it needs no git, database or model.</summary>
public sealed record StatusReportInput(
    string RepositoryPath,
    DateTime GeneratedAt,
    string? Branch,
    string? HeadSha,
    IReadOnlyList<string> UncommittedChanges,
    IReadOnlyList<string> RecentCommits,
    string? GitProblem,
    IReadOnlyList<RunReportInput> Runs,
    IReadOnlyList<AdHocRequest> Requests);

/// <summary>Writes the status report for a repository: its state, its runs and what each one is waiting for.</summary>
public static class StatusReportBuilder
{
    private const int MaxListed = 8;

    public static string Build(StatusReportInput input)
    {
        var builder = new StringBuilder();
        var name = Path.GetFileName(input.RepositoryPath.TrimEnd('/', '\\'));
        builder.Append("# Status report: ").Append(string.IsNullOrEmpty(name) ? input.RepositoryPath : name).Append('\n');
        builder.Append(input.RepositoryPath).Append(" · ").Append(input.GeneratedAt.ToString("yyyy-MM-dd HH:mm")).Append(" UTC\n");

        AppendRepository(builder, input);
        AppendRuns(builder, input);
        AppendRequests(builder, input);
        return builder.ToString().TrimEnd() + "\n";
    }

    private static void AppendRepository(StringBuilder builder, StatusReportInput input)
    {
        builder.Append("\n## Repository\n");
        if (input.GitProblem is not null)
        {
            builder.Append("- Git couldn't be read: ").Append(input.GitProblem).Append('\n');
            return;
        }

        builder.Append("- On ").Append(input.Branch ?? "an unknown branch");
        if (input.HeadSha is { Length: > 0 }) builder.Append(" at ").Append(input.HeadSha.Length > 8 ? input.HeadSha[..8] : input.HeadSha);
        builder.Append('\n');

        if (input.UncommittedChanges.Count == 0)
        {
            builder.Append("- No uncommitted changes\n");
        }
        else
        {
            builder.Append("- ").Append(input.UncommittedChanges.Count).Append(input.UncommittedChanges.Count == 1 ? " uncommitted change" : " uncommitted changes").Append(":\n");
            foreach (var change in input.UncommittedChanges.Take(MaxListed)) builder.Append("  - ").Append(change).Append('\n');
            if (input.UncommittedChanges.Count > MaxListed) builder.Append("  - and ").Append(input.UncommittedChanges.Count - MaxListed).Append(" more\n");
        }

        if (input.RecentCommits.Count > 0)
        {
            builder.Append("- Latest commits:\n");
            foreach (var commit in input.RecentCommits) builder.Append("  - ").Append(commit).Append('\n');
        }
    }

    private static void AppendRuns(StringBuilder builder, StatusReportInput input)
    {
        builder.Append("\n## Runs\n");
        if (input.Runs.Count == 0)
        {
            builder.Append("No runs in this repository yet.\n");
            return;
        }

        var open = input.Runs.Where(r => !RunStateMachine.IsTerminal(r.Run.Status)).OrderByDescending(r => r.Run.Id).ToList();
        var finished = input.Runs.Where(r => RunStateMachine.IsTerminal(r.Run.Status)).OrderByDescending(r => r.Run.Id).ToList();

        if (open.Count == 0) builder.Append("Nothing is in progress.\n");
        foreach (var report in open) AppendOpenRun(builder, report);

        if (finished.Count > 0)
        {
            builder.Append("\n### Finished\n");
            foreach (var report in finished.Take(MaxListed))
                builder.Append("- #").Append(report.Run.Id).Append(" \"").Append(report.Run.Task.Title).Append("\": ").Append(Label(report.Run.Status))
                    .Append(", ").Append(Done(report.Subtasks)).Append(" subtasks done\n");
            if (finished.Count > MaxListed) builder.Append("- and ").Append(finished.Count - MaxListed).Append(" older runs\n");
        }
    }

    private static void AppendOpenRun(StringBuilder builder, RunReportInput report)
    {
        var (run, subtasks) = (report.Run, report.Subtasks);
        builder.Append("\n### Run #").Append(run.Id).Append(" \"").Append(run.Task.Title).Append("\": ").Append(Label(run.Status));
        if (run.PausedByUser) builder.Append(" (paused by you)");
        builder.Append('\n');
        builder.Append("- Progress: ").Append(Done(subtasks)).Append(" of ").Append(subtasks.Count).Append(" subtasks done\n");

        var current = subtasks.FirstOrDefault(s => s.Id == run.CurrentSubtaskId);
        if (current is not null)
        {
            builder.Append("- Now: ").Append(run.CurrentStage?.ToString() ?? "working").Append(" on subtask ").Append(current.Order).Append(", ").Append(current.Title);
            if (current.FixAttempts > 0) builder.Append(" (").Append(current.FixAttempts).Append(current.FixAttempts == 1 ? " fix attempt" : " fix attempts").Append(')');
            builder.Append('\n');
        }
        else if (run.CurrentStage is { } stage)
        {
            builder.Append("- Now: ").Append(stage).Append('\n');
        }

        if (run.BranchName is not null) builder.Append("- Branch: ").Append(run.BranchName).Append(run.BaseBranch is null ? "" : $" (from {run.BaseBranch})").Append('\n');
        builder.Append("- Tokens used: ").Append((run.PromptTokens + run.CompletionTokens).ToString("N0")).Append('\n');
        if (report.Estimate is { } estimate)
            builder.Append("- Working time left: about ").Append(Span(estimate.Low)).Append(" to ").Append(Span(estimate.High)).Append(" for ")
                .Append(estimate.SubtasksLeft).Append(estimate.SubtasksLeft == 1 ? " subtask" : " subtasks").Append('\n');

        var skipped = subtasks.Where(s => s.Status == SubtaskStatus.Skipped).ToList();
        if (skipped.Count > 0)
            builder.Append("- Skipped: ").Append(string.Join("; ", skipped.Select(s => $"{s.Order} {s.Title}"))).Append('\n');

        var next = subtasks.Where(s => s.Status == SubtaskStatus.Pending).OrderBy(s => s.Order).Take(3).ToList();
        if (next.Count > 0)
            builder.Append("- Next up: ").Append(string.Join("; ", next.Select(s => $"{s.Order} {s.Title}"))).Append('\n');

        if (run.Status == RunStatus.NeedsHuman)
        {
            var brief = PauseBriefBuilder.Build(run, subtasks.FirstOrDefault(s => s.Status == SubtaskStatus.NeedsHuman));
            if (brief is not null)
            {
                builder.Append("- **").Append(brief.NeedsDecision ? "Needs you" : "Waiting").Append(":** ").Append(brief.Headline).Append('\n');
                builder.Append("  - ").Append(brief.Why).Append('\n');
                builder.Append("  - What is needed: ").Append(brief.Ask).Append('\n');
            }
        }
        else if (run.Status == RunStatus.AwaitingPlanApproval)
        {
            builder.Append("- **Needs you:** the plan is waiting for your review.\n");
        }
        else if (run.Status == RunStatus.ReadyToMerge)
        {
            builder.Append("- **Needs you:** every subtask is committed").Append(run.IntegrationPassed == true ? " and the final check passed" : "").Append(". Merge it, prepare a PR, or close it.\n");
        }

        if (report.RecentEvents.Count > 0)
        {
            builder.Append("- Recent events:\n");
            foreach (var evt in report.RecentEvents.TakeLast(5)) builder.Append("  - ").Append(evt).Append('\n');
        }
    }

    private static void AppendRequests(StringBuilder builder, StatusReportInput input)
    {
        if (input.Requests.Count == 0) return;
        builder.Append("\n## Ad hoc requests\n");
        foreach (var request in input.Requests.OrderByDescending(r => r.Id).Take(MaxListed))
        {
            builder.Append("- #").Append(request.Id).Append(' ').Append(KindLabel(request.Kind)).Append(": ").Append(request.Title).Append(", ").Append(request.Status.ToString().ToLowerInvariant());
            if (request.IsActive) builder.Append(" for ").Append(Span(input.GeneratedAt - request.CreatedAt));
            builder.Append('\n');
        }
    }

    public static string KindLabel(RequestKind kind) => kind switch
    {
        RequestKind.StatusReport => "Status report",
        RequestKind.RunApp => "Run the app",
        RequestKind.RunCommand => "Command",
        RequestKind.Ask => "Question",
        RequestKind.Defect => "Defect report",
        RequestKind.Change => "Change request",
        _ => kind.ToString()
    };

    private static int Done(IReadOnlyList<FactorySubtask> subtasks) =>
        subtasks.Count(s => SubtaskStateMachine.IsFinished(s.Status) && s.Status != SubtaskStatus.Cancelled && s.Status != SubtaskStatus.Skipped);

    private static string Label(RunStatus status) => status switch
    {
        RunStatus.Queued => "queued",
        RunStatus.Ingesting => "scanning the repository",
        RunStatus.Decomposing => "planning",
        RunStatus.AwaitingPlanApproval => "waiting for plan review",
        RunStatus.Executing => "building",
        RunStatus.NeedsHuman => "needs you",
        RunStatus.Integrating => "running the final check",
        RunStatus.ReadyToMerge => "ready to merge",
        RunStatus.Merged => "merged",
        RunStatus.Closed => "closed",
        RunStatus.Failed => "failed",
        RunStatus.Cancelled => "cancelled",
        _ => status.ToString()
    };

    private static string Span(TimeSpan span) =>
        span.TotalMinutes < 1 ? "under a minute" :
        span.TotalHours < 1 ? $"{(int)span.TotalMinutes}m" :
        span.TotalHours < 10 ? $"{span.TotalHours:0.#}h" : $"{(int)span.TotalHours}h";
}
