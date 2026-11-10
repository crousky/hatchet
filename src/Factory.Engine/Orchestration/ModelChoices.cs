using Factory.Contracts;

namespace Factory.Engine.Orchestration;

/// <summary>One model someone can pick for a piece of work. <see cref="Value"/> is what gets stored, and empty means "as set in Settings".</summary>
public sealed record ModelOption(string Value, string Label, string Group);

/// <summary>
/// Which model does a piece of work, picked when it starts instead of in Settings: <c>local:&lt;model id&gt;</c> puts every agent
/// on that model of the local server, and <c>claude:&lt;opus|sonnet|haiku&gt;</c> puts every agent on that Claude model through Claude
/// Code. No choice means the agents run as Settings says. Everything else about each agent, such as its effort, its
/// temperature and its prompt, stays as configured.
/// </summary>
public static class ModelChoices
{
    private const string LocalPrefix = "local:";
    private const string ClaudePrefix = "claude:";

    public static string Local(string modelId) => LocalPrefix + modelId;

    public static string Claude(string alias) => ClaudePrefix + alias;

    /// <summary>
    /// The canonical form of a choice, or null for "as set in Settings". Plain Claude names such as "opus" are accepted as a shorthand.
    /// Throws <see cref="FactoryValidationException"/> for anything else it can't make sense of.
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

        var alias = (text.StartsWith(ClaudePrefix, StringComparison.OrdinalIgnoreCase) ? text[ClaudePrefix.Length..] : text).Trim().ToLowerInvariant();
        return ClaudeCodeOptions.Models.Contains(alias)
            ? Claude(alias)
            : throw new FactoryValidationException($"\"{choice}\" isn't a model choice. Use local:<model id>, or claude:{string.Join(", claude:", ClaudeCodeOptions.Models)}, or leave it out to use Settings.");
    }

    /// <summary>The Claude model a choice names, or null when the choice is Settings or a local model.</summary>
    public static string? ClaudeModelOf(string? choice) =>
        Normalize(choice) is { } normalized && normalized.StartsWith(ClaudePrefix, StringComparison.Ordinal) ? normalized[ClaudePrefix.Length..] : null;

    /// <summary>In words, for the places a choice is shown. Null when the choice is Settings.</summary>
    public static string? Describe(string? choice)
    {
        if (Normalize(choice) is not { } normalized) return null;
        return normalized.StartsWith(ClaudePrefix, StringComparison.Ordinal)
            ? ClaudeCodeOptions.ModelLabel(normalized[ClaudePrefix.Length..])
            : $"{normalized[LocalPrefix.Length..]} (local)";
    }

    /// <summary>
    /// The settings a piece of work runs with: the given ones, with every agent moved to the chosen model. The given settings
    /// are left as they were, since they are shared by every run.
    /// </summary>
    public static FactorySettings Apply(FactorySettings settings, string? choice)
    {
        if (Normalize(choice) is not { } normalized) return settings;

        var onClaude = normalized.StartsWith(ClaudePrefix, StringComparison.Ordinal);
        var model = normalized[(onClaude ? ClaudePrefix : LocalPrefix).Length..];
        var copy = settings.Clone();
        if (!onClaude) copy.ModelId = model;

        foreach (var role in Enum.GetValues<AgentRole>())
        {
            var profileOverride = copy.ProfileOverrides.FirstOrDefault(o => o.Role == role);
            if (profileOverride is null)
            {
                var profile = AgentProfiles.Resolve(settings, role);
                profileOverride = new AgentProfileOverride
                {
                    Role = role, ReasoningEffort = profile.ReasoningEffort, Temperature = profile.Temperature,
                    ClaudeModel = profile.ClaudeModel, ClaudeEffort = profile.ClaudeEffort
                };
                copy.ProfileOverrides.Add(profileOverride);
            }
            profileOverride.Engine = onClaude ? AgentEngine.ClaudeCode : AgentEngine.Local;
            if (onClaude) profileOverride.ClaudeModel = model;
        }
        return copy;
    }

    /// <summary>
    /// Every model on offer: the local ones (the configured model, and whatever else the server has) and Claude's. The first,
    /// with an empty value, is "as set in Settings".
    /// </summary>
    public static IReadOnlyList<ModelOption> Options(FactorySettings settings, IEnumerable<string> localModels)
    {
        var options = new List<ModelOption> { new("", "As set in Settings", "") };
        // Servers list embedding models beside the chat ones, and those can't write code. The configured model stays whatever it is called.
        var chatModels = localModels.Where(id => !id.Contains("embed", StringComparison.OrdinalIgnoreCase));
        foreach (var id in new[] { settings.ModelId }.Concat(chatModels).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal))
            options.Add(new ModelOption(Local(id), id, "Local model"));
        foreach (var alias in ClaudeCodeOptions.Models)
            options.Add(new ModelOption(Claude(alias), ClaudeCodeOptions.ModelLabel(alias), "Claude, through Claude Code"));
        return options;
    }
}
