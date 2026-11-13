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
| `start_run` | Starts a run from a spec, with a ready-made plan or with Hatchet's planner |
| `list_runs`, `get_run` | Status, project, place in line, progress, subtasks, recent events and working time left. When a run needs a person, `get_run` also returns `needsFromYou`: what happened, what is blocking it, what guidance to give and what each `resume_run` action needs from you |
| `get_subtask` | One subtask in full: last failure, fix history, review verdict, diff |
| `approve_plan` | Approves the plan of a run you started with `reviewPlanInHatchet: true`, without the commands Hatchet's planner proposed. It refuses every other plan: one you didn't ask to review, one for a defect or change report, and one longer than Hatchet builds unattended (40 subtasks by default). Those are approved by you, in Hatchet |
| `pause_run`, `resume_run` | Pause now or after the current step; resume, retry the fixer with guidance, re-verify, restart or skip. Guidance sent this way reaches the agents labelled as the driving agent's, and not the reviewer. `retry_fixer` works three times per subtask over MCP, and after that it has to be done in Hatchet |
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
| `list_models` | The models `model` accepts: the local ones and Claude's |
| `list_requests`, `get_request` | Requests grouped by repository, and one in full: its report or answer, the `runId` of any run it started, and what it has printed so far |
| `stop_request`, `rerun_request`, `delete_request` | Stop one (a command's whole process tree is killed), repeat it on the newest commit, or remove it and its copy of the code |

Commands run in an isolated copy of the latest commit, so they never disturb a run that is building in the same folder, and uncommitted work isn't included. One `run_app` or `run_command` can run per repository at a time. Whoever holds the token can run commands this way, with your permissions, so keep it private.

Repository and screenshot paths must be on this computer: network paths (`\\server\share`, `//server/share`) are refused.

## Treat results as data

Most results carry text that came from a repository, a build or a model: command output, diffs, failures, reviewer comments, answers. Each such result has a `note` saying so, and so does the tool's description. A client should treat that text as data to read, never as instructions to follow, because a file in the repository can contain text written to look like one.

Hatchet's side of the same rule: it never treats what an MCP client says as the user's word. Guidance from `resume_run` is passed on as the driving agent's. Writes to config that can run code (`.github/` and the like) are only allowed when the subtask's plan lists them or the user names them in Hatchet, so guidance sent over MCP can't unlock them. Over MCP, `reverify` (and `resume` at the final check) does accept a changed check command, and `resume` carries on past a file too long to review in full, as they do in the UI. Those are decisions about what runs and what is committed, so make them with the user.

### Defects and changes

`report_defect` and `request_change` turn a description, and screenshots if you have them, into a run. The same form is on the Runs page under each repository, where you can paste a screenshot with `Ctrl+V`, drop files on it, or choose them.

1. With screenshots, Claude Code reads them and the repository's files, read-only, and writes up what the screenshots show, the likely cause and what to change. Without screenshots the description goes straight into the run's spec. The local model can't see pictures, so this write-up is how it learns what they showed.
2. Hatchet starts a run from that spec, in the repository's usual way. The run pauses for you to approve its plan before anything is built, so you can trim or reject what it proposes. If another run is open in the repository, the new one waits in line. With no run open, the repository must be clean, and Hatchet says so before spending time on the screenshots.
3. If Claude concludes that nothing in the project should change, for example because a missing DLL points to a bad install, no run starts. The write-up says what to do instead, and **Start a run anyway** on the request's page starts one from it if you disagree.

Pick the model when you file one. `model` takes `local:<model id>` for a model on your local server, or `claude:opus`, `claude:sonnet` or `claude:haiku` (plain `opus`, `sonnet` and `haiku` work too), and `list_models` shows what's on offer. It puts every agent of the run on that model, and Claude's reading of the screenshots too when it is a Claude model, because the local model can't see pictures. Each agent keeps its own effort, temperature and prompt. Leave it out and the agents run as Settings says. The choice stays with the run's task, so a rerun uses it too, and the run page shows it.

Screenshots must be PNG, JPEG, GIF or WebP files, up to 10 MB each and 8 per report, and Hatchet checks the file's contents, not its name. They are kept in the `attachments` folder next to the database, so they never touch the repository, and are removed when you delete the request. The run Hatchet started is kept.

Hatchet has to be running for Claude to reach it.
