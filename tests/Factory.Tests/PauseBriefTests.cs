using Factory.Contracts;
using Factory.Engine.Orchestration;

namespace Factory.Tests;

public class PauseBriefTests
{
    private static FactoryRun Run(string? reason, PipelineStage? stage = PipelineStage.Review, bool byUser = false) => new()
    {
        Status = RunStatus.NeedsHuman,
        PauseReason = reason,
        PausedStage = stage,
        PausedByUser = byUser,
        BranchName = "factory/run-1",
        Task = new FactoryTask { RepositoryPath = @"C:\projects\demo" }
    };

    private static FactorySubtask Subtask(string? lastFailure = null, bool built = true, string? reviewJson = null, int reviewFixes = 0) => new()
    {
        Title = "Vendor the libraries",
        Status = SubtaskStatus.NeedsHuman,
        LastFailure = lastFailure ?? "something failed",
        SummaryOfChanges = built ? "Added the files" : null,
        ReviewJson = reviewJson,
        ReviewFixAttempts = reviewFixes
    };

    private const string Verdict = """{"passed":false,"confidenceScore":93,"blockingIssues":["The bundle has no license","The script runs unpinned downloads"],"suggestions":["Rename it"]}""";

    [Fact]
    public void Returns_nothing_for_a_run_that_is_not_waiting()
    {
        var run = Run("x");
        run.Status = RunStatus.Executing;
        Assert.Null(PauseBriefBuilder.Build(run, null));
    }

    [Fact]
    public void A_stalled_review_lists_what_the_reviewer_objects_to_and_what_to_provide()
    {
        var subtask = Subtask(reviewJson: Verdict, reviewFixes: 3);
        var brief = PauseBriefBuilder.Build(Run(PauseBriefBuilder.ReviewStalledReason(subtask.Title, 3)), subtask)!;

        Assert.Equal(PauseKind.ReviewStalled, brief.Kind);
        Assert.True(brief.NeedsDecision);
        Assert.Equal(["The bundle has no license", "The script runs unpinned downloads"], brief.Findings);
        Assert.Contains("3 fix attempts have", brief.Why);
        Assert.Contains("93%", brief.Why);
        Assert.Equal(["retry_fixer", "reverify", "restart_subtask", "skip_subtask"], brief.Options.Select(o => o.Action));

        var reverify = brief.Options.Single(o => o.Action == "reverify");
        Assert.Contains(@"C:\projects\demo", reverify.YouProvide);
        Assert.Contains("factory/run-1", reverify.YouProvide);
    }

    [Fact]
    public void A_stalled_review_with_an_unreadable_verdict_still_points_somewhere()
    {
        var subtask = Subtask(reviewJson: "not json");
        var brief = PauseBriefBuilder.Build(Run(PauseBriefBuilder.ReviewStalledReason(subtask.Title, 3)), subtask)!;

        Assert.Equal(PauseKind.ReviewStalled, brief.Kind);
        Assert.Single(brief.Findings);
        Assert.Contains("Subtask tab", brief.Findings[0]);
    }

    [Fact]
    public void A_failing_check_shows_the_errors_not_the_whole_output()
    {
        var failure = "`dotnet build` failed with exit code 1.\n\nErrors:\nApp.cs(1,1): error CS0103: no such name\nBuild FAILED.\n\nOutput:\nlots of noise\nmore noise";
        var subtask = Subtask(failure);
        var brief = PauseBriefBuilder.Build(Run(PauseBriefBuilder.VerificationStalledReason(subtask.Title, 3), PipelineStage.Verify), subtask)!;

        Assert.Equal(PauseKind.VerificationStalled, brief.Kind);
        Assert.Equal(["App.cs(1,1): error CS0103: no such name", "Build FAILED."], brief.Findings);
        Assert.Contains("`dotnet build` failed with exit code 1.", brief.Why);
    }

    [Fact]
    public void An_error_outside_the_code_says_no_code_change_is_needed()
    {
        var subtask = Subtask();
        var brief = PauseBriefBuilder.Build(Run("The model server refused the connection."), subtask)!;

        Assert.Equal(PauseKind.StepError, brief.Kind);
        Assert.Equal(["The model server refused the connection."], brief.Findings);
        Assert.Contains("No code changes are needed", brief.Ask);
        Assert.DoesNotContain(brief.Options, o => o.Action == "retry_fixer");
    }

    [Fact]
    public void A_context_overflow_tells_the_user_to_raise_the_model_context_length()
    {
        var reason = "The model server reported an error: request (10073 tokens) exceeds the available context size (8192 tokens), try increasing it";
        var brief = PauseBriefBuilder.Build(Run(reason, PipelineStage.Refine), Subtask(built: false))!;

        Assert.Equal(PauseKind.StepError, brief.Kind);
        Assert.Contains("larger context length", brief.Ask);
        Assert.DoesNotContain("start it again", brief.Ask);
    }

    [Fact]
    public void A_subtask_that_was_never_built_offers_resume_instead_of_reverify()
    {
        var subtask = Subtask(built: false);
        var brief = PauseBriefBuilder.Build(Run(PauseBriefBuilder.VerificationStalledReason(subtask.Title, 3), PipelineStage.Build), subtask)!;

        Assert.Contains(brief.Options, o => o.Action == "resume");
        Assert.DoesNotContain(brief.Options, o => o.Action == "reverify");
    }

    [Fact]
    public void A_failed_final_check_says_the_fix_must_be_committed()
    {
        var brief = PauseBriefBuilder.Build(Run("The final check failed: `dotnet test` exited with code 1.", PipelineStage.Integrate), null)!;

        Assert.Equal(PauseKind.FinalCheckFailed, brief.Kind);
        var option = Assert.Single(brief.Options);
        Assert.Equal("Run the final check again", option.Label);
        Assert.Contains("committed", option.YouProvide);
    }

    [Fact]
    public void A_run_the_user_paused_or_that_was_interrupted_needs_no_decision()
    {
        var paused = PauseBriefBuilder.Build(Run(FactoryOrchestrator.PausedByUserReason, byUser: true), Subtask())!;
        var interrupted = PauseBriefBuilder.Build(Run(FactoryOrchestrator.InterruptedReason), Subtask())!;

        Assert.Equal(PauseKind.UserPaused, paused.Kind);
        Assert.False(paused.NeedsDecision);
        Assert.Equal(PauseKind.Interrupted, interrupted.Kind);
        Assert.False(interrupted.NeedsDecision);
        Assert.Equal("resume", interrupted.Options[0].Action);
    }

    [Fact]
    public void A_pause_before_building_names_what_resuming_does()
    {
        var brief = PauseBriefBuilder.Build(Run("The working tree has uncommitted changes.", PipelineStage.Ingest), null)!;

        Assert.Equal(PauseKind.Other, brief.Kind);
        Assert.Equal("Scan again", Assert.Single(brief.Options).Label);
        Assert.Contains("uncommitted changes", brief.Why);
    }

    [Fact]
    public void The_text_form_includes_every_part()
    {
        var subtask = Subtask(reviewJson: Verdict, reviewFixes: 3);
        var text = PauseBriefBuilder.Build(Run(PauseBriefBuilder.ReviewStalledReason(subtask.Title, 3)), subtask)!.ToText();

        Assert.Contains("The bundle has no license", text);
        Assert.Contains("What is needed from you:", text);
        Assert.Contains("(retry_fixer)", text);
        Assert.Contains("Guidance to give:", text);
    }
}
