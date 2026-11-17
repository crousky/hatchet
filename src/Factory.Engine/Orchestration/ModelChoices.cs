using Factory.Contracts;

namespace Factory.Engine.Orchestration;

/// <summary>One model someone can pick for a piece of work. <see cref="Value"/> is what gets stored, and empty means "as set in Settings".</summary>
public sealed record ModelOption(string Value, string Label, string Group);

/// <summary>
/// Which model does a piece of work, picked when it starts instead of in Settings: <c>local:&lt;model id&gt;</c> puts every agent
/// on that model of the local server, <c>claude:&lt;opus|sonnet|haiku&gt;</c> puts every agent on that Claude model through Claude
/// Code, <c>agy:&lt;model id&gt;</c> puts every agent on that model through the Antigravity CLI, and <c>profile:&lt;name&gt;</c>
/// gives each agent the model a run profile from Settings names for it. No choice means the agents run as Settings says.
/// Everything else about each agent, such as its effort, its temperature and its prompt, stays as configured.
/// </summary>
public static class ModelChoices
{
    private const string LocalPrefix = "local:";
    private const string ClaudePrefix = "claude:";
    private const string ProfilePrefix = "profile:";
    private const string AgyPrefix = "agy:";

    public static string Local(string modelId) => LocalPrefix + modelId;

    public static string Claude(string alias) => ClaudePrefix + alias;

    public static string Profile(string name) => ProfilePrefix + name;
    public static string Antigravity(string modelId) => AgyPrefix + modelId;

