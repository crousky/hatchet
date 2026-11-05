using System.Text.Json;
using Factory.Engine.Workspace;

namespace Factory.Tests;

/// <summary>
/// Reads a real package from the local NuGet cache: the logging abstractions Hatchet itself builds against, so the
/// cache has them wherever these tests build.
/// </summary>
public class PackageDefinitionsTests : IDisposable
{
    private const string Package = "Microsoft.Extensions.Logging.Abstractions";
    private const string Version = "9.0.22";
    private readonly TempDirectory _repo = new();

    private static string PackagesRoot =>
        Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

    public PackageDefinitionsTests()
    {
        var assets = new
        {
            version = 3,
            targets = new Dictionary<string, object>
            {
                ["net9.0"] = new Dictionary<string, object>
                {
                    [$"{Package}/{Version}"] = new { type = "package", compile = new Dictionary<string, object> { [$"lib/net9.0/{Package}.dll"] = new { } } },
                    ["Some.Project/1.0.0"] = new { type = "project", compile = new Dictionary<string, object> { ["bin/placeholder/Some.Project.dll"] = new { } } }
                }
            },
            libraries = new Dictionary<string, object>
            {
                [$"{Package}/{Version}"] = new { type = "package", path = $"{Package.ToLowerInvariant()}/{Version}" }
            },
            packageFolders = new Dictionary<string, object> { [PackagesRoot + Path.DirectorySeparatorChar] = new { } }
        };
        _repo.Write("src/App/obj/project.assets.json", JsonSerializer.Serialize(assets));
        _repo.Write("src/App/Widget.cs", "public sealed class LogLevelPicker { }\n");
    }

    public void Dispose() => _repo.Dispose();

    private static bool PackageIsCached => File.Exists(Path.Combine(PackagesRoot, Package.ToLowerInvariant(), Version, "lib", "net9.0", $"{Package}.dll"));

    [Fact]
    public void Gives_the_signatures_of_package_types_the_text_names()
    {
        if (!PackageIsCached) return;

        var found = PackageDefinitions.Find(_repo.Path, "Log through an ILogger at the right LogLevel when a Scan finishes.",
            new HashSet<string>(), maxTypes: 6, maxCharsPerType: 6000);

        var logger = found.First(f => f.RelativePath == $"package {Package} {Version}: Microsoft.Extensions.Logging.ILogger");
        Assert.Contains("namespace Microsoft.Extensions.Logging;", logger.Content);
        Assert.Contains("public interface ILogger", logger.Content);
        Assert.Contains("bool IsEnabled(LogLevel logLevel);", logger.Content);
        Assert.Contains("void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter);", logger.Content);

        var level = found.Single(f => f.RelativePath.EndsWith(": Microsoft.Extensions.Logging.LogLevel"));
        Assert.Contains("public enum LogLevel", level.Content);
        Assert.Contains("Warning = 3,", level.Content);

        // A capitalised word isn't looked up, so prose doesn't pull in types that happen to share its name.
        Assert.DoesNotContain(found, f => f.RelativePath.EndsWith(".Scan"));
    }

    [Fact]
    public void Leaves_out_names_the_repository_declares_itself_and_knows_every_package_type()
    {
        if (!PackageIsCached) return;

        var found = PackageDefinitions.Find(_repo.Path, "Use ILogger and LogLevel.", new HashSet<string> { "LogLevel" }, 6, 6000);

        Assert.DoesNotContain(found, f => f.RelativePath.EndsWith(".LogLevel"));
        Assert.Contains(found, f => f.RelativePath.EndsWith(".ILogger"));
        Assert.Contains("LoggerExtensions", PackageDefinitions.KnownNames(_repo.Path));
    }

    [Fact]
    public void A_repository_that_was_never_restored_has_no_package_types()
    {
        using var bare = new TempDirectory();
        bare.Write("src/App/Program.cs", "class Program { }\n");

        Assert.Empty(PackageDefinitions.Find(bare.Path, "ILogger", new HashSet<string>(), 6, 6000));
        Assert.Empty(PackageDefinitions.KnownNames(bare.Path));
    }
}
