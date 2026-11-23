namespace Hatchet.Tray;

/// <summary>
/// The server's output, with the tray's own notes. Each start begins a new file and keeps the last one as
/// hatchet.previous.log, so the output of a server that crashed survives one restart. A file that grows past
/// the limit is rolled over the same way. Other programs can open it while it is being written.
/// </summary>
internal sealed class LogFile : IDisposable
{
    public const long DefaultMaxBytes = 20 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly long _maxBytes;
    private StreamWriter? _writer;

    public LogFile(string path, long maxBytes = DefaultMaxBytes)
    {
        Path = path;
        _maxBytes = maxBytes;
    }

    public string Path { get; }

    public string PreviousPath => System.IO.Path.ChangeExtension(Path, ".previous.log");

    /// <summary>Starts a new file, keeping the current one as the previous log.</summary>
    public void Rotate()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            if (File.Exists(Path)) File.Move(Path, PreviousPath, overwrite: true);
            _writer = Open();
        }
    }

    public void Write(string line)
    {
        lock (_gate)
        {
            _writer ??= Open();
            _writer.WriteLine(line);
            if (_writer.BaseStream.Length > _maxBytes) Rotate();
        }
    }

    /// <summary>A line from the tray itself, set apart from the server's output.</summary>
    public void Note(string message) => Write($"[tray {DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private StreamWriter Open()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var stream = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        return new StreamWriter(stream) { AutoFlush = true };
    }
}
