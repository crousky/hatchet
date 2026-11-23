namespace Factory.Web.Services;

/// <summary>
/// Lets the tray app stop Hatchet cleanly. Hatchet runs without a console there, so Ctrl+C can't reach it, and killing
/// the process would skip pausing the runs in flight. The tray names a Windows event in Hatchet:StopEvent and sets it
/// to ask for a normal shutdown. Without the setting, or off Windows, this does nothing.
/// </summary>
public sealed class StopSignal(IConfiguration configuration, IHostApplicationLifetime lifetime, ILogger<StopSignal> logger) : BackgroundService
{
    public const string SettingName = "Hatchet:StopEvent";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var name = configuration[SettingName];
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!OperatingSystem.IsWindows())
        {
            logger.LogWarning("{Setting} is only supported on Windows, so it is ignored", SettingName);
            return;
        }

        // Opens the tray's event, or makes it if the tray hasn't yet. An event set before Hatchet got here stays set,
        // so a stop asked for during startup still happens.
        using var signal = new EventWaitHandle(false, EventResetMode.ManualReset, name);
        if (await WaitAsync(signal, stoppingToken))
        {
            logger.LogInformation("Stopping, as the tray app asked");
            lifetime.StopApplication();
        }
    }

    private static async Task<bool> WaitAsync(WaitHandle handle, CancellationToken cancellationToken)
    {
        var signalled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = ThreadPool.RegisterWaitForSingleObject(handle, (_, _) => signalled.TrySetResult(true), null, Timeout.Infinite, executeOnlyOnce: true);
        try
        {
            await using (cancellationToken.Register(() => signalled.TrySetResult(false)))
                return await signalled.Task;
        }
        finally
        {
            registration.Unregister(null);
        }
    }
}
