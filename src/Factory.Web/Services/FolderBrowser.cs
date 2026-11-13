namespace Factory.Web.Services;

public sealed record FolderEntry(string Name, string FullPath, bool IsGitRepository);

public sealed record FolderPlace(string Label, string Path, string Icon);

public sealed record FolderListing(string? Path, string? Parent, IReadOnlyList<FolderEntry> Folders, string? Error);

/// <summary>
/// Lists folders on this machine for the folder picker. A browser can't hand a page the full path of a
/// local folder, but Hatchet's server runs on the same machine, so it does the browsing.
/// </summary>
public static class FolderBrowser
{
    private const int MaxEntries = 1000;

    /// <summary>Drives on Windows, the file system root elsewhere. The picker shows these when no folder is open.</summary>
    public static FolderListing Roots()
    {
        if (!OperatingSystem.IsWindows())
            return new FolderListing(null, null, [new FolderEntry("/", "/", false)], null);

        var drives = DriveInfo.GetDrives()
            .Where(d => d.IsReady)
            .Select(d => new FolderEntry(DriveLabel(d), d.RootDirectory.FullName, false))
            .ToList();
        return new FolderListing(null, null, drives, null);
    }

    public static FolderListing List(string path)
    {
        // A UNC path would reach out to another machine, so it is treated like any path that can't be used.
        if (PathGuard.IsNetworkPath(path.Trim().Trim('"'))) return new FolderListing(path, null, [], "That isn't a valid path.");

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new FolderListing(path, null, [], "That isn't a valid path.");
        }

        var directory = new DirectoryInfo(fullPath);
        if (!directory.Exists) return new FolderListing(fullPath, directory.Parent?.FullName, [], "That folder doesn't exist.");

        try
        {
            var folders = directory.EnumerateDirectories()
                .Where(d => !IsHidden(d))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Take(MaxEntries)
                .Select(d => new FolderEntry(d.Name, d.FullName, IsGitRepository(d.FullName)))
                .ToList();
            return new FolderListing(directory.FullName, directory.Parent?.FullName, folders, null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return new FolderListing(directory.FullName, directory.Parent?.FullName, [], "Hatchet doesn't have permission to read that folder.");
        }
    }

    /// <summary>Common places to start from that exist on this machine.</summary>
    public static IReadOnlyList<FolderPlace> Places()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<FolderPlace>
        {
            new("Home", home, "folder"),
            new("Repos", Path.Combine(home, "source", "repos"), "git-branch"),
            new("Projects", Path.Combine(home, "projects"), "folder"),
            new("Code", Path.Combine(home, "code"), "folder"),
            new("Dev", Path.Combine(home, "dev"), "folder"),
            new("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "folder"),
            new("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "folder")
        };
        if (OperatingSystem.IsWindows())
        {
            candidates.Add(new("C:\\projects", @"C:\projects", "folder"));
            candidates.Add(new("C:\\src", @"C:\src", "folder"));
            candidates.Add(new("C:\\dev", @"C:\dev", "folder"));
        }

        return candidates
            .Where(p => !string.IsNullOrEmpty(p.Path) && Directory.Exists(p.Path))
            .DistinctBy(p => Path.GetFullPath(p.Path).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())
            .ToList();
    }

    /// <summary>Creates a folder for a new project and returns its path.</summary>
    /// <exception cref="ArgumentException">The name isn't a usable folder name or the folder already exists.</exception>
    public static string CreateFolder(string parent, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
            throw new ArgumentException("Use a plain folder name, without slashes or special characters.");

        var path = Path.Combine(parent, name);
        if (Directory.Exists(path) || File.Exists(path)) throw new ArgumentException($"\"{name}\" already exists here.");

        Directory.CreateDirectory(path);
        return path;
    }

    private static bool IsGitRepository(string path) =>
        Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git"));

    private static bool IsHidden(DirectoryInfo directory)
    {
        if (directory.Name.StartsWith('.')) return true;
        try
        {
            return (directory.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static string DriveLabel(DriveInfo drive)
    {
        var label = drive.VolumeLabel;
        var name = drive.Name.TrimEnd('\\');
        return string.IsNullOrWhiteSpace(label) ? name : $"{label} ({name})";
    }
}
