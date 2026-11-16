namespace Factory.Contracts;

public sealed class FactorySettings
{
    public const string LmStudioUrl = "http://localhost:1234/v1";
    public const string OllamaUrl = "http://localhost:11434/v1";

    public LlmProvider Provider { get; set; } = LlmProvider.LmStudio;
    public string BaseUrl { get; set; } = LmStudioUrl;
    public string ModelId { get; set; } = "qwen3.8";

    /// <summary>Fixer loops per subtask before the run pauses for a human.</summary>
    public int MaxFixRetries { get; set; } = 3;

    /// <summary>The most subtasks Hatchet's own planner may plan before the plan waits for the user's approval.</summary>
    public int MaxPlannedSubtasks { get; set; } = 40;

    /// <summary>
    /// Failed fix attempts on the local model before the Bug Fixer hands the subtask to Claude through Claude Code.
    /// Local models often guess library APIs; Claude usually knows them. 0 keeps fixes local.
    /// </summary>
    public int EscalateFixesToClaudeAfter { get; set; } = 1;

    /// <summary>
    /// A fix on the local model still running after this many minutes is abandoned and handed to Claude, since a model
    /// that keeps thinking never fails and so never escalates. Applies only when fixes can escalate. 0 sets no limit.
    /// </summary>
    public double LocalFixTimeLimitMinutes { get; set; } = 90;

    /// <summary>
    /// When an agent on the local model gets stuck in a loop or fills its context window twice, even after a retry
    /// with more varied sampling, Claude takes that call instead of the run pausing. Applies to every agent.
    /// </summary>
    public bool EscalateRunawaysToClaude { get; set; } = true;

    /// <summary>
    /// Runs that may work at once when any of their agents use the local model. It serves one request at a time, so
    /// more runs only take turns with it. Runs past the limit wait in line.
    /// </summary>
    public int MaxParallelLocalRuns { get; set; } = 1;

    /// <summary>Runs that may work at once when every agent runs on Claude. Runs past the limit wait in line.</summary>
    public int MaxParallelClaudeRuns { get; set; } = 4;

    /// <summary>Attempts per LLM call, covering transport errors and unparseable output.</summary>
    public int MaxLlmAttempts { get; set; } = 3;

    public int VerifyTimeoutSeconds { get; set; } = 900;

    /// <summary>Abort an LLM call after this long with no streamed data. Local prompt processing can be slow.</summary>
    public int LlmStallTimeoutSeconds { get; set; } = 900;

    public int MaxFileChars { get; set; } = 40_000;
    public int MaxContextChars { get; set; } = 120_000;
    public int MaxDiffChars { get; set; } = 60_000;
    public int MaxLogChars { get; set; } = 24_000;

    public List<AgentProfileOverride> ProfileOverrides { get; set; } = [];

    /// <summary>
    /// Named choices of model for each agent, which a run picks as <c>profile:&lt;name&gt;</c> in place of the routing
    /// above. Settings saved before run profiles existed have none stored, and so get the built-in ones.
    /// </summary>
    public List<RunProfile> RunProfiles { get; set; } = RunProfile.BuiltIn();

    /// <summary>The Claude Code executable, for roles routed to Claude. A bare name is looked up on PATH.</summary>
    public string ClaudeCodePath { get; set; } = "claude";

    /// <summary>A copy that can be changed without touching these settings, which every run shares.</summary>
    public FactorySettings Clone()
    {
        var copy = (FactorySettings)MemberwiseClone();
        copy.ProfileOverrides = ProfileOverrides.Select(o => o.Clone()).ToList();
        copy.RunProfiles = RunProfiles.Select(p => p.Clone()).ToList();
        return copy;
    }
}

/// <summary>
/// Which model each agent runs on, under a name, so one run can be built locally and the next on Claude without
/// changing Settings in between. A role the profile leaves out runs as Settings has it. Everything else about an
/// agent, such as its temperature and its prompt, stays as configured.
/// </summary>
public sealed class RunProfile
{
    /// <summary>Lowercase letters, digits and hyphens, as it is written after <c>profile:</c>.</summary>
    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    public List<RunProfileRole> Roles { get; set; } = [];

