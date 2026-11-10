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
    public void Choices_are_read_in_their_short_and_long_forms(string? given, string? expected) =>
        Assert.Equal(expected, ModelChoices.Normalize(given));

    [Theory]
    [InlineData("gpt-9")]
    [InlineData("claude:gpt")]
    [InlineData("local:")]
    public void Choices_that_make_no_sense_are_refused_with_the_ways_to_write_one(string given)
    {
        var error = Assert.Throws<FactoryValidationException>(() => ModelChoices.Normalize(given));

        Assert.True(error.Message.Contains("local:") || error.Message.Contains("claude:"));
    }

    [Fact]
    public void A_choice_is_described_in_words_and_only_a_claude_one_names_a_claude_model()
    {
        Assert.Null(ModelChoices.Describe(null));
        Assert.Equal("Claude Opus", ModelChoices.Describe("opus"));
        Assert.Equal("qwen3.8 (local)", ModelChoices.Describe("local:qwen3.8"));
        Assert.Equal("sonnet", ModelChoices.ClaudeModelOf("claude:sonnet"));
        Assert.Null(ModelChoices.ClaudeModelOf("local:qwen3.8"));
        Assert.Null(ModelChoices.ClaudeModelOf(null));
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

        Assert.Equal(["", "local:qwen3.8", "local:llama-3", "claude:opus", "claude:sonnet", "claude:haiku"], options.Select(o => o.Value));
        Assert.Equal("As set in Settings", options[0].Label);
        Assert.All(options.Skip(1), o => Assert.Equal(o.Value, ModelChoices.Normalize(o.Value)));
    }
}
