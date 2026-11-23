using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Hatchet.Tray.Tests;

/// <summary>Stands Windows PowerShell in for the server, which is always there on Windows.</summary>
public sealed class ServerProcessTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("hatchet-tray-tests-").FullName;
    private readonly LogFile _log;

    public ServerProcessTests() => _log = new LogFile(Path.Combine(_root, "hatchet.log"));

    public void Dispose()
    {
        _log.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static Func<string, ProcessStartInfo> PowerShell(Func<string, string> script) => stopEvent =>
    {
        var info = new ProcessStartInfo("powershell.exe");
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script(stopEvent) }) info.ArgumentList.Add(argument);
        return info;
    };

    private string ReadLog()
    {
        using var stream = new FileStream(_log.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(stream).ReadToEnd();
    }

    private async Task WaitForLogAsync(string text)
    {
        for (var i = 0; i < 300 && !ReadLog().Contains(text); i++) await Task.Delay(100);
        Assert.Contains(text, ReadLog());
    }

    [Fact]
    public async Task A_graceful_stop_sets_the_event_and_the_server_exits_on_its_own()
    {
        using var server = new ServerProcess(PowerShell(name =>
            $"$e = New-Object System.Threading.EventWaitHandle($false, 'ManualReset', '{name}'); Write-Output ('wait' + 'ing'); [void]$e.WaitOne(); Write-Output ('asked ' + 'to stop'); exit 7"), _log);
        var unexpected = false;
        server.ExitedUnexpectedly += _ => unexpected = true;

        server.Start();
        Assert.True(server.IsRunning);
        await WaitForLogAsync("waiting");

        var watch = Stopwatch.StartNew();
        await server.StopAsync(graceful: true, TimeSpan.FromSeconds(30));

        Assert.False(server.IsRunning);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
        Assert.Contains("asked to stop", ReadLog());
        Assert.DoesNotContain("didn't stop within", ReadLog());
        Assert.False(unexpected);
    }

    [Fact]
    public async Task A_server_that_ignores_the_event_is_killed_with_everything_it_started()
    {
        using var server = new ServerProcess(PowerShell(_ =>
            "$child = Start-Process ping.exe -ArgumentList '-n','600','127.0.0.1' -WindowStyle Hidden -PassThru; Write-Output ('child-pid=' + $child.Id); Start-Sleep -Seconds 600"), _log);

        server.Start();
        // The tray's own note quotes the script, so only a line that starts with the marker and a number counts.
        Match? pid = null;
        for (var i = 0; i < 300 && !(pid = Regex.Match(ReadLog(), @"^child-pid=(\d+)", RegexOptions.Multiline)).Success; i++) await Task.Delay(100);
        Assert.True(pid!.Success, ReadLog());
        var childId = int.Parse(pid.Groups[1].Value);
        using var child = Process.GetProcessById(childId);

        await server.StopAsync(graceful: true, TimeSpan.FromSeconds(1));

        Assert.False(server.IsRunning);
        Assert.Contains("didn't stop within 1 seconds", ReadLog());
        Assert.True(child.WaitForExit(10_000), "the server's own child was left running");
    }

    [Fact]
    public async Task A_server_that_exits_by_itself_is_reported()
    {
        using var server = new ServerProcess(PowerShell(_ => "exit 3"), _log);
        var exited = new TaskCompletionSource<int>();
        server.ExitedUnexpectedly += code => exited.TrySetResult(code);

        server.Start();

        Assert.Equal(3, await exited.Task.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.False(server.IsRunning);
        Assert.Contains("exited with code 3", ReadLog());
    }

    [Fact]
    public void A_program_that_isnt_there_fails_to_start_and_leaves_it_stopped()
    {
        using var server = new ServerProcess(_ => new ProcessStartInfo(Path.Combine(_root, "missing.exe")), _log);
        Assert.ThrowsAny<System.ComponentModel.Win32Exception>(server.Start);
        Assert.False(server.IsRunning);
    }
}
