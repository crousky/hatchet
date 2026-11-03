using Factory.Contracts;
using Factory.Engine.Orchestration;

namespace Factory.Tests;

public class WorkEstimatorTests
{
    [Fact]
    public void Says_nothing_until_enough_subtasks_have_finished()
    {
        var (subtasks, work) = Plan(finishedMinutes: [20, 30], left: 5);

        Assert.Null(WorkEstimator.Estimate(subtasks, work));
    }

    [Fact]
    public void Says_nothing_once_no_subtasks_are_left()
    {
        var (subtasks, work) = Plan(finishedMinutes: [20, 30, 40], left: 0);

        Assert.Null(WorkEstimator.Estimate(subtasks, work));
    }

    [Fact]
    public void Subtasks_that_all_take_the_same_time_give_an_exact_estimate()
    {
        var (subtasks, work) = Plan(finishedMinutes: [20, 20, 20], left: 4);

        var estimate = WorkEstimator.Estimate(subtasks, work)!;

        Assert.Equal(TimeSpan.FromMinutes(80), estimate.Low);
        Assert.Equal(TimeSpan.FromMinutes(80), estimate.High);
        Assert.Equal(4, estimate.SubtasksLeft);
        Assert.Equal(3, estimate.FinishedSubtasks);
        Assert.Equal(TimeSpan.FromMinutes(20), estimate.AveragePerSubtask);
    }

    [Fact]
    public void Work_already_spent_on_a_subtask_comes_off_the_estimate()
    {
        var (subtasks, work) = Plan(finishedMinutes: [20, 20, 20], left: 2);
        work[subtasks[^1].Id] = TimeSpan.FromMinutes(15);

        var estimate = WorkEstimator.Estimate(subtasks, work)!;

        Assert.Equal(TimeSpan.FromMinutes(25), estimate.High);
    }

    [Fact]
    public void Skipped_subtasks_are_neither_finished_work_nor_work_left()
    {
        var (subtasks, work) = Plan(finishedMinutes: [20, 20, 20], left: 1);
        var skipped = new FactorySubtask { Id = 100, Status = SubtaskStatus.Skipped };
        work[skipped.Id] = TimeSpan.FromMinutes(500);

        var estimate = WorkEstimator.Estimate([.. subtasks, skipped], work)!;

        Assert.Equal(1, estimate.SubtasksLeft);
        Assert.Equal(TimeSpan.FromMinutes(20), estimate.High);
    }

    [Fact]
    public void A_run_with_uneven_subtasks_gets_a_range_that_narrows_as_more_finish()
    {
        // The working minutes of the first eight subtasks of a real run, where some needed long fixes.
        double[] early = [17, 29, 96, 231, 17, 92, 57, 34];
        var (subtasks, work) = Plan(early, left: 43);

        var estimate = WorkEstimator.Estimate(subtasks, work)!;

        var expected = TimeSpan.FromMinutes(early.Average() * 43);
        Assert.InRange(expected, estimate.Low, estimate.High);
        Assert.InRange(estimate.Low.TotalHours, 25, 40);
        Assert.InRange(estimate.High.TotalHours, 60, 80);

        // The same subtasks four times over, with fewer left: the range is much tighter for its size.
        var (later, laterWork) = Plan([.. early, .. early, .. early, .. early], left: 19);
        var narrower = WorkEstimator.Estimate(later, laterWork)!;
        Assert.True(Width(narrower) < Width(estimate) * 0.75, $"{Width(narrower):0.00} vs {Width(estimate):0.00}");
    }

    [Fact]
    public void A_call_still_running_counts_as_work_spent_on_its_subtask()
    {
        var (subtasks, work) = Plan(finishedMinutes: [20, 20, 20], left: 2);
        work[subtasks[^1].Id] = TimeSpan.FromMinutes(5);

        var estimate = WorkEstimator.Estimate(subtasks, work, (subtasks[^1].Id, TimeSpan.FromMinutes(10)))!;

        Assert.Equal(TimeSpan.FromMinutes(25), estimate.High);
    }

    /// <summary>The range's width relative to its middle.</summary>
    private static double Width(WorkEstimate estimate) =>
        (estimate.High - estimate.Low) / ((estimate.High + estimate.Low) / 2);

    private static (List<FactorySubtask> Subtasks, Dictionary<int, TimeSpan> Work) Plan(double[] finishedMinutes, int left)
    {
        var subtasks = new List<FactorySubtask>();
        var work = new Dictionary<int, TimeSpan>();
        foreach (var minutes in finishedMinutes)
        {
            var subtask = new FactorySubtask { Id = subtasks.Count + 1, Order = subtasks.Count + 1, Status = SubtaskStatus.Completed };
            subtasks.Add(subtask);
            work[subtask.Id] = TimeSpan.FromMinutes(minutes);
        }
        for (var i = 0; i < left; i++)
        {
            subtasks.Add(new FactorySubtask { Id = subtasks.Count + 1, Order = subtasks.Count + 1, Status = SubtaskStatus.Pending });
        }
        return (subtasks, work);
    }
}
