using System.ComponentModel;
using Factory.Contracts;
using Factory.Engine.AdHoc;
using Factory.Engine.Orchestration;
using Factory.Engine.Workspace;
using Factory.Web.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Factory.Web.Mcp;

public sealed record RequestSummary(
    int RequestId, string Repository, string Kind, string Status, string Title,
    string? Command, string? Source, int? ExitCode, int? RunId, string? Error, string Started, string Url);

public sealed record RequestDetails(RequestSummary Request, string? Result, string? Output, string? WorkDirectory, string Note = McpNotes.Data);

public sealed record RepositoryRequests(string Repository, string Project, IReadOnlyList<RequestSummary> Requests);

/// <summary>
/// Ad hoc requests for MCP clients: a status report, running the software, a command, a question, or a defect report or change request, always about one
/// repository. They are the same requests the UI shows, grouped under that repository beside its runs.
/// </summary>
[McpServerToolType]
public sealed class AdHocTools(AdHocManager manager, IConfiguration configuration, ISettingsStore settingsStore, ModelHealthService modelHealth)
{
    private const int MaxOutputChars = 20_000;

    [McpServerTool(Name = "status_report")]
    [Description("""
        Returns a current status report for a repository: its branch and uncommitted changes, every Hatchet run in it with progress,
        what each is doing now, what each needs from the user, and recent events. Built from Hatchet's own data, instantly, with no model.
        The report is kept as a request, grouped under the repository.
        """ + " " + McpNotes.Data)]
    public async Task<RequestDetails> StatusReport([Description("Absolute path of the repository.")] string repositoryPath, CancellationToken ct = default) =>
        await DescribeAsync((await Do(() => manager.RequestStatusReportAsync(PathGuard.RejectNetworkPath(repositoryPath, nameof(repositoryPath)), ct))).Id, ct);

    [McpServerTool(Name = "run_app")]
    [Description("""
        Runs the current version of the software in a repository: its latest commit, checked out into an isolated copy so it can't clash
        with a run that is building the same repository. Uncommitted work isn't included. Hatchet detects how to start it (dotnet, npm, cargo,
        go or python) unless you give a command. It keeps running until stopped with stop_request; follow it with get_request.
        Only one run_app or run_command can run per repository at a time.
        """ + " " + McpNotes.Data)]
    public async Task<RequestDetails> RunApp(
        [Description("Absolute path of the repository.")] string repositoryPath,
        [Description("The command to start it, if Hatchet shouldn't detect one.")] string? command = null,
        CancellationToken ct = default) =>
        await DescribeAsync((await Do(() => manager.RequestRunAppAsync(PathGuard.RejectNetworkPath(repositoryPath, nameof(repositoryPath)), command, ct))).Id, ct);

    [McpServerTool(Name = "run_command")]
    [Description("""
        Runs a command, such as the tests, in an isolated copy of a repository's latest commit. Uncommitted work isn't included.
        Waits up to waitSeconds for it to finish and returns what it printed; if it is still running, follow it with get_request.
        """ + " " + McpNotes.Data)]
    public async Task<RequestDetails> RunCommand(
        [Description("Absolute path of the repository.")] string repositoryPath,
        [Description("The command to run.")] string command,
        [Description("How long to wait for it to finish before returning.")] int waitSeconds = 20,
        CancellationToken ct = default)
    {
        var request = await Do(() => manager.RequestCommandAsync(PathGuard.RejectNetworkPath(repositoryPath, nameof(repositoryPath)), command, ct));
        return await WaitAndDescribeAsync(request.Id, waitSeconds, ct);
    }

    [McpServerTool(Name = "ask")]
    [Description("""
        Asks Claude Code a question about a repository, such as why a subtask is slow or where something is implemented. It can read and search
        the files and nothing else, and is given the repository's status report. Waits up to waitSeconds for the answer; if it isn't ready,
        follow it with get_request.
        """ + " " + McpNotes.Data)]
    public async Task<RequestDetails> Ask(
        [Description("Absolute path of the repository.")] string repositoryPath,
        [Description("The question.")] string question,
        [Description("How long to wait for the answer before returning.")] int waitSeconds = 60,
        CancellationToken ct = default)
    {
        var request = await Do(() => manager.AskAsync(PathGuard.RejectNetworkPath(repositoryPath, nameof(repositoryPath)), question, ct));
        return await WaitAndDescribeAsync(request.Id, waitSeconds, ct);
    }

