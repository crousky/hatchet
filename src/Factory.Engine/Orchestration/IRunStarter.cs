using Factory.Contracts;

namespace Factory.Engine.Orchestration;

/// <summary>What ad hoc requests need from the orchestrator: starting a run, and checking first that one could start.</summary>
public interface IRunStarter
{
    /// <summary>Throws <see cref="FactoryValidationException"/> when a run couldn't start in the folder, for example because the tree is dirty.</summary>
    Task EnsureCanStartAsync(string repositoryPath, CancellationToken ct = default);

    Task<FactoryRun> StartRunAsync(NewRunRequest request, CancellationToken ct = default);
}
