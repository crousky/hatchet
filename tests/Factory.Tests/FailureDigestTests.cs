using Factory.Engine.Verification;

namespace Factory.Tests;

public class FailureDigestTests
{
    [Fact]
    public void Picks_out_each_error_once_from_build_noise()
    {
        const string output = """
              Determining projects to restore...
              Restored C:\demo\src\Demo.App\Demo.App.csproj (in 608 ms).
              Demo.Core -> C:\demo\src\Demo.Core\bin\Debug\net9.0\Demo.Core.dll
            C:\demo\src\Demo.App\App.xaml.cs(15,13): error CS0104: 'ILogger' is an ambiguous reference [C:\demo\src\Demo.App\Demo.App_0iwv3kbi_wpftmp.csproj]
            C:\demo\src\Demo.App\App.xaml.cs(15,13): error CS0104: 'ILogger' is an ambiguous reference [C:\demo\src\Demo.App\Demo.App.csproj]
            Build FAILED.
                0 Warning(s)
                1 Error(s)
            """;

        Assert.Equal(
            ["C:\\demo\\src\\Demo.App\\App.xaml.cs(15,13): error CS0104: 'ILogger' is an ambiguous reference", "Build FAILED.", "1 Error(s)"],
            FailureDigest.Errors(output));
    }

    [Fact]
    public void Ignores_summaries_that_report_success()
    {
        Assert.Empty(FailureDigest.Errors("    0 Error(s)\nPassed!  - Failed:     0, Passed:   12\nTests: 3 passed, 0 failed"));
    }

    [Fact]
    public void Finds_test_and_script_failures()
    {
        var errors = FailureDigest.Errors("  Failed NetMap.Tests.MacTests.Parses [12 ms]\nnpm ERR! code ELIFECYCLE\nTraceback (most recent call last):");

        Assert.Equal(3, errors.Count);
    }

    [Fact]
    public void Names_the_repository_files_errors_point_at()
    {
        using var repo = new TempDirectory();
        repo.Write("src/App/Settings.cs", "x");
        repo.Write("src/App/Views/Launcher.xaml", "x");
        repo.Write("web/a.ts", "x");
        var settings = Path.Combine(repo.Path, "src", "App", "Settings.cs");

        var files = FailureDigest.FilesNamed([
            $"{settings}(207,36): error CS0117: 'HotkeyModifiers' does not contain a definition for 'None'",
            $"{Path.Combine(repo.Path, "src", "App", "Views", "Launcher.xaml")}(36,22): error MC4102: not supported",
            "web/a.ts:12:5 - error TS2304: Cannot find name 'x'.",
            $"{settings}(10,1): error CS1002: ; expected",              // Named once.
            @"C:\elsewhere\Other.cs(1,1): error CS0001: outside",       // Not in the repository.
            "src/App/Missing.cs(1,1): error CS0002: gone",              // Doesn't exist.
            "Build FAILED."
        ], repo.Path);

        Assert.Equal(["src/App/Settings.cs", "src/App/Views/Launcher.xaml", "web/a.ts"], files);
    }
}
