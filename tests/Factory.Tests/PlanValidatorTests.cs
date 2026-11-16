using Factory.Contracts;
using Factory.Engine.Orchestration;
using Factory.Engine.Skills;
using Factory.Engine.Workspace;

namespace Factory.Tests;

public sealed class PlanValidatorTests : IAsyncLifetime
{
    private readonly TempDirectory _repo = new();
    private readonly GitWorkspace _git;

    public PlanValidatorTests() => _git = new GitWorkspace(_repo.Path);

    public async Task InitializeAsync()
    {
        await _git.InitializeAsync();
        await _git.EnsureInitialCommitAsync();
        _repo.Write(".gitignore", "samples/\n");
        _repo.Write("src/App.cs", "class App { }");
        _repo.Write("docs/notes.md", "notes");
        _repo.Write(".hatchet/skills/invariants/SKILL.md", "---\nname: invariants\ndescription: Rules.\n---\nKeep to them.\n");
        await _git.StageAllAsync();
        await _git.CommitStagedAsync("Add files");
        _repo.Write("samples/real.csv", "private");
    }

    public Task DisposeAsync()
    {
        _repo.Dispose();
        return Task.CompletedTask;
    }

    private static SubtaskEdit Step(string title, string[]? modify = null, string[]? create = null, string[]? read = null, string[]? skills = null) =>
        new(null, title, $"Do {title}", (modify ?? []).ToList(), (create ?? []).ToList(), (read ?? []).ToList(), "", skills?.ToList());

    private Task<IReadOnlyList<PlanWarning>> ValidateAsync(params SubtaskEdit[] plan) =>
        PlanValidator.ValidateAsync(_repo.Path, plan, new SkillLibrary(null).Load(_repo.Path));

    [Fact]
    public async Task A_plan_that_matches_the_repository_has_no_warnings()
    {
        var warnings = await ValidateAsync(
            Step("one", modify: ["src/App.cs"], create: ["src/New.cs"], read: [@"docs\notes.md"], skills: ["Invariants"]),
            // A later step may change or read what an earlier one creates.
            Step("two", modify: ["./src/New.cs"], read: ["src/New.cs", "src/App.cs"], create: ["src/Another.cs"]));

        Assert.Empty(warnings);
    }

    [Fact]
    public async Task An_unknown_skill_is_named_with_its_step_and_the_skills_there_are()
    {
        var warning = Assert.Single(await ValidateAsync(Step("one", create: ["a.txt"]), Step("two", create: ["b.txt"], skills: ["invariants", "nimnal-invarients"])));

        Assert.Equal((PlanWarningKinds.UnknownSkill, 2, "two", "nimnal-invarients"), (warning.Kind, warning.Step, warning.StepTitle, warning.Subject));
        Assert.Contains("The skills are: invariants.", warning.Message);
    }

    [Fact]
    public async Task A_file_to_modify_or_read_that_does_not_exist_is_named()
    {
        var warnings = await ValidateAsync(Step("one", modify: ["src/Ap.cs"], read: ["docs/missing.md"]));

        Assert.Equal(
            [(PlanWarningKinds.FileToModifyMissing, "src/Ap.cs"), (PlanWarningKinds.FileToReadMissing, "docs/missing.md")],
            warnings.Select(w => (w.Kind, w.Subject)));
    }

    [Fact]
    public async Task A_file_to_create_that_exists_or_that_an_earlier_step_creates_is_named()
    {
        var warnings = await ValidateAsync(Step("one", create: ["src/App.cs", "src/New.cs"]), Step("two", create: ["src/new.cs"]));

        Assert.Equal(
            [(PlanWarningKinds.FileToCreateExists, 1, "src/App.cs"), (PlanWarningKinds.FileToCreateExists, 2, "src/new.cs")],
            warnings.Select(w => (w.Kind, w.Step, w.Subject)));
        Assert.Contains("already exists", warnings[0].Message);
        Assert.Contains("step 1 already creates it", warnings[1].Message);
    }

    [Fact]
    public async Task Secret_and_ignored_paths_are_named_wherever_the_step_lists_them()
    {
        _repo.Write("src/Api/local.settings.json", "KEY");

        var warnings = await ValidateAsync(Step("one", modify: ["src/Api/local.settings.json"], read: ["samples/real.csv"], create: ["samples/new.csv", "infra/.env.wizard"]));

        Assert.Equal(
            [
                (PlanWarningKinds.SecretPath, "src/Api/local.settings.json"), (PlanWarningKinds.IgnoredPath, "samples/new.csv"),
                (PlanWarningKinds.SecretPath, "infra/.env.wizard"), (PlanWarningKinds.IgnoredPath, "samples/real.csv")
            ],
            warnings.Select(w => (w.Kind, w.Subject)));
    }

    [Theory]
    [InlineData("../outside.cs")]
    [InlineData(".git/config")]
    [InlineData("C:/Windows/win.ini")]
    public async Task A_path_hatchet_refuses_is_named_once_and_not_also_called_missing(string path)
    {
        var warning = Assert.Single(await ValidateAsync(Step("one", modify: [path])));

        Assert.Equal((PlanWarningKinds.InvalidPath, path), (warning.Kind, warning.Subject));
    }
}
