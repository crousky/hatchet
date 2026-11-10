using Factory.Contracts;

namespace Factory.Engine.Orchestration;

/// <summary>Durable run state. Implementations hand out detached copies; callers save changes explicitly.</summary>
public interface IRunStore
{
    Task<FactoryRun> CreateRunAsync(int? existingTaskId, FactoryTask? newTask, CancellationToken ct = default);
    Task<FactoryRun> GetRunAsync(int runId, CancellationToken ct = default);
    Task<IReadOnlyList<FactoryRun>> ListRunsAsync(CancellationToken ct = default);
    Task UpdateRunAsync(FactoryRun run, CancellationToken ct = default);

    Task<IReadOnlyList<FactorySubtask>> GetSubtasksAsync(int runId, CancellationToken ct = default);
    /// <summary>The subtasks of several runs in one query, ordered by run and then by order.</summary>
    Task<IReadOnlyList<FactorySubtask>> GetSubtasksForRunsAsync(IReadOnlyCollection<int> runIds, CancellationToken ct = default);
    Task UpdateSubtaskAsync(FactorySubtask subtask, CancellationToken ct = default);
    Task ReplaceSubtasksAsync(int runId, IReadOnlyList<FactorySubtask> subtasks, CancellationToken ct = default);

    Task<AgentLogEntry> AppendLogAsync(AgentLogEntry entry, CancellationToken ct = default);
    /// <summary>Entries with an id above <paramref name="afterId"/>, so live views can fetch only what is new.</summary>
    Task<IReadOnlyList<AgentLogEntry>> GetLogsAsync(int runId, long afterId = 0, CancellationToken ct = default);
    /// <summary>The latest <paramref name="count"/> entries, oldest first, leaving out model calls that succeeded.</summary>
    Task<IReadOnlyList<AgentLogEntry>> GetRecentEventsAsync(int runId, int count, CancellationToken ct = default);
    /// <summary>Working time spent on each subtask of a run, by subtask id: what its model calls and commands took.</summary>
    Task<IReadOnlyDictionary<int, TimeSpan>> GetWorkBySubtaskAsync(int runId, CancellationToken ct = default);

    Task<DiffSnapshot> AddDiffAsync(DiffSnapshot diff, CancellationToken ct = default);
    Task<IReadOnlyList<DiffSnapshot>> GetDiffsAsync(int runId, CancellationToken ct = default);
}

/// <summary>Durable ad hoc requests, kept beside the runs and grouped with them by repository.</summary>
public interface IAdHocStore
{
    Task<AdHocRequest> CreateAsync(AdHocRequest request, CancellationToken ct = default);
    Task<AdHocRequest?> GetAsync(int id, CancellationToken ct = default);
    /// <summary>
    /// Newest first. Pass a repository folder to list only its requests. Listed requests leave out <c>Output</c> and
    /// <c>Result</c>, which can be large: use <see cref="GetAsync"/> for those, and don't save a listed request back.
    /// </summary>
    Task<IReadOnlyList<AdHocRequest>> ListAsync(string? repositoryPath = null, CancellationToken ct = default);
    Task UpdateAsync(AdHocRequest request, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
}

public interface ISettingsStore
{
    Task<FactorySettings> GetAsync(CancellationToken ct = default);
    Task SaveAsync(FactorySettings settings, CancellationToken ct = default);
}
