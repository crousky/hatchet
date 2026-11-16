using Factory.Contracts;
using Factory.Engine.Orchestration;
using Factory.Engine.Verification;

namespace Factory.Tests;

public sealed class VerificationGatesTests
{
    [Theory]
    [InlineData("infra/**", "infra/main.bicep")]
    [InlineData("infra/**", "infra/modules/storage/account.bicep")]
    [InlineData("infra", "infra/main.bicep")]
    [InlineData("infra/", "infra/main.bicep")]
    [InlineData(@".\infra\**", "infra/main.bicep")]
    [InlineData("INFRA/**", "infra/main.bicep")]
    [InlineData("*.sh", "deploy.sh")]
    [InlineData("*.sh", "scripts/ci/deploy.sh")]
    [InlineData("**/*.bicep", "main.bicep")]
    [InlineData("**/*.bicep", "infra/modules/account.bicep")]
    [InlineData(".github/workflows/*.yml", ".github/workflows/build.yml")]
    [InlineData("src/**/Migrations/*.cs", "src/Web/Data/Migrations/Init.cs")]
    [InlineData("src/**/Migrations/*.cs", "src/Migrations/Init.cs")]
    [InlineData("Dockerfile", "services/api/Dockerfile")]
    [InlineData("file?.txt", "file1.txt")]
    public void A_pattern_matches_the_paths_it_is_for(string pattern, string path) =>
        Assert.True(VerificationGates.Matches(pattern, path));

    [Theory]
    [InlineData("infra/**", "src/infra.cs")]
    [InlineData("infra/**", "infrastructure/main.bicep")]
    [InlineData("infra/**", "docs/infra/readme.md")]
    [InlineData("*.sh", "deploy.sh.txt")]
    [InlineData("*.sh", "deploy.shx")]
    [InlineData(".github/workflows/*.yml", ".github/workflows/nested/build.yml")]
    [InlineData(".github/workflows/*.yml", ".github/dependabot.yml")]
    [InlineData("file?.txt", "file12.txt")]
    [InlineData("a.b", "axb")]
    [InlineData("", "anything")]
    public void A_pattern_leaves_other_paths_alone(string pattern, string path) =>
        Assert.False(VerificationGates.Matches(pattern, path));

    [Fact]
    public void The_gates_for_a_change_keep_their_order_and_run_a_shared_command_once()
    {
        VerificationGate[] gates = [new("infra/**", "lint-infra"), new("*.sh", "shellcheck"), new("*.bicep", "lint-infra"), new("docs/**", "vale")];

        Assert.Equal(["lint-infra", "shellcheck"], VerificationGates.For(gates, ["infra/main.bicep", "scripts/run.sh", "src/App.cs"]).Select(g => g.Command));
        Assert.Empty(VerificationGates.For(gates, ["src/App.cs"]));
    }

    [Fact]
    public void Gates_are_trimmed_and_repeats_and_empty_rows_dropped()
    {
        var gates = VerificationGates.Normalize([new(@" .\infra\ ", " lint "), new("infra", "lint"), new("", " "), new("*.sh", "shellcheck")]);

        Assert.Equal([new VerificationGate("infra", "lint"), new VerificationGate("*.sh", "shellcheck")], gates);
        Assert.Equal(gates, VerificationGates.Parse(VerificationGates.ToJson(gates)));
        Assert.Null(VerificationGates.ToJson([]));
        Assert.Empty(VerificationGates.Parse(null));
    }

    [Theory]
    [InlineData("", "lint", "has no path pattern")]
    [InlineData("infra/**", "", "has no command")]
    [InlineData("../other/**", "lint", "not a path pattern inside the repository")]
    [InlineData("infra/**", "lint\nrm -rf .", "on one line")]
    public void A_gate_that_could_not_run_as_meant_is_refused(string paths, string command, string expected)
    {
        var refused = Assert.Throws<FactoryValidationException>(() => VerificationGates.Normalize([new(paths, command)]));
        Assert.Contains(expected, refused.Message);
    }

    [Fact]
    public void More_gates_than_the_limit_are_refused()
    {
        var gates = Enumerable.Range(0, VerificationGates.MaxGates + 1).Select(i => new VerificationGate($"dir{i}/**", "lint"));
        Assert.Throws<FactoryValidationException>(() => VerificationGates.Normalize(gates));
    }

    [Theory]
    [InlineData("az bicep build --file infra/main.bicep", "az")]
    [InlineData("  shellcheck scripts/*.sh", "shellcheck")]
    [InlineData("\"C:\\Program Files\\Tool\\tool.exe\" --check", "C:\\Program Files\\Tool\\tool.exe")]
    [InlineData("actionlint", "actionlint")]
    public void The_tool_is_the_program_the_command_starts_with(string command, string tool) =>
        Assert.Equal(tool, VerificationGates.ToolOf(command));

    [Theory]
    [InlineData("cd infra && az bicep build")]
    [InlineData("./scripts/lint.sh")]
    [InlineData(@"scripts\lint.cmd infra")]
    [InlineData("FOO=1 lint")]
    [InlineData("$LINTER infra")]
    [InlineData("(lint)")]
    [InlineData("\"unclosed")]
    [InlineData("")]
    public void A_command_whose_program_cannot_be_told_is_not_checked(string command) =>
        Assert.Null(VerificationGates.ToolOf(command));

    [Fact]
    public void The_first_gate_with_a_missing_tool_is_named()
    {
        VerificationGate[] gates = [new("*.sh", "shellcheck x.sh"), new("infra/**", "az bicep build"), new("docs/**", "cd docs && vale .")];

        var missing = VerificationGates.MissingTool(gates, tool => tool == "shellcheck");

        Assert.NotNull(missing);
        Assert.Contains("\"az\" isn't installed", missing);
        Assert.Contains("\"infra/**\"", missing);
        Assert.Null(VerificationGates.MissingTool(gates, _ => true));
    }

    [Fact]
    public void Git_is_found_on_the_machine_and_a_made_up_tool_is_not()
    {
        // The tests already need Git on the PATH.
        Assert.True(VerificationGates.IsInstalled("git"));
        Assert.False(VerificationGates.IsInstalled("hatchet-no-such-tool-7f3a"));
    }
}
