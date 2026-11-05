using System.Text;
using System.Text.Json;
using Factory.Contracts;
using Factory.Engine.Skills;

namespace Factory.Engine.Orchestration;

public static class PromptBuilder
{
    private static readonly JsonSerializerOptions Indented = new(LlmSchemas.SerializerOptions) { WriteIndented = true };

    public static string Decompose(string spec, string repoMap, string verification, string? feedback, IReadOnlyList<Skill>? skills = null)
    {
        var builder = new StringBuilder();
        Section(builder, "Architecture specification", spec);
        Section(builder, "Repository map", repoMap);
        Section(builder, "How each subtask is verified", verification);
        if (skills is { Count: > 0 })
        {
            var list = string.Join('\n', skills.Select(s => $"- {s.Name}: {(s.Description.Length > 0 ? s.Description : "(no description)")} (for the {string.Join(", ", s.Roles.Select(RoleName))})"));
            Section(builder, "Skills", $"""
                Skills are instructions written once for this repository, such as how to use a library. Put a skill's name in a subtask's skills list when that subtask needs it, and its agents get the instructions.

                {list}
                """);
        }
        if (!string.IsNullOrWhiteSpace(feedback)) Section(builder, "Reviewer feedback on the previous plan", feedback);
        builder.Append("Break the specification into ordered, atomic coding subtasks.");
        return builder.ToString();
    }

    public static string Refine(FactorySubtask subtask, string spec, string repoMap, string fileContext, string definitions = "", string? guidance = null, string skills = "")
    {
        var builder = new StringBuilder();
        Section(builder, "Overall architecture specification", spec);
        Section(builder, "Repository map", repoMap);
        SubtaskSection(builder, subtask);
        SkillsSection(builder, skills);
        GuidanceSection(builder, guidance);
        Section(builder, "Existing file contents", fileContext);
        DefinitionsSection(builder, definitions);
        builder.Append("Produce the exact implementation instructions, signatures, test cases and edge-case checks for this subtask.");
        return builder.ToString();
    }

    public static string Build(FactorySubtask subtask, string fileContext, string definitions = "", string? guidance = null, string skills = "")
    {
        var builder = new StringBuilder();
        SubtaskSection(builder, subtask);
        Section(builder, "Refined specification", subtask.RefinedSpecJson ?? "(none)");
        SkillsSection(builder, skills);
        GuidanceSection(builder, guidance);
        Section(builder, "Current file contents", fileContext);
        DefinitionsSection(builder, definitions);
        builder.Append("Write the implementation and unit tests for this subtask.");
        return builder.ToString();
    }

    /// <summary>
    /// Asks for the files a cut-off build didn't finish. The finished ones are already written, so they appear in the
    /// current file contents for the rest to match.
    /// </summary>
    public static string BuildRest(FactorySubtask subtask, string fileContext, IReadOnlyList<string> written, IReadOnlyList<string> remaining,
        string cutOff, string definitions = "", string? guidance = null, string skills = "")
    {
        var builder = new StringBuilder();
        SubtaskSection(builder, subtask);
        Section(builder, "Refined specification", subtask.RefinedSpecJson ?? "(none)");
        SkillsSection(builder, skills);
        GuidanceSection(builder, guidance);
        Section(builder, "Your previous answer was cut off", $"""
            {cutOff}
            The files it finished are already written: {string.Join(", ", written)}. Their contents are below.
            Write only the files still missing: {string.Join(", ", remaining)}. Keep any data file short: only the rows the specification names, never ranges or tables recited from memory.
            """);
        Section(builder, "Current file contents", fileContext);
        DefinitionsSection(builder, definitions);
        builder.Append("Write the missing files for this subtask, matching the files already written.");
        return builder.ToString();
    }

    /// <summary>A prompt for another try after the model looped or filled its context window, naming what went wrong.</summary>
    public static string AfterRunaway(string prompt, string problem) =>
        $"""
        {prompt}

        ## Your previous answer to this was stopped

        {problem}
        Don't repeat it. Keep your thinking brief and go straight to the answer. Write data files with only the rows the specification names (a few dozen at most); never enumerate ranges or sequences, or recite tables, registries or other long data from memory. If complete data is needed, write the code or script that fetches or generates it instead.
        """;

