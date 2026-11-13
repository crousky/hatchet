using Factory.Engine.Verification;

namespace Factory.Tests;

public class VerificationDetectorTests : IDisposable
{
    private readonly TempDirectory _repo = new();

    public void Dispose() => _repo.Dispose();

    private const string TestProject = """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="1" /></ItemGroup></Project>""";
    private const string AppProject = """<Project Sdk="Microsoft.NET.Sdk"></Project>""";

    [Fact]
    public void An_empty_repository_has_nothing_to_detect()
    {
        _repo.Write("README.md", "# Hello");

        Assert.Null(VerificationDetector.Detect(_repo.Path));
    }

    [Fact]
    public void A_solution_anywhere_near_the_root_is_built_then_tested()
    {
        _repo.Write("src/My App.sln", "");
        _repo.Write("src/App/App.csproj", AppProject);

        var detected = VerificationDetector.Detect(_repo.Path)!;

        Assert.Equal("dotnet build \"src/My App.sln\" --no-incremental && dotnet test \"src/My App.sln\" --no-build", detected.Command);
    }

    [Fact]
    public void Projects_without_a_solution_are_each_built_and_test_projects_tested()
    {
        _repo.Write("src/App/App.csproj", AppProject);
        _repo.Write("tests/App.Tests/App.Tests.csproj", TestProject);
        _repo.Write("src/App/obj/Ignored.csproj", AppProject);

        var detected = VerificationDetector.Detect(_repo.Path)!;

        Assert.Equal(
            "dotnet build src/App/App.csproj --no-incremental && dotnet build tests/App.Tests/App.Tests.csproj --no-incremental && dotnet test tests/App.Tests/App.Tests.csproj --no-build",
            detected.Command);
    }

    [Fact]
    public void Node_runs_build_and_test_scripts_and_installs_when_needed()
    {
        _repo.Write("package.json", """{ "scripts": { "build": "tsc", "test": "vitest run" } }""");
        _repo.Write("package-lock.json", "{}");

        Assert.Equal("npm ci --ignore-scripts && npm run build && npm test", VerificationDetector.Detect(_repo.Path)!.Command);

        Directory.CreateDirectory(_repo.File("node_modules"));
        Assert.Equal("npm run build && npm test", VerificationDetector.Detect(_repo.Path)!.Command);
    }

    [Fact]
    public void Node_ignores_the_npm_init_placeholder_test_script()
    {
        _repo.Write("package.json", """{ "scripts": { "test": "echo \"Error: no test specified\" && exit 1" } }""");

        Assert.Null(VerificationDetector.Detect(_repo.Path));

        _repo.Write("tsconfig.json", "{}");
        Assert.Equal("npm install --ignore-scripts && npx tsc --noEmit", VerificationDetector.Detect(_repo.Path)!.Command);
    }

    [Fact]
    public void Node_uses_the_package_manager_its_lockfile_names()
    {
        _repo.Write("package.json", """{ "scripts": { "test": "jest" } }""");
        _repo.Write("pnpm-lock.yaml", "");

        Assert.Equal("pnpm install --ignore-scripts && pnpm test", VerificationDetector.Detect(_repo.Path)!.Command);
    }

    [Fact]
    public void Python_runs_pytest_when_tests_exist_and_a_syntax_check_otherwise()
    {
        _repo.Write("app/main.py", "print(1)");
        Assert.StartsWith("python -m compileall", VerificationDetector.Detect(_repo.Path)!.Command);

        _repo.Write("tests/test_main.py", "def test_x(): pass");
        Assert.Equal("python -m pytest -q", VerificationDetector.Detect(_repo.Path)!.Command);
    }

    [Fact]
    public void Stray_python_scripts_do_not_add_a_check_to_another_stack()
    {
        _repo.Write("App.csproj", AppProject);
        _repo.Write("scripts/release.py", "print(1)");

        Assert.DoesNotContain("python", VerificationDetector.Detect(_repo.Path)!.Command);
    }

    [Fact]
    public void A_full_stack_repository_checks_every_part()
    {
        _repo.Write("Api.sln", "");
        _repo.Write("package.json", """{ "scripts": { "test": "vitest run" } }""");
        Directory.CreateDirectory(_repo.File("node_modules"));

        var detected = VerificationDetector.Detect(_repo.Path)!;

        Assert.Equal("dotnet build Api.sln --no-incremental && dotnet test Api.sln --no-build && npm test", detected.Command);
        Assert.Equal(".NET solution Api.sln + Node (npm)", detected.Description);
    }

    [Theory]
    [InlineData("Cargo.toml", "cargo build && cargo test")]
    [InlineData("go.mod", "go build ./... && go test ./...")]
    [InlineData("pom.xml", "mvn -q -B verify")]
    [InlineData("build.gradle", "gradle build")]
    public void Other_stacks_use_their_standard_commands(string marker, string command)
    {
        _repo.Write(marker, "");

        Assert.Equal(command, VerificationDetector.Detect(_repo.Path)!.Command);
    }

    [Fact]
    public void A_solution_with_shell_characters_in_its_name_is_ignored()
    {
        _repo.Write("a&b.sln", "");

        Assert.Null(VerificationDetector.Detect(_repo.Path));
    }

    [Fact]
    public void An_unsafe_solution_name_falls_through_to_the_next_safe_candidate()
    {
        _repo.Write("a&b.sln", "");
        _repo.Write("src/Good.sln", "");

        Assert.Equal("dotnet build src/Good.sln --no-incremental && dotnet test src/Good.sln --no-build", VerificationDetector.Detect(_repo.Path)!.Command);
    }

    [Fact]
    public void A_project_with_shell_characters_in_its_name_is_ignored_by_both_detectors()
    {
        _repo.Write("a&b.csproj", """<Project Sdk="Microsoft.NET.Sdk.Web"></Project>""");

        Assert.Null(VerificationDetector.Detect(_repo.Path));
        Assert.Null(RunCommandDetector.Detect(_repo.Path));
    }

    [Fact]
    public void A_solution_name_with_a_space_is_still_quoted()
    {
        _repo.Write("my app.sln", "");

        Assert.Equal("dotnet build \"my app.sln\" --no-incremental && dotnet test \"my app.sln\" --no-build", VerificationDetector.Detect(_repo.Path)!.Command);
    }
}
