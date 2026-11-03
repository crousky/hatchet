using Factory.Contracts;

namespace Factory.Engine.Orchestration;

public sealed class InvalidTransitionException(string message) : InvalidOperationException(message);

public static class RunStateMachine
{
    private static readonly Dictionary<RunStatus, RunStatus[]> Allowed = new()
    {
        [RunStatus.Queued] = [RunStatus.Ingesting, RunStatus.NeedsHuman, RunStatus.Failed, RunStatus.Cancelled],
        [RunStatus.Ingesting] = [RunStatus.Decomposing, RunStatus.NeedsHuman, RunStatus.Failed, RunStatus.Cancelled],
        [RunStatus.Decomposing] = [RunStatus.AwaitingPlanApproval, RunStatus.Executing, RunStatus.NeedsHuman, RunStatus.Failed, RunStatus.Cancelled],
        [RunStatus.AwaitingPlanApproval] = [RunStatus.Executing, RunStatus.Decomposing, RunStatus.Cancelled],
        [RunStatus.Executing] = [RunStatus.Integrating, RunStatus.NeedsHuman, RunStatus.Failed, RunStatus.Cancelled],
        [RunStatus.NeedsHuman] = [RunStatus.Ingesting, RunStatus.Decomposing, RunStatus.Executing, RunStatus.Integrating, RunStatus.Cancelled],
        [RunStatus.Integrating] = [RunStatus.ReadyToMerge, RunStatus.NeedsHuman, RunStatus.Failed, RunStatus.Cancelled],
        [RunStatus.ReadyToMerge] = [RunStatus.Merged, RunStatus.Closed],
        [RunStatus.Merged] = [],
        [RunStatus.Closed] = [],
        [RunStatus.Failed] = [],
        [RunStatus.Cancelled] = []
    };

    public static bool CanTransition(RunStatus from, RunStatus to) => Allowed[from].Contains(to);

    public static bool IsTerminal(RunStatus status) => Allowed[status].Length == 0;

    /// <summary>The pipeline is doing work, as opposed to waiting on a human or finished.</summary>
    public static bool IsWorking(RunStatus status) =>
        status is RunStatus.Queued or RunStatus.Ingesting or RunStatus.Decomposing or RunStatus.Executing or RunStatus.Integrating;

    /// <summary>The run still has work ahead of it, whether it is working or waiting on a human.</summary>
    public static bool IsUnderway(RunStatus status) => IsWorking(status) || status == RunStatus.NeedsHuman;

    public static void Apply(FactoryRun run, RunStatus to)
    {
        if (!CanTransition(run.Status, to))
            throw new InvalidTransitionException($"Run {run.Id} cannot move from {run.Status} to {to}.");
        run.Status = to;
    }
}

public static class SubtaskStateMachine
{
    private static readonly Dictionary<SubtaskStatus, SubtaskStatus[]> Allowed = new()
    {
        [SubtaskStatus.Pending] = [SubtaskStatus.Refining, SubtaskStatus.Building, SubtaskStatus.NeedsHuman, SubtaskStatus.Skipped, SubtaskStatus.Cancelled],
        [SubtaskStatus.Refining] = [SubtaskStatus.Building, SubtaskStatus.NeedsHuman, SubtaskStatus.Cancelled],
        [SubtaskStatus.Building] = [SubtaskStatus.Verifying, SubtaskStatus.Fixing, SubtaskStatus.NeedsHuman, SubtaskStatus.Cancelled],
        [SubtaskStatus.Verifying] = [SubtaskStatus.Reviewing, SubtaskStatus.Fixing, SubtaskStatus.NeedsHuman, SubtaskStatus.Cancelled],
        [SubtaskStatus.Fixing] = [SubtaskStatus.Verifying, SubtaskStatus.Fixing, SubtaskStatus.NeedsHuman, SubtaskStatus.Cancelled],
        [SubtaskStatus.Reviewing] = [SubtaskStatus.Completed, SubtaskStatus.Fixing, SubtaskStatus.NeedsHuman, SubtaskStatus.Cancelled],
        [SubtaskStatus.NeedsHuman] = [SubtaskStatus.Pending, SubtaskStatus.Verifying, SubtaskStatus.Fixing, SubtaskStatus.Skipped, SubtaskStatus.Cancelled],
        [SubtaskStatus.Completed] = [],
        [SubtaskStatus.Skipped] = [],
        [SubtaskStatus.Cancelled] = []
    };

    public static bool CanTransition(SubtaskStatus from, SubtaskStatus to) => Allowed[from].Contains(to);

    public static bool IsFinished(SubtaskStatus status) => Allowed[status].Length == 0;

    public static void Apply(FactorySubtask subtask, SubtaskStatus to)
    {
        if (!CanTransition(subtask.Status, to))
            throw new InvalidTransitionException($"Subtask {subtask.Id} cannot move from {subtask.Status} to {to}.");
        subtask.Status = to;
    }
}
