using Factory.Contracts;
using Factory.Engine.Llm;
using Factory.Engine.Orchestration;
using Factory.Web.Data;

namespace Factory.Web.Services;

/// <summary>Polls the configured endpoint, so the UI always shows whether the model is reachable.</summary>
public sealed class ModelHealthService(EfSettingsStore settingsStore, ILlmClient llm, LmStudioModelLoader loader, ILogger<ModelHealthService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    public ModelEndpointStatus? Current { get; private set; }

    /// <summary>The configured server type, for advice that depends on it.</summary>
    public LlmProvider Provider { get; private set; }

    /// <summary>The agents set to run on Claude Code, and whether it can serve them. Null status until checked.</summary>
    public IReadOnlyList<string> ClaudeAgents { get; private set; } = [];

    public ClaudeCodeStatus? ClaudeCode { get; private set; }

    // Starting Claude Code takes a moment, so its login is checked less often than the model server.
    private static readonly TimeSpan ClaudeCheckInterval = TimeSpan.FromMinutes(1);
    private DateTime _claudeCheckedAt = DateTime.MinValue;

    /// <summary>True while LM Studio is reloading the model with a bigger context window.</summary>
    public bool Reloading { get; private set; }

    public string? ReloadError { get; private set; }

    public event Action? Changed;

    /// <summary>Has LM Studio load the configured model again with the recommended context window.</summary>
    public async Task ReloadWithRecommendedContextAsync()
    {
        if (Reloading) return;
        Reloading = true;
        ReloadError = null;
        Changed?.Invoke();
        try
        {
            var settings = await settingsStore.GetAsync();
            var applied = await loader.ReloadAsync(settings.BaseUrl, settings.ModelId, ContextWindow.Recommended, CancellationToken.None);
            logger.LogInformation("Reloaded {Model} with a {Context}-token context", settings.ModelId, applied);
        }
        catch (LlmException ex)
        {
            ReloadError = ex.Message;
        }
        finally
        {
            Reloading = false;
            try
            {
                await RefreshAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Model health check after reload failed");
                Changed?.Invoke();
            }
        }
    }

    public async Task<ModelEndpointStatus> CheckAsync(string baseUrl, string modelId, CancellationToken ct = default)
    {
        try
        {
            var models = await llm.ListModelsAsync(baseUrl, ct);
            var available = models.Contains(modelId);
            var contextLength = available ? await llm.GetContextLengthAsync(baseUrl, modelId, ct) : null;
            return new ModelEndpointStatus(true, baseUrl, modelId, available, models, null, contextLength);
        }
        catch (LlmException ex)
        {
            return new ModelEndpointStatus(false, baseUrl, modelId, false, [], ex.Message);
        }
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var settings = await settingsStore.GetAsync(ct);
        Provider = settings.Provider;
        Current = await CheckAsync(settings.BaseUrl, settings.ModelId, ct);

        ClaudeAgents = AgentProfiles.Defaults
            .Select(p => AgentProfiles.Resolve(settings, p.Role))
            .Where(p => p.UsesClaude)
            .Select(p => p.DisplayName)
            .ToList();
        if (ClaudeAgents.Count == 0)
        {
            ClaudeCode = null;
        }
        else if (DateTime.UtcNow - _claudeCheckedAt > ClaudeCheckInterval)
        {
            ClaudeCode = await ClaudeCodeLlmClient.CheckAsync(settings.ClaudeCodePath, ct);
            _claudeCheckedAt = DateTime.UtcNow;
        }
        Changed?.Invoke();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        settingsStore.Changed += OnSettingsChanged;
        try
        {
            using var timer = new PeriodicTimer(PollInterval);
            do
            {
                try
                {
                    await RefreshAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Model health check failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            settingsStore.Changed -= OnSettingsChanged;
        }
    }

    private void OnSettingsChanged()
    {
        _claudeCheckedAt = DateTime.MinValue;
        _ = RefreshAsync();
    }
}
