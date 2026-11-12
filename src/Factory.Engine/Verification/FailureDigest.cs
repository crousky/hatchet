using System.Text.RegularExpressions;

namespace Factory.Engine.Verification;

/// <summary>
/// Pulls the error lines out of build and test output. Restore and build noise can bury a single compiler error,
/// and small models fix what they're shown first, so the errors go at the top of the failure.
/// </summary>
public static partial class FailureDigest
{
    private const int MaxLines = 40;
    private const int MaxLineChars = 400;

    public static IReadOnlyList<string> Errors(string output)
    {
        var errors = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || !ErrorLine().IsMatch(line) || NoErrors().IsMatch(line)) continue;

            // MSBuild repeats each error per project build, with a different temporary project name in brackets.
            line = ProjectSuffix().Replace(line, "");
            if (line.Length > MaxLineChars) line = line[..MaxLineChars] + "…";
            if (seen.Add(line)) errors.Add(line);
            if (errors.Count == MaxLines) break;
        }
        return errors;
    }

    /// <summary>
    /// The repository files that error lines point at, such as <c>C:\repo\src\A.cs(12,5): error</c> or
    /// <c>src/a.ts:12:5 - error</c>, as repository-relative paths with forward slashes, in order of first mention. Paths
    /// outside the repository or that don't exist are left out.
    /// </summary>
    public static IReadOnlyList<string> FilesNamed(IEnumerable<string> errorLines, string repositoryPath, int max = 10)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath)) + Path.DirectorySeparatorChar;
        var files = new List<string>();
        foreach (var line in errorLines)
        {
            foreach (Match match in FileReference().Matches(line))
            {
                string full;
                try { full = Path.GetFullPath(Path.IsPathRooted(match.Groups["path"].Value) ? match.Groups["path"].Value : Path.Combine(root, match.Groups["path"].Value)); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full)) continue;

                var relative = full[root.Length..].Replace('\\', '/');
                if (!files.Contains(relative, StringComparer.OrdinalIgnoreCase)) files.Add(relative);
                if (files.Count == max) return files;
            }
        }
        return files;
    }

    // A path with a file extension, followed by the "(line" or ":line" compilers put after it.
    [GeneratedRegex(@"(?<path>(?:[A-Za-z]:[\\/])?[\w.\-\\/]+\.\w{1,10})(?=\(\d|:\d)")]
    private static partial Regex FileReference();

    [GeneratedRegex(@"\berror\b|\bfailed\b|\bexception\b|^FAIL\b|AssertionError|npm ERR!|Traceback", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorLine();

    // Summary lines such as "0 Error(s)" or "Failed: 0" report success.
    [GeneratedRegex(@"\b0 (error|failed)|\b(errors?|failed):\s*0\b", RegexOptions.IgnoreCase)]
    private static partial Regex NoErrors();

    [GeneratedRegex(@"\s*\[[^\]]+\.(cs|fs|vb)proj\]$")]
    private static partial Regex ProjectSuffix();
}
