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

    [Fact]
    public void ReadFiles_does_not_return_an_ignored_files_content()
    {
        _repo.Write("samples/real.csv", "name,dob");
        _repo.Write("a.txt", "plain content");
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "samples/real.csv", "not-there.csv" };

        var files = RepositoryScanner.ReadFiles(_repo.Path, ["samples/real.csv", "a.txt", "not-there.csv"], 1000, 10000, ignored);

        Assert.Equal((RepositoryScanner.IgnoredNotice, true), (files[0].Content, files[0].Withheld));
        Assert.Equal(("plain content", false), (files[1].Content, files[1].Withheld));
        Assert.Null(files[2].Content); // Nothing to withhold: the agent is told it doesn't exist yet.
        Assert.DoesNotContain("name,dob", RepositoryScanner.Render(files));
    }

    [Theory]
    [InlineData("src/Api/local.settings.json")]
    [InlineData("infra/.env.wizard")]
    [InlineData("deploy/site.publishsettings")]
    public void ReadFiles_does_not_return_local_settings_and_env_files(string path)
    {
        _repo.Write(path, "KEY=hunter2");

        var file = RepositoryScanner.ReadFiles(_repo.Path, [path], 1000, 10000).Single();

        Assert.Equal((RepositoryScanner.SecretNotice, true), (file.Content, file.Withheld));
    }

    [Theory]
    [InlineData("src/Api/local.settings.json.example")]
    [InlineData("src/Web/appsettings.Development.json")]
    [InlineData(".env.example")]
    public void ReadFiles_still_reads_examples_and_shared_settings(string path)
    {
        _repo.Write(path, "KEY=");

        var file = RepositoryScanner.ReadFiles(_repo.Path, [path], 1000, 10000).Single();

        Assert.Equal(("KEY=", false), (file.Content, file.Withheld));
    }
}
