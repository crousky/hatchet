using Factory.Contracts;
using Factory.Engine.Orchestration;

namespace Factory.Web.Services;

/// <summary>A project: the runs built in one repository and the ad hoc requests made about it, newest first.</summary>
public sealed record ProjectGroup(string Repository, string Name, IReadOnlyList<FactoryRun> Runs, IReadOnlyList<AdHocRequest> Requests)
{
    /// <summary>Runs not yet merged, closed, failed or cancelled.</summary>
    public int Open => Runs.Count(r => !RunStateMachine.IsTerminal(r.Status));

    /// <summary>Requests still running, such as software that is open.</summary>
    public int RunningRequests => Requests.Count(r => r.IsActive);

    /// <summary>When something last started here, for putting the busiest project first.</summary>
    public DateTime Latest => new[] { Runs.Count > 0 ? Runs.Max(r => r.CreatedAt) : DateTime.MinValue, Requests.Count > 0 ? Requests.Max(r => r.CreatedAt) : DateTime.MinValue }.Max();
}

public static class Projects
{
    /// <summary>
    /// Groups runs and requests by the repository they belong to, with the project that saw something start most recently
    /// first. A repository with only requests, such as one asked about over MCP, still gets a group.
    /// </summary>
    public static IReadOnlyList<ProjectGroup> Group(IEnumerable<FactoryRun> runs, IEnumerable<AdHocRequest>? requests = null)
    {
        var byRuns = runs.ToLookup(r => FactoryOrchestrator.RepositoryKey(r.Task.RepositoryPath), StringComparer.OrdinalIgnoreCase);
        var byRequests = (requests ?? []).ToLookup(r => FactoryOrchestrator.RepositoryKey(r.RepositoryPath), StringComparer.OrdinalIgnoreCase);

        return byRuns.Select(g => g.Key).Concat(byRequests.Select(g => g.Key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(key => new ProjectGroup(
                key, Fmt.FolderName(key),
                byRuns[key].OrderByDescending(r => r.Id).ToList(),
                byRequests[key].OrderByDescending(r => r.Id).ToList()))
            .OrderByDescending(p => p.Latest)
            .ToList();
    }
}
