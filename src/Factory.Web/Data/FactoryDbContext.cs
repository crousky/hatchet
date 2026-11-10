using Factory.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Factory.Web.Data;

public sealed class SettingsRow
{
    public int Id { get; set; }
    public string Json { get; set; } = "{}";
}

public sealed class FactoryDbContext(DbContextOptions<FactoryDbContext> options) : DbContext(options)
{
    public DbSet<FactoryTask> Tasks => Set<FactoryTask>();
    public DbSet<FactoryRun> Runs => Set<FactoryRun>();
    public DbSet<FactorySubtask> Subtasks => Set<FactorySubtask>();
    public DbSet<AgentLogEntry> AgentLogs => Set<AgentLogEntry>();
    public DbSet<DiffSnapshot> Diffs => Set<DiffSnapshot>();
    public DbSet<SettingsRow> Settings => Set<SettingsRow>();
    public DbSet<AdHocRequest> AdHocRequests => Set<AdHocRequest>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<RequestKind>().HaveConversion<string>();
        builder.Properties<RequestStatus>().HaveConversion<string>();
        // SQLite drops DateTimeKind. Everything is stored as UTC, so restore it on read.
        builder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        builder.Properties<RunStatus>().HaveConversion<string>();
        builder.Properties<SubtaskStatus>().HaveConversion<string>();
        builder.Properties<PipelineStage>().HaveConversion<string>();
        builder.Properties<AgentLogKind>().HaveConversion<string>();
        builder.Properties<AgentRole>().HaveConversion<string>();
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<FactoryTask>().HasMany(t => t.Runs).WithOne(r => r.Task).HasForeignKey(r => r.TaskId);
        model.Entity<FactoryRun>().HasMany(r => r.Subtasks).WithOne().HasForeignKey(s => s.RunId);
        model.Entity<FactoryRun>().HasIndex(r => r.Status);
        model.Entity<FactorySubtask>().HasIndex(s => new { s.RunId, s.Order });
        model.Entity<AgentLogEntry>().HasOne<FactoryRun>().WithMany().HasForeignKey(l => l.RunId);
        model.Entity<AgentLogEntry>().HasIndex(l => l.RunId);
        model.Entity<DiffSnapshot>().HasOne<FactoryRun>().WithMany().HasForeignKey(d => d.RunId);
        model.Entity<DiffSnapshot>().HasIndex(d => d.RunId);
        model.Entity<AdHocRequest>().HasIndex(r => r.RepositoryPath);
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        value => value,
        value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
}

public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<FactoryDbContext>
{
    public FactoryDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<FactoryDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
