using System.Text;

namespace Factory.Engine.Workspace;

public sealed record ContextFile(string RelativePath, string? Content, bool Truncated);

/// <summary>Builds the lightweight repository map and reads files for agent context.</summary>
public static class RepositoryScanner
{
    private const int MaxMapEntries = 3000;

    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "bin", "obj", "node_modules", ".vs", ".idea", ".vscode", "dist", "build", "target",
        "__pycache__", ".venv", "venv", "packages", "TestResults", ".next", "coverage"
    };

    public static string BuildRepoMap(string repositoryPath)
    {
        var lines = new List<string>();
        Walk(new DirectoryInfo(repositoryPath), "", lines);
        if (lines.Count == 0) return "(the repository has no files yet)";

        var map = string.Join('\n', lines.Take(MaxMapEntries));
        return lines.Count > MaxMapEntries ? $"{map}\n... and {lines.Count - MaxMapEntries} more files" : map;
    }

    private static void Walk(DirectoryInfo directory, string prefix, List<string> lines)
    {
        if (lines.Count > MaxMapEntries * 2) return;

        foreach (var file in directory.EnumerateFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            lines.Add($"{prefix}{file.Name} ({FormatSize(file.Length)})");

        foreach (var child in directory.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (IsIgnoredDirectory(child)) continue;
            Walk(child, $"{prefix}{child.Name}/", lines);
        }
    }

    public static bool IsIgnoredDirectory(DirectoryInfo directory) =>
        IgnoredDirectories.Contains(directory.Name) || directory.LinkTarget is not null;

    /// <summary>
    /// Reads files up to the per-file and total budgets. Unsafe paths and missing files come back
    /// with null content, so the agent is told they don't exist instead of the run failing.
    /// </summary>
    public static IReadOnlyList<ContextFile> ReadFiles(string repositoryPath, IEnumerable<string> relativePaths, int maxFileChars, int maxTotalChars)
    {
        var files = new List<ContextFile>();
        var remaining = maxTotalChars;
        foreach (var relative in relativePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string fullPath;
            try { fullPath = FilePatcher.ResolveSafePath(repositoryPath, relative); }
            catch (PatchRejectedException)
            {
                files.Add(new ContextFile(relative, null, false));
                continue;
            }

            if (!File.Exists(fullPath))
            {
                files.Add(new ContextFile(relative, null, false));
                continue;
            }

            if (FilePatcher.IsSecretPath(relative))
            {
                files.Add(new ContextFile(relative, "[not read: looks like a secrets file]", false));
                continue;
            }

            var content = File.ReadAllText(fullPath);
            var budget = Math.Min(maxFileChars, remaining);
            var truncated = content.Length > budget;
            if (truncated) content = content[..budget];
            remaining -= content.Length;
            files.Add(new ContextFile(relative, content, truncated));
        }
        return files;
    }

    public static string Render(IReadOnlyList<ContextFile> files)
    {
        if (files.Count == 0) return "(none)";

        var builder = new StringBuilder();
        foreach (var file in files)
        {
            builder.Append("--- ").Append(file.RelativePath).Append(" ---\n");
            if (file.Content is null)
            {
                builder.Append("(this file does not exist yet)\n");
                continue;
            }

            builder.Append(file.Content);
            if (!file.Content.EndsWith('\n')) builder.Append('\n');
            if (file.Truncated) builder.Append("[TRUNCATED: the rest of this file was not shown. Do not overwrite it unless you can reproduce the unseen part.]\n");
        }
        return builder.ToString();
    }

    private static string FormatSize(long bytes) =>
        bytes < 1024 ? $"{bytes} B" :
        bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB" :
        $"{bytes / (1024.0 * 1024.0):0.#} MB";
}
