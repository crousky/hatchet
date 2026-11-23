namespace Hatchet.Tray;

/// <summary>The tray's command line. Start at login runs it with --repo, so it knows where to run Hatchet from.</summary>
internal sealed record TrayOptions
{
    public const string DefaultUrl = "http://localhost:5199";
    private const string SolutionFile = "LocalSoftwareFactory.sln";

    /// <summary>The Hatchet repository to run from.</summary>
    public required string RepositoryPath { get; init; }

    public string Url { get; init; } = DefaultUrl;

    /// <summary>Hatchet's data folder, when it isn't the default. Passed on as Hatchet:DataDirectory.</summary>
    public string? DataDirectory { get; init; }

    /// <summary>Close the tray that is already running, which stops the Hatchet it started, and wait for it.</summary>
    public bool Exit { get; init; }

    /// <summary>Have the tray start at login with these options, then run as usual.</summary>
    public bool EnableStartAtLogin { get; init; }

    public string ProjectPath => Path.Combine(RepositoryPath, "src", "Factory.Web", "Factory.Web.csproj");

    /// <summary>Where the tray keeps the server's log: Hatchet's data folder.</summary>
    public string LogPath => Path.Combine(
        DataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hatchet"),
        "logs", "hatchet.log");

    /// <summary>Names the tray's single-instance lock and exit event, so a tray for another port is a separate one.</summary>
    public string InstanceName => $@"Local\Hatchet.Tray.{new Uri(Url).Port}";

    /// <summary>
    /// Runs Hatchet from source in Release, so it rebuilds with the latest code on each start, and so the Debug
    /// build that "dotnet build" and "dotnet test" use isn't locked while it runs.
    /// </summary>
    public System.Diagnostics.ProcessStartInfo ServerStartInfo(string stopEventName)
    {
        var info = new System.Diagnostics.ProcessStartInfo("dotnet") { WorkingDirectory = RepositoryPath };
        foreach (var argument in new[] { "run", "--project", ProjectPath, "-c", "Release", "--no-launch-profile", "--", $"--Urls={Url}", $"--Hatchet:StopEvent={stopEventName}" })
            info.ArgumentList.Add(argument);
        if (DataDirectory is not null) info.ArgumentList.Add($"--Hatchet:DataDirectory={DataDirectory}");
        // Build nodes would otherwise linger after the build, inside the job, until Hatchet stops.
        info.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        return info;
    }

    /// <summary>The arguments start at login passes, which are these options without the one-off flags.</summary>
    public IEnumerable<string> StartAtLoginArguments()
    {
        yield return "--repo";
        yield return RepositoryPath;
        if (Url != DefaultUrl)
        {
            yield return "--url";
            yield return Url;
        }
        if (DataDirectory is not null)
        {
            yield return "--data-dir";
            yield return DataDirectory;
        }
    }

    /// <summary>Reads the command line. Without --repo, looks for the repository above the tray's own folder.</summary>
    public static TrayOptions Parse(IReadOnlyList<string> args, string baseDirectory)
    {
        string? repo = null, url = null, data = null;
        bool exit = false, startAtLogin = false;

        for (var i = 0; i < args.Count; i++)
        {
            string Value() => i + 1 < args.Count ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--repo": repo = Value(); break;
                case "--url": url = Value(); break;
                case "--data-dir": data = Value(); break;
                case "--exit": exit = true; break;
                case "--start-at-login": startAtLogin = true; break;
                default: throw new ArgumentException($"Unknown option {args[i]}.");
            }
        }

        if (url is not null && (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            throw new ArgumentException($"--url must be an http address, such as {DefaultUrl}.");

        repo ??= FindRepository(baseDirectory);
        if (repo is null && !exit)
            throw new ArgumentException("Couldn't find the Hatchet repository. Pass it with --repo, or install the tray with install-tray.ps1.");

        return new TrayOptions
        {
            // A trailing backslash would escape the closing quote of the start-at-login command line.
            RepositoryPath = repo is null ? "" : Path.TrimEndingDirectorySeparator(Path.GetFullPath(repo)),
            Url = url?.TrimEnd('/') ?? DefaultUrl,
            DataDirectory = data is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(data)),
            Exit = exit,
            EnableStartAtLogin = startAtLogin,
        };
    }

    private static string? FindRepository(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, SolutionFile))) return dir.FullName;
        return null;
    }
}
