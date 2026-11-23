namespace Hatchet.Tray;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        TrayOptions options;
        try
        {
            options = TrayOptions.Parse(args, AppContext.BaseDirectory);
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(ex.Message, "Hatchet", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }

        if (options.Exit) return ExitRunningTray(options);

        if (options.EnableStartAtLogin)
            new StartAtLogin().Enable(StartAtLogin.CommandLine(Environment.ProcessPath!, options.StartAtLoginArguments()));

        // One tray per address. A second one would only find the first one's Hatchet already running.
        using var instance = new Mutex(initiallyOwned: true, options.InstanceName, out var first);
        if (!first) return 0;

        using var exitRequested = new EventWaitHandle(false, EventResetMode.AutoReset, options.InstanceName + ".Exit");
        using (var app = new TrayApp(options, exitRequested))
            Application.Run(app);

        instance.ReleaseMutex();
        return 0;
    }

    /// <summary>Asks the running tray to exit, which stops its Hatchet, and waits until it has.</summary>
    private static int ExitRunningTray(TrayOptions options)
    {
        if (!EventWaitHandle.TryOpenExisting(options.InstanceName + ".Exit", out var exitRequested)) return 0;
        using (exitRequested) exitRequested.Set();

        using var instance = new Mutex(false, options.InstanceName);
        try
        {
            if (!instance.WaitOne(TimeSpan.FromSeconds(90))) return 1;
        }
        catch (AbandonedMutexException)
        {
            // The tray ended without letting go, which still means it's gone.
        }
        instance.ReleaseMutex();
        return 0;
    }
}
