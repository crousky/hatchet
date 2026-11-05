using System.Collections.Concurrent;
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

    // A type declaration in most languages: class, interface, record, struct, enum, trait, protocol, object or type
    // alias, at the start of a line after any modifiers. Comments, strings and "import type" lines don't match.
    [GeneratedRegex(@"^[ \t]*(?:(?:public|private|protected|internal|static|sealed|abstract|partial|readonly|ref|unsafe|file|new|export|default|declare|data|open|final|inline|value|case|pub(?:\([a-z]+\))?)[ \t]+)*(?:enum[ \t]+(?:class|struct)|record(?:[ \t]+(?:class|struct))?|class|interface|struct|enum|trait|protocol|object|type|typealias)[ \t]+([A-Z][A-Za-z0-9_]*)", RegexOptions.Multiline)]
    private static partial Regex Declaration();

    private sealed record IndexedFile(DateTime Modified, long Length, string[] Types);

    // The types each file declares, by repository, so a file is read again only once it changes.
    private static readonly ConcurrentDictionary<string, Dictionary<string, IndexedFile>> Indexes = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>The names of the types the repository's own source declares.</summary>
    public static IReadOnlySet<string> DeclaredNames(string repositoryPath) =>
        Directory.Exists(repositoryPath) ? IndexDeclarations(repositoryPath).Keys.ToHashSet(StringComparer.Ordinal) : new HashSet<string>();

    /// <summary>
    /// Each declared type name, with the files that declare it. Files unchanged since the last call, by time and size,
    /// aren't read again.
    /// </summary>
    private static Dictionary<string, List<string>> IndexDeclarations(string repositoryPath)
    {
        var index = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var root = new DirectoryInfo(repositoryPath);
        var key = Path.TrimEndingDirectorySeparator(root.FullName);
        var previous = Indexes.TryGetValue(key, out var cached) ? cached : [];
        var current = new Dictionary<string, IndexedFile>(StringComparer.OrdinalIgnoreCase);
        var indexed = 0;
        foreach (var file in SourceFiles(root))
        {
            if (++indexed > MaxIndexedFiles) break;
            var relative = Normalize(Path.GetRelativePath(root.FullName, file.FullName));
            if (!previous.TryGetValue(relative, out var entry) || entry.Modified != file.LastWriteTimeUtc || entry.Length != file.Length)
            {
                string content;
                try { content = File.ReadAllText(file.FullName); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                entry = new IndexedFile(file.LastWriteTimeUtc, file.Length,
                    Declaration().Matches(content).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray());
            }
            current[relative] = entry;

            foreach (var name in entry.Types)
            {
                if (!index.TryGetValue(name, out var files)) index[name] = files = [];
                files.Add(relative);
            }
        }
        Indexes[key] = current;
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