    [McpServerTool(Name = "report_defect")]
    [Description("""
        Reports a defect in a repository's software, with screenshots if you have them, and has Hatchet fix it. With screenshots, Claude Code reads
        them and the project's files (read-only) and writes up what they show and the likely cause. Hatchet then starts a run from that, which pauses
        for the user to approve its plan before anything is built, on its own factory/run-N branch. Without screenshots the description goes straight
        into the run's spec. Waits up to waitSeconds for the run to start; if it hasn't, follow it with get_request, whose runId says which run it became.
        If a run is already open in the repository the new one waits in line. Fails if the repository has uncommitted changes and no run is open.
        When Claude finds nothing in the project to change, for example because the cause is outside the code, no run is started: runId stays empty
        and result holds Claude's write-up, which says what the person should do instead.
        """ + " " + McpNotes.Data)]
    public async Task<RequestDetails> ReportDefect(
        [Description("Absolute path of the repository.")] string repositoryPath,
        [Description("What went wrong and what was expected, and how to make it happen. The first line becomes the title.")] string description,
        [Description("Absolute paths of screenshot files on this computer (PNG, JPEG, GIF or WebP, up to 10 MB and 8 files).")] string[]? screenshotPaths = null,
        [Description("Which model does the work: local:<model id> or claude:opus, claude:sonnet or claude:haiku (plain opus, sonnet and haiku work too). It runs every agent of the run, and Claude's reading of the screenshots when it is a Claude model. Leave it out to use Settings. list_models shows what is available.")] string? model = null,
        [Description("How long to wait for the run to start before returning.")] int waitSeconds = 90,
        CancellationToken ct = default) =>
        await FileReportAsync(RequestKind.Defect, repositoryPath, description, screenshotPaths, model, waitSeconds, ct);

    [McpServerTool(Name = "request_change")]
    [Description("""
        Asks Hatchet to change a repository's software, with screenshots if you have them, for example a mock-up or a screenshot of what to alter.
        It works like report_defect: Claude Code reads the screenshots and the code, then Hatchet starts a run that pauses for the user to approve its plan.
        """ + " " + McpNotes.Data)]
    public async Task<RequestDetails> RequestChange(
        [Description("Absolute path of the repository.")] string repositoryPath,
        [Description("What should change. The first line becomes the title.")] string description,
        [Description("Absolute paths of screenshot files on this computer (PNG, JPEG, GIF or WebP, up to 10 MB and 8 files).")] string[]? screenshotPaths = null,
        [Description("Which model does the work: local:<model id> or claude:opus, claude:sonnet or claude:haiku (plain opus, sonnet and haiku work too). It runs every agent of the run, and Claude's reading of the screenshots when it is a Claude model. Leave it out to use Settings. list_models shows what is available.")] string? model = null,
        [Description("How long to wait for the run to start before returning.")] int waitSeconds = 90,
        CancellationToken ct = default) =>
        await FileReportAsync(RequestKind.Change, repositoryPath, description, screenshotPaths, model, waitSeconds, ct);

    [McpServerTool(Name = "list_models", ReadOnly = true)]
    [Description("Lists the models that can be chosen for report_defect and request_change: the local models (the configured one and whatever else the model server has) and Claude's. The empty value means as set in Settings.")]
    public async Task<IReadOnlyList<ModelOption>> ListModels(CancellationToken ct = default)
    {
        var settings = await settingsStore.GetAsync(ct);
        return ModelChoices.Options(settings, modelHealth.Current?.AvailableModels ?? []);
    }

    [McpServerTool(Name = "list_requests", ReadOnly = true)]
    [Description("Lists ad hoc requests, grouped by repository, newest first. Pass a repository to list only its requests.")]
    public async Task<IReadOnlyList<RepositoryRequests>> ListRequests(
        [Description("Absolute path of a repository, to list only its requests.")] string? repositoryPath = null,
        CancellationToken ct = default)
    {
        var requests = await manager.ListAsync(PathGuard.RejectNetworkPathIfSet(repositoryPath, nameof(repositoryPath)), ct);
        return requests
            .GroupBy(r => FactoryOrchestrator.RepositoryKey(r.RepositoryPath), StringComparer.OrdinalIgnoreCase)
            .Select(g => new RepositoryRequests(g.First().RepositoryPath, Fmt.FolderName(g.First().RepositoryPath), g.Select(Summarize).ToList()))
            .ToList();
    }

    [McpServerTool(Name = "get_request", ReadOnly = true)]
    [Description("Shows a request in full: its status, the report or answer, and what the command has printed so far (the end of it, if long). " + McpNotes.Data)]
    public Task<RequestDetails> GetRequest([Description("The request's id.")] int requestId, CancellationToken ct = default) =>
        DescribeAsync(requestId, ct);

    [McpServerTool(Name = "stop_request")]
    [Description("Stops a running request. A command's whole process tree is killed, so this is how to close software started with run_app. " + McpNotes.Data)]
    public async Task<RequestDetails> StopRequest([Description("The request's id.")] int requestId, CancellationToken ct = default)
    {
        await Do(() => manager.StopAsync(requestId));
        return await DescribeAsync(requestId, ct);
    }

