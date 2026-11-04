using Factory.Engine.Orchestration;

namespace Factory.Tests;

public class WorkerQueueTests
{
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<int>> NoHolders = new Dictionary<string, IReadOnlySet<int>>();

    private static Func<WorkerPool, int> Limits(int local, int claude) => pool => pool == WorkerPool.Claude ? claude : local;

    /// <summary>A run let in finishes waiting on another thread, so this waits a little for it.</summary>
    private static bool Started(Task wait) => wait.Wait(TimeSpan.FromSeconds(5));

    [Fact]
    public void Runs_start_in_order_up_to_their_pools_limit()
    {
        var queue = new WorkerQueue();
        var first = queue.WaitAsync(1, "a", WorkerPool.Local, CancellationToken.None);
        var second = queue.WaitAsync(2, "b", WorkerPool.Local, CancellationToken.None);

        queue.Admit(Limits(1, 4), NoHolders);

        Assert.True(Started(first));
        Assert.False(second.IsCompleted);
        Assert.Equal(new QueueSpot(1, WorkerPool.Local, null), queue.SpotOf(2));

        queue.Release(1);
        queue.Admit(Limits(1, 4), NoHolders);

        Assert.True(Started(second));
        Assert.Null(queue.SpotOf(2));
    }

    [Fact]
    public void Each_pool_has_its_own_limit()
    {
        var queue = new WorkerQueue();
        var local = queue.WaitAsync(1, "a", WorkerPool.Local, CancellationToken.None);
        var claude = Enumerable.Range(2, 5).Select(id => queue.WaitAsync(id, $"repo{id}", WorkerPool.Claude, CancellationToken.None)).ToList();

        queue.Admit(Limits(1, 4), NoHolders);

        Assert.True(Started(local));
        Assert.Equal(4, claude.Count(Started));
        Assert.Equal(1, queue.SpotOf(6)?.Position);
    }

    [Fact]
    public void A_run_waits_for_its_repository_without_holding_up_the_runs_behind_it()
    {
        var queue = new WorkerQueue();
        var holders = new Dictionary<string, IReadOnlySet<int>>(StringComparer.OrdinalIgnoreCase) { ["C:\\Repo"] = new HashSet<int> { 7 } };
        var blocked = queue.WaitAsync(1, "c:\\repo", WorkerPool.Claude, CancellationToken.None);
        var free = queue.WaitAsync(2, "C:\\Other", WorkerPool.Claude, CancellationToken.None);

        queue.Admit(Limits(1, 4), holders);

        Assert.False(blocked.IsCompleted);
        Assert.Equal(new QueueSpot(1, WorkerPool.Claude, 7), queue.SpotOf(1));
        Assert.True(Started(free));
    }

    [Fact]
    public void A_run_that_holds_its_repository_itself_can_start()
    {
        var queue = new WorkerQueue();
        var holders = new Dictionary<string, IReadOnlySet<int>> { ["repo"] = new HashSet<int> { 1 } };
        var resumed = queue.WaitAsync(1, "repo", WorkerPool.Local, CancellationToken.None);

        queue.Admit(Limits(1, 4), holders);

        Assert.True(Started(resumed));
    }

    [Fact]
    public void Two_waiting_runs_on_one_repository_go_one_at_a_time()
    {
        var queue = new WorkerQueue();
        var first = queue.WaitAsync(1, "repo", WorkerPool.Claude, CancellationToken.None);
        var second = queue.WaitAsync(2, "repo", WorkerPool.Claude, CancellationToken.None);

        queue.Admit(Limits(1, 4), NoHolders);

        Assert.True(Started(first));
        Assert.Equal(new QueueSpot(1, WorkerPool.Claude, 1), queue.SpotOf(2));
    }

    [Fact]
    public async Task A_run_that_stops_waiting_leaves_the_line()
    {
        var queue = new WorkerQueue();
        _ = queue.WaitAsync(1, "a", WorkerPool.Local, CancellationToken.None);
        queue.Admit(Limits(1, 4), NoHolders);
        using var cancel = new CancellationTokenSource();
        var waiting = queue.WaitAsync(2, "b", WorkerPool.Local, cancel.Token);
        var behind = queue.WaitAsync(3, "c", WorkerPool.Local, CancellationToken.None);

        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Null(queue.SpotOf(2));
        Assert.Equal(1, queue.SpotOf(3)?.Position);
        Assert.False(behind.IsCompleted);
    }

    [Fact]
    public void A_limit_below_one_still_lets_one_run_work()
    {
        var queue = new WorkerQueue();
        var run = queue.WaitAsync(1, "a", WorkerPool.Local, CancellationToken.None);

        queue.Admit(Limits(0, 0), NoHolders);

        Assert.True(Started(run));
    }

    [Fact]
    public void Admit_reports_only_the_runs_it_let_in_and_those_whose_place_changed()
    {
        var queue = new WorkerQueue();
        _ = queue.WaitAsync(1, "a", WorkerPool.Local, CancellationToken.None);
        _ = queue.WaitAsync(2, "b", WorkerPool.Local, CancellationToken.None);
        _ = queue.WaitAsync(3, "c", WorkerPool.Local, CancellationToken.None);

        Assert.Equal([1, 2, 3], queue.Admit(Limits(1, 4), NoHolders));
        Assert.Empty(queue.Admit(Limits(1, 4), NoHolders));

        queue.Release(1);
        Assert.Equal([2, 3], queue.Admit(Limits(1, 4), NoHolders)); // 2 is let in and 3 moves up.
    }

    [Fact]
    public void Waiting_runs_move_to_the_pool_their_agents_use_now_and_keep_their_place()
    {
        var queue = new WorkerQueue();
        var working = queue.WaitAsync(1, "a", WorkerPool.Local, CancellationToken.None);
        var first = queue.WaitAsync(2, "b", WorkerPool.Local, CancellationToken.None);
        var second = queue.WaitAsync(3, "c", WorkerPool.Local, CancellationToken.None);
        queue.Admit(Limits(1, 1), NoHolders);
        Assert.True(Started(working));

        queue.Admit(Limits(1, 1), NoHolders, WorkerPool.Claude);

        Assert.True(Started(first));
        Assert.Equal(WorkerPool.Claude, queue.WorkingPool(2));
        Assert.Equal(WorkerPool.Local, queue.WorkingPool(1));
        Assert.Equal(new QueueSpot(1, WorkerPool.Claude, null), queue.SpotOf(3));
        Assert.False(second.IsCompleted);
    }
}