    /// <summary>
    /// The canonical form of a choice, or null for "as set in Settings". Plain Claude names such as "opus" are accepted as a shorthand.
    /// Throws <see cref="FactoryValidationException"/> for anything else it can't make sense of. A profile is checked for its
    /// spelling only: <see cref="Require"/> also checks that Settings has it.
    /// </summary>
    public static string? Normalize(string? choice)
    {
        var text = choice?.Trim();
        if (string.IsNullOrEmpty(text) || text.Equals("settings", StringComparison.OrdinalIgnoreCase) || text.Equals("default", StringComparison.OrdinalIgnoreCase)) return null;

        if (text.StartsWith(LocalPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var id = text[LocalPrefix.Length..].Trim();
            return id.Length > 0 ? Local(id) : throw new FactoryValidationException("Say which local model, as local:<model id>.");
        }

        if (text.StartsWith(ProfilePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var name = text[ProfilePrefix.Length..].Trim().ToLowerInvariant();
            return RunProfile.IsValidName(name)
                ? Profile(name)
                : throw new FactoryValidationException("Say which run profile, as profile:<name>. A name is lowercase letters, digits and hyphens.");
        }

        if (text.StartsWith(AgyPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var id = text[AgyPrefix.Length..].Trim().ToLowerInvariant();
            if (id.Length == 0) throw new FactoryValidationException("Say which Antigravity model, as agy:<model id>.");
            return AntigravityOptions.IsValidModelId(id)
                ? Antigravity(id)
                : throw new FactoryValidationException($"\"{choice}\" isn't a valid Antigravity model id.");
        }

        if (AntigravityOptions.Models.Contains(text.ToLowerInvariant()))
            return Antigravity(text.ToLowerInvariant());

        var alias = (text.StartsWith(ClaudePrefix, StringComparison.OrdinalIgnoreCase) ? text[ClaudePrefix.Length..] : text).Trim().ToLowerInvariant();
        return ClaudeCodeOptions.Models.Contains(alias)
            ? Claude(alias)
            : throw new FactoryValidationException($"\"{choice}\" isn't a model choice. Use local:<model id>, or claude:{string.Join(", claude:", ClaudeCodeOptions.Models)}, or agy:<model id>, or profile:<name> for a run profile from Settings, or leave it out to use Settings.");
    }

    /// <summary>
    /// <see cref="Normalize"/>, and a profile must be one Settings has. Work is started with this, so a mistyped profile
    /// is refused there and then, not found out when the run reaches its first agent.
    /// </summary>
    public static string? Require(FactorySettings settings, string? choice)
    {
        var normalized = Normalize(choice);
        if (ProfileNameOf(normalized) is { } name) FindProfile(settings, name);
        return normalized;
    }

    /// <summary>The Claude model a choice names, or null when the choice is Settings, a local model, Antigravity or a profile.</summary>
    public static string? ClaudeModelOf(string? choice) =>
        Normalize(choice) is { } normalized && normalized.StartsWith(ClaudePrefix, StringComparison.Ordinal) ? normalized[ClaudePrefix.Length..] : null;

    /// <summary>The Antigravity model a choice names, or null when the choice is Settings, a local model, Claude or a profile.</summary>
    public static string? AntigravityModelOf(string? choice) =>
        Normalize(choice) is { } normalized && normalized.StartsWith(AgyPrefix, StringComparison.Ordinal) ? normalized[AgyPrefix.Length..] : null;

    /// <summary>The run profile a choice names, or null when it names none.</summary>
    public static string? ProfileNameOf(string? choice) =>
        Normalize(choice) is { } normalized && normalized.StartsWith(ProfilePrefix, StringComparison.Ordinal) ? normalized[ProfilePrefix.Length..] : null;

    /// <summary>In words, for the places a choice is shown. Null when the choice is Settings.</summary>
    public static string? Describe(string? choice)
    {
        if (Normalize(choice) is not { } normalized) return null;
        if (normalized.StartsWith(ProfilePrefix, StringComparison.Ordinal)) return $"{normalized[ProfilePrefix.Length..]} profile";
        if (normalized.StartsWith(ClaudePrefix, StringComparison.Ordinal))
            return ClaudeCodeOptions.ModelLabel(normalized[ClaudePrefix.Length..]);
        if (normalized.StartsWith(AgyPrefix, StringComparison.Ordinal))
            return AntigravityOptions.ModelLabel(normalized[AgyPrefix.Length..]);
        return $"{normalized[LocalPrefix.Length..]} (local)";
    }

    /// <summary>
    /// What a choice does to the agents, as a sentence: one model for all of them, or a profile's model for each. Null when the
    /// choice is Settings. A profile Settings no longer has is said to be missing.
    /// </summary>
    public static string? Explain(FactorySettings settings, string? choice)
    {
        if (Describe(choice) is not { } name) return null;
        if (ProfileNameOf(choice) is not { } profileName) return $"Every agent in the run uses {name}.";
        var profile = settings.RunProfiles.FirstOrDefault(p => p.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase));
        return profile is null ? $"Settings has no run profile called {profileName}." : $"{Summarize(profile)}.";
    }

    /// <summary>Which agents a profile puts on which model, such as "Task Planner, Static Reviewer: Claude Opus · Code Generator: local model".</summary>
    public static string Summarize(RunProfile profile)
    {
        var groups = AgentProfiles.Defaults
            .Select(agent => (agent.DisplayName, Route: profile.Roles.FirstOrDefault(r => r.Role == agent.Role)))
            .GroupBy(a => a.Route is null
                ? "as set in Settings"
                : a.Route.Engine == AgentEngine.ClaudeCode
                    ? ClaudeCodeOptions.ModelLabel(a.Route.ClaudeModel)
                    : a.Route.Engine == AgentEngine.Antigravity
                        ? AntigravityOptions.ModelLabel(a.Route.AntigravityModel)
                        : "local model")
            .Select(g => $"{string.Join(", ", g.Select(a => a.DisplayName))}: {g.Key}");
        return string.Join(" · ", groups);
    }

    /// <summary>
    /// The settings a piece of work runs with: the given ones, with every agent moved to the chosen model, or to the model the
    /// chosen profile names for it. The given settings are left as they were, since they are shared by every run. Throws
    /// <see cref="FactoryValidationException"/> when the choice is a profile Settings no longer has.
    /// </summary>
    public static FactorySettings Apply(FactorySettings settings, string? choice)
    {
        if (Normalize(choice) is not { } normalized) return settings;

        var copy = settings.Clone();
        if (normalized.StartsWith(ProfilePrefix, StringComparison.Ordinal))
        {
            foreach (var route in FindProfile(settings, normalized[ProfilePrefix.Length..]).Roles) Route(copy, settings, route);
            return copy;
        }

        var onClaude = normalized.StartsWith(ClaudePrefix, StringComparison.Ordinal);
        var onAgy = normalized.StartsWith(AgyPrefix, StringComparison.Ordinal);
        var prefix = onClaude ? ClaudePrefix : onAgy ? AgyPrefix : LocalPrefix;
        var model = normalized[prefix.Length..];
        if (!onClaude && !onAgy) copy.ModelId = model;

        foreach (var role in Enum.GetValues<AgentRole>())
        {
            var profileOverride = OverrideFor(copy, settings, role);
            profileOverride.Engine = onClaude ? AgentEngine.ClaudeCode : onAgy ? AgentEngine.Antigravity : AgentEngine.Local;
            if (onClaude) profileOverride.ClaudeModel = model;
            if (onAgy) profileOverride.AntigravityModel = model;
        }
        return copy;
    }

    /// <summary>The run profile a strong subtask takes its models from.</summary>
    public const string StrongProfileName = "strong";

    /// <summary>The agents that build a subtask, which are the ones its tier moves. The planner and the reviewer work for the whole run.</summary>
    public static readonly IReadOnlyList<AgentRole> TieredRoles = [AgentRole.ContextSpecifier, AgentRole.CodeGenerator, AgentRole.BugFixer];

    /// <summary>
    /// The "strong" profile as Settings has it, so the user decides what strong means. If they removed it, the one
    /// Hatchet ships with is used: a plan that marks a step strong was written expecting it to exist.
    /// </summary>
    public static RunProfile StrongProfile(FactorySettings settings) =>
        settings.RunProfiles.FirstOrDefault(p => p.Name.Equals(StrongProfileName, StringComparison.OrdinalIgnoreCase))
        ?? RunProfile.BuiltIn().First(p => p.Name == StrongProfileName);

    /// <summary>
    /// The settings one subtask is built with: the run's, and for a strong subtask its building agents moved to where
    /// the "strong" profile puts them. The run's settings are left as they were.
    /// </summary>
    public static FactorySettings ApplyTier(FactorySettings settings, string? tier)
    {
        if (!SubtaskTiers.IsStrong(tier)) return settings;

        var copy = settings.Clone();
        foreach (var route in StrongProfile(settings).Roles.Where(r => TieredRoles.Contains(r.Role))) Route(copy, settings, route);
        return copy;
    }

    /// <summary>Where a strong subtask's building agents run, such as "Context Specifier, Code Generator, Bug Fixer: Claude Sonnet".</summary>
    public static string DescribeTier(FactorySettings settings)
    {
        var applied = ApplyTier(settings, SubtaskTiers.Strong);
        return string.Join(" · ", TieredRoles
            .Select(role => AgentProfiles.Resolve(applied, role))
            .GroupBy(p => p.UsesClaude ? ClaudeCodeOptions.ModelLabel(p.ClaudeModel) : p.UsesAntigravity ? AntigravityOptions.ModelLabel(p.AntigravityModel) : "local model")
            .Select(g => $"{string.Join(", ", g.Select(p => p.DisplayName))}: {g.Key}"));
    }

    private static void Route(FactorySettings copy, FactorySettings settings, RunProfileRole route)
    {
        var profileOverride = OverrideFor(copy, settings, route.Role);
        profileOverride.Engine = route.Engine;
        if (route.Engine == AgentEngine.ClaudeCode)
        {
            profileOverride.ClaudeModel = route.ClaudeModel;
            profileOverride.ClaudeEffort = route.ClaudeEffort;
        }
        else if (route.Engine == AgentEngine.Antigravity)
        {
            profileOverride.AntigravityModel = route.AntigravityModel;
        }
    }

    /// <summary>The role's override in the copy, added with the role's settings as they stand if it had none.</summary>
    private static AgentProfileOverride OverrideFor(FactorySettings copy, FactorySettings settings, AgentRole role)
    {
        if (copy.ProfileOverrides.FirstOrDefault(o => o.Role == role) is { } existing) return existing;

        var profile = AgentProfiles.Resolve(settings, role);
        var added = new AgentProfileOverride
        {
            Role = role, ReasoningEffort = profile.ReasoningEffort, Temperature = profile.Temperature,
            ClaudeModel = profile.ClaudeModel, ClaudeEffort = profile.ClaudeEffort,
            AntigravityModel = profile.AntigravityModel
        };
        copy.ProfileOverrides.Add(added);
        return added;
    }

    private static RunProfile FindProfile(FactorySettings settings, string name) =>
        settings.RunProfiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new FactoryValidationException(settings.RunProfiles.Count == 0
            ? $"Settings has no run profile called \"{name}\", or any other. Add one under Settings, or choose a model."
            : $"Settings has no run profile called \"{name}\". It has: {string.Join(", ", settings.RunProfiles.Select(p => p.Name))}.");

    /// <summary>
    /// Every choice on offer: the run profiles, the local models (the configured model, and whatever else the server has) and
    /// Claude's and Antigravity's when provided. The first, with an empty value, is "as set in Settings".
    /// </summary>
    public static IReadOnlyList<ModelOption> Options(FactorySettings settings, IEnumerable<string> localModels, IEnumerable<(string Value, string Label)>? antigravityModels = null)
    {
        var options = new List<ModelOption> { new("", "As set in Settings", "") };
        foreach (var profile in settings.RunProfiles.Where(p => RunProfile.IsValidName(p.Name)))
            options.Add(new ModelOption(Profile(profile.Name), $"{profile.Name} ({Summarize(profile)})", "Run profile, a model for each agent"));
        // Servers list embedding models beside the chat ones, and those can't write code. The configured model stays whatever it is called.
        var chatModels = localModels.Where(id => !id.Contains("embed", StringComparison.OrdinalIgnoreCase));
        foreach (var id in new[] { settings.ModelId }.Concat(chatModels).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
            options.Add(new ModelOption(Local(id), id, "Local model"));
        foreach (var alias in ClaudeCodeOptions.Models)
            options.Add(new ModelOption(Claude(alias), ClaudeCodeOptions.ModelLabel(alias), "Claude, through Claude Code"));
        foreach (var (id, label) in antigravityModels ?? [])
        {
            if (AntigravityOptions.IsValidModelId(id))
                options.Add(new ModelOption(Antigravity(id), string.IsNullOrWhiteSpace(label) ? AntigravityOptions.ModelLabel(id) : label, "Gemini, through Antigravity"));
        }
        return options;
    }
}
