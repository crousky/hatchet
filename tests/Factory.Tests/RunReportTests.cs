using System.Text.Json;
using Factory.Contracts;
using Factory.Engine.Orchestration;

namespace Factory.Tests;

public sealed class RunReportTests
{
    private const string Diff = """
        diff --git a/src/New.cs b/src/New.cs
        new file mode 100644
        index 0000000..e69de29
        --- /dev/null
        +++ b/src/New.cs
        @@ -0,0 +1,2 @@
        +class New { }
        ++++ b/not/a/file.cs
        diff --git a/src/App.cs b/src/App.cs
        index 1111111..2222222 100644
        --- a/src/App.cs
        +++ b/src/App.cs
        @@ -1 +1 @@
        -old
        +new
        diff --git a/docs/old.md b/docs/old.md
        deleted file mode 100644
        index 3333333..0000000
        --- a/docs/old.md
        +++ /dev/null
        @@ -1 +0,0 @@
        -gone
        diff --git a/a.txt b/b.txt
        similarity index 100%
        rename from a.txt
        rename to b.txt
        diff --git a/logo.png b/logo.png
        index 4444444..5555555 100644
        Binary files a/logo.png and b/logo.png differ
        diff --git "a/with space.txt" "b/with space.txt"
        index 6666666..7777777 100644
        --- "a/with space.txt"
        +++ "b/with space.txt"
        @@ -1 +1 @@
        -a
        +b
        """;

    private static string Verdict(bool passed, params string[] suggestions) =>
        JsonSerializer.Serialize(new ReviewVerdictResponse(passed, 90, [], suggestions.ToList()), LlmSchemas.SerializerOptions);

    private static string AddDiff(string path) => $"diff --git a/{path} b/{path}\nnew file mode 100644\n--- /dev/null\n+++ b/{path}\n@@ -0,0 +1 @@\n+x\n";

    private static string ChangeDiff(string path) => $"diff --git a/{path} b/{path}\nindex 1..2 100644\n--- a/{path}\n+++ b/{path}\n@@ -1 +1 @@\n-x\n+y\n";

    private static string DeleteDiff(string path) => $"diff --git a/{path} b/{path}\ndeleted file mode 100644\n--- a/{path}\n+++ /dev/null\n@@ -1 +0,0 @@\n-x\n";

    [Fact]
    public void The_files_in_a_diff_are_named_with_what_happened_to_each()
    {
        Assert.Equal(
            [
                new RunReportFile("src/New.cs", "added"), new RunReportFile("src/App.cs", "modified"), new RunReportFile("docs/old.md", "deleted"),
                new RunReportFile("b.txt", "renamed"), new RunReportFile("logo.png", "modified"), new RunReportFile("with space.txt", "modified")
            ],
            RunReportBuilder.FilesIn(Diff.ReplaceLineEndings("\n")));
        Assert.Equal(RunReportBuilder.FilesIn(Diff.ReplaceLineEndings("\n")), RunReportBuilder.FilesIn(Diff.ReplaceLineEndings("\r\n")));
        Assert.Empty(RunReportBuilder.FilesIn(""));
    }

