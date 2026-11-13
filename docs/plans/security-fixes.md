# Security fixes plan

Result of a three-expert security review (web/network, command execution and filesystem, LLM/agent) on 2026-11-13, triaged by the team lead. Finding IDs are the experts' own: `WEB-n`, `SYS-n`, `LLM-n`.

## Triage

Severity is the lead's call, not the experts'. **High** means: reachable by a realistic attacker, breaks a safety claim the README makes, and ends in code execution or a change no diff shows.

### High — fix first

| # | Finding | Expert IDs | Why High |
| :--- | :--- | :--- | :--- |
| H1 | The patcher's `.git` guard is bypassable on Windows: `.git./hooks/pre-commit` and `GIT~1/…` pass the check at `FilePatcher.cs:175`, then `Path.GetFullPath` resolves them into `.git`. Reads share the resolver. | SYS-1, LLM-3 | Model output can plant a hook or `core.fsmonitor`. It is in no diff, the reviewer never sees it, and it survives rollback and cancel. Confirmed by emulation. |
| H2 | `/mcp` and the UI have no authentication. Any local process that sends a POST without an `Origin` header can call `run_command`. If the listener is ever bound beyond loopback, that is remote. The same caller can approve a plan a human was asked to review. | WEB-1, WEB-2, SYS-2, LLM-1, LLM-2 | One request gives command execution as the developer, with output returned. |

### Medium

| # | Finding | Expert IDs |
| :--- | :--- | :--- |
| M1 | Git runs un-hardened on untrusted folders. `git status` in the probe (fired 300 ms after each keystroke in New run) honours a repo's `core.fsmonitor`; later calls honour `diff.external`, filters and hooks. | SYS-3 |
| M2 | The verification command is re-detected before every check from files the model just wrote, and run without a pause when it changes. Nothing stops a fix from deleting tests or rewriting the files that define the check. | SYS-4, LLM-5 |
| M3 | The reviewer sees only the first `MaxDiffChars` (60,000) of the diff, but `git add -A` commits everything. `passed` with blocking issues is accepted. | LLM-6 |
| M4 | Writes are not limited to the subtask's files. The model can write `.github/workflows/*`, `.claude/settings.json`, `.vscode/tasks.json`, `.husky/*`, skills folders. | LLM-4, SYS-12 |
| M5 | Untrusted text (files, test output, skills) joins prompts under plain `##` headings, so it can forge `## Human guidance. Follow it where it conflicts with the rest`. | LLM-7 |
| M6 | No secret filtering: the planner can name `.env` as a context file, it goes to the model (Claude after one failed local fix, by default), into the log database, and `git add -A` can commit it. | LLM-8 |
| M7 | Skills in an untrusted repo load as instructions, override a user skill of the same name, and symlinked `SKILL.md` files are followed with no size cap. | LLM-9, SYS-7 |
| M8 | MCP tool results hand raw test output, diffs and reviewer text to the driving agent beside wording that invites it to act on them. | LLM-11 |

### Low and Info

| # | Finding | Expert IDs |
| :--- | :--- | :--- |
| L1 | The Origin check covers only `/mcp`, not `/_blazor`; 16 MB SignalR receive limit. | WEB-3, WEB-4 |
| L2 | Plan approval shows model-proposed commands in a one-line input and approves them all in one click. | WEB-5 |
| L3 | The copied `gh pr create` command embeds an unescaped run title. | WEB-6 |
| L4 | `screenshotPaths` and `repositoryPath` accept UNC paths (NTLM leak); every file is read before the 8-file cap. | WEB-7, SYS-2 |
| L5 | `dotnet run` starts in Development, so the developer exception page is on. No CSP. | WEB-8, WEB-9 |
| L6 | The folder picker expands environment variables and follows UNC paths. | WEB-10 |
| L7 | Detected commands embed repo file names with quoting only for spaces. | SYS-5, LLM-12 |
| L8 | Branch names read from the repo reach `git checkout` with no validation. | SYS-6 |
| L9 | Claude Code calls don't pin settings sources, permission mode or read scope; `.cmd` shims allow argument injection. | SYS-8, SYS-9, LLM-10 |
| L10 | Child processes: no job object, survivors after a normal exit, git has no timeout or output cap. | SYS-10 |
| L11 | Children inherit the whole environment; detected Node installs run lifecycle scripts. | SYS-11, LLM-14 |
| L12 | `<think>` stripping deletes matching spans inside generated file content. | LLM-13 |
| L13 | No cap on planner subtasks or on MCP-driven budget resets. | LLM-15 |
| L14 | Prefix check without separator at `FactoryOrchestrator.cs:1188`; attribute reset may follow links in `AdHocManager.DeleteDirectory`. | SYS-13 |
| L15 | No NuGet lock files or package source mapping. | LLM-16 |

