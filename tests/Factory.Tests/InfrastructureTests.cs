using System.Diagnostics;
using Factory.Engine.Verification;
using Factory.Engine.Workspace;

namespace Factory.Tests;

public class ShellCommandRunnerTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly ShellCommandRunner _runner = new();

    public void Dispose() => _dir.Dispose();

    private Task<CommandResult> Run(string command, int timeoutSeconds = 30, Action<string>? onLine = null, CancellationToken ct = default) =>
        _runner.RunAsync(command, _dir.Path, TimeSpan.FromSeconds(timeoutSeconds), onLine, ct);

    [Fact]
    public async Task Captures_stdout_stderr_and_streams_lines()
    {
        var lines = new List<string>();

        var result = await Run("echo out&& echo err 1>&2", onLine: lines.Add);

        Assert.True(result.Success);
        Assert.Contains("out", result.Output);
        Assert.Contains("err", result.Output);
        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public async Task Reports_failure_exit_code()
    {
        var result = await Run("exit 7");

        Assert.False(result.Success);
        Assert.Equal(7, result.ExitCode);
    }

    [Fact]
    public async Task And_chain_stops_at_the_first_failure()
    {
        var result = await Run("exit 3 && echo never");

        Assert.Equal(3, result.ExitCode);
        Assert.DoesNotContain("never", result.Output);
    }

    [Fact]
    public async Task Runs_in_the_working_directory()
    {
        _dir.Write("marker.txt", "x");

        var result = await Run(OperatingSystem.IsWindows() ? "if exist marker.txt (exit 0) else (exit 1)" : "test -f marker.txt");

        Assert.True(result.Success);
    }

    [Fact]
    public async Task Timeout_kills_the_process_and_is_a_failure()
    {
        var stopwatch = Stopwatch.StartNew();

        var result = await Run(OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1" : "sleep 30", timeoutSeconds: 1);

        Assert.True(result.TimedOut);
        Assert.False(result.Success);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task Cancellation_kills_the_process_and_throws()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Run(OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1" : "sleep 30", ct: cts.Token));
    }
}

public class GitWorkspaceTests : IAsyncLifetime
{
    private readonly TempDirectory _repo = new();
    private readonly GitWorkspace _git;

    public GitWorkspaceTests() => _git = new GitWorkspace(_repo.Path);

    // Async setup: blocking here starves the thread pool that other test classes' process I/O runs on.
    public Task InitializeAsync() => _git.InitializeAsync();

    public Task DisposeAsync()
    {
        _repo.Dispose();
        return Task.CompletedTask;
    }

    private async Task<string> SeedAsync()
    {
        await _git.EnsureInitialCommitAsync();
        return await _git.GetCurrentBranchAsync();
    }

    [Fact]
    public async Task Probe_reports_repository_state()
    {
        Assert.False((await GitWorkspace.ProbeAsync(_repo.File("nope"))).Exists);

        var empty = await GitWorkspace.ProbeAsync(_repo.Path);
        Assert.True(empty.IsGitRepository);
        Assert.False(empty.HasCommits);

        await SeedAsync();
        _repo.Write("dirty.txt", "x");
        var dirty = await GitWorkspace.ProbeAsync(_repo.Path);
        Assert.True(dirty.HasCommits);
        Assert.False(dirty.IsClean);
    }

    [Fact]
    public async Task Probe_recognizes_a_folder_that_is_not_a_repository()
    {
        using var plain = new TempDirectory();

        var probe = await GitWorkspace.ProbeAsync(plain.Path);

        Assert.True(probe.Exists);
        Assert.False(probe.IsGitRepository);
    }

    [Fact]
    public async Task Stage_diff_and_commit_include_new_files()
    {
        await SeedAsync();
        _repo.Write("a.txt", "hello\n");

        await _git.StageAllAsync();
        var diff = await _git.GetStagedDiffAsync();
        var sha = await _git.CommitStagedAsync("feat: a");

        Assert.Contains("+hello", diff);
        Assert.Equal(await _git.GetHeadShaAsync(), sha);
        Assert.True(await _git.IsCleanAsync());
    }

    [Fact]
    public async Task Build_artifacts_are_not_staged_even_without_a_gitignore()
    {
        await SeedAsync();
        _repo.Write("src/a.cs", "code");
        _repo.Write("src/obj/project.assets.json", "{}");
        _repo.Write("src/bin/Debug/net9.0/a.dll", "binary");
        _repo.Write("node_modules/x/index.js", "dep");

        await _git.StageAllAsync();

        Assert.Equal(["src/a.cs"], await _git.GetStagedFilesAsync());
    }

    [Fact]
    public async Task Build_artifacts_alone_do_not_make_the_tree_dirty()
    {
        await SeedAsync();
        _repo.Write("tracked/a.cs", "code");
        await _git.StageAllAsync();
        await _git.CommitStagedAsync("base");

        _repo.Write("tracked/obj/project.assets.json", "{}");
        _repo.Write("tracked/bin/Debug/net9.0/a.dll", "binary");
        _repo.Write("untracked/obj/project.assets.json", "{}");
        _repo.Write("untracked/bin/Release/a.dll", "binary");
        Assert.True(await _git.IsCleanAsync());

        _repo.Write("untracked/real.cs", "users work");
        Assert.False(await _git.IsCleanAsync());
    }

    [Fact]
    public async Task Rollback_discards_changes_and_new_files()
    {
        await SeedAsync();
        _repo.Write("kept.txt", "v1");
        await _git.StageAllAsync();
        await _git.CommitStagedAsync("base");
        await _git.EnsureBranchAsync("factory/run-1");
        _repo.Write("kept.txt", "v2");
        _repo.Write("new/file.txt", "x");

        await _git.RollbackAsync("factory/run-1");

        Assert.Equal("v1", File.ReadAllText(_repo.File("kept.txt")));
        Assert.False(File.Exists(_repo.File("new/file.txt")));
        Assert.True(await _git.IsCleanAsync());
    }

    [Fact]
    public async Task Rollback_refuses_to_touch_any_other_branch()
    {
        var main = await SeedAsync();
        _repo.Write("users-work.txt", "precious");

        await Assert.ThrowsAsync<GitException>(() => _git.RollbackAsync("factory/run-1"));

        Assert.Equal(main, await _git.GetCurrentBranchAsync());
        Assert.True(File.Exists(_repo.File("users-work.txt")));
    }

    [Fact]
    public async Task EnsureBranch_creates_then_reuses()
    {
        var main = await SeedAsync();

        await _git.EnsureBranchAsync("factory/run-1");
        Assert.Equal("factory/run-1", await _git.GetCurrentBranchAsync());

        await _git.CheckoutAsync(main);
        await _git.EnsureBranchAsync("factory/run-1");
        Assert.Equal("factory/run-1", await _git.GetCurrentBranchAsync());
    }

    [Fact]
    public async Task Merge_conflict_is_aborted_and_restores_the_branch()
    {
        var main = await SeedAsync();
        _repo.Write("a.txt", "base\n");
        await _git.StageAllAsync();
        await _git.CommitStagedAsync("base");

        await _git.EnsureBranchAsync("factory/run-1");
        _repo.Write("a.txt", "factory\n");
        await _git.StageAllAsync();
        await _git.CommitStagedAsync("factory change");

        await _git.CheckoutAsync(main);
        _repo.Write("a.txt", "user\n");
        await _git.StageAllAsync();
        await _git.CommitStagedAsync("user change");
        await _git.CheckoutAsync("factory/run-1");

        await Assert.ThrowsAsync<GitException>(() => _git.MergeIntoAsync(main, "factory/run-1", "merge"));

        Assert.Equal("factory/run-1", await _git.GetCurrentBranchAsync());
        Assert.True(await _git.IsCleanAsync());
    }

    [Fact]
    public async Task Merge_brings_the_branch_into_base()
    {
        var main = await SeedAsync();
        await _git.EnsureBranchAsync("factory/run-1");
        _repo.Write("a.txt", "x");
        await _git.StageAllAsync();
        await _git.CommitStagedAsync("feature");

        await _git.MergeIntoAsync(main, "factory/run-1", "merge");

        Assert.Equal(main, await _git.GetCurrentBranchAsync());
        Assert.True(File.Exists(_repo.File("a.txt")));
    }
}

public class RepositoryScannerTests : IDisposable
{
    private readonly TempDirectory _repo = new();

    public void Dispose() => _repo.Dispose();

    [Fact]
    public void Map_lists_files_and_skips_ignored_directories()
    {
        _repo.Write("README.md", "x");
        _repo.Write("src/a.cs", "x");
        _repo.Write("bin/out.dll", "x");
        _repo.Write("src/obj/x.json", "x");
        _repo.Write("node_modules/p/i.js", "x");
        _repo.Write(".git/config", "x");

        var map = RepositoryScanner.BuildRepoMap(_repo.Path);

        Assert.Contains("README.md", map);
        Assert.Contains("src/a.cs", map);
        Assert.DoesNotContain("out.dll", map);
        Assert.DoesNotContain("x.json", map);
        Assert.DoesNotContain("i.js", map);
        Assert.DoesNotContain("config", map);
    }

    [Fact]
    public void ReadFiles_respects_budgets_and_reports_missing_files()
    {
        _repo.Write("a.txt", new string('a', 100));
        _repo.Write("b.txt", new string('b', 100));

        var files = RepositoryScanner.ReadFiles(_repo.Path, ["a.txt", "b.txt", "missing.txt", "../secret.txt"], maxFileChars: 60, maxTotalChars: 80);

        Assert.Equal(60, files[0].Content!.Length);
        Assert.True(files[0].Truncated);
        Assert.Equal(20, files[1].Content!.Length);
        Assert.Null(files[2].Content);
        Assert.Null(files[3].Content);
    }
}
