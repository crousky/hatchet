using Factory.Engine.Workspace;

namespace Factory.Tests;

public class RepositoryScannerSecretTests : IDisposable
{
    private readonly TempDirectory _repo = new();

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void ReadFiles_does_not_return_secret_file_content()
    {
        _repo.Write(".env", "API_KEY=hunter2");
        _repo.Write("a.txt", "plain content");

        var files = RepositoryScanner.ReadFiles(_repo.Path, [".env", "a.txt"], 1000, 10000);

        Assert.DoesNotContain(files, f => f.Content!.Contains("hunter2"));
        Assert.Equal("[not read: looks like a secrets file]", files[0].Content);
        Assert.Equal("plain content", files[1].Content);
    }
}
