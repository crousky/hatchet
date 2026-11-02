using System.Text.Json;
using System.Text.Json.Nodes;
using Factory.Contracts;
using Factory.Engine.Workspace;

namespace Factory.Engine.Verification;

/// <summary>
/// Works out how to build and test a repository from the files in it. Runs that have no configured command
/// detect again before every check, so a project the planner sets up partway through is picked up.
/// </summary>
public static class VerificationDetector
{
    private const int MaxDepth = 4;

    public static DetectedVerification? Detect(string repositoryPath)
    {
        if (!Directory.Exists(repositoryPath)) return null;

        var root = new DirectoryInfo(repositoryPath);
        var steps = new List<(string Command, string Description)>();
        if (DetectDotnet(root) is { } dotnet) steps.Add(dotnet);
        if (DetectNode(root) is { } node) steps.Add(node);
        if (Has(root, "Cargo.toml")) steps.Add(("cargo build && cargo test", "Rust (Cargo)"));
        if (Has(root, "go.mod")) steps.Add(("go build ./... && go test ./...", "Go modules"));
        if (Has(root, "pom.xml")) steps.Add(("mvn -q -B verify", "Maven"));
        if (DetectGradle(root) is { } gradle) steps.Add(gradle);

        // Python scripts often sit beside another stack, so Python only counts alone or with real project files.
        var pythonProject = Has(root, "pyproject.toml") || Has(root, "setup.py") || Has(root, "requirements.txt") || Has(root, "pytest.ini");
        if ((steps.Count == 0 || pythonProject) && DetectPython(root, pythonProject) is { } python) steps.Add(python);

        return steps.Count == 0
            ? null
            : new DetectedVerification(string.Join(" && ", steps.Select(s => s.Command)), string.Join(" + ", steps.Select(s => s.Description)));
    }

    private static (string, string)? DetectDotnet(DirectoryInfo root)
    {
        var solutions = Find(root, "*.sln").Concat(Find(root, "*.slnx"))
            .OrderBy(f => Depth(root, f)).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (solutions.Count > 0)
        {
            var solution = Quote(Relative(root, solutions[0]));
            return ($"dotnet build {solution} --no-incremental && dotnet test {solution} --no-build", $".NET solution {solutions[0].Name}");
        }

        var projects = Find(root, "*.csproj").Concat(Find(root, "*.fsproj")).OrderBy(f => Relative(root, f), StringComparer.OrdinalIgnoreCase).ToList();
        if (projects.Count == 0) return null;

        var commands = projects.Select(p => $"dotnet build {Quote(Relative(root, p))} --no-incremental").ToList();
        commands.AddRange(projects.Where(IsTestProject).Select(p => $"dotnet test {Quote(Relative(root, p))} --no-build"));
        return (string.Join(" && ", commands), projects.Count == 1 ? $".NET project {projects[0].Name}" : $"{projects.Count} .NET projects");
    }

    private static bool IsTestProject(FileInfo project)
    {
        var text = File.ReadAllText(project.FullName);
        return text.Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase)
            || text.Contains("<IsTestProject>true", StringComparison.OrdinalIgnoreCase);
    }

    private static (string, string)? DetectNode(DirectoryInfo root)
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

        var (manager, install) =
            Has(root, "pnpm-lock.yaml") ? ("pnpm", "pnpm install") :
            Has(root, "yarn.lock") ? ("yarn", "yarn install") :
            Has(root, "bun.lockb") || Has(root, "bun.lock") ? ("bun", "bun install") :
            ("npm", Has(root, "package-lock.json") ? "npm ci" : "npm install");

        var steps = new List<string>();
        if (Script(scripts, "build") is not null) steps.Add($"{manager} run build");
        if (Script(scripts, "test") is { } test && !test.Contains("no test specified", StringComparison.OrdinalIgnoreCase)) steps.Add($"{manager} test");
        if (steps.Count == 0 && Has(root, "tsconfig.json")) steps.Add("npx tsc --noEmit");
        if (steps.Count == 0) return null;

        if (!Directory.Exists(Path.Combine(root.FullName, "node_modules"))) steps.Insert(0, install);
        return (string.Join(" && ", steps), $"Node ({manager})");
    }

    private static string? Script(JsonObject? scripts, string name) =>
        scripts?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static (string, string)? DetectGradle(DirectoryInfo root)
    {
        if (Has(root, "gradlew") || Has(root, "gradlew.bat"))
            return (OperatingSystem.IsWindows() ? "gradlew.bat build" : "./gradlew build", "Gradle wrapper");
        return Has(root, "build.gradle") || Has(root, "build.gradle.kts") ? ("gradle build", "Gradle") : null;
    }

    private static (string, string)? DetectPython(DirectoryInfo root, bool pythonProject)
    {
        var sources = Find(root, "*.py").ToList();
        if (sources.Count == 0 && !pythonProject) return null;

        var hasTests = sources.Any(f => f.Name.StartsWith("test_", StringComparison.Ordinal) || f.Name.EndsWith("_test.py", StringComparison.Ordinal));
        return hasTests
            ? ("python -m pytest -q", "Python (pytest)")
            : ("python -m compileall -q -x \"(venv|node_modules|\\.git)\" .", "Python (syntax check, no tests found)");
    }

    private static bool Has(DirectoryInfo root, string fileName) =>
        File.Exists(Path.Combine(root.FullName, fileName));

    private static IEnumerable<FileInfo> Find(DirectoryInfo root, string pattern) => Find(root, pattern, 0);

    private static IEnumerable<FileInfo> Find(DirectoryInfo directory, string pattern, int depth)
    {
        IEnumerable<FileInfo> files;
        IEnumerable<DirectoryInfo> children;
        try
        {
            files = directory.EnumerateFiles(pattern).ToList();
            children = directory.EnumerateDirectories().ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            yield break;
        }

        foreach (var file in files) yield return file;
        if (depth >= MaxDepth) yield break;

        foreach (var child in children)
        {
            if (RepositoryScanner.IsIgnoredDirectory(child)) continue;
            foreach (var file in Find(child, pattern, depth + 1)) yield return file;
        }
    }

    private static int Depth(DirectoryInfo root, FileInfo file) => Relative(root, file).Count(c => c == '/');

    private static string Relative(DirectoryInfo root, FileInfo file) =>
        Path.GetRelativePath(root.FullName, file.FullName).Replace('\\', '/');

    private static string Quote(string path) => path.Contains(' ') ? $"\"{path}\"" : path;
}