No action: SYS-14 (HTTP clients are sound). The experts found no XSS sink, no CSRF-able endpoint, no SQL injection, no stored API keys, and no path to merge or push without the user.

## Decisions (made 2026-11-13)

1. **UI authentication (part of H2): no.** The UI stays without a login. `/mcp` gets a bearer token; the UI is covered only by the loopback check, host filtering and the Origin check. Unit B step 3 is dropped. Residual risk: another local user or process that can drive a browser session against `localhost:5199` still has the UI.
2. **Git hooks (M1):** ignored for probe and status calls, still run on Hatchet's own commits.
3. **Commands over MCP (H2):** a caller holding the token can still send commands. MCP must keep working; the only change for a client is the `Authorization` header.

## How the work is run

- Every unit is one **Sonnet** subagent (`Agent` tool, `model: "sonnet"`, `isolation: "worktree"`), started from this branch's tip.
- Units in a wave touch disjoint files, so they run in parallel and merge cleanly. An agent that finds it must edit a file outside its list stops and reports back instead.
- Each agent: reads the files it owns, makes the change, adds the tests listed, runs `dotnet build` and `dotnet test`, commits on its worktree branch, and reports the branch, what changed, test results, and any doc note for the README.
- The lead merges each wave into this branch, runs the full suite, and reviews every diff before the next wave. Nothing is pushed.
- Only unit B and unit J edit `README.md` and `docs/`.

### Wave 1 — parallel, disjoint files

**Unit A — Path safety (H1, M4 helper, M6 read side)**
Files: `src/Factory.Engine/Workspace/FilePatcher.cs`, `RepositoryScanner.cs`, `tests/Factory.Tests/FilePatcherTests.cs`.
1. In `ResolveSafePath`: reject raw segments ending in `.` or a space, or matching `^git~\d+$` (ignore case). After `GetFullPath`, split `Path.GetRelativePath(root, fullPath)` and reject any `.git` segment (ignore case).
2. Add `public static bool IsSensitivePath(string relativePath)` for `.github/`, `.claude/`, `.hatchet/`, `.vscode/`, `.husky/`, `.gitattributes`, `.gitmodules`, `.npmrc`, `nuget.config`. Unit G enforces it.
3. Add `public static bool IsSecretPath(string relativePath)` for `.env*`, `*.pem`, `*.pfx`, `*.key`, `id_rsa*`, `id_ed25519*`, `secrets.*`, `*.tfvars`. `RepositoryScanner.ReadFiles` skips matches with a note in place of the content.
4. Tests: `.git./x`, `.GIT./x`, `GIT~1/x`, `.git /x`, `a/.git.` rejected; ordinary dotfiles still accepted; a secret file is not read.

