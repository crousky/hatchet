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
    Task UpdateSubtaskAsync(FactorySubtask subtask, CancellationToken ct = default);
    Task ReplaceSubtasksAsync(int runId, IReadOnlyList<FactorySubtask> subtasks, CancellationToken ct = default);

    Task<AgentLogEntry> AppendLogAsync(AgentLogEntry entry, CancellationToken ct = default);
    /// <summary>Entries with an id above <paramref name="afterId"/>, so live views can fetch only what is new.</summary>
    Task<IReadOnlyList<AgentLogEntry>> GetLogsAsync(int runId, long afterId = 0, CancellationToken ct = default);

    Task<DiffSnapshot> AddDiffAsync(DiffSnapshot diff, CancellationToken ct = default);
    Task<IReadOnlyList<DiffSnapshot>> GetDiffsAsync(int runId, CancellationToken ct = default);
}

public interface ISettingsStore
{
    Task<FactorySettings> GetAsync(CancellationToken ct = default);
    Task SaveAsync(FactorySettings settings, CancellationToken ct = default);
}
