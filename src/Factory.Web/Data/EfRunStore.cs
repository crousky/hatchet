using Factory.Contracts;
using Factory.Engine.Orchestration;
using Microsoft.EntityFrameworkCore;

namespace Factory.Web.Data;

public sealed class EfRunStore(IDbContextFactory<FactoryDbContext> factory) : IRunStore
{
    public async Task<FactoryRun> CreateRunAsync(int? existingTaskId, FactoryTask? newTask, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var run = new FactoryRun();
        if (existingTaskId is { } taskId) run.TaskId = taskId;
        else run.Task = newTask ?? throw new ArgumentNullException(nameof(newTask));

        db.Runs.Add(run);
        await db.SaveChangesAsync(ct);
        return await GetRunAsync(run.Id, ct);
    }

    public async Task<FactoryRun> GetRunAsync(int runId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Runs.AsNoTracking().Include(r => r.Task).SingleAsync(r => r.Id == runId, ct);
    }

    public async Task<IReadOnlyList<FactoryRun>> ListRunsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Runs.AsNoTracking().Include(r => r.Task).OrderByDescending(r => r.Id).ToListAsync(ct);
    }

    public async Task UpdateRunAsync(FactoryRun run, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Runs.SingleAsync(r => r.Id == run.Id, ct);
        db.Entry(row).CurrentValues.SetValues(run);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<FactorySubtask>> GetSubtasksAsync(int runId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Subtasks.AsNoTracking().Where(s => s.RunId == runId).OrderBy(s => s.Order).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<FactorySubtask>> GetSubtasksForRunsAsync(IReadOnlyCollection<int> runIds, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Subtasks.AsNoTracking().Where(s => runIds.Contains(s.RunId)).OrderBy(s => s.RunId).ThenBy(s => s.Order).ToListAsync(ct);
    }

    public async Task UpdateSubtaskAsync(FactorySubtask subtask, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Subtasks.SingleAsync(s => s.Id == subtask.Id, ct);
        db.Entry(row).CurrentValues.SetValues(subtask);
        await db.SaveChangesAsync(ct);
    }

    public async Task ReplaceSubtasksAsync(int runId, IReadOnlyList<FactorySubtask> subtasks, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Subtasks.Where(s => s.RunId == runId).ExecuteDeleteAsync(ct);
        db.Subtasks.AddRange(subtasks);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<FactorySubtask> AddSubtaskAsync(FactorySubtask subtask, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Subtasks.Add(subtask);
        await db.SaveChangesAsync(ct);
        db.Entry(subtask).State = EntityState.Detached;
        return subtask;
    }

    public async Task<AgentLogEntry> AppendLogAsync(AgentLogEntry entry, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.AgentLogs.Add(entry);
        await db.SaveChangesAsync(ct);
        return entry;
    }

    public async Task<IReadOnlyList<AgentLogEntry>> GetLogsAsync(int runId, long afterId = 0, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.AgentLogs.AsNoTracking().Where(l => l.RunId == runId && l.Id > afterId).OrderBy(l => l.Id).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<AgentLogEntry>> GetRecentEventsAsync(int runId, int count, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var latest = await db.AgentLogs.AsNoTracking()
            .Where(l => l.RunId == runId && (l.Kind != AgentLogKind.LlmCall || !l.Succeeded))
            .OrderByDescending(l => l.Id).Take(count).ToListAsync(ct);
        latest.Reverse();
        return latest;
    }

    public async Task<IReadOnlyDictionary<int, TimeSpan>> GetWorkBySubtaskAsync(int runId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.AgentLogs.AsNoTracking()
            .Where(l => l.RunId == runId && l.SubtaskId != null)
            .GroupBy(l => l.SubtaskId!.Value)
            .Select(g => new { SubtaskId = g.Key, Milliseconds = g.Sum(l => l.DurationMs) })
            .ToDictionaryAsync(w => w.SubtaskId, w => TimeSpan.FromMilliseconds(w.Milliseconds), ct);
    }

    public async Task<DiffSnapshot> AddDiffAsync(DiffSnapshot diff, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.Diffs.Add(diff);
        await db.SaveChangesAsync(ct);
        return diff;
    }

    public async Task<IReadOnlyList<DiffSnapshot>> GetDiffsAsync(int runId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.Diffs.AsNoTracking().Where(d => d.RunId == runId).OrderBy(d => d.Id).ToListAsync(ct);
    }
}