    public static string Review(FactorySubtask subtask, string diff, string testSummary)
    {
        var builder = new StringBuilder();
        SubtaskSection(builder, subtask);
        Section(builder, "Refined specification", subtask.RefinedSpecJson ?? "(none)");
        Section(builder, "Git diff", diff.Length > 0 ? diff : "(the diff is empty: no files changed)");
        Section(builder, "Build and test summary", testSummary);
        builder.Append("Review this diff against the refined specification.");
        return builder.ToString();
    }

    public static string Fix(FactorySubtask subtask, string failure, string fileContext, string? guidance, string definitions = "", string skills = "")
    {
        var builder = new StringBuilder();
        SubtaskSection(builder, subtask);
        Section(builder, "Refined specification", subtask.RefinedSpecJson ?? "(none)");
        if (subtask.FixHistory.Count > 0)
            Section(builder, "Earlier fix attempts, all of which failed", string.Join("\n\n", subtask.FixHistory));
        Section(builder, "Failure to fix", failure);
        SkillsSection(builder, skills);
        GuidanceSection(builder, guidance);
        Section(builder, "Current file contents", fileContext);
        DefinitionsSection(builder, definitions);
        builder.Append($"This is fix attempt {subtask.FixAttempts}. Return the file changes that fix the failure.");
        return builder.ToString();
    }

    // The files that declare types named above, so their members and constructors are used as they are, not guessed.
    private static void DefinitionsSection(StringBuilder builder, string definitions)
    {
        if (definitions.Length == 0) return;
        Section(builder, "Definitions of types named above. Use their members and constructor parameters exactly as declared here", definitions);
    }

    // Instructions written once for the repository and chosen for this subtask, such as how to use a UI library.
    private static void SkillsSection(StringBuilder builder, string skills)
    {
        if (skills.Length == 0) return;
        Section(builder, "Skills. Follow these instructions for this subtask", skills);
    }

    private static string RoleName(AgentRole role) => AgentProfiles.Defaults.Single(p => p.Role == role).DisplayName;

    public static string Repair(string prompt, string error) =>
        $"{prompt}\n\nYour previous response could not be used: {error}\nRespond again with one complete JSON object that matches the schema.";

    public static string ToJson<T>(T value) => JsonSerializer.Serialize(value, Indented);

    /// <summary>Keeps the end of the text, where build failures and test summaries usually are.</summary>
    public static string Tail(string text, int maxChars) =>
        text.Length <= maxChars ? text : $"[earlier output omitted]\n{text[^maxChars..]}";

    public static string Head(string text, int maxChars) =>
        text.Length <= maxChars ? text : $"{text[..maxChars]}\n[diff truncated at {maxChars} characters]";

    private static void SubtaskSection(StringBuilder builder, FactorySubtask subtask)
    {
        var details = new StringBuilder();
        details.Append("Title: ").Append(subtask.Title).Append('\n');
        details.Append("Description: ").Append(subtask.Description).Append('\n');
        details.Append("Files to modify: ").Append(Join(subtask.TargetFilesToModify)).Append('\n');
        details.Append("Files to create: ").Append(Join(subtask.TargetFilesToCreate)).Append('\n');
        details.Append("Reference files: ").Append(Join(subtask.ContextFilesToRead));
        Section(builder, "Subtask", details.ToString());
    }

    private static string Join(List<string> items) => items.Count == 0 ? "(none)" : string.Join(", ", items);

    private static void GuidanceSection(StringBuilder builder, string? guidance)
    {
        if (!string.IsNullOrWhiteSpace(guidance)) Section(builder, "Human guidance. Follow it where it conflicts with the rest", guidance);
    }

    private static void Section(StringBuilder builder, string title, string body) =>
        builder.Append("## ").Append(title).Append("\n\n").Append(body.TrimEnd()).Append("\n\n");
}
