using System.Text;
using Factory.Contracts;

namespace Factory.Engine.Workspace;

public sealed record PatchResult(IReadOnlyList<string> Applied, IReadOnlyList<string> Warnings);

/// <summary>The model proposed an operation that may not be applied. Nothing has been written.</summary>
public sealed class PatchRejectedException(string message) : Exception(message);

/// <summary>Applies model-generated file operations, confined to the repository.</summary>
public static class FilePatcher
{
    /// <summary>Validates every operation first, so a rejected set leaves the disk untouched.</summary>
    public static PatchResult Apply(string repositoryPath, IReadOnlyList<FileOperationDto> operations)
    {
        var root = Path.GetFullPath(repositoryPath);
        var resolved = operations
            .Select(op => (Op: op, Action: NormalizeAction(op), Path: ResolveSafePath(root, op.RelativeFilePath)))
            .ToList();

        var applied = new List<string>();
        var warnings = new List<string>();
        foreach (var (op, action, path) in resolved)
        {
            var relative = NormalizeSeparators(op.RelativeFilePath);
            if (action == FileActions.Delete)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    applied.Add($"DELETE {relative}");
                }
                else
                {
                    warnings.Add($"DELETE {relative}: the file does not exist.");
                }
                continue;
            }

            var exists = File.Exists(path);
            if (action == FileActions.Create && exists) warnings.Add($"CREATE {relative}: the file already existed and was overwritten.");
            if (action == FileActions.Overwrite && !exists) warnings.Add($"OVERWRITE {relative}: the file did not exist and was created.");

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            WritePreservingStyle(path, op.FileContent, exists);
            applied.Add($"{action} {relative}");
        }

        return new PatchResult(applied, warnings);
    }

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
        return action is FileActions.Create or FileActions.Overwrite or FileActions.Delete
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
