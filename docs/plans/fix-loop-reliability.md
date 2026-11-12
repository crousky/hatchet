# Plan: fix-loop reliability

Written 2026-11-12, after run 6 (zingiber) stalled on subtask 4, "Live project index with FileSystemWatcher-based background updates".

## What happened

1. The Code Generator wrote subtask 4 with four build errors:
   - `ProjectScanner`, made `static` in subtask 3, was taken as a constructor parameter and created with `new`.
   - A `readonly` field was assigned.
   - A literal `\n` joined two `using` lines.
   - `ProjectIndexTests.cs` lacked `using Zingiber.Core.Indexing;`.
2. The first fix rewrote `ProjectIndexTests.cs` with OVERWRITE, cutting it from 265 lines to 181. The truncation check rejected it, which was correct. But `ApplyPatchAsync` **replaced `LastFailure` with the rejection message**, so the compiler errors were gone. Every later fixer said it "couldn't see the build output" and worked from the code alone.
3. As told, the next fixes used EDIT. The dropped-type guard (`DroppedRequiredTypes`) **read an EDIT's SEARCH/REPLACE blocks as if they were the whole file**. Blocks that add a `using` line don't mention `ProjectIndexTests`, so the class looked taken out and every fix was refused.
4. After three refusals in a row the run paused for a person. The pause asked them to "tell the fixer how `ProjectIndexTests` should be used". Nobody could have answered that, because the guard was wrong and nothing showed it. The cycle used about 600k tokens.

Both bugs came in with 3d4e52f, which added EDIT, because the code around it still assumed `FileContent` is the whole file.

## Already fixed

- **Bug A, the guard reads EDIT blocks as the file.** `FilePatcher.Preview` now works out each file's final content without writing anything. `DroppedRequiredTypes` judges that preview. Test: `An_edit_is_judged_by_the_file_it_leaves_not_by_its_blocks`.
- **Bug B, a rejected patch erases the failure.** The rejection now keeps the failure under "The failure it was meant to fix:", the same way a guard refusal does. Test: `A_rejected_fix_keeps_the_failure_it_was_meant_to_fix`.
- The zingiber code for subtask 4 was fixed by hand and re-verified. The run carried on.

## Long-term work

### P0: stop false stalls

**1. One model of what a patch does.** Every check should judge the files as the patch leaves them, never the raw operations.
- Make `Preview` return a `PatchPlan` that holds each file's content before and after, plus the applied list and warnings. `Apply` writes a plan, and every check reads the same plan.
- Split `FileOperationDto.FileContent` into `Content` (CREATE and OVERWRITE) and `Edits` (EDIT), so blocks can't be mistaken for a file again. Keep the wire schema as it is and map it at the boundary.
- Add an architecture test so that only `FilePatcher` can read the raw content of an operation.

**2. Keep the failure separate from notes about patches.** `LastFailure` is now one string that several paths overwrite, and the code finds the real failure again by searching for a marker phrase.
- Give the subtask two fields: `VerificationFailure` holds the last real build, test or review output. `PatchNote` says why the last patch wasn't applied (rejected or refused) and is cleared once a patch applies.
- The fixer prompt always gets both, and `FailureBeingFixed` with its marker parsing goes away.
- Migration: a new nullable column, filled from `LastFailure` once.

**3. Guards that block must show their evidence, and a person must be able to overrule them.**
- When the guard refuses a fix, record where the name was before (file and line) and the fact that the planned files no longer contain it. Show this in the log and in the pause.
- Try the refused patch in a scratch git worktree (build and tests). If it passes, apply it and pass the possible dropped type to the Static Reviewer as a finding instead of refusing. A guard should not hold back a fix that works.
- Notice disagreement. If the fixer's summary says it kept X and the guard twice says X was taken out, pause with "Hatchet's guard may be wrong here", not "tell the fixer how to use X".
- Add a `resume_run` action, `apply_refused`, that applies the last refused patch and re-verifies. Show the refused diff in the pause.

### P1: fewer broken first builds

**4. Catch spec contradictions before building.** The spec for subtask 4 said the watcher is "built from ProjectScanner", but subtask 3 made `ProjectScanner` static. The plan itself contained the contradiction.
- Give the Context Specifier the declarations, with modifiers, of the existing types the spec names. `DefinitionFinder` already finds them.
- Ask it to settle conflicts such as creating a static class, assigning a read-only member or subclassing a sealed class in the refined spec, and to say which way it settled each one.

**5. Cheap repairs before the build.** A literal `\n` or `\t` outside a string literal in source code is almost always the model escaping its JSON twice.
- Detect it after the patch is applied and either repair it or put it in front of the fixer, before a build cycle is spent on it.
- Log every repair.

**5b. Make the subtask check as strict as the final check.** Run 6 later stalled at the final check. `dotnet build --no-incremental` found two errors that every incremental `dotnet test` had missed:
- `HotkeyModifiers.None` was used by subtask 23 but never declared by subtask 12.
- A `bool[]` attached property in a DataTemplate broke from subtask 20 onward (WPF error MC4102).

WPF's markup pass is skipped when its outputs look current. A failed final check now goes to the Bug Fixer automatically (see how-it-works.md), but it is cheaper to catch these at the subtask that causes them:
- Run the final check's command as the subtask check whenever a subtask touches `.xaml`, project files or shared types, or on every fifth subtask.
- Or clean `obj/` for projects that have XAML before checking.

### P2: cost and trust

**6. A token budget per subtask.** Refused fixes don't count as attempts, so a run can spend a lot before it stops.
- Track tokens per subtask, and pause with a clear message once a set ceiling is passed, for example 150k.
- When a person hands the fixer to another model, let it carry on from the current state instead of starting over.

**7. Check reliability when the spec asks for it.** Subtask 4 asks that tests "pass reliably". A simple version of its watcher test failed 3 times in 5 runs, but Hatchet runs the tests once.
- When the spec mentions reliability, or the tests use timing, file watchers or `Task.Delay`, run the tests three times before accepting. For this suite that costs about a second.

## Order of work

1. Restart Hatchet so the running server has the fixes for A and B. A run interrupted by the restart pauses and can be resumed.
2. Items 3 and 2, the ones that stop false stalls and let a person act.
3. Item 1, the refactor that makes this kind of bug hard to write again.
4. Items 4 and 5 for better first builds, then 6 and 7.

Each item gets a replay test built from this run's subtask 4, like the two already added, so the same stall can't come back unnoticed.
