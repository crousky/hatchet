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
| Fix | Bug Fixer | `xhigh` | 0.2 | Runs only after a failed Verify or a rejected Review. Shares a budget of 3 attempts per subtask. |
| Integrate | | | | Runs the full check once more, then offers one-click merge or a PR draft. |

The model is never swapped. Roles differ only by system prompt, JSON schema, temperature and `reasoning_effort`. Effort and temperature can be changed in Settings.

If a subtask runs out of attempts, or the model server goes away, the run pauses and asks you what to do. You can retry the Fixer with your own guidance, re-verify after fixing it by hand, restart the subtask, skip it, or cancel the run. Runs interrupted by a restart pause the same way and can be resumed.

## Safety

**Hatchet runs model-written code on your machine, with no sandbox.** Verification executes whatever tests the model wrote, with your user's permissions. Use it on repositories you can afford to have changed, and in a VM or container if you want isolation.

What it does to limit the damage:

- **Clean tree required.** A run won't start with uncommitted changes, so rollbacks can't touch your work.
- **Separate branch.** Work stays on `factory/run-N`. Rollback refuses to run on any other branch, and nothing merges until you click Merge.
- **No unreviewed commands.** Verification commands proposed by the model only run if you approved them at plan review. With plan review off, only your own command runs.
- **Writes stay inside the repo.** A patch with an absolute path, a `..`, a symlink on the way, or a target in `.git` is rejected whole, before anything is written.
- **Local only.** The server binds to `localhost` and rejects other host names. It has no login, so don't expose it.
- **Nothing is pushed.** Prepare PR gives you the commands and the description, and you run them yourself.

## Project layout

```
LocalSoftwareFactory.sln
src/Factory.Contracts   Domain models, DTOs, state enums, strict JSON schemas
src/Factory.Engine      LLM client, Git workspace, command runner, orchestrator
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
