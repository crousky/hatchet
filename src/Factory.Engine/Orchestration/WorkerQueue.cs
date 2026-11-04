namespace Factory.Engine.Orchestration;

/// <summary>Where a run's agents run, which decides how many such runs may work at once.</summary>
public enum WorkerPool
{
    /// <summary>At least one agent uses the local model, which serves one request at a time.</summary>
    Local,

    /// <summary>Every agent runs on Claude through Claude Code.</summary>
    Claude
}

/// <summary>A run waiting for a worker: its place in line, and what it is waiting for.</summary>
/// <param name="BlockedByRunId">Another run that has this run's repository, if that is why it waits.</param>
public sealed record QueueSpot(int Position, WorkerPool Pool, int? BlockedByRunId);

/// <summary>
/// Hands out workers to runs in the order they asked. A run needs a free worker in its pool, and its repository to
/// itself, since runs share the working tree of the repository they build in. A run held up by its repository doesn't
/// hold up the runs behind it.
/// </summary>
public sealed class WorkerQueue
{
    private sealed class Entry(int runId, string repository, WorkerPool pool)
    {
        public int RunId { get; } = runId;
        public string Repository { get; } = repository;
        public WorkerPool Pool { get; set; } = pool;
        public int? BlockedByRunId { get; set; }

        /// <summary>The place in line last reported for it, so only runs whose place changed are reported again.</summary>
        public QueueSpot? Reported { get; set; }
        public TaskCompletionSource Admitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object _gate = new();
    private readonly List<Entry> _waiting = [];
    private readonly Dictionary<int, Entry> _working = [];

    /// <summary>Joins the line and waits for a worker. Call <see cref="Admit"/> after, so the run can be let in.</summary>
    public async Task WaitAsync(int runId, string repository, WorkerPool pool, CancellationToken ct)
    {
        var entry = new Entry(runId, repository, pool);
        lock (_gate)
        {
            if (_waiting.Any(e => e.RunId == runId) || _working.ContainsKey(runId))
                throw new InvalidOperationException($"Run {runId} is already queued or working.");
            _waiting.Add(entry);
        }

        try
        {
            await entry.Admitted.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // It may have been let in just as it gave up, so its worker is handed back too.
            Release(runId);
            throw;
        }
    }

    /// <summary>Gives back a run's worker, or takes it out of the line.</summary>
    public void Release(int runId)
    {
        lock (_gate)
        {
            _working.Remove(runId);
            _waiting.RemoveAll(e => e.RunId == runId);
        }
    }

    /// <summary>
    /// Lets in, in order, each waiting run whose pool has a worker free and whose repository no other run has.
    /// Returns the runs it let in and the waiting runs whose place in line, or what they wait for, has changed.
    /// </summary>
    /// <param name="limit">How many runs of a pool may work at once. Below 1 counts as 1.</param>
    /// <param name="holders">For each repository, the runs that have it already, such as a paused run with its branch checked out.</param>
    /// <param name="waitingPool">
    /// The pool waiting runs belong to now, if the settings that decide it may have changed since they joined. They
    /// keep their place in line.
    /// </param>
    public IReadOnlyList<int> Admit(Func<WorkerPool, int> limit, IReadOnlyDictionary<string, IReadOnlySet<int>> holders, WorkerPool? waitingPool = null)
    {
        lock (_gate)
        {
            if (waitingPool is { } pool)
            {
                foreach (var entry in _waiting) entry.Pool = pool;
            }

            var working = _working.Values.GroupBy(e => e.Pool).ToDictionary(g => g.Key, g => g.Count());
            var taken = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (repository, runs) in holders) Take(taken, repository).UnionWith(runs);
            foreach (var entry in _working.Values) Take(taken, entry.Repository).Add(entry.RunId);

            var touched = new List<int>();
            foreach (var entry in _waiting.ToList())
            {
                var blocker = Take(taken, entry.Repository).Where(id => id != entry.RunId).Order().Cast<int?>().FirstOrDefault();
                entry.BlockedByRunId = blocker;
                if (blocker is not null || working.GetValueOrDefault(entry.Pool) >= Math.Max(limit(entry.Pool), 1)) continue;

                _waiting.Remove(entry);
                _working[entry.RunId] = entry;
                working[entry.Pool] = working.GetValueOrDefault(entry.Pool) + 1;
                Take(taken, entry.Repository).Add(entry.RunId);
                touched.Add(entry.RunId);
                entry.Admitted.TrySetResult();
            }

            for (var index = 0; index < _waiting.Count; index++)
            {
                var entry = _waiting[index];
                var spot = new QueueSpot(index + 1, entry.Pool, entry.BlockedByRunId);
                if (entry.Reported == spot) continue;
                entry.Reported = spot;
                touched.Add(entry.RunId);
            }
            return touched;
        }
    }

    /// <summary>The pool a run is working in, or null if it hasn't been let in.</summary>
    public WorkerPool? WorkingPool(int runId)
    {
        lock (_gate) return _working.TryGetValue(runId, out var entry) ? entry.Pool : null;
    }

    /// <summary>Where a run is in line, or null if it isn't waiting.</summary>
    public QueueSpot? SpotOf(int runId)
    {
        lock (_gate)
        {
            var index = _waiting.FindIndex(e => e.RunId == runId);
            if (index < 0) return null;
            var entry = _waiting[index];
            return new QueueSpot(index + 1, entry.Pool, entry.BlockedByRunId);
        }
    }

    private static HashSet<int> Take(Dictionary<string, HashSet<int>> taken, string repository)
    {
        if (!taken.TryGetValue(repository, out var runs)) taken[repository] = runs = [];
        return runs;
    }
}
