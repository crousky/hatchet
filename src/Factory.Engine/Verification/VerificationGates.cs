using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Factory.Contracts;
using Factory.Engine.Orchestration;

namespace Factory.Engine.Verification;

/// <summary>
/// A run's extra checks. Each gate is a path pattern and a command, and the command runs after the main check when
/// a subtask changed a file the pattern matches. Gates are set when the run is started and kept with its task, so
/// nothing a model writes can add one or change what one runs.
/// </summary>
public static class VerificationGates
{
    public const int MaxGates = 20;
    private const int MaxPatternChars = 200;
    private const int MaxCommandChars = 2000;

    /// <summary>First words that the shell itself answers, or that set up the command that follows, so there is no program to look for.</summary>
    private static readonly HashSet<string> ShellWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "cd", "pushd", "popd", "echo", "set", "export", "call", "if", "for", "while", "test", "true", "false", "exit",
        "type", "dir", "copy", "del", "rem", "start", "env", "exec", "source", "eval", "time", "command"
    };

    public static IReadOnlyList<VerificationGate> Parse(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<VerificationGate>>(json) ?? [];

    public static string? ToJson(IReadOnlyList<VerificationGate> gates) => gates.Count == 0 ? null : JsonSerializer.Serialize(gates);

    /// <summary>Trims the gates and drops repeats. A gate with a part missing is refused, as it could never run or would always run.</summary>
    public static List<VerificationGate> Normalize(IEnumerable<VerificationGate>? gates)
    {
        var normal = new List<VerificationGate>();
        foreach (var gate in gates ?? [])
        {
            var (paths, command) = (NormalizePattern(gate.Paths), (gate.Command ?? "").Trim());
            if (paths.Length == 0 && command.Length == 0) continue;
            if (paths.Length == 0)
                throw new FactoryValidationException($"The extra check `{command}` has no path pattern. Give it one, such as infra/**, so Hatchet knows which changes it is for.");
            if (command.Length == 0)
                throw new FactoryValidationException($"The extra check for \"{paths}\" has no command.");
            if (paths.Length > MaxPatternChars || paths.Split('/').Contains(".."))
                throw new FactoryValidationException($"\"{paths}\" is not a path pattern inside the repository. Use one such as infra/**, *.sh or .github/workflows/*.yml.");
            if (command.Length > MaxCommandChars || command.Contains('\n') || command.Contains('\r'))
                throw new FactoryValidationException($"The extra check for \"{paths}\" needs a command on one line, of at most {MaxCommandChars:N0} characters.");
            if (!normal.Any(g => g.Command == command && string.Equals(g.Paths, paths, StringComparison.OrdinalIgnoreCase)))
                normal.Add(new VerificationGate(paths, command));
        }
        if (normal.Count > MaxGates)
            throw new FactoryValidationException($"A run can have at most {MaxGates} extra checks, and this has {normal.Count}.");
        return normal;
    }

    /// <summary>
    /// Whether a repository-relative path matches a pattern. <c>*</c> and <c>?</c> stay inside one folder, <c>**</c>
    /// crosses folders, a pattern with no slash matches at any depth, and a folder matches everything inside it.
    /// Case is ignored, as it is by the file systems Hatchet mostly runs on.
    /// </summary>
    public static bool Matches(string pattern, string path)
    {
        var normal = NormalizePattern(pattern);
        if (normal.Length == 0) return false;
        // As in a .gitignore, a bare name or extension is meant wherever it is.
        if (!normal.Contains('/')) normal = "**/" + normal;

        var regex = new StringBuilder("^");
        for (var i = 0; i < normal.Length; i++)
        {
            var c = normal[i];
            if (c == '*' && i + 1 < normal.Length && normal[i + 1] == '*')
            {
                var wholeSegment = (i == 0 || normal[i - 1] == '/') && i + 2 < normal.Length && normal[i + 2] == '/';
                regex.Append(wholeSegment ? "(?:.*/)?" : ".*");
                i += wholeSegment ? 2 : 1;
            }
            else if (c == '*') regex.Append("[^/]*");
            else if (c == '?') regex.Append("[^/]");
            else regex.Append(Regex.Escape(c.ToString()));
        }
        regex.Append("(?:/.*)?$");
        return Regex.IsMatch(NormalizePattern(path), regex.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    /// <summary>The gates to run for a change to these files, in the order they were given. A command two gates share runs once.</summary>
    public static IReadOnlyList<VerificationGate> For(IReadOnlyList<VerificationGate> gates, IReadOnlyCollection<string> changedFiles) =>
        gates.Where(g => changedFiles.Any(f => Matches(g.Paths, f))).DistinctBy(g => g.Command).ToList();

    /// <summary>
    /// The program a command starts with, or null when that can't be told from its text: a shell word such as cd, a
    /// variable, or a script in the repository, which the run itself may be about to write.
    /// </summary>
    public static string? ToolOf(string command)
    {
        var text = command.Trim();
        if (text.Length == 0) return null;

        string word;
        if (text[0] is '"' or '\'')
        {
            var end = text.IndexOf(text[0], 1);
            if (end < 0) return null;
            word = text[1..end];
        }
        else
        {
            var end = text.IndexOfAny([' ', '\t']);
            word = end < 0 ? text : text[..end];
            if (word.IndexOfAny(['&', '|', ';', '<', '>', '(', ')', '$', '%', '=', '`']) >= 0) return null;
        }

        if (word.Length == 0 || ShellWords.Contains(word)) return null;
        // A relative path is a script in the repository. One that is rooted is a program somewhere on the machine.
        if (word.IndexOfAny(['/', '\\']) >= 0 && !Path.IsPathRooted(word)) return null;
        return word;
    }

    /// <summary>Whether the machine has the program: on the PATH Hatchet runs commands with, or at its full path.</summary>
    public static bool IsInstalled(string tool)
    {
        var extensions = new List<string> { "" };
        if (OperatingSystem.IsWindows() && !Path.HasExtension(tool))
            extensions.AddRange((Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        IEnumerable<string> folders = Path.IsPathRooted(tool)
            ? [""]
            : (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var folder in folders)
        {
            foreach (var extension in extensions)
            {
                try
                {
                    if (File.Exists(Path.Combine(folder.Trim('"'), tool + extension))) return true;
                }
                catch (ArgumentException)
                {
                    // A PATH entry that isn't a path holds nothing.
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Says which gate needs a program the machine doesn't have, or null when every gate can run. Checked before a run
    /// starts: found later, it would stop the run part-way, at the first subtask to touch the gate's files.
    /// </summary>
    public static string? MissingTool(IReadOnlyList<VerificationGate> gates, Func<string, bool> isInstalled)
    {
        foreach (var gate in gates)
        {
            if (ToolOf(gate.Command) is { } tool && !isInstalled(tool))
                return $"The extra check for \"{gate.Paths}\" runs `{gate.Command}`, but \"{tool}\" isn't installed on this machine, or isn't on the PATH Hatchet runs with. "
                    + $"Install {tool} and restart Hatchet so it sees the new PATH, or start the run without that check.";
        }
        return null;
    }

    private static string NormalizePattern(string? pattern)
    {
        var normal = (pattern ?? "").Replace('\\', '/').Trim();
        while (normal.StartsWith("./", StringComparison.Ordinal)) normal = normal[2..];
        normal = normal.TrimStart('/');
        // "infra/" means the folder, which already matches everything inside it.
        return normal.TrimEnd('/');
    }
}