    public RunProfile Clone() => new() { Name = Name, Description = Description, Roles = Roles.Select(r => r.Clone()).ToList() };

    /// <summary>True for a name that can follow <c>profile:</c>: lowercase letters, digits and hyphens, starting with a letter or digit.</summary>
    public static bool IsValidName(string name) =>
        name.Length is > 0 and <= 40 && name[0] != '-' && name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');

    /// <summary>What stops a profile being saved under its name, or null when the name is fine.</summary>
    public static string? NameProblem(RunProfile profile, IReadOnlyList<RunProfile> all)
    {
        var name = profile.Name.Trim();
        if (!IsValidName(name))
            return name.Length == 0
                ? "A run profile needs a name."
                : $"\"{name}\" can't be a run profile's name. Use lowercase letters, digits and hyphens, starting with a letter or digit, and at most 40 characters.";
        return all.Any(other => !ReferenceEquals(other, profile) && other.Name.Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            ? $"Two run profiles are called \"{name}\". Give each its own name."
            : null;
    }

    /// <summary>The profiles a new install starts with. They can be edited or removed in Settings like any other.</summary>
    public static List<RunProfile> BuiltIn() =>
    [
        new()
        {
            Name = "local",
            Description = "Builds on the local model. Claude plans and reviews.",
            Roles =
            [
                OnClaude(AgentRole.TaskPlanner, "opus"), Local(AgentRole.ContextSpecifier), Local(AgentRole.CodeGenerator),
                OnClaude(AgentRole.StaticReviewer, "sonnet"), Local(AgentRole.BugFixer)
            ]
        },
        new()
        {
            // With the Bug Fixer on Sonnet, the last automatic fix attempt goes to Opus by itself.
            Name = "strong",
            Description = "Builds on Claude Sonnet. Claude Opus plans, reviews and makes the last fix attempt.",
            Roles =
            [
                OnClaude(AgentRole.TaskPlanner, "opus"), OnClaude(AgentRole.ContextSpecifier, "sonnet"), OnClaude(AgentRole.CodeGenerator, "sonnet"),
                OnClaude(AgentRole.StaticReviewer, "opus"), OnClaude(AgentRole.BugFixer, "sonnet")
            ]
        },
        new()
        {
            Name = "all-opus",
            Description = "Every agent on Claude Opus.",
            Roles = Enum.GetValues<AgentRole>().Select(role => OnClaude(role, "opus")).ToList()
        }
    ];

    private static RunProfileRole Local(AgentRole role) => new() { Role = role, Engine = AgentEngine.Local };

    private static RunProfileRole OnClaude(AgentRole role, string model) => new() { Role = role, Engine = AgentEngine.ClaudeCode, ClaudeModel = model };
}

/// <summary>Where one agent runs under a <see cref="RunProfile"/>.</summary>
public sealed class RunProfileRole
{
    public AgentRole Role { get; set; }
    public AgentEngine Engine { get; set; }
    public string ClaudeModel { get; set; } = ClaudeCodeOptions.DefaultModel;
    public string ClaudeEffort { get; set; } = ClaudeCodeOptions.DefaultEffort;

    public RunProfileRole Clone() => (RunProfileRole)MemberwiseClone();
}

public sealed class AgentProfileOverride
{
    public AgentRole Role { get; set; }
    public string ReasoningEffort { get; set; } = ReasoningEfforts.Medium;
    public double Temperature { get; set; }
    public AgentEngine Engine { get; set; }
    public string ClaudeModel { get; set; } = ClaudeCodeOptions.DefaultModel;
    public string ClaudeEffort { get; set; } = ClaudeCodeOptions.DefaultEffort;

    /// <summary>A replacement system prompt, or null for the built-in one.</summary>
    public string? SystemPrompt { get; set; }

    public AgentProfileOverride Clone() => (AgentProfileOverride)MemberwiseClone();
}
