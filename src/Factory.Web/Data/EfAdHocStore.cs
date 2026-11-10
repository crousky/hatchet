using Factory.Contracts;
using Factory.Engine.Orchestration;
using Microsoft.EntityFrameworkCore;

namespace Factory.Web.Data;

public sealed class EfAdHocStore(IDbContextFactory<FactoryDbContext> factory) : IAdHocStore
{
    public async Task<AdHocRequest> CreateAsync(AdHocRequest request, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        db.AdHocRequests.Add(request);
        await db.SaveChangesAsync(ct);
        return request;
    }

    public async Task<AdHocRequest?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.AdHocRequests.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
    }

    public async Task<IReadOnlyList<AdHocRequest>> ListAsync(string? repositoryPath = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        // Lists leave out the output and the result, which can be large and are only read on a request's own page.
        var all = await db.AdHocRequests.AsNoTracking().OrderByDescending(r => r.Id)
            .Select(r => new AdHocRequest
            {
                Id = r.Id, RepositoryPath = r.RepositoryPath, Kind = r.Kind, Status = r.Status, Title = r.Title, Input = r.Input,
                Command = r.Command, Source = r.Source, WorkDirectory = r.WorkDirectory, ExitCode = r.ExitCode, RunId = r.RunId, Error = r.Error,
                CreatedAt = r.CreatedAt, CompletedAt = r.CompletedAt
            })
            .ToListAsync(ct);
        if (repositoryPath is null) return all;

        // Folders match however their path was written: by case and by a trailing slash.
        var key = FactoryOrchestrator.RepositoryKey(repositoryPath);
        return all.Where(r => FactoryOrchestrator.RepositoryKey(r.RepositoryPath).Equals(key, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public async Task UpdateAsync(AdHocRequest request, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.AdHocRequests.SingleOrDefaultAsync(r => r.Id == request.Id, ct);
        if (row is null) return; // Deleted while it was finishing.
        db.Entry(row).CurrentValues.SetValues(request);
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await db.AdHocRequests.Where(r => r.Id == id).ExecuteDeleteAsync(ct);
    }
}
