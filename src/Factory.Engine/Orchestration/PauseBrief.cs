using System.Text;
using System.Text.Json;
using Factory.Contracts;

namespace Factory.Engine.Orchestration;

public enum PauseKind
{
    /// <summary>The user paused a healthy run.</summary>
    UserPaused,
    /// <summary>Hatchet stopped while the run was working.</summary>
    Interrupted,
    /// <summary>The build and tests pass, but the reviewer still rejects the change and the fix attempts are spent.</summary>
    ReviewStalled,
    /// <summary>The subtask's check still fails and the fix attempts are spent.</summary>
    VerificationStalled,
    /// <summary>Every subtask is committed, but the check over the whole branch fails.</summary>
    FinalCheckFailed,
    /// <summary>A step hit an error that isn't a code problem, such as the model server going away.</summary>
    StepError,
    /// <summary>Anything before building starts, such as an unclean working tree.</summary>
    Other
}

/// <param name="Action">The <c>resume_run</c> action this option is, so a person or an MCP client can map it to a button or a call.</param>
/// <param name="When">When this is the right choice.</param>
/// <param name="YouProvide">What the person has to supply or do for it to work. "Nothing" when there's nothing.</param>
public sealed record PauseOption(string Action, string Label, string When, string YouProvide);

/// <summary>What a run that needs a person is waiting for: what happened, what is blocking it, and what each way forward needs from them.</summary>
public sealed record PauseBrief(
    PauseKind Kind,
    string Headline,
    string Why,
    string Ask,
    string? FindingsLabel,
    IReadOnlyList<string> Findings,
    string GuidanceHint,
    IReadOnlyList<PauseOption> Options)
{
    /// <summary>Kinds where something has actually gone wrong, as opposed to a run that only needs a nudge to carry on.</summary>
    public bool NeedsDecision => Kind is not (PauseKind.UserPaused or PauseKind.Interrupted);

    public string ToText()
    {
        var builder = new StringBuilder();
        builder.Append(Headline).Append("\n\n").Append(Why).Append("\n\n");
        if (Findings.Count > 0)
        {
            builder.Append(FindingsLabel ?? "Details").Append(":\n");
            foreach (var finding in Findings) builder.Append("- ").Append(finding).Append('\n');
            builder.Append('\n');
        }
        builder.Append("What is needed from you: ").Append(Ask).Append("\n\nOptions:\n");
        foreach (var option in Options)
            builder.Append("- ").Append(option.Label).Append(" (").Append(option.Action).Append("): ").Append(option.When)
                .Append(" You provide: ").Append(option.YouProvide).Append('\n');
        builder.Append("\nGuidance to give: ").Append(GuidanceHint);
        return builder.ToString();
    }
}

public static class PauseBriefBuilder
{
    private const string ReviewStalledFragment = "since the reviewer first rejected it";

    /// <summary>The reason recorded when the reviewer keeps rejecting a subtask and its fix attempts run out.</summary>
    public static string ReviewStalledReason(string title, int used) =>
        $"\"{title}\" still isn't accepted after {used} fix attempts {ReviewStalledFragment}.";

    /// <summary>The reason recorded when a subtask's check keeps failing and its fix attempts run out.</summary>
    public static string VerificationStalledReason(string title, int used) =>
        $"\"{title}\" still fails after {used} fix attempts.";

    private const string RepeatedFailureFragment = "fails with the same error after ";
    private const string RefusedFixesFragment = "were refused in a row";

    /// <summary>The reason recorded when the last two fixes ended in the same error, so Hatchet stops instead of trying a third time.</summary>
    public static string RepeatedFailureReason(string title, int attempts) =>
        $"\"{title}\" {RepeatedFailureFragment}{attempts} fix attempts, so Hatchet stopped trying.";

    /// <summary>The reason recorded when the guard refuses one fix after another for taking out something the specification asks for.</summary>
    public static string RefusedFixesReason(string title, int refused) =>
        $"{refused} fixes for \"{title}\" {RefusedFixesFragment}, because each took out something the specification asks for.";

    private static bool IsRepeatedFailure(string reason) => reason.Contains(RepeatedFailureFragment, StringComparison.Ordinal);

    private static bool IsRefusedFixes(string reason) => reason.Contains(RefusedFixesFragment, StringComparison.Ordinal);

    private static bool IsReviewStall(string reason) => reason.EndsWith(ReviewStalledFragment + ".", StringComparison.Ordinal);

