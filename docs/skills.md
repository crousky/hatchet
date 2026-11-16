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

`roles` is Hatchet's own, and optional. It names the agents a skill is for: `ContextSpecifier`, `CodeGenerator`, `BugFixer` or `StaticReviewer`. Without it, a skill goes to the first three, the agents that write the code. Claude Code's other keys, such as `allowed-tools`, are ignored.

`always` is Hatchet's own too. A skill with `always: true` goes with every subtask, whether or not the plan lists it. See [Always-on skills](#always-on-skills).

Hatchet looks for skills in three places, and when two share a name the first one wins:

1. Your own `%LOCALAPPDATA%\Hatchet\skills\<name>\SKILL.md`, for every repository.
2. The repository's `.hatchet/skills/<name>/SKILL.md`.
3. The repository's `.claude/skills/<name>/SKILL.md`, shared with Claude Code.

Yours come first because a repository may not be yours: it can't replace a skill of yours by using the same name. A skill from a repository is still instructions the agents follow, so read the skills in a repository you didn't write before you run Hatchet on it.

In a repository, a skill folder or `SKILL.md` that is a symbolic link is skipped, as is a `SKILL.md` larger than 64 KB. Settings doesn't list them. Your own skills folder has neither limit, so skills linked in from a dotfiles folder work.

Local models can't call tools, so Hatchet chooses the skills rather than the model. The Task Planner sees each skill's name and description and lists the ones each subtask needs, beside its files. You can change them when you review the plan, and a plan handed over through MCP can name them too. Each agent then gets the bodies of that subtask's skills meant for it, in a Skills section of the prompt, and the run log says which, such as "Gave the Code Generator the skills: fluent-ui-blazor." Skills are read again for every call, so an edit reaches the next one. A prompt that wouldn't leave the local model room to answer drops the type definitions first, then skills from the end of the subtask's list, and the log says so. Claude agents get skills the same way, in the prompt; Claude Code's own skills stay off.

## Skills for the reviewer

The Static Reviewer takes skills too, but only the ones whose `roles` name `StaticReviewer`. It reads them differently from the other agents: as rules the repository keeps to, each of which it checks the diff against. A change that breaks one is rejected with a blocking issue that names the rule, and goes back to the Bug Fixer like any other rejection.

That is why a skill with no `roles` doesn't reach the reviewer. Most skills are advice on how to build something, and advice read as rules would turn into grounds for rejection. Put `StaticReviewer` in `roles` for the skills that state what must hold: an invariant, a security rule, a boundary between layers. Write each rule so that a diff can be seen to break it.

A reviewer's skills can only add reasons to reject a change. The prompt says so, so a skill in a repository can't be written to talk a change through review. Guidance an MCP client sends still never reaches the reviewer.

## Always-on skills

```markdown
---
name: house-invariants
description: Rules every change in this repository keeps to.
roles: [ContextSpecifier, CodeGenerator, BugFixer, StaticReviewer]
always: true
---
- A container's partition key never changes once it has shipped.
- The tenant comes from the signed-in user's claims, never from the request.
```

A skill with `always: true` is given to every subtask, ahead of the skills the plan lists, to each agent its `roles` name. Plans don't have to list it, and the Task Planner is told not to. It is also the last thing dropped when a prompt won't fit the local model's window: the type definitions go first, then the listed skills from the end, and only then the always-on ones. The run log names it with the rest.

Use it for the few rules that apply to any change. Every always-on skill is in every prompt, so a long one costs the local model room it needs for the code.

An always-on skill in a repository reaches the agents without a plan naming it, so nobody approves it step by step. That is one more reason to read a repository's skills before you run Hatchet on a repository you didn't write. Settings marks them **Always on**.

**Settings → Skills** lists the skills Hatchet finds, yours and each project's, with where each came from and anything wrong with its file. When a run pauses on a subtask, **Save this guidance as a project skill** writes the guidance you typed to `.hatchet/skills/<name>/SKILL.md` and commits it on its own on the run's branch. Rolling back the subtask keeps it, and it reaches the base branch with the rest of the run, so the Task Planner can offer it from then on.
