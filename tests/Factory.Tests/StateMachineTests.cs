using Factory.Contracts;
using Factory.Engine.Orchestration;

namespace Factory.Tests;

public class StateMachineTests
{
    [Theory]
    [InlineData(RunStatus.Queued, RunStatus.Ingesting)]
    [InlineData(RunStatus.Ingesting, RunStatus.Decomposing)]
    [InlineData(RunStatus.Decomposing, RunStatus.AwaitingPlanApproval)]
    [InlineData(RunStatus.Decomposing, RunStatus.Executing)]
    [InlineData(RunStatus.AwaitingPlanApproval, RunStatus.Executing)]
    [InlineData(RunStatus.AwaitingPlanApproval, RunStatus.Decomposing)]
    [InlineData(RunStatus.Executing, RunStatus.NeedsHuman)]
    [InlineData(RunStatus.NeedsHuman, RunStatus.Executing)]
    [InlineData(RunStatus.Executing, RunStatus.Integrating)]
    [InlineData(RunStatus.Integrating, RunStatus.ReadyToMerge)]
    [InlineData(RunStatus.ReadyToMerge, RunStatus.Merged)]
    public void Run_allows_pipeline_transitions(RunStatus from, RunStatus to)
    {
        var run = new FactoryRun { Status = from };

        RunStateMachine.Apply(run, to);

        Assert.Equal(to, run.Status);
    }

    [Theory]
    [InlineData(RunStatus.Queued, RunStatus.Executing)]
    [InlineData(RunStatus.Ingesting, RunStatus.Executing)]
    [InlineData(RunStatus.AwaitingPlanApproval, RunStatus.Integrating)]
    [InlineData(RunStatus.Executing, RunStatus.ReadyToMerge)]
    [InlineData(RunStatus.ReadyToMerge, RunStatus.Executing)]
    [InlineData(RunStatus.Merged, RunStatus.Executing)]
    [InlineData(RunStatus.Cancelled, RunStatus.Ingesting)]
    public void Run_rejects_skipping_stages_and_leaving_terminal_states(RunStatus from, RunStatus to)
    {
        var run = new FactoryRun { Status = from };

        Assert.Throws<InvalidTransitionException>(() => RunStateMachine.Apply(run, to));
        Assert.Equal(from, run.Status);
    }

    [Theory]
    [InlineData(RunStatus.Merged)]
    [InlineData(RunStatus.Closed)]
    [InlineData(RunStatus.Failed)]
    [InlineData(RunStatus.Cancelled)]
    public void Run_terminal_states_have_no_exits(RunStatus status) =>
        Assert.True(RunStateMachine.IsTerminal(status));

    [Fact]
    public void Every_run_and_subtask_status_has_a_transition_entry()
    {
        foreach (var status in Enum.GetValues<RunStatus>()) _ = RunStateMachine.IsTerminal(status);
        foreach (var status in Enum.GetValues<SubtaskStatus>()) _ = SubtaskStateMachine.IsFinished(status);
    }

    [Theory]
    [InlineData(SubtaskStatus.Pending, SubtaskStatus.Refining)]
    [InlineData(SubtaskStatus.Refining, SubtaskStatus.Building)]
    [InlineData(SubtaskStatus.Building, SubtaskStatus.Verifying)]
    [InlineData(SubtaskStatus.Verifying, SubtaskStatus.Fixing)]
    [InlineData(SubtaskStatus.Fixing, SubtaskStatus.Verifying)]
    [InlineData(SubtaskStatus.Verifying, SubtaskStatus.Reviewing)]
    [InlineData(SubtaskStatus.Reviewing, SubtaskStatus.Fixing)]
    [InlineData(SubtaskStatus.Reviewing, SubtaskStatus.Completed)]
    [InlineData(SubtaskStatus.NeedsHuman, SubtaskStatus.Skipped)]
    public void Subtask_allows_factory_loop_transitions(SubtaskStatus from, SubtaskStatus to)
    {
        var subtask = new FactorySubtask { Status = from };

        SubtaskStateMachine.Apply(subtask, to);

        Assert.Equal(to, subtask.Status);
    }

    [Theory]
    [InlineData(SubtaskStatus.Verifying, SubtaskStatus.Completed)] // Never commit without review.
    [InlineData(SubtaskStatus.Fixing, SubtaskStatus.Reviewing)]    // A fix must be verified before review.
    [InlineData(SubtaskStatus.Building, SubtaskStatus.Reviewing)]  // Review only comes after a passing build.
    [InlineData(SubtaskStatus.Completed, SubtaskStatus.Fixing)]
    [InlineData(SubtaskStatus.Skipped, SubtaskStatus.Pending)]
    public void Subtask_rejects_shortcuts_around_verification_and_review(SubtaskStatus from, SubtaskStatus to)
    {
        var subtask = new FactorySubtask { Status = from };

        Assert.Throws<InvalidTransitionException>(() => SubtaskStateMachine.Apply(subtask, to));
    }
}