    private static bool IsContextOverflow(string reason) =>
        reason.Contains("exceed_context_size", StringComparison.OrdinalIgnoreCase)
        || reason.Contains("exceeds the available context", StringComparison.OrdinalIgnoreCase)
        || reason.Contains("context length", StringComparison.OrdinalIgnoreCase);

    private static bool IsVerificationStall(string reason) =>
        reason.Contains("still fails after ", StringComparison.Ordinal) && reason.EndsWith(" fix attempts.", StringComparison.Ordinal);

    /// <summary>Null unless the run is waiting for a person. <paramref name="paused"/> is the subtask it stopped on, if any.</summary>
    public static PauseBrief? Build(FactoryRun run, FactorySubtask? paused)
    {
        if (run.Status != RunStatus.NeedsHuman) return null;
        var reason = run.PauseReason ?? "";

        if (run.PausedByUser)
            return Simple(PauseKind.UserPaused, "You paused this run.", "Nothing is wrong. The run stopped because you asked it to.",
                "Resume when you're ready. It carries on from the step it was on.", run, paused);

        if (reason == FactoryOrchestrator.InterruptedReason)
            return Simple(PauseKind.Interrupted, "Hatchet stopped while this run was working.",
                "Nothing failed. Hatchet was closed or restarted partway through, and the run was put on hold.",
                "Resume to pick up where it stopped. Fix attempts already used are kept.", run, paused);

        if (run.PausedStage == PipelineStage.Integrate) return FinalCheck(run, reason);
        if (paused is null) return Other(run, reason);

        if (IsReviewStall(reason)) return ReviewStalled(run, paused);
        if (IsRepeatedFailure(reason)) return RepeatedFailure(run, paused);
        if (IsRefusedFixes(reason)) return RefusedFixes(run, paused);
        if (IsVerificationStall(reason)) return VerificationStalled(run, paused);
        return StepError(run, paused, reason);
    }

    private static PauseBrief Simple(PauseKind kind, string headline, string why, string ask, FactoryRun run, FactorySubtask? paused)
    {
        var options = new List<PauseOption> { new("resume", ResumeLabel(run), "You're ready to carry on.", "Nothing.") };
        if (paused is not null) options.AddRange(RestartAndSkip(paused));
        return new PauseBrief(kind, headline, why, ask, null, [], "Nothing is needed. Add guidance only if you want to steer the agents.", options);
    }

    private static PauseBrief ReviewStalled(FactoryRun run, FactorySubtask paused)
    {
        var verdict = ParseVerdict(paused.ReviewJson);
        var issues = verdict?.BlockingIssues.Where(i => !string.IsNullOrWhiteSpace(i)).ToList() ?? [];
        var findings = issues.Count > 0 ? issues : ["The reviewer's verdict couldn't be read. Open the Subtask tab to see it."];
        var tries = paused.ReviewFixAttempts;
        var confidence = verdict is null ? "" : $" The latest verdict is {verdict.ConfidenceScore}% confident.";

        return new PauseBrief(
            PauseKind.ReviewStalled,
            $"The reviewer and the Bug Fixer are deadlocked on \"{paused.Title}\".",
            $"The build and tests pass, but the reviewer rejected the change and {tries} fix {(tries == 1 ? "attempt has" : "attempts have")} not got it accepted. Hatchet has used up its attempts and won't keep looping on its own.{confidence}",
            "Decide whether the reviewer is right. If it is, tell the fixer how to resolve each point. If it's being too strict or has misread the spec, say why each point doesn't matter. Or make the change yourself.",
            "What the reviewer still objects to (latest verdict)",
            findings,
            "Answer each blocking issue: how to fix it, or why it is acceptable. Every agent working on this subtask, the reviewer included, follows this over the original spec.",
            [.. RetryOptions(run, paused, "You know why the fixes aren't landing, or you think the reviewer's objection should be overruled.",
                "Guidance in the box above that answers each blocking issue: the root cause or approach to take, or why the objection doesn't apply. The fixer gets a fresh budget of attempts.")]);
    }

