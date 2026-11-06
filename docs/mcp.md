# Driving Hatchet from Claude Code

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