    [McpServerTool(Name = "rerun_request")]
    [Description("Starts a request again, as a new one, on the repository's latest commit. For a command that is how to run it on newer code. " + McpNotes.Data)]
    public async Task<RequestDetails> RerunRequest([Description("The id of the request to repeat.")] int requestId, CancellationToken ct = default) =>
        await DescribeAsync((await Do(() => manager.RerunAsync(requestId, ct))).Id, ct);

    [McpServerTool(Name = "delete_request", Destructive = true)]
    [Description("Deletes a request and, if nothing else uses it, its isolated copy of the code. A running request is stopped first. Commits and the repository itself are untouched.")]
    public async Task<string> DeleteRequest([Description("The request's id.")] int requestId)
    {
        await Do(() => manager.DeleteAsync(requestId));
        return $"Deleted request {requestId}.";
    }

    private async Task<RequestDetails> FileReportAsync(RequestKind kind, string repositoryPath, string description, string[]? screenshotPaths, string? model, int waitSeconds, CancellationToken ct)
    {
        PathGuard.RejectNetworkPath(repositoryPath, nameof(repositoryPath));
        var images = new List<ReportImage>();
        if (screenshotPaths is { Length: > ReportBuilder.MaxImages }) throw new McpException($"Attach at most {ReportBuilder.MaxImages} screenshots.");
        foreach (var path in screenshotPaths ?? [])
        {
            // The count and sizes are checked before reading, so a huge file can't be pulled into memory, and only the first
            // bytes are read to see whether it is a picture at all. Network and device paths are never opened.
            try
            {
                if (PathGuard.IsNetworkPath(path)) throw new McpException($"\"{path}\" is a network or device path. Use a file on this computer.");
                if (!Path.IsPathFullyQualified(path)) throw new McpException($"\"{path}\" isn't an absolute path.");
                var info = new FileInfo(path);
                if (!info.Exists) throw new McpException($"There is no file at {path}.");
                if (info.Length > ReportBuilder.MaxImageBytes) throw new McpException($"{path} is larger than {ReportBuilder.MaxImageBytes / 1024 / 1024} MB.");
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
                var head = new byte[16];
                var read = await stream.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
                if (ReportBuilder.ImageExtension(head[..read]) is null) throw new McpException($"{path} isn't a PNG, JPEG, GIF or WebP picture.");
                var content = new byte[Math.Min(info.Length, ReportBuilder.MaxImageBytes)];
                head.AsSpan(0, Math.Min(read, content.Length)).CopyTo(content);
                if (content.Length > read) await stream.ReadExactlyAsync(content.AsMemory(read), ct);
                images.Add(new ReportImage(info.Name, content));
            }
            catch (EndOfStreamException)
            {
                throw new McpException($"{path} changed while it was being read.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new McpException($"Could not read {path}: {ex.Message}");
            }
        }
        var request = await Do(() => manager.RequestReportAsync(kind, repositoryPath, description, images, model, ct));
        return await WaitAndDescribeAsync(request.Id, waitSeconds, ct);
    }

    private async Task<RequestDetails> WaitAndDescribeAsync(int id, int waitSeconds, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(waitSeconds, 0, 300));
        while (manager.IsActive(id) && DateTime.UtcNow < deadline) await Task.Delay(250, ct);
        return await DescribeAsync(id, ct);
    }

    private async Task<RequestDetails> DescribeAsync(int id, CancellationToken ct)
    {
        var request = await manager.GetAsync(id, ct) ?? throw new McpException($"There is no request {id}. Use list_requests to see them.");
        var output = manager.GetOutput(request);
        if (output.Length > MaxOutputChars) output = "[earlier output omitted]\n" + output[^MaxOutputChars..];
        return new RequestDetails(Summarize(request), request.Result, output.Length == 0 ? null : output, request.WorkDirectory);
    }

    private RequestSummary Summarize(AdHocRequest request) => new(
        request.Id, request.RepositoryPath, StatusReportBuilder.KindLabel(request.Kind), request.Status.ToString(), request.Title,
        request.Command, request.Source, request.ExitCode, request.RunId, request.Error, Fmt.Ago(request.CreatedAt),
        $"{Program.BaseUrl(configuration)}/requests/{request.Id}");

    // Validation problems are for the caller to read, so they go back as tool errors with their message.
    private static async Task<T> Do<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (ex is FactoryValidationException or GitException)
        {
            throw new McpException(ex.Message);
        }
    }

    private static Task Do(Func<Task> action) => Do(async () =>
    {
        await action();
        return true;
    });
}