    [Fact]
    public void A_report_gives_each_steps_summary_the_skipped_and_unfinished_steps_the_files_and_the_suggestions_nothing_acted_on()
    {
        var run = new FactoryRun { Id = 7, Status = RunStatus.NeedsHuman, BranchName = "factory/run-7", BaseBranch = "main", Task = new FactoryTask { Title = "Add devices" } };
        FactorySubtask[] subtasks =
        [
            new() { Id = 3, Order = 3, Title = "Wire it up", Status = SubtaskStatus.NeedsHuman, SummaryOfChanges = "Half done", ReviewJson = Verdict(false, "Not yet passed") },
            new() { Id = 1, Order = 1, Title = "Add the model", Status = SubtaskStatus.Completed, SummaryOfChanges = " Added Device. ", CommitSha = "aaa", FixAttempts = 2,
                Tier = SubtaskTiers.Strong, ReviewJson = Verdict(true, "Name the index", " ") },
            new() { Id = 2, Order = 2, Title = "Add the docs", Status = SubtaskStatus.Skipped, SummaryOfChanges = "Wrote docs", ReviewJson = Verdict(true, "From before the skip") },
            new() { Id = 4, Order = 4, Title = "Tidy", Status = SubtaskStatus.Pending },
            new() { Id = 5, Order = 5, Title = "Remove the stub", Status = SubtaskStatus.Completed, SummaryOfChanges = "Removed it", CommitSha = "bbb", ReviewJson = "not json" }
        ];
        DiffSnapshot[] diffs =
        [
            // A diff from before the commit, such as one a fix replaced, is not what the step changed.
            new() { Id = 1, RunId = 7, SubtaskId = 1, Stage = PipelineStage.Build, UnifiedDiff = AddDiff("wrong.cs") },
            new() { Id = 2, RunId = 7, SubtaskId = 1, Stage = PipelineStage.Review, CommitSha = "aaa", UnifiedDiff = AddDiff("src/Device.cs") + AddDiff("src/Stub.cs") + ChangeDiff("src/App.cs") },
            new() { Id = 3, RunId = 7, SubtaskId = 5, Stage = PipelineStage.Review, CommitSha = "bbb", UnifiedDiff = DeleteDiff("src/Stub.cs") + ChangeDiff("src/Device.cs") + DeleteDiff("old.md") }
        ];

        var report = RunReportBuilder.Build(run, subtasks, diffs);

        Assert.Equal((7, "Add devices", "NeedsHuman", "factory/run-7", "main"), (report.RunId, report.Title, report.Status, report.Branch, report.BaseBranch));
        Assert.Equal([1, 2, 3, 4, 5], report.Steps.Select(s => s.Order));
        var first = report.Steps[0];
        Assert.Equal(("Added Device.", "aaa", 2, "strong"), (first.Summary, first.Commit, first.FixAttempts, first.Tier));
        Assert.Equal(["src/Device.cs", "src/Stub.cs", "src/App.cs"], first.Files.Select(f => f.Path));
        // Work that isn't in the branch has no summary to pass on.
        Assert.Null(report.Steps[1].Summary);
        Assert.Null(report.Steps[2].Summary);
        Assert.Empty(report.Steps[1].Files);

        Assert.Equal([new RunReportSkipped(2, "Add the docs", "Skipped")], report.Skipped);
        Assert.Equal([(3, "NeedsHuman"), (4, "Pending")], report.Unfinished.Select(u => (u.Order, u.Status)));
        // With no whole-run diff yet, the commits so far are put together: added then changed is added, added then deleted is nothing.
        Assert.Equal(
            [new RunReportFile("src/Device.cs", "added"), new RunReportFile("src/App.cs", "modified"), new RunReportFile("old.md", "deleted")],
            report.FilesChanged);
        Assert.Equal([new RunReportSuggestion(1, "Add the model", "Name the index")], report.UnactionedSuggestions);
    }

    [Fact]
    public void Once_the_final_check_has_passed_the_files_come_from_the_whole_runs_diff()
    {
        var run = new FactoryRun { Id = 1, Status = RunStatus.Merged, IntegrationPassed = true, MergeCommit = "mmm", Task = new FactoryTask { Title = "t" } };
        FactorySubtask[] subtasks = [new() { Id = 1, Order = 1, Title = "a", Status = SubtaskStatus.Completed, CommitSha = "aaa", SummaryOfChanges = "Did a" }];
        DiffSnapshot[] diffs =
        [
            new() { Id = 1, RunId = 1, SubtaskId = 1, Stage = PipelineStage.Review, CommitSha = "aaa", UnifiedDiff = AddDiff("a.txt") },
            new() { Id = 2, RunId = 1, Stage = PipelineStage.Integrate, UnifiedDiff = AddDiff("a.txt") + ChangeDiff("readme.md") }
        ];

        var report = RunReportBuilder.Build(run, subtasks, diffs);

        Assert.Equal(["a.txt", "readme.md"], report.FilesChanged.Select(f => f.Path));
        Assert.Equal((true, "mmm"), (report.FinalCheckPassed, report.MergeCommit));
        Assert.Empty(report.Skipped);
        Assert.Empty(report.Unfinished);
        Assert.Empty(report.UnactionedSuggestions);
    }
}
