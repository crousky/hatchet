using System.Text;
using System.Text.RegularExpressions;
using Factory.Contracts;

namespace Factory.Engine.Skills;

/// <summary>Where a skill was found. Earlier places win when two skills share a name.</summary>
public enum SkillSource
{
    /// <summary>The repository's <c>.hatchet/skills</c> folder.</summary>
    Project,

    /// <summary>The repository's <c>.claude/skills</c> folder, shared with Claude Code.</summary>
    ClaudeProject,

    /// <summary>The user's own folder, for every repository.</summary>
    User
}

/// <summary>
/// Instructions written once and given to agents whose subtask needs them. A skill is a folder holding a
/// <c>SKILL.md</c> in the Claude Code layout: YAML frontmatter with a name and a description, then a markdown body.
/// Hatchet also reads an optional <c>roles</c> list naming the agents it is for.
/// </summary>
/// <param name="Path">The full path of the skill's <c>SKILL.md</c>.</param>
/// <param name="Problems">What was wrong with the file, such as a role Hatchet doesn't know. The skill still loads.</param>
public sealed record Skill(
    string Name,
    string Description,
    IReadOnlyList<AgentRole> Roles,
    string Body,
    string Path,
    SkillSource Source,
    IReadOnlyList<string> Problems)
{
    public bool IsFor(AgentRole role) => Roles.Contains(role);
}

/// <summary>Finds the skills for a repository: its own, then the user's.</summary>
/// <param name="userDirectory">The user's skills folder, or null for none, as in tests.</param>
public sealed partial class SkillLibrary(string? userDirectory)
{
    public const string FileName = "SKILL.md";

    /// <summary><c>%LOCALAPPDATA%\Hatchet\skills</c>.</summary>
    public static string DefaultUserDirectory =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hatchet", "skills");

    /// <summary>The agents whose prompts carry skills. A skill without a roles list goes to all of them.</summary>
    public static readonly IReadOnlyList<AgentRole> SkilledRoles = [AgentRole.ContextSpecifier, AgentRole.CodeGenerator, AgentRole.BugFixer];

    /// <summary>Where a new project skill is written, relative to the repository.</summary>
    public const string ProjectFolder = ".hatchet/skills";

    private const string ClaudeProjectFolder = ".claude/skills";

    public string? UserDirectory => userDirectory;

    /// <summary>
    /// The skills a run in the repository can use, by name. A project skill hides a user skill of the same name, and
    /// one in <c>.hatchet/skills</c> hides one in <c>.claude/skills</c>.
    /// </summary>
    public IReadOnlyList<Skill> Load(string? repositoryPath)
    {
        var found = new Dictionary<string, Skill>(StringComparer.OrdinalIgnoreCase);
        foreach (var (directory, source) in Places(repositoryPath))
        {
            foreach (var skill in LoadFolder(directory, source))
                found.TryAdd(skill.Name, skill);
        }
        return found.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Only the user's skills.</summary>
    public IReadOnlyList<Skill> LoadUser() => Load(null);

    private IEnumerable<(string Directory, SkillSource Source)> Places(string? repositoryPath)
    {
        if (!string.IsNullOrWhiteSpace(repositoryPath))
        {
            yield return (System.IO.Path.Combine(repositoryPath, ProjectFolder), SkillSource.Project);
            yield return (System.IO.Path.Combine(repositoryPath, ClaudeProjectFolder), SkillSource.ClaudeProject);
        }
        if (!string.IsNullOrWhiteSpace(userDirectory)) yield return (userDirectory, SkillSource.User);
    }

    private static IEnumerable<Skill> LoadFolder(string directory, SkillSource source)
    {
        if (!Directory.Exists(directory)) yield break;

        IEnumerable<string> folders;
        try
        {
            folders = Directory.EnumerateDirectories(directory).Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var folder in folders)
        {
            var file = System.IO.Path.Combine(folder, FileName);
            string text;
            try
            {
                if (!File.Exists(file)) continue;
                text = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            yield return Parse(text, file, source, System.IO.Path.GetFileName(folder));
        }
    }

    /// <summary>Reads a <c>SKILL.md</c>. A missing name falls back to the folder's name.</summary>
    public static Skill Parse(string text, string path, SkillSource source, string folderName)
    {
        var problems = new List<string>();
        var (fields, body) = SplitFrontmatter(text.TrimStart('﻿'));
        if (fields is null) problems.Add("It has no YAML frontmatter, so it has no description for the Task Planner.");
        fields ??= [];

        var name = Scalar(fields, "name");
        if (string.IsNullOrWhiteSpace(name)) name = folderName;
        var description = Scalar(fields, "description") ?? "";
        if (description.Length == 0 && fields.Count > 0) problems.Add("It has no description, so the Task Planner can't tell when it applies.");

        var roles = new List<AgentRole>();
        if (fields.TryGetValue("roles", out var listed))
        {
            // A plain scalar may hold a comma list, as in "roles: CodeGenerator, BugFixer".
            foreach (var entry in listed.SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
            {
                if (ParseRole(entry) is { } role)
                {
                    if (!roles.Contains(role)) roles.Add(role);
                }
                else
                {
                    problems.Add($"\"{entry}\" isn't a Hatchet agent role. Use {string.Join(", ", SkilledRoles)}.");
                }
            }
            foreach (var role in roles.Where(r => !SkilledRoles.Contains(r)))
                problems.Add($"The {role} doesn't take skills, so it won't get this one.");
        }
        if (roles.Count == 0) roles.AddRange(SkilledRoles);

        return new Skill(name.Trim(), description.Trim(), roles, body.Trim(), path, source, problems);
    }

    /// <summary>Accepts role names as Hatchet writes them, such as <c>CodeGenerator</c>, <c>Code Generator</c> or <c>code-generator</c>.</summary>
    public static AgentRole? ParseRole(string text)
    {
        var compact = new string(text.Where(char.IsLetterOrDigit).ToArray());
        return Enum.TryParse<AgentRole>(compact, ignoreCase: true, out var role) && Enum.IsDefined(role) && !compact.All(char.IsDigit) ? role : null;
    }

    private static string? Scalar(Dictionary<string, List<string>> fields, string key) =>
        fields.TryGetValue(key, out var values) && values.Count > 0 ? string.Join(", ", values) : null;

    /// <summary>
    /// Splits off the frontmatter and reads its top-level keys: plain and quoted scalars, block scalars
    /// (<c>|</c> and <c>&gt;</c>), and flow (<c>[a, b]</c>) or block (<c>- a</c>) lists. Nested maps, such as
    /// Claude Code's <c>metadata</c>, are skipped. Returns null fields when there is no frontmatter.
    /// </summary>
    private static (Dictionary<string, List<string>>? Fields, string Body) SplitFrontmatter(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || lines[0].Trim() != "---") return (null, text);
        var end = Array.FindIndex(lines, 1, l => l.TrimEnd() is "---" or "...");
        if (end < 0) return (null, text);

        var fields = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < end; i++)
        {
            var match = KeyLine().Match(lines[i]);
            if (!match.Success) continue;
            var key = match.Groups[1].Value;
            var value = match.Groups[2].Value.Trim();

            // The lines that belong to this key: indented, list items, or blank.
            var next = i + 1;
            while (next < end && (lines[next].Length == 0 || char.IsWhiteSpace(lines[next][0]) || lines[next].StartsWith('-'))) next++;
            var block = lines[(i + 1)..next];
            i = next - 1;

            fields[key] = ReadValue(value, block);
        }
        return (fields, string.Join('\n', lines[(end + 1)..]));
    }

    private static List<string> ReadValue(string value, string[] block)
    {
        if (value.Length > 0 && value[0] is '|' or '>')
        {
            var content = Dedent(block);
            var joined = value[0] == '|' ? string.Join('\n', content) : Fold(content);
            return [joined.Trim()];
        }
        if (value.StartsWith('['))
        {
            return value.Trim('[', ']').Split(',').Select(v => Unquote(v.Trim())).Where(v => v.Length > 0).ToList();
        }
        if (value.Length == 0)
        {
            // Lines that aren't list items are a nested map, which Hatchet doesn't use.
            return block.Select(l => l.Trim()).Where(l => l.StartsWith('-')).Select(l => Unquote(l[1..].Trim())).Where(v => v.Length > 0).ToList();
        }

        // A plain scalar may continue on indented lines.
        var continued = block.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('-')).ToList();
        return [Unquote(continued.Count == 0 ? value : $"{value} {string.Join(' ', continued)}")];
    }

