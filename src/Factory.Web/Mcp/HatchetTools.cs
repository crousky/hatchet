using System.ComponentModel;
using Factory.Contracts;
using Factory.Engine.Orchestration;
using Factory.Engine.Workspace;
using Factory.Web.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Factory.Web.Mcp;

/// <summary>One step of a plan handed to Hatchet, as an MCP client writes it.</summary>
public sealed class PlanStep
{
    [Description("Short imperative title, such as \"Add the device repository\".")]
    public string Title { get; set; } = "";

    [Description("What to build, how it fits the architecture and what done looks like, so an engineer with no other context could do it.")]
    public string Description { get; set; } = "";

    [Description("Existing files to change, repository-relative with forward slashes.")]
    public List<string> FilesToModify { get; set; } = [];

    [Description("New files to create, repository-relative with forward slashes.")]
    public List<string> FilesToCreate { get; set; } = [];

    [Description("Files to read for context only.")]
    public List<string> FilesToRead { get; set; } = [];

    [Description("Command that proves this step works, run from the repository root. Leave empty to use the run's check, which Hatchet detects from the repository.")]
    public string? VerificationCommand { get; set; }

    [Description("Names of Hatchet skills whose instructions this step's agents should get, from the repository's .hatchet/skills or .claude/skills folders or the user's own. Leave empty for none.")]
    public List<string>? Skills { get; set; }
}

public sealed record RunSummary(int RunId, string Title, string Status, string Project, string Repository, int SubtasksDone, int Subtasks, string? CurrentStep, string? Queue, string? PauseReason, string Url);

public sealed record SubtaskSummary(int Order, string Title, string Status, int FixAttempts);

public sealed record RunDetails(RunSummary Run, string? Branch, string? BaseBranch, bool? FinalCheckPassed, int Tokens, string? WorkLeft, IReadOnlyList<SubtaskSummary> Subtasks, IReadOnlyList<string> RecentEvents, PauseBrief? NeedsFromYou = null, string Note = McpNotes.Data);

public sealed record SubtaskDetails(
    int Order, string Title, string Status, string Description, int FixAttempts,
    string? SummaryOfChanges, string? LastFailure, IReadOnlyList<string> FixHistory, string? ReviewVerdict, string? Commit, string? LatestDiff, string Note = McpNotes.Data);

/// <summary>
/// Lets an MCP client such as Claude Code drive Hatchet: plan a change in conversation, hand the plan over,
/// then follow the run and step in when it pauses. Everything here is also possible in the UI.
/// </summary>
[McpServerToolType]
public sealed class HatchetTools(FactoryOrchestrator orchestrator, IRunStore store, IConfiguration configuration)
{
    private const int MaxDiffChars = 20_000;

    [McpServerTool(Name = "probe_repository", ReadOnly = true)]
    [Description("Checks whether a folder can host a Hatchet run (a Git repository with a clean working tree) and how Hatchet would verify it. Call this before start_run. If openRunId is set, that run has the repository, and a new run waits in line for it however the tree looks now. " + McpNotes.Data)]
    public async Task<object> ProbeRepository([Description("Absolute path of the repository folder.")] string path, CancellationToken ct)
    {
        PathGuard.RejectNetworkPath(path);
        var probe = await GitWorkspace.ProbeAsync(path, ct);
        var openRun = probe.IsGitRepository ? await orchestrator.GetOpenRunAsync(path, ct) : null;
        return new
        {
            probe.Exists,
            probe.IsGitRepository,
            probe.HasCommits,
            probe.IsClean,
            probe.CurrentBranch,
            Verification = probe.Verification is { } v ? $"{v.Description}: {v.Command}" : "Nothing detected yet. The first subtask should set up the build and a test project.",
            probe.Error,
            OpenRunId = openRun?.Id
        };
    }

