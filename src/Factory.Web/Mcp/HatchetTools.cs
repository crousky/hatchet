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
}

public sealed record RunSummary(int RunId, string Title, string Status, string Repository, int SubtasksDone, int Subtasks, string? CurrentStep, string? PauseReason, string Url);

public sealed record SubtaskSummary(int Order, string Title, string Status, int FixAttempts);

public sealed record RunDetails(RunSummary Run, string? Branch, string? BaseBranch, bool? FinalCheckPassed, int Tokens, string? WorkLeft, IReadOnlyList<SubtaskSummary> Subtasks, IReadOnlyList<string> RecentEvents);

public sealed record SubtaskDetails(
    int Order, string Title, string Status, string Description, int FixAttempts,
    string? SummaryOfChanges, string? LastFailure, IReadOnlyList<string> FixHistory, string? ReviewVerdict, string? Commit, string? LatestDiff);

/// <summary>
/// Lets an MCP client such as Claude Code drive Hatchet: plan a change in conversation, hand the plan over,
/// then follow the run and step in when it pauses. Everything here is also possible in the UI.
/// </summary>
[McpServerToolType]
public sealed class HatchetTools(FactoryOrchestrator orchestrator, IRunStore store, IConfiguration configuration)
{
    private const int MaxDiffChars = 20_000;

    [McpServerTool(Name = "probe_repository", ReadOnly = true)]
    [Description("Checks whether a folder can host a Hatchet run (a Git repository with a clean working tree) and how Hatchet would verify it. Call this before start_run.")]
    public async Task<object> ProbeRepository([Description("Absolute path of the repository folder.")] string path, CancellationToken ct)
    {
        var probe = await GitWorkspace.ProbeAsync(path, ct);
        return new
        {
            probe.Exists,
            probe.IsGitRepository,
            probe.HasCommits,
            probe.IsClean,
            probe.CurrentBranch,
            Verification = probe.Verification is { } v ? $"{v.Description}: {v.Command}" : "Nothing detected yet. The first subtask should set up the build and a test project.",
            probe.Error
        };
    }

    [McpServerTool(Name = "start_run")]
    [Description("""
        Starts a Hatchet run on a local Git repository. Hatchet makes a factory/run-N branch, then builds, verifies, reviews and commits each subtask in order, fixing failures as it goes, on the user's local model.
        Pass the plan you worked out with the user to skip Hatchet's own planner. Without a plan, Hatchet plans from the spec itself, which is slower.
        The working tree must be clean. The run continues in the background: follow it with get_run.
        """)]
    public async Task<RunDetails> StartRun(
        [Description("Absolute path of the repository.")] string repositoryPath,
        [Description("The architecture spec or feature description, in Markdown. Every subtask is built against it.")] string spec,
        [Description("The ordered subtasks to build. Each should be small enough to build and verify on its own.")] List<PlanStep>? plan = null,
        [Description("Short title for the run. Taken from the spec if omitted.")] string? title = null,
        [Description("Command that verifies the whole repository. Omit to let Hatchet detect it (dotnet, npm, cargo and others).")] string? verificationCommand = null,
        [Description("True to stop after planning so the user can review or edit the plan in Hatchet's UI before building.")] bool reviewPlanInHatchet = false,
        CancellationToken ct = default)
    {
        // A client may send null for a list or field it leaves empty.
        var edits = plan?.Select(step => new SubtaskEdit(null, step.Title ?? "", step.Description ?? "",
            step.FilesToModify ?? [], step.FilesToCreate ?? [], step.FilesToRead ?? [], step.VerificationCommand ?? "")).ToList();
        var run = await Do(() => orchestrator.StartRunAsync(new NewRunRequest(repositoryPath, spec, title, verificationCommand, reviewPlanInHatchet), edits, ct));
        return await DescribeRunAsync(run.Id, ct);
    }

    [McpServerTool(Name = "list_runs", ReadOnly = true)]
    [Description("Lists Hatchet runs, newest first, with their status and progress.")]
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
    [Description("Shows a run's status, its subtasks, recent events and, once a few subtasks are done, how much working time is likely left. If it is paused, pauseReason says why; use get_subtask for the details and resume_run to continue.")]
    public Task<RunDetails> GetRun([Description("The run's id.")] int runId, CancellationToken ct = default) =>
        DescribeRunAsync(runId, ct);

