using Factory.Engine.Orchestration;

namespace Factory.Web.Services;

/// <summary>Recovers runs that a previous process left mid-work, and stops pipelines on shutdown.</summary>
public sealed class OrchestratorLifetime(FactoryOrchestrator orchestrator) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => orchestrator.RecoverInterruptedRunsAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => orchestrator.StopAllAsync();
}