    [McpServerTool(Name = "start_run")]
    [Description("""
        Starts a Hatchet run on a local Git repository. Hatchet makes a factory/run-N branch, then builds, verifies, reviews and commits each subtask in order, fixing failures as it goes, on the user's local model.
        Pass the plan you worked out with the user to skip Hatchet's own planner. Without a plan, Hatchet plans from the spec itself, which is slower.
        The working tree must be clean. The run continues in the background: follow it with get_run.
        If the user's limit of runs at once is reached, or another open run has the repository, the run waits in line and starts by itself; queue says why.
        """ + " " + McpNotes.Data)]
    public async Task<RunDetails> StartRun(
        [Description("Absolute path of the repository.")] string repositoryPath,
        [Description("The architecture spec or feature description, in Markdown. Every subtask is built against it.")] string spec,
        [Description("The ordered subtasks to build. Each should be small enough to build and verify on its own.")] List<PlanStep>? plan = null,
        [Description("Short title for the run. Taken from the spec if omitted.")] string? title = null,
        [Description("Command that verifies the whole repository. Omit to let Hatchet detect it (dotnet, npm, cargo and others).")] string? verificationCommand = null,
        [Description("True to stop after planning so the user can review or edit the plan in Hatchet's UI before building.")] bool reviewPlanInHatchet = false,
        CancellationToken ct = default)
    {
        PathGuard.RejectNetworkPath(repositoryPath, nameof(repositoryPath));
        // A client may send null for a list or field it leaves empty.
        var edits = plan?.Select(step => new SubtaskEdit(null, step.Title ?? "", step.Description ?? "",
            step.FilesToModify ?? [], step.FilesToCreate ?? [], step.FilesToRead ?? [], step.VerificationCommand ?? "", step.Skills ?? [])).ToList();
        var run = await Do(() => orchestrator.StartRunAsync(new NewRunRequest(repositoryPath, spec, title, verificationCommand, reviewPlanInHatchet, PlanReviewByUser: false), edits, ct));
        return await DescribeRunAsync(run.Id, ct);
    }

    [McpServerTool(Name = "list_runs", ReadOnly = true)]
    [Description("Lists Hatchet runs, newest first, with their status, progress and project (the repository's folder name). For a run that needs a person, call get_run to see what it needs. " + McpNotes.Data)]
    public async Task<IReadOnlyList<RunSummary>> ListRuns(
        [Description("True to include merged, closed, failed and cancelled runs.")] bool includeFinished = false,
        CancellationToken ct = default)
    {
        var runs = (await store.ListRunsAsync(ct))
            .Where(r => includeFinished || !RunStateMachine.IsTerminal(r.Status))
            .OrderByDescending(r => r.Id)
            .ToList();
        var subtasks = (await store.GetSubtasksForRunsAsync(runs.Select(r => r.Id).ToList(), ct)).ToLookup(s => s.RunId);
        return runs.Select(run => Summarize(run, subtasks[run.Id].ToList())).ToList();
    }

    [McpServerTool(Name = "get_run", ReadOnly = true)]
    [Description("Shows a run's status, its subtasks, recent events and, once a few subtasks are done, how much working time is likely left. If it is waiting for a person, needsFromYou explains what happened, what is blocking it, what to give the agents as guidance and what each resume_run action needs; use get_subtask for the full failure and diff. " + McpNotes.Data)]
    public Task<RunDetails> GetRun([Description("The run's id.")] int runId, CancellationToken ct = default) =>
        DescribeRunAsync(runId, ct);

    [McpServerTool(Name = "get_subtask", ReadOnly = true)]
    [Description("Shows one subtask in full: what it should do, what changed, the last failure, earlier fix attempts, the review verdict and its latest diff. Use it to decide how to help a paused run. " + McpNotes.Data)]
    public async Task<SubtaskDetails> GetSubtask(
        [Description("The run's id.")] int runId,
        [Description("The subtask's position in the plan, starting at 1.")] int order,
        CancellationToken ct = default)
    {
        await RequireRunAsync(runId, ct);
        var subtask = (await store.GetSubtasksAsync(runId, ct)).FirstOrDefault(s => s.Order == order)
                      ?? throw new McpException($"Run {runId} has no subtask {order}.");
        var diff = (await store.GetDiffsAsync(runId, ct)).Where(d => d.SubtaskId == subtask.Id).MaxBy(d => d.Id)?.UnifiedDiff;
        if (diff is { Length: > MaxDiffChars }) diff = diff[..MaxDiffChars] + $"\n[diff truncated at {MaxDiffChars} characters]";

        return new SubtaskDetails(subtask.Order, subtask.Title, subtask.Status.ToString(), subtask.Description, subtask.FixAttempts,
            subtask.SummaryOfChanges, subtask.LastFailure, subtask.FixHistory, subtask.ReviewJson, subtask.CommitSha, diff);
    }