    [McpServerTool(Name = "get_subtask", ReadOnly = true)]
    [Description("Shows one subtask in full: what it should do, what changed, the last failure, earlier fix attempts, the review verdict and its latest diff. Use it to decide how to help a paused run.")]
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
        """)]
    public async Task<RunDetails> ApprovePlan([Description("The run's id.")] int runId, CancellationToken ct = default)
    {
        await RequireRunAsync(runId, ct);
        var plan = (await store.GetSubtasksAsync(runId, ct))
            .OrderBy(s => s.Order)
            .Select(s => new SubtaskEdit(s.Id, s.Title, s.Description, s.TargetFilesToModify, s.TargetFilesToCreate, s.ContextFilesToRead,
                s.CommandApproved ? s.VerificationCommand : ""))
            .ToList();
        await Do(() => orchestrator.ApprovePlanAsync(runId, plan, ct));
        return await DescribeRunAsync(runId, ct);
    }

    [McpServerTool(Name = "pause_run")]
    [Description("Pauses a working run. By default the current step finishes first, so nothing is lost. With immediately=true the current step stops now and runs again on resume.")]
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
        - retry_fixer: send the last failure, with your guidance, to the Bug Fixer with a fresh budget of attempts.
        - reverify: run the checks again on the working tree as it is, for example after you fixed the code yourself.
        - restart_subtask: discard the subtask's uncommitted changes and build it again.
        - skip_subtask: discard its changes and move on to the next subtask.
        Guidance is passed to the Bug Fixer: name the root cause or the approach to take.
        """)]
    public async Task<RunDetails> ResumeRun(
        [Description("The run's id.")] int runId,
        [Description("resume, retry_fixer, reverify, restart_subtask or skip_subtask.")] string action = "resume",
        [Description("Guidance for the Bug Fixer.")] string? guidance = null,
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
        await Do(() => orchestrator.ResumeAsync(runId, decision, guidance, ct));
        return await DescribeRunAsync(runId, ct);
    }

    [McpServerTool(Name = "hand_over_call")]
    [Description("""
        Stops the model call a working run is making and makes it again on another model. Use it when an agent is taking far too long,
        for example a local Bug Fixer thinking for an hour. The call starts over on the new model without using an attempt;
        later calls keep the model set in Hatchet's settings. get_run's currentStep and recentEvents show what is running.
        """)]
    public async Task<RunDetails> HandOverCall(
        [Description("The run's id.")] int runId,
        [Description("local for the local model, or a Claude model: opus, sonnet or haiku.")] string to,
        CancellationToken ct = default)
    {
        await RequireRunAsync(runId, ct);
        var target = to.Trim().ToLowerInvariant();
        if (target != "local" && !ClaudeCodeOptions.Models.Contains(target))
            throw new McpException($"Unknown model \"{to}\". Use local, {string.Join(", ", ClaudeCodeOptions.Models)}.");
        await Do(() => target == "local"
            ? orchestrator.HandOverCallAsync(runId, AgentEngine.Local)
            : orchestrator.HandOverCallAsync(runId, AgentEngine.ClaudeCode, target));
        return await DescribeRunAsync(runId, ct);
    }

    [McpServerTool(Name = "cancel_run", Destructive = true)]
    [Description("Cancels a run for good: discards uncommitted changes and returns the repository to its base branch. Commits stay on the run's branch. Prefer pause_run unless the user wants to abandon the run.")]
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
        var estimate = RunStateMachine.IsWorking(run.Status) || run.Status == RunStatus.NeedsHuman
            ? WorkEstimator.Estimate(subtasks, await store.GetWorkBySubtaskAsync(runId, ct))
            : null;

        return new RunDetails(Summarize(run, subtasks), run.BranchName, run.BaseBranch, run.IntegrationPassed,
            run.PromptTokens + run.CompletionTokens, estimate is null ? null : Fmt.WorkLeft(estimate),
            subtasks.OrderBy(s => s.Order).Select(s => new SubtaskSummary(s.Order, s.Title, s.Status.ToString(), s.FixAttempts)).ToList(),
            events);
    }

    private RunSummary Summarize(FactoryRun run, IReadOnlyList<FactorySubtask> subtasks)
    {
        var current = subtasks.FirstOrDefault(s => s.Id == run.CurrentSubtaskId);
        var step = run.CurrentStage is { } stage
            ? current is null ? stage.ToString() : $"{stage} on subtask {current.Order}: {current.Title}"
            : null;
        var status = run.PausedByUser ? "Paused" : run.Status.ToString();
        return new RunSummary(run.Id, run.Task.Title, status, run.Task.RepositoryPath,
            subtasks.Count(s => SubtaskStateMachine.IsFinished(s.Status) && s.Status != SubtaskStatus.Cancelled), subtasks.Count,
            RunStateMachine.IsWorking(run.Status) || run.Status == RunStatus.NeedsHuman ? step : null,
            run.PauseReason, $"{Program.BaseUrl(configuration)}/runs/{run.Id}");
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
