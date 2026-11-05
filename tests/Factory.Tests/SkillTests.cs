using Factory.Contracts;
using Factory.Engine.Orchestration;
using Factory.Engine.Skills;

namespace Factory.Tests;

public class SkillTests : IDisposable
{
    private readonly TempDirectory _repo = new();
    private readonly TempDirectory _user = new();

    public void Dispose()
    {
        _repo.Dispose();
        _user.Dispose();
    }

    private static Skill Parse(string text, string folder = "folder") => SkillLibrary.Parse(text, $"{folder}/SKILL.md", SkillSource.Project, folder);

    [Fact]
    public void Reads_a_claude_code_skill_with_roles_and_skips_keys_hatchet_does_not_use()
    {
        var skill = Parse("""
            ---
            name: fluent-ui-blazor
            description: >
              Building Blazor UI with
              Microsoft Fluent UI.
            allowed-tools: Read, Grep
            metadata:
              version: 2
              roles: nope
            roles:
              - Code Generator
              - bug-fixer
            ---
            # Fluent UI

            Use FluentButton, never a plain button.
            """);

        Assert.Equal("fluent-ui-blazor", skill.Name);
        Assert.Equal("Building Blazor UI with Microsoft Fluent UI.", skill.Description);
        Assert.Equal([AgentRole.CodeGenerator, AgentRole.BugFixer], skill.Roles);
        Assert.StartsWith("# Fluent UI", skill.Body);
        Assert.EndsWith("never a plain button.", skill.Body);
        Assert.Empty(skill.Problems);
    }

    [Theory]
    [InlineData("roles: [CodeGenerator, ContextSpecifier]")]
    [InlineData("roles: CodeGenerator, context-specifier")]
    [InlineData("roles:\n- \"CodeGenerator\"\n- 'Context Specifier'")]
    public void Roles_can_be_written_as_a_flow_list_a_comma_list_or_a_block_list(string roles)
    {
        var skill = Parse($"---\nname: s\ndescription: d\n{roles}\n---\nBody");

        Assert.Equal([AgentRole.CodeGenerator, AgentRole.ContextSpecifier], skill.Roles);
        Assert.Empty(skill.Problems);
    }

    [Fact]
    public void Unknown_roles_and_roles_that_take_no_skills_are_reported()
    {
        var skill = Parse("---\nname: s\ndescription: d\nroles: [Designer, StaticReviewer, 3]\n---\nBody");

        Assert.Equal([AgentRole.StaticReviewer], skill.Roles);
        Assert.Contains(skill.Problems, p => p.Contains("\"Designer\" isn't a Hatchet agent role"));
        Assert.Contains(skill.Problems, p => p.Contains("\"3\" isn't"));
        Assert.Contains(skill.Problems, p => p.Contains("StaticReviewer doesn't take skills"));
        Assert.False(skill.IsFor(AgentRole.CodeGenerator));
    }

    [Fact]
    public void A_skill_without_roles_is_for_every_agent_that_takes_skills()
    {
        var skill = Parse("---\nname: s\ndescription: \"Use when: always\"\n---\nBody");

        Assert.Equal(SkillLibrary.SkilledRoles, skill.Roles);
        Assert.Equal("Use when: always", skill.Description);
    }

    [Fact]
    public void A_file_without_frontmatter_is_named_after_its_folder()
    {
        var skill = Parse("﻿Just instructions.", "my-skill");

        Assert.Equal("my-skill", skill.Name);
        Assert.Equal("", skill.Description);
        Assert.Equal("Just instructions.", skill.Body);
        Assert.Contains(skill.Problems, p => p.Contains("no YAML frontmatter"));
    }

