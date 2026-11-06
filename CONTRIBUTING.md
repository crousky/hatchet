# Contributing to Hatchet

Thanks for taking an interest. Hatchet is a small personal project, so the process is light: open an issue, or send a pull request.

## Before you start

- For anything bigger than a small fix, **open an issue first** to talk it through. It saves you from building something that doesn't fit.
- Bug reports help most when they include what you ran, the model and server you used (for example, Qwen 3.8 on LM Studio), and the run's log from the agent pane. **Remove anything private from logs and specs before posting them.**

## Building and testing

You need the [.NET SDK](https://dotnet.microsoft.com/download) (9 or 10) and Git.

```bash
dotnet build
dotnet test
```

The tests include full pipeline runs against real temporary Git repositories, with a fake model in place of a real one, so they need Git on your `PATH` but no model server.

Run the app from source:

```bash
dotnet run --project src/Factory.Web
```

## Project layout

```
src/Factory.Contracts   Domain models, DTOs, state enums, strict JSON schemas
src/Factory.Engine      LLM client, Git workspace, command runner, skills, orchestrator
src/Factory.Web         Blazor Server UI, SQLite persistence (EF Core)
tests/Factory.Tests     xUnit tests
```

## Database migrations

After changing an entity, add a migration. Migrations are applied at startup.

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project src/Factory.Web --output-dir Data/Migrations
```

## Pull requests

- Keep each pull request to one change, and add or update tests for behaviour you change.
- Make sure `dotnet test` passes.
- Match the style of the code around you. Comments explain why, not what.
- Describe what changed and why in the pull request. If it changes how Hatchet behaves, update the README or the page in [docs/](docs/) that covers it.

## Reporting security issues

Hatchet runs model-written code and has no login, so security matters here. If you find a way to escape the repository, run an unapproved command, or reach the server from another machine or a web page, please **don't open a public issue**. Use the **Report a vulnerability** button under the repository's Security tab, so it stays private until it's fixed.
