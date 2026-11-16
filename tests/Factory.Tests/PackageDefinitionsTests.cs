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
            packageFolders = new Dictionary<string, object> { [PackagesRoot + Path.DirectorySeparatorChar] = new { } },
            // As a web project's restore writes it: ASP.NET Core comes as a shared framework, not a package.
            project = new
            {
                frameworks = new Dictionary<string, object>
                {
                    ["net9.0"] = new { frameworkReferences = new Dictionary<string, object> { ["Microsoft.AspNetCore.App"] = new { }, ["Microsoft.NETCore.App"] = new { } } }
                }
            }
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
    public void Gives_the_signatures_of_shared_framework_types_the_text_names()
    {
        // The tests' own web host needs ASP.NET Core installed, so some version of it is on the machine.
        var found = PackageDefinitions.FindFramework(_repo.Path,
            "Create the user with UserManager, then send them on with NavigationManager. Keep a StringBuilder and an HttpClient.",
            new HashSet<string>(), maxTypes: 4, maxCharsPerType: 6000);

        var manager = found.First(f => f.RelativePath.EndsWith(": Microsoft.AspNetCore.Identity.UserManager"));
        Assert.StartsWith("framework Microsoft.AspNetCore.App ", manager.RelativePath);
        Assert.Contains(" shared framework: its public members, without bodies.", manager.Content);
        Assert.Contains("namespace Microsoft.AspNetCore.Identity;", manager.Content);
        Assert.Contains("public class UserManager<TUser>", manager.Content);
        Assert.Contains("CreateAsync(TUser user, string password);", manager.Content);

        var navigation = found.First(f => f.RelativePath.EndsWith(": Microsoft.AspNetCore.Components.NavigationManager"));
        Assert.Contains("public void NavigateTo(string uri, bool forceLoad", navigation.Content);

        // The base class library isn't looked up: agents know it, and nearly every name in a file is one of its types.
        Assert.DoesNotContain(found, f => f.RelativePath.EndsWith(".StringBuilder") || f.RelativePath.EndsWith(".HttpClient"));
        // Framework types are a lookup of their own, so they don't take the packages' places.
        Assert.DoesNotContain(PackageDefinitions.Find(_repo.Path, "UserManager", new HashSet<string>(), 6, 6000), f => f.RelativePath.Contains("UserManager"));
    }

    [Fact]
    public void A_framework_type_gives_way_to_the_repositorys_own_and_to_a_package_of_the_same_name()
    {
        var skipped = PackageDefinitions.FindFramework(_repo.Path, "UserManager and NavigationManager", new HashSet<string> { "UserManager" }, 4, 6000);
        Assert.DoesNotContain(skipped, f => f.RelativePath.Contains("UserManager"));
        Assert.Contains(skipped, f => f.RelativePath.EndsWith(".NavigationManager"));

        if (!PackageIsCached) return;
        // ILogger is in the logging package the project compiles against, as well as in the framework.
        Assert.Empty(PackageDefinitions.FindFramework(_repo.Path, "ILogger", new HashSet<string>(), 4, 6000));
    }

    [Fact]
    public void A_project_that_references_no_framework_beyond_the_base_library_has_no_framework_types()
    {
        using var console = new TempDirectory();
        console.Write("src/App/obj/project.assets.json", JsonSerializer.Serialize(new
        {
            version = 3,
            targets = new Dictionary<string, object> { ["net9.0"] = new Dictionary<string, object>() },
            libraries = new Dictionary<string, object>(),
            project = new { frameworks = new Dictionary<string, object> { ["net9.0"] = new { frameworkReferences = new Dictionary<string, object> { ["Microsoft.NETCore.App"] = new { } } } } }
        }));

        Assert.Empty(PackageDefinitions.FindFramework(console.Path, "UserManager and HttpClient", new HashSet<string>(), 4, 6000));
    }

    [Fact]
    public void A_repository_that_was_never_restored_has_no_package_types()
    {
        using var bare = new TempDirectory();
        bare.Write("src/App/Program.cs", "class Program { }\n");

        Assert.Empty(PackageDefinitions.FindFramework(bare.Path, "UserManager", new HashSet<string>(), 4, 6000));
        Assert.Empty(PackageDefinitions.Find(bare.Path, "ILogger", new HashSet<string>(), 6, 6000));
        Assert.Empty(PackageDefinitions.KnownNames(bare.Path));
    }
}
