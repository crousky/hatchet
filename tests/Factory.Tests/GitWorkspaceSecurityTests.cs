using System.Diagnostics;
using Factory.Engine.Workspace;

namespace Factory.Tests;

public sealed class GitWorkspaceSecurityTests : IDisposable
{
    private readonly TempDirectory _repo = new();
    private readonly GitWorkspace _git;

    public GitWorkspaceSecurityTests()
    {
        _git = new GitWorkspace(_repo.Path);
    }

    public void Dispose() => _repo.Dispose();

    private static void RunGit(string dir, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var p = Process.Start(info)!;
        p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)}: {err}");
    }

    private async Task InitWithCommitAsync()
    {
        await _git.InitializeAsync();
        await _git.EnsureInitialCommitAsync();
    }

    [Fact]
    public async Task Probe_does_not_run_a_repository_configured_fsmonitor()
    {
        await InitWithCommitAsync();
        var marker = _repo.File("fsmonitor-ran.txt").Replace('\\', '/');
        // git runs the hook through sh on every platform, including git for Windows.
        RunGit(_repo.Path, "config", "core.fsmonitor", $"echo x > \"{marker}\"; echo");
        _repo.Write("a.txt", "a");

        var probe = await GitWorkspace.ProbeAsync(_repo.Path);

        Assert.True(probe.IsGitRepository);
        Assert.False(File.Exists(_repo.File("fsmonitor-ran.txt")));
    }

    [Fact]
    public async Task A_head_that_names_an_option_is_refused()
    {
        await InitWithCommitAsync();
        File.WriteAllText(Path.Combine(_repo.Path, ".git", "HEAD"), "ref: refs/heads/-f\n");

        var probe = await GitWorkspace.ProbeAsync(_repo.Path);
        Assert.NotNull(probe.Error);

        await Assert.ThrowsAsync<GitException>(() => _git.GetCurrentBranchAsync());
        await Assert.ThrowsAsync<GitException>(() => _git.CheckoutAsync("-f"));
        await Assert.ThrowsAsync<GitException>(() => _git.EnsureBranchAsync("has space"));
    }

    [Fact]
    public async Task StageAll_leaves_secret_looking_new_files_unstaged()
    {
        await InitWithCommitAsync();
        _repo.Write(".env", "KEY=1");
        _repo.Write(".env.example", "KEY=");
        _repo.Write("src/server.pem", "x");
        _repo.Write("app.txt", "hello");

        await _git.StageAllAsync();

        var staged = await _git.GetStagedFilesAsync();
        Assert.Contains(".env.example", staged);
        Assert.Contains("app.txt", staged);
        Assert.DoesNotContain(".env", staged);
        Assert.DoesNotContain("src/server.pem", staged);
        Assert.True(File.Exists(_repo.File(".env")));
    }

    private async Task CommitAsync(string message)
    {
        await _git.StageAllAsync();
        await _git.CommitStagedAsync(message);
    }

    [Fact]
    public async Task Ignored_paths_are_named_as_they_were_given_and_a_tracked_file_is_never_one()
    {
        await InitWithCommitAsync();
        _repo.Write("tracked/kept.local.json", "{}");
        await CommitAsync("Track a file the ignore rules will match");
        _repo.Write(".gitignore", "samples/\n*.local.json\ninfra/.env.wizard\n");
        await CommitAsync("Add ignore rules");
        _repo.Write("samples/real-child.csv", "name,dob");
        _repo.Write("src/app.local.json", "{}");
        _repo.Write("src/app.json", "{}");

        var ignored = await _git.GetIgnoredAsync(
            ["samples/real-child.csv", @".\src\app.local.json", "src/app.json", "tracked/kept.local.json", "infra/.env.wizard", "missing.txt", "../outside.local.json", "-z"]);

        // The rule decides, so a file that doesn't exist yet is ignored too. A path outside the repository is not asked about.
        Assert.Equal(new[] { "samples/real-child.csv", @".\src\app.local.json", "infra/.env.wizard" }.Order(StringComparer.Ordinal), ignored.Order(StringComparer.Ordinal));
        Assert.Empty(await _git.GetIgnoredAsync([]));
    }

    [Fact]
    public async Task Many_paths_and_odd_names_are_checked_in_one_go()
    {
        await InitWithCommitAsync();
        _repo.Write(".gitignore", "*.secret\n");
        await CommitAsync("Add ignore rules");
        var paths = Enumerable.Range(0, 2500).Select(i => i % 500 == 0 ? $"dir/file{i}.secret" : $"dir/file{i}.txt")
            .Concat(["a b/it's [x] *.secret", "ünï/cödé.secret", "-n.secret", "a b/plain *.txt"]).ToList();

        var ignored = await _git.GetIgnoredAsync(paths);

        Assert.Equal(new[] { "dir/file0.secret", "dir/file500.secret", "dir/file1000.secret", "dir/file1500.secret", "dir/file2000.secret",
            "a b/it's [x] *.secret", "ünï/cödé.secret", "-n.secret" }.Order(StringComparer.Ordinal), ignored.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_path_git_cannot_check_counts_as_ignored_and_does_not_hide_the_rest()
    {
        await InitWithCommitAsync();
        _repo.Write(".gitignore", "*.secret\n");
        await CommitAsync("Add ignore rules");
        // A folder with a repository of its own: git refuses to answer for a path inside it.
        var nested = new GitWorkspace(Path.Combine(_repo.Path, "vendor"));
        Directory.CreateDirectory(nested.RepositoryPath);
        await nested.InitializeAsync();
        await nested.EnsureInitialCommitAsync();
        File.WriteAllText(Path.Combine(nested.RepositoryPath, "lib.txt"), "x");
        RunGit(nested.RepositoryPath, "add", "lib.txt");
        RunGit(nested.RepositoryPath, "-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "lib");
        RunGit(_repo.Path, "add", "vendor");

        var ignored = await _git.GetIgnoredAsync(["a.txt", "vendor/lib.txt", "b.secret"]);

        Assert.Contains("b.secret", ignored);
        Assert.Contains("vendor/lib.txt", ignored);
        Assert.DoesNotContain("a.txt", ignored);
    }

    [Fact]
    public async Task The_repo_map_leaves_out_ignored_files_and_folders()
    {
        await InitWithCommitAsync();
        _repo.Write(".gitignore", "samples/\n*.local.json\n");
        _repo.Write("src/app.cs", "x");
        await CommitAsync("Add ignore rules");
        _repo.Write("samples/real-child.csv", "name,dob");
        _repo.Write("samples/deep/another.csv", "name,dob");
        _repo.Write("src/app.local.json", "{}");
        _repo.Write("src/new.cs", "x");

        var map = RepositoryScanner.BuildRepoMap(_repo.Path, await _git.GetIgnoredEntriesAsync());

        Assert.Contains("src/app.cs", map);
        Assert.Contains("src/new.cs", map); // Untracked but not ignored, so still part of the work.
        Assert.Contains(".gitignore", map);
        Assert.DoesNotContain("samples", map);
        Assert.DoesNotContain("csv", map);
        Assert.DoesNotContain("app.local.json", map);
    }

    [Fact]
    public async Task A_pre_commit_hook_still_runs_on_hatchets_own_commit()
    {
        await InitWithCommitAsync();
        var marker = _repo.File("hook-ran.txt").Replace('\\', '/');
        var hook = Path.Combine(_repo.Path, ".git", "hooks", "pre-commit");
        Directory.CreateDirectory(Path.GetDirectoryName(hook)!);
        File.WriteAllText(hook, $"#!/bin/sh\necho x > \"{marker}\"\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _repo.Write("b.txt", "b");
        await _git.StageAllAsync();

        await _git.CommitStagedAsync("add b");

        Assert.True(File.Exists(_repo.File("hook-ran.txt")));
    }
}
