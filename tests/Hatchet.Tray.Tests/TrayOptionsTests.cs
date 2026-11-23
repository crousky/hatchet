namespace Hatchet.Tray.Tests;

public sealed class TrayOptionsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("hatchet-tray-tests-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Reads_every_option()
    {
        var options = TrayOptions.Parse(["--repo", _root + @"\", "--url", "http://localhost:5311/", "--data-dir", Path.Combine(_root, "data"), "--start-at-login"], _root);

        Assert.Equal(_root, options.RepositoryPath);
        Assert.Equal("http://localhost:5311", options.Url);
        Assert.Equal(Path.Combine(_root, "data"), options.DataDirectory);
        Assert.True(options.EnableStartAtLogin);
        Assert.False(options.Exit);
        Assert.Equal(Path.Combine(_root, "data", "logs", "hatchet.log"), options.LogPath);
        Assert.Equal(@"Local\Hatchet.Tray.5311", options.InstanceName);
    }

    [Fact]
    public void Without_a_repo_it_finds_the_solution_above_its_own_folder()
    {
        File.WriteAllText(Path.Combine(_root, "LocalSoftwareFactory.sln"), "");
        var bin = Directory.CreateDirectory(Path.Combine(_root, "src", "Hatchet.Tray", "bin", "Debug")).FullName;

        var options = TrayOptions.Parse([], bin);

        Assert.Equal(_root, options.RepositoryPath);
        Assert.Equal(TrayOptions.DefaultUrl, options.Url);
        Assert.Null(options.DataDirectory);
    }

    [Fact]
    public void Without_a_repo_or_a_solution_above_it_says_how_to_fix_it()
    {
        var error = Assert.Throws<ArgumentException>(() => TrayOptions.Parse([], _root));
        Assert.Contains("--repo", error.Message);
    }

    [Fact]
    public void Exit_needs_no_repo()
    {
        Assert.True(TrayOptions.Parse(["--exit"], _root).Exit);
    }

    [Theory]
    [InlineData("--nope")]
    [InlineData("--repo")]
    public void Refuses_unknown_options_and_missing_values(string arg)
    {
        Assert.Throws<ArgumentException>(() => TrayOptions.Parse([arg], _root));
    }

    [Theory]
    [InlineData("localhost:5199")]
    [InlineData("ftp://localhost:5199")]
    public void Refuses_an_address_that_is_not_http(string url)
    {
        Assert.Throws<ArgumentException>(() => TrayOptions.Parse(["--repo", _root, "--url", url], _root));
    }

    [Fact]
    public void Start_at_login_keeps_the_options_but_not_the_one_off_flags()
    {
        var options = TrayOptions.Parse(["--repo", _root, "--url", "http://localhost:5311", "--data-dir", _root, "--start-at-login"], _root);
        Assert.Equal(["--repo", _root, "--url", "http://localhost:5311", "--data-dir", _root], options.StartAtLoginArguments());

        var defaults = TrayOptions.Parse(["--repo", _root], _root);
        Assert.Equal(["--repo", _root], defaults.StartAtLoginArguments());
    }

    [Fact]
    public void Runs_the_web_project_in_release_with_the_address_and_stop_event()
    {
        var options = TrayOptions.Parse(["--repo", _root, "--url", "http://localhost:5311", "--data-dir", Path.Combine(_root, "data")], _root);

        var info = options.ServerStartInfo(@"Local\Hatchet.Stop.abc");

        Assert.Equal("dotnet", info.FileName);
        Assert.Equal(_root, info.WorkingDirectory);
        Assert.Equal(
            ["run", "--project", Path.Combine(_root, "src", "Factory.Web", "Factory.Web.csproj"), "-c", "Release", "--no-launch-profile", "--",
             "--Urls=http://localhost:5311", @"--Hatchet:StopEvent=Local\Hatchet.Stop.abc", $"--Hatchet:DataDirectory={Path.Combine(_root, "data")}"],
            info.ArgumentList);
    }
}
