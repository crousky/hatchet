namespace Factory.Contracts;

// Structured-output DTOs. Each one has a matching strict JSON Schema in LlmSchemas.

public record TaskGraphResponse(List<SubtaskDto> Subtasks);

public record SubtaskDto(
    int Order,
    string Title,
    string Description,
    List<string> TargetFilesToModify,
    List<string> TargetFilesToCreate,
    List<string> ContextFilesToRead,
    string VerificationCommand,
    List<string>? Skills = null); // Optional when parsing, so an answer from before skills still reads.

public record RefinedSpecResponse(
    string ImplementationInstructions,
    List<string> MethodSignatures,
    List<string> Interfaces,
    List<string> TestCases,
    List<string> EdgeCaseChecks);

public record FilePatchSetResponse(
    string SummaryOfChanges,
    List<FileOperationDto> Operations);

public record FileOperationDto(
    string RelativeFilePath,
    string Action, // "CREATE", "OVERWRITE", "EDIT", "DELETE"
    string FileContent);

public record ReviewVerdictResponse(
    bool Passed,
    int ConfidenceScore,
    List<string> BlockingIssues,
    List<string> Suggestions);

// Requests from the UI into the orchestrator.

public sealed record NewRunRequest(
    string RepositoryPath,
    string ArchitectureSpec,
    string? Title,
    string? VerificationCommand,
    bool RequirePlanApproval,
    string? ModelChoice = null,
    bool PlanReviewByUser = true);

public sealed record SubtaskEdit(
    int? Id,
    string Title,
    string Description,
    List<string> TargetFilesToModify,
    List<string> TargetFilesToCreate,
    List<string> ContextFilesToRead,
    string VerificationCommand,
    List<string>? Skills = null);

/// <summary>Something in a supplied plan that doesn't match the repository. The run starts all the same.</summary>
/// <param name="Kind">One of <see cref="PlanWarningKinds"/>.</param>
/// <param name="Step">The step's position in the plan, starting at 1.</param>
/// <param name="Subject">The skill name or path the warning is about.</param>
public sealed record PlanWarning(string Kind, int Step, string StepTitle, string Subject, string Message);

public static class PlanWarningKinds
{
    public const string UnknownSkill = "unknown_skill";
    public const string FileToModifyMissing = "file_to_modify_missing";
    public const string FileToReadMissing = "file_to_read_missing";
    public const string FileToCreateExists = "file_to_create_exists";
    public const string SecretPath = "secret_path";
    public const string IgnoredPath = "ignored_path";
    public const string InvalidPath = "invalid_path";
}

public sealed record RepositoryProbe(
    bool Exists,
    bool IsGitRepository,
    bool HasCommits,
    bool IsClean,
    string? CurrentBranch,
    DetectedVerification? Verification,
    string? Error);

public sealed record DetectedVerification(string Command, string Description);

public sealed record PullRequestDraft(
    string Title,
    string Body,
    string BranchName,
    string BaseBranch,
    bool HasRemote);

public sealed record ModelEndpointStatus(
    bool Reachable,
    string BaseUrl,
    string ConfiguredModel,
    bool ModelAvailable,
    IReadOnlyList<string> AvailableModels,
    string? Error,
    int? ContextLength = null);
