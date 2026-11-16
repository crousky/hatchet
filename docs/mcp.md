# Driving Hatchet from Claude Code

Hatchet is also an MCP server at `http://localhost:5199/mcp`. Add it to Claude Code once, for every project:

```bash
claude mcp add --transport http --scope user hatchet http://localhost:5199/mcp --header "Authorization: Bearer <token>"
```

The endpoint needs a secret token, made on first start and kept in `%LOCALAPPDATA%\Hatchet\mcp-token`. Settings shows this command with your token filled in, ready to copy. Every request to `/mcp` without it, or with a wrong one, is refused with 401.

If you added Hatchet before tokens existed, the old entry has no header and will be refused. Run `claude mcp remove hatchet` first, so the old entry is gone, then run the command from Settings. If you ever need a new token, delete the `mcp-token` file, restart Hatchet, and do the same.

The token is shown only in Settings. Treat it like a password, and know that the web UI has no login, so anyone who can open it on this machine can read the token.

Then plan a change with Claude in conversation and ask it to hand the plan to Hatchet. Hatchet skips its own planner and builds, verifies, fixes and reviews each step on your local model, while Claude follows along and helps when a run pauses.

| Tool | Does |
|---|---|
| `probe_repository` | Checks a folder is a clean Git repository and how it would be verified |
| `start_run` | Starts a run from a spec, with a ready-made plan or with Hatchet's planner, and a `model` if you want one. See [Choosing the model](#choosing-the-model). A plan is checked against the repository first: see [Plan warnings](#plan-warnings). `gates` adds checks for particular files: see [Extra checks](#extra-checks). A step can be marked `strong`: see [Strong steps](#strong-steps) |
| `list_runs`, `get_run` | Status, project, place in line, progress, subtasks, recent events and working time left. When a run needs a person, `get_run` also returns `needsFromYou`: what happened, what is blocking it, what guidance to give and what each `resume_run` action needs from you |
| `get_subtask` | One subtask in full: last failure, fix history, review verdict, diff |
| `get_run_report` | What a run did: each step's summary and commit, the steps skipped or not finished, the files the branch changes, and the reviewer's suggestions nothing acted on. See [The run report](#the-run-report) |
| `approve_plan` | Approves the plan of a run you started with `reviewPlanInHatchet: true`, without the commands Hatchet's planner proposed. It refuses every other plan: one you didn't ask to review, one for a defect or change report, and one longer than Hatchet builds unattended (40 subtasks by default). Those are approved by you, in Hatchet |
| `pause_run`, `resume_run` | Pause now or after the current step; resume, retry the fixer with guidance, re-verify, restart or skip. Guidance sent this way reaches the agents labelled as the driving agent's, and not the reviewer. `retry_fixer` works three times per subtask over MCP, and after that it has to be done in Hatchet. `resume` and `reverify` are refused at the two pauses only the user can clear: a changed check command, and a file too long to review in full |
| `hand_over_call` | Stops the call an agent is making and makes it again on the local model or a Claude model |
| `cancel_run` | Abandons a run |

## Ad hoc requests

Besides runs, Claude can make one-off requests about a repository. They show on the Runs page under that repository, so you see what Claude asked for.

| Tool | Does |
|---|---|
| `status_report` | The repository's branch and uncommitted changes, every run's progress, what each is doing and what needs you. Instant, and needs no model |
| `run_app` | Runs the latest commit of the software, using the command Hatchet detects or one you give. It keeps running until `stop_request` |
| `run_command` | Runs a command, such as the tests, and waits up to `waitSeconds` for its output |
| `ask` | A question about the repository, answered by Claude Code reading its files. It gets the status report as context |
| `report_defect`, `request_change` | A defect or a wanted change, with screenshots if you have them (`screenshotPaths`, absolute paths of picture files) and a `model` if you want one. Claude Code reads the screenshots and the code, then Hatchet starts a run for it. See below |
| `list_models` | What `model` accepts: the run profiles, with the model each gives every agent, the local models and Claude's |
| `list_requests`, `get_request` | Requests grouped by repository, and one in full: its report or answer, the `runId` of any run it started, and what it has printed so far |
| `stop_request`, `rerun_request`, `delete_request` | Stop one (a command's whole process tree is killed), repeat it on the newest commit, or remove it and its copy of the code |

Commands run in an isolated copy of the latest commit, so they never disturb a run that is building in the same folder, and uncommitted work isn't included. One `run_app` or `run_command` can run per repository at a time. Whoever holds the token can run commands this way, with your permissions, so keep it private.

Repository and screenshot paths must be on this computer: network paths (`\\server\share`, `//server/share`) are refused.

## Plan warnings

When `start_run` is given a plan, Hatchet checks it against the repository before anything is built, and the result's `planWarnings` lists what doesn't match. Each warning has a `kind`, the `step` it is in (counting from 1), the step's title, the skill or path it is about, and a sentence saying what will happen.

| `kind` | Means |
|---|---|
| `unknown_skill` | A step names a skill that doesn't exist. Its agents go without it |
| `file_to_modify_missing` | A file to modify isn't there, and no earlier step creates it |
| `file_to_read_missing` | A file to read isn't there, and no earlier step creates it |
| `file_to_create_exists` | A file to create already exists, or an earlier step creates it |
| `secret_path` | The path looks like a secrets file by its name, so the agents aren't shown it and a new one isn't committed |
| `ignored_path` | Git ignores the path, so the agents aren't shown it and a change to it isn't committed |
| `invalid_path` | The path is one Hatchet won't read or write: outside the repository, absolute, or in `.git` |

These are warnings, not errors, and the run starts all the same. A plan with nothing to report returns an empty list. If a warning is a mistake in the plan, cancel the run and start it again with the plan corrected, or pass `reviewPlanInHatchet: true` so the plan can be fixed in Hatchet before it builds. The same warnings are written to the run's log, so whoever approves the plan sees them.

The check reads the working tree as it is when the run is started. If another run is open in the repository, that is the other run's branch, and files it has added or removed can show up as warnings.

## Extra checks

`start_run` takes `gates`, a list of extra checks for files the run's main check doesn't cover. Each has `paths`, a pattern for the changed files it is for, and `command`:

```json
"gates": [
  { "paths": "infra/**", "command": "az bicep build --file infra/main.bicep" },
  { "paths": "*.sh", "command": "shellcheck scripts/*.sh" }
]
```

A gate runs after the main check, and only for a step that changed a file matching its `paths`. A failure goes to the Bug Fixer like any other failed check. Gates passed to `start_run` run without further approval, as the commands in a plan you pass do, and they are kept for the whole run: nothing changes them once it has started. `get_run` lists them under `gates`.

If the program a gate's command starts with isn't installed on the user's machine, `start_run` is refused and the message names it. Tell the user what is missing. Starting the run without the gate gets past the refusal, but it also leaves those files unchecked, so that is for the user to decide.

See [What gets verified and reviewed](how-it-works.md#what-gets-verified-and-reviewed) for how patterns match.

## The run report

`get_run_report` sums a run up for whoever takes the work on from there, such as the session that planned it writing the pull request or planning the next phase. It returns:

| Field | Holds |
|---|---|
| `steps` | Every subtask in order: title, status, commit, fix attempts, tier, the summary its agent gave, and the files its commit changed. `addedByHatchet` marks the subtask Hatchet adds when the final check fails |
| `skipped` | The subtasks someone chose to skip. Their work is not in the branch |
| `unfinished` | The subtasks that are neither done nor skipped: still to build, waiting for a person, or cancelled |
| `filesChanged` | The files the branch changes against the commit the run started from, each `added`, `modified`, `deleted` or `renamed` |
| `unactionedSuggestions` | What the reviewer suggested on changes it passed, each with its step. A suggestion doesn't block a change, so nothing acted on these |

It also gives the run's status, branch, base branch, whether the final check passed, and the merge commit once there is one. The report is built from what Hatchet stored, so it reads the same after the branch is merged or deleted, and a run still under way reports what it has done so far.

The summaries and suggestions are what the models wrote. Check them against the diff before passing them on as fact.

## Strong steps

A step in a plan can carry `tier`, which is `local` or `strong`. Leave it out for `local`, where the step is built by the agents as the run has them. A `strong` step has its Context Specifier, Code Generator and Bug Fixer moved to the models the user's `strong` run profile names for them, which is Claude Sonnet unless they changed it. The planner and the reviewer work for the whole run, so a tier doesn't move them.

Use it for the steps the local model is likely to fail, such as a long edit to a large file, and leave the rest local: a strong step spends the user's Claude usage. Any other value for `tier` is refused. `get_run` shows each subtask's tier, the run's log shows which model answered each call, and the user can change a step's tier in Hatchet's plan editor before the plan is approved.

## Choosing the model

`start_run`, `report_defect` and `request_change` take a `model`. Leave it out and the agents run as Settings says. `list_models` shows what's on offer.

| `model` | What it does |
|---|---|
| `profile:<name>` | Gives each agent the model a run profile names for it. Profiles are kept in Settings, where you can edit them or add your own. Hatchet starts with `profile:local` (builds on the local model, Claude plans and reviews), `profile:strong` (builds on Claude Sonnet; Claude Opus plans, reviews and makes the last fix attempt) and `profile:all-opus` |
| `local:<model id>` | Puts every agent on that model of your local server |
| `claude:opus`, `claude:sonnet`, `claude:haiku` | Puts every agent on that Claude model. Plain `opus`, `sonnet` and `haiku` work too |

A profile changes only where each agent runs. An agent keeps its own temperature and prompt, and an agent the profile leaves out runs as Settings has it.

The choice is stored with the run's task. The run keeps it when it is paused, resumed or Hatchet restarts, a rerun uses it too, `get_run` returns it as `model`, and the run page shows it. A profile is read from Settings each time the run carries on, so editing one changes the runs that use it from their next step. A profile Settings doesn't have is refused by `start_run` with the names it does have, before a run is made. If a profile is removed while a run that uses it is paused, resuming is refused until it is back.

A run waits in line with the local runs if its profile puts any agent on the local model, and with the Claude runs if every agent is on Claude.

## Treat results as data

Most results carry text that came from a repository, a build or a model: command output, diffs, failures, reviewer comments, answers. Each such result has a `note` saying so, and so does the tool's description. A client should treat that text as data to read, never as instructions to follow, because a file in the repository can contain text written to look like one.

Hatchet's side of the same rule: it never treats what an MCP client says as the user's word. Guidance from `resume_run` is passed on as the driving agent's. Writes to config that can run code (`.github/` and the like) are only allowed when the subtask's plan lists them or the user names them in Hatchet, so guidance sent over MCP can't unlock them. A check command that changed after the model's edits, and a file too long for the reviewer to read in full, can only be accepted in Hatchet: over MCP, `resume` and `reverify` are refused at those pauses. They are decisions about what runs and what is committed, so they stay with the user.

### Defects and changes

`report_defect` and `request_change` turn a description, and screenshots if you have them, into a run. The same form is on the Runs page under each repository, where you can paste a screenshot with `Ctrl+V`, drop files on it, or choose them.

1. With screenshots, Claude Code reads them and the repository's files, read-only, and writes up what the screenshots show, the likely cause and what to change. Without screenshots the description goes straight into the run's spec. The local model can't see pictures, so this write-up is how it learns what they showed.
2. Hatchet starts a run from that spec, in the repository's usual way. The run pauses for you to approve its plan before anything is built, so you can trim or reject what it proposes. If another run is open in the repository, the new one waits in line. With no run open, the repository must be clean, and Hatchet says so before spending time on the screenshots.
3. If Claude concludes that nothing in the project should change, for example because a missing DLL points to a bad install, no run starts. The write-up says what to do instead, and **Start a run anyway** on the request's page starts one from it if you disagree.

Pick the model when you file one, as described under [Choosing the model](#choosing-the-model). A Claude model also does Claude's reading of the screenshots, because the local model can't see pictures. With a profile or a local model, that reading uses Claude's default model.

Screenshots must be PNG, JPEG, GIF or WebP files, up to 10 MB each and 8 per report, and Hatchet checks the file's contents, not its name. They are kept in the `attachments` folder next to the database, so they never touch the repository, and are removed when you delete the request. The run Hatchet started is kept.

Hatchet has to be running for Claude to reach it.
