# How Hatchet works

The [README](../README.md) shows the pipeline at a glance. This page covers the behaviour behind it: how fixes are kept to the spec, how patches are checked, what is verified and reviewed, how untrusted text is marked in prompts, what happens when the final check fails, what agents are told about types, how runaway answers are handled, and how runs queue.

For skills, see [skills.md](skills.md). For driving Hatchet from Claude Code, see [mcp.md](mcp.md).

## Keeping fixes to the spec

A fix has to fix what the spec asks for, not route around it. If a fix takes a type the spec names out of the subtask's files altogether, such as swapping a named component for plain markup, Hatchet doesn't apply it. The next attempt is told what to keep, along with the failure it still has to fix. Only real types count, from the repository or its packages, so a fix can still replace a member the spec got wrong.

Fixes before the first review and fixes after it have a budget each. A change that only builds on its last allowed fix still gets fixed for what the reviewer finds, instead of pausing as soon as the reviewer sees it.

A fix also can't make the check pass by changing the check. Hatchet refuses a Bug Fixer patch that deletes a test file, or changes `package.json`, a solution, a test project, `Makefile` and the like, unless the subtask's plan lists that file. Like the spec guard, a refusal changes nothing, doesn't use up an attempt, and tells the next attempt why. Editing a test is still allowed.

## Patch safety

Every patch is checked whole before anything is written. A path must stay inside the repository: no absolute paths, no `..`, no symlink on the way, and nothing in `.git`, whichever way it is spelled (`.git.`, `GIT~1`, a trailing space, invisible characters). The path is checked a second time after it is resolved, so a spelling the first check missed still can't reach `.git`. Reading a file into a prompt goes through the same check.

Some files can run code or change how tools behave: `.github/`, `.claude/`, `.hatchet/`, `.vscode/`, `.husky/`, `.gitattributes`, `.gitmodules`, `.npmrc` and `nuget.config`. A patch may write one only if the subtask's plan lists it as a target, or your own guidance, typed in Hatchet, names it. Otherwise nothing from the patch is written and the run pauses with the file named, so you can decide.

Files whose names mark them as secrets, such as `.env`, `*.pem` and `id_rsa`, are never read into a prompt, and a new one is left out when Hatchet stages a subtask's files. Either way the run's log names the file. This goes by file name, and the list is kept short so that ordinary files such as `secrets.ts` or `.env.development` are not caught by it. A Windows short name (`ENV~1.LOC`) that stands in for an existing file is refused, so it can't be used to get round the list.

## When the final check fails

Each subtask is checked on its own, and the final check then builds and tests the whole branch from scratch. That can turn up what no single subtask did, such as a member one subtask uses and another never declared, or markup that only a full rebuild compiles.

When the final check fails, Hatchet doesn't stop at once:

1. **Hand it to the Bug Fixer.** Hatchet adds one more subtask, "Make the final check pass". It starts at the Bug Fixer with the check's errors and the files they point at, and uses the final check as its own check. It has the same fix budget, escalation to Claude, guard and review as any other subtask, and is committed as `fix(integrate): ...`.
2. **Check again.** Once that subtask is committed, the final check runs again.
3. **Ask you.** If the check still fails, or the Bug Fixer can't fix it, the run pauses with the errors. Choose **Hand to the Fixer**, with optional guidance, to give it another go as a new subtask. Or commit a fix on the run's branch yourself and choose **Run the final check again**. Over MCP, these are `resume_run` with `retry_fixer` or `resume`.

The Bug Fixer gets one automatic try, so a fix that doesn't hold ends with you rather than a loop.

## What gets verified and reviewed

**The check command is pinned.** When no command was set for the run, Hatchet detects one from the repository and records it at the start. Before each check it detects again. If the answer is different, for example because a model-written `package.json` now makes it an npm project, Hatchet doesn't run the new command. The run pauses and shows both, and **Re-verify** (or **Resume**, at the final check) accepts the new one. If nothing could be detected at the start, the first command that appears is recorded and run. A command you set yourself is not re-detected.

**Reviews cover the whole diff.** Everything staged is committed, so everything staged has to be seen. A staged diff longer than the review limit (`MaxDiffChars`, 60,000 characters by default) is split by file into parts, each reviewed in its own call, and the change passes only if every part does. A single file bigger than the limit is cut short for the reviewer, so a pass isn't enough: the run pauses with the file named, and **Resume** commits it as it is once you've read it. Lock files and generated files (`package-lock.json`, `*.Designer.cs` and the like) are committed with a note in the log instead. A verdict that says `passed` and also lists blocking issues is counted as a rejection.

**Git runs with less trust.** Every call turns off `core.fsmonitor` and `safe.bareRepository`, and diffs use no external diff or text conversion program, so opening or diffing an untrusted repository doesn't start its programs. Hooks are ignored for read-only calls, and still run for the calls that change the repository, such as Hatchet's own commits. Each call has a timeout and an output cap, and refs that start with `-` are refused.

**Commands run with less.** Variables whose names look like credentials are removed from the environment of every command. On Windows a command runs in a Job Object, so anything it started in the background is killed when it ends.

None of this makes the build or the tests safe to run. They are still model-written code, with your permissions.

## Prompts and untrusted text

Much of what goes into a prompt comes from a place the model, or someone else, could write to: the spec, files in the repository, test output, diffs, skills and earlier fix attempts. Each of those goes inside a tagged block with a random tag, made fresh for every prompt, and the system prompt says text inside a block is material to work on, not instructions. If the content contains the tag, it is rewritten, so it can't close its own block or open one. Your guidance sits in a block of its own. Guidance that an MCP client sends is labelled as the driving agent's, without the "follow it first" wording, and the reviewer never sees it.

This lowers the odds that text in a file can steer an agent. It doesn't make it impossible. What bounds the damage is the checks above: the plan-listed writes, the pinned command, the review of everything committed.

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
