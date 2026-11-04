using System.Text.RegularExpressions;

namespace Factory.Engine.Workspace;

/// <summary>
/// Finds the source files that define the types a piece of text names, such as a subtask's description or a compiler
/// error. Agents that see the real signatures of what they build on use them, rather than guessing at members and
/// constructor arguments that don't exist.
/// </summary>
public static partial class DefinitionFinder
{
    private const int MaxIndexedFiles = 5000;
    private const long MaxIndexedFileBytes = 256 * 1024;

    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".fs", ".vb", ".ts", ".tsx", ".js", ".jsx", ".mjs", ".java", ".kt", ".scala", ".go", ".rs", ".py",
        ".swift", ".dart", ".php", ".rb", ".cpp", ".cc", ".h", ".hpp"
    };

    // A type declaration in most languages: class, interface, record, struct, enum, trait, protocol or type alias.
    [GeneratedRegex(@"\b(?:class|interface|struct|enum|record(?:\s+(?:class|struct))?|trait|protocol|type|typealias)\s+([A-Z][A-Za-z0-9_]*)")]
    private static partial Regex Declaration();

    // A name that could be a type: starts upper case and has a lower case letter, so FooBar and IFoo but not URL or ID.
    [GeneratedRegex(@"\b[A-Z][A-Za-z0-9_]*[a-z][A-Za-z0-9_]*\b")]
    private static partial Regex TypeLikeName();

    /// <summary>
    /// The repository files that declare a type the text names, in the order the text first names them, without the
    /// files in <paramref name="exclude"/>. Paths are relative and use forward slashes.
    /// </summary>
    public static IReadOnlyList<string> Find(string repositoryPath, string text, IEnumerable<string> exclude, int maxFiles)
    {
        var names = TypeLikeName().Matches(text).Select(m => m.Value).Distinct(StringComparer.Ordinal).ToList();
        if (names.Count == 0 || maxFiles <= 0 || !Directory.Exists(repositoryPath)) return [];

        var index = IndexDeclarations(repositoryPath);
        var skip = exclude.Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new List<string>();
        foreach (var name in names)
        {
            if (!index.TryGetValue(name, out var files)) continue;
            foreach (var file in files)
            {
                if (!skip.Add(file)) continue;
                found.Add(file);
                if (found.Count == maxFiles) return found;
            }
        }
        return found;
    }

    /// <summary>Each declared type name, with the files that declare it.</summary>
    private static Dictionary<string, List<string>> IndexDeclarations(string repositoryPath)
    {
        var index = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var root = new DirectoryInfo(repositoryPath);
        var indexed = 0;
        foreach (var file in SourceFiles(root))
        {
            if (++indexed > MaxIndexedFiles) break;
            string content;
            try { content = File.ReadAllText(file.FullName); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            var relative = Normalize(Path.GetRelativePath(root.FullName, file.FullName));
            foreach (Match match in Declaration().Matches(content))
            {
                var name = match.Groups[1].Value;
                if (!index.TryGetValue(name, out var files)) index[name] = files = [];
                if (!files.Contains(relative)) files.Add(relative);
            }
        }
        return index;
    }

    private static IEnumerable<FileInfo> SourceFiles(DirectoryInfo directory)
    {
        foreach (var file in directory.EnumerateFiles())
        {
            if (SourceExtensions.Contains(file.Extension) && file.Length <= MaxIndexedFileBytes) yield return file;
        }
        foreach (var child in directory.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (RepositoryScanner.IsIgnoredDirectory(child)) continue;
            foreach (var file in SourceFiles(child)) yield return file;
        }
    }

    private static string Normalize(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
    }
}
