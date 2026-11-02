namespace Factory.Web.Services;

public enum DiffLineKind { Context, Added, Removed, Hunk, Meta }

public sealed record DiffLine(DiffLineKind Kind, string Text, int? OldNumber, int? NewNumber);

public sealed class DiffFile
{
    public string Path { get; set; } = "";
    public string? Status { get; set; }
    public int Additions { get; set; }
    public int Deletions { get; set; }
    public List<DiffLine> Lines { get; } = [];
}

/// <summary>Parses git's unified diff output for the diff viewer.</summary>
public static class UnifiedDiff
{
    public static List<DiffFile> Parse(string? diff)
    {
        var files = new List<DiffFile>();
        if (string.IsNullOrEmpty(diff)) return files;

        DiffFile? file = null;
        var oldLine = 0;
        var newLine = 0;
        var inHunk = false;

        foreach (var raw in diff.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                file = new DiffFile { Path = PathFromHeader(line) };
                files.Add(file);
                inHunk = false;
                continue;
            }
            if (file is null) continue;

            if (!inHunk)
            {
                if (line.StartsWith("+++ b/", StringComparison.Ordinal)) file.Path = line[6..];
                else if (line.StartsWith("new file", StringComparison.Ordinal)) file.Status = "new";
                else if (line.StartsWith("deleted file", StringComparison.Ordinal)) file.Status = "deleted";
                else if (line.StartsWith("rename from", StringComparison.Ordinal)) file.Status = "renamed";
                else if (line.StartsWith("Binary files", StringComparison.Ordinal)) file.Lines.Add(new DiffLine(DiffLineKind.Meta, "Binary file", null, null));
            }

            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                inHunk = true;
                (oldLine, newLine) = ParseHunkStart(line);
                file.Lines.Add(new DiffLine(DiffLineKind.Hunk, line, null, null));
                continue;
            }
            if (!inHunk || line.Length == 0) continue;

            switch (line[0])
            {
                case '+':
                    file.Additions++;
                    file.Lines.Add(new DiffLine(DiffLineKind.Added, line[1..], null, newLine++));
                    break;
                case '-':
                    file.Deletions++;
                    file.Lines.Add(new DiffLine(DiffLineKind.Removed, line[1..], oldLine++, null));
                    break;
                case ' ':
                    file.Lines.Add(new DiffLine(DiffLineKind.Context, line[1..], oldLine++, newLine++));
                    break;
                case '\\':
                    file.Lines.Add(new DiffLine(DiffLineKind.Meta, line, null, null));
                    break;
            }
        }

        return files;
    }

    private static string PathFromHeader(string header)
    {
        var index = header.LastIndexOf(" b/", StringComparison.Ordinal);
        return index >= 0 ? header[(index + 3)..] : header;
    }

    private static (int Old, int New) ParseHunkStart(string hunk)
    {
        // @@ -12,7 +12,9 @@ optional context
        var parts = hunk.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return (parts.Length > 1 ? Start(parts[1]) : 1, parts.Length > 2 ? Start(parts[2]) : 1);

        static int Start(string range) =>
            int.TryParse(range.TrimStart('-', '+').Split(',')[0], out var start) ? start : 1;
    }
}
