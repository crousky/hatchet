using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Factory.Contracts;
using Factory.Engine.Llm;
using Factory.Engine.Skills;
using Factory.Engine.Verification;
using Factory.Engine.Workspace;
using Microsoft.Extensions.Logging;

namespace Factory.Engine.Orchestration;

/// <summary>The request can't be carried out as asked. The message is shown to the user.</summary>
public class FactoryValidationException(string message) : Exception(message);

/// <summary>The working tree isn't ready for a run yet, such as having uncommitted changes. The user can fix it and resume.</summary>
public sealed class RepositoryNotReadyException(string message) : FactoryValidationException(message);

/// <summary>
/// Runs the factory loop: Ingest, Decompose, then Refine, Build, Verify, Review and Fix per subtask,
/// then Integrate. Pipelines run in the background; progress goes to the store and the event bus.
/// </summary>
public sealed partial class FactoryOrchestrator(
    IRunStore store,
    ISettingsStore settingsStore,
    ILlmClient llm,
    ICommandRunner commands,
    FactoryEventBus bus,
    ILogger<FactoryOrchestrator> logger,
    SkillLibrary? skills = null) : IRunStarter
{
    /// <summary>Where the skills for a run's repository come from. Without one, only the repository's own skills are used.</summary>
    public SkillLibrary Skills { get; } = skills ?? new SkillLibrary(null);

    private enum SubtaskEntry { Start, Verify, Fix }

    private sealed record ResumePoint(int SubtaskId, SubtaskEntry Entry);

    private enum RequestKind { Start, Approve, Regenerate, Resume }

    /// <summary>
    /// What a run does once it has a worker. It is saved on the run while it waits, so it can join the line again after
    /// a restart, and nothing it asks for happens until the run's turn comes.
    /// </summary>
    /// <param name="Text">The plan feedback for <see cref="RequestKind.Regenerate"/>, or the guidance for <see cref="RequestKind.Resume"/>.</param>
    private sealed record QueuedRequest(RequestKind Kind, HumanDecision Decision = HumanDecision.Resume, string? Text = null)
    {
        /// <summary>The status a run has while it waits with this request.</summary>
        public RunStatus WaitingStatus => Kind switch
        {
            RequestKind.Start => RunStatus.Queued,
            RequestKind.Approve or RequestKind.Regenerate => RunStatus.AwaitingPlanApproval,
            _ => RunStatus.NeedsHuman
        };

        public static QueuedRequest? Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonSerializer.Deserialize<QueuedRequest>(json); }
            catch (JsonException) { return null; }
        }
    }

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

        /// <summary>Done once the run has taken its place in line, or given up before it could.</summary>
        public TaskCompletionSource Joined { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
    private volatile bool _stopping;

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
        if (_stopping) return;
        await _queueGate.WaitAsync();
        try
        {
            var settings = await settingsStore.GetAsync();
            // A run that has started has its branch checked out until it finishes, even while it waits for the user.
            var openRuns = (await store.ListRunsAsync()).Where(r => !RunStateMachine.IsTerminal(r.Status)).ToList();
            var holders = openRuns
                .Where(r => r.BranchName is not null)
                .GroupBy(r => RepositoryKey(r.Task.RepositoryPath), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => (IReadOnlySet<int>)g.Select(r => r.Id).ToHashSet(), StringComparer.OrdinalIgnoreCase);
            // Waiting runs haven't read their settings yet, so they go by the agents' models as they are now, or as the run's own choice has them.
            var pools = openRuns.ToDictionary(r => r.Id, r => PoolFor(ModelChoices.Apply(settings, r.Task.ModelChoice)));
            var touched = _queue.Admit(pool => pool == WorkerPool.Claude ? settings.MaxParallelClaudeRuns : settings.MaxParallelLocalRuns,
                holders, runId => pools.GetValueOrDefault(runId, PoolFor(settings)));
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

    /// <summary>The repository a path names, in one spelling, so runs in the same repository are grouped and queued together.</summary>
    public static string RepositoryKey(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>True while any pipeline is working, and so may be waiting on the model.</summary>
    public bool HasActiveRuns => !_active.IsEmpty;

    // ---- Commands from the UI ----

    public Task<FactoryRun> StartRunAsync(NewRunRequest request, CancellationToken ct = default) => StartRunAsync(request, null, ct);

    public Task EnsureCanStartAsync(string repositoryPath, CancellationToken ct = default) =>
        RequireStartableRepositoryAsync(Path.GetFullPath(repositoryPath.Trim()), ct);

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
        var modelChoice = ModelChoices.Normalize(request.ModelChoice);
        await RequireStartableRepositoryAsync(path, ct);

        var task = new FactoryTask
        {
            Title = string.IsNullOrWhiteSpace(request.Title) ? TitleFromSpec(request.ArchitectureSpec) : request.Title.Trim(),
            RepositoryPath = path,
            ArchitectureSpec = request.ArchitectureSpec,
            VerificationCommand = command,
            RequirePlanApproval = request.RequirePlanApproval,
            SuppliedPlanJson = plan is null ? null : JsonSerializer.Serialize(plan),
            ModelChoice = modelChoice
        };

        // Once the run is saved it has to be launched, or it would sit queued with no pipeline behind it.
        var run = await store.CreateRunAsync(null, task, CancellationToken.None);
        Launch(run.Id, new QueuedRequest(RequestKind.Start));
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
            Skills = edit.Skills ?? [],
            VerificationCommand = edit.VerificationCommand.Trim(),
            CommandApproved = true
        }).ToList();

    /// <summary>Starts a fresh run of an earlier run's task.</summary>
    public async Task<FactoryRun> RerunAsync(int runId, CancellationToken ct = default)
    {
        var previous = await store.GetRunAsync(runId, ct);
        await RequireStartableRepositoryAsync(previous.Task.RepositoryPath, ct);
        var run = await store.CreateRunAsync(previous.TaskId, null, ct);
        Launch(run.Id, new QueuedRequest(RequestKind.Start));
        return run;
    }

    public async Task ApprovePlanAsync(int runId, IReadOnlyList<SubtaskEdit> plan, CancellationToken ct = default)
    {
        var run = await store.GetRunAsync(runId, ct);
        RequireStatus(run, RunStatus.AwaitingPlanApproval);
        if (plan.Count == 0) throw new FactoryValidationException("The plan has no subtasks.");

        await store.ReplaceSubtasksAsync(runId, ToSubtasks(runId, plan), ct);
        bus.Publish(new PlanChanged(runId));
        Launch(runId, new QueuedRequest(RequestKind.Approve));
    }

    public async Task RegeneratePlanAsync(int runId, string? feedback, CancellationToken ct = default)
    {
        var run = await store.GetRunAsync(runId, ct);
        RequireStatus(run, RunStatus.AwaitingPlanApproval);
        Launch(runId, new QueuedRequest(RequestKind.Regenerate, Text: feedback));
    }

    public async Task ResumeAsync(int runId, HumanDecision decision, string? guidance, CancellationToken ct = default)
    {
        var run = await store.GetRunAsync(runId, ct);
        RequireStatus(run, RunStatus.NeedsHuman);
        // The decision is carried out when the run's turn comes, so leaving the line first undoes nothing.
        Launch(runId, new QueuedRequest(RequestKind.Resume, decision, guidance));
    }

    /// <summary>
    /// Saves guidance given at a pause as a project skill, in the repository's <c>.hatchet/skills</c> folder, so the
    /// Task Planner can hand it to later subtasks and runs. It is committed on its own on the run's branch, so rolling
    /// back the paused subtask keeps it and it reaches the base branch with the rest of the run.
    /// Returns the skill file's repository-relative path.
    /// </summary>
    public async Task<string> SaveGuidanceAsSkillAsync(int runId, string name, string description, string guidance, CancellationToken ct = default)
    {
        var run = await store.GetRunAsync(runId, ct);
        RequireStatus(run, RunStatus.NeedsHuman);
        if (IsActive(runId)) throw new FactoryValidationException("This run is already resuming, so its repository can't take a new skill now.");

        var slug = SkillLibrary.Slug(name);
        if (slug.Length == 0) throw new FactoryValidationException("Give the skill a name, using letters, digits or hyphens.");
        if (string.IsNullOrWhiteSpace(description)) throw new FactoryValidationException("Say when the skill applies. The Task Planner chooses skills by their descriptions.");
        if (string.IsNullOrWhiteSpace(guidance)) throw new FactoryValidationException("There's no guidance to save as a skill.");

        var git = new GitWorkspace(run.Task.RepositoryPath);
        if (run.BranchName is null || await git.GetCurrentBranchAsync(ct) != run.BranchName)
            throw new FactoryValidationException($"Skills are saved on the run's branch, {run.BranchName ?? "which doesn't exist yet"}, and the repository isn't on it.");

        var relative = SkillLibrary.ProjectSkillPath(slug);
        var path = Path.Combine(run.Task.RepositoryPath, relative.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(path)) throw new FactoryValidationException($"There's already a skill at {relative}. Choose another name.");
        if (Skills.Load(run.Task.RepositoryPath).FirstOrDefault(s => string.Equals(s.Name, slug, StringComparison.OrdinalIgnoreCase)) is { } existing)
            throw new FactoryValidationException($"There's already a skill called {slug}, at {existing.Path}. Choose another name.");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, SkillLibrary.Compose(slug, description, guidance), ct);
        try
        {
            await git.CommitFilesAsync($"Add the {slug} skill", [relative], ct);
        }
        catch (GitException)
        {
            // An uncommitted file would be swept into the subtask's commit, or lost when it rolls back.
            File.Delete(path);
            throw;
        }
        await LogAsync(run.Id, run.CurrentSubtaskId, AgentLogKind.Info, null,
            $"Saved the guidance as the project skill {slug}, in {relative}, and committed it on {run.BranchName}. The Task Planner can give it to later subtasks.", ct);
        return relative;
    }

    /// <summary>Carries out what a run asked to do, once it has a worker.</summary>
    private Task RunRequestAsync(PipelineContext context, QueuedRequest request, CancellationToken ct) => request.Kind switch
    {
        RequestKind.Start => RunFromIngestAsync(context, ct),
        RequestKind.Approve => ExecuteAsync(context, null, ct),
        RequestKind.Regenerate => DecomposeAndContinueAsync(context, request.Text, ct),
        _ => ResumeFromPauseAsync(context, request.Decision, request.Text, ct)
    };

    private async Task ResumeFromPauseAsync(PipelineContext context, HumanDecision decision, string? guidance, CancellationToken ct)
    {
        var run = context.Run;
        RequireStatus(run, RunStatus.NeedsHuman);

        switch (run.PausedStage)
        {
            // No stage means it was paused while waiting in line, before it started.
            case PipelineStage.Ingest or null:
                await RunFromIngestAsync(context, ct);
                return;
            case PipelineStage.Decompose:
                await PlanAsync(context, ct);
                return;
            case PipelineStage.Integrate:
                // "Hand to the Fixer" always hands it over, even after an earlier final-check fix.
                await IntegrateAsync(context, ct, handOff: decision == HumanDecision.RetryFixer, guidance);
                return;
        }

        var subtasks = await store.GetSubtasksAsync(run.Id, ct);
        var paused = subtasks.FirstOrDefault(s => s.Status == SubtaskStatus.NeedsHuman);
        if (paused is null)
        {
            await ExecuteAsync(context, null, ct);
            return;
        }

        paused.HumanGuidance = string.IsNullOrWhiteSpace(guidance) ? paused.HumanGuidance : guidance.Trim();
        var resumePoint = await ApplyDecisionAsync(run, paused, decision, ct);
        await store.UpdateSubtaskAsync(paused, ct);
        await ExecuteAsync(context, resumePoint, ct);
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
        run.QueuedRequest = null;
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
    /// still waiting in line, whether to start, to carry out an approved plan or to resume, join the line again in the
    /// order they were started, to do what they were waiting to do.
    /// </summary>
    public async Task RecoverInterruptedRunsAsync(CancellationToken ct = default)
    {
        foreach (var run in (await store.ListRunsAsync(ct)).OrderBy(r => r.Id))
        {
            if (IsActive(run.Id)) continue;
            var request = QueuedRequest.Parse(run.QueuedRequest)
                ?? (run is { Status: RunStatus.Queued, BranchName: null } ? new QueuedRequest(RequestKind.Start) : null);
            if (request is not null && run.Status == request.WaitingStatus)
            {
                // Each takes its place before the next is launched, so the line keeps its order.
                await Launch(run.Id, request).Joined.Task;
                continue;
            }
            if (RunStateMachine.IsWorking(run.Status)) await PauseRunAsync(run, InterruptedReason, ct);
        }
    }

    /// <summary>Stops pipelines for shutdown. Their state is left as is, for recovery at the next start.</summary>
    public async Task StopAllAsync()
    {
        // Runs giving back their workers mustn't let waiting runs in, which would start them as Hatchet stops.
        _stopping = true;
        foreach (var active in _active.Values) await active.Cancellation.CancelAsync();
        await Task.WhenAll(_active.Values.Select(a => a.Completion));
    }

    // ---- Pipeline ----

    /// <summary>Starts a pipeline that waits in line for a worker, then carries out the request.</summary>
    private ActivePipeline Launch(int runId, QueuedRequest request)
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
                var settings = await WaitForWorkerAsync(runId, request, active, token);
                var run = await store.GetRunAsync(runId, token);
                run.QueuedRequest = null;
                await store.UpdateRunAsync(run, token);
                context = new PipelineContext(run, ModelChoices.Apply(settings, run.Task.ModelChoice));
                await RunRequestAsync(context, request, token);
            }
            catch (Exception ex) when (ex is PausedByUserException || ex is OperationCanceledException && token.IsCancellationRequested && active.PauseRequested)
            {
                // Leaving the line drops what the run was waiting to do.
                await ForgetQueuedRequestAsync(runId);
                await PauseByUserAsync(context?.Run ?? await store.GetRunAsync(runId, CancellationToken.None));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // The user cancelled (CancelAsync finishes the job) or the app is stopping (recovery does, and a run
                // still in line keeps its request so it can join the line again).
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Pipeline for run {RunId} stopped", runId);
                await ForgetQueuedRequestAsync(runId);
                await HandlePipelineFailureAsync(runId, context, ex);
            }
            finally
            {
                active.Joined.TrySetResult();
                _active.TryRemove(runId, out _);
                _queue.Release(runId);
                bus.Publish(new QueueChanged(runId));
                await RefreshQueueAsync();
            }
        }, CancellationToken.None);
        return active;
    }

    /// <summary>
    /// Waits in line until the run's pool has a worker free and no other run has its repository. Returns the settings
    /// the run starts with, read once its turn comes, which can be long after it joined.
    /// </summary>
    private async Task<FactorySettings> WaitForWorkerAsync(int runId, QueuedRequest request, ActivePipeline active, CancellationToken ct)
    {
        var run = await store.GetRunAsync(runId, ct);
        var repository = RepositoryKey(run.Task.RepositoryPath);
        // Saved first, so if Hatchet restarts while the run waits, it joins the line again to do the same thing.
        var saved = JsonSerializer.Serialize(request);
        if (run.QueuedRequest != saved)
        {
            run.QueuedRequest = saved;
            await store.UpdateRunAsync(run, ct);
        }

        var pool = PoolFor(ModelChoices.Apply(await settingsStore.GetAsync(ct), run.Task.ModelChoice));
        while (true)
        {
            var wait = _queue.WaitAsync(runId, repository, pool, ct);
            active.Joined.TrySetResult();
            await RefreshQueueAsync();
            await wait;
            // Hatchet may be stopping, and a run let in as others gave back their workers keeps its request for the restart.
            ct.ThrowIfCancellationRequested();
            // A pause asked for while it waited takes effect before it starts.
            PauseIfRequested(runId);

            // The line moves runs to the pool their agents use as settings change, so this differs only if they
            // changed again just as it was let in. Then it waits for a worker in the right pool.
            var settings = await settingsStore.GetAsync(ct);
            pool = PoolFor(ModelChoices.Apply(settings, run.Task.ModelChoice));
            if (_queue.WorkingPool(runId) == pool) return settings;
            _queue.Release(runId);
        }
    }

    private async Task ForgetQueuedRequestAsync(int runId)
    {
        try
        {
            var run = await store.GetRunAsync(runId, CancellationToken.None);
            if (run.QueuedRequest is null) return;
            run.QueuedRequest = null;
            await store.UpdateRunAsync(run, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not clear what run {RunId} was waiting to do", runId);
        }
    }

    private async Task HandlePipelineFailureAsync(int runId, PipelineContext? context, Exception ex)
    {
        try
        {
            var run = context?.Run ?? await store.GetRunAsync(runId);
            await LogAsync(runId, run.CurrentSubtaskId, AgentLogKind.Error, run.CurrentStage, ex.Message);

            // Ingest failures mean the repository itself is unusable, and retrying can't fix that. A working tree that
            // isn't ready only needs the user to tidy it, so that pauses for a resume instead.
            if (run.CurrentStage == PipelineStage.Ingest && ex is (GitException or FactoryValidationException) and not RepositoryNotReadyException)
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

        var available = Skills.Load(task.RepositoryPath);
        var prompt = PromptBuilder.Decompose(task.ArchitectureSpec, run.RepoMap, DescribeVerification(task), feedback, available);
        var graph = await InvokeAgentAsync<TaskGraphResponse>(
            context, AgentRole.TaskPlanner, null, prompt,
            g => g.Subtasks.Count == 0 ? "The subtasks list is empty." : null,
            ct);

        // Names are matched whatever their case. Skills that don't exist are dropped.
        var byName = available.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var unknown = graph.Subtasks.SelectMany(s => s.Skills ?? []).Select(n => n.Trim()).Where(n => !byName.ContainsKey(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (unknown.Count > 0)
            await LogAsync(run.Id, null, AgentLogKind.Warning, PipelineStage.Decompose,
                $"The Task Planner named skills that don't exist, so they were left out: {string.Join(", ", unknown)}.", ct);

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
                Skills = (dto.Skills ?? []).Select(n => n.Trim()).Where(byName.ContainsKey).Select(n => byName[n].Name).Distinct().ToList(),
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
            var entry = resumePoint?.SubtaskId == subtask.Id ? resumePoint.Entry
                // A final-check fix has nothing to build, so it starts at the Bug Fixer with the failure it was made for.
                : subtask.IsIntegrationFix ? SubtaskEntry.Fix
                : SubtaskEntry.Start;
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
                    // Fixes before the first review and fixes after it have a budget each, so the reviewer's findings
                    // are always worked on, however many build fixes it took to reach the reviewer.
                    var reviewed = subtask.ReviewJson is not null;
                    var used = reviewed ? subtask.ReviewFixAttempts : subtask.FixAttempts - subtask.ReviewFixAttempts;
                    var settings = context.Settings;

                    // Fixes the guard refused never touched the code, so they don't spend the budget. A run of them still ends the loop.
                    if (RefusedInARow(subtask) >= Math.Min(Math.Max(settings.MaxFixRetries, 1), MaxFixHistory))
                    {
                        await PauseForSubtaskAsync(run, subtask, PauseBriefBuilder.RefusedFixesReason(subtask.Title, RefusedInARow(subtask)), ct);
                        return false;
                    }

                    if (used >= FixBudget(settings))
                    {
                        await PauseForSubtaskAsync(run, subtask, reviewed
                            ? PauseBriefBuilder.ReviewStalledReason(subtask.Title, used)
                            : PauseBriefBuilder.VerificationStalledReason(subtask.Title, used), ct);
                        return false;
                    }

                    // Once the normal attempts are spent, one more goes to the strongest Claude model before anyone is asked.
                    var lastResort = used >= settings.MaxFixRetries ? LastResortModel(settings) : null;

                    if (reviewed) subtask.ReviewFixAttempts++;
                    needsFix = false;
                    if (!await FixAsync(context, subtask, lastResort, ct))
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

                    // The same error after a fix by Claude (or with no Claude to turn to) means the code isn't the problem,
                    // so more attempts would only spend time and tokens.
                    if (SameErrorTwice(subtask.FixHistory) && await ClaudeHasTriedAsync(context, subtask, ct))
                    {
                        await PauseForSubtaskAsync(run, subtask, PauseBriefBuilder.RepeatedFailureReason(subtask.Title, subtask.FixAttempts), ct);
                        return false;
                    }
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
        catch (Exception ex) when (ex is LlmException or GitException or IOException or UnauthorizedAccessException or GatePauseException)
        {
            if (ex is not GatePauseException) await LogAsync(run.Id, subtask.Id, AgentLogKind.Error, run.CurrentStage, ex.Message, CancellationToken.None);
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
        var definitions = await ReadDefinitionsAsync(context, subtask, AgentRole.ContextSpecifier, planned, files, ct, subtask.Title, subtask.Description, subtask.HumanGuidance);
        var fileContext = RepositoryScanner.Render(files);
        var prompt = await FitPromptAsync(context, subtask, AgentRole.ContextSpecifier, definitions,
            (d, k) => PromptBuilder.Refine(subtask, context.Task.ArchitectureSpec, run.RepoMap, fileContext, d, subtask.HumanGuidance, k), ct);

        var refined = await InvokeAgentAsync<RefinedSpecResponse>(context, AgentRole.ContextSpecifier, subtask.Id, prompt, null, ct);
        subtask.RefinedSpecJson = PromptBuilder.ToJson(refined);
        await store.UpdateSubtaskAsync(subtask, ct);
    }

    /// <summary>Calls to the Code Generator for one subtask's build, when each answer is cut off but keeps some whole files.</summary>
    private const int MaxBuildParts = 4;

    /// <summary>
    /// Returns false if the model's patch was rejected, which sends it to the Fixer. An answer that loops or fills the
    /// context window keeps the files it finished, and the Code Generator is asked again for only the rest, so one
    /// oversized file doesn't cost the whole subtask.
    /// </summary>
    private async Task<bool> BuildAsync(PipelineContext context, FactorySubtask subtask, CancellationToken ct)
    {
        var (run, settings) = (context.Run, context.Settings);
        if (subtask.SummaryOfChanges is not null) return true;

        await SetSubtaskStatusAsync(subtask, SubtaskStatus.Building, ct);
        await SetStageAsync(run, PipelineStage.Build, subtask.Id, ct);

        var planned = PlannedFiles(subtask).ToList();
        var targets = subtask.TargetFilesToCreate.Concat(subtask.TargetFilesToModify).Select(NormalizePath).ToList();
        var written = new List<string>();
        var summaries = new List<string>();
        IReadOnlyList<string> remaining = [];
        string? cutOff = null;
        for (var part = 1; ; part++)
        {
            var files = RepositoryScanner.ReadFiles(context.Task.RepositoryPath, planned, settings.MaxFileChars, settings.MaxContextChars);
            var definitions = await ReadDefinitionsAsync(context, subtask, AgentRole.CodeGenerator, planned, files, ct,
                subtask.Title, subtask.Description, subtask.RefinedSpecJson, subtask.HumanGuidance);
            var fileContext = RepositoryScanner.Render(files);
            var prompt = await FitPromptAsync(context, subtask, AgentRole.CodeGenerator, definitions, (d, k) => cutOff is null
                ? PromptBuilder.Build(subtask, fileContext, d, subtask.HumanGuidance, k)
                : PromptBuilder.BuildRest(subtask, fileContext, written, remaining, cutOff, d, subtask.HumanGuidance, k), ct);

            var answer = await AskAgentAsync<FilePatchSetResponse>(
                context, AgentRole.CodeGenerator, subtask.Id, prompt,
                p => p.Operations.Count == 0 ? "The operations list is empty, but this subtask requires file changes." : null,
                ct, salvage: part < MaxBuildParts ? KeepWholeFiles : null);
            var whole = answer.CutOff is null;
            if (!await ApplyPatchAsync(context, subtask, answer.Value, PipelineStage.Build, ct, finished: whole)) return false;
            summaries.Add(answer.Value.SummaryOfChanges.Trim());
            if (whole) break;

            written.AddRange(answer.Value.Operations.Select(o => NormalizePath(o.RelativeFilePath)));
            remaining = targets.Concat(answer.CutOffPath is { } path ? [NormalizePath(path)] : [])
                .Where(p => !written.Contains(p, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            // Nothing planned is missing, so the check and the reviewer judge what was kept.
            if (remaining.Count == 0) break;
            cutOff = answer.CutOff;
            await LogAsync(run.Id, subtask.Id, AgentLogKind.Info, PipelineStage.Build,
                $"Kept the {answer.Value.Operations.Count} whole {(answer.Value.Operations.Count == 1 ? "file" : "files")} from the cut-off answer, and asked the Code Generator for the rest: {string.Join(", ", remaining)}.", ct);
        }

        subtask.SummaryOfChanges = string.Join(" ", summaries.Where(s => s.Length > 0));
        await store.UpdateSubtaskAsync(subtask, ct);
        return true;
    }

    private static (FilePatchSetResponse Value, string? CutOffPath)? KeepWholeFiles(string content) =>
        StructuredOutputParser.SalvagePatch(content) is { } kept ? (kept.Patch, kept.CutOffPath) : null;

    private static string NormalizePath(string path)
    {
        var normal = path.Replace('\\', '/').Trim();
        while (normal.StartsWith("./", StringComparison.Ordinal)) normal = normal[2..];
        return normal.TrimStart('/');
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
        var (settings, repository) = (context.Settings, context.Task.RepositoryPath);
        var named = string.Join('\n', texts.Concat(files.Select(f => f.Content)).Where(t => !string.IsNullOrWhiteSpace(t)));
        var budget = settings.MaxContextChars - files.Sum(f => f.Content?.Length ?? 0);
        if (budget <= 0) return "";

        var paths = DefinitionFinder.Find(repository, named, planned, MaxDefinitionFiles);
        var definitions = paths.Count == 0
            ? []
            : RepositoryScanner.ReadFiles(repository, paths, settings.MaxFileChars, budget).Where(f => f.Content is { Length: > 0 }).ToList();
        budget -= definitions.Sum(f => f.Content!.Length);

        // Types from packages, such as a UI library's components, which agents otherwise guess at from memory.
        var packaged = new List<ContextFile>();
        if (budget > 0)
        {
            foreach (var type in PackageDefinitions.Find(repository, named, DefinitionFinder.DeclaredNames(repository), MaxPackageTypes, Math.Min(MaxPackageTypeChars, settings.MaxFileChars)))
            {
                if (type.Content!.Length > budget) break;
                packaged.Add(type);
                budget -= type.Content.Length;
            }
        }

        var name = AgentProfiles.Resolve(settings, role).DisplayName;
        if (definitions.Count > 0)
            await LogAsync(context.Run.Id, subtask.Id, AgentLogKind.Info, context.Run.CurrentStage,
                $"Gave the {name} the definitions of types it names: {string.Join(", ", definitions.Select(f => f.RelativePath))}.", ct);
        if (packaged.Count > 0)
            await LogAsync(context.Run.Id, subtask.Id, AgentLogKind.Info, context.Run.CurrentStage,
                $"Gave the {name} the signatures of package types it names: {string.Join(", ", packaged.Select(f => f.RelativePath["package ".Length..]))}.", ct);
        return definitions.Count + packaged.Count == 0 ? "" : RepositoryScanner.Render([.. definitions, .. packaged]);
    }

    private const int MaxPackageTypes = 6;
    private const int MaxPackageTypeChars = 6000;

    /// <summary>
    /// Returns false if the fix itself was unusable. The attempt still counts, unless the fix was refused for taking out
    /// something the specification asks for: it changed nothing, so it gives the budget back.
    /// </summary>
    /// <param name="lastResortModel">When set, this is the final automatic attempt and this Claude model makes it.</param>
    private async Task<bool> FixAsync(PipelineContext context, FactorySubtask subtask, string? lastResortModel, CancellationToken ct)
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
        var fileContext = RepositoryScanner.Render(files);
        var prompt = await FitPromptAsync(context, subtask, AgentRole.BugFixer, definitions,
            (d, k) => PromptBuilder.Fix(subtask, subtask.LastFailure ?? "(no failure was recorded)", fileContext, subtask.HumanGuidance, d, k), ct);

        Func<FilePatchSetResponse, string?> validate = p => p.Operations.Count == 0 ? "The operations list is empty, so nothing would change and the failure would repeat." : null;
        var claude = ClaudeCodeOptions.ModelLabel(AgentProfiles.Resolve(settings, AgentRole.BugFixer).ClaudeModel);
        FilePatchSetResponse patch;
        var failedLocalFixes = await CountFailedLocalFixesAsync(run.Id, subtask.Id, ct);
        if (lastResortModel is not null)
        {
            await LogAsync(run.Id, subtask.Id, AgentLogKind.Info, PipelineStage.Fix,
                $"The normal fix attempts are used up, so {ClaudeCodeOptions.ModelLabel(lastResortModel)} makes one last try before Hatchet asks you.", ct);
            patch = await InvokeAgentAsync(context, AgentRole.BugFixer, subtask.Id, prompt, validate, ct, claudeModel: lastResortModel);
        }
        else if (ShouldEscalateFix(settings, failedLocalFixes))
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
        if (DroppedRequiredTypes(context.Task.RepositoryPath, subtask, patch, changed) is { Count: > 0 } dropped)
        {
            await RefuseFixAsync(context, subtask, patch, dropped, ct);
            return false;
        }
        return await ApplyPatchAsync(context, subtask, patch, PipelineStage.Fix, ct);
    }

    private const string RefusedFixLead = "The last fix was not applied, because it took out ";
    private const string FailureToFixLead = "The failure it was meant to fix:";

    /// <summary>
    /// The build, test or review failure under <paramref name="lastFailure"/>, without the notes about fixes that were
    /// refused or rejected on the way, so those notes never replace the failure itself.
    /// </summary>
    private static string FailureBeingFixed(string? lastFailure)
    {
        var failure = lastFailure ?? "(no failure was recorded)";
        var marker = failure.LastIndexOf(FailureToFixLead, StringComparison.Ordinal);
        return marker >= 0 ? failure[(marker + FailureToFixLead.Length)..].TrimStart() : failure;
    }

    [GeneratedRegex(@"\b[A-Za-z_][A-Za-z0-9_]*\b")]
    private static partial Regex Identifier();

    // C-style line and block comments, HTML comments and Razor comments. A "//" right after a colon is a URL, not a comment.
    [GeneratedRegex(@"/\*.*?\*/|<!--.*?-->|@\*.*?\*@|(?<!:)//[^\r\n]*", RegexOptions.Singleline)]
    private static partial Regex Comment();

    /// <summary>The names a file's code uses. Words in comments don't count, as "In" at the start of a sentence isn't the type called In.</summary>
    private static IEnumerable<string> CodeNames(string content) =>
        Identifier().Matches(Comment().Replace(content, " ")).Select(m => m.Value);

    /// <summary>
    /// The types the specification names that a fix takes out of the subtask's files altogether, such as a component
    /// swapped for plain markup to get past an error in how it was used. Only real types count, from the repository
    /// or its packages, so a member the specification got wrong can still be replaced. Only names in code count,
    /// on either side of the fix, so prose in a comment that happens to match a type can't hold a fix back.
    /// </summary>
    internal static IReadOnlyList<string> DroppedRequiredTypes(string repositoryPath, FactorySubtask subtask, FilePatchSetResponse patch, IEnumerable<string> changedFiles)
    {
        var spec = string.Join('\n', new[] { subtask.Title, subtask.Description, subtask.RefinedSpecJson, subtask.HumanGuidance }.Where(t => t is not null));
        var named = Identifier().Matches(spec).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
        if (named.Count == 0 || patch.Operations.Count == 0) return [];

        var root = Path.GetFullPath(repositoryPath);
        string? Read(string relative)
        {
            var full = Path.GetFullPath(Path.Combine(root, relative));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) return null;
            try { return File.ReadAllText(full); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }

        var patched = patch.Operations.Select(o => NormalizePath(o.RelativeFilePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var before = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in patched)
            if (Read(path) is { } content) before.UnionWith(CodeNames(content).Where(named.Contains));
        if (before.Count == 0) return [];

        // After the fix: each file it touches as the patch would leave it (an EDIT carries only its SEARCH/REPLACE blocks,
        // so the blocks alone would make everything else in the file look taken out), and the subtask's other changed files as they are.
        IReadOnlyDictionary<string, string?> preview;
        try { preview = FilePatcher.Preview(repositoryPath, patch.Operations); }
        catch (PatchRejectedException) { return []; } // Applying it fails with a clearer message than this guard's.
        var after = new HashSet<string>(StringComparer.Ordinal);
        foreach (var content in preview.Values)
            if (content is not null) after.UnionWith(CodeNames(content));
        foreach (var path in changedFiles.Select(NormalizePath).Where(p => !patched.Contains(p)))
            if (Read(path) is { } content) after.UnionWith(CodeNames(content));

        var dropped = before.Where(n => !after.Contains(n)).ToList();
        if (dropped.Count == 0) return [];
        var declared = DefinitionFinder.DeclaredNames(repositoryPath);
        var packaged = PackageDefinitions.KnownNames(repositoryPath);
        return dropped.Where(n => declared.Contains(n) || packaged.Contains(n)).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>Leaves the files as they were, and tells the next fix to keep what the specification asks for.</summary>
    private async Task RefuseFixAsync(PipelineContext context, FactorySubtask subtask, FilePatchSetResponse patch, IReadOnlyList<string> dropped, CancellationToken ct)
    {
        var names = string.Join(", ", dropped.Select(n => $"`{n}`"));
        var (it, isIt) = dropped.Count == 1 ? ("it", "it is") : ("them", "they are");
        var failure = FailureBeingFixed(subtask.LastFailure);

        subtask.LastFailure = $"{RefusedFixLead}{names}, which the specification asks for. Keep {it} and fix how {isIt} used instead. "
            + $"The definitions below show {(dropped.Count == 1 ? "its" : "their")} namespace and members, where Hatchet found them.\n\n{FailureToFixLead}\n{failure}";
        // Nothing was written, so this wasn't an attempt at the failure. RefusedInARow ends the loop if the guard keeps saying no.
        subtask.FixAttempts--;
        if (subtask.ReviewJson is not null) subtask.ReviewFixAttempts--;
        subtask.FixHistory.Add($"{RefusedFixLabel} {patch.SummaryOfChanges}\nNot applied: it took out {names}, which the specification asks for.");
        if (subtask.FixHistory.Count > MaxFixHistory) subtask.FixHistory.RemoveAt(0);
        await store.UpdateSubtaskAsync(subtask, ct);
        await LogAsync(context.Run.Id, subtask.Id, AgentLogKind.Warning, PipelineStage.Fix,
            $"Didn't apply the Bug Fixer's change: it took out {names}, which the specification asks for. The next attempt is told to keep {it}.", ct);
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
    private const string RefusedFixLabel = "Refused fix (not counted as an attempt):";
    private const string FailedNextMarker = "\nWhat failed next:\n";

    /// <summary>Fixes in a row, newest first, that the guard refused to apply.</summary>
    internal static int RefusedInARow(FactorySubtask subtask)
    {
        var count = 0;
        for (var i = subtask.FixHistory.Count - 1; i >= 0 && subtask.FixHistory[i].StartsWith(RefusedFixLabel, StringComparison.Ordinal); i--) count++;
        return count;
    }

    /// <summary>Forgets refused fixes after a person has stepped in, so they don't end the next stretch of attempts at once.</summary>
    private static void ForgetRefusedFixes(FactorySubtask subtask) =>
        subtask.FixHistory.RemoveAll(h => h.StartsWith(RefusedFixLabel, StringComparison.Ordinal));

    /// <summary>The stronger model for the final automatic attempt, or null when there's none to turn to.</summary>
    internal static string? LastResortModel(FactorySettings settings)
    {
        var profile = AgentProfiles.Resolve(settings, AgentRole.BugFixer);
        var canUseClaude = profile.UsesClaude || settings.EscalateFixesToClaudeAfter > 0;
        return settings.MaxFixRetries > 0 && canUseClaude && !profile.ClaudeModel.Equals(ClaudeCodeOptions.StrongestModel, StringComparison.OrdinalIgnoreCase)
            ? ClaudeCodeOptions.StrongestModel
            : null;
    }

    /// <summary>The attempts a stage gets: the configured number, plus the last-resort one when there's a stronger model to use.</summary>
    internal static int FixBudget(FactorySettings settings) =>
        settings.MaxFixRetries + (LastResortModel(settings) is null ? 0 : 1);

    /// <summary>
    /// True when the last two fix attempts, in a row, ended in the same error. Line numbers are ignored, as a fix can
    /// move the code without changing what is wrong. Only failed builds and tests count, not a reviewer's objections.
    /// </summary>
    internal static bool SameErrorTwice(IReadOnlyList<string> history)
    {
        if (history.Count < 2) return false;
        var (previous, latest) = (history[^2], history[^1]);
        if (AttemptNumber(previous) is not { } before || AttemptNumber(latest) is not { } after || after != before + 1) return false;
        if (FailedNext(previous) is not { } first || FailedNext(latest) is not { } second) return false;
        return first.Length > 0 && first == second;
    }

    private static int? AttemptNumber(string entry) =>
        entry.StartsWith("Attempt ", StringComparison.Ordinal) && int.TryParse(entry["Attempt ".Length..].Split(':', 2)[0], out var n) ? n : null;

    private static string? FailedNext(string entry)
    {
        var at = entry.IndexOf(FailedNextMarker, StringComparison.Ordinal);
        if (at < 0) return null;
        var outcome = entry[(at + FailedNextMarker.Length)..];
        if (outcome.StartsWith("The reviewer rejected it", StringComparison.Ordinal)) return null;
        return WhitespaceRuns().Replace(LineNumbers().Replace(outcome, ""), " ").Trim();
    }

    [GeneratedRegex(@"\(\d+(,\d+)?\)|:\d+(:\d+)?|\bline \d+", RegexOptions.IgnoreCase)]
    private static partial Regex LineNumbers();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRuns();

    /// <summary>
    /// True when a fix by Claude has been tried since the latest build, or when there's no Claude to try. Two identical
    /// failures from the local model alone don't show the code isn't the problem: Claude hasn't had its go yet.
    /// </summary>
    private async Task<bool> ClaudeHasTriedAsync(PipelineContext context, FactorySubtask subtask, CancellationToken ct)
    {
        var profile = AgentProfiles.Resolve(context.Settings, AgentRole.BugFixer);
        if (!profile.UsesClaude && context.Settings.EscalateFixesToClaudeAfter <= 0) return true;
        var calls = (await store.GetLogsAsync(context.Run.Id, ct: ct))
            .Where(l => l is { Kind: AgentLogKind.LlmCall, Succeeded: true } && l.SubtaskId == subtask.Id)
            .ToList();
        var lastBuild = calls.LastOrDefault(l => l.Role == AgentRole.CodeGenerator)?.Id ?? 0;
        return calls.Any(l => l.Id > lastBuild && l is { Role: AgentRole.BugFixer, Model: not null });
    }

    /// <summary>Notes that the latest fix didn't work and why, if the code being checked came from a fix.</summary>
    private static void RecordFailedFix(FactorySubtask subtask, string outcome)
    {
        if (subtask.FixAttempts == 0) return;
        subtask.FixHistory.Add($"Attempt {subtask.FixAttempts}: {subtask.SummaryOfChanges}\nWhat failed next:\n{outcome}");
        if (subtask.FixHistory.Count > MaxFixHistory) subtask.FixHistory.RemoveAt(0);
    }

    /// <param name="finished">False for part of a build that is still missing files, so a restart builds the subtask again.</param>
    private async Task<bool> ApplyPatchAsync(PipelineContext context, FactorySubtask subtask, FilePatchSetResponse patch, PipelineStage stage, CancellationToken ct,
        bool finished = true)
    {
        var run = context.Run;
        await RequireNoUnplannedSensitiveWritesAsync(context, subtask, patch, stage, ct);
        PatchResult result;
        try
        {
            result = FilePatcher.Apply(context.Task.RepositoryPath, patch.Operations);
        }
        catch (PatchRejectedException ex)
        {
            // Keep the failure the patch was meant to fix, or the next fix works blind, from the rejection alone.
            var rejection = $"The previous patch was rejected and nothing was written: {ex.Message}";
            subtask.LastFailure = subtask.LastFailure is null ? rejection : $"{rejection}\n\n{FailureToFixLead}\n{FailureBeingFixed(subtask.LastFailure)}";
            await store.UpdateSubtaskAsync(subtask, ct);
            await LogAsync(run.Id, subtask.Id, AgentLogKind.Warning, stage, subtask.LastFailure, ct);
            return false;
        }

        if (finished)
        {
            subtask.SummaryOfChanges = patch.SummaryOfChanges;
            await store.UpdateSubtaskAsync(subtask, ct);
        }
        await LogAsync(run.Id, subtask.Id, AgentLogKind.Info, stage, $"Applied {result.Applied.Count} file operations: {string.Join(", ", result.Applied)}", ct);
        foreach (var warning in result.Warnings) await LogAsync(run.Id, subtask.Id, AgentLogKind.Warning, stage, warning, ct);

        await CaptureDiffAsync(context, subtask, stage, ct);
        return true;
    }

    /// <summary>A gate stopped the pipeline for a person. The message is the pause reason.</summary>
    private sealed class GatePauseException(string message) : Exception(message);

    /// <summary>The path as the patcher will resolve it, relative to the repository, so "./" and ".." can't hide it. Null if it won't resolve.</summary>
    private static string? ResolvedRelativePath(string repositoryPath, string path)
    {
        try { return NormalizePath(Path.GetRelativePath(Path.GetFullPath(repositoryPath), FilePatcher.ResolveSafePath(repositoryPath, path))); }
        catch (PatchRejectedException) { return null; } // Applying it fails with a clearer message than this gate's.
    }

    /// <summary>The subtask's planned targets, in the form <see cref="ResolvedRelativePath"/> gives.</summary>
    private static HashSet<string> PlannedTargets(string repositoryPath, FactorySubtask subtask) =>
        subtask.TargetFilesToModify.Concat(subtask.TargetFilesToCreate)
            .Select(p => ResolvedRelativePath(repositoryPath, p) ?? NormalizePath(p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// CI, agent, editor and hook config can run code or change how tools behave, so a patch may only touch them when
    /// the subtask's plan, which a person approved or supplied, names them. Otherwise nothing is written and the run pauses.
    /// </summary>
    private async Task RequireNoUnplannedSensitiveWritesAsync(PipelineContext context, FactorySubtask subtask, FilePatchSetResponse patch, PipelineStage stage, CancellationToken ct)
    {
        var repository = context.Task.RepositoryPath;
        var planned = PlannedTargets(repository, subtask);
        var unplanned = patch.Operations
            .Select(o => ResolvedRelativePath(repository, o.RelativeFilePath))
            .Where(p => p is not null && FilePatcher.IsSensitivePath(p) && !planned.Contains(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unplanned.Count == 0) return;

        var names = string.Join(", ", unplanned.Select(p => $"`{p}`"));
        var reason = $"The change wants to write {names}, which can run code or change how tools behave and {(unplanned.Count == 1 ? "is" : "are")} not in this subtask's files. "
            + "Nothing was written. Add the file to the subtask's files or to your guidance if it is really needed, or make the change yourself, then resume; otherwise retry or skip the subtask.";
        // Kept as the failure, so "Hand to the Fixer" has something to work from.
        subtask.LastFailure = subtask.LastFailure is null ? reason : $"{reason}\n\n{FailureToFixLead}\n{FailureBeingFixed(subtask.LastFailure)}";
        await store.UpdateSubtaskAsync(subtask, ct);
        await LogAsync(context.Run.Id, subtask.Id, AgentLogKind.Warning, stage, reason, ct);
        throw new GatePauseException(reason);
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
        var prompt = PromptBuilder.Review(subtask, PromptBuilder.Head(diff, settings.MaxDiffChars), testSummary, subtask.HumanGuidance);

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
            var message = subtask.IsIntegrationFix ? $"fix(integrate): {title}" : $"feat(subtask): {title}";
            subtask.CommitSha = await git.CommitStagedAsync(message, ct);
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
            await LogAsync(run.Id, subtask.Id, AgentLogKind.Info, PipelineStage.Review, $"Committed {subtask.CommitSha[..8]}: {message}", ct);
        }

        subtask.LastFailure = null;
        await SetSubtaskStatusAsync(subtask, SubtaskStatus.Completed, ct);
    }

    internal const string IntegrationFixTitle = "Make the final check pass";

    /// <param name="handOff">
    /// A person chose to hand the failure to the Bug Fixer. Without it, a failure goes to the Bug Fixer only the first
    /// time, so a fix that doesn't hold ends with a person rather than a loop.
    /// </param>
    private async Task IntegrateAsync(PipelineContext context, CancellationToken ct, bool handOff = false, string? guidance = null)
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
                if (!handOff && subtasks.Any(s => s.IsIntegrationFix))
                {
                    await PauseRunAsync(run, FinalCheckReason(result), ct);
                    return;
                }

                // Each subtask passed on its own, so what breaks is where they meet. The Bug Fixer takes it on as one
                // more subtask: the same fix budget, escalation, guard, review and commit as the rest of the run.
                var fix = await AddIntegrationFixAsync(context, subtasks, command, result, guidance, ct);
                await SetRunStatusAsync(run, RunStatus.Executing, ct);
                run.CurrentSubtaskId = fix.Id;
                if (!await ProcessSubtaskAsync(context, fix, SubtaskEntry.Fix, ct)) return;
                run.CurrentSubtaskId = null;
                await IntegrateAsync(context, ct);
                return;
            }
        }

        var finalDiff = await git.GetRangeDiffAsync(run.BaseCommit!, "HEAD", ct);
        var snapshot = await store.AddDiffAsync(new DiffSnapshot { RunId = run.Id, Stage = PipelineStage.Integrate, UnifiedDiff = finalDiff }, ct);
        bus.Publish(new DiffCaptured(run.Id, null, snapshot.Id));

        await SetRunStatusAsync(run, RunStatus.ReadyToMerge, ct);
    }

    /// <summary>The subtask that hands a failed final check to the Bug Fixer, saved after the run's other subtasks.</summary>
    private async Task<FactorySubtask> AddIntegrationFixAsync(PipelineContext context, IReadOnlyList<FactorySubtask> subtasks, string command,
        CommandResult result, string? guidance, CancellationToken ct)
    {
        var run = context.Run;
        var errors = FailureDigest.Errors(result.Output);
        var fix = await store.AddSubtaskAsync(new FactorySubtask
        {
            RunId = run.Id,
            Order = subtasks.Count == 0 ? 1 : subtasks.Max(s => s.Order) + 1,
            Title = IntegrationFixTitle,
            Description = $"Every planned subtask passed its own check and is committed, but the final check over the whole branch fails: `{command}`. "
                + "Find what breaks where the subtasks' work meets, such as a member one subtask uses and another never declared, or a project only a full rebuild compiles, "
                + "and fix it with the smallest change. Keep what each subtask built; don't remove features, types or tests to get the check through.",
            // The files the errors point at, so the Bug Fixer sees them without being told.
            TargetFilesToModify = [.. FailureDigest.FilesNamed(errors, context.Task.RepositoryPath)],
            VerificationCommand = command,
            CommandApproved = true,
            IsIntegrationFix = true,
            LastFailure = DescribeFailure(result, context.Settings),
            HumanGuidance = string.IsNullOrWhiteSpace(guidance) ? null : guidance.Trim(),
            // It starts from a failure, never from Refine and Build, and counts as built for the pause options.
            SummaryOfChanges = "Nothing yet: the Bug Fixer starts from the final check's failure."
        }, ct);
        bus.Publish(new PlanChanged(run.Id));
        await LogAsync(run.Id, fix.Id, AgentLogKind.Warning, PipelineStage.Integrate,
            $"The final check failed, so the Bug Fixer takes it on as subtask {fix.Order}, \"{fix.Title}\"."
            + (fix.TargetFilesToModify.Count > 0 ? $" The errors point at {string.Join(", ", fix.TargetFilesToModify)}." : ""), ct);
        return fix;
    }

    /// <summary>Why a run paused on its final check, with the errors under "Errors:" so the pause can show them.</summary>
    private static string FinalCheckReason(CommandResult result)
    {
        var what = result.TimedOut
            ? $"The final check timed out: `{result.Command}` ran for {result.Duration.TotalSeconds:0} seconds."
            : $"The final check failed: `{result.Command}` exited with code {result.ExitCode}.";
        var errors = FailureDigest.Errors(result.Output);
        return errors.Count == 0 ? what : $"{what}\nErrors:\n{string.Join('\n', errors.Take(8))}";
    }

    // ---- Agents and commands ----

    /// <summary>
    /// An agent's answer. One kept from a cut-off answer says what cut it off and, if known, the file it was writing.
    /// </summary>
    private sealed record AgentAnswer<T>(T Value, string? CutOff = null, string? CutOffPath = null);

    /// <param name="localTimeLimit">How long the calls on the local model may take in all, counted from now. Past it, a
    /// <see cref="TimeLimitException"/> is thrown. A call handed to Claude isn't limited.</param>
    /// <param name="claudeModel">Runs the call on this Claude model, whatever the agent's profile says.</param>
    private async Task<T> InvokeAgentAsync<T>(PipelineContext context, AgentRole role, int? subtaskId, string prompt, Func<T, string?>? validate, CancellationToken ct,
        bool onClaude = false, TimeSpan? localTimeLimit = null, string? claudeModel = null) =>
        (await AskAgentAsync(context, role, subtaskId, prompt, validate, ct, onClaude, localTimeLimit, claudeModel: claudeModel)).Value;

    /// <summary>
    /// Calls an agent and parses its answer, retrying unusable output. Output that loops, or fills a context window
    /// that is already big enough, goes down a ladder before the run pauses: whatever <paramref name="salvage"/> can
    /// keep is returned; otherwise the call is tried once more with more varied sampling and a note on what went wrong,
    /// then on Claude if the settings allow. Repeating the same call unchanged would only fail the same way.
    /// </summary>
    /// <param name="salvage">Keeps the usable part of a cut-off answer, with the path it was writing when it stopped.</param>
    private async Task<AgentAnswer<T>> AskAgentAsync<T>(PipelineContext context, AgentRole role, int? subtaskId, string prompt, Func<T, string?>? validate, CancellationToken ct,
        bool onClaude = false, TimeSpan? localTimeLimit = null, Func<string, (T Value, string? CutOffPath)?>? salvage = null, string? claudeModel = null)
    {
        var (run, settings) = (context.Run, context.Settings);
        var profile = AgentProfiles.Resolve(settings, role);
        if (onClaude) profile = profile with { Engine = AgentEngine.ClaudeCode };
        if (claudeModel is not null) profile = profile with { Engine = AgentEngine.ClaudeCode, ClaudeModel = claudeModel };
        var schema = LlmSchemas.For<T>();
        var attemptPrompt = prompt;
        var lastError = "";
        var deadline = DateTime.UtcNow + localTimeLimit;
        _active.TryGetValue(run.Id, out var active);
        // What went wrong with each answer that looped or filled the window. Each one moves the call a step down the ladder.
        var runaways = new List<string>();

        for (var attempt = 1; attempt <= settings.MaxLlmAttempts; attempt++)
        {
            PauseIfRequested(run.Id);
            if (attempt > 1) await Task.Delay(TimeSpan.FromSeconds(1), ct);

            var callId = Guid.NewGuid();
            LlmResult? result = null;
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
            var guard = new RepetitionGuard();
            RepetitionVerdict? loop = null;
            var answer = new StringBuilder();
            var thinking = new StringBuilder();
            try
            {
                bus.Publish(started);
                result = await llm.StreamAsync(settings, new LlmRequest(profile, attemptPrompt, schema), delta =>
                {
                    active?.Streamed(delta, ++deltas);
                    bus.Publish(new LlmDeltaReceived(run.Id, callId, delta.Text, delta.IsThinking, deltas));
                    if (loop is not null) return;
                    (delta.IsThinking ? thinking : answer).Append(delta.Text);
                    if (guard.Push(delta.Text, delta.IsThinking) is not { } verdict) return;
                    loop = verdict;
                    call.Cancel();
                }, call.Token);
            }
            // Stopped because it was looping. What streamed so far stands in for the answer.
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && loop is not null)
            {
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

            if (loop is not null)
            {
                // A stopped stream sends no token counts, so the output is estimated from its length.
                var tokens = result is { CompletionTokens: > 0 } finished ? finished.CompletionTokens : ContextWindow.EstimateTokens(answer.Length + thinking.Length);
                result = new LlmResult(answer.ToString(), thinking.ToString(), result?.PromptTokens ?? 0, tokens, "loop", result?.Duration ?? elapsed.Elapsed);
            }
            var outcome = result!;

            run.PromptTokens += outcome.PromptTokens;
            run.CompletionTokens += outcome.CompletionTokens;
            await store.UpdateRunAsync(run, ct);

            var runaway = loop is not null
                ? $"{loop.Describe(profile.DisplayName)} It was stopped after about {outcome.CompletionTokens:N0} tokens and {DescribeDuration(outcome.Duration)}."
                : outcome.FinishReason == "length"
                    ? await DescribeFullWindowAsync(profile, settings, outcome, ct)
                    : null;
            if (runaway is not null)
            {
                await AppendLlmLogAsync(profile, run.Id, subtaskId, attemptPrompt, outcome, false, runaway, ct);
                bus.Publish(new LlmCallFinished(run.Id, callId));
                if (salvage?.Invoke(outcome.Content) is { } kept) return new AgentAnswer<T>(kept.Value, runaway, kept.CutOffPath);

                runaways.Add(runaway);
                attempt--; // Each step down the ladder changes the call, so it isn't one of the repeated attempts.
                attemptPrompt = PromptBuilder.AfterRunaway(prompt, runaway);
                if (!profile.UsesClaude && runaways.Count == 1)
                {
                    profile = profile with { Temperature = RetryTemperature(profile.Temperature) };
                    await LogAsync(run.Id, subtaskId, AgentLogKind.Warning, profile.Stage,
                        $"{runaway} Trying once more at temperature {profile.Temperature:0.0#}, with a note on what went wrong.", ct);
                    continue;
                }
                if (!profile.UsesClaude && settings.EscalateRunawaysToClaude)
                {
                    profile = profile with { Engine = AgentEngine.ClaudeCode };
                    await LogAsync(run.Id, subtaskId, AgentLogKind.Warning, profile.Stage,
                        $"The retry went the same way, so {ClaudeCodeOptions.ModelLabel(profile.ClaudeModel)} takes this call.", ct);
                    continue;
                }
                throw new LlmException(Stuck(profile, settings, runaways));
            }

            var (value, parseError) = TryParse(outcome, validate);
            lastError = parseError ?? "";
            var message = parseError is null
                ? $"{profile.DisplayName} finished."
                : $"{profile.DisplayName} returned unusable output (attempt {attempt} of {settings.MaxLlmAttempts}): {parseError}";
            await AppendLlmLogAsync(profile, run.Id, subtaskId, attemptPrompt, outcome, parseError is null, message, ct);
            bus.Publish(new LlmCallFinished(run.Id, callId));

            if (value is not null) return new AgentAnswer<T>(value);
            attemptPrompt = PromptBuilder.Repair(runaways.Count > 0 ? PromptBuilder.AfterRunaway(prompt, runaways[^1]) : prompt, parseError!);
        }

        var before = runaways.Count > 0 ? $"{runaways[^1]} " : "";
        throw new LlmException($"{before}{profile.DisplayName} failed after {settings.MaxLlmAttempts} attempts. {lastError}");
    }

    /// <summary>
    /// The temperature for another try after a loop. Low temperatures make a model that has started repeating itself
    /// likely to keep at it, so the retry samples more freely.
    /// </summary>
    internal static double RetryTemperature(double temperature) => Math.Min(1.0, Math.Max(0.7, temperature + 0.3));

    /// <summary>Why the run pauses after every step of the ladder went the same way.</summary>
    private static string Stuck(AgentProfile profile, FactorySettings settings, IReadOnlyList<string> runaways)
    {
        var tried = runaways.Count switch
        {
            1 => "",
            2 => " Another try with more varied sampling went the same way.",
            _ => " Another try with more varied sampling, then one on Claude, went the same way."
        };
        var claude = profile.UsesClaude
            ? ""
            : settings.EscalateRunawaysToClaude
                ? $", or set the {profile.DisplayName} to run on Claude in Settings"
                : ", or turn on \"Hand runaway calls to Claude\" in Settings";
        return $"{runaways[^1]}{tried} A bigger context window won't help with this. Resume with guidance that narrows the job, such as keeping data files to a few rows{claude}.";
    }

    private static string EffortOf(AgentProfile profile) => profile.UsesClaude ? profile.ClaudeEffort : profile.ReasoningEffort;

    private static string ModelName(AgentProfile profile) => profile.UsesClaude ? ClaudeCodeOptions.ModelLabel(profile.ClaudeModel) : "the local model";

    private static string? ModelOf(AgentProfile profile) => profile.UsesClaude ? profile.ClaudeModel : null;

    /// <summary>
    /// Says how an answer filled the context window, when another try could help: the window was already big enough,
    /// so the answer ran on too long. Throws instead when only something else will: a bigger window, a smaller prompt,
    /// or a lower effort on Claude.
    /// </summary>
    private async Task<string> DescribeFullWindowAsync(AgentProfile profile, FactorySettings settings, LlmResult result, CancellationToken ct)
    {
        if (profile.UsesClaude)
            throw new LlmException($"{profile.DisplayName} reached Claude's output limit before the answer was finished. Try a lower effort for this agent in Settings, then resume the run.");

        var total = result.PromptTokens + result.CompletionTokens;
        var window = await llm.GetContextLengthAsync(settings.BaseUrl, settings.ModelId, ct) ?? total;
        var started = result.Content.Length > 0;
        var usage = total > 0
            ? $"The prompt took {result.PromptTokens:N0} tokens and {(started ? "the thinking and answer" : "the thinking")} took {result.CompletionTokens:N0}, filling the model's {window:N0}-token context window"
            : "The model's context window filled up";
        var unfinished = started ? "before the answer was finished" : "before it started the answer";

        if (window <= 0 || ContextWindow.BiggerWouldHelp(window))
            throw new LlmException($"{profile.DisplayName} ran out of context. {usage} {unfinished}. {ContextWindow.Advice(settings.Provider)} Then resume the run.");
        if (result.PromptTokens * 2 >= window)
            throw new LlmException($"{profile.DisplayName} ran out of context. {usage} {unfinished}. The prompt alone takes half the window, so lower \"Max file context per call\" in Settings or run this agent on Claude, then resume the run.");
        return $"{profile.DisplayName} filled the context window. {usage} {unfinished}, though the window is already {window:N0} tokens.";
    }

    /// <summary>
    /// Builds a prompt with the definitions of the types it names and the skills chosen for the subtask. When the local
    /// model's context window wouldn't then leave the agent room to think and answer, the definitions are left out
    /// first, and then skills from the end of the subtask's list.
    /// </summary>
    private async Task<string> FitPromptAsync(PipelineContext context, FactorySubtask subtask, AgentRole role, string definitions,
        Func<string, string, string> build, CancellationToken ct)
    {
        var settings = context.Settings;
        var profile = AgentProfiles.Resolve(settings, role);
        var skills = await SkillsForAsync(context, subtask, role, ct);
        string Render(IEnumerable<Skill> chosen) => SkillLibrary.Render(chosen, settings.MaxFileChars);

        var prompt = build(definitions, Render(skills));
        var window = (definitions.Length > 0 || skills.Count > 0) && !profile.UsesClaude
            ? await llm.GetContextLengthAsync(settings.BaseUrl, settings.ModelId, ct)
            : null;
        if (window is { } size)
        {
            var reserve = ContextWindow.AnswerReserve(profile.ReasoningEffort);
            int Needed(string text) => ContextWindow.EstimateTokens(profile.SystemPrompt.Length + text.Length) + reserve;

            if (definitions.Length > 0 && Needed(prompt) > size)
            {
                await LogAsync(context.Run.Id, subtask.Id, AgentLogKind.Info, context.Run.CurrentStage,
                    $"Left the type definitions out of the {profile.DisplayName}'s prompt: with them it would take about {Needed(prompt) - reserve:N0} of the model's {size:N0}-token context window, leaving too little room to answer.", ct);
                prompt = build("", Render(skills));
            }

            var dropped = new List<Skill>();
            var before = Needed(prompt) - reserve;
            while (skills.Count > 0 && Needed(prompt) > size)
            {
                dropped.Insert(0, skills[^1]);
                skills.RemoveAt(skills.Count - 1);
                prompt = build("", Render(skills));
            }
            if (dropped.Count > 0)
                await LogAsync(context.Run.Id, subtask.Id, AgentLogKind.Info, context.Run.CurrentStage,
                    $"Left the {Plural(dropped.Count, "skill")} {string.Join(", ", dropped.Select(s => s.Name))} out of the {profile.DisplayName}'s prompt: with {(dropped.Count == 1 ? "it" : "them")} it would take about {before:N0} of the model's {size:N0}-token context window, leaving too little room to answer.", ct);
        }

        if (skills.Count > 0)
            await LogAsync(context.Run.Id, subtask.Id, AgentLogKind.Info, context.Run.CurrentStage,
                $"Gave the {profile.DisplayName} the skills: {string.Join(", ", skills.Select(s => s.Name))}.", ct);
        return prompt;
    }

    private static string Plural(int count, string noun) => count == 1 ? noun : $"{noun}s";

    /// <summary>
    /// The subtask's skills that are for this agent, in the subtask's order. They are read again for every call, so a
    /// skill edited or saved while the run is paused is used when it resumes.
    /// </summary>
    private async Task<List<Skill>> SkillsForAsync(PipelineContext context, FactorySubtask subtask, AgentRole role, CancellationToken ct)
    {
        if (subtask.Skills.Count == 0) return [];
        var available = Skills.Load(context.Task.RepositoryPath).ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        var missing = subtask.Skills.Where(n => !available.ContainsKey(n)).ToList();
        if (missing.Count > 0)
            await LogAsync(context.Run.Id, subtask.Id, AgentLogKind.Warning, context.Run.CurrentStage,
                $"Couldn't find the {Plural(missing.Count, "skill")} {string.Join(", ", missing)}, so the {AgentProfiles.Resolve(context.Settings, role).DisplayName} goes without.", ct);
        return subtask.Skills
            .Where(available.ContainsKey)
            .Select(n => available[n])
            .Where(s => s.IsFor(role))
            .DistinctBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static (T? Value, string? Error) TryParse<T>(LlmResult result, Func<T, string?>? validate)
    {
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
            // A final-check fix has no plan to build from: starting over means dropping its changes, running the check
            // again and fixing what fails, with a fresh budget.
            case HumanDecision.RestartSubtask when subtask.IsIntegrationFix:
                await RollbackAsync(run, ct);
                subtask.ReviewJson = null;
                subtask.FixAttempts = 0;
                subtask.ReviewFixAttempts = 0;
                subtask.FixHistory.Clear();
                return new ResumePoint(subtask.Id, SubtaskEntry.Verify);

            case HumanDecision.RestartSubtask:
                await RollbackAsync(run, ct);
                subtask.RefinedSpecJson = null;
                subtask.SummaryOfChanges = null;
                subtask.LastFailure = null;
                subtask.ReviewJson = null;
                subtask.FixAttempts = 0;
                subtask.ReviewFixAttempts = 0;
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
                subtask.ReviewFixAttempts = 0;
                ForgetRefusedFixes(subtask);
                return new ResumePoint(subtask.Id, SubtaskEntry.Fix);

            case HumanDecision.Reverify when built:
                subtask.FixAttempts = 0;
                subtask.ReviewFixAttempts = 0;
                ForgetRefusedFixes(subtask);
                return new ResumePoint(subtask.Id, SubtaskEntry.Verify);

            case HumanDecision.Resume when built:
                ForgetRefusedFixes(subtask);
                return new ResumePoint(subtask.Id, SubtaskEntry.Verify);

            default:
                await SetSubtaskStatusAsync(subtask, SubtaskStatus.Pending, ct);
                return new ResumePoint(subtask.Id, subtask.IsIntegrationFix ? SubtaskEntry.Fix : SubtaskEntry.Start);
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
        // A finished run gives up its repository, which a queued run may be waiting for. A pipeline that is still
        // running lets queued runs in itself as it ends.
        if (RunStateMachine.IsTerminal(to) && !IsActive(run.Id)) await RefreshQueueAsync();
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
        if (!probe.IsClean) throw new RepositoryNotReadyException("The repository has uncommitted changes. Commit or stash them first, so Hatchet's commits and rollbacks can't touch your work.");
        if (probe.CurrentBranch is null) throw new RepositoryNotReadyException("The repository is in detached HEAD state. Check out a branch first.");
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
