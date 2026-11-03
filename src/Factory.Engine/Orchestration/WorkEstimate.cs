using Factory.Contracts;

namespace Factory.Engine.Orchestration;

/// <summary>How much working time a run's unfinished subtasks should take, as a likely range.</summary>
public sealed record WorkEstimate(TimeSpan Low, TimeSpan High, int SubtasksLeft, int FinishedSubtasks, TimeSpan AveragePerSubtask);

public static class WorkEstimator
{
    /// <summary>Fewer finished subtasks than this say too little about the rest.</summary>
    public const int MinimumFinished = 3;

    /// <summary>
    /// Judges the rest of a run by the subtasks it has finished. Working time is what model calls and commands took,
    /// so time spent paused or waiting for the user doesn't count. A single subtask varies too much to estimate,
    /// since one that needs fixing can take ten times as long, but over many subtasks the spread evens out.
    /// </summary>
    /// <param name="workBySubtask">Working time spent so far on each subtask, by subtask id.</param>
    public static WorkEstimate? Estimate(IReadOnlyList<FactorySubtask> subtasks, IReadOnlyDictionary<int, TimeSpan> workBySubtask)
    {
        var finished = subtasks
            .Where(s => s.Status == SubtaskStatus.Completed)
            .Select(s => workBySubtask.GetValueOrDefault(s.Id).TotalMinutes)
            .Where(minutes => minutes > 0)
            .ToList();
        var left = subtasks.Where(s => !SubtaskStateMachine.IsFinished(s.Status)).ToList();
        if (finished.Count < MinimumFinished || left.Count == 0) return null;

        var mean = finished.Average();
        var variance = finished.Sum(m => (m - mean) * (m - mean)) / (finished.Count - 1);

        // Work already spent on a subtask in progress comes off what it is expected to take.
        var expected = left.Sum(s => Math.Max(mean - workBySubtask.GetValueOrDefault(s.Id).TotalMinutes, 0));
        // One standard deviation: the subtasks left vary among themselves, and the average they are judged by is
        // itself uncertain while few subtasks have finished.
        var spread = Math.Sqrt(variance * left.Count * (1 + (double)left.Count / finished.Count));

        return new WorkEstimate(
            TimeSpan.FromMinutes(Math.Max(expected - spread, 0)),
            TimeSpan.FromMinutes(expected + spread),
            left.Count, finished.Count, TimeSpan.FromMinutes(mean));
    }

    /// <summary>Working time spent on each subtask, from a run's log.</summary>
    public static IReadOnlyDictionary<int, TimeSpan> WorkBySubtask(IEnumerable<AgentLogEntry> logs) => logs
        .Where(l => l.SubtaskId is not null)
        .GroupBy(l => l.SubtaskId!.Value)
        .ToDictionary(g => g.Key, g => TimeSpan.FromMilliseconds(g.Sum(l => l.DurationMs)));
}
