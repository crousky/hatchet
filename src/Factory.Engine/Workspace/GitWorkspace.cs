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
            var (inside, _) = await workspace.TryReadAsync(ct, "rev-parse", "--is-inside-work-tree");
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
        (await TryReadAsync(ct, "rev-parse", "--verify", "--quiet", "HEAD")).Success;

    public async Task<string> GetCurrentBranchAsync(CancellationToken ct = default) =>
        ValidateRefName((await ReadAsync(ct, "symbolic-ref", "--short", "-q", "HEAD")).Trim());

    public async Task<string> GetHeadShaAsync(CancellationToken ct = default) =>
        (await ReadAsync(ct, "rev-parse", "HEAD")).Trim();

    /// <summary>Build output doesn't count, so a verified run in a repo with no .gitignore doesn't block the next one.</summary>
    public async Task<bool> IsCleanAsync(CancellationToken ct = default)
    {
        string[] args = ["status", "--porcelain", "--", ".", .. ArtifactExclusions];
        return (await ReadAsync(ct, args)).Trim().Length == 0;
    }

    public async Task<bool> HasRemoteAsync(CancellationToken ct = default) =>
        (await ReadAsync(ct, "remote")).Trim().Length > 0;

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
        ValidateRefName(branch);
        if (await GetCurrentBranchOrNullAsync(ct) == branch) return;

        if (await BranchExistsAsync(branch, ct)) await GitAsync(ct, "checkout", branch, "--");
        else await GitAsync(ct, "checkout", "-b", branch);
    }

    private async Task<bool> BranchExistsAsync(string branch, CancellationToken ct) =>
        (await TryReadAsync(ct, "rev-parse", "--verify", "--quiet", $"refs/heads/{ValidateRefName(branch)}")).Success;

    public async Task CheckoutAsync(string branch, CancellationToken ct = default) =>
        await GitAsync(ct, "checkout", ValidateRefName(branch), "--");

    public async Task StageAllAsync(CancellationToken ct = default)
    {
        string[] args = ["add", "-A", "--", ".", .. ArtifactExclusions];
        await GitAsync(ct, args);
        await UnstageSecretsAsync(ct);
    }

    /// <summary>Newly added files that look like secrets are unstaged (and left on disk) so they are never committed.</summary>
    private async Task UnstageSecretsAsync(CancellationToken ct)
    {
        var added = await GitAsync(ct, "diff", "--cached", "--name-only", "--diff-filter=A", "-z", "--no-ext-diff", "--no-textconv");
        var secrets = added.Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(FilePatcher.IsSecretPath).ToList();
        if (secrets.Count == 0) return;
        await GitAsync(ct, ["reset", "-q", "--", .. secrets.Select(p => ":(literal)" + p)]);
    }

    /// <summary>Refuses a name that git would read as an option or that holds whitespace or control characters.</summary>
    private static string ValidateRefName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.StartsWith('-') || name.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new GitException($"Refusing unsafe branch or revision name '{name}'.");
        return name;
    }

    public async Task<string> GetStagedDiffAsync(CancellationToken ct = default) =>
        await ReadAsync(ct, "diff", "--no-ext-diff", "--no-textconv", "--cached", "HEAD");

    public async Task<string> GetRangeDiffAsync(string fromCommit, string toCommit, CancellationToken ct = default) =>
        await ReadAsync(ct, "diff", "--no-ext-diff", "--no-textconv", $"{ValidateRefName(fromCommit)}..{ValidateRefName(toCommit)}");

    public async Task<IReadOnlyList<string>> GetStagedFilesAsync(CancellationToken ct = default) =>
        (await ReadAsync(ct, "diff", "--no-ext-diff", "--no-textconv", "--cached", "--name-only", "HEAD"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public async Task<string> CommitStagedAsync(string message, CancellationToken ct = default)
    {
        await CommitInternalAsync(ct, "-m", message);
        return await GetHeadShaAsync(ct);
    }

    /// <summary>Commits only the given files, leaving any other changes, staged or not, as they are.</summary>
    public async Task<string> CommitFilesAsync(string message, IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        await GitAsync(ct, ["add", "--", .. paths]);
        await CommitInternalAsync(ct, ["-m", message, "--only", "--", .. paths]);
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

    /// <summary>The checked-out branch, or "HEAD" when detached.</summary>
    public async Task<string> GetBranchLabelAsync(CancellationToken ct = default) => await GetCurrentBranchOrNullAsync(ct) ?? "HEAD";

    /// <summary>
    /// Checks a commit out into a new folder of its own, detached, so something can build and run it without touching
    /// this working tree. The folder shares the repository's history and takes no branch.
    /// </summary>
    public async Task AddDetachedWorktreeAsync(string path, string commit, CancellationToken ct = default) =>
        await GitAsync(ct, "worktree", "add", "--detach", "--force", path, commit);

    /// <summary>Removes a folder made by <see cref="AddDetachedWorktreeAsync"/>, with whatever it built. Nothing is lost that a commit holds.</summary>
    public async Task RemoveWorktreeAsync(string path, CancellationToken ct = default)
    {
        await TryGitAsync(ct, "worktree", "remove", "--force", path);
        await TryGitAsync(ct, "worktree", "prune");
    }

    /// <summary>Uncommitted changes as <c>git status --porcelain</c> lines, leaving out build output.</summary>
    public async Task<IReadOnlyList<string>> GetUncommittedChangesAsync(CancellationToken ct = default)
    {
        string[] args = ["status", "--porcelain", "--", ".", .. ArtifactExclusions];
        return (await ReadAsync(ct, args)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>The latest commits on a revision, newest first, as "shortsha subject".</summary>
    public async Task<IReadOnlyList<string>> GetRecentCommitsAsync(string revision, int count, CancellationToken ct = default)
    {
        var (success, output) = await TryReadAsync(ct, "log", $"-{count}", "--format=%h %s", "--no-ext-diff", "--no-textconv", ValidateRefName(revision), "--");
        return success ? output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
    }

    /// <summary>Commits on <paramref name="branch"/> that <paramref name="baseRevision"/> doesn't have. Zero if either is missing.</summary>
    public async Task<int> CountCommitsAheadAsync(string baseRevision, string branch, CancellationToken ct = default)
    {
        var (success, output) = await TryReadAsync(ct, "rev-list", "--count", $"{ValidateRefName(baseRevision)}..{ValidateRefName(branch)}");
        return success && int.TryParse(output.Trim(), out var count) ? count : 0;
    }

    /// <summary>Merges into the base branch. On conflict the merge is aborted and the original branch restored.</summary>
    public async Task<string> MergeIntoAsync(string baseBranch, string branch, string message, CancellationToken ct = default)
    {
        ValidateRefName(baseBranch);
        ValidateRefName(branch);
        await GitAsync(ct, "checkout", baseBranch, "--");
        var (merged, output) = await TryGitWithIdentityAsync(ct, "merge", "--no-ff", "-m", message, branch);
        if (merged) return await GetHeadShaAsync(ct);

        await TryGitAsync(ct, "merge", "--abort");
        await GitAsync(ct, "checkout", branch, "--");
        throw new GitException($"The merge into '{baseBranch}' did not complete and was aborted:\n{output}");
    }

    private async Task<string?> GetCurrentBranchOrNullAsync(CancellationToken ct)
    {
        var (success, output) = await TryReadAsync(ct, "symbolic-ref", "--short", "-q", "HEAD");
        return success ? ValidateRefName(output.Trim()) : null;
    }

    private async Task CommitInternalAsync(CancellationToken ct, params string[] commitArgs)
    {
        var (success, output) = await TryGitWithIdentityAsync(ct, ["commit", .. commitArgs]);
        if (!success) throw new GitException($"git commit failed: {output}");
    }

    private async Task<(bool Success, string Output)> TryGitWithIdentityAsync(CancellationToken ct, params string[] args)
    {
        var (hasIdentity, _) = await TryReadAsync(ct, "config", "user.email");
        string[] withIdentity = hasIdentity ? args : ["-c", "user.name=Hatchet", "-c", "user.email=hatchet@localhost", .. args];
        return await TryGitAsync(ct, withIdentity);
    }

    private async Task<string> GitAsync(CancellationToken ct, params string[] args) => await RunAsync(false, ct, args);

    /// <summary>For read-only queries: hooks are ignored and the call gets a shorter timeout.</summary>
    private async Task<string> ReadAsync(CancellationToken ct, params string[] args) => await RunAsync(true, ct, args);

    private async Task<string> RunAsync(bool readOnly, CancellationToken ct, string[] args)
    {
        var (success, output) = await TryGitCoreAsync(readOnly, ct, args);
        return success ? output : throw new GitException($"git {string.Join(' ', args)} failed: {output}");
    }

    private Task<(bool Success, string Output)> TryGitAsync(CancellationToken ct, params string[] args) => TryGitCoreAsync(false, ct, args);

    private Task<(bool Success, string Output)> TryReadAsync(CancellationToken ct, params string[] args) => TryGitCoreAsync(true, ct, args);

    private static readonly Lazy<string> EmptyHooksDirectory = new(() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hatchet-no-hooks")).FullName.Replace('\\', '/'));

    private const int MaxOutputChars = 20_000_000;

    private async Task<(bool Success, string Output)> TryGitCoreAsync(bool readOnly, CancellationToken ct, string[] args)
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
        // The repository may be untrusted: neutralise config that would run programs from it.
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("core.quotepath=off");
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("core.fsmonitor=false");
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("safe.bareRepository=explicit");
        if (readOnly)
        {
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add($"core.hooksPath={EmptyHooksDirectory.Value}");
        }
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";

        var timeout = readOnly ? TimeSpan.FromSeconds(60) : TimeSpan.FromMinutes(10);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        using var process = Process.Start(info) ?? throw new GitException("Could not start git.");
        var stdout = ReadCappedAsync(process.StandardOutput, cts.Token);
        var stderr = process.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            var output = await stdout;
            var error = await stderr;
            return process.ExitCode == 0
                ? (true, output)
                : (false, string.Join('\n', new[] { error, output }.Where(s => s.Length > 0)).Trim());
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            _ = stdout.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            _ = stderr.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            if (ct.IsCancellationRequested) throw;
            throw new GitException($"git {string.Join(' ', args)} timed out after {timeout.TotalSeconds:0} seconds and was stopped.");
        }
    }

    /// <summary>Reads to the end but keeps only the first <see cref="MaxOutputChars"/> characters, still draining the pipe.</summary>
    private static async Task<string> ReadCappedAsync(StreamReader reader, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buffer = new char[8192];
        var truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            if (sb.Length >= MaxOutputChars) { truncated = true; continue; }
            var take = Math.Min(read, MaxOutputChars - sb.Length);
            sb.Append(buffer, 0, take);
            if (take < read) truncated = true;
        }
        if (truncated) sb.Append("\n[output truncated]\n");
        return sb.ToString();
    }
}
