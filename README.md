# Hatchet

A local software factory. Give it an architecture spec and a Git repository. One locally hosted model then plans the work, writes it, tests it, reviews it and commits it, one subtask at a time. Anything it can't finish comes back to you.

## Quick start

You need the [.NET SDK](https://dotnet.microsoft.com/download) (9 or 10) and Git. The web app carries its own ASP.NET Core 9 runtime, so nothing else needs installing.

1. Serve the model with an OpenAI-compatible endpoint:
   - **LM Studio:** load Qwen 3.8 and start the local server (`http://localhost:1234/v1`).
   - **Ollama:** run the model (`http://localhost:11434/v1`). Set `OLLAMA_CONTEXT_LENGTH=32768` or higher, because a small default silently truncates long prompts.
2. Start Hatchet:

   ```powershell
   ./run.ps1
   ```

   Or run it directly, then open http://localhost:5199:

   ```bash
   dotnet run --project src/Factory.Web
   ```

3. In **Settings**, pick your server and press **Test**. Choose the model ID your server actually reports. The default is `qwen3.8`, and servers often name it differently.
4. In **New run**, paste or upload your spec, point at a clean Git repository, and set the verification command.

## How a run works

| Stage | Agent | Effort | Temp | What happens |
| :--- | :--- | :--- | :--- | :--- |
| Ingest | | | | Scans the repo into a lightweight map. |
| Decompose | Task Planner | `xhigh` | 0.6 | Splits the spec into ordered subtasks. You can approve, edit or reorder them. |
| Refine | Context Specifier | `medium` | 0.4 | Reads the relevant files and writes exact signatures, test cases and edge cases. |
| Build | Code Generator | `medium` | 0.2 | Writes the code and tests on a `factory/run-N` branch. |
| Verify | | | | Runs your command. A failure goes to the Fixer, and the Reviewer doesn't run until it passes. |
| Review | Static Reviewer | `xhigh` | 0.3 | Checks the diff against the refined spec. Approval commits as `feat(subtask): ...`. |
| Fix | Bug Fixer | `xhigh` | 0.2 | Runs only after a failed Verify or a rejected Review. Gets 3 attempts before the first review, and 3 more after it. |
| Integrate | | | | Runs the full check once more, then offers one-click merge or a PR draft. |

Roles differ by system prompt, JSON schema, temperature and `reasoning_effort`, all of which can be changed in Settings. Each role runs on the local model by default, or on Claude through the Claude Code CLI, which uses your Claude login rather than an API key. When a fix fails on the local model, the next attempt goes to Claude, and so does a local fix still running after 90 minutes. While any agent is working, you can stop its call and hand it to another model from its card in the agent pane.

Once three subtasks are done, the run header shows a likely range for the working time left. It's based on how long the finished subtasks took, and time paused doesn't count. It narrows as more subtasks finish.

If a subtask runs out of attempts, or the model server goes away, the run pauses and asks you what to do. You can retry with your own guidance, re-verify after fixing it by hand, restart the subtask, skip it, or cancel the run. Your guidance goes to every agent that works on the subtask from then on. Runs interrupted by a restart pause the same way and can be resumed.

### Runaway answers

A local model at low temperature sometimes gets stuck repeating itself. It might count up through made-up table rows, or restate one thought over and over until its context window is full. Hatchet watches each answer and its thinking as they stream in. When many lines in a row differ only in their numbers, or the latest text compresses far better than any real code, it stops the call, usually within a few thousand tokens. It handles an answer that fills a context window that's already 64k or bigger the same way, since a bigger window wouldn't help. It then works through these steps:

1. **Keep the whole files.** A cut-off build keeps every file it finished. The Code Generator is then asked for only the missing files, so one oversized file doesn't cost the whole subtask.
2. **Try again, differently.** The call runs once more at a higher temperature, with a note on what went wrong and a reminder to keep data files short.
3. **Hand it to Claude.** If that goes the same way, Claude takes the call. You can turn this off in Settings.
4. **Pause.** If nothing works, the run pauses. The message says which steps were tried and what to do next.

When the window really is too small, the pause says so and tells you how to load the model with a bigger one. Before a call, Hatchet leaves the type definitions out of a prompt that wouldn't leave the model room to answer. The planner and agents are also told never to type out long data from memory, such as registries or lookup tables. Instead they plan a script that fetches the data, plus a small sample.

### What agents are told about types

Before each call, Hatchet finds the types the subtask, its files and its errors name, and adds their definitions to the prompt, so agents use real members instead of guessing:

- **The repository's own types.** It adds the source files that declare them.
- **Types from packages.** For .NET projects that have been restored, it adds the public signatures of the package types they name, such as a UI library's components with their namespace and parameters. They're read from the compile assemblies listed in each project's `obj/project.assets.json`. Only names that read as code are looked up, such as `FluentSelect` or `IToastService`, so capitalised words in prose don't pull in types.

### Skills

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

### Keeping fixes to the spec

A fix has to fix what the spec asks for, not route around it. If a fix takes a type the spec names out of the subtask's files altogether, such as swapping a named component for plain markup, Hatchet doesn't apply it. The next attempt is told what to keep, along with the failure it still has to fix. Only real types count, from the repository or its packages, so a fix can still replace a member the spec got wrong.

Fixes before the first review and fixes after it have a budget each. A change that only builds on its last allowed fix still gets fixed for what the reviewer finds, instead of pausing as soon as the reviewer sees it.

### Projects and the queue

The runs list groups runs by project, which is the repository they build in. You can start as many runs as you like, and the ones past the limit wait in line:

- **Runs at once.** A run counts as a local run if any of its agents uses the local model. Local runs go one at a time by default, because the model answers one request at a time. Runs whose agents are all set to Claude go up to 4 at a time. Both limits are in Settings.
- **One run per project.** Runs in the same repository go one at a time, because they share its working tree. A run waits until the one before it in that project is merged, closed or cancelled. A run waiting for its project doesn't hold up runs in other projects.

Resuming a paused run, approving its plan or asking for a new plan also waits for a worker, and what you asked for happens when the run's turn comes. A queued run starts by itself then. Stop waiting takes it out of the line without doing what it was waiting to do, and Resume puts it back at the end. Runs still in line when Hatchet stops join it again when Hatchet starts.

When a run's turn comes, Hatchet checks the working tree. If it has uncommitted changes, or no branch is checked out, the run pauses so you can tidy the tree and resume it.

## Driving Hatchet from Claude Code

Hatchet is also an MCP server at `http://localhost:5199/mcp`. Add it to Claude Code once, for every project:

```bash
claude mcp add --transport http --scope user hatchet http://localhost:5199/mcp
```

Then plan a change with Claude in conversation and ask it to hand the plan to Hatchet. Hatchet skips its own planner and builds, verifies, fixes and reviews each step on your local model, while Claude follows along and helps when a run pauses.

| Tool | Does |
|---|---|
| `probe_repository` | Checks a folder is a clean Git repository and how it would be verified |
| `start_run` | Starts a run from a spec, with a ready-made plan or with Hatchet's planner |
| `list_runs`, `get_run` | Status, project, place in line, progress, subtasks, recent events and working time left |
| `get_subtask` | One subtask in full: last failure, fix history, review verdict, diff |
| `approve_plan` | Approves a plan waiting for review in Hatchet, without the commands Hatchet's planner proposed |
| `pause_run`, `resume_run` | Pause now or after the current step; resume, retry the fixer with guidance, re-verify, restart or skip |
| `hand_over_call` | Stops the call an agent is making and makes it again on the local model or a Claude model |
| `cancel_run` | Abandons a run |

Hatchet has to be running for Claude to reach it.

## Safety

**Hatchet runs model-written code on your machine, with no sandbox.** Verification executes whatever tests the model wrote, with your user's permissions. Use it on repositories you can afford to have changed, and in a VM or container if you want isolation.

What it does to limit the damage:

- **Clean tree required.** A run won't start with uncommitted changes, so rollbacks can't touch your work.
- **Separate branch.** Work stays on `factory/run-N`. Rollback refuses to run on any other branch, and nothing merges until you click Merge.
- **No unreviewed commands.** Verification commands proposed by Hatchet's planner only run if you approved them at plan review. Otherwise Hatchet runs your own command, or one it detects from the repository. A plan handed over through MCP counts as approved by whoever handed it over, but an MCP client can't approve commands proposed by Hatchet's planner.
- **Writes stay inside the repo.** A patch with an absolute path, a `..`, a symlink on the way, or a target in `.git` is rejected whole, before anything is written.
- **Local only.** The server binds to `localhost` and rejects other host names. The MCP endpoint also refuses requests from web pages other than Hatchet's own. It has no login, so don't expose it.
- **Nothing is pushed.** Prepare PR gives you the commands and the description, and you run them yourself.

## Project layout

```
LocalSoftwareFactory.sln
src/Factory.Contracts   Domain models, DTOs, state enums, strict JSON schemas
src/Factory.Engine      LLM client, Git workspace, command runner, skills, orchestrator
src/Factory.Web         Blazor Server UI, SQLite persistence (EF Core)
tests/Factory.Tests     xUnit tests, including full pipeline runs against real Git repos
```

The database lives at `%LOCALAPPDATA%\Hatchet\hatchet.db`. Set `ConnectionStrings__Factory` to move it.

```bash
dotnet test
```

After changing an entity, add a migration. Migrations are applied at startup.

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project src/Factory.Web --output-dir Data/Migrations
```

Keyboard: `Ctrl+B` toggles the subtask pane, `Ctrl+Alt+B` the agent pane, `Ctrl+J` the terminal panel. Pane edges can be dragged to resize.
