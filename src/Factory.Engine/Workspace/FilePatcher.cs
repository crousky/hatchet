using System.Text;
using Factory.Contracts;

namespace Factory.Engine.Workspace;

public sealed record PatchResult(IReadOnlyList<string> Applied, IReadOnlyList<string> Warnings);

/// <summary>The model proposed an operation that may not be applied. Nothing has been written.</summary>
public sealed class PatchRejectedException(string message) : Exception(message);

/// <summary>Applies model-generated file operations, confined to the repository.</summary>
public static class FilePatcher
{
    /// <summary>A file at least this long is checked against truncated rewrites.</summary>
    internal const int ShrinkCheckMinLines = 200;

    /// <summary>An OVERWRITE that keeps less than this share of a long file's lines is refused as a likely truncated answer.</summary>
    internal const double ShrinkCheckKeepShare = 0.7;

    internal const string SearchMarker = "<<<<<<< SEARCH";
    internal const string DividerMarker = "=======";
    internal const string ReplaceMarker = ">>>>>>> REPLACE";

    /// <summary>
    /// Works out every operation first, so a rejected set leaves the disk untouched. EDIT changes part of an existing
    /// file, so a long file needn't be reproduced in full, and an OVERWRITE that cuts a long file down sharply is
    /// refused: a model that can't fit the whole file in its answer returns a shortened one that still looks complete.
    /// </summary>
    public static PatchResult Apply(string repositoryPath, IReadOnlyList<FileOperationDto> operations)
    {
        var root = Path.GetFullPath(repositoryPath);
        var resolved = operations
            .Select(op => (Op: op, Action: NormalizeAction(op), Path: ResolveSafePath(root, op.RelativeFilePath)))
            .ToList();

        // Where each file will end up, in order, so later operations see the effect of earlier ones. Null means deleted.
        var state = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string? Current(string path) => state.TryGetValue(path, out var c) ? c : File.Exists(path) ? File.ReadAllText(path) : null;

        var applied = new List<string>();
        var warnings = new List<string>();
        foreach (var (op, action, path) in resolved)
        {
            var relative = NormalizeSeparators(op.RelativeFilePath);
            var current = Current(path);
            if (action == FileActions.Delete)
            {
                if (current is not null)
                {
                    state[path] = null;
                    applied.Add($"DELETE {relative}");
                }
                else
                {
                    warnings.Add($"DELETE {relative}: the file does not exist.");
                }
                continue;
            }

            if (action == FileActions.Edit)
            {
                if (current is null) throw new PatchRejectedException($"EDIT {relative}: the file does not exist. Use CREATE for a new file.");
                state[path] = ApplyEdits(relative, current.ReplaceLineEndings("\n"), op.FileContent);
                applied.Add($"EDIT {relative}");
                continue;
            }

            if (action == FileActions.Create && current is not null) warnings.Add($"CREATE {relative}: the file already existed and was overwritten.");
            if (action == FileActions.Overwrite && current is null) warnings.Add($"OVERWRITE {relative}: the file did not exist and was created.");
            if (action == FileActions.Overwrite && current is not null) RejectTruncation(relative, current, op.FileContent);

            state[path] = op.FileContent;
            applied.Add($"{action} {relative}");
        }

        foreach (var (path, content) in state)
        {
            if (content is null)
            {
                if (File.Exists(path)) File.Delete(path);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            WritePreservingStyle(path, content, File.Exists(path));
        }

        return new PatchResult(applied, warnings);
    }

    private static void RejectTruncation(string relative, string existing, string content)
    {
        var before = CountLines(existing);
        var after = CountLines(content);
        if (before < ShrinkCheckMinLines || after >= before * ShrinkCheckKeepShare) return;
        throw new PatchRejectedException(
            $"OVERWRITE {relative} would cut it from {before} lines to {after}, which looks like a truncated answer. " +
            "Don't rewrite a long file from memory: change it with an EDIT operation of SEARCH/REPLACE blocks, or, if the cut is intended, " +
            "DELETE the file and CREATE it again in the same patch.");
    }

    private static int CountLines(string text) => text.Length == 0 ? 0 : text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n').Length;

    /// <summary>
    /// Applies the SEARCH/REPLACE blocks in <paramref name="blocks"/> to <paramref name="content"/> (line ends are \n). Each
    /// SEARCH text must match the file exactly once, so an edit never lands in the wrong place.
    /// </summary>
    internal static string ApplyEdits(string relative, string content, string blocks)
    {
        var lines = blocks.ReplaceLineEndings("\n").Split('\n');
        var result = content;
        var count = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0) continue;
            if (lines[i].TrimEnd() != SearchMarker)
                throw new PatchRejectedException($"EDIT {relative}: expected a line `{SearchMarker}` but found `{Truncate(lines[i])}`. Each block is {SearchMarker}, the exact existing text, {DividerMarker}, the new text, {ReplaceMarker}.");

            var search = new List<string>();
            var replace = new List<string>();
            var reading = 0; // 0 for the SEARCH text, 1 for the REPLACE text
            var closed = false;
            for (i++; i < lines.Length; i++)
            {
                if (reading == 0 && lines[i].TrimEnd() == DividerMarker) { reading = 1; continue; }
                if (reading == 1 && lines[i].TrimEnd() == ReplaceMarker) { closed = true; break; }
                (reading == 0 ? search : replace).Add(lines[i]);
            }
            if (!closed)
                throw new PatchRejectedException($"EDIT {relative}: block {count + 1} isn't finished. It needs {DividerMarker} between the old and new text and {ReplaceMarker} at the end.");

            var old = string.Join('\n', search);
            if (old.Length == 0)
                throw new PatchRejectedException($"EDIT {relative}: block {count + 1} has an empty SEARCH text. Name the existing lines to change.");
            var first = result.IndexOf(old, StringComparison.Ordinal);
            if (first < 0)
                throw new PatchRejectedException($"EDIT {relative}: block {count + 1}'s SEARCH text isn't in the file. Copy it exactly, including indentation: `{Truncate(search[0])}`.");
            if (result.IndexOf(old, first + 1, StringComparison.Ordinal) >= 0)
                throw new PatchRejectedException($"EDIT {relative}: block {count + 1}'s SEARCH text appears more than once. Include more surrounding lines so it matches one place: `{Truncate(search[0])}`.");
            result = string.Concat(result.AsSpan(0, first), string.Join('\n', replace), result.AsSpan(first + old.Length));
            count++;
        }
        if (count == 0) throw new PatchRejectedException($"EDIT {relative}: no {SearchMarker} blocks were given.");
        return result;
    }

    private static string Truncate(string text) => text.Length <= 80 ? text : text[..80] + "…";

    /// <summary>Returns the absolute path, or throws if it would land outside the repository or inside .git.</summary>
    public static string ResolveSafePath(string repositoryPath, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new PatchRejectedException("A file operation has an empty path.");

        var normalized = NormalizeSeparators(relativePath.Trim());
        if (Path.IsPathRooted(normalized) || normalized.StartsWith('/') || normalized.Contains(':'))
            throw new PatchRejectedException($"'{relativePath}' is not a relative path inside the repository.");

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Contains(".."))
            throw new PatchRejectedException($"'{relativePath}' escapes the repository.");
        if (segments.Any(s => s.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            throw new PatchRejectedException($"'{relativePath}' targets the .git directory.");

        var root = Path.GetFullPath(repositoryPath);
        var fullPath = Path.GetFullPath(Path.Combine(root, normalized));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new PatchRejectedException($"'{relativePath}' escapes the repository.");

        RejectLinkedAncestors(root, fullPath, relativePath);
        return fullPath;
    }

    private static void RejectLinkedAncestors(string root, string fullPath, string relativePath)
    {
        var current = fullPath;
        while (current.Length > root.Length)
        {
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && info.LinkTarget is not null)
                throw new PatchRejectedException($"'{relativePath}' passes through a symbolic link.");
            current = Path.GetDirectoryName(current)!;
        }
    }

    private static string NormalizeAction(FileOperationDto op)
    {
        var action = op.Action?.Trim().ToUpperInvariant();
        return action is FileActions.Create or FileActions.Overwrite or FileActions.Delete or FileActions.Edit
            ? action
            : throw new PatchRejectedException($"'{op.Action}' is not a valid action for '{op.RelativeFilePath}'.");
    }

    private static string NormalizeSeparators(string path) => path.Replace('\\', '/');

    // Models emit LF with no BOM. Keeping an existing file's style stops a Windows repo's diff marking every line as changed.
    private static void WritePreservingStyle(string path, string content, bool exists)
    {
        var useCrLf = false;
        var useBom = false;
        if (exists)
        {
            var existing = File.ReadAllBytes(path);
            useBom = existing.AsSpan().StartsWith(Encoding.UTF8.Preamble);
            useCrLf = existing.AsSpan().IndexOf("\r\n"u8) >= 0;
        }

        var text = content.ReplaceLineEndings("\n");
        if (useCrLf) text = text.Replace("\n", "\r\n");
        File.WriteAllText(path, text, new UTF8Encoding(useBom));
    }
}