    private static PauseBrief VerificationStalled(FactoryRun run, FactorySubtask paused)
    {
        var failure = paused.LastFailure ?? "";
        var errors = ErrorLines(failure);
        var summary = failure.Split('\n', 2)[0].Trim();
        var findings = errors.Count > 0 ? errors : summary.Length > 0 ? [summary] : [];

        return new PauseBrief(
            PauseKind.VerificationStalled,
            $"\"{paused.Title}\" still fails its check.",
            $"The Bug Fixer used its attempts and the check still fails.{(summary.Length > 0 ? " Last failure: " + summary : "")}",
            "Work out what the errors really come from and tell the fixer, or fix it yourself. The fixer has already tried the obvious.",
            errors.Count > 0 ? "Errors from the last run" : "Last failure",
            findings,
            "Name the root cause or the approach to take, such as \"each test needs its own database\" or \"the helper lives in Core, not App\".",
            [.. RetryOptions(run, paused, "You can see what the fixer keeps getting wrong.",
                "Guidance in the box above that names the root cause or the approach. The fixer gets a fresh budget of attempts.")]);
    }

    private static PauseBrief RepeatedFailure(FactoryRun run, FactorySubtask paused)
    {
        var failure = paused.LastFailure ?? "";
        var errors = ErrorLines(failure);
        var summary = failure.Split('\n', 2)[0].Trim();
        return new PauseBrief(
            PauseKind.VerificationStalled,
            $"\"{paused.Title}\" keeps failing with the same error.",
            "Two fixes in a row ended in the same error, and Claude has had its turn if it is set up. A third would most likely do the same, so Hatchet stopped to save time and tokens. "
            + "This usually means the cause isn't in the code the fixer can change: a missing download or tool, no network, a file it can't see, or a wrong assumption in the spec."
            + (summary.Length > 0 ? " Last failure: " + summary : ""),
            "Look at the error for something outside the code: a missing file, library or tool, or a setting. Fix that and re-verify, or tell the fixer what is really wrong.",
            errors.Count > 0 ? "The error that kept coming back" : "Last failure",
            errors.Count > 0 ? errors : summary.Length > 0 ? [summary] : [],
            "Say what the error really comes from, such as \"the libraries must be downloaded by tools/Update-JsLibs.ps1, not written by hand\".",
            [.. RetryOptions(run, paused, "You've found what the fixer can't see or do.",
                "Fix it in the repository yourself and choose re-verify, or give guidance that names the real cause. The fixer gets a fresh budget of attempts.")]);
    }

    private static PauseBrief RefusedFixes(FactoryRun run, FactorySubtask paused)
    {
        var failure = paused.LastFailure ?? "";
        var summary = failure.Split('\n', 2)[0].Trim();
        return new PauseBrief(
            PauseKind.VerificationStalled,
            $"The fixer can't fix \"{paused.Title}\" without dropping something the spec asks for.",
            "Each fix took out a type the specification names, to get past an error in how it was used. Hatchet refused them all and left the code as it was. These refused fixes didn't use up the fix attempts."
            + (summary.Length > 0 ? " " + summary : ""),
            "Tell the fixer how that type should be used instead, or fix the usage yourself. If the spec is wrong about it, say so in the guidance.",
            null,
            [],
            "Name the type and how it should be used: which members to set, and where it lives.",
            [.. RetryOptions(run, paused, "You know how the type has to be used.",
                "Guidance in the box above, or your own fix in the repository followed by re-verify. The fixer gets a fresh budget of attempts.")]);
    }

    private static PauseBrief StepError(FactoryRun run, FactorySubtask paused, string reason) => new(
        PauseKind.StepError,
        $"A step on \"{paused.Title}\" hit an error that isn't a code problem.",
        $"Hatchet stopped on an error rather than a failing check, so the Bug Fixer has nothing to fix: {reason}",
        IsContextOverflow(reason)
            ? "The model server's context window is too small for this step's prompt. Reload the model with a larger context length (LM Studio: the model's Context Length setting; Ollama: OLLAMA_CONTEXT_LENGTH=32768 or higher), then resume. No code changes are needed."
            : "Fix the cause, then resume. If the model server went away, start it again and check its address in Settings. No code changes are needed.",
        "The error",
        [reason],
        "Nothing is needed once the cause is fixed. Add guidance only if you want to steer the agents.",
        [.. StepErrorOptions(paused)]);

