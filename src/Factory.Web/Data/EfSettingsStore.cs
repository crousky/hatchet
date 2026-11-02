using System.Text.Json;
using Factory.Contracts;
using Factory.Engine.Orchestration;
using Microsoft.EntityFrameworkCore;

namespace Factory.Web.Data;

public sealed class EfSettingsStore(IDbContextFactory<FactoryDbContext> factory) : ISettingsStore
{
    private const int RowId = 1;

    public event Action? Changed;

    public async Task<FactorySettings> GetAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == RowId, ct);
        return row is null ? new FactorySettings() : JsonSerializer.Deserialize<FactorySettings>(row.Json) ?? new FactorySettings();
    }

    public async Task SaveAsync(FactorySettings settings, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.Settings.SingleOrDefaultAsync(s => s.Id == RowId, ct);
        if (row is null)
        {
            row = new SettingsRow { Id = RowId };
            db.Settings.Add(row);
        }
        row.Json = JsonSerializer.Serialize(settings);
        await db.SaveChangesAsync(ct);
        Changed?.Invoke();
    }
}
