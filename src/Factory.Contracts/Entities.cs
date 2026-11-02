namespace Factory.Contracts;

/// <summary>The work item: a spec and a target repository. A task can be run more than once.</summary>
public sealed class FactoryTask
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string RepositoryPath { get; set; } = "";
    public string ArchitectureSpec { get; set; } = "";

    /// <summary>The user's configured verification command; used by integration and any subtask without its own approved command.</summary>
    public string VerificationCommand { get; set; } = "";

    public bool RequirePlanApproval { get; set; } = true;

    /// <summary>The plan handed over with the task, as JSON <see cref="SubtaskEdit"/>s. Null when Hatchet's planner makes the plan.</summary>
    public string? SuppliedPlanJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<FactoryRun> Runs { get; set; } = [];
}

public sealed class FactoryRun
{
    public int Id { get; set; }
    public int TaskId { get; set; }
    public FactoryTask Task { get; set; } = null!;

    public RunStatus Status { get; set; } = RunStatus.Queued;
    public PipelineStage? CurrentStage { get; set; }
    public int? CurrentSubtaskId { get; set; }

    /// <summary>Stage to return to when a run in <see cref="RunStatus.NeedsHuman"/> resumes.</summary>
    public PipelineStage? PausedStage { get; set; }
    public string? PauseReason { get; set; }

    /// <summary>The user paused the run, as opposed to it stopping on a problem.</summary>
    public bool PausedByUser { get; set; }

    public string? BranchName { get; set; }
    public string? BaseBranch { get; set; }
    public string? BaseCommit { get; set; }
    public string RepoMap { get; set; } = "";

    public bool? IntegrationPassed { get; set; }
    public string? MergeCommit { get; set; }

    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }

    public List<FactorySubtask> Subtasks { get; set; } = [];
}

public sealed class FactorySubtask
{
    public int Id { get; set; }
    public int RunId { get; set; }

    public int Order { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> TargetFilesToModify { get; set; } = [];
    public List<string> TargetFilesToCreate { get; set; } = [];
    public List<string> ContextFilesToRead { get; set; } = [];

    /// <summary>Proposed by the planner. Runs only once a human has approved the plan containing it.</summary>
    public string VerificationCommand { get; set; } = "";
    public bool CommandApproved { get; set; }

    public SubtaskStatus Status { get; set; } = SubtaskStatus.Pending;
    public int FixAttempts { get; set; }
    public string? LastFailure { get; set; }

    /// <summary>What each fix attempt changed and what failed next, so later attempts don't repeat it.</summary>
    public List<string> FixHistory { get; set; } = [];
    public string? HumanGuidance { get; set; }

    public string? RefinedSpecJson { get; set; }
    public string? SummaryOfChanges { get; set; }
    public string? ReviewJson { get; set; }
    public string? CommitSha { get; set; }
}

public sealed class AgentLogEntry
{
    public long Id { get; set; }
    public int RunId { get; set; }
    public int? SubtaskId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public AgentLogKind Kind { get; set; }
    public PipelineStage? Stage { get; set; }
    public string Message { get; set; } = "";

    // LLM calls
    public AgentRole? Role { get; set; }
    public string? ReasoningEffort { get; set; }

    /// <summary>The Claude model alias when Claude Code answered the call, or null for the local model.</summary>
    public string? Model { get; set; }
    public double? Temperature { get; set; }
    public string? Prompt { get; set; }
    public string? Thinking { get; set; }
    public string? Response { get; set; }
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }

    // LLM calls and commands
    public long DurationMs { get; set; }
    public bool Succeeded { get; set; } = true;

    // Commands
    public string? Command { get; set; }
    public int? ExitCode { get; set; }
    public string? Output { get; set; }
}

public sealed class DiffSnapshot
{
    public long Id { get; set; }
    public int RunId { get; set; }

    /// <summary>Null for the whole-run diff captured at integration.</summary>
    public int? SubtaskId { get; set; }
    public PipelineStage Stage { get; set; }
    public int Attempt { get; set; }
    public string UnifiedDiff { get; set; } = "";
    public string? CommitSha { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
