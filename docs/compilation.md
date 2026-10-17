# Compilation

A Debug build of the engine and the app takes several minutes on the owner's
15 W laptop and slows everything else on it, and several agents share that
machine. These rules keep builds rare, one at a time, and used.

## Compile last

**Owner ruling: delay compilation until nothing else can answer the
question.** A build started early is repeated after the next edit, so build when
the work cannot continue without a build's answer. That is usually the end of
the task. It is earlier only when later edits depend on the answer, such as how
a WinUI control behaves at run time.

1. Finish all the requested code and the
   [focused review passes](#focused-review-passes).
2. Answer every question you can without a build: read the code, its callers
   and the platform documentation. An existing output or a running app shows
   how the product behaved before your edits; it cannot verify them.
   Documentation-only changes need no build.
3. List what still needs a build: compiler errors, tests, an app inspection or a
   capture. [Testing](testing.md) decides which checks earn their cost.
4. Build once, for that whole list, and run every check on its output.

Done when the build passed and every listed check has a result.

## Build

Run [Build.ps1](../Build.ps1) from the repository root with `pwsh`. One build
covers the whole list:

- Add `-Engine` when only engine code changed and the checks run the engine
  alone, such as [Checks.ps1](../engine/tests/Checks.ps1): it compiles the
  engine and prints its path. It leaves the app folder alone, because the
  window there may not speak this engine's protocol; to run the window with a
  changed engine, build without `-Engine`. Add `-App` when only app code
  changed: it compiles the app and copies the engine's last build beside it.
  Without either, both build. Each part ignores edits that only the other part
  reads, so another agent's work on the other part does not rebuild yours.
- Add `-Capture` when the list includes a capture. The capture build compiles
  everything the normal build does, so every check runs on it.
- Add `-Project <path>` for each further project a check needs, such as
  `lib\TableView\tests\TableViewTests.csproj`.
- Add `-Release` only for optimised measurements or release work.

A build takes minutes, so run it where your tool's command time limit cannot
stop it.

The script owns the shared rules, so follow its output instead of repeating its
work:

- It waits while another build runs, so builds never overlap.
- It reuses an output that already matches the current build inputs, so a
  repeated request costs seconds.
- It builds into the first output folder that no running program uses,
  including an app DLL loaded by a benchmark host, so it never replaces a
  running app or a benchmark's assembly.
- It runs MSBuild at below-normal priority, so the owner's own work stays
  responsive.
- It records each build in `artifacts\compilation\builds.jsonl` and warns when
  generated output appears outside `artifacts\`.

## When a build fails

Read the whole log and fix every error before you build again, because each
attempt costs minutes.

- Errors in your change: fix them.
- Errors in files another agent is changing: leave its files alone and tell
  that agent or the owner when you can. Report your task as blocked by that
  error, with the log path, and build again only after those files change.
- The same error after your fix: stop and report the error, what you changed and
  the log path.
- `3rdParty\` incomplete: stop and report it to the owner.

## Verify the build before launch

Run every check from the output your last `Build.ps1` run printed, and run
`Build.ps1` again only after you edit a build input. An executable from another
folder, or one built before your last edit, shows old code. The check scripts
default to other folders, so pass the printed paths:

- App check scripts: `-BinaryDirectory <folder>`.
- [Checks.ps1](../engine/tests/Checks.ps1): `-EnginePath <folder>\TinyTorrent.exe`.
- [Capture.ps1](../app/tests/Capture.ps1): `-ArtifactsPath <artifacts>`, and
  `-Configuration Release` after a `-Release` build.

A capture waits while another capture runs, and it stops development instances
of this checkout, so ask the owner first when they are using one.

## Focused review passes

Finish the feature and review it before you compile, not after. Preserve the
user's cumulative steers and their order in the working plan. These passes
supply the default review sequence where the user has not specified one. Each
pass reads the current result of the preceding fixes; three independent
readings of the original diff do not verify those fixes.

1. **Correctness and paths.** Trace every changed behavior from its entry points
   through its callers, state changes and consumers. Follow applicable empty,
   error, cancellation, repeated-operation and lifetime paths, including indirect
   consumers such as capture reflection. Fix defects found within the feature.
   Complete when affected paths have been traced and every finding has a fix or
   an explicit unresolved reason; compilation alone cannot establish this.
2. **Ownership and simplicity.** Apply the existing
   [code principles](../AGENTS.md#code-style) to the feature as a whole. Check
   that each rule has one authoritative owner and implementation; remove
   duplication, competing state and ceremony without a concrete requirement.
   Refactor when it materially clarifies responsibility or simplifies the code,
   rather than preserving a poor structure to keep the diff small. Reconsider
   relevant deferred findings, while respecting explicit user deferrals and
   leaving unrelated architecture alone. Complete when remaining layers and
   state have a concrete purpose and the feature follows its established owners.
3. **Fresh read after fixes.** Read the resulting code as its next maintainer.
   Recheck paths affected by corrections and simplifications, then apply the
   [naming](naming.md) and [comment](comments.md) policies. Prefer clear intent
   and straightforward control flow over cleverness. Complete when the corrected
   paths have been revisited and no actionable finding from this pass remains
   unaccounted for.

A new finding means repair and another focused pass over the affected behavior
and its consumers. Continue while new defects or concrete simplifications emerge;
stop repeating a settled review. Scale the work to the change: a small fix can
receive three short, distinct passes without three reports, extra agents or new
tests.

Completion requires the cumulative requested work, these applicable passes and
the needed validation evidence; otherwise report the precise remaining work
without calling the task done.
