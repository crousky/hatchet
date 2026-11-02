using System.Diagnostics;
using System.Text;
using Factory.Contracts;
using Factory.Engine.Verification;

namespace Factory.Engine.Workspace;

public sealed class GitException(string message) : Exception(message);

/// <summary>Wraps the git CLI for one target repository.</summary>
public sealed class GitWorkspace(string repositoryPath)
{
    // Build output is excluded when staging, so a repo with no .gitignore doesn't get artifacts committed after a build.
    private static readonly string[] ArtifactExclusions =
    [
        ":(exclude,glob)**/obj/**",
        ":(exclude,glob)**/bin/Debug/**",
        ":(exclude,glob)**/bin/Release/**",
        ":(exclude,glob)**/node_modules/**",
        ":(exclude,glob)**/__pycache__/**",
        ":(exclude,glob)**/.vs/**",
        ":(exclude,glob)**/TestResults/**"
    ];

    public string RepositoryPath { get; } = repositoryPath;

    public static async Task<RepositoryProbe> ProbeAsync(string? path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return new RepositoryProbe(false, false, false, false, null, null, "The folder does not exist.");

        try
        {
            var workspace = new GitWorkspace(path);
            var detected = VerificationDetector.Detect(path);
            var (inside, _) = await workspace.TryGitAsync(ct, "rev-parse", "--is-inside-work-tree");
            if (!inside) return new RepositoryProbe(true, false, false, false, null, detected, null);

            var hasCommits = await workspace.HasCommitsAsync(ct);
            var branch = await workspace.GetCurrentBranchAsync(ct);
            var clean = await workspace.IsCleanAsync(ct);
            return new RepositoryProbe(true, true, hasCommits, clean, branch, detected, null);
        }
        catch (Exception ex) when (ex is GitException or System.ComponentModel.Win32Exception)
        {
            return new RepositoryProbe(true, false, false, false, null, null, ex.Message);
        }
    }

    public async Task InitializeAsync(CancellationToken ct = default) => await GitAsync(ct, "init");

    public async Task<bool> HasCommitsAsync(CancellationToken ct = default) =>
        (await TryGitAsync(ct, "rev-parse", "--verify", "--quiet", "HEAD")).Success;

    public async Task<string> GetCurrentBranchAsync(CancellationToken ct = default) =>
        (await GitAsync(ct, "symbolic-ref", "--short", "-q", "HEAD")).Trim();

    public async Task<string> GetHeadShaAsync(CancellationToken ct = default) =>
        (await GitAsync(ct, "rev-parse", "HEAD")).Trim();

    /// <summary>Build output doesn't count, so a verified run in a repo with no .gitignore doesn't block the next one.</summary>
    public async Task<bool> IsCleanAsync(CancellationToken ct = default)
    {
        string[] args = ["status", "--porcelain", "--", ".", .. ArtifactExclusions];
        return (await GitAsync(ct, args)).Trim().Length == 0;
    }

    public async Task<bool> HasRemoteAsync(CancellationToken ct = default) =>
        (await GitAsync(ct, "remote")).Trim().Length > 0;

    /// <summary>A branch has nothing to base on until the repository has a commit.</summary>
    public async Task EnsureInitialCommitAsync(CancellationToken ct = default)
    {
        if (await HasCommitsAsync(ct)) return;
        await CommitInternalAsync(ct, "--allow-empty", "-m", "chore: initialize repository");
    }

    /// <summary>A repository can outlive Hatchet's database, so a run's natural branch name may already be taken.</summary>
    public async Task<string> GetUnusedBranchNameAsync(string preferred, CancellationToken ct = default)
    {
        var candidate = preferred;
        for (var suffix = 2; await BranchExistsAsync(candidate, ct); suffix++) candidate = $"{preferred}-{suffix}";
        return candidate;
    }

    /// <summary>Checks out the branch, creating it from HEAD on first use.</summary>
    public async Task EnsureBranchAsync(string branch, CancellationToken ct = default)
    {
        if (await GetCurrentBranchOrNullAsync(ct) == branch) return;

        if (await BranchExistsAsync(branch, ct)) await GitAsync(ct, "checkout", branch);
        else await GitAsync(ct, "checkout", "-b", branch);
    }

