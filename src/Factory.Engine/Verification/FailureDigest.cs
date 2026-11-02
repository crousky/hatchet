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

    [GeneratedRegex(@"\berror\b|\bfailed\b|\bexception\b|^FAIL\b|AssertionError|npm ERR!|Traceback", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorLine();

    // Summary lines such as "0 Error(s)" or "Failed: 0" report success.
    [GeneratedRegex(@"\b0 (error|failed)|\b(errors?|failed):\s*0\b", RegexOptions.IgnoreCase)]
    private static partial Regex NoErrors();

    [GeneratedRegex(@"\s*\[[^\]]+\.(cs|fs|vb)proj\]$")]
    private static partial Regex ProjectSuffix();
}