    [McpServerTool(Name = "approve_plan")]
    [Description("""
        Approves the plan of a run waiting for plan review, as it stands, and starts building.
        Verification commands proposed by Hatchet's own planner are dropped, because only the user can approve those, in Hatchet's UI.
        Those subtasks are checked with the run's verification command instead. Commands in a plan you supplied with start_run are kept.
        Only works for a run you started yourself with reviewPlanInHatchet. A plan the user asked to review, such as one for a reported defect, or one longer than Hatchet builds unattended, is approved by the user in Hatchet's UI, and this refuses it.
        """ + " " + McpNotes.Data)]
    public async Task<RunDetails> ApprovePlan([Description("The run's id.")] int runId, CancellationToken ct = default)
    {
        await RequireRunAsync(runId, ct);
        var plan = (await store.GetSubtasksAsync(runId, ct))
            .OrderBy(s => s.Order)
            .Select(s => new SubtaskEdit(s.Id, s.Title, s.Description, s.TargetFilesToModify, s.TargetFilesToCreate, s.ContextFilesToRead,
                s.CommandApproved ? s.VerificationCommand : "", s.Skills))
            .ToList();
        await Do(() => orchestrator.ApprovePlanAsDrivingAgentAsync(runId, plan, ct));
        return await DescribeRunAsync(runId, ct);
    }

    [McpServerTool(Name = "pause_run")]
    [Description("Pauses a working run. By default the current step finishes first, so nothing is lost. With immediately=true the current step stops now and runs again on resume. " + McpNotes.Data)]
    public async Task<RunDetails> PauseRun(
        [Description("The run's id.")] int runId,
        [Description("Stop the current step now rather than after it finishes.")] bool immediately = false,
        CancellationToken ct = default)
    {
        await RequireRunAsync(runId, ct);
        await Do(() => orchestrator.PauseAsync(runId, immediately, ct));
        return await DescribeRunAsync(runId, ct);
    }

    [McpServerTool(Name = "resume_run")]
    [Description("""
        Continues a paused run. action is one of:
        - resume: carry on from where it stopped.
        - retry_fixer: send the last failure, with your guidance, to the Bug Fixer with a fresh budget of attempts. On a failed final check, this hands the check's errors to the Bug Fixer as one more subtask, then runs the final check again.
        - reverify: run the checks again on the working tree as it is, for example after you fixed the code yourself.
        - restart_subtask: discard the subtask's uncommitted changes and build it again.
        - skip_subtask: discard its changes and move on to the next subtask.
        Guidance goes to every agent that works on the subtask from then on: name the root cause or the approach to take, such as "keep the seed file to a few rows".
        Two pauses can only be cleared by the user in Hatchet: a check command that changed after the model's edits, and a file too long for the reviewer to read in full. resume and reverify are refused there.
        """ + " " + McpNotes.Data)]
    public async Task<RunDetails> ResumeRun(
        [Description("The run's id.")] int runId,
        [Description("resume, retry_fixer, reverify, restart_subtask or skip_subtask.")] string action = "resume",
        [Description("Guidance for the agents that work on the subtask next.")] string? guidance = null,
        CancellationToken ct = default)
    {
        var decision = action.Trim().ToLowerInvariant() switch
        {
            "resume" => HumanDecision.Resume,
            "retry_fixer" => HumanDecision.RetryFixer,
            "reverify" => HumanDecision.Reverify,
            "restart_subtask" => HumanDecision.RestartSubtask,
            "skip_subtask" => HumanDecision.SkipSubtask,
            _ => throw new McpException($"Unknown action \"{action}\". Use resume, retry_fixer, reverify, restart_subtask or skip_subtask.")
        };
        await RequireRunAsync(runId, ct);
        // Guidance sent here is the driving agent's, and carries less weight with Hatchet's agents than the user's. retry_fixer is capped.
        await Do(() => orchestrator.ResumeAsDrivingAgentAsync(runId, decision, guidance, ct));
        return await DescribeRunAsync(runId, ct);
    }

