using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Factory.Engine.Verification;

public sealed record DetectedRunCommand(string Command, string Description);

/// <summary>
/// Works out how to start the software in a repository, from the files in it, the way <see cref="VerificationDetector"/>
/// works out how to test it. Used when someone asks to run the current version without saying how.
/// </summary>
public static partial class RunCommandDetector
{
    public static DetectedRunCommand? Detect(string directory)
    {
        if (!Directory.Exists(directory)) return null;
        var root = new DirectoryInfo(directory);
        return DetectDotnet(root) ?? DetectNode(root) ?? DetectCargo(root) ?? DetectGo(root) ?? DetectPython(root);
    }

    [GeneratedRegex(@"<OutputType>\s*(Exe|WinExe)\s*</OutputType>", RegexOptions.IgnoreCase)]
    private static partial Regex Executable();

    private static DetectedRunCommand? DetectDotnet(DirectoryInfo root)
    {
        var candidates = VerificationDetector.Find(root, "*.csproj").Concat(VerificationDetector.Find(root, "*.fsproj"))
            .Where(file => VerificationDetector.IsSafePath(Relative(root, file)))
            .Select(file => (File: file, Text: File.ReadAllText(file.FullName)))
            .Where(p => !p.Text.Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase)
                        && !p.Text.Contains("<IsTestProject>true", StringComparison.OrdinalIgnoreCase))
            .Select(p => (p.File, Rank: Rank(p.Text)))
            .Where(p => p.Rank > 0)
            .OrderByDescending(p => p.Rank)
            .ThenBy(p => Relative(root, p.File).Count(c => c == '/'))
            .ThenBy(p => Relative(root, p.File), StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidates.Count == 0) return null;

        var project = Relative(root, candidates[0].File);
        var quoted = project.Contains(' ') ? $"\"{project}\"" : project;
        return new DetectedRunCommand($"dotnet run --project {quoted}", $".NET project {candidates[0].File.Name}");
    }

    /// <summary>Web apps and desktop apps are what people mean by "the software", ahead of plain console tools.</summary>
    private static int Rank(string project)
    {
        if (project.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase)) return 3;
        if (project.Contains("<UseWPF>true", StringComparison.OrdinalIgnoreCase) || project.Contains("<UseWindowsForms>true", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(project, @"<OutputType>\s*WinExe", RegexOptions.IgnoreCase)) return 2;
        return Executable().IsMatch(project) ? 1 : 0;
    }

    private static DetectedRunCommand? DetectNode(DirectoryInfo root)
    {
        var packageJson = Path.Combine(root.FullName, "package.json");
        if (!File.Exists(packageJson)) return null;

        JsonObject? scripts;
        try
        {
            scripts = JsonNode.Parse(File.ReadAllText(packageJson))?["scripts"] as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        var script = new[] { "start", "dev" }.FirstOrDefault(name => scripts?[name] is JsonValue value && value.TryGetValue<string>(out _));
        if (script is null) return null;

        var (manager, install) =
            File.Exists(Path.Combine(root.FullName, "pnpm-lock.yaml")) ? ("pnpm", "pnpm install --ignore-scripts") :
            File.Exists(Path.Combine(root.FullName, "yarn.lock")) ? ("yarn", "yarn install --ignore-scripts") :
            ("npm", File.Exists(Path.Combine(root.FullName, "package-lock.json")) ? "npm ci --ignore-scripts" : "npm install --ignore-scripts");

        var run = script == "start" ? $"{manager} start" : $"{manager} run {script}";
        // A fresh copy of the commit has no installed packages.
        return new DetectedRunCommand(Directory.Exists(Path.Combine(root.FullName, "node_modules")) ? run : $"{install} && {run}", $"Node ({manager} {script})");
    }

    private static DetectedRunCommand? DetectCargo(DirectoryInfo root) =>
        File.Exists(Path.Combine(root.FullName, "Cargo.toml")) && (File.Exists(Path.Combine(root.FullName, "src", "main.rs")) || File.ReadAllText(Path.Combine(root.FullName, "Cargo.toml")).Contains("[[bin]]"))
            ? new DetectedRunCommand("cargo run", "Rust (Cargo)")
            : null;

    private static DetectedRunCommand? DetectGo(DirectoryInfo root) =>
        File.Exists(Path.Combine(root.FullName, "go.mod")) && File.Exists(Path.Combine(root.FullName, "main.go"))
            ? new DetectedRunCommand("go run .", "Go modules")
            : null;

    private static DetectedRunCommand? DetectPython(DirectoryInfo root)
    {
        var entry = new[] { "main.py", "app.py", "__main__.py" }.FirstOrDefault(name => File.Exists(Path.Combine(root.FullName, name)));
        return entry is null ? null : new DetectedRunCommand($"python {entry}", $"Python ({entry})");
    }

    private static string Relative(DirectoryInfo root, FileInfo file) =>
        Path.GetRelativePath(root.FullName, file.FullName).Replace('\\', '/');
}
