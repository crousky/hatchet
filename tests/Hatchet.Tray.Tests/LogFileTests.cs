namespace Hatchet.Tray.Tests;

public sealed class LogFileTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("hatchet-tray-tests-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return new StreamReader(stream).ReadToEnd();
    }

    [Fact]
    public void A_new_start_keeps_the_last_log_as_the_previous_one()
    {
        using var log = new LogFile(Path.Combine(_root, "logs", "hatchet.log"));
        log.Rotate();
        log.Write("first run");
        log.Rotate();
        log.Write("second run");

        Assert.Equal(Path.Combine(_root, "logs", "hatchet.previous.log"), log.PreviousPath);
        Assert.Contains("first run", Read(log.PreviousPath));
        Assert.DoesNotContain("first run", Read(log.Path));
        Assert.Contains("second run", Read(log.Path));
    }

    [Fact]
    public void A_log_past_the_limit_rolls_over()
    {
        using var log = new LogFile(Path.Combine(_root, "hatchet.log"), maxBytes: 100);
        log.Write(new string('a', 120));
        log.Write("after");

        Assert.Contains(new string('a', 120), Read(log.PreviousPath));
        Assert.Equal("after", Read(log.Path).Trim());
    }

    [Fact]
    public void A_log_another_program_holds_open_keeps_being_written_instead_of_throwing()
    {
        using var log = new LogFile(Path.Combine(_root, "hatchet.log"), maxBytes: 100);
        log.Write("first");
        // Like Get-Content -Wait: shares reading and writing, but not deleting, so the file can't be moved.
        using (new FileStream(log.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            log.Write(new string('a', 120));
            log.Rotate();
            log.Write("still here");
        }

        Assert.Contains("still here", Read(log.Path));
        Assert.False(File.Exists(log.PreviousPath));

        // Once it's let go, the next roll-over goes ahead.
        log.Write(new string('b', 120));
        log.Write("after");
        Assert.Contains("still here", Read(log.PreviousPath));
        Assert.Equal("after", Read(log.Path).Trim());
    }

    [Fact]
    public void A_log_that_cant_be_created_drops_lines_without_throwing()
    {
        File.WriteAllText(Path.Combine(_root, "not-a-folder"), "");
        using var log = new LogFile(Path.Combine(_root, "not-a-folder", "hatchet.log"));

        log.Rotate();
        log.Write("dropped");
        log.Note("dropped too");
    }

    [Fact]
    public void Notes_are_marked_as_the_trays()
    {
        using var log = new LogFile(Path.Combine(_root, "hatchet.log"));
        log.Note("Starting");
        Assert.StartsWith("[tray ", Read(log.Path));
        Assert.Contains("] Starting", Read(log.Path));
    }
}
