using Factory.Contracts;
using Factory.Engine.Orchestration;

namespace Factory.Tests;

public class ModelChoicesTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData("settings", null)]
    [InlineData("Default", null)]
    [InlineData("opus", "claude:opus")]
    [InlineData(" Sonnet ", "claude:sonnet")]
    [InlineData("claude:haiku", "claude:haiku")]
    [InlineData("CLAUDE:Opus", "claude:opus")]
    [InlineData("local:qwen3.8", "local:qwen3.8")]
    [InlineData("local: my model ", "local:my model")]
    [InlineData("profile:strong", "profile:strong")]
    [InlineData(" Profile: All-Opus ", "profile:all-opus")]
    [InlineData("agy:gemini-4-argon-high", "agy:gemini-4-argon-high")]
    [InlineData("AGY:Gemini-3.8-Flash-High", "agy:gemini-3.8-flash-high")]
    [InlineData("gemini-4-argon-high", "agy:gemini-4-argon-high")]
    public void Choices_are_read_in_their_short_and_long_forms(string? given, string? expected) =>
        Assert.Equal(expected, ModelChoices.Normalize(given));

    [Theory]
    [InlineData("gpt-9")]
    [InlineData("claude:gpt")]
    [InlineData("local:")]
    [InlineData("profile:")]
    [InlineData("profile:two words")]
    [InlineData("profile:-x")]
    [InlineData("agy:")]
    public void Choices_that_make_no_sense_are_refused_with_the_ways_to_write_one(string given)
    {
        var error = Assert.Throws<FactoryValidationException>(() => ModelChoices.Normalize(given));

        Assert.True(error.Message.Contains("local:") || error.Message.Contains("claude:") || error.Message.Contains("profile:") || error.Message.Contains("agy:"));
    }

    [Fact]
    public void A_profile_settings_does_not_have_is_refused_by_name_with_the_ones_it_has()
    {
        var settings = new FactorySettings();

        Assert.Equal("profile:strong", ModelChoices.Require(settings, "profile:Strong"));
        Assert.Equal("claude:opus", ModelChoices.Require(settings, "opus"));
        Assert.Null(ModelChoices.Require(settings, null));
        var error = Assert.Throws<FactoryValidationException>(() => ModelChoices.Require(settings, "profile:stronk"));
        Assert.Contains("called \"stronk\"", error.Message);
        Assert.Contains("local, strong, all-opus", error.Message);
        Assert.Throws<FactoryValidationException>(() => ModelChoices.Apply(settings, "profile:stronk"));
    }

    [Fact]
    public void The_built_in_profiles_route_each_agent_as_their_names_say()
    {
        var settings = new FactorySettings();
        (bool OnClaude, string Model) RouteOf(string profile, AgentRole role)
        {
            var agent = AgentProfiles.Resolve(ModelChoices.Apply(settings, $"profile:{profile}"), role);
            return (agent.UsesClaude, agent.UsesClaude ? agent.ClaudeModel : "");
        }

        Assert.Equal((true, "opus"), RouteOf("local", AgentRole.TaskPlanner));
        Assert.Equal((false, ""), RouteOf("local", AgentRole.ContextSpecifier));
        Assert.Equal((false, ""), RouteOf("local", AgentRole.CodeGenerator));
        Assert.Equal((false, ""), RouteOf("local", AgentRole.BugFixer));
        Assert.Equal((true, "sonnet"), RouteOf("local", AgentRole.StaticReviewer));

        Assert.Equal((true, "opus"), RouteOf("strong", AgentRole.TaskPlanner));
        Assert.Equal((true, "sonnet"), RouteOf("strong", AgentRole.ContextSpecifier));
        Assert.Equal((true, "sonnet"), RouteOf("strong", AgentRole.CodeGenerator));
        Assert.Equal((true, "sonnet"), RouteOf("strong", AgentRole.BugFixer));
        Assert.Equal((true, "opus"), RouteOf("strong", AgentRole.StaticReviewer));
        // The last automatic fix attempt of a strong run goes to the strongest model.
        Assert.Equal("opus", FactoryOrchestrator.LastResortModel(ModelChoices.Apply(settings, "profile:strong")));

        Assert.All(Enum.GetValues<AgentRole>(), role => Assert.Equal((true, "opus"), RouteOf("all-opus", role)));
    }

    [Fact]
    public void A_strong_subtask_moves_its_building_agents_to_the_strong_profile_and_leaves_the_rest()
    {
        var run = ModelChoices.Apply(new FactorySettings(), "profile:local");
        (bool OnClaude, string Model) RouteOf(FactorySettings settings, AgentRole role)
        {
            var agent = AgentProfiles.Resolve(settings, role);
            return (agent.UsesClaude, agent.UsesClaude ? agent.ClaudeModel : "");
        }

        var strong = ModelChoices.ApplyTier(run, SubtaskTiers.Strong);

        Assert.All(ModelChoices.TieredRoles, role => Assert.Equal((true, "sonnet"), RouteOf(strong, role)));
        // The planner and the reviewer stay where the run's own profile put them.
        Assert.Equal((true, "opus"), RouteOf(strong, AgentRole.TaskPlanner));
        Assert.Equal((true, "sonnet"), RouteOf(strong, AgentRole.StaticReviewer));
        Assert.Equal("opus", FactoryOrchestrator.LastResortModel(strong));
        // The run's settings are shared by its other subtasks, so they are left alone.
        Assert.All(ModelChoices.TieredRoles, role => Assert.Equal((false, ""), RouteOf(run, role)));

        Assert.Same(run, ModelChoices.ApplyTier(run, SubtaskTiers.Local));
        Assert.Same(run, ModelChoices.ApplyTier(run, null));
        Assert.Same(run, ModelChoices.ApplyTier(run, ""));
        Assert.Equal("Context Specifier, Code Generator, Bug Fixer: Claude Sonnet", ModelChoices.DescribeTier(run));
    }

    [Fact]
    public void Strong_means_what_the_users_strong_profile_says_and_the_built_in_one_if_they_removed_it()
    {
        var settings = new FactorySettings();
        var strong = settings.RunProfiles.Single(p => p.Name == "strong");
        strong.Roles.Single(r => r.Role == AgentRole.CodeGenerator).ClaudeModel = "opus";
        strong.Roles.RemoveAll(r => r.Role == AgentRole.ContextSpecifier);

        var applied = ModelChoices.ApplyTier(settings, "Strong");
        Assert.Equal("opus", AgentProfiles.Resolve(applied, AgentRole.CodeGenerator).ClaudeModel);
        // A role the profile leaves out runs as the run has it.
        Assert.Equal(AgentProfiles.Resolve(settings, AgentRole.ContextSpecifier).UsesClaude, AgentProfiles.Resolve(applied, AgentRole.ContextSpecifier).UsesClaude);

        settings.RunProfiles.Clear();
        var builtIn = AgentProfiles.Resolve(ModelChoices.ApplyTier(settings, SubtaskTiers.Strong), AgentRole.CodeGenerator);
        Assert.Equal((true, "sonnet"), (builtIn.UsesClaude, builtIn.ClaudeModel));
    }

    [Theory]
    [InlineData(null, "local")]
    [InlineData("", "local")]
    [InlineData(" Local ", "local")]
    [InlineData("STRONG", "strong")]
    [InlineData("claude", null)]
    public void A_tier_is_local_or_strong(string? text, string? expected) => Assert.Equal(expected, SubtaskTiers.Parse(text));

    [Fact]
    public void A_profile_changes_where_an_agent_runs_and_nothing_else_and_leaves_out_roles_as_settings_has_them()
    {
        var settings = new FactorySettings();
        settings.ProfileOverrides.Add(new AgentProfileOverride { Role = AgentRole.BugFixer, ReasoningEffort = "low", Temperature = 0.7, SystemPrompt = "Fix it my way." });
        settings.ProfileOverrides.Add(new AgentProfileOverride { Role = AgentRole.TaskPlanner, Engine = AgentEngine.ClaudeCode, ClaudeModel = "haiku", ClaudeEffort = "low" });
        settings.RunProfiles =
        [
            new RunProfile
            {
                Name = "review-only",
                Roles =
                [
                    new RunProfileRole { Role = AgentRole.StaticReviewer, Engine = AgentEngine.ClaudeCode, ClaudeModel = "sonnet", ClaudeEffort = "max" },
                    new RunProfileRole { Role = AgentRole.BugFixer, Engine = AgentEngine.ClaudeCode, ClaudeModel = "opus" }
                ]
            }
        ];

        var applied = ModelChoices.Apply(settings, "profile:review-only");

        var reviewer = AgentProfiles.Resolve(applied, AgentRole.StaticReviewer);
        Assert.Equal((true, "sonnet", "max"), (reviewer.UsesClaude, reviewer.ClaudeModel, reviewer.ClaudeEffort));
        var fixer = AgentProfiles.Resolve(applied, AgentRole.BugFixer);
        Assert.Equal((true, "opus", "low", 0.7, "Fix it my way."), (fixer.UsesClaude, fixer.ClaudeModel, fixer.ReasoningEffort, fixer.Temperature, fixer.SystemPrompt));
        var planner = AgentProfiles.Resolve(applied, AgentRole.TaskPlanner);
        Assert.Equal((true, "haiku", "low"), (planner.UsesClaude, planner.ClaudeModel, planner.ClaudeEffort)); // Not in the profile, so as Settings has it.
        Assert.False(AgentProfiles.Resolve(applied, AgentRole.CodeGenerator).UsesClaude);
        Assert.Equal(2, settings.ProfileOverrides.Count); // The shared settings are untouched.
        Assert.False(AgentProfiles.Resolve(settings, AgentRole.StaticReviewer).UsesClaude);
    }

    [Fact]
    public void A_profile_is_described_by_name_and_explained_by_what_each_agent_runs_on()
    {
        var settings = new FactorySettings();

        Assert.Equal("strong profile", ModelChoices.Describe("profile:strong"));
        Assert.Equal("strong", ModelChoices.ProfileNameOf("Profile:Strong"));
        Assert.Null(ModelChoices.ProfileNameOf("claude:opus"));
        Assert.Null(ModelChoices.ClaudeModelOf("profile:strong"));
        Assert.Equal("Task Planner, Static Reviewer: Claude Opus · Context Specifier, Code Generator, Bug Fixer: Claude Sonnet.", ModelChoices.Explain(settings, "profile:strong"));
        Assert.Equal("Task Planner: Claude Opus · Context Specifier, Code Generator, Bug Fixer: local model · Static Reviewer: Claude Sonnet.", ModelChoices.Explain(settings, "profile:local"));
        Assert.Equal("Every agent in the run uses Claude Haiku.", ModelChoices.Explain(settings, "haiku"));
        Assert.Equal("Settings has no run profile called gone.", ModelChoices.Explain(settings, "profile:gone"));
        Assert.Null(ModelChoices.Explain(settings, ""));
    }

    [Fact]
    public void Settings_saved_before_run_profiles_existed_get_the_built_in_ones_and_saved_profiles_are_kept_as_they_are()
    {
        var old = System.Text.Json.JsonSerializer.Deserialize<FactorySettings>("""{"ModelId":"qwen3.8","ProfileOverrides":[]}""")!;
        Assert.Equal(["local", "strong", "all-opus"], old.RunProfiles.Select(p => p.Name));

        var edited = new FactorySettings();
        edited.RunProfiles.RemoveAll(p => p.Name != "strong");
        edited.RunProfiles[0].Roles.RemoveAll(r => r.Role == AgentRole.TaskPlanner);
        var saved = System.Text.Json.JsonSerializer.Deserialize<FactorySettings>(System.Text.Json.JsonSerializer.Serialize(edited))!;
        Assert.Equal("strong", Assert.Single(saved.RunProfiles).Name);
        Assert.Equal(4, saved.RunProfiles[0].Roles.Count);

        var clone = edited.Clone();
        clone.RunProfiles[0].Roles[0].ClaudeModel = "haiku";
        Assert.NotEqual("haiku", edited.RunProfiles[0].Roles[0].ClaudeModel);
    }

    [Theory]
    [InlineData("strong", true)]
    [InlineData("phase-5", true)]
    [InlineData("Strong", false)]
    [InlineData("", false)]
    [InlineData("two words", false)]
    [InlineData("-lead", false)]
    [InlineData("a:b", false)]
    public void Profile_names_are_lowercase_letters_digits_and_hyphens(string name, bool valid)
    {
        Assert.Equal(valid, RunProfile.IsValidName(name));
        var profile = new RunProfile { Name = name };
        Assert.Equal(valid, RunProfile.NameProblem(profile, [profile]) is null);
    }

    [Fact]
    public void Two_profiles_may_not_share_a_name()
    {
        var first = new RunProfile { Name = "strong" };
        var second = new RunProfile { Name = " strong " };

        Assert.Contains("Two run profiles", RunProfile.NameProblem(second, [first, second]));
    }

    [Fact]
    public void A_choice_is_described_in_words_and_only_a_claude_one_names_a_claude_model()
    {
        Assert.Null(ModelChoices.Describe(null));
        Assert.Equal("Claude Opus", ModelChoices.Describe("opus"));
        Assert.Equal("Gemini 4 Argon · high", ModelChoices.Describe("agy:gemini-4-argon-high"));
        Assert.Equal("qwen3.8 (local)", ModelChoices.Describe("local:qwen3.8"));
        Assert.Equal("sonnet", ModelChoices.ClaudeModelOf("claude:sonnet"));
        Assert.Null(ModelChoices.ClaudeModelOf("agy:gemini-4-argon-high"));
        Assert.Null(ModelChoices.ClaudeModelOf("local:qwen3.8"));
        Assert.Null(ModelChoices.ClaudeModelOf(null));
        Assert.Equal("gemini-4-argon-high", ModelChoices.AntigravityModelOf("agy:gemini-4-argon-high"));
        Assert.Null(ModelChoices.AntigravityModelOf("claude:sonnet"));
    }

    [Fact]
    public void An_antigravity_choice_moves_every_agent_to_that_model_and_keeps_each_ones_effort_and_prompt()
    {
        var settings = new FactorySettings();
        settings.ProfileOverrides.Add(new AgentProfileOverride { Role = AgentRole.BugFixer, ReasoningEffort = "low", Temperature = 0.7, SystemPrompt = "Fix it my way." });

        var applied = ModelChoices.Apply(settings, "agy:gemini-4-argon-high");

        foreach (var role in Enum.GetValues<AgentRole>())
        {
            var profile = AgentProfiles.Resolve(applied, role);
            Assert.True(profile.UsesAntigravity);
            Assert.Equal("gemini-4-argon-high", profile.AntigravityModel);
        }
        var fixer = AgentProfiles.Resolve(applied, AgentRole.BugFixer);
        Assert.Equal(("low", 0.7, "Fix it my way."), (fixer.ReasoningEffort, fixer.Temperature, fixer.SystemPrompt));
    }

    [Fact]
    public void A_claude_choice_moves_every_agent_to_that_model_and_keeps_each_ones_effort_and_prompt()
    {
        var settings = new FactorySettings();
        settings.ProfileOverrides.Add(new AgentProfileOverride { Role = AgentRole.BugFixer, ReasoningEffort = "low", Temperature = 0.7, SystemPrompt = "Fix it my way." });

        var applied = ModelChoices.Apply(settings, "claude:sonnet");

        foreach (var role in Enum.GetValues<AgentRole>())
        {
            var profile = AgentProfiles.Resolve(applied, role);
            Assert.True(profile.UsesClaude);
            Assert.Equal("sonnet", profile.ClaudeModel);
        }
        var fixer = AgentProfiles.Resolve(applied, AgentRole.BugFixer);
        Assert.Equal(("low", 0.7, "Fix it my way."), (fixer.ReasoningEffort, fixer.Temperature, fixer.SystemPrompt));
        var planner = AgentProfiles.Resolve(applied, AgentRole.TaskPlanner);
        Assert.Equal((AgentProfiles.TaskPlanner.ReasoningEffort, AgentProfiles.TaskPlanner.Temperature), (planner.ReasoningEffort, planner.Temperature));
    }

    [Fact]
    public void A_local_choice_moves_every_agent_off_claude_and_names_the_model()
    {
        var settings = new FactorySettings();
        foreach (var role in Enum.GetValues<AgentRole>())
            settings.ProfileOverrides.Add(new AgentProfileOverride { Role = role, Engine = AgentEngine.ClaudeCode, ClaudeModel = "opus" });

        var applied = ModelChoices.Apply(settings, "local:qwen-small");

        Assert.Equal("qwen-small", applied.ModelId);
        Assert.All(Enum.GetValues<AgentRole>(), role => Assert.False(AgentProfiles.Resolve(applied, role).UsesClaude));
    }

    [Fact]
    public void Applying_a_choice_never_changes_the_shared_settings()
    {
        var settings = new FactorySettings();
        settings.ProfileOverrides.Add(new AgentProfileOverride { Role = AgentRole.CodeGenerator, Engine = AgentEngine.Local });

        ModelChoices.Apply(settings, "claude:opus");
        ModelChoices.Apply(settings, "local:other");

        Assert.Equal("qwen3.8", settings.ModelId);
        var only = Assert.Single(settings.ProfileOverrides);
        Assert.Equal(AgentEngine.Local, only.Engine);
        Assert.Same(settings, ModelChoices.Apply(settings, null));
    }

    [Fact]
    public void The_options_offer_settings_every_local_model_once_and_every_claude_model()
    {
        var options = ModelChoices.Options(new FactorySettings { ModelId = "qwen3.8" }, ["qwen3.8", "llama-3", "", "llama-3", "text-embedding-nomic-embed-text-v1.5"]);

        Assert.Equal(["", "profile:local", "profile:strong", "profile:all-opus", "local:qwen3.8", "local:llama-3", "claude:opus", "claude:sonnet", "claude:haiku"], options.Select(o => o.Value));
        Assert.Equal("As set in Settings", options[0].Label);
        Assert.Equal("all-opus (Task Planner, Context Specifier, Code Generator, Static Reviewer, Bug Fixer: Claude Opus)", options[3].Label);
        Assert.All(options.Skip(1), o => Assert.Equal(o.Value, ModelChoices.Normalize(o.Value)));
    }

    [Fact]
    public void A_run_profile_can_route_an_agent_to_antigravity()
    {
        var settings = new FactorySettings
        {
            RunProfiles =
            [
                new RunProfile
                {
                    Name = "gemini-review",
                    Roles = [new RunProfileRole { Role = AgentRole.StaticReviewer, Engine = AgentEngine.Antigravity, AntigravityModel = "gemini-4-argon-high" }]
                }
            ]
        };

        var applied = ModelChoices.Apply(settings, "profile:gemini-review");
        var reviewer = AgentProfiles.Resolve(applied, AgentRole.StaticReviewer);

        Assert.True(reviewer.UsesAntigravity);
        Assert.Equal("gemini-4-argon-high", reviewer.AntigravityModel);
        Assert.Contains("Static Reviewer: Gemini 4 Argon · high", ModelChoices.Summarize(settings.RunProfiles[0]));
    }
}