    private static List<string> Dedent(string[] block)
    {
        var indent = block.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
        return block.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart()).ToList();
    }

    private static string Fold(IEnumerable<string> lines)
    {
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            if (line.Trim().Length == 0) builder.Append('\n');
            else builder.Append(builder.Length == 0 || builder[^1] == '\n' ? "" : " ").Append(line.Trim());
        }
        return builder.ToString();
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') return value[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') return value[1..^1].Replace("''", "'");
        return value;
    }

    [GeneratedRegex(@"^([A-Za-z0-9_-]+):(?:\s+(.*)|\s*)$")]
    private static partial Regex KeyLine();

    /// <summary>A skill's folder name: lowercase letters, digits and hyphens, as Claude Code expects.</summary>
    public static string Slug(string name)
    {
        var builder = new StringBuilder();
        foreach (var c in name.Trim().ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') builder.Append(c);
            else if (builder.Length > 0 && builder[^1] != '-') builder.Append('-');
        }
        var slug = builder.ToString().Trim('-');
        return slug.Length > 64 ? slug[..64].TrimEnd('-') : slug;
    }

    /// <summary>The text of a new <c>SKILL.md</c>.</summary>
    public static string Compose(string name, string description, string body, IReadOnlyList<AgentRole>? roles = null)
    {
        var builder = new StringBuilder();
        builder.Append("---\n");
        builder.Append("name: ").Append(name).Append('\n');
        builder.Append("description: ").Append(Quote(description.ReplaceLineEndings(" ").Trim())).Append('\n');
        if (roles is { Count: > 0 }) builder.Append("roles: [").Append(string.Join(", ", roles)).Append("]\n");
        builder.Append("---\n\n");
        builder.Append(body.ReplaceLineEndings("\n").Trim()).Append('\n');
        return builder.ToString();
    }

    private static string Quote(string value) => $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    /// <summary>The repository-relative path of a project skill's file, with forward slashes.</summary>
    public static string ProjectSkillPath(string slug) => $"{ProjectFolder}/{slug}/{FileName}";

    /// <summary>The skills' bodies under their names, each cut to <paramref name="maxChars"/>, for a prompt.</summary>
    public static string Render(IEnumerable<Skill> skills, int maxChars)
    {
        var builder = new StringBuilder();
        foreach (var skill in skills)
        {
            var body = skill.Body.Length <= maxChars ? skill.Body : $"{skill.Body[..maxChars]}\n[skill truncated at {maxChars} characters]";
            builder.Append("### ").Append(skill.Name).Append("\n\n").Append(body).Append("\n\n");
        }
        return builder.ToString().TrimEnd();
    }
}