    [McpServerTool(Name = "hand_over_call")]
    [Description("""
        Stops the model call a working run is making and makes it again on another model. Use it when an agent is taking far too long,
        for example a local Bug Fixer thinking for an hour. The call starts over on the new model without using an attempt;
        later calls keep the model set in Hatchet's settings. get_run's currentStep and recentEvents show what is running.
        """ + " " + McpNotes.Data)]
    public async Task<RunDetails> HandOverCall(
        [Description("The run's id.")] int runId,
        [Description("local for the local model, or a Claude model: opus, sonnet or haiku.")] string to,
        CancellationToken ct = default)
    {
        await RequireRunAsync(runId, ct);
        await Do(() => orchestrator.HandOverCallAsync(runId, to));
        return await DescribeRunAsync(runId, ct);
    }

    [McpServerTool(Name = "cancel_run", Destructive = true)]
    [Description("Cancels a run for good: discards uncommitted changes and returns the repository to its base branch. Commits stay on the run's branch. Prefer pause_run unless the user wants to abandon the run. " + McpNotes.Data)]
    public async Task<RunDetails> CancelRun([Description("The run's id.")] int runId, CancellationToken ct = default)
    {
        await RequireRunAsync(runId, ct);
        await Do(() => orchestrator.CancelAsync(runId, ct));
        return await DescribeRunAsync(runId, ct);
    }

    private async Task<RunDetails> DescribeRunAsync(int runId, CancellationToken ct)
    {
        var run = await RequireRunAsync(runId, ct);
        var subtasks = await store.GetSubtasksAsync(runId, ct);
        var events = (await store.GetRecentEventsAsync(runId, 12, ct)).Select(l => $"{l.CreatedAt:HH:mm:ss} {l.Message}").ToList();
        var live = orchestrator.GetLiveCall(runId);
        var estimate = RunStateMachine.IsUnderway(run.Status)
            ? WorkEstimator.Estimate(subtasks, await store.GetWorkBySubtaskAsync(runId, ct),
                live?.Call.SubtaskId is { } subtaskId ? (subtaskId, DateTime.UtcNow - live.StartedAt) : null)
            : null;

        return new RunDetails(Summarize(run, subtasks), run.BranchName, run.BaseBranch, run.IntegrationPassed,
            run.PromptTokens + run.CompletionTokens, estimate is null ? null : Fmt.WorkLeft(estimate),
            subtasks.OrderBy(s => s.Order).Select(s => new SubtaskSummary(s.Order, s.Title, s.Status.ToString(), s.FixAttempts)).ToList(),
            events, PauseBriefBuilder.Build(run, subtasks.FirstOrDefault(s => s.Status == SubtaskStatus.NeedsHuman)));
    }

    private RunSummary Summarize(FactoryRun run, IReadOnlyList<FactorySubtask> subtasks)
    {
        var current = subtasks.FirstOrDefault(s => s.Id == run.CurrentSubtaskId);
        var step = run.CurrentStage is { } stage
            ? current is null ? stage.ToString() : $"{stage} on subtask {current.Order}: {current.Title}"
            : null;
        var spot = orchestrator.GetQueueSpot(run.Id);
        var status = spot is not null ? "Queued" : run.PausedByUser ? "Paused" : run.Status.ToString();
        return new RunSummary(run.Id, run.Task.Title, status, Fmt.FolderName(run.Task.RepositoryPath), run.Task.RepositoryPath,
            subtasks.Count(s => SubtaskStateMachine.IsFinished(s.Status) && s.Status != SubtaskStatus.Cancelled), subtasks.Count,
            spot is null && RunStateMachine.IsUnderway(run.Status) ? step : null, spot is null ? null : Fmt.Queue(spot),
            spot is null ? run.PauseReason : null, $"{Program.BaseUrl(configuration)}/runs/{run.Id}");
    }

    private async Task<FactoryRun> RequireRunAsync(int runId, CancellationToken ct)
    {
        try
        {
            return await store.GetRunAsync(runId, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        {
            throw new McpException($"There is no run {runId}. Use list_runs to see the runs.");
        }
    }

    // Validation problems are for the caller to read, so they go back as tool errors with their message.
    private static async Task<T> Do<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (ex is FactoryValidationException or GitException or InvalidTransitionException)
        {
            throw new McpException(ex.Message);
        }
    }

    private static Task Do(Func<Task> action) => Do(async () =>
    {
        await action();
        return true;
    });
}
