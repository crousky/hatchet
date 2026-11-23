using System.Diagnostics;
using Microsoft.Win32;

namespace Hatchet.Tray;

/// <summary>The tray icon and its menu. It starts Hatchet when it starts, and stops it when it exits.</summary>
internal sealed class TrayApp : ApplicationContext
{
    /// <summary>Hatchet waits up to 30 seconds for its own shutdown, pausing runs, so this gives it a little more.</summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(40);

    private readonly TrayOptions _options;
    private readonly LogFile _log;
    private readonly ServerProcess _server;
    private readonly StartAtLogin _startAtLogin = new();
    private readonly TrayIcons _icons = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly NotifyIcon _tray;
    private readonly ToolStripMenuItem _status, _open, _start, _stop, _restart, _login, _exit;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2000 };
    private readonly SynchronizationContext _ui;
    private readonly RegisteredWaitHandle _exitWait;

    private ServerState _state = ServerState.Stopped;
    private string? _problem;
    private bool _answered, _probing, _busy, _openWhenReady, _exiting;

    public TrayApp(TrayOptions options, WaitHandle exitRequested)
    {
        _options = options;
        _log = new LogFile(options.LogPath);
        _server = new ServerProcess(options.ServerStartInfo, _log);

        var menu = new ContextMenuStrip();
        _status = new ToolStripMenuItem { Enabled = false };
        _open = new ToolStripMenuItem("Open Hatchet", null, (_, _) => Open()) { Font = new Font(menu.Font, FontStyle.Bold) };
        _start = new ToolStripMenuItem("Start", null, (_, _) => StartServer());
        _stop = new ToolStripMenuItem("Stop", null, async (_, _) => await StopServerAsync());
        _restart = new ToolStripMenuItem("Restart (rebuilds)", null, async (_, _) => await RestartAsync());
        var openLog = new ToolStripMenuItem("Open log", null, (_, _) => OpenLog());
        _login = new ToolStripMenuItem("Start at login", null, (_, _) => ToggleStartAtLogin());
        _exit = new ToolStripMenuItem("Exit", null, async (_, _) => await ExitAsync());
        menu.Items.AddRange([_status, _open, new ToolStripSeparator(), _start, _stop, _restart, openLog, new ToolStripSeparator(), _login, _exit]);
        menu.Opening += (_, _) => _login.Checked = _startAtLogin.IsEnabled;

        // The menu is the first control, which installs the Windows Forms context that events are sent back to.
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _tray = new NotifyIcon { ContextMenuStrip = menu, Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Open(); };
        _tray.BalloonTipClicked += (_, _) => { if (_state == ServerState.Failed) OpenLog(); };

        _server.ExitedUnexpectedly += code => _ui.Post(_ => OnServerExited(code), null);
        _exitWait = ThreadPool.RegisterWaitForSingleObject(exitRequested, (_, _) => _ui.Post(async _ => await ExitAsync(), null), null, Timeout.Infinite, executeOnlyOnce: true);
        SystemEvents.SessionEnded += OnSessionEnded;

        _timer.Tick += async (_, _) => await ProbeAsync();
        SetState(ServerState.Stopped);
        _ui.Post(async _ => await StartupAsync(), null);
    }

    private async Task StartupAsync()
    {
        // Something else, such as run.ps1, may already have Hatchet's address. Starting another would only fail.
        if (await AnswersAsync())
        {
            _log.Note($"Hatchet is already running at {_options.Url}, so the tray didn't start it");
            SetState(ServerState.External);
        }
        else
        {
            StartServer();
        }
        _timer.Start();
    }

    private void StartServer()
    {
        if (_busy || _exiting || _server.IsRunning) return;
        try
        {
            _server.Start();
            _answered = false;
            _problem = null;
            SetState(ServerState.Starting);
        }
        catch (Exception ex)
        {
            _log.Note($"Couldn't start: {ex.Message}");
            Fail($"Hatchet couldn't start: {ex.Message}");
        }
    }

    private async Task StopServerAsync()
    {
        if (_busy || !_server.IsRunning) return;
        _busy = true;
        _openWhenReady = false;
        SetState(ServerState.Stopping);
        try
        {
            // Before it answers, it is still building or starting, and there is nothing to shut down cleanly.
            await _server.StopAsync(graceful: _answered, StopTimeout);
        }
        finally
        {
            _busy = false;
            _problem = null;
            SetState(ServerState.Stopped);
        }
    }

    private async Task RestartAsync()
    {
        await StopServerAsync();
        StartServer();
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        UpdateMenu();
        _timer.Stop();
        while (_busy) await Task.Delay(200);
        await StopServerAsync();
        ExitThread();
    }

    private async Task ProbeAsync()
    {
        if (_probing || _exiting) return;
        _probing = true;
        try
        {
            var answers = await AnswersAsync();
            if (_busy || _exiting) return;

            if (_server.IsRunning)
            {
                if (answers && _state != ServerState.Running)
                {
                    _answered = true;
                    SetState(ServerState.Running);
                    if (_openWhenReady)
                    {
                        _openWhenReady = false;
                        OpenBrowser();
                    }
                }
            }
            else if (answers)
            {
                if (_state != ServerState.External) SetState(ServerState.External);
            }
            else if (_state is ServerState.External or ServerState.Running)
            {
                SetState(ServerState.Stopped);
            }
        }
        finally
        {
            _probing = false;
        }
    }

    private async Task<bool> AnswersAsync()
    {
        try
        {
            // Any answer at all means the address is taken, so only a refused or timed-out request counts as down.
            using var response = await _http.GetAsync(_options.Url, HttpCompletionOption.ResponseHeadersRead);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    private void OnServerExited(int code)
    {
        if (_exiting) return;
        _openWhenReady = false;
        Fail(_answered
            ? $"Hatchet stopped unexpectedly (exit code {code})."
            : $"Hatchet didn't start (exit code {code}). The log says why.");
    }

    private void Fail(string problem)
    {
        _problem = problem;
        SetState(ServerState.Failed);
        _tray.ShowBalloonTip(10_000, "Hatchet", problem + " Click to open the log.", ToolTipIcon.Warning);
    }

    private void Open()
    {
        switch (_state)
        {
            case ServerState.Running or ServerState.External:
                OpenBrowser();
                break;
            case ServerState.Starting:
                _openWhenReady = true;
                break;
            case ServerState.Stopped or ServerState.Failed:
                _openWhenReady = true;
                StartServer();
                break;
        }
    }

    private void OpenBrowser() => ShellOpen(_options.Url);

    private void OpenLog()
    {
        var path = File.Exists(_log.Path) ? _log.Path : _log.PreviousPath;
        if (File.Exists(path)) ShellOpen(path);
        else MessageBox.Show("There's no log yet. It starts when the tray first starts Hatchet.", "Hatchet");
    }

    private void ToggleStartAtLogin()
    {
        try
        {
            if (_startAtLogin.IsEnabled) _startAtLogin.Disable();
            else _startAtLogin.Enable(StartAtLogin.CommandLine(Environment.ProcessPath!, _options.StartAtLoginArguments()));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            MessageBox.Show($"Couldn't change start at login: {ex.Message}", "Hatchet", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnSessionEnded(object? sender, SessionEndedEventArgs e)
    {
        // Windows is signing out or shutting down and won't wait long, so Hatchet gets less time than usual.
        _exiting = true;
        _server.StopAsync(graceful: _answered, TimeSpan.FromSeconds(10)).Wait();
    }

    private void SetState(ServerState state)
    {
        _state = state;
        var status = state switch
        {
            ServerState.Starting => "Hatchet is starting…",
            ServerState.Running => $"Hatchet is running at {_options.Url}",
            ServerState.Stopping => "Hatchet is stopping…",
            ServerState.External => "Hatchet is running, started outside the tray",
            ServerState.Failed => _problem ?? "Hatchet stopped unexpectedly.",
            _ => "Hatchet is stopped",
        };
        _status.Text = status;
        _tray.Text = status.Length > 127 ? status[..127] : status;
        _tray.Icon = _icons.For(state);
        UpdateMenu();
    }

    private void UpdateMenu()
    {
        var running = _server.IsRunning;
        _open.Enabled = !_busy && !_exiting;
        _start.Enabled = !_busy && !_exiting && !running && _state != ServerState.External;
        _stop.Enabled = !_busy && !_exiting && running;
        _restart.Enabled = !_busy && !_exiting && running;
        _exit.Enabled = !_exiting;
    }

    private static void ShellOpen(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            MessageBox.Show($"Couldn't open {target}: {ex.Message}", "Hatchet", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.SessionEnded -= OnSessionEnded;
            _exitWait.Unregister(null);
            _timer.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _server.Dispose();
            _log.Dispose();
            _icons.Dispose();
            _http.Dispose();
        }
        base.Dispose(disposing);
    }
}
