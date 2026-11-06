# Skills

A skill is instructions you write once and Hatchet hands to agents whose subtask needs them, such as how a project uses its UI library. Skills use the Claude Code layout, so ones you already have work as they are: a folder holding a `SKILL.md`, with YAML frontmatter and a markdown body.

```markdown
---
name: fluent-ui-blazor
description: Building Blazor pages and components with Microsoft Fluent UI.
roles: [CodeGenerator, BugFixer]
---
Use FluentButton and FluentTextField, never plain HTML controls. ...
```

`roles` is Hatchet's own, and optional. It names the agents a skill is for: `ContextSpecifier`, `CodeGenerator` or `BugFixer`. Without it, a skill goes to all three. Claude Code's other keys, such as `allowed-tools`, are ignored.

Hatchet looks for skills in three places, and when two share a name the first one wins:

1. The repository's `.hatchet/skills/<name>/SKILL.md`.
2. The repository's `.claude/skills/<name>/SKILL.md`, shared with Claude Code.
3. Your own `%LOCALAPPDATA%\Hatchet\skills\<name>\SKILL.md`, for every repository.

Local models can't call tools, so Hatchet chooses the skills rather than the model. The Task Planner sees each skill's name and description and lists the ones each subtask needs, beside its files. You can change them when you review the plan, and a plan handed over through MCP can name them too. The Context Specifier, Code Generator and Bug Fixer then get the bodies of that subtask's skills meant for them, in a Skills section of the prompt, and the run log says which, such as "Gave the Code Generator the skills: fluent-ui-blazor." Skills are read again for every call, so an edit reaches the next one. A prompt that wouldn't leave the local model room to answer drops the type definitions first, then skills from the end of the subtask's list, and the log says so. Claude agents get skills the same way, in the prompt; Claude Code's own skills stay off.

**Settings → Skills** lists the skills Hatchet finds, yours and each project's, with where each came from and anything wrong with its file. When a run pauses on a subtask, **Save this guidance as a project skill** writes the guidance you typed to `.hatchet/skills/<name>/SKILL.md` and commits it on its own on the run's branch. Rolling back the subtask keeps it, and it reaches the base branch with the rest of the run, so the Task Planner can offer it from then on.