    private async Task<bool> BranchExistsAsync(string branch, CancellationToken ct) =>
        (await TryGitAsync(ct, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}")).Success;

    public async Task CheckoutAsync(string branch, CancellationToken ct = default) =>
        await GitAsync(ct, "checkout", branch);

    public async Task StageAllAsync(CancellationToken ct = default)
    {
        string[] args = ["add", "-A", "--", ".", .. ArtifactExclusions];
        await GitAsync(ct, args);
    }

    public async Task<string> GetStagedDiffAsync(CancellationToken ct = default) =>
        await GitAsync(ct, "diff", "--cached", "HEAD");

    public async Task<string> GetRangeDiffAsync(string fromCommit, string toCommit, CancellationToken ct = default) =>
        await GitAsync(ct, "diff", $"{fromCommit}..{toCommit}");

    public async Task<IReadOnlyList<string>> GetStagedFilesAsync(CancellationToken ct = default) =>
        (await GitAsync(ct, "diff", "--cached", "--name-only", "HEAD"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public async Task<string> CommitStagedAsync(string message, CancellationToken ct = default)
    {
        await CommitInternalAsync(ct, "-m", message);
        return await GetHeadShaAsync(ct);
    }

    /// <summary>
    /// Discards uncommitted changes. The branch check guards the destructive reset and clean, so a
    /// repository that has been switched by hand never loses work on another branch.
    /// </summary>
    public async Task RollbackAsync(string expectedBranch, CancellationToken ct = default)
    {
        var current = await GetCurrentBranchOrNullAsync(ct);
        if (current != expectedBranch)
            throw new GitException($"Refusing to roll back: the repository is on '{current}', expected '{expectedBranch}'.");

        await GitAsync(ct, "reset", "--hard", "HEAD");
        await GitAsync(ct, "clean", "-fd");
    }

    /// <summary>Merges into the base branch. On conflict the merge is aborted and the original branch restored.</summary>
    public async Task<string> MergeIntoAsync(string baseBranch, string branch, string message, CancellationToken ct = default)
    {
        await GitAsync(ct, "checkout", baseBranch);
        var (merged, output) = await TryGitWithIdentityAsync(ct, "merge", "--no-ff", "-m", message, branch);
        if (merged) return await GetHeadShaAsync(ct);

        await TryGitAsync(ct, "merge", "--abort");
        await GitAsync(ct, "checkout", branch);
        throw new GitException($"The merge into '{baseBranch}' did not complete and was aborted:\n{output}");
    }

    private async Task<string?> GetCurrentBranchOrNullAsync(CancellationToken ct)
    {
        var (success, output) = await TryGitAsync(ct, "symbolic-ref", "--short", "-q", "HEAD");
        return success ? output.Trim() : null;
    }

    private async Task CommitInternalAsync(CancellationToken ct, params string[] commitArgs)
    {
        var (success, output) = await TryGitWithIdentityAsync(ct, ["commit", .. commitArgs]);
        if (!success) throw new GitException($"git commit failed: {output}");
    }

    private async Task<(bool Success, string Output)> TryGitWithIdentityAsync(CancellationToken ct, params string[] args)
    {
        var (hasIdentity, _) = await TryGitAsync(ct, "config", "user.email");
        string[] withIdentity = hasIdentity ? args : ["-c", "user.name=Hatchet", "-c", "user.email=hatchet@localhost", .. args];
        return await TryGitAsync(ct, withIdentity);
    }

    private async Task<string> GitAsync(CancellationToken ct, params string[] args)
    {
        var (success, output) = await TryGitAsync(ct, args);
        return success ? output : throw new GitException($"git {string.Join(' ', args)} failed: {output}");
    }

    private async Task<(bool Success, string Output)> TryGitAsync(CancellationToken ct, params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = RepositoryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("core.quotepath=off");
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var process = Process.Start(info) ?? throw new GitException("Could not start git.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var output = await stdout;
        return process.ExitCode == 0
            ? (true, output)
            : (false, string.Join('\n', new[] { await stderr, output }.Where(s => s.Length > 0)).Trim());
    }
}
