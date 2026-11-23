using Factory.Web.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Factory.Tests;

/// <summary>The tray app stops Hatchet by setting a named Windows event.</summary>
public sealed class StopSignalTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "hatchet-stop-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _eventName = $@"Local\Hatchet.Tests.Stop.{Guid.NewGuid():N}";

    public void Dispose()
    {
        try { Directory.Delete(_data, recursive: true); } catch (IOException) { }
    }

    private WebApplicationFactory<Program> Factory(string? eventName) => new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
    {
        host.UseSetting("Hatchet:DataDirectory", _data);
        if (eventName is not null) host.UseSetting(StopSignal.SettingName, eventName);
    });

    private static Task Stopping(WebApplicationFactory<Program> factory)
    {
        var stopping = new TaskCompletionSource();
        factory.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.Register(() => stopping.TrySetResult());
        return stopping.Task;
    }

    [Fact]
    public async Task Setting_the_event_stops_hatchet()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var signal = new EventWaitHandle(false, EventResetMode.ManualReset, _eventName);
        await using var factory = Factory(_eventName);
        await factory.CreateClient().GetAsync("/");
        var stopping = Stopping(factory);

        signal.Set();

        await stopping.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task An_event_set_before_hatchet_started_still_stops_it()
    {
        if (!OperatingSystem.IsWindows()) return;

        // The test host can't start an app that stops itself while starting, so this drives the service directly.
        using var signal = new EventWaitHandle(true, EventResetMode.ManualReset, _eventName);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection([new(StopSignal.SettingName, _eventName)]).Build();
        var lifetime = new FakeLifetime();
        using var service = new StopSignal(configuration, lifetime, NullLogger<StopSignal>.Instance);

        await service.StartAsync(CancellationToken.None);

        await lifetime.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public TaskCompletionSource Stopped { get; } = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => Stopped.TrySetResult();
    }

    [Fact]
    public async Task Without_the_setting_nothing_listens()
    {
        await using var factory = Factory(null);
        var stopping = Stopping(factory);
        await factory.CreateClient().GetAsync("/");

        await Task.Delay(500);
        Assert.False(stopping.IsCompleted);
    }
}
