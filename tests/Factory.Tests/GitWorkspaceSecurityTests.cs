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
