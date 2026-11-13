using Factory.Contracts;
using Factory.Engine.AdHoc;
using Factory.Engine.Llm;
using Factory.Engine.Orchestration;
using Factory.Engine.Verification;
using Factory.Engine.Workspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace Factory.Tests;

/// <summary>Ad hoc requests against a real git repository, with a scripted command runner and asker.</summary>
public class AdHocTests : IAsyncLifetime
{
    private readonly TempDirectory _repo = new();
    private readonly TempDirectory _data = new();
    private readonly GitWorkspace _git;
    private readonly MemoryAdHocStore _store = new();
    private readonly StubRunStore _runs = new();
    private readonly FakeRunner _runner = new();
    private readonly FakeAsker _asker = new();
    private readonly FakeStarter _starter = new();
    private readonly AdHocManager _manager;

    public AdHocTests()
    {
        _git = new GitWorkspace(_repo.Path);
        _manager = new AdHocManager(_store, _runs, new MemorySettings(), _runner, _asker, new FactoryEventBus(), NullLogger<AdHocManager>.Instance, _data.Path, _starter);
    }

    // Just enough of each format for its signature to be recognised.
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0, 1];
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1];

    [Fact]
    public async Task A_defect_with_a_screenshot_is_read_by_claude_and_becomes_a_run_that_asks_for_plan_approval()
    {
        _asker.Answer = "## What the screenshots show\nA dialog: Could not load Microsoft.Windows.SDK.NET.";
        var request = await _manager.RequestReportAsync(RequestKind.Defect, _repo.Path, "Unexpected Error on start\nIt shows after I double-click the exe.", [new ReportImage("Screenshot 1.png", Png)]);

        request = await FinishedAsync(request);

        Assert.Equal(RequestStatus.Succeeded, request.Status);
        Assert.Equal(41, request.RunId);
        var run = Assert.Single(_starter.Started);
        Assert.True(run.RequirePlanApproval);
        Assert.Equal("Fix: Unexpected Error on start", run.Title);
        Assert.Contains("Microsoft.Windows.SDK.NET", run.ArchitectureSpec);
        Assert.Contains("double-click the exe", run.ArchitectureSpec);
        Assert.Contains("Fix this defect and nothing else", run.ArchitectureSpec);
        Assert.Equal(run.ArchitectureSpec, request.Result);

        // Claude gets its own brief and can read the folder the screenshots were saved in, but nothing is left in the repository.
        var directory = Path.Combine(_data.Path, "attachments", request.Id.ToString());
        Assert.Equal(ReportBuilder.TriagePrompt, _asker.Options!.SystemPrompt);
        Assert.Equal([directory], _asker.Options.ExtraDirectories);
        Assert.Contains(Path.Combine(directory, "1-Screenshot1.png"), _asker.Question);
        Assert.Equal([Path.Combine(directory, "1-Screenshot1.png")], _manager.GetAttachments(request.Id));
        Assert.Empty(await _git.GetUncommittedChangesAsync());
    }

    [Fact]
    public async Task When_claude_finds_nothing_to_change_no_run_starts_but_one_can_be_started_anyway()
    {
        _asker.Answer = "## Likely cause\nThe app was copied without its DLLs.\n\n## What to change\nNothing.\n\nVERDICT: NO CHANGE\n";
        var request = await FinishedAsync(await _manager.RequestReportAsync(RequestKind.Defect, _repo.Path, "Unexpected Error on start", [new ReportImage("a.png", Png)]));

        Assert.Equal(RequestStatus.Succeeded, request.Status);
        Assert.Null(request.RunId);
        Assert.Empty(_starter.Started);
        Assert.Contains("copied without its DLLs", request.Result);
        Assert.DoesNotContain("VERDICT", request.Result);

        var started = await _manager.StartRunAnywayAsync(request.Id);

        Assert.Equal(41, started.RunId);
        Assert.Contains("copied without its DLLs", Assert.Single(_starter.Started).ArchitectureSpec);
        Assert.Equal(_starter.Started[0].ArchitectureSpec, (await _store.GetAsync(request.Id))!.Result);
        var again = await Assert.ThrowsAsync<FactoryValidationException>(() => _manager.StartRunAnywayAsync(request.Id));
        Assert.Contains("already became run #41", again.Message);
    }

    [Theory]
    [InlineData("## Done when\n- it works\n\nVERDICT: CHANGE", true, "## Done when\n- it works")]
    [InlineData("Nothing to do.\r\n**`VERDICT: NO CHANGE`**\r\n", false, "Nothing to do.")]
    [InlineData("verdict: no change", false, "")]
    [InlineData("## Done when\n- it works", true, "## Done when\n- it works")]
    [InlineData("It might not need a change.\nVERDICT: NO CHANGE, probably", true, "It might not need a change.\nVERDICT: NO CHANGE, probably")]
    public void Claudes_closing_verdict_is_split_off_and_only_an_explicit_no_change_stops_the_run(string reply, bool changeNeeded, string body)
    {
        Assert.Equal((body, changeNeeded), ReportBuilder.ParseVerdict(reply));
    }

    [Fact]
    public async Task A_run_cannot_be_started_anyway_for_a_report_that_is_still_being_read_or_failed()
    {
        _asker.Failure = new LlmException("Claude Code isn't signed in.");
        var failed = await FinishedAsync(await _manager.RequestReportAsync(RequestKind.Defect, _repo.Path, "It crashes.", [new ReportImage("a.png", Png)]));

        await Assert.ThrowsAsync<FactoryValidationException>(() => _manager.StartRunAnywayAsync(failed.Id));
        await Assert.ThrowsAsync<FactoryValidationException>(() => _manager.StartRunAnywayAsync(failed.Id + 99));
        Assert.Empty(_starter.Started);
    }

    [Fact]
    public async Task The_chosen_model_reads_the_screenshots_and_is_given_to_the_run()
    {
        var request = await FinishedAsync(await _manager.RequestReportAsync(RequestKind.Defect, _repo.Path, "It crashes.", [new ReportImage("a.png", Png)], "opus"));

        Assert.Equal("opus", _asker.Options!.Model);
        Assert.Equal("claude:opus", Assert.Single(_starter.Started).ModelChoice);
        Assert.Equal("claude:opus", (await _store.GetAsync(request.Id))!.ModelChoice);
    }

    [Fact]
    public async Task A_local_choice_goes_to_the_run_while_claude_still_reads_the_screenshots_as_usual()
    {
        await FinishedAsync(await _manager.RequestReportAsync(RequestKind.Change, _repo.Path, "Add a dark theme.", [new ReportImage("a.png", Png)], "local:qwen-small"));

        Assert.Null(_asker.Options!.Model); // Claude Code's default for questions applies, since the local model can't see pictures.
        Assert.Equal("local:qwen-small", Assert.Single(_starter.Started).ModelChoice);
    }

    [Fact]
    public async Task A_run_started_anyway_keeps_the_model_that_was_chosen()
    {
        _asker.Answer = "## What to change\nNothing.\n\nVERDICT: NO CHANGE";
        var request = await FinishedAsync(await _manager.RequestReportAsync(RequestKind.Defect, _repo.Path, "It crashes.", [new ReportImage("a.png", Png)], "claude:haiku"));

        await _manager.StartRunAnywayAsync(request.Id);

        Assert.Equal("claude:haiku", Assert.Single(_starter.Started).ModelChoice);
    }

    [Fact]
    public async Task A_model_that_is_not_a_choice_is_refused_before_a_report_is_filed()
    {
        await Assert.ThrowsAsync<FactoryValidationException>(() =>
            _manager.RequestReportAsync(RequestKind.Defect, _repo.Path, "It crashes.", [new ReportImage("a.png", Png)], "gpt-9"));

        Assert.Empty(await _store.ListAsync());
        Assert.Null(_asker.Question);
    }

    [Fact]
    public async Task A_change_without_screenshots_goes_straight_into_the_spec_without_asking_claude()
    {
        var request = await FinishedAsync(await _manager.RequestReportAsync(RequestKind.Change, _repo.Path, "Add a dark theme."));

        Assert.Equal(RequestStatus.Succeeded, request.Status);
        Assert.Null(_asker.Question);
        var run = Assert.Single(_starter.Started);
        Assert.Equal("Change: Add a dark theme.", run.Title);
        Assert.Contains("Make this change and nothing else", run.ArchitectureSpec);
        Assert.DoesNotContain("screenshot", run.ArchitectureSpec);
        Assert.Empty(_manager.GetAttachments(request.Id));
    }

    [Fact]
    public async Task A_report_stops_before_asking_claude_when_a_run_could_not_start()
    {
        _starter.NotReady = new FactoryValidationException("The repository has uncommitted changes.");

        var failure = await Assert.ThrowsAsync<FactoryValidationException>(() =>
            _manager.RequestReportAsync(RequestKind.Defect, _repo.Path, "It crashes.", [new ReportImage("a.png", Png)]));

        Assert.Contains("uncommitted changes", failure.Message);
        Assert.Null(_asker.Question);
        Assert.Empty(await _store.ListAsync());
    }

    [Fact]
    public async Task A_report_whose_screenshots_claude_cannot_read_fails_without_starting_a_run()
    {
        _asker.Failure = new LlmException("Claude Code isn't signed in.");

        var request = await FinishedAsync(await _manager.RequestReportAsync(RequestKind.Defect, _repo.Path, "It crashes.", [new ReportImage("a.png", Png)]));

        Assert.Equal(RequestStatus.Failed, request.Status);
        Assert.Contains("isn't signed in", request.Error);
        Assert.Empty(_starter.Started);
        Assert.Null(request.RunId);
    }

    [Fact]
    public async Task Only_pictures_are_accepted_and_only_a_few()
    {
        var notAPicture = await Assert.ThrowsAsync<FactoryValidationException>(() =>
            _manager.RequestReportAsync(RequestKind.Defect, _repo.Path, "It crashes.", [new ReportImage("id_rsa.png", "-----BEGIN PRIVATE KEY-----"u8.ToArray())]));
        Assert.Contains("isn't a PNG, JPEG, GIF or WebP", notAPicture.Message);

        var tooMany = await Assert.ThrowsAsync<FactoryValidationException>(() =>
            _manager.RequestReportAsync(RequestKind.Defect, _repo.Path, "It crashes.", Enumerable.Repeat(new ReportImage("a.png", Png), ReportBuilder.MaxImages + 1).ToList()));
        Assert.Contains("at most", tooMany.Message);

        await Assert.ThrowsAsync<FactoryValidationException>(() => _manager.RequestReportAsync(RequestKind.Defect, _repo.Path, "  "));
        Assert.Empty(await _store.ListAsync());
        Assert.Empty(_starter.Started);
    }

    [Fact]
    public void Pictures_are_saved_in_order_under_names_their_bytes_decide()
    {
        var prepared = ReportBuilder.Prepare([new ReportImage("evil name!.exe", Png), new ReportImage("", Jpeg)]);

        Assert.Equal(["1-evilname.png", "2-screenshot.jpg"], prepared.Select(p => p.FileName));
    }

    [Fact]
    public async Task Deleting_a_report_removes_its_screenshots_but_not_the_run()
    {
        var request = await FinishedAsync(await _manager.RequestReportAsync(RequestKind.Defect, _repo.Path, "It crashes.", [new ReportImage("a.png", Png)]));
        var directory = Path.Combine(_data.Path, "attachments", request.Id.ToString());
        Assert.True(Directory.Exists(directory));

        await _manager.DeleteAsync(request.Id);

        Assert.False(Directory.Exists(directory));
        Assert.Single(_starter.Started);
    }

    [Fact]
    public async Task A_report_cannot_be_repeated_because_it_already_became_a_run()
    {
        var request = await FinishedAsync(await _manager.RequestReportAsync(RequestKind.Change, _repo.Path, "Add a dark theme."));

        var failure = await Assert.ThrowsAsync<FactoryValidationException>(() => _manager.RerunAsync(request.Id));

        Assert.Contains("already became run #41", failure.Message);
    }

    [Fact]
    public async Task Attachments_are_only_found_by_their_exact_file_name()
    {
        var request = await FinishedAsync(await _manager.RequestReportAsync(RequestKind.Defect, _repo.Path, "It crashes.", [new ReportImage("a.png", Png)]));

        Assert.NotNull(_manager.GetAttachmentPath(request.Id, "1-a.png"));
        Assert.Null(_manager.GetAttachmentPath(request.Id, @"..\1-a.png"));
        Assert.Null(_manager.GetAttachmentPath(request.Id, "../1-a.png"));
        Assert.Null(_manager.GetAttachmentPath(request.Id + 1, "1-a.png"));
    }

    public async Task InitializeAsync()
    {
        await _git.InitializeAsync();
        _repo.Write("README.md", "hello");
        await _git.StageAllAsync();
        await _git.CommitStagedAsync("First commit");
    }

    public Task DisposeAsync()
    {
        _repo.Dispose();
        _data.Dispose();
        return Task.CompletedTask;
    }

    private async Task<AdHocRequest> FinishedAsync(AdHocRequest request)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (_manager.IsActive(request.Id) && DateTime.UtcNow < deadline) await Task.Delay(10);
        return (await _store.GetAsync(request.Id))!;
    }

    private async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(condition());
    }

    private FactoryRun Run(int id, string title, RunStatus status, string? repository = null) => new()
    {
        Id = id, Status = status, BranchName = $"factory/run-{id}", BaseBranch = "main",
        Task = new FactoryTask { Title = title, RepositoryPath = repository ?? _repo.Path }
    };

    // ---- Status report ----

    [Fact]
    public async Task A_status_report_is_built_at_once_from_the_repository_and_its_runs()
    {
        _repo.Write("notes.txt", "draft");
        _runs.Runs.Add(Run(1, "Network Map", RunStatus.Executing));
        _runs.Subtasks[1] =
        [
            new() { Id = 1, RunId = 1, Order = 1, Title = "Scaffold", Status = SubtaskStatus.Completed },
            new() { Id = 2, RunId = 1, Order = 2, Title = "Grid", Status = SubtaskStatus.Pending }
        ];

        var request = await _manager.RequestStatusReportAsync(_repo.Path);

        Assert.Equal(RequestStatus.Succeeded, request.Status);
        Assert.Equal(RequestKind.StatusReport, request.Kind);
        Assert.NotNull(request.CompletedAt);
        Assert.Contains("Run #1 \"Network Map\": building", request.Result);
        Assert.Contains("1 of 2 subtasks done", request.Result);
        Assert.Contains("notes.txt", request.Result); // Uncommitted work is reported, though commands don't run it.
        Assert.Contains("First commit", request.Result);
        Assert.Equal(request.Id, (await _store.ListAsync()).Single().Id);
    }

    [Fact]
    public void A_status_report_says_what_a_stopped_run_needs_from_the_user()
    {
        var run = Run(1, "Network Map", RunStatus.NeedsHuman);
        run.PauseReason = "The model server refused the connection.";
        run.PausedStage = PipelineStage.Build;
        run.CurrentSubtaskId = 7;
        var subtasks = new List<FactorySubtask> { new() { Id = 7, Order = 4, Title = "Map page", Status = SubtaskStatus.NeedsHuman, LastFailure = "x" } };

        var report = StatusReportBuilder.Build(new StatusReportInput(_repo.Path, DateTime.UtcNow, "main", "abcdef0123", [], [], null,
            [new RunReportInput(run, subtasks, ["12:00:00 It stopped."], null)], []));

        Assert.Contains("**Needs you:**", report);
        Assert.Contains("hit an error that isn't a code problem", report);
        Assert.Contains("12:00:00 It stopped.", report);
        Assert.Contains("On main at abcdef01", report);
    }

    // ---- Running things ----

    [Fact]
    public async Task A_command_runs_in_an_isolated_copy_of_the_latest_commit_not_the_working_tree()
    {
        _repo.Write("draft.txt", "uncommitted");
        var head = (await _git.GetHeadShaAsync())[..8];

        var request = await _manager.RequestCommandAsync(_repo.Path, "dotnet test");
        request = await FinishedAsync(request);

        Assert.Equal(RequestStatus.Succeeded, request.Status);
        Assert.Equal("dotnet test", request.Command);
        Assert.Contains(head, request.Source);
        var (_, directory) = Assert.Single(_runner.Calls);
        Assert.NotEqual(_repo.Path, directory);
        Assert.StartsWith(_data.Path, directory);
        Assert.True(File.Exists(Path.Combine(directory, "README.md"))); // Committed work is there.
        Assert.False(File.Exists(Path.Combine(directory, "draft.txt"))); // Uncommitted work isn't.
        Assert.Contains("fake output", request.Output);
        Assert.Equal(directory, request.WorkDirectory);
        Assert.Equal(["draft.txt"], (await _git.GetUncommittedChangesAsync()).Select(l => l.Split(' ').Last()));
    }

    [Fact]
    public async Task Running_the_app_detects_how_to_start_it()
    {
        _repo.Write("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>WinExe</OutputType><UseWPF>true</UseWPF></PropertyGroup></Project>");
        _repo.Write("src/Lib/Lib.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        await _git.StageAllAsync();
        await _git.CommitStagedAsync("Add projects");

        var request = await FinishedAsync(await _manager.RequestRunAppAsync(_repo.Path));

        Assert.Equal(RequestStatus.Succeeded, request.Status);
        Assert.Equal("dotnet run --project src/App/App.csproj", request.Command);
        Assert.Equal("", request.Input); // Hatchet chose it, so the request stays "run the app" when repeated.
        Assert.Contains("Detected .NET project App.csproj", request.Output);
    }

    [Fact]
    public async Task Running_the_app_with_nothing_to_detect_fails_and_says_what_to_do()
    {
        var request = await FinishedAsync(await _manager.RequestRunAppAsync(_repo.Path));

        Assert.Equal(RequestStatus.Failed, request.Status);
        Assert.Contains("Say which command to run", request.Error);
        Assert.Empty(_runner.Calls);
    }

    [Fact]
    public async Task A_failing_command_is_marked_failed_with_its_exit_code()
    {
        _runner.ExitCode = 3;

        var request = await FinishedAsync(await _manager.RequestCommandAsync(_repo.Path, "false"));

        Assert.Equal(RequestStatus.Failed, request.Status);
        Assert.Equal(3, request.ExitCode);
        Assert.Contains("exited with code 3", request.Error);
    }

    [Fact]
    public async Task Only_one_command_runs_per_repository_and_stopping_it_frees_the_repository()
    {
        _runner.Hold = true;
        var app = await _manager.RequestCommandAsync(_repo.Path, "dotnet run");
        await WaitUntilAsync(() => _runner.Calls.Count == 1);

        var refused = await Assert.ThrowsAsync<FactoryValidationException>(() => _manager.RequestCommandAsync(_repo.Path, "dotnet test"));
        Assert.Contains($"#{app.Id} is still running", refused.Message);

        await _manager.StopAsync(app.Id);
        var stopped = await FinishedAsync(app);
        Assert.Equal(RequestStatus.Stopped, stopped.Status);
        Assert.False(_manager.IsActive(app.Id));

        _runner.Hold = false;
        var next = await FinishedAsync(await _manager.RequestCommandAsync(_repo.Path, "dotnet test"));
        Assert.Equal(RequestStatus.Succeeded, next.Status);
    }

    [Fact]
    public async Task Live_output_is_available_while_a_command_runs_and_kept_once_it_ends()
    {
        _runner.Hold = true;
        var request = await _manager.RequestCommandAsync(_repo.Path, "dotnet run");
        await WaitUntilAsync(() => _runner.Calls.Count == 1);
        await WaitUntilAsync(() => _manager.GetOutput(request).Contains("fake output"));

        await _manager.StopAsync(request.Id);
        var saved = await FinishedAsync(request);

        Assert.Contains("fake output", saved.Output);
        Assert.Contains("Checking out the latest commit", saved.Output);
    }

    [Fact]
    public async Task The_same_commit_reuses_its_copy_and_a_new_commit_gets_a_new_one()
    {
        var first = await FinishedAsync(await _manager.RequestCommandAsync(_repo.Path, "one"));
        var again = await FinishedAsync(await _manager.RequestCommandAsync(_repo.Path, "two"));
        Assert.Equal(first.WorkDirectory, again.WorkDirectory);

        _repo.Write("new.txt", "more");
        await _git.StageAllAsync();
        await _git.CommitStagedAsync("Second commit");
        var newer = await FinishedAsync(await _manager.RequestCommandAsync(_repo.Path, "three"));

        Assert.NotEqual(first.WorkDirectory, newer.WorkDirectory);
        Assert.True(File.Exists(Path.Combine(newer.WorkDirectory!, "new.txt")));
    }

    [Fact]
    public async Task Deleting_a_request_removes_its_copy_once_nothing_else_uses_it()
    {
        var one = await FinishedAsync(await _manager.RequestCommandAsync(_repo.Path, "one"));
        var two = await FinishedAsync(await _manager.RequestCommandAsync(_repo.Path, "two"));

        await _manager.DeleteAsync(one.Id);
        Assert.True(Directory.Exists(one.WorkDirectory)); // Another request still uses the copy.

        await _manager.DeleteAsync(two.Id);
        Assert.False(Directory.Exists(one.WorkDirectory));
        Assert.Empty(await _store.ListAsync());
        // The repository is untouched, and git no longer lists the copy.
        Assert.True(File.Exists(_repo.File("README.md")));
    }

    [Fact]
    public async Task Rerunning_starts_the_same_request_again_as_a_new_one()
    {
        var original = await FinishedAsync(await _manager.RequestCommandAsync(_repo.Path, "dotnet test"));

        var again = await FinishedAsync(await _manager.RerunAsync(original.Id));

        Assert.NotEqual(original.Id, again.Id);
        Assert.Equal("dotnet test", again.Command);
        Assert.Equal(2, (await _store.ListAsync()).Count);
    }

    [Fact]
    public async Task Requests_that_were_running_when_hatchet_stopped_end_as_stopped()
    {
        var stale = await _store.CreateAsync(new AdHocRequest { RepositoryPath = _repo.Path, Kind = RequestKind.RunApp, Title = "Run the app" });

        await _manager.ReconcileAsync();

        var request = (await _store.GetAsync(stale.Id))!;
        Assert.Equal(RequestStatus.Stopped, request.Status);
        Assert.Contains("close it yourself", request.Error);
    }

    // ---- Questions ----

    [Fact]
    public async Task A_question_is_answered_with_the_status_report_as_context()
    {
        _runs.Runs.Add(Run(1, "Network Map", RunStatus.Executing));
        _asker.Answer = "Subtask 41 is in review.";

        var request = await FinishedAsync(await _manager.AskAsync(_repo.Path, "  Where is run 1?  "));

        Assert.Equal(RequestStatus.Succeeded, request.Status);
        Assert.Equal("Subtask 41 is in review.", request.Result);
        Assert.Equal("Where is run 1?", _asker.Question);
        Assert.Contains("Run #1 \"Network Map\"", _asker.Context);
        Assert.Equal(_repo.Path, _asker.Repository);
    }

    [Fact]
    public async Task A_question_claude_cannot_answer_fails_with_the_reason()
    {
        _asker.Failure = new LlmException("Claude Code isn't logged in.");

        var request = await FinishedAsync(await _manager.AskAsync(_repo.Path, "Anything?"));

        Assert.Equal(RequestStatus.Failed, request.Status);
        Assert.Equal("Claude Code isn't logged in.", request.Error);
    }

    // ---- Validation and grouping ----

    [Fact]
    public async Task Requests_need_a_git_repository_with_a_commit_and_something_to_do()
    {
        await Assert.ThrowsAsync<FactoryValidationException>(() => _manager.RequestStatusReportAsync(Path.Combine(_data.Path, "missing")));
        using var plain = new TempDirectory();
        await Assert.ThrowsAsync<FactoryValidationException>(() => _manager.RequestStatusReportAsync(plain.Path));
        await Assert.ThrowsAsync<FactoryValidationException>(() => _manager.RequestCommandAsync(_repo.Path, "  "));
        await Assert.ThrowsAsync<FactoryValidationException>(() => _manager.AskAsync(_repo.Path, ""));
    }

    [Fact]
    public async Task Requests_group_with_a_repositorys_runs_however_its_path_is_written()
    {
        if (!OperatingSystem.IsWindows()) return; // Paths differ only by case where the file system ignores it.
        _runs.Runs.Add(Run(1, "Network Map", RunStatus.Executing, _repo.Path.ToUpperInvariant() + Path.DirectorySeparatorChar));

        var request = await _manager.RequestStatusReportAsync(_repo.Path.ToLowerInvariant());

        // The request takes the path the run already uses, so both list under one folder.
        Assert.Equal(_repo.Path.ToUpperInvariant() + Path.DirectorySeparatorChar, request.RepositoryPath, ignoreCase: true);
        Assert.Contains("Run #1", request.Result);
        Assert.Single(await _manager.ListAsync(_repo.Path));
    }

    [Fact]
    public void The_run_command_detector_prefers_apps_over_tools_and_skips_test_projects()
    {
        using var folder = new TempDirectory();
        folder.Write("tests/T.Tests/T.Tests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" /></ItemGroup></Project>");
        folder.Write("tools/Cli/Cli.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup></Project>");
        Assert.Equal("dotnet run --project tools/Cli/Cli.csproj", RunCommandDetector.Detect(folder.Path)!.Command);

        folder.Write("src/Web/Web.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");
        Assert.Equal("dotnet run --project src/Web/Web.csproj", RunCommandDetector.Detect(folder.Path)!.Command);
    }

    [Fact]
    public void The_run_command_detector_covers_node_python_and_nothing()
    {
        using var node = new TempDirectory();
        node.Write("package.json", "{\"scripts\":{\"dev\":\"vite\",\"test\":\"vitest\"}}");
        Assert.Equal("npm install && npm run dev", RunCommandDetector.Detect(node.Path)!.Command); // A fresh copy has no packages yet.
        node.Write("package.json", "{\"scripts\":{\"start\":\"node .\"}}");
        node.Write("node_modules/x/index.js", "");
        Assert.Equal("npm start", RunCommandDetector.Detect(node.Path)!.Command);

        using var python = new TempDirectory();
        python.Write("main.py", "print('hi')");
        Assert.Equal("python main.py", RunCommandDetector.Detect(python.Path)!.Command);

        using var empty = new TempDirectory();
        empty.Write("notes.txt", "nothing to run");
        Assert.Null(RunCommandDetector.Detect(empty.Path));
    }

    // ---- Fakes ----

    private sealed class FakeRunner : ICommandRunner
    {
        public List<(string Command, string Directory)> Calls { get; } = [];
        public int ExitCode { get; set; }

        /// <summary>Keeps the command running until it is cancelled, like software that stays open.</summary>
        public bool Hold { get; set; }

        public async Task<CommandResult> RunAsync(string command, string workingDirectory, TimeSpan timeout, Action<string>? onLine, CancellationToken ct)
        {
            lock (Calls) Calls.Add((command, workingDirectory));
            onLine?.Invoke("fake output");
            if (Hold) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new CommandResult(command, ExitCode, false, "fake output", TimeSpan.FromMilliseconds(1));
        }
    }

    private sealed class FakeAsker : IClaudeAsker
    {
        public string Answer { get; set; } = "An answer.";
        public Exception? Failure { get; set; }
        public string? Repository { get; private set; }
        public string? Context { get; private set; }
        public string? Question { get; private set; }
        public AskOptions? Options { get; private set; }

        public Task<string> AskAsync(FactorySettings settings, string repositoryPath, string context, string question, Action<string> onText, CancellationToken ct, AskOptions? options = null)
        {
            (Repository, Context, Question, Options) = (repositoryPath, context, question, options);
            if (Failure is not null) throw Failure;
            onText(Answer);
            return Task.FromResult(Answer);
        }
    }

    private sealed class FakeStarter : IRunStarter
    {
        public List<NewRunRequest> Started { get; } = [];
        public Exception? NotReady { get; set; }

        public Task EnsureCanStartAsync(string repositoryPath, CancellationToken ct = default) =>
            NotReady is null ? Task.CompletedTask : Task.FromException(NotReady);

        public Task<FactoryRun> StartRunAsync(NewRunRequest request, CancellationToken ct = default)
        {
            Started.Add(request);
            return Task.FromResult(new FactoryRun { Id = 40 + Started.Count });
        }
    }

    private sealed class MemorySettings : ISettingsStore
    {
        public Task<FactorySettings> GetAsync(CancellationToken ct = default) => Task.FromResult(new FactorySettings());

        public Task SaveAsync(FactorySettings settings, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class MemoryAdHocStore : IAdHocStore
    {
        private readonly object _gate = new();
        private readonly List<AdHocRequest> _items = [];
        private int _nextId = 1;

        // Clones mirror the EF store: callers only ever hold detached copies, and lists leave out the output and result.
        private static AdHocRequest Clone(AdHocRequest r, bool full) => new()
        {
            Id = r.Id, RepositoryPath = r.RepositoryPath, Kind = r.Kind, Status = r.Status, Title = r.Title, Input = r.Input,
            Command = r.Command, Source = r.Source, WorkDirectory = r.WorkDirectory, ExitCode = r.ExitCode, RunId = r.RunId, Error = r.Error, ModelChoice = r.ModelChoice,
            CreatedAt = r.CreatedAt, CompletedAt = r.CompletedAt, Output = full ? r.Output : "", Result = full ? r.Result : null
        };

        public Task<AdHocRequest> CreateAsync(AdHocRequest request, CancellationToken ct = default)
        {
            lock (_gate)
            {
                request.Id = _nextId++;
                _items.Add(Clone(request, true));
                return Task.FromResult(request);
            }
        }

        public Task<AdHocRequest?> GetAsync(int id, CancellationToken ct = default)
        {
            lock (_gate) return Task.FromResult(_items.Where(r => r.Id == id).Select(r => Clone(r, true)).SingleOrDefault());
        }

        public Task<IReadOnlyList<AdHocRequest>> ListAsync(string? repositoryPath = null, CancellationToken ct = default)
        {
            lock (_gate)
            {
                var key = repositoryPath is null ? null : FactoryOrchestrator.RepositoryKey(repositoryPath);
                return Task.FromResult<IReadOnlyList<AdHocRequest>>(_items
                    .Where(r => key is null || FactoryOrchestrator.RepositoryKey(r.RepositoryPath).Equals(key, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(r => r.Id).Select(r => Clone(r, false)).ToList());
            }
        }

        public Task UpdateAsync(AdHocRequest request, CancellationToken ct = default)
        {
            lock (_gate)
            {
                var index = _items.FindIndex(r => r.Id == request.Id);
                if (index >= 0) _items[index] = Clone(request, true);
            }
            return Task.CompletedTask;
        }

        public Task DeleteAsync(int id, CancellationToken ct = default)
        {
            lock (_gate) _items.RemoveAll(r => r.Id == id);
            return Task.CompletedTask;
        }
    }

    /// <summary>Only what the report reads: runs, their subtasks and their recent events.</summary>
    private sealed class StubRunStore : IRunStore
    {
        public List<FactoryRun> Runs { get; } = [];
        public Dictionary<int, List<FactorySubtask>> Subtasks { get; } = [];

        public Task<IReadOnlyList<FactoryRun>> ListRunsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<FactoryRun>>(Runs);

        public Task<IReadOnlyList<FactorySubtask>> GetSubtasksForRunsAsync(IReadOnlyCollection<int> runIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<FactorySubtask>>(Subtasks.Where(p => runIds.Contains(p.Key)).SelectMany(p => p.Value).ToList());

        public Task<IReadOnlyList<AgentLogEntry>> GetRecentEventsAsync(int runId, int count, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AgentLogEntry>>([]);

        public Task<IReadOnlyDictionary<int, TimeSpan>> GetWorkBySubtaskAsync(int runId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<int, TimeSpan>>(new Dictionary<int, TimeSpan>());

        public Task<FactoryRun> CreateRunAsync(int? existingTaskId, FactoryTask? newTask, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<FactoryRun> GetRunAsync(int runId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateRunAsync(FactoryRun run, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<FactorySubtask>> GetSubtasksAsync(int runId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateSubtaskAsync(FactorySubtask subtask, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ReplaceSubtasksAsync(int runId, IReadOnlyList<FactorySubtask> subtasks, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<FactorySubtask> AddSubtaskAsync(FactorySubtask subtask, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentLogEntry> AppendLogAsync(AgentLogEntry entry, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AgentLogEntry>> GetLogsAsync(int runId, long afterId = 0, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DiffSnapshot> AddDiffAsync(DiffSnapshot diff, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DiffSnapshot>> GetDiffsAsync(int runId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
