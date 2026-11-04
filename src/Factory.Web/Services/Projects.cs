using Factory.Contracts;
using Factory.Engine.Orchestration;

namespace Factory.Web.Services;

/// <summary>A project: the runs built in one repository, newest first.</summary>
public sealed record ProjectGroup(string Repository, string Name, IReadOnlyList<FactoryRun> Runs)
{
    /// <summary>Runs not yet merged, closed, failed or cancelled.</summary>
    public int Open => Runs.Count(r => !RunStateMachine.IsTerminal(r.Status));
}

public static class Projects
{
    /// <summary>Groups runs by the repository they build in, with the project of the latest run first.</summary>
    public static IReadOnlyList<ProjectGroup> Group(IEnumerable<FactoryRun> runs) => runs
        .GroupBy(r => FactoryOrchestrator.RepositoryKey(r.Task.RepositoryPath), StringComparer.OrdinalIgnoreCase)
        .Select(g => new ProjectGroup(g.Key, Fmt.FolderName(g.Key), g.OrderByDescending(r => r.Id).ToList()))
        .OrderByDescending(p => p.Runs[0].Id)
        .ToList();
}
