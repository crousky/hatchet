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
    public void User_skills_hide_project_skills_and_hatchet_project_skills_hide_claude_project_skills()
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
        Assert.Equal(("user", SkillSource.User), (skills[1].Description, skills[1].Source));
        Assert.Equal(("from hatchet", SkillSource.Project), (skills[2].Description, skills[2].Source));
        Assert.Equal(["alpha", "beta"], new SkillLibrary(_user.Path).LoadUser().Select(s => s.Name));
        Assert.Empty(new SkillLibrary(null).Load(null));
    }

    [Fact]
    public void A_skill_file_over_64_kb_is_skipped()
    {
        _repo.Write(".hatchet/skills/big/SKILL.md", "---\nname: big\ndescription: d\n---\n" + new string('x', 70 * 1024));
        _repo.Write(".hatchet/skills/small/SKILL.md", "---\nname: small\ndescription: d\n---\nok");

        var skills = new SkillLibrary(null).Load(_repo.Path);

        Assert.Equal(["small"], skills.Select(s => s.Name));
    }

    [Fact]
    public void A_symlinked_skill_file_or_folder_is_skipped()
    {
        var secret = System.IO.Path.Combine(_user.Path, "secret.md");
        File.WriteAllText(secret, "---\nname: linked\ndescription: d\n---\nsecret");
        var skillsRoot = System.IO.Path.Combine(_repo.Path, ".hatchet", "skills");
        Directory.CreateDirectory(System.IO.Path.Combine(skillsRoot, "linked"));
        _repo.Write(".hatchet/skills/real/SKILL.md", "---\nname: real\ndescription: d\n---\nok");
        try
        {
            File.CreateSymbolicLink(System.IO.Path.Combine(skillsRoot, "linked", "SKILL.md"), secret);
            Directory.CreateSymbolicLink(System.IO.Path.Combine(skillsRoot, "dirlink"), _user.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // Creating links needs a privilege this machine hasn't granted.
        }

        var skills = new SkillLibrary(null).Load(_repo.Path);

        Assert.Equal(["real"], skills.Select(s => s.Name));
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
            Assert.Contains("## Skills. Follow these instructions for this subtask\n\n<skill-", prompt);
            Assert.Contains("\n### s\n\nDo it this way.\n</skill-", prompt);
        }
        Assert.DoesNotContain("## Skills", PromptBuilder.Build(subtask, "files"));
    }
}
