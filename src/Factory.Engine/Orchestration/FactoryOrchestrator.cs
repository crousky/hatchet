using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Factory.Contracts;
using Factory.Engine.Llm;
using Factory.Engine.Verification;
using Factory.Engine.Workspace;
using Microsoft.Extensions.Logging;

namespace Factory.Engine.Orchestration;

/// <summary>The request can't be carried out as asked. The message is shown to the user.</summary>
public sealed class FactoryValidationException(string message) : Exception(message);

/// <summary>
/// Runs the factory loop: Ingest, Decompose, then Refine, Build, Verify, Review and Fix per subtask,
/// then Integrate. Pipelines run in the background; progress goes to the store and the event bus.
/// </summary>
public sealed class FactoryOrchestrator(
    IRunStore store,
    ISettingsStore settingsStore,
    ILlmClient llm,
    ICommandRunner commands,
    FactoryEventBus bus,
    ILogger<FactoryOrchestrator> logger)
{
    private enum SubtaskEntry { Start, Verify, Fix }

    private sealed record ResumePoint(int SubtaskId, SubtaskEntry Entry);

    private sealed class PipelineContext(FactoryRun run, FactorySettings settings)
    {
        public FactoryRun Run { get; } = run;
        public FactorySettings Settings { get; } = settings;
        public FactoryTask Task => Run.Task;
        public GitWorkspace Git { get; } = new(run.Task.RepositoryPath);
    }

    private sealed class ActivePipeline
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Completion { get; set; } = Task.CompletedTask;

        /// <summary>Set when the user pauses. The pipeline stops at its next step, or at once if Cancellation fires too.</summary>
        public volatile bool PauseRequested;

        private const int MaxStreamedChars = 200_000;

        private readonly object _callGate = new();
        private CancellationTokenSource? _call;
        private HandOver? _handOver;
        private LlmCallStarted? _started;
        private DateTime _startedAt;
        private readonly StringBuilder _thinking = new();
        private readonly StringBuilder _content = new();
        private int _deltas;

        /// <summary>The model call in progress and what it has streamed, so a page opened partway through it can still show it.</summary>
        public LiveCallInfo? LiveCall
        {
            get
            {
                lock (_callGate)
                    return _started is null ? null : new LiveCallInfo(_started, _startedAt, _thinking.ToString(), _content.ToString(), _deltas);
            }
        }

        /// <summary>Marks the start of a model call, which the user can then hand to another model.</summary>
        public void CallStarted(CancellationTokenSource call, LlmCallStarted started)
        {
            lock (_callGate)
            {
                (_call, _handOver, _started, _startedAt, _deltas) = (call, null, started, DateTime.UtcNow, 0);
                _thinking.Clear();
                _content.Clear();
            }
        }

        /// <summary>Keeps the end of what the current call has streamed. <paramref name="sequence"/> counts the deltas so far.</summary>
        public void Streamed(LlmDelta delta, int sequence)
        {
            lock (_callGate)
            {
                var text = delta.IsThinking ? _thinking : _content;
                text.Append(delta.Text);
                if (text.Length > MaxStreamedChars) text.Remove(0, text.Length - MaxStreamedChars / 2);
                _deltas = sequence;
            }
        }

        /// <summary>Marks the end of the current call. Returns the hand-over the user asked for, if that is why it ended.</summary>
        public HandOver? CallEnded()
        {
            lock (_callGate)
            {
                var handOver = _handOver;
                (_call, _handOver, _started) = (null, null, null);
                _thinking.Clear();
                _content.Clear();
                return handOver;
            }
        }

        /// <summary>Stops the current call so it can be made again on another model. False if no call is running.</summary>
        public bool RequestHandOver(HandOver handOver)
        {
            lock (_callGate)
            {
                if (_call is null) return false;
                _handOver = handOver;
                _call.Cancel();
                return true;
            }
        }
    }

    private sealed record HandOver(AgentEngine Engine, string ClaudeModel);

    /// <summary>The model call a run is making, if any.</summary>
    public LiveCallInfo? GetLiveCall(int runId) => _active.TryGetValue(runId, out var active) ? active.LiveCall : null;

    /// <summary>Thrown at a step boundary to stop a pipeline the user paused.</summary>
    private sealed class PausedByUserException : Exception;

    /// <summary>Thrown when a call on the local model runs past the time limit it was given.</summary>
    private sealed class TimeLimitException : Exception;

    public const string PausedByUserReason = "You paused this run. Resume picks up where it stopped.";

    public const string InterruptedReason = "Hatchet stopped while this run was in progress. Resume picks up where it stopped.";

    private readonly ConcurrentDictionary<int, ActivePipeline> _active = new();

    // One local model serves every agent, so requests from separate runs take turns.
    private readonly SemaphoreSlim _modelGate = new(1, 1);

    // Runs past the parallel limits wait here. Letting runs in reads the database, so one pass runs at a time.
    private readonly WorkerQueue _queue = new();
    private readonly SemaphoreSlim _queueGate = new(1, 1);

    public bool IsActive(int runId) => _active.ContainsKey(runId);

    /// <summary>True while a run is finishing its current step before it pauses.</summary>
    public bool IsPausing(int runId) => _active.TryGetValue(runId, out var active) && active.PauseRequested;

    /// <summary>Where a run is in line for a worker, or null if it isn't waiting for one.</summary>
    public QueueSpot? GetQueueSpot(int runId) => _queue.SpotOf(runId);

    /// <summary>
    /// Lets waiting runs start where a worker and their repository are free. Runs call it as they finish; call it too
    /// after changing the parallel limits.
    /// </summary>
    public async Task RefreshQueueAsync()
    {
        await _queueGate.WaitAsync();
        try
        {
            var settings = await settingsStore.GetAsync();
            // A run that has started has its branch checked out until it finishes, even while it waits for the user.
            var holders = (await store.ListRunsAsync())
                .Where(r => !RunStateMachine.IsTerminal(r.Status) && r.BranchName is not null)
                .GroupBy(r => RepositoryKey(r.Task.RepositoryPath), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => (IReadOnlySet<int>)g.Select(r => r.Id).ToHashSet(), StringComparer.OrdinalIgnoreCase);
            var touched = _queue.Admit(pool => pool == WorkerPool.Claude ? settings.MaxParallelClaudeRuns : settings.MaxParallelLocalRuns, holders);
            foreach (var runId in touched) bus.Publish(new QueueChanged(runId));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not let queued runs start");
        }
        finally
        {
            _queueGate.Release();
        }
    }

    /// <summary>Runs that use the local model for any agent share its workers, and runs entirely on Claude share Claude's.</summary>
    internal static WorkerPool PoolFor(FactorySettings settings) =>
        Enum.GetValues<AgentRole>().All(role => AgentProfiles.Resolve(settings, role).UsesClaude) ? WorkerPool.Claude : WorkerPool.Local;

    private static string RepositoryKey(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>True while any pipeline is working, and so may be waiting on the model.</summary>
    public bool HasActiveRuns => !_active.IsEmpty;

    // ---- Commands from the UI ----

    public Task<FactoryRun> StartRunAsync(NewRunRequest request, CancellationToken ct = default) => StartRunAsync(request, null, ct);

    /// <summary>
    /// Starts a run. With a <paramref name="plan"/>, for example one worked out with Claude over MCP, Hatchet's own
    /// planner is skipped. Whoever supplied the plan has approved it, so its verification commands may run.
    /// </summary>
    public async Task<FactoryRun> StartRunAsync(NewRunRequest request, IReadOnlyList<SubtaskEdit>? plan, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.ArchitectureSpec))
            throw new FactoryValidationException("The architecture spec is empty.");
        if (plan is not null && (plan.Count == 0 || plan.Any(s => string.IsNullOrWhiteSpace(s.Title) || string.IsNullOrWhiteSpace(s.Description))))
            throw new FactoryValidationException("A supplied plan needs at least one subtask, and every subtask needs a title and a description.");

        var command = request.VerificationCommand?.Trim() ?? "";
        var path = Path.GetFullPath(request.RepositoryPath.Trim());
        await RequireStartableRepositoryAsync(path, ct);

        var task = new FactoryTask
        {
            Title = string.IsNullOrWhiteSpace(request.Title) ? TitleFromSpec(request.ArchitectureSpec) : request.Title.Trim(),
            RepositoryPath = path,
            ArchitectureSpec = request.ArchitectureSpec,
            VerificationCommand = command,
            RequirePlanApproval = request.RequirePlanApproval,
            SuppliedPlanJson = plan is null ? null : JsonSerializer.Serialize(plan)
        };

        // Once the run is saved it has to be launched, or it would sit queued with no pipeline behind it.
        var run = await store.CreateRunAsync(null, task, CancellationToken.None);
        Launch(run.Id, (context, token) => RunFromIngestAsync(context, token));
        return run;
    }

    private static List<FactorySubtask> ToSubtasks(int runId, IReadOnlyList<SubtaskEdit> plan) =>
        plan.Select((edit, index) => new FactorySubtask
        {
            RunId = runId,
            Order = index + 1,
            Title = edit.Title.Trim(),
            Description = edit.Description,
            TargetFilesToModify = edit.TargetFilesToModify,
            TargetFilesToCreate = edit.TargetFilesToCreate,
            ContextFilesToRead = edit.ContextFilesToRead,
            VerificationCommand = edit.VerificationCommand.Trim(),
            CommandApproved = true
        }).ToList();

    /// <summary>Starts a fresh run of an earlier run's task.</summary>
    public async Task<FactoryRun> RerunAsync(int runId, CancellationToken ct = default)
    {
        var previous = await store.GetRunAsync(runId, ct);
        await RequireStartableRepositoryAsync(previous.Task.RepositoryPath, ct);
        var run = await store.CreateRunAsync(previous.TaskId, null, ct);
        Launch(run.Id, (context, token) => RunFromIngestAsync(context, token));
        return run;
    }

    public async Task ApprovePlanAsync(int runId, IReadOnlyList<SubtaskEdit> plan, CancellationToken ct = default)
    {
        var run = await store.GetRunAsync(runId, ct);
        RequireStatus(run, RunStatus.AwaitingPlanApproval);
        if (plan.Count == 0) throw new FactoryValidationException("The plan has no subtasks.");

        await store.ReplaceSubtasksAsync(runId, ToSubtasks(runId, plan), ct);
        bus.Publish(new PlanChanged(runId));
        Launch(runId, (context, token) => ExecuteAsync(context, null, token));
    }

    public async Task RegeneratePlanAsync(int runId, string? feedback, CancellationToken ct = default)
    {
        var run = await store.GetRunAsync(runId, ct);
        RequireStatus(run, RunStatus.AwaitingPlanApproval);
        Launch(runId, (context, token) => DecomposeAndContinueAsync(context, feedback, token));
    }

    public async Task ResumeAsync(int runId, HumanDecision decision, string? guidance, CancellationToken ct = default)
    {
        var run = await store.GetRunAsync(runId, ct);
        RequireStatus(run, RunStatus.NeedsHuman);

        switch (run.PausedStage)
        {
            // No stage means it was paused while waiting in line, before it started.
            case PipelineStage.Ingest or null:
                Launch(runId, (context, token) => RunFromIngestAsync(context, token));
                return;
            case PipelineStage.Decompose:
                Launch(runId, (context, token) => PlanAsync(context, token));
                return;
            case PipelineStage.Integrate:
                Launch(runId, (context, token) => IntegrateAsync(context, token));
                return;
        }

        var subtasks = await store.GetSubtasksAsync(runId, ct);
        var paused = subtasks.FirstOrDefault(s => s.Status == SubtaskStatus.NeedsHuman);
        if (paused is null)
        {
            Launch(runId, (context, token) => ExecuteAsync(context, null, token));
            return;
        }

        paused.HumanGuidance = string.IsNullOrWhiteSpace(guidance) ? paused.HumanGuidance : guidance.Trim();
        var resumePoint = await ApplyDecisionAsync(run, paused, decision, ct);
        await store.UpdateSubtaskAsync(paused, ct);
        Launch(runId, (context, token) => ExecuteAsync(context, resumePoint, token));
    }

    /// <summary>
    /// Pauses a working run. By default the current step (a model call or command) finishes first, so nothing is
    /// lost; <paramref name="immediately"/> stops it now, and that step runs again from its start on resume.
    /// </summary>
    public async Task PauseAsync(int runId, bool immediately, CancellationToken ct = default)
    {
        if (!_active.TryGetValue(runId, out var active))
        {
            var run = await store.GetRunAsync(runId, ct);
            if (!RunStateMachine.IsWorking(run.Status)) throw new FactoryValidationException("This run isn't working, so there's nothing to pause.");
            await PauseByUserAsync(run);
            return;
        }

        active.PauseRequested = true;
        bus.Publish(new PauseRequested(runId));
        // A run still waiting for a worker has no step to finish, so it leaves the line now.
        if (!immediately && _queue.SpotOf(runId) is null) return;

        await active.Cancellation.CancelAsync();
        await active.Completion;
    }

    /// <summary>
    /// Stops the model call a run is making and makes it again on another model: the local one, or a Claude model
    /// through Claude Code. The call starts over and doesn't use up an attempt. Later calls keep their configured model.
    /// </summary>
    public Task HandOverCallAsync(int runId, AgentEngine engine, string? claudeModel = null)
    {
        var handOver = new HandOver(engine, claudeModel ?? ClaudeCodeOptions.DefaultModel);
        if (!_active.TryGetValue(runId, out var active) || active.LiveCall is not { } live)
            throw new FactoryValidationException("This run isn't waiting on a model right now.");
        // Making the same call again on the same model would only throw away what it has done so far.
        var target = engine == AgentEngine.Local ? null : handOver.ClaudeModel;
        if (live.Call.Model == target)
            throw new FactoryValidationException($"The call is already running on {(target is null ? "the local model" : ClaudeCodeOptions.ModelLabel(target))}.");
        if (!active.RequestHandOver(handOver))
            throw new FactoryValidationException("This run isn't waiting on a model right now.");
        return Task.CompletedTask;
    }

    /// <summary>Names the local model as a hand-over target. Claude models are named as in <see cref="ClaudeCodeOptions.Models"/>.</summary>
    public const string LocalHandOverTarget = "local";

    /// <summary>Hands the current call to <see cref="LocalHandOverTarget"/> or a Claude model such as "opus".</summary>
    public Task HandOverCallAsync(int runId, string target)
    {
        var name = target.Trim().ToLowerInvariant();
        if (name == LocalHandOverTarget) return HandOverCallAsync(runId, AgentEngine.Local);
        if (ClaudeCodeOptions.Models.Contains(name)) return HandOverCallAsync(runId, AgentEngine.ClaudeCode, name);
        throw new FactoryValidationException($"Unknown model \"{target}\". Use {LocalHandOverTarget}, {string.Join(", ", ClaudeCodeOptions.Models)}.");
    }

    /// <summary>Called between steps: stops the pipeline here if the user asked it to pause.</summary>
    private void PauseIfRequested(int runId)
    {
        if (_active.TryGetValue(runId, out var active) && active.PauseRequested) throw new PausedByUserException();
    }

    private async Task PauseByUserAsync(FactoryRun run)
    {
        if (!RunStateMachine.CanTransition(run.Status, RunStatus.NeedsHuman)) return;
        await LogAsync(run.Id, run.CurrentSubtaskId, AgentLogKind.Info, run.CurrentStage, "Paused at your request.", CancellationToken.None);
        await PauseRunAsync(run, PausedByUserReason, CancellationToken.None);
        run.PausedByUser = true;
        await store.UpdateRunAsync(run, CancellationToken.None);
    }

    public async Task CancelAsync(int runId, CancellationToken ct = default)
    {
        if (_active.TryGetValue(runId, out var active))
        {
            await active.Cancellation.CancelAsync();
            await active.Completion;
        }

        var run = await store.GetRunAsync(runId, ct);
        if (!RunStateMachine.CanTransition(run.Status, RunStatus.Cancelled)) return;

        foreach (var subtask in await store.GetSubtasksAsync(runId, ct))
        {
            if (SubtaskStateMachine.IsFinished(subtask.Status)) continue;
            await SetSubtaskStatusAsync(subtask, SubtaskStatus.Cancelled, ct);
        }

        await LeaveWorkingBranchAsync(run, ct);
        await SetRunStatusAsync(run, RunStatus.Cancelled, ct);
    }

    public async Task<string> MergeAsync(int runId, CancellationToken ct = default)
    {
        var run = await store.GetRunAsync(runId, ct);
        RequireStatus(run, RunStatus.ReadyToMerge);

        var git = new GitWorkspace(run.Task.RepositoryPath);
        try
        {
            run.MergeCommit = await git.MergeIntoAsync(run.BaseBranch!, run.BranchName!, $"Merge {run.BranchName}: {run.Task.Title}", ct);
        }
        catch (GitException ex)
        {
            throw new FactoryValidationException(ex.Message);
        }

        await SetRunStatusAsync(run, RunStatus.Merged, ct);
        await LogAsync(run.Id, null, AgentLogKind.Info, PipelineStage.Integrate, $"Merged {run.BranchName} into {run.BaseBranch} at {run.MergeCommit[..Math.Min(8, run.MergeCommit.Length)]}.");
        return run.MergeCommit;
    }

    /// <summary>Finishes a run without merging. The branch stays so it can be pushed or inspected later.</summary>
    public async Task CloseAsync(int runId, CancellationToken ct = default)
    {
        var run = await store.GetRunAsync(runId, ct);
        RequireStatus(run, RunStatus.ReadyToMerge);
        await LeaveWorkingBranchAsync(run, ct);
        await SetRunStatusAsync(run, RunStatus.Closed, ct);
    }

    public async Task<PullRequestDraft> PreparePullRequestAsync(int runId, CancellationToken ct = default)
    {
        var run = await store.GetRunAsync(runId, ct);
        var subtasks = await store.GetSubtasksAsync(runId, ct);

        var body = new StringBuilder();
        body.Append("## Summary\n\nBuilt by Hatchet from the architecture spec, one reviewed subtask at a time.\n\n## Subtasks\n\n");
        foreach (var subtask in subtasks.OrderBy(s => s.Order))
        {
            var mark = subtask.Status == SubtaskStatus.Completed ? "x" : " ";
            var note = subtask.Status == SubtaskStatus.Skipped ? " (skipped)" : "";
            body.Append($"- [{mark}] {subtask.Title}{note}");
            if (!string.IsNullOrWhiteSpace(subtask.SummaryOfChanges)) body.Append($": {subtask.SummaryOfChanges.ReplaceLineEndings(" ")}");
            body.Append('\n');
        }

        var finalCheck = (await store.GetLogsAsync(runId, ct: ct)).LastOrDefault(l => l is { Kind: AgentLogKind.Command, Stage: PipelineStage.Integrate, Succeeded: true });
        if (finalCheck is not null)
            body.Append($"\n## Verification\n\n`{finalCheck.Command}` passed on the final branch.\n");

        var hasRemote = await new GitWorkspace(run.Task.RepositoryPath).HasRemoteAsync(ct);
        return new PullRequestDraft(run.Task.Title, body.ToString(), run.BranchName ?? "", run.BaseBranch ?? "", hasRemote);
    }

    /// <summary>
    /// A run left mid-work by a previous process has no pipeline behind it, so it pauses for a resume. Runs that were
    /// still waiting in line hadn't started, so they join the line again, in the order they were started.
    /// </summary>
    public async Task RecoverInterruptedRunsAsync(CancellationToken ct = default)
    {
        foreach (var run in (await store.ListRunsAsync(ct)).OrderBy(r => r.Id))
        {
            if (!RunStateMachine.IsWorking(run.Status) || IsActive(run.Id)) continue;
            if (run is { Status: RunStatus.Queued, BranchName: null })
                Launch(run.Id, (context, token) => RunFromIngestAsync(context, token));
            else
                await PauseRunAsync(run, InterruptedReason, ct);
        }
    }

    /// <summary>Stops pipelines for shutdown. Their state is left as is, for recovery at the next start.</summary>
    public async Task StopAllAsync()
    {
        foreach (var active in _active.Values) await active.Cancellation.CancelAsync();
        await Task.WhenAll(_active.Values.Select(a => a.Completion));
    }

    // ---- Pipeline ----

    private void Launch(int runId, Func<PipelineContext, CancellationToken, Task> body)
    {
        var active = new ActivePipeline();
        if (!_active.TryAdd(runId, active))
            throw new FactoryValidationException("This run is already working.");

        var token = active.Cancellation.Token;
        active.Completion = Task.Run(async () =>
        {
            PipelineContext? context = null;
            try
            {
                await WaitForWorkerAsync(runId, token);
                // Read after the wait, which can be long, so the run starts with current settings.
                var run = await store.GetRunAsync(runId, token);
                context = new PipelineContext(run, await settingsStore.GetAsync(token));
                await body(context, token);
            }
            catch (Exception ex) when (ex is PausedByUserException || ex is OperationCanceledException && token.IsCancellationRequested && active.PauseRequested)
            {
                await PauseByUserAsync(context?.Run ?? await store.GetRunAsync(runId, CancellationToken.None));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // The user cancelled (CancelAsync finishes the job) or the app is stopping (recovery does).
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Pipeline for run {RunId} stopped", runId);
                await HandlePipelineFailureAsync(runId, context, ex);
            }
            finally
            {
                _active.TryRemove(runId, out _);
                _queue.Release(runId);
                bus.Publish(new QueueChanged(runId));
                await RefreshQueueAsync();
            }
        }, CancellationToken.None);
    }

    /// <summary>Waits in line until the run's pool has a worker free and no other run has its repository.</summary>
    private async Task WaitForWorkerAsync(int runId, CancellationToken ct)
    {
        var run = await store.GetRunAsync(runId, ct);
        var pool = PoolFor(await settingsStore.GetAsync(ct));
        var wait = _queue.WaitAsync(runId, RepositoryKey(run.Task.RepositoryPath), pool, ct);
        await RefreshQueueAsync();
        await wait;
        // A pause asked for while it waited takes effect before it starts.
        PauseIfRequested(runId);
    }

    private async Task HandlePipelineFailureAsync(int runId, PipelineContext? context, Exception ex)
    {
        try
        {
            var run = context?.Run ?? await store.GetRunAsync(runId);
            await LogAsync(runId, run.CurrentSubtaskId, AgentLogKind.Error, run.CurrentStage, ex.Message);

            // Ingest failures mean the repository itself is unusable, and retrying can't fix that.
            if (run.CurrentStage == PipelineStage.Ingest && ex is GitException or FactoryValidationException)
            {
                run.PauseReason = ex.Message;
                await SetRunStatusAsync(run, RunStatus.Failed, CancellationToken.None);
                return;
            }

            await PauseRunAsync(run, ex.Message, CancellationToken.None);
        }
        catch (Exception persistError)
        {
            logger.LogError(persistError, "Could not record the failure of run {RunId}", runId);
        }
    }

    private async Task RunFromIngestAsync(PipelineContext context, CancellationToken ct)
    {
        await IngestAsync(context, ct);
        await PlanAsync(context, ct);
    }

    /// <summary>Uses the plan handed over with the task, if there is one, and otherwise has the Task Planner make one.</summary>
    private Task PlanAsync(PipelineContext context, CancellationToken ct) =>
        context.Task.SuppliedPlanJson is null ? DecomposeAndContinueAsync(context, null, ct) : UseSuppliedPlanAsync(context, ct);

    private async Task UseSuppliedPlanAsync(PipelineContext context, CancellationToken ct)
    {
        var run = context.Run;
        await SetRunStatusAsync(run, RunStatus.Decomposing, ct);
        await SetStageAsync(run, PipelineStage.Decompose, null, ct);

        // A run resumed here may already hold the plan, edited at plan review perhaps, so only a fresh run copies it.
        if ((await store.GetSubtasksAsync(run.Id, ct)).Count == 0)
        {
            var plan = JsonSerializer.Deserialize<List<SubtaskEdit>>(context.Task.SuppliedPlanJson!)!;
            await store.ReplaceSubtasksAsync(run.Id, ToSubtasks(run.Id, plan), ct);
            bus.Publish(new PlanChanged(run.Id));
            await LogAsync(run.Id, null, AgentLogKind.Info, PipelineStage.Decompose, $"Using the plan supplied with the run ({plan.Count} subtasks), so the Task Planner was skipped.", ct);
        }

        if (context.Task.RequirePlanApproval)
        {
            await SetRunStatusAsync(run, RunStatus.AwaitingPlanApproval, ct);
            return;
        }
        await ExecuteAsync(context, null, ct);
    }

    private async Task IngestAsync(PipelineContext context, CancellationToken ct)
    {
        var (run, task, git) = (context.Run, context.Task, context.Git);
        await SetRunStatusAsync(run, RunStatus.Ingesting, ct);
        await SetStageAsync(run, PipelineStage.Ingest, null, ct);

        // A run that waited in line may have been started while another run had the repository, so its tree is checked now.
        RequireCleanCheckout(await GitWorkspace.ProbeAsync(task.RepositoryPath, ct));
        await git.EnsureInitialCommitAsync(ct);
        run.BaseBranch = await git.GetCurrentBranchAsync(ct);
        run.BaseCommit = await git.GetHeadShaAsync(ct);
        run.BranchName = await git.GetUnusedBranchNameAsync($"factory/run-{run.Id}", ct);
        run.RepoMap = RepositoryScanner.BuildRepoMap(task.RepositoryPath);
        await store.UpdateRunAsync(run, ct);

        var fileCount = run.RepoMap.Count(c => c == '\n') + 1;
        await LogAsync(run.Id, null, AgentLogKind.Info, PipelineStage.Ingest, $"Mapped {fileCount} files in {task.RepositoryPath}, based on {run.BaseBranch}.", ct);
    }

    private async Task DecomposeAndContinueAsync(PipelineContext context, string? feedback, CancellationToken ct)
    {
        var (run, task) = (context.Run, context.Task);
        await SetRunStatusAsync(run, RunStatus.Decomposing, ct);
        await SetStageAsync(run, PipelineStage.Decompose, null, ct);

        var prompt = PromptBuilder.Decompose(task.ArchitectureSpec, run.RepoMap, DescribeVerification(task), feedback);
        var graph = await InvokeAgentAsync<TaskGraphResponse>(
            context, AgentRole.TaskPlanner, null, prompt,
            g => g.Subtasks.Count == 0 ? "The subtasks list is empty." : null,
            ct);

        var subtasks = graph.Subtasks
            .OrderBy(s => s.Order)
            .Select((dto, index) => new FactorySubtask
            {
                RunId = run.Id,
                Order = index + 1,
                Title = dto.Title,
                Description = dto.Description,
                TargetFilesToModify = dto.TargetFilesToModify,
                TargetFilesToCreate = dto.TargetFilesToCreate,
                ContextFilesToRead = dto.ContextFilesToRead,
                VerificationCommand = dto.VerificationCommand.Trim()
            })
            .ToList();

        await store.ReplaceSubtasksAsync(run.Id, subtasks, ct);
        bus.Publish(new PlanChanged(run.Id));
        await LogAsync(run.Id, null, AgentLogKind.Info, PipelineStage.Decompose, $"Planned {subtasks.Count} subtasks.", ct);

        if (task.RequirePlanApproval)
        {
            await SetRunStatusAsync(run, RunStatus.AwaitingPlanApproval, ct);
            return;
        }

        await ExecuteAsync(context, null, ct);
    }

    private async Task ExecuteAsync(PipelineContext context, ResumePoint? resumePoint, CancellationToken ct)
    {
        var (run, git) = (context.Run, context.Git);
        await SetRunStatusAsync(run, RunStatus.Executing, ct);
        await SetStageAsync(run, PipelineStage.Build, run.CurrentSubtaskId, ct);

        if (await git.GetCurrentBranchAsync(ct) != run.BranchName)
        {
            if (!await git.IsCleanAsync(ct))
                throw new FactoryValidationException($"The repository has uncommitted changes, so Hatchet won't switch to {run.BranchName}. Commit or stash them, then resume.");
            await git.EnsureBranchAsync(run.BranchName!, ct);
        }

        var subtasks = await store.GetSubtasksAsync(run.Id, ct);
        foreach (var subtask in subtasks.OrderBy(s => s.Order))
        {
            if (SubtaskStateMachine.IsFinished(subtask.Status)) continue;

            PauseIfRequested(run.Id);
            run.CurrentSubtaskId = subtask.Id;
            var entry = resumePoint?.SubtaskId == subtask.Id ? resumePoint.Entry : SubtaskEntry.Start;
            var finished = await ProcessSubtaskAsync(context, subtask, entry, ct);
            if (!finished) return;
        }

        run.CurrentSubtaskId = null;
        await IntegrateAsync(context, ct);
    }

    /// <summary>Returns false if the run paused for a human.</summary>
    private async Task<bool> ProcessSubtaskAsync(PipelineContext context, FactorySubtask subtask, SubtaskEntry entry, CancellationToken ct)
    {
        var run = context.Run;
        var needsFix = entry == SubtaskEntry.Fix;
        try
        {
            if (entry == SubtaskEntry.Start)
            {
                await RefineAsync(context, subtask, ct);
                needsFix = !await BuildAsync(context, subtask, ct);
            }

            while (true)
            {
                if (needsFix)
                {
                    if (subtask.FixAttempts >= context.Settings.MaxFixRetries)
                    {
                        await PauseForSubtaskAsync(run, subtask, $"\"{subtask.Title}\" still fails after {subtask.FixAttempts} fix attempts.", ct);
                        return false;
                    }

                    needsFix = false;
                    if (!await FixAsync(context, subtask, ct))
                    {
                        needsFix = true;
                        continue;
                    }
                }

                var verification = await VerifyAsync(context, subtask, ct);
                if (verification is { Success: false })
                {
                    subtask.LastFailure = DescribeFailure(verification, context.Settings);
                    RecordFailedFix(subtask, FailureDigest.Errors(verification.Output) is { Count: > 0 } errors
                        ? string.Join('\n', errors.Take(10))
                        : PromptBuilder.Tail(verification.Output, 800));
                    await store.UpdateSubtaskAsync(subtask, ct);
                    needsFix = true;
                    continue;
                }

                var verdict = await ReviewAsync(context, subtask, verification, ct);
                if (verdict.Passed)
                {
                    await CommitAsync(context, subtask, ct);
                    return true;
                }

                subtask.LastFailure = DescribeRejection(verdict);
                RecordFailedFix(subtask, $"The reviewer rejected it: {string.Join("; ", verdict.BlockingIssues)}");
                await store.UpdateSubtaskAsync(subtask, ct);
                needsFix = true;
            }
        }
        catch (Exception ex) when (ex is LlmException or GitException or IOException or UnauthorizedAccessException)
        {
            await LogAsync(run.Id, subtask.Id, AgentLogKind.Error, run.CurrentStage, ex.Message, CancellationToken.None);
            await PauseForSubtaskAsync(run, subtask, ex.Message, CancellationToken.None);
            return false;
        }
    }

    private async Task RefineAsync(PipelineContext context, FactorySubtask subtask, CancellationToken ct)
    {
        var (run, settings) = (context.Run, context.Settings);
        if (subtask.RefinedSpecJson is not null) return;

        await SetSubtaskStatusAsync(subtask, SubtaskStatus.Refining, ct);
        await SetStageAsync(run, PipelineStage.Refine, subtask.Id, ct);

        var planned = PlannedFiles(subtask).ToList();
        var files = RepositoryScanner.ReadFiles(context.Task.RepositoryPath, planned, settings.MaxFileChars, settings.MaxContextChars);
        var definitions = await ReadDefinitionsAsync(context, subtask, AgentRole.ContextSpecifier, planned, files, ct, subtask.Title, subtask.Description);
        var prompt = PromptBuilder.Refine(subtask, context.Task.ArchitectureSpec, run.RepoMap, RepositoryScanner.Render(files), definitions);

        var refined = await InvokeAgentAsync<RefinedSpecResponse>(context, AgentRole.ContextSpecifier, subtask.Id, prompt, null, ct);
        subtask.RefinedSpecJson = PromptBuilder.ToJson(refined);
        await store.UpdateSubtaskAsync(subtask, ct);
    }

    /// <summary>Returns false if the model's patch was rejected, which sends it to the Fixer.</summary>
    private async Task<bool> BuildAsync(PipelineContext context, FactorySubtask subtask, CancellationToken ct)
    {
        var (run, settings) = (context.Run, context.Settings);
        if (subtask.SummaryOfChanges is not null) return true;

        await SetSubtaskStatusAsync(subtask, SubtaskStatus.Building, ct);
        await SetStageAsync(run, PipelineStage.Build, subtask.Id, ct);

        var planned = PlannedFiles(subtask).ToList();
        var files = RepositoryScanner.ReadFiles(context.Task.RepositoryPath, planned, settings.MaxFileChars, settings.MaxContextChars);
        var definitions = await ReadDefinitionsAsync(context, subtask, AgentRole.CodeGenerator, planned, files, ct,
            subtask.Title, subtask.Description, subtask.RefinedSpecJson);
        var prompt = PromptBuilder.Build(subtask, RepositoryScanner.Render(files), definitions);

        var patch = await InvokeAgentAsync<FilePatchSetResponse>(
            context, AgentRole.CodeGenerator, subtask.Id, prompt,
            p => p.Operations.Count == 0 ? "The operations list is empty, but this subtask requires file changes." : null,
            ct);
        return await ApplyPatchAsync(context, subtask, patch, PipelineStage.Build, ct);
    }

    private const int MaxDefinitionFiles = 10;

    private static IEnumerable<string> PlannedFiles(FactorySubtask subtask) =>
        subtask.TargetFilesToModify.Concat(subtask.TargetFilesToCreate).Concat(subtask.ContextFilesToRead);

    /// <summary>
    /// Reads the files that define the types the given texts name, and then those the agent's files name, such as the
    /// parameter types of an interface it implements. Files the agent already sees are left out, and the rest fit in
    /// what is left of the context budget. Returns them rendered, or an empty string if there are none.
    /// </summary>
    private async Task<string> ReadDefinitionsAsync(PipelineContext context, FactorySubtask subtask, AgentRole role,
        IReadOnlyList<string> planned, IReadOnlyList<ContextFile> files, CancellationToken ct, params string?[] texts)
    {
        var settings = context.Settings;
        var named = texts.Concat(files.Select(f => f.Content)).Where(t => !string.IsNullOrWhiteSpace(t));
        var paths = DefinitionFinder.Find(context.Task.RepositoryPath, string.Join('\n', named), planned, MaxDefinitionFiles);
        var budget = settings.MaxContextChars - files.Sum(f => f.Content?.Length ?? 0);
        if (paths.Count == 0 || budget <= 0) return "";

        var definitions = RepositoryScanner.ReadFiles(context.Task.RepositoryPath, paths, settings.MaxFileChars, budget)
            .Where(f => f.Content is { Length: > 0 })
            .ToList();
        if (definitions.Count == 0) return "";

        await LogAsync(context.Run.Id, subtask.Id, AgentLogKind.Info, context.Run.CurrentStage,
            $"Gave the {AgentProfiles.Resolve(settings, role).DisplayName} the definitions of types it names: {string.Join(", ", definitions.Select(f => f.RelativePath))}.", ct);
        return RepositoryScanner.Render(definitions);
    }

    /// <summary>Returns false if the fix itself was unusable. The attempt still counts.</summary>
    private async Task<bool> FixAsync(PipelineContext context, FactorySubtask subtask, CancellationToken ct)
    {
        var (run, settings, git) = (context.Run, context.Settings, context.Git);
        subtask.FixAttempts++;
        // Saved now, as the status may already be Fixing: a restart must not hand out extra attempts.
        await store.UpdateSubtaskAsync(subtask, ct);
        await SetSubtaskStatusAsync(subtask, SubtaskStatus.Fixing, ct);
        await SetStageAsync(run, PipelineStage.Fix, subtask.Id, ct);

        await git.StageAllAsync(ct);
        var changed = await git.GetStagedFilesAsync(ct);
        var planned = changed.Concat(PlannedFiles(subtask)).ToList();
        var files = RepositoryScanner.ReadFiles(context.Task.RepositoryPath, planned, settings.MaxFileChars, settings.MaxContextChars);
        // The errors name what the fix got wrong, so the types they name come first.
        var definitions = await ReadDefinitionsAsync(context, subtask, AgentRole.BugFixer, planned, files, ct,
            subtask.LastFailure, subtask.HumanGuidance, subtask.Title, subtask.Description, subtask.RefinedSpecJson);
        var prompt = PromptBuilder.Fix(subtask, subtask.LastFailure ?? "(no failure was recorded)", RepositoryScanner.Render(files), subtask.HumanGuidance, definitions);

        Func<FilePatchSetResponse, string?> validate = p => p.Operations.Count == 0 ? "The operations list is empty, so nothing would change and the failure would repeat." : null;
        var claude = ClaudeCodeOptions.ModelLabel(AgentProfiles.Resolve(settings, AgentRole.BugFixer).ClaudeModel);
        FilePatchSetResponse patch;
        var failedLocalFixes = await CountFailedLocalFixesAsync(run.Id, subtask.Id, ct);
        if (ShouldEscalateFix(settings, failedLocalFixes))
        {
            await LogAsync(run.Id, subtask.Id, AgentLogKind.Info, PipelineStage.Fix,
                $"{failedLocalFixes} fix {(failedLocalFixes == 1 ? "attempt" : "attempts")} on the local model didn't work, so {claude} takes this one.", ct);
            try
            {
                patch = await InvokeAgentAsync(context, AgentRole.BugFixer, subtask.Id, prompt, validate, ct, onClaude: true);
            }
            catch (LlmException ex)
            {
                await LogAsync(run.Id, subtask.Id, AgentLogKind.Warning, PipelineStage.Fix, $"Claude couldn't take the fix, so the local model tries again. {ex.Message}", ct);
                patch = await InvokeAgentAsync(context, AgentRole.BugFixer, subtask.Id, prompt, validate, ct);
            }
        }
        else if (CanEscalateFixes(settings) && settings.LocalFixTimeLimitMinutes > 0)
        {
            var limit = TimeSpan.FromMinutes(settings.LocalFixTimeLimitMinutes);
            try
            {
                patch = await InvokeAgentAsync(context, AgentRole.BugFixer, subtask.Id, prompt, validate, ct, localTimeLimit: limit);
            }
            catch (TimeLimitException)
            {
                // Claude is the way out here, so there's no falling back to the local model if it can't take the fix.
                await LogAsync(run.Id, subtask.Id, AgentLogKind.Warning, PipelineStage.Fix,
                    $"The local Bug Fixer was still working after {DescribeDuration(limit)}, so it was stopped and {claude} takes this fix.", ct);
                patch = await InvokeAgentAsync(context, AgentRole.BugFixer, subtask.Id, prompt, validate, ct, onClaude: true);
            }
        }
        else
        {
            patch = await InvokeAgentAsync(context, AgentRole.BugFixer, subtask.Id, prompt, validate, ct);
        }
        return await ApplyPatchAsync(context, subtask, patch, PipelineStage.Fix, ct);
    }

    private static string DescribeDuration(TimeSpan span) =>
        span.TotalMinutes >= 1 ? $"{span.TotalMinutes:0.#} minutes" : $"{span.TotalSeconds:0} seconds";

    /// <summary>True when a fix on the local model can be handed to Claude.</summary>
    private static bool CanEscalateFixes(FactorySettings settings) =>
        settings.EscalateFixesToClaudeAfter > 0 && !AgentProfiles.Resolve(settings, AgentRole.BugFixer).UsesClaude;

    /// <summary>True once enough local fixes have failed that this attempt should go to Claude.</summary>
    internal static bool ShouldEscalateFix(FactorySettings settings, int failedLocalFixes) =>
        CanEscalateFixes(settings) && failedLocalFixes >= settings.EscalateFixesToClaudeAfter;

    /// <summary>
    /// Local fixes applied since the subtask's latest build. A new fix is only needed because they didn't work.
    /// Counted from the log rather than FixAttempts, which a human decision resets to give a fresh budget.
    /// </summary>
    private async Task<int> CountFailedLocalFixesAsync(int runId, int subtaskId, CancellationToken ct)
    {
        var calls = (await store.GetLogsAsync(runId, ct: ct))
            .Where(l => l is { Kind: AgentLogKind.LlmCall, Succeeded: true } && l.SubtaskId == subtaskId)
            .ToList();
        var lastBuild = calls.LastOrDefault(l => l.Role == AgentRole.CodeGenerator)?.Id ?? 0;
        return calls.Count(l => l.Id > lastBuild && l is { Role: AgentRole.BugFixer, Model: null });
    }

    private const int MaxFixHistory = 5;

    /// <summary>Notes that the latest fix didn't work and why, if the code being checked came from a fix.</summary>
    private static void RecordFailedFix(FactorySubtask subtask, string outcome)
    {
        if (subtask.FixAttempts == 0) return;
        subtask.FixHistory.Add($"Attempt {subtask.FixAttempts}: {subtask.SummaryOfChanges}\nWhat failed next:\n{outcome}");
        if (subtask.FixHistory.Count > MaxFixHistory) subtask.FixHistory.RemoveAt(0);
    }

    private async Task<bool> ApplyPatchAsync(PipelineContext context, FactorySubtask subtask, FilePatchSetResponse patch, PipelineStage stage, CancellationToken ct)
    {
        var run = context.Run;
        PatchResult result;
        try
        {
            result = FilePatcher.Apply(context.Task.RepositoryPath, patch.Operations);
        }
        catch (PatchRejectedException ex)
        {
            subtask.LastFailure = $"The previous patch was rejected and nothing was written: {ex.Message}";
            await store.UpdateSubtaskAsync(subtask, ct);
            await LogAsync(run.Id, subtask.Id, AgentLogKind.Warning, stage, subtask.LastFailure, ct);
            return false;
        }

        subtask.SummaryOfChanges = patch.SummaryOfChanges;
        await store.UpdateSubtaskAsync(subtask, ct);
        await LogAsync(run.Id, subtask.Id, AgentLogKind.Info, stage, $"Applied {result.Applied.Count} file operations: {string.Join(", ", result.Applied)}", ct);
        foreach (var warning in result.Warnings) await LogAsync(run.Id, subtask.Id, AgentLogKind.Warning, stage, warning, ct);

        await CaptureDiffAsync(context, subtask, stage, ct);
        return true;
    }

    /// <summary>Returns null when there is nothing to run yet: no command was given and no build or tests can be detected.</summary>
    private async Task<CommandResult?> VerifyAsync(PipelineContext context, FactorySubtask subtask, CancellationToken ct)
    {
        var run = context.Run;
        PauseIfRequested(run.Id);
        await SetSubtaskStatusAsync(subtask, SubtaskStatus.Verifying, ct);
        await SetStageAsync(run, PipelineStage.Verify, subtask.Id, ct);

        if (ConfiguredCommand(context.Task, subtask) is { } configured)
            return await RunCommandAsync(context, subtask.Id, PipelineStage.Verify, configured, ct);

        if (VerificationDetector.Detect(context.Task.RepositoryPath) is { } detected)
        {
            await LogAsync(run.Id, subtask.Id, AgentLogKind.Info, PipelineStage.Verify, $"Detected {detected.Description}, so verifying with `{detected.Command}`.", ct);
            return await RunCommandAsync(context, subtask.Id, PipelineStage.Verify, detected.Command, ct);
        }

        await LogAsync(run.Id, subtask.Id, AgentLogKind.Warning, PipelineStage.Verify, "No build or test setup was found yet, so nothing was run. The reviewer was told.", ct);
        return null;
    }

    private async Task<ReviewVerdictResponse> ReviewAsync(PipelineContext context, FactorySubtask subtask, CommandResult? verification, CancellationToken ct)
    {
        var (run, settings) = (context.Run, context.Settings);
        await SetSubtaskStatusAsync(subtask, SubtaskStatus.Reviewing, ct);
        await SetStageAsync(run, PipelineStage.Review, subtask.Id, ct);

        await context.Git.StageAllAsync(ct);
        var diff = await context.Git.GetStagedDiffAsync(ct);
        var testSummary = verification is null
            ? "No build or test setup exists yet, so nothing was built or tested. Judge the change on the diff alone."
            : $"Command: {verification.Command}\nExit code: {verification.ExitCode}\nDuration: {verification.Duration.TotalSeconds:0.0}s\n\n{PromptBuilder.Tail(verification.Output, 4000)}";
        var prompt = PromptBuilder.Review(subtask, PromptBuilder.Head(diff, settings.MaxDiffChars), testSummary);

        var verdict = await InvokeAgentAsync<ReviewVerdictResponse>(context, AgentRole.StaticReviewer, subtask.Id, prompt, null, ct);
        subtask.ReviewJson = PromptBuilder.ToJson(verdict);
        await store.UpdateSubtaskAsync(subtask, ct);

        var outcome = verdict.Passed ? "approved" : "rejected";
        await LogAsync(run.Id, subtask.Id, verdict.Passed ? AgentLogKind.Info : AgentLogKind.Warning, PipelineStage.Review,
            $"Reviewer {outcome} with {verdict.ConfidenceScore}% confidence; {verdict.BlockingIssues.Count} blocking issues, {verdict.Suggestions.Count} suggestions.", ct);
        return verdict;
    }

    private async Task CommitAsync(PipelineContext context, FactorySubtask subtask, CancellationToken ct)
    {
        var (run, git) = (context.Run, context.Git);
        await git.StageAllAsync(ct);
        var diff = await git.GetStagedDiffAsync(ct);
        if (diff.Length == 0)
        {
            await LogAsync(run.Id, subtask.Id, AgentLogKind.Warning, PipelineStage.Review, "The reviewer approved an empty diff, so there is nothing to commit.", ct);
        }
        else
        {
            var title = subtask.Title.ReplaceLineEndings(" ").Trim();
            subtask.CommitSha = await git.CommitStagedAsync($"feat(subtask): {title}", ct);
            var snapshot = await store.AddDiffAsync(new DiffSnapshot
            {
                RunId = run.Id,
                SubtaskId = subtask.Id,
                Stage = PipelineStage.Review,
                Attempt = subtask.FixAttempts,
                UnifiedDiff = diff,
                CommitSha = subtask.CommitSha
            }, ct);
            bus.Publish(new DiffCaptured(run.Id, subtask.Id, snapshot.Id));
            await LogAsync(run.Id, subtask.Id, AgentLogKind.Info, PipelineStage.Review, $"Committed {subtask.CommitSha[..8]}: feat(subtask): {title}", ct);
        }

        subtask.LastFailure = null;
        await SetSubtaskStatusAsync(subtask, SubtaskStatus.Completed, ct);
    }

    private async Task IntegrateAsync(PipelineContext context, CancellationToken ct)
    {
        var (run, task, git) = (context.Run, context.Task, context.Git);
        PauseIfRequested(run.Id);
        await SetRunStatusAsync(run, RunStatus.Integrating, ct);
        await SetStageAsync(run, PipelineStage.Integrate, null, ct);

        var subtasks = await store.GetSubtasksAsync(run.Id, ct);
        var command = task.VerificationCommand.Length > 0
            ? task.VerificationCommand
            : VerificationDetector.Detect(task.RepositoryPath)?.Command
              ?? subtasks.Where(s => s.CommandApproved && s.VerificationCommand.Length > 0).OrderBy(s => s.Order).LastOrDefault()?.VerificationCommand;

        if (command is null)
        {
            await LogAsync(run.Id, null, AgentLogKind.Warning, PipelineStage.Integrate, "No build or test setup was found, so there was no final check.", ct);
        }
        else
        {
            var result = await RunCommandAsync(context, null, PipelineStage.Integrate, command, ct);
            run.IntegrationPassed = result.Success;
            if (!result.Success)
            {
                await PauseRunAsync(run, $"The final check failed: `{command}` exited with code {result.ExitCode}.", ct);
                return;
            }
        }

        var finalDiff = await git.GetRangeDiffAsync(run.BaseCommit!, "HEAD", ct);
        var snapshot = await store.AddDiffAsync(new DiffSnapshot { RunId = run.Id, Stage = PipelineStage.Integrate, UnifiedDiff = finalDiff }, ct);
        bus.Publish(new DiffCaptured(run.Id, null, snapshot.Id));

        await SetRunStatusAsync(run, RunStatus.ReadyToMerge, ct);
    }

    // ---- Agents and commands ----

    /// <param name="localTimeLimit">How long the calls on the local model may take in all, counted from now. Past it, a
    /// <see cref="TimeLimitException"/> is thrown. A call handed to Claude isn't limited.</param>
    private async Task<T> InvokeAgentAsync<T>(PipelineContext context, AgentRole role, int? subtaskId, string prompt, Func<T, string?>? validate, CancellationToken ct,
        bool onClaude = false, TimeSpan? localTimeLimit = null)
    {
        var (run, settings) = (context.Run, context.Settings);
        var profile = AgentProfiles.Resolve(settings, role);
        if (onClaude) profile = profile with { Engine = AgentEngine.ClaudeCode };
        var schema = LlmSchemas.For<T>();
        var attemptPrompt = prompt;
        var lastError = "";
        var deadline = DateTime.UtcNow + localTimeLimit;
        _active.TryGetValue(run.Id, out var active);

        for (var attempt = 1; attempt <= settings.MaxLlmAttempts; attempt++)
        {
            PauseIfRequested(run.Id);
            if (attempt > 1) await Task.Delay(TimeSpan.FromSeconds(1), ct);

            var callId = Guid.NewGuid();
            LlmResult result;
            // Only the local model is shared. Claude calls from separate runs can overlap.
            var gate = profile.UsesClaude ? null : _modelGate;
            if (gate is not null) await gate.WaitAsync(ct);
            using var call = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (deadline is { } end && !profile.UsesClaude)
            {
                var left = end - DateTime.UtcNow;
                call.CancelAfter(left > TimeSpan.Zero ? left : TimeSpan.Zero);
            }
            var started = new LlmCallStarted(run.Id, subtaskId, callId, role, profile.Stage, EffortOf(profile), profile.Temperature, ModelOf(profile));
            active?.CallStarted(call, started);
            var elapsed = Stopwatch.StartNew();
            var deltas = 0;
            try
            {
                bus.Publish(started);
                result = await llm.StreamAsync(settings, new LlmRequest(profile, attemptPrompt, schema), delta =>
                {
                    active?.Streamed(delta, ++deltas);
                    bus.Publish(new LlmDeltaReceived(run.Id, callId, delta.Text, delta.IsThinking, deltas));
                }, call.Token);
            }
            // A call that is stopped is still logged with how long it ran, as that time counts towards the run's.
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && active?.CallEnded() is { } handOver)
            {
                var from = profile;
                profile = profile with { Engine = handOver.Engine, ClaudeModel = handOver.ClaudeModel };
                await AppendLlmLogAsync(from, run.Id, subtaskId, attemptPrompt, null, false,
                    $"You handed the {profile.DisplayName} from {ModelName(from)} to {ModelName(profile)}, so it starts over.", ct, elapsed.Elapsed);
                bus.Publish(new LlmCallFinished(run.Id, callId));
                attempt--; // The user's choice, so it doesn't count against the attempts.
                continue;
            }
            catch (LlmException ex)
            {
                lastError = ex.Message;
                await AppendLlmLogAsync(profile, run.Id, subtaskId, attemptPrompt, null, false, ex.Message, ct, elapsed.Elapsed);
                bus.Publish(new LlmCallFinished(run.Id, callId));
                continue;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Not handed over and not stopped from outside, so the call ran out of time.
                await AppendLlmLogAsync(profile, run.Id, subtaskId, attemptPrompt, null, false,
                    $"The {profile.DisplayName} was stopped after {DescribeDuration(elapsed.Elapsed)}, at its time limit.", ct, elapsed.Elapsed);
                bus.Publish(new LlmCallFinished(run.Id, callId));
                throw new TimeLimitException();
            }
            catch (OperationCanceledException)
            {
                await AppendLlmLogAsync(profile, run.Id, subtaskId, attemptPrompt, null, false,
                    $"The {profile.DisplayName} was stopped after {DescribeDuration(elapsed.Elapsed)}.", CancellationToken.None, elapsed.Elapsed);
                bus.Publish(new LlmCallFinished(run.Id, callId));
                throw;
            }
            finally
            {
                active?.CallEnded();
                gate?.Release();
            }

            run.PromptTokens += result.PromptTokens;
            run.CompletionTokens += result.CompletionTokens;
            await store.UpdateRunAsync(run, ct);

            var (value, parseError) = TryParse(result, validate);
            lastError = parseError ?? "";
            var message = parseError is null
                ? $"{profile.DisplayName} finished."
                : $"{profile.DisplayName} returned unusable output (attempt {attempt} of {settings.MaxLlmAttempts}): {parseError}";
            await AppendLlmLogAsync(profile, run.Id, subtaskId, attemptPrompt, result, parseError is null, message, ct);
            bus.Publish(new LlmCallFinished(run.Id, callId));

            if (value is not null) return value;
            // Hatchet never caps response length, so a cut-off means the context window filled up. Another try hits the same wall.
            if (result.FinishReason == "length") throw new LlmException(OutOfContext(profile, settings, result));
            attemptPrompt = PromptBuilder.Repair(prompt, parseError!);
        }

        throw new LlmException($"{profile.DisplayName} failed after {settings.MaxLlmAttempts} attempts. {lastError}");
    }

    private static string EffortOf(AgentProfile profile) => profile.UsesClaude ? profile.ClaudeEffort : profile.ReasoningEffort;

    private static string ModelName(AgentProfile profile) => profile.UsesClaude ? ClaudeCodeOptions.ModelLabel(profile.ClaudeModel) : "the local model";

    private static string? ModelOf(AgentProfile profile) => profile.UsesClaude ? profile.ClaudeModel : null;

    private static string OutOfContext(AgentProfile profile, FactorySettings settings, LlmResult result)
    {
        if (profile.UsesClaude)
            return $"{profile.DisplayName} reached Claude's output limit before the answer was finished. Try a lower effort for this agent in Settings, then resume the run.";

        var started = result.Content.Length > 0;
        var total = result.PromptTokens + result.CompletionTokens;
        var usage = total > 0
            ? $"The prompt took {result.PromptTokens:N0} tokens and {(started ? "the thinking and answer" : "the thinking")} took the remaining {result.CompletionTokens:N0}, filling the model's {total:N0}-token context window"
            : "The model's context window filled up";
        var answer = started ? "before the answer was finished" : "before it started the answer";
        return $"{profile.DisplayName} ran out of context. {usage} {answer}. {ContextWindow.Advice(settings.Provider)} Then resume the run.";
    }

    private static (T? Value, string? Error) TryParse<T>(LlmResult result, Func<T, string?>? validate)
    {
        if (result.FinishReason == "length")
            return (default, "The response was cut off by the model's token or context limit before the JSON was complete.");

        try
        {
            var value = StructuredOutputParser.Parse<T>(result.Content);
            var problem = validate?.Invoke(value);
            return problem is null ? (value, null) : (default, problem);
        }
        catch (JsonException ex)
        {
            return (default, ex.Message);
        }
    }

    /// <param name="duration">How long the call ran, for a call that has no result.</param>
    private async Task AppendLlmLogAsync(AgentProfile profile, int runId, int? subtaskId, string prompt, LlmResult? result, bool succeeded, string message, CancellationToken ct,
        TimeSpan? duration = null)
    {
        var entry = await store.AppendLogAsync(new AgentLogEntry
        {
            RunId = runId,
            SubtaskId = subtaskId,
            Kind = AgentLogKind.LlmCall,
            Stage = profile.Stage,
            Role = profile.Role,
            ReasoningEffort = EffortOf(profile),
            Temperature = profile.Temperature,
            Model = ModelOf(profile),
            Message = message,
            Prompt = prompt,
            Thinking = result?.Thinking,
            Response = result?.Content,
            PromptTokens = result?.PromptTokens ?? 0,
            CompletionTokens = result?.CompletionTokens ?? 0,
            DurationMs = (long)(result?.Duration ?? duration ?? TimeSpan.Zero).TotalMilliseconds,
            Succeeded = succeeded
        }, CancellationToken.None);
        bus.Publish(new LogAppended(runId, entry.Id));
        ct.ThrowIfCancellationRequested();
    }

    private async Task<CommandResult> RunCommandAsync(PipelineContext context, int? subtaskId, PipelineStage stage, string command, CancellationToken ct)
    {
        var run = context.Run;
        PauseIfRequested(run.Id);
        bus.Publish(new CommandStarted(run.Id, subtaskId, command));
        var result = await commands.RunAsync(command, context.Task.RepositoryPath, TimeSpan.FromSeconds(context.Settings.VerifyTimeoutSeconds),
            line => bus.Publish(new CommandOutputReceived(run.Id, line)), ct);
        bus.Publish(new CommandFinished(run.Id, command, result.ExitCode, result.TimedOut));

        var entry = await store.AppendLogAsync(new AgentLogEntry
        {
            RunId = run.Id,
            SubtaskId = subtaskId,
            Kind = AgentLogKind.Command,
            Stage = stage,
            Command = command,
            ExitCode = result.ExitCode,
            Output = result.Output,
            DurationMs = (long)result.Duration.TotalMilliseconds,
            Succeeded = result.Success,
            Message = result.TimedOut ? $"Timed out: {command}" : $"Exit {result.ExitCode}: {command}"
        }, ct);
        bus.Publish(new LogAppended(run.Id, entry.Id));
        return result;
    }

    private async Task CaptureDiffAsync(PipelineContext context, FactorySubtask subtask, PipelineStage stage, CancellationToken ct)
    {
        await context.Git.StageAllAsync(ct);
        var snapshot = await store.AddDiffAsync(new DiffSnapshot
        {
            RunId = context.Run.Id,
            SubtaskId = subtask.Id,
            Stage = stage,
            Attempt = subtask.FixAttempts,
            UnifiedDiff = await context.Git.GetStagedDiffAsync(ct)
        }, ct);
        bus.Publish(new DiffCaptured(context.Run.Id, subtask.Id, snapshot.Id));
    }

    // ---- Helpers ----

    /// <summary>
    /// The command a human chose for this subtask, or null to detect one from the repository when it runs.
    /// A planner-proposed command counts only once a human approved the plan holding it, so unreviewed
    /// model output is never handed to a shell as a command.
    /// </summary>
    public static string? ConfiguredCommand(FactoryTask task, FactorySubtask subtask) =>
        subtask.CommandApproved && subtask.VerificationCommand.Length > 0 ? subtask.VerificationCommand :
        task.VerificationCommand.Length > 0 ? task.VerificationCommand :
        null;

    private static string DescribeVerification(FactoryTask task)
    {
        if (task.VerificationCommand.Length > 0)
            return $"The user configured `{task.VerificationCommand}`. It runs after every subtask and must pass.";

        if (VerificationDetector.Detect(task.RepositoryPath) is { } detected)
            return $"No command was configured. Hatchet detected {detected.Description} and will run `{detected.Command}` after every subtask, detecting again as the project grows.";

        return "Hatchet found no build or test setup in the repository. Make the first subtask set up the project structure, its build "
            + "and a test project with at least one passing test, using the stack the spec calls for. Hatchet detects standard .NET, Node, "
            + "Python, Rust, Go, Maven and Gradle layouts and verifies every later subtask with them. Give each subtask a verificationCommand too.";
    }

    private async Task<ResumePoint> ApplyDecisionAsync(FactoryRun run, FactorySubtask subtask, HumanDecision decision, CancellationToken ct)
    {
        var built = subtask.SummaryOfChanges is not null;
        switch (decision)
        {
            case HumanDecision.RestartSubtask:
                await RollbackAsync(run, ct);
                subtask.RefinedSpecJson = null;
                subtask.SummaryOfChanges = null;
                subtask.LastFailure = null;
                subtask.ReviewJson = null;
                subtask.FixAttempts = 0;
                subtask.FixHistory.Clear();
                await SetSubtaskStatusAsync(subtask, SubtaskStatus.Pending, ct);
                return new ResumePoint(subtask.Id, SubtaskEntry.Start);

            case HumanDecision.SkipSubtask:
                await RollbackAsync(run, ct);
                await SetSubtaskStatusAsync(subtask, SubtaskStatus.Skipped, ct);
                await LogAsync(run.Id, subtask.Id, AgentLogKind.Warning, null, $"Skipped \"{subtask.Title}\" at the user's request.", ct);
                return new ResumePoint(subtask.Id, SubtaskEntry.Start);

            case HumanDecision.RetryFixer when subtask.LastFailure is not null:
                subtask.FixAttempts = 0;
                return new ResumePoint(subtask.Id, SubtaskEntry.Fix);

            case HumanDecision.Reverify when built:
                subtask.FixAttempts = 0;
                return new ResumePoint(subtask.Id, SubtaskEntry.Verify);

            case HumanDecision.Resume when built:
                return new ResumePoint(subtask.Id, SubtaskEntry.Verify);

            default:
                await SetSubtaskStatusAsync(subtask, SubtaskStatus.Pending, ct);
                return new ResumePoint(subtask.Id, SubtaskEntry.Start);
        }
    }

    private async Task RollbackAsync(FactoryRun run, CancellationToken ct)
    {
        if (run.BranchName is null) return;
        await new GitWorkspace(run.Task.RepositoryPath).RollbackAsync(run.BranchName, ct);
    }

    /// <summary>Discards uncommitted work and returns the repository to the branch the run started from.</summary>
    private async Task LeaveWorkingBranchAsync(FactoryRun run, CancellationToken ct)
    {
        if (run.BranchName is null || run.BaseBranch is null) return;
        var git = new GitWorkspace(run.Task.RepositoryPath);
        try
        {
            if (await git.GetCurrentBranchAsync(ct) != run.BranchName) return;
            await git.RollbackAsync(run.BranchName, ct);
            await git.CheckoutAsync(run.BaseBranch, ct);
        }
        catch (GitException ex)
        {
            await LogAsync(run.Id, null, AgentLogKind.Warning, null, $"Could not switch back to {run.BaseBranch}: {ex.Message}", ct);
        }
    }

    private async Task PauseForSubtaskAsync(FactoryRun run, FactorySubtask subtask, string reason, CancellationToken ct)
    {
        await SetSubtaskStatusAsync(subtask, SubtaskStatus.NeedsHuman, ct);
        await PauseRunAsync(run, reason, ct);
    }

    private async Task PauseRunAsync(FactoryRun run, string reason, CancellationToken ct)
    {
        run.PausedStage = run.CurrentStage;
        run.PauseReason = reason;
        if (run.CurrentSubtaskId is { } subtaskId)
        {
            var subtask = (await store.GetSubtasksAsync(run.Id, ct)).FirstOrDefault(s => s.Id == subtaskId);
            if (subtask is not null && !SubtaskStateMachine.IsFinished(subtask.Status) && subtask.Status != SubtaskStatus.NeedsHuman)
                await SetSubtaskStatusAsync(subtask, SubtaskStatus.NeedsHuman, ct);
        }
        await SetRunStatusAsync(run, RunStatus.NeedsHuman, ct);
    }

    private async Task SetRunStatusAsync(FactoryRun run, RunStatus to, CancellationToken ct)
    {
        if (run.Status == to) return;

        var from = run.Status;
        RunStateMachine.Apply(run, to);
        if (from == RunStatus.NeedsHuman)
        {
            run.PausedStage = null;
            run.PauseReason = null;
            run.PausedByUser = false;
        }
        // A finished run can wait a long time for its merge, and that wait isn't build time.
        if (to == RunStatus.ReadyToMerge || (RunStateMachine.IsTerminal(to) && run.CompletedAt is null)) run.CompletedAt = DateTime.UtcNow;

        await store.UpdateRunAsync(run, ct);
        bus.Publish(new RunStatusChanged(run.Id, from, to));
        // A finished run gives up its repository, which a queued run may be waiting for.
        if (RunStateMachine.IsTerminal(to)) await RefreshQueueAsync();
    }

    private async Task SetSubtaskStatusAsync(FactorySubtask subtask, SubtaskStatus to, CancellationToken ct)
    {
        if (subtask.Status == to) return;

        var from = subtask.Status;
        SubtaskStateMachine.Apply(subtask, to);
        await store.UpdateSubtaskAsync(subtask, ct);
        bus.Publish(new SubtaskStatusChanged(subtask.RunId, subtask.Id, from, to));
    }

    private async Task SetStageAsync(FactoryRun run, PipelineStage stage, int? subtaskId, CancellationToken ct)
    {
        run.CurrentStage = stage;
        run.CurrentSubtaskId = subtaskId;
        await store.UpdateRunAsync(run, ct);
        bus.Publish(new StageChanged(run.Id, subtaskId, stage));
    }

    private async Task LogAsync(int runId, int? subtaskId, AgentLogKind kind, PipelineStage? stage, string message, CancellationToken ct = default)
    {
        var entry = await store.AppendLogAsync(new AgentLogEntry { RunId = runId, SubtaskId = subtaskId, Kind = kind, Stage = stage, Message = message }, ct);
        bus.Publish(new LogAppended(runId, entry.Id));
    }

    private static string DescribeFailure(CommandResult result, FactorySettings settings)
    {
        var what = result.TimedOut
            ? $"`{result.Command}` timed out after {result.Duration.TotalSeconds:0} seconds."
            : $"`{result.Command}` failed with exit code {result.ExitCode}.";
        var errors = FailureDigest.Errors(result.Output);
        var output = PromptBuilder.Tail(result.Output, settings.MaxLogChars);
        return errors.Count == 0
            ? $"{what}\n\n{output}"
            : $"{what}\n\nErrors:\n{string.Join('\n', errors)}\n\nOutput:\n{output}";
    }

    private static string DescribeRejection(ReviewVerdictResponse verdict)
    {
        var builder = new StringBuilder("The build and tests passed, but the code reviewer rejected the change.\n\nBlocking issues:\n");
        foreach (var issue in verdict.BlockingIssues) builder.Append("- ").Append(issue).Append('\n');
        return builder.ToString();
    }

    private async Task RequireStartableRepositoryAsync(string path, CancellationToken ct)
    {
        var probe = await GitWorkspace.ProbeAsync(path, ct);
        if (!probe.Exists) throw new FactoryValidationException($"The folder {path} does not exist.");
        if (!probe.IsGitRepository) throw new FactoryValidationException($"{path} is not a Git repository.");

        // Another open run has the working tree, so this one waits in line for it, and the tree is checked when its turn comes.
        if (await GetOpenRunAsync(path, ct) is null) RequireCleanCheckout(probe);
    }

    /// <summary>
    /// The latest run in a repository that isn't finished, if any. It has the repository's working tree, so a run
    /// started there now waits in line until it finishes.
    /// </summary>
    public async Task<FactoryRun?> GetOpenRunAsync(string repositoryPath, CancellationToken ct = default)
    {
        var key = RepositoryKey(repositoryPath);
        return (await store.ListRunsAsync(ct))
            .Where(r => !RunStateMachine.IsTerminal(r.Status) && string.Equals(RepositoryKey(r.Task.RepositoryPath), key, StringComparison.OrdinalIgnoreCase))
            .MaxBy(r => r.Id);
    }

    private static void RequireCleanCheckout(RepositoryProbe probe)
    {
        if (!probe.IsClean) throw new FactoryValidationException("The repository has uncommitted changes. Commit or stash them first, so Hatchet's commits and rollbacks can't touch your work.");
        if (probe.CurrentBranch is null) throw new FactoryValidationException("The repository is in detached HEAD state. Check out a branch first.");
    }

    private static void RequireStatus(FactoryRun run, RunStatus expected)
    {
        if (run.Status != expected)
            throw new FactoryValidationException($"Run #{run.Id} is {run.Status}, not {expected}.");
    }

    private static string TitleFromSpec(string spec)
    {
        var line = spec.Split('\n').Select(l => l.Trim().TrimStart('#').Trim()).FirstOrDefault(l => l.Length > 0) ?? "Untitled";
        return line.Length <= 80 ? line : line[..80];
    }
}