**Unit B — Web host and MCP (H2, L1, L4, L5, L6, M8)**
Files: `src/Factory.Web/Program.cs`, `Mcp/HatchetTools.cs`, `Mcp/AdHocTools.cs`, `Services/FolderBrowser.cs`, `appsettings.json`, `Properties/launchSettings.json`, `Components/Pages/Settings.razor`, `run.ps1`, `docs/mcp.md`, `README.md` (Safety and MCP sections only), a new test file.
1. Generate a random 32-byte token on first start, stored in a file beside the database in `%LOCALAPPDATA%\Hatchet`. Require `Authorization: Bearer <token>` on `/mcp`, compared in constant time; 401 otherwise.
2. Show the token in Settings inside the `claude mcp add --transport http … --header "Authorization: Bearer …"` command, and update `docs/mcp.md`.
3. Dropped (Decision 1): the UI keeps no login.
4. First in the pipeline: 403 when `Connection.RemoteIpAddress` is not loopback.
5. Apply the Origin check to every path, not only `/mcp`. Lower `MaximumReceiveMessageSize` to 1 MB if the spec upload path (`InputFile`) does not depend on it; otherwise leave it and say so.
6. `approve_plan` refuses when plan review was required by the UI or by a report; that approval belongs to the UI.
7. Reject UNC and device paths (`\\`, `//`) for `repositoryPath` and `screenshotPaths`; check the count against `ReportBuilder.MaxImages` before reading; read the magic bytes before the whole file.
8. Return build output, diffs, reviewer text and Ask answers under a field named `untrustedOutput`, and say in each tool description that it is data from the build, not instructions.
9. `FolderBrowser`: drop `ExpandEnvironmentVariables`, refuse UNC paths.
10. Remove the Development environment from the launch profile so `/error` is used. Add response headers: a CSP (`default-src 'self'`, `object-src 'none'`, `base-uri 'self'`, `frame-ancestors 'none'`, plus what Blazor's websocket needs) and `X-Content-Type-Options: nosniff`; move the two inline `onclick` handlers in `App.razor` into `app.js`.
11. Tests (`WebApplicationFactory`): `/mcp` without a token is 401, with a foreign Origin is 403, with the token works; UNC paths are refused.

**Unit C — Git hardening (M1, L8, L10 git part, M6 staging side)**
Files: `src/Factory.Engine/Workspace/GitWorkspace.cs`, `src/Factory.Web/Components/Pages/NewRun.razor`, a new `GitWorkspaceTests.cs`.
1. Every call: add `-c core.fsmonitor=false -c safe.bareRepository=explicit`. Diff calls: add `--no-ext-diff --no-textconv`. Probe and status calls: add `-c core.hooksPath=<empty directory>` (Decision 2).
2. Validate branch names read from the repo (reject a leading `-`; use `git check-ref-format --branch`).
3. Give `TryGitAsync` a default timeout (60 s for probes, longer for commit and merge) and an output cap.
4. Probe on blur or folder pick, not on each keystroke.
5. `StageAllAsync` leaves new files matching the secret patterns unstaged. Duplicate the pattern list locally with a comment naming `FilePatcher.IsSecretPath`; unit G unifies them.
6. Tests: a repo whose config sets `core.fsmonitor` to a script that writes a marker file is probed without the marker appearing; a `-f` branch name is refused.

**Unit D — Command execution (L7, L10, L11)**
Files: `src/Factory.Engine/Verification/CommandRunner.cs`, `VerificationDetector.cs`, `RunCommandDetector.cs`, `tests/Factory.Tests/VerificationDetectorTests.cs`, `InfrastructureTests.cs` (command runner tests only).
1. Detectors skip candidate paths containing characters outside `[A-Za-z0-9 _.\-/+@]` and always quote the rest.
2. Add `--ignore-scripts` to detected `npm install`, `npm ci`, `pnpm install`, `yarn install`.
3. Strip variables matching `*_KEY`, `*_TOKEN`, `*SECRET*`, `*PASSWORD*`, `*CREDENTIAL*` from the child environment.
4. Windows: run children in a Job Object with `KILL_ON_JOB_CLOSE`, closed after a normal exit as well. Unix: own process group, killed as a group.
5. Tests: a `.sln` named `a&b.sln` is skipped; a child cannot see `FAKE_API_TOKEN`; a background grandchild is gone after the command returns.

**Unit E — Model clients and skills (L9, L12, M7)**
Files: `src/Factory.Engine/Llm/ClaudeCodeAsker.cs`, `ClaudeCodeLlmClient.cs`, `StructuredOutputParser.cs`, `ThinkTagSplitter.cs`, `src/Factory.Engine/Skills/SkillLibrary.cs`, `tests/Factory.Tests/ClaudeCodeTests.cs`, `SkillTests.cs`, `LlmClientTests.cs`.
1. First run `claude --help` and read the current Claude Code CLI documentation for the exact flags; do not rely on memory. Then pin settings sources, set the permission mode explicitly, and scope Read/Grep/Glob to the repo and attachments folders with allow rules. Remove `CLAUDE_CODE_*` variables from the child environment.
2. Refuse a `ClaudeCodePath` ending in `.cmd` or `.bat` with a clear message, or resolve it to `node <cli.js>`.
3. Strip `<think>` blocks only outside the JSON answer: before the first `{` and after its matching `}`. Same rule in the splitter.
4. Skills: skip folders and files whose `LinkTarget` is not null, cap `SKILL.md` at 64 KB, let a user skill win a name clash with a project skill, and mark project skills as such in the data returned so the UI can label them.
5. Tests: the argument list; a file body containing `<think>…</think>` survives; a symlinked skill is skipped; user skill beats project skill.

**Unit F — Prompt structure (M5)**
Files: `src/Factory.Engine/Orchestration/PromptBuilder.cs`, `AgentProfiles.cs`, tests that assert prompt text.
1. Wrap each untrusted block (spec, repo map, file contents, definitions, package signatures, skill bodies, test output, diffs, fix history) in a per-call random delimiter, and neutralise that delimiter if it appears in the content.
2. Each system prompt states that text inside those blocks is data and that instructions come only from outside them.
3. Human guidance goes in its own nonce-tagged block. Add an optional source parameter so guidance from MCP is labelled "Guidance from the driving agent", without the "overrides the rest" wording, and is left out of the reviewer prompt. Unit G passes the source.
4. Tests: content containing `## Human guidance` or the delimiter stays inside its block.

**Unit H — Small UI fixes (L2, L3)**
Files: `src/Factory.Web/Components/RunView/PlanEditor.razor`, `SummaryView.razor`.
1. Plan editor: show each model-proposed command in a wrapping, full-width, read-only block tagged "proposed by the model", with a tick per command; unticked commands are sent as an empty string. Flag commands with control characters, newlines or long runs of whitespace.
2. Summary: copy `gh pr create --base … --head … --fill --body-file pr.md`, with no title interpolated.

**Unit I — Supply chain (L15)** — moved to wave 3, because lock files depend on the packages the other units add.
Files: `Directory.Build.props`, a new `nuget.config`, generated `packages.lock.json` files.
1. Enable `RestorePackagesWithLockFile`, commit the lock files, add package source mapping to nuget.org.

### Wave 2 — one agent, after wave 1 is merged

**Unit G — Orchestrator gates (M2, M3, M4, L13, L14, rest of H2/LLM-2)**
Files: `src/Factory.Engine/Orchestration/FactoryOrchestrator.cs`, `src/Factory.Engine/AdHoc/AdHocManager.cs`, `src/Factory.Contracts/*` and one EF migration if a field is needed, `tests/Factory.Tests/OrchestratorTests.cs`. Serial because every item lands in the same file.
1. **Scope (M4).** In `ApplyPatchAsync`, pause for the human (`NeedsHuman`) when an operation hits `FilePatcher.IsSensitivePath` and the path is not one of the subtask's approved targets.
2. **Detection (M2).** Detect the verification command once at run start, store it on the task, and show it. Re-detect only when nothing was found before. When a later detection differs, pause instead of running it.
3. **Gate tampering (M2).** Pause on a Bug Fixer patch that deletes a test file or edits the files that define the check (`package.json` scripts, `*.sln`, test project files, `pytest.ini`) unless they are subtask targets.
4. **Review coverage (M3).** When the staged diff is longer than `MaxDiffChars`, review it in per-file chunks and require every chunk to pass; if one file alone is too long, pause for the human. Reject a verdict with `passed` and blocking issues.
5. **Budgets (L13).** Pause when the planner returns more than a configurable number of subtasks (default 40). Cap budget resets from MCP `retry_fixer` per subtask. Pass the guidance source through to unit F's parameter.
6. **Path checks (L14).** Add the separator to the prefix check near line 1188, or reuse `ResolveSafePath`. `DeleteDirectory` skips reparse points when resetting attributes.
7. Unify the secret pattern list from units A and C.
8. Tests, using the existing fake model: an out-of-scope `.github/workflows/x.yml` write pauses; a `package.json` added mid-run pauses instead of running `npm`; a 70,000-character diff is fully reviewed; `passed` with blocking issues is rejected; a 41-subtask plan pauses.

### Wave 3 — check and document

**Unit J — Regression check and docs (one Sonnet agent, then the lead)**
1. Re-run each High and Medium exploit from the expert reports as a test or a scripted check against the merged branch, and report any that still works.
2. Update `README.md` Safety, `docs/how-it-works.md` and `docs/mcp.md` to match: the token, what is and isn't filtered, that verification still executes model-written build files with no sandbox, the Claude escalation and what it sends.
3. The lead runs `dotnet build`, `dotnet test`, starts the app, and checks by hand: the UI opens through the link, the MCP client connects with the token, a run completes on a throwaway repository.

## Known limits after this plan

- Verification still runs model-written code with the user's permissions. The fixes narrow what the model can choose and hide; they do not sandbox it. The README's advice to use a VM or container stands.
- Prompt delimiting (M5) lowers the success rate of injection; it does not end it. The gates in unit G are what bound the damage.
- Unit E's Claude Code flags depend on the installed CLI version; the argument test pins what was verified at the time.

## Outcome

Written after the regression check (2026-11-13). Each High and Medium exploit was re-run against the merged branch, by reading the fix and by tests in `tests/Factory.Tests/SecurityRegressionTests.cs`.

### High

- **H1** `ResolveSafePath` rejects `.git` aliases (trailing dot or space, `GIT~n`, invisible format characters, colons, backslash and `./` forms), then re-checks the resolved path. Reads share it. Closed.
- **H2** `/mcp` needs the bearer token (constant-time compare, 401 otherwise, every spelling of the path). Non-loopback connections get 403 first. A foreign Origin gets 403 on every path. Host filtering (`AllowedHosts`) stops DNS rebinding. `approve_plan` refuses user-requested and over-long plans. The token is shown only in Settings. Closed, apart from the UI decision below.

### Medium

- **M1** Git runs with `core.fsmonitor=false`, `safe.bareRepository=explicit`, `--no-ext-diff --no-textconv`, hooks off for read-only calls, a timeout and an output cap, and refuses ref names that start with `-` or hold whitespace.
- **M2** The detected check command is recorded per run, and a different detection pauses until the user re-verifies. A fix that deletes a test or changes a check-defining file outside the plan's targets is refused.
- **M3** A staged diff over `MaxDiffChars` is reviewed in per-file parts, all must pass, a file too long to see pauses the run (lock and generated files excepted), and `passed` with blocking issues is a rejection.
- **M4** A patch that writes `.github/`, `.claude/`, `.hatchet/`, `.vscode/`, `.husky/`, `.gitattributes`, `.gitmodules`, `.npmrc` or `nuget.config` pauses the run unless the plan lists the file or the user's own guidance names it. Checked on the resolved path, so case and `./` variants are caught.
- **M5** Every prompt built by `PromptBuilder` wraps untrusted text in per-prompt nonce-tagged blocks and neutralises the nonce in content. Human guidance has its own block. MCP guidance is labelled as the driving agent's and kept from the reviewer. See open items for what bypasses it.
- **M6** `.env`-style files are not read by `RepositoryScanner.ReadFiles` (so not by the fixer, the definitions or the planner's context files) and new ones are not staged. See open items for the gaps.
- **M7** Symlinked and over-64 KB `SKILL.md` files are skipped, and a user skill beats a project skill of the same name.
- **M8** MCP results for runs, subtasks and requests carry a `note` that the text is data, as do the tool descriptions. `approve_plan` refuses user-owned plans. MCP guidance is labelled and `retry_fixer` is capped at 3 resets per subtask.

### Low and Info

- **L1** Origin check on every path. The SignalR limit stays at 16 MB, because pasting a spec depends on it.
- **L2** The plan editor shows each proposed command in full, with a tick to keep it.
- **L3** The copied PR command uses `--fill --body-file pr.md` with no title in it.
- **L4** UNC and device paths are refused for repository and screenshot paths. The count is checked before reading, and the first bytes before the file.
- **L5** The launch profile no longer sets Development. Responses carry a CSP, `nosniff` and `Referrer-Policy`.
- **L6** The folder picker no longer expands environment variables or follows UNC paths.
- **L7** Detected .NET commands skip solution and project files whose names hold characters outside a plain set.
- **L8** Branch names read from the repo go through `ValidateRefName`: no leading `-`, no whitespace or control characters. It is not `git check-ref-format`.
- **L9** Claude Code runs with pinned settings sources, `dontAsk`, no permission prompts, `--restricted` and added directories for Ask. `CLAUDE_CODE_*` is dropped from its environment. A `.cmd` shim is allowed only with arguments `cmd.exe` can't misread, rather than refused.
- **L10** Windows commands run in a Job Object. Git has timeouts and an output cap. On other platforms there is no process group; leftovers are killed best-effort after the command exits.
- **L11** Credential-looking variables are removed from command environments. Detected Node installs use `--ignore-scripts`.
- **L12** `<think>` blocks are stripped only outside the JSON answer.
- **L13** A planner result over `MaxPlannedSubtasks` (40) waits for approval. MCP `retry_fixer` is capped.
- **L14** The prefix check has a separator. `DeleteDirectory` skips reparse points.
- **L15** Only the root `nuget.config` landed: nuget.org alone, with a source mapping. The lock files did not. `Factory.Web` sets `RuntimeIdentifier` to the SDK's own, so its lock file gets a `net9.0/win-x64` target on this machine and would get `linux-x64` or `osx-arm64` elsewhere, and every contributor on another system would dirty it on restore. The lock files came out the same under SDKs 10.0.303, 10.0.404 and 11.0.100, so the SDK isn't the problem; the operating system is. They can be added once that RID is fixed or dropped.

### Not done, on purpose

- **A login for the web UI.** The owner decided against it (Decision 1). Anyone who can open `localhost:5199` on the machine can use the UI and read the MCP token in Settings. The README says so.

### Still open

From the regression check. None of these were fixed there, because that step reports and does not patch product code.

1. **8.3 short names read secrets (M6, Windows).** `RepositoryScanner.ReadFiles` tests `IsSecretPath` on the name it is given, not the resolved one. `ENV~1.LOC` reads `.env.local` into the prompt: `ResolveSafePath` expands the short name when it opens the file, but the secret check ran on the alias. The write gates are not affected: they test the resolved path. It needs 8.3 names on the volume (the default on C:) and a planner that names the alias. A skipped test, `A_short_name_does_not_read_a_secret_file_into_context`, marks it.
2. **The secret list is by name and short.** Not covered: `.npmrc`, `.netrc`, `.pgpass`, `*.p8`, `*.jks`, `*.keystore`, `credentials.json`, `service-account*.json`, `appsettings.*.json`, `*.tfstate`, `foo.env`. A tracked secret file that a fix modifies is staged, and its diff goes to the reviewer and the database.
3. **Untrusted text that reaches a model outside a data block (M5).** The Ask context (the status report, built from run titles, pause reasons and events) and the question go to Claude undelimited (`ClaudeCodeAsker.cs`, the `WritePromptToAsync` call; `AdHocManager.AnswerAsync`). Ask can only read. The triage write-up that Claude produces from screenshots and repository files is pasted into a run's spec (`ReportBuilder.BuildSpec`), where it is delimited as the spec but has the spec's authority; the run's plan always waits for the user. Parse errors and runaway notes appended by `PromptBuilder.Repair` and `AfterRunaway` quote the model's own earlier output outside a block.
4. **The oversize-review exemption is by name (M3).** `IsLockOrGenerated` in `FactoryOrchestrator.cs` skips the pause for a file named `*.Designer.cs`, `*.min.js` or a lock file. A model can name a large new source file that way and the reviewer sees only the first 60,000 characters of it.
5. **Gate tampering is narrow (M2).** `LooksLikeTestFile` is case-sensitive on `Test`, so deleting `test_foo.py` outside a `tests` folder isn't caught. `DefinesCheck` does not cover `Directory.Build.props`, `*.targets`, a non-test `.csproj`, `pyproject.toml`, `conftest.py` or `build.gradle`. A fix can still add an MSBuild `Exec` target to the main project. This is the known limit again: verification runs model-written code.
6. **A driving agent can clear two gates.** With the token, MCP `reverify` accepts a changed check command and MCP `resume` carries on past an unreviewed oversize file. That follows Decision 3, but it means the pause for the user only holds against a client that behaves.
7. **Tools without the data note (M8).** `list_runs` (`pauseReason`), `list_requests` (`error`, titles) and `probe_repository` (`error`) return text derived from repositories and builds without a `note`. The fields were not renamed `untrustedOutput` as unit B planned.
8. **A skills folder that is itself a link (M7).** Only each skill's folder and file are checked for links, not `.hatchet/skills` or `.claude/skills`, so a repository can link either folder to another directory, and any `*/SKILL.md` under it (up to 64 KB) is loaded.
9. **Job Object race (L10).** The command is assigned to the job just after it starts, so a child it starts in that instant isn't in it. On Unix there is no process group at all.

### The plan's own wording

"How the work is run" describes the subagent setup as it was meant to run. Wave 3 was done by one agent rather than the lead, and Unit I did not land as written (see L15). Nothing else there is false.
