using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Factory.Contracts;
using Factory.Engine.Llm;
using Factory.Engine.Orchestration;
using Factory.Engine.Verification;
using Factory.Engine.Workspace;
using Microsoft.Extensions.Logging;

namespace Factory.Engine.AdHoc;

/// <summary>
/// Runs ad hoc requests: a status report, running the software, a command, a question, or a defect report or change
/// request that becomes a run. They sit beside a
/// repository's runs and never touch its working tree. Commands run in an isolated copy of the latest commit, a git
/// worktree under Hatchet's data folder, so they can't clash with a run that is building the same repository.
/// </summary>
public sealed class AdHocManager(
    IAdHocStore store,
    IRunStore runs,
    ISettingsStore settings,
    ICommandRunner runner,
    IClaudeAsker asker,
    FactoryEventBus bus,
    ILogger<AdHocManager> logger,
    string dataDirectory,
    IRunStarter? runStarter = null)
{
    private const int MaxOutputChars = 400_000;
    private const int MaxQuestionTitle = 120;

    private sealed class Active(RequestKind kind, string repositoryKey)
    {
        public RequestKind Kind { get; } = kind;
        public string RepositoryKey { get; } = repositoryKey;
        public CancellationTokenSource Cancellation { get; } = new();
        public StringBuilder Output { get; } = new();
        public object Gate { get; } = new();
        public bool StopRequested { get; set; }
        public Task Completion { get; set; } = Task.CompletedTask;
        public bool RunsCommands => Kind is RequestKind.RunApp or RequestKind.RunCommand;
    }

    private readonly ConcurrentDictionary<int, Active> _active = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);

    public bool IsActive(int id) => _active.ContainsKey(id);

    /// <summary>What a request has printed so far: live while it runs, as saved once it has finished.</summary>
    public string GetOutput(AdHocRequest request)
    {
        if (!_active.TryGetValue(request.Id, out var active)) return request.Output;
        lock (active.Gate) return active.Output.ToString();
    }

    /// <summary>How Hatchet would start the software in a repository, for showing before someone asks.</summary>
    public static DetectedRunCommand? DetectRunCommand(string repositoryPath) => RunCommandDetector.Detect(repositoryPath);

    public Task<IReadOnlyList<AdHocRequest>> ListAsync(string? repositoryPath = null, CancellationToken ct = default) =>
        store.ListAsync(repositoryPath, ct);

    public Task<AdHocRequest?> GetAsync(int id, CancellationToken ct = default) => store.GetAsync(id, ct);

    /// <summary>Requests that were running when Hatchet last stopped can't still be tracked, so they end here.</summary>
    public async Task ReconcileAsync(CancellationToken ct = default)
    {
        foreach (var listed in (await store.ListAsync(null, ct)).Where(r => r.IsActive))
        {
            // Listed requests leave out their output, so the full one is read before it's saved back.
            if (await store.GetAsync(listed.Id, ct) is not { } request) continue;
            request.Status = RequestStatus.Stopped;
            request.CompletedAt = DateTime.UtcNow;
            request.Error = request.RunsCommands()
                ? "Hatchet restarted while this was running. If the software is still open, close it yourself."
                : "Hatchet restarted while this was running.";
            await store.UpdateAsync(request, ct);
        }
    }

    // ---- Requests ----

    public async Task<AdHocRequest> RequestStatusReportAsync(string repositoryPath, CancellationToken ct = default)
    {
        var repository = await RequireRepositoryAsync(repositoryPath, ct);
        var report = await BuildReportAsync(repository, ct);
        var request = await store.CreateAsync(new AdHocRequest
        {
            RepositoryPath = repository,
            Kind = RequestKind.StatusReport,
            Title = "Status report",
            Status = RequestStatus.Succeeded,
            Result = report,
            CompletedAt = DateTime.UtcNow
        }, ct);
        bus.Publish(new RequestChanged(request.Id));
        return request;
    }

    /// <summary>Runs the latest committed version of the software. <paramref name="command"/> overrides what Hatchet detects.</summary>
    public Task<AdHocRequest> RequestRunAppAsync(string repositoryPath, string? command = null, CancellationToken ct = default) =>
        StartCommandRequestAsync(RequestKind.RunApp, repositoryPath, string.IsNullOrWhiteSpace(command) ? null : command.Trim(), ct);

    public Task<AdHocRequest> RequestCommandAsync(string repositoryPath, string command, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(command)
            ? throw new FactoryValidationException("Say which command to run.")
            : StartCommandRequestAsync(RequestKind.RunCommand, repositoryPath, command.Trim(), ct);

    public async Task<AdHocRequest> AskAsync(string repositoryPath, string question, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(question)) throw new FactoryValidationException("Ask a question.");
        var repository = await RequireRepositoryAsync(repositoryPath, ct);
        var text = question.Trim();
        var request = await store.CreateAsync(new AdHocRequest
        {
            RepositoryPath = repository,
            Kind = RequestKind.Ask,
            Title = text.Length <= MaxQuestionTitle ? text : text[..MaxQuestionTitle] + "…",
            Input = text
        }, ct);

        var active = new Active(RequestKind.Ask, FactoryOrchestrator.RepositoryKey(repository));
        _active[request.Id] = active;
        bus.Publish(new RequestChanged(request.Id));
        active.Completion = Task.Run(() => AnswerAsync(request, active, text));
        return request;
    }

    /// <summary>Starts the same request again, as a new one.</summary>
    public async Task<AdHocRequest> RerunAsync(int id, CancellationToken ct = default)
    {
        var request = await store.GetAsync(id, ct) ?? throw new FactoryValidationException($"There is no request {id}.");
        return request.Kind switch
        {
            RequestKind.StatusReport => await RequestStatusReportAsync(request.RepositoryPath, ct),
            RequestKind.RunApp => await RequestRunAppAsync(request.RepositoryPath, request.Input, ct),
            RequestKind.RunCommand => await RequestCommandAsync(request.RepositoryPath, request.Input, ct),
            RequestKind.Defect or RequestKind.Change => throw new FactoryValidationException(
                $"This {(request.Kind == RequestKind.Defect ? "report" : "request")} already became run #{request.RunId}. File a new one if it still needs doing."),
            _ => await AskAsync(request.RepositoryPath, request.Input, ct)
        };
    }

    /// <summary>Stops a running request. A command's whole process tree is killed.</summary>
    public async Task StopAsync(int id)
    {
        if (!_active.TryGetValue(id, out var active)) throw new FactoryValidationException($"Request {id} isn't running.");
        active.StopRequested = true;
        try
        {
            await active.Cancellation.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            return; // It finished just now.
        }
        await active.Completion.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
    }

    /// <summary>Removes a request. A running one is stopped first. Its isolated copy goes too, once no other request uses it.</summary>
    public async Task DeleteAsync(int id)
    {
        var request = await store.GetAsync(id) ?? throw new FactoryValidationException($"There is no request {id}.");
        if (_active.ContainsKey(id)) await StopAsync(id);
        await store.DeleteAsync(id);
        bus.Publish(new RequestChanged(id));
        DeleteAttachments(id);
        await RemoveWorkspaceIfUnusedAsync(request);
    }

    // ---- Defect reports and change requests ----

    /// <summary>
    /// Files a defect report or a change request, with screenshots if there are any. Claude reads the screenshots and the code,
    /// read-only, and writes a brief the local model can use. That becomes the spec of a new run in the repository, which asks for
    /// plan approval before anything is built. Without screenshots the description goes straight into the spec.
    /// <paramref name="modelChoice"/> puts every agent of the run on one model, and Claude's reading of the screenshots too when it is
    /// a Claude model; null leaves it to Settings.
    /// </summary>
    public async Task<AdHocRequest> RequestReportAsync(RequestKind kind, string repositoryPath, string description, IReadOnlyList<ReportImage>? images = null, string? modelChoice = null, CancellationToken ct = default)
    {
        if (kind is not (RequestKind.Defect or RequestKind.Change)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (string.IsNullOrWhiteSpace(description)) throw new FactoryValidationException(kind == RequestKind.Defect ? "Describe the defect." : "Describe the change you want.");
        var starter = runStarter ?? throw new InvalidOperationException("Reports need a run starter.");
        var prepared = ReportBuilder.Prepare(images ?? []);
        var choice = ModelChoices.Normalize(modelChoice);
        var repository = await RequireRepositoryAsync(repositoryPath, ct);
        // Checked first, so a dirty tree is found now and not after Claude has spent a minute on the screenshots.
        await starter.EnsureCanStartAsync(repository, ct);

        var text = description.Trim();
        var request = await store.CreateAsync(new AdHocRequest
        {
            RepositoryPath = repository,
            Kind = kind,
            Title = ReportBuilder.Title(text),
            Input = text,
            ModelChoice = choice
        }, ct);

        try
        {
            var directory = AttachmentDirectory(request.Id);
            if (prepared.Count > 0) Directory.CreateDirectory(directory);
            foreach (var (fileName, image) in prepared) await File.WriteAllBytesAsync(Path.Combine(directory, fileName), image.Content, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await store.DeleteAsync(request.Id, CancellationToken.None);
            DeleteAttachments(request.Id);
            throw new FactoryValidationException($"Could not save the screenshots: {ex.Message}");
        }

        var active = new Active(kind, FactoryOrchestrator.RepositoryKey(repository));
        _active[request.Id] = active;
        bus.Publish(new RequestChanged(request.Id));
        active.Completion = Task.Run(() => FileReportAsync(request, active, starter, prepared.Count));
        return request;
    }

    /// <summary>
    /// Starts the run for a report that Claude judged to need no change, for when the person disagrees. The run's spec holds
    /// the report and Claude's write-up, so the planner sees both.
    /// </summary>
    public async Task<AdHocRequest> StartRunAnywayAsync(int id, CancellationToken ct = default)
    {
        var starter = runStarter ?? throw new InvalidOperationException("Reports need a run starter.");
        var request = await store.GetAsync(id, ct) ?? throw new FactoryValidationException($"There is no request {id}.");
        if (request.Kind is not (RequestKind.Defect or RequestKind.Change)) throw new FactoryValidationException($"Request {id} isn't a defect report or change request.");
        if (request.RunId is { } existing) throw new FactoryValidationException($"This already became run #{existing}.");
        if (request.IsActive || request.Status != RequestStatus.Succeeded) throw new FactoryValidationException("It hasn't finished being looked at yet.");

        var spec = ReportBuilder.BuildSpec(request.Kind, request.Input, request.Result, GetAttachments(id).Count);
        var run = await starter.StartRunAsync(new NewRunRequest(request.RepositoryPath, spec, ReportBuilder.RunTitle(request.Kind, request.Input), null, RequirePlanApproval: true, request.ModelChoice, PlanReviewByUser: true), ct);
        request.RunId = run.Id;
        request.Result = spec;
        await store.UpdateAsync(request, CancellationToken.None);
        bus.Publish(new RequestChanged(id));
        return request;
    }

    /// <summary>The pictures that came with a report, in the order they were attached.</summary>
    public IReadOnlyList<string> GetAttachments(int requestId)
    {
        var directory = AttachmentDirectory(requestId);
        return Directory.Exists(directory) ? Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal).ToList() : [];
    }

    /// <summary>The full path of one of a report's pictures, or null if it has none by that name. Names with folders in them never match.</summary>
    public string? GetAttachmentPath(int requestId, string fileName) =>
        GetAttachments(requestId).FirstOrDefault(path => Path.GetFileName(path).Equals(fileName, StringComparison.Ordinal));

    private async Task FileReportAsync(AdHocRequest request, Active active, IRunStarter starter, int imageCount)
    {
        var ct = active.Cancellation.Token;
        try
        {
            string? triage = null;
            if (imageCount > 0)
            {
                Append(active, "Claude is looking at the screenshots and the code…");
                var context = $"The project is at {request.RepositoryPath}.";
                triage = await asker.AskAsync(
                    await settings.GetAsync(ct), request.RepositoryPath, context, ReportBuilder.TriageQuestion(request.Kind, request.Input, GetAttachments(request.Id)),
                    text => Append(active, text, newLine: false), ct,
                    new AskOptions(ReportBuilder.TriagePrompt, [AttachmentDirectory(request.Id)], ModelChoices.ClaudeModelOf(request.ModelChoice)));
                if (string.IsNullOrWhiteSpace(triage)) throw new FactoryValidationException("Claude sent back nothing about the screenshots.");
            }

            if (triage is not null)
            {
                var (body, changeNeeded) = ReportBuilder.ParseVerdict(triage);
                triage = body;
                if (!changeNeeded)
                {
                    // Nothing in the project looks to need changing, so a run would only be told to change something anyway. The write-up is kept for the person to judge.
                    request.Result = body;
                    request.Status = RequestStatus.Succeeded;
                    Append(active, "\n\nClaude found nothing in the project to change, so no run was started.");
                    return;
                }
            }

            var spec = ReportBuilder.BuildSpec(request.Kind, request.Input, triage, imageCount);
            ct.ThrowIfCancellationRequested();
            Append(active, "\n\nStarting a run for it…");
            var run = await starter.StartRunAsync(new NewRunRequest(request.RepositoryPath, spec, ReportBuilder.RunTitle(request.Kind, request.Input), null, RequirePlanApproval: true, request.ModelChoice, PlanReviewByUser: true), CancellationToken.None);
            request.RunId = run.Id;
            request.Result = spec;
            request.Status = RequestStatus.Succeeded;
            Append(active, $"\nStarted run #{run.Id}. It will ask you to approve its plan before building anything.");
        }
        catch (OperationCanceledException)
        {
            request.Status = RequestStatus.Stopped;
        }
        catch (Exception ex)
        {
            if (ex is not (FactoryValidationException or LlmException or GitException or IOException))
                logger.LogError(ex, "Report {RequestId} failed", request.Id);
            request.Status = RequestStatus.Failed;
            request.Error = ex.Message;
        }
        finally
        {
            await FinishAsync(request, active);
        }
    }

    private string AttachmentDirectory(int requestId) => Path.Combine(dataDirectory, "attachments", requestId.ToString());

    private void DeleteAttachments(int requestId)
    {
        var directory = AttachmentDirectory(requestId);
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not remove {Directory}", directory);
        }
    }

    // ---- Running software and commands ----

    private async Task<AdHocRequest> StartCommandRequestAsync(RequestKind kind, string repositoryPath, string? command, CancellationToken ct)
    {
        var repository = await RequireRepositoryAsync(repositoryPath, ct);
        var key = FactoryOrchestrator.RepositoryKey(repository);

        AdHocRequest request;
        Active active;
        await _startGate.WaitAsync(ct);
        try
        {
            // One at a time per repository: a running app holds its build output, and a second build would fail on it.
            if (_active.FirstOrDefault(p => p.Value.RunsCommands && p.Value.RepositoryKey.Equals(key, StringComparison.OrdinalIgnoreCase)) is { Value: not null } running)
                throw new FactoryValidationException($"Request #{running.Key} is still running in this repository. Stop it first.");

            request = await store.CreateAsync(new AdHocRequest
            {
                RepositoryPath = repository,
                Kind = kind,
                Title = command ?? "Run the app",
                Input = command ?? ""
            }, ct);
            active = new Active(kind, key);
            _active[request.Id] = active;
        }
        finally
        {
            _startGate.Release();
        }

        bus.Publish(new RequestChanged(request.Id));
        active.Completion = Task.Run(() => RunCommandAsync(request, active, command));
        return request;
    }

    private async Task RunCommandAsync(AdHocRequest request, Active active, string? command)
    {
        var ct = active.Cancellation.Token;
        try
        {
            Append(active, "Checking out the latest commit into its own folder…");
            var (directory, source) = await PrepareWorkspaceAsync(request.RepositoryPath, ct);
            request.WorkDirectory = directory;
            request.Source = source;
            Append(active, $"Using {source}, in {directory}");

            if (command is null)
            {
                var detected = RunCommandDetector.Detect(directory)
                    ?? throw new FactoryValidationException("Hatchet couldn't tell how to start this software from its files. Say which command to run.");
                command = detected.Command;
                Append(active, $"Detected {detected.Description}.");
            }

            request.Command = command;
            await store.UpdateAsync(request, CancellationToken.None);
            bus.Publish(new RequestChanged(request.Id));

            Append(active, $"> {command}");
            var timeout = request.Kind == RequestKind.RunApp ? TimeSpan.FromDays(7) : TimeSpan.FromHours(1);
            var result = await runner.RunAsync(command, directory, timeout, line => Append(active, line), ct);

            request.ExitCode = result.ExitCode;
            if (result.Success)
            {
                request.Status = RequestStatus.Succeeded;
            }
            else
            {
                request.Status = RequestStatus.Failed;
                request.Error = result.TimedOut ? $"Timed out after {timeout.TotalMinutes:0} minutes, so it was stopped." : $"It exited with code {result.ExitCode}.";
            }
        }
        catch (OperationCanceledException)
        {
            request.Status = RequestStatus.Stopped;
        }
        catch (Exception ex)
        {
            if (ex is not (FactoryValidationException or GitException or IOException or UnauthorizedAccessException))
                logger.LogError(ex, "Ad hoc request {RequestId} failed", request.Id);
            request.Status = RequestStatus.Failed;
            request.Error = ex.Message;
        }
        finally
        {
            await FinishAsync(request, active);
        }
    }

    /// <summary>A folder holding the repository's latest commit, made once per commit and reused after that.</summary>
    private async Task<(string Directory, string Source)> PrepareWorkspaceAsync(string repository, CancellationToken ct)
    {
        var git = new GitWorkspace(repository);
        var sha = await git.GetHeadShaAsync(ct);
        var branch = await git.GetBranchLabelAsync(ct);
        var directory = Path.Combine(dataDirectory, "adhoc", KeyHash(repository), sha[..8]);

        // A worktree's .git is a file. Without it the folder is a leftover that git doesn't know.
        if (!File.Exists(Path.Combine(directory, ".git")))
        {
            if (Directory.Exists(directory)) DeleteDirectory(directory);
            Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
            await git.AddDetachedWorktreeAsync(directory, sha, ct);
        }
        return (directory, $"{branch} @ {sha[..8]}");
    }

    private async Task RemoveWorkspaceIfUnusedAsync(AdHocRequest request)
    {
        if (request.WorkDirectory is not { } directory) return;
        try
        {
            if ((await store.ListAsync(request.RepositoryPath)).Any(r => string.Equals(r.WorkDirectory, directory, StringComparison.OrdinalIgnoreCase))) return;
            await new GitWorkspace(request.RepositoryPath).RemoveWorktreeAsync(directory);
            if (Directory.Exists(directory)) DeleteDirectory(directory);
        }
        catch (Exception ex) when (ex is GitException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not remove {Directory}", directory);
        }
    }

    // ---- Questions ----

    private async Task AnswerAsync(AdHocRequest request, Active active, string question)
    {
        var ct = active.Cancellation.Token;
        try
        {
            var report = await BuildReportAsync(request.RepositoryPath, ct);
            var context = $"The project is at {request.RepositoryPath}.\n\nHatchet's current status report for it:\n\n{report}";
            var answer = await asker.AskAsync(await settings.GetAsync(ct), request.RepositoryPath, context, question, text => Append(active, text, newLine: false), ct);
            request.Result = answer;
            request.Status = RequestStatus.Succeeded;
        }
        catch (OperationCanceledException)
        {
            request.Status = RequestStatus.Stopped;
        }
        catch (Exception ex) when (ex is LlmException or GitException or IOException)
        {
            request.Status = RequestStatus.Failed;
            request.Error = ex.Message;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ad hoc question {RequestId} failed", request.Id);
            request.Status = RequestStatus.Failed;
            request.Error = ex.Message;
        }
        finally
        {
            await FinishAsync(request, active);
        }
    }

    // ---- Reports ----

    /// <summary>The status report for a repository, as of now.</summary>
    public async Task<string> BuildReportAsync(string repositoryPath, CancellationToken ct = default)
    {
        var key = FactoryOrchestrator.RepositoryKey(repositoryPath);
        string? branch = null, head = null, problem = null;
        IReadOnlyList<string> changes = [], commits = [];
        try
        {
            var git = new GitWorkspace(repositoryPath);
            branch = await git.GetBranchLabelAsync(ct);
            head = await git.GetHeadShaAsync(ct);
            changes = await git.GetUncommittedChangesAsync(ct);
            commits = await git.GetRecentCommitsAsync("HEAD", 5, ct);
        }
        catch (Exception ex) when (ex is GitException or System.ComponentModel.Win32Exception)
        {
            problem = ex.Message;
        }

        var repoRuns = (await runs.ListRunsAsync(ct))
            .Where(r => FactoryOrchestrator.RepositoryKey(r.Task.RepositoryPath).Equals(key, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var subtasks = (await runs.GetSubtasksForRunsAsync(repoRuns.Select(r => r.Id).ToList(), ct)).ToLookup(s => s.RunId);

        var reports = new List<RunReportInput>();
        foreach (var run in repoRuns)
        {
            var open = !RunStateMachine.IsTerminal(run.Status);
            var runSubtasks = subtasks[run.Id].OrderBy(s => s.Order).ToList();
            reports.Add(new RunReportInput(
                run, runSubtasks,
                open ? (await runs.GetRecentEventsAsync(run.Id, 6, ct)).Select(l => $"{l.CreatedAt:HH:mm:ss} {l.Message}").ToList() : [],
                open && RunStateMachine.IsUnderway(run.Status) ? WorkEstimator.Estimate(runSubtasks, await runs.GetWorkBySubtaskAsync(run.Id, ct)) : null));
        }

        var requests = (await store.ListAsync(repositoryPath, ct)).ToList();
        return StatusReportBuilder.Build(new StatusReportInput(repositoryPath, DateTime.UtcNow, branch, head, changes, commits, problem, reports, requests));
    }

    // ---- Helpers ----

    /// <summary>Checks the folder is a Git repository with a commit, and returns its path as runs already know it, if they do.</summary>
    private async Task<string> RequireRepositoryAsync(string repositoryPath, CancellationToken ct)
    {
        var probe = await GitWorkspace.ProbeAsync(repositoryPath, ct);
        if (!probe.Exists) throw new FactoryValidationException($"The folder {repositoryPath} does not exist.");
        if (!probe.IsGitRepository) throw new FactoryValidationException($"{repositoryPath} is not a Git repository.");
        if (!probe.HasCommits) throw new FactoryValidationException($"{repositoryPath} has no commits yet, so there is nothing to run or report on.");

        var key = FactoryOrchestrator.RepositoryKey(repositoryPath);
        var known = (await runs.ListRunsAsync(ct)).Select(r => r.Task.RepositoryPath)
            .Concat((await store.ListAsync(null, ct)).Select(r => r.RepositoryPath))
            .FirstOrDefault(p => FactoryOrchestrator.RepositoryKey(p).Equals(key, StringComparison.OrdinalIgnoreCase));
        return known ?? key;
    }

    private static void Append(Active active, string text, bool newLine = true)
    {
        lock (active.Gate)
        {
            active.Output.Append(text);
            if (newLine) active.Output.Append('\n');
            if (active.Output.Length > MaxOutputChars) active.Output.Remove(0, active.Output.Length - MaxOutputChars * 3 / 4);
        }
    }

    private async Task FinishAsync(AdHocRequest request, Active active)
    {
        request.CompletedAt = DateTime.UtcNow;
        lock (active.Gate) request.Output = active.Output.ToString();
        try
        {
            await store.UpdateAsync(request, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not save ad hoc request {RequestId}", request.Id);
        }
        _active.TryRemove(request.Id, out _);
        active.Cancellation.Dispose();
        bus.Publish(new RequestChanged(request.Id));
    }

    private static string KeyHash(string repository) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(FactoryOrchestrator.RepositoryKey(repository).ToLowerInvariant())))[..8].ToLowerInvariant();

    private static void DeleteDirectory(string directory)
    {
        // Git marks object files read-only, which blocks a recursive delete on Windows.
        // Linked folders are skipped, so a junction or symlink can't lead this out of the worktree.
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in Directory.EnumerateFiles(directory, "*", options))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(directory, recursive: true);
    }
}

internal static class AdHocRequestExtensions
{
    public static bool RunsCommands(this AdHocRequest request) => request.Kind is RequestKind.RunApp or RequestKind.RunCommand;
}