    private static PauseBrief FinalCheck(FactoryRun run, string reason)
    {
        var summary = reason.Split('\n', 2)[0].Trim();
        var errors = ErrorLines(reason);
        return new PauseBrief(
            PauseKind.FinalCheckFailed,
            "Every subtask is committed, but the final check failed.",
            $"{summary} Each subtask passed on its own, so something breaks when the pieces come together.",
            "Hand the failure to the Bug Fixer, or fix it on the run's branch yourself and run the final check again.",
            errors.Count > 0 ? "Errors from the final check" : "The failing check",
            errors.Count > 0 ? errors : [summary],
            "Optional, for the Bug Fixer: what you think breaks, such as \"the settings window uses a member the hotkey code never declared\".",
            [
                new PauseOption("retry_fixer", "Hand to the Fixer",
                    "You'd rather an agent finds and fixes it.",
                    "Optionally, guidance in the box above. The Bug Fixer gets the errors and a fresh budget as one more subtask, which is reviewed and committed like the others, and then the final check runs again."),
                new PauseOption("resume", "Run the final check again", "You've fixed the problem yourself.",
                    $"A fix committed on {run.BranchName ?? "the run's branch"} in {run.Task.RepositoryPath}. Uncommitted changes aren't part of the check. The Log tab shows the command's output.")
            ]);
    }

    private static PauseBrief Other(FactoryRun run, string reason) => new(
        PauseKind.Other,
        "This run stopped before it started building.",
        reason.Length > 0 ? reason : "No reason was recorded.",
        $"Fix what the message says, then {ResumeLabel(run)}.",
        null,
        [],
        "Nothing is needed. Add guidance only if you want to steer the agents.",
        [new PauseOption("resume", ResumeLabel(run), "The problem above is fixed.", "Nothing.")]);

    // The options mirror the buttons on the run page, which depend on how far the subtask got.
    private static IEnumerable<PauseOption> RetryOptions(FactoryRun run, FactorySubtask paused, string retryWhen, string retryProvide)
    {
        if (paused.LastFailure is not null)
            yield return new PauseOption("retry_fixer", "Retry with Fixer", retryWhen, retryProvide);
        if (paused.SummaryOfChanges is not null)
            yield return new PauseOption("reverify", "I fixed it, re-verify", "It is quicker to make the change yourself.",
                $"Edit the files in {run.Task.RepositoryPath} (branch {run.BranchName ?? "of the run"}) and leave the changes uncommitted. Build, tests and review run again on what is there.");
        else
            yield return new PauseOption("resume", "Resume", "You want to continue this subtask from where it stopped.", "Nothing.");
        foreach (var option in RestartAndSkip(paused)) yield return option;
    }

    private static IEnumerable<PauseOption> StepErrorOptions(FactorySubtask paused)
    {
        yield return paused.SummaryOfChanges is not null
            ? new PauseOption("reverify", "I fixed it, re-verify", "The cause is fixed.", "Nothing. Build, tests and review run again on the work so far.")
            : new PauseOption("resume", "Resume", "The cause is fixed.", "Nothing. The subtask continues from where it stopped.");
        foreach (var option in RestartAndSkip(paused)) yield return option;
    }

    private static IEnumerable<PauseOption> RestartAndSkip(FactorySubtask paused)
    {
        yield return new PauseOption("restart_subtask", "Restart subtask", "The approach itself is wrong, so more fixing won't help.",
            "Optionally, guidance describing the approach to take instead. This discards the subtask's uncommitted changes and builds it again from the start.");
        yield return new PauseOption("skip_subtask", "Skip subtask", "Later subtasks don't need it, or you'll do it by hand.",
            "Nothing. This discards the subtask's uncommitted changes, and later subtasks that depend on it may fail.");
    }

    private static string ResumeLabel(FactoryRun run) => run.PausedStage switch
    {
        PipelineStage.Ingest => "Scan again",
        PipelineStage.Decompose when !run.PausedByUser && run.Task.SuppliedPlanJson is null => "Plan again",
        PipelineStage.Integrate => "Run the final check again",
        _ => "Resume"
    };

    private static ReviewVerdictResponse? ParseVerdict(string? json)
    {
        if (json is null) return null;
        try
        {
            return JsonSerializer.Deserialize<ReviewVerdictResponse>(json, LlmSchemas.SerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The lines under "Errors:" in a recorded failure, which hold the compiler or test errors.</summary>
    private static List<string> ErrorLines(string failure)
    {
        var lines = failure.Split('\n');
        var start = Array.FindIndex(lines, l => l.Trim() == "Errors:");
        if (start < 0) return [];
        return lines.Skip(start + 1).TakeWhile(l => l.Trim().Length > 0).Select(l => l.Trim()).Take(8).ToList();
    }
}