    [Fact]
    public void Project_skills_hide_claude_project_skills_which_hide_user_skills()
    {
        _repo.Write(".hatchet/skills/shared/SKILL.md", "---\nname: shared\ndescription: from hatchet\n---\nA");
        _repo.Write(".claude/skills/shared-copy/SKILL.md", "---\nname: Shared\ndescription: from claude\n---\nB");
        _repo.Write(".claude/skills/beta/SKILL.md", "---\nname: beta\ndescription: claude only\n---\nC");
        _repo.Write(".claude/skills/not-a-skill/README.md", "ignored");
        _user.Write("beta/SKILL.md", "---\nname: beta\ndescription: user\n---\nD");
        _user.Write("alpha/SKILL.md", "---\nname: alpha\ndescription: user only\n---\nE");

        var skills = new SkillLibrary(_user.Path).Load(_repo.Path);

        Assert.Equal(["alpha", "beta", "shared"], skills.Select(s => s.Name));
        Assert.Equal(SkillSource.User, skills[0].Source);
        Assert.Equal(("claude only", SkillSource.ClaudeProject), (skills[1].Description, skills[1].Source));
        Assert.Equal(("from hatchet", SkillSource.Project), (skills[2].Description, skills[2].Source));
        Assert.Equal(["alpha", "beta"], new SkillLibrary(_user.Path).LoadUser().Select(s => s.Name));
        Assert.Empty(new SkillLibrary(null).Load(null));
    }

    [Theory]
    [InlineData("Use Alpha Files", "use-alpha-files")]
    [InlineData("  --Fluent UI / Blazor!  ", "fluent-ui-blazor")]
    [InlineData("!!!", "")]
    public void Slug_keeps_lowercase_letters_digits_and_single_hyphens(string name, string slug) =>
        Assert.Equal(slug, SkillLibrary.Slug(name));

    [Fact]
    public void A_composed_skill_reads_back_as_written()
    {
        var text = SkillLibrary.Compose("tabs", "Use when \"tabs\" are\nneeded: always", "Indent with tabs.\r\nNever spaces.", [AgentRole.BugFixer]);

        var skill = Parse(text);

        Assert.Equal("tabs", skill.Name);
        Assert.Equal("Use when \"tabs\" are needed: always", skill.Description);
        Assert.Equal([AgentRole.BugFixer], skill.Roles);
        Assert.Equal("Indent with tabs.\nNever spaces.", skill.Body);
        Assert.Empty(skill.Problems);
    }

    [Fact]
    public void Render_puts_each_body_under_its_name_and_cuts_long_ones()
    {
        var skills = new[] { Parse("---\nname: a\ndescription: d\n---\nshort"), Parse("---\nname: b\ndescription: d\n---\n" + new string('x', 50)) };

        var rendered = SkillLibrary.Render(skills, 20);

        Assert.StartsWith("### a\n\nshort\n\n### b\n\n" + new string('x', 20), rendered);
        Assert.EndsWith("[skill truncated at 20 characters]", rendered);
    }

    [Fact]
    public void The_planner_prompt_lists_each_skill_with_its_description_and_agents()
    {
        var skills = new[] { Parse("---\nname: fluent-ui-blazor\ndescription: Blazor UI with Fluent UI.\nroles: [CodeGenerator]\n---\nBody") };

        var prompt = PromptBuilder.Decompose("spec", "map", "check", null, skills);

        Assert.Contains("## Skills", prompt);
        Assert.Contains("- fluent-ui-blazor: Blazor UI with Fluent UI. (for the Code Generator)", prompt);
        Assert.DoesNotContain("## Skills", PromptBuilder.Decompose("spec", "map", "check", null, []));
    }

    [Fact]
    public void Refine_build_and_fix_prompts_carry_the_skills_section_only_when_there_are_skills()
    {
        var subtask = new FactorySubtask { Title = "t", Description = "d" };
        const string skills = "### s\n\nDo it this way.";

        foreach (var prompt in new[]
                 {
                     PromptBuilder.Refine(subtask, "spec", "map", "files", skills: skills),
                     PromptBuilder.Build(subtask, "files", skills: skills),
                     PromptBuilder.BuildRest(subtask, "files", ["a"], ["b"], "cut", skills: skills),
                     PromptBuilder.Fix(subtask, "failed", "files", null, skills: skills)
                 })
        {
            Assert.Contains("## Skills. Follow these instructions for this subtask\n\n### s\n\nDo it this way.", prompt);
        }
        Assert.DoesNotContain("## Skills", PromptBuilder.Build(subtask, "files"));
    }
}
