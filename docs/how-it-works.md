# How Hatchet works

The [README](../README.md) shows the pipeline at a glance. This page covers the behaviour behind it: how fixes are kept to the spec, what agents are told about types, how runaway answers are handled, and how runs queue.

For skills, see [skills.md](skills.md). For driving Hatchet from Claude Code, see [mcp.md](mcp.md).

## Keeping fixes to the spec

A fix has to fix what the spec asks for, not route around it. If a fix takes a type the spec names out of the subtask's files altogether, such as swapping a named component for plain markup, Hatchet doesn't apply it. The next attempt is told what to keep, along with the failure it still has to fix. Only real types count, from the repository or its packages, so a fix can still replace a member the spec got wrong.

Fixes before the first review and fixes after it have a budget each. A change that only builds on its last allowed fix still gets fixed for what the reviewer finds, instead of pausing as soon as the reviewer sees it.

## What agents are told about types

Before each call, Hatchet finds the types the subtask, its files and its errors name, and adds their definitions to the prompt, so agents use real members instead of guessing:

- **The repository's own types.** It adds the source files that declare them.
- **Types from packages.** For .NET projects that have been restored, it adds the public signatures of the package types they name, such as a UI library's components with their namespace and parameters. They're read from the compile assemblies listed in each project's `obj/project.assets.json`. Only names that read as code are looked up, such as `FluentSelect` or `IToastService`, so capitalised words in prose don't pull in types.

## Runaway answers

A local model at low temperature sometimes gets stuck repeating itself. It might count up through made-up table rows, or restate one thought over and over until its context window is full. Hatchet watches each answer and its thinking as they stream in. When many lines in a row differ only in their numbers, or the latest text compresses far better than any real code, it stops the call, usually within a few thousand tokens. It handles an answer that fills a context window that's already 64k or bigger the same way, since a bigger window wouldn't help. It then works through these steps:

1. **Keep the whole files.** A cut-off build keeps every file it finished. The Code Generator is then asked for only the missing files, so one oversized file doesn't cost the whole subtask.
2. **Try again, differently.** The call runs once more at a higher temperature, with a note on what went wrong and a reminder to keep data files short.
3. **Hand it to Claude.** If that goes the same way, Claude takes the call. You can turn this off in Settings.
4. **Pause.** If nothing works, the run pauses. The message says which steps were tried and what to do next.

When the window really is too small, the pause says so and tells you how to load the model with a bigger one. Before a call, Hatchet leaves the type definitions out of a prompt that wouldn't leave the model room to answer. The planner and agents are also told never to type out long data from memory, such as registries or lookup tables. Instead they plan a script that fetches the data, plus a small sample.

## Projects and the queue

The runs list groups runs by project, which is the repository they build in. You can start as many runs as you like, and the ones past the limit wait in line:

- **Runs at once.** A run counts as a local run if any of its agents uses the local model. Local runs go one at a time by default, because the model answers one request at a time. Runs whose agents are all set to Claude go up to 4 at a time. Both limits are in Settings.
- **One run per project.** Runs in the same repository go one at a time, because they share its working tree. A run waits until the one before it in that project is merged, closed or cancelled. A run waiting for its project doesn't hold up runs in other projects.

Resuming a paused run, approving its plan or asking for a new plan also waits for a worker, and what you asked for happens when the run's turn comes. A queued run starts by itself then. Stop waiting takes it out of the line without doing what it was waiting to do, and Resume puts it back at the end. Runs still in line when Hatchet stops join it again when Hatchet starts.

When a run's turn comes, Hatchet checks the working tree. If it has uncommitted changes, or no branch is checked out, the run pauses so you can tidy the tree and resume it.
