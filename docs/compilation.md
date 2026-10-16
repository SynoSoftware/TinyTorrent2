# Compilation protocol

## Common sense comes first

**Owner ruling: common sense serves completion.** Queue ready work, keep working,
and let the first agent that notices an eligible batch compile it for everyone.
Requesters then use that output to finish the checks their tasks need. A failed
build stays with its claimant through repair and a successful retry. This saves
the owner's machine by sharing compilation and using the result.

The [root validation ruling](../AGENTS.md#working-rules) authorizes the focused
build, tests and application inspection needed for the requested work. Choose
the evidence under [testing](testing.md) before queuing a build. Compilation
alone completes validation only when compilation is the evidence the task needs.

If a procedural step prevents this outcome, take the simplest safe alternative
and record why. Resolve routine coordination without asking again for permission
already granted. Resolve contention with the agents involved, using the existing
messages and records. Add coordination machinery only when a demonstrated
conflict cannot reasonably be resolved this way. Preserve explicit user
restrictions, other agents' source and running processes, the shared build
interval and the bounds on unattended work unless the user overrides them.
An eligible request, a running build or a repairable compiler error is work to
finish; reporting it as pending does not discharge its owner.

## Shared record

All chats and worktrees for this repository use
`C:\SynoSoftware\TinyTorrent2\artifacts\compilation`, not a folder in each
worktree. Create it when the first request needs it. Keep coordination records
and logs local; this protocol is the tracked authority.

- `requests/<id>.md`: one file per request, written by its requesting agent.
  Register before editing build inputs so the claimant can identify active
  writers. Record a unique chat/agent identity, source checkout, targets and
  changed paths, the checks needed and required configuration. Distinguish edits
  in progress from a ready revision. Give each ready revision an identifier;
  further edits withdraw an unclaimed revision and publish a new one. Preserve
  claimed revisions and their results so evidence stays tied to what was built.
  After success, distinguish a revision awaiting checks from one still needing
  compilation. Record the build used and each required check's result; the
  request remains outstanding until those checks finish or a real blocker is
  identified with an owner and next action. Record any scheduled follow-up's
  identifier, due time and whether it has been consumed; keep this history across
  revisions so retries cannot reset its allowance.
- `builds/<id>.md`: one record per claimed build, written by its claimant.
  Record owner, process ID, source checkout and checkpoint (commit plus local
  changes), included request revisions, exact commands and build properties,
  output directory, start/end UTC, result, log paths and findings. Record
  hashes of runtime output files after success, including the app assembly,
  engine, XAML resources and assets, so later overwrites can be detected. An
  unchanged executable launcher does not prove its loaded code is unchanged. Record
  no credentials or secret property values in shared reports.
- `build.lock`: the common exclusive file lock for compilation and brief
  coordination claims. Its existence means nothing; holding its OS handle
  establishes exclusion. A coordination claim does not consume a build slot.
- `capture.lock`: the exclusive file lock for the shared capture harness.
  Keep both lock files in place; their handles, not their existence, protect work.

Request owners update their own files; the claimant reports coverage and the
output to use in its build record. This avoids agents overwriting one shared
queue document. A new worktree's different source revision is not covered just
because its request names the same project.

Use available session messaging to coordinate readiness, hand off a finding or
announce a result; the owner authorizes those build-coordination messages.
Record resulting decisions here as well, because Codex, Claude and other workers
may not share a messaging channel. Files are the common handoff, not a reason
to avoid direct communication. Transfer ownership only after the recipient accepts;
a sent message alone does not transfer responsibility.

## Queue, keep working, claim

1. **Check and register.** Before editing build inputs, read active builds,
   latest results and outstanding findings, then register the inputs you will
   change. Reuse successful evidence covering the requested source revision and
   configuration. Documentation-only changes require no compilation. Complete
   when your request identifies its inputs, intended checks and current owner.
2. **Queue a ready revision.** Finish a coherent set of edits and its applicable
   [review passes](#focused-review-passes), fixing related queued findings
   together. Mark that revision ready with the targets and configuration its
   checks need. Readiness queues compilation; it neither freezes the checkout
   nor completes the task.
3. **Keep working and check the queue.** At coherent edit/review checkpoints,
   and before reporting completion, consume available results and check whether
   a batch can start. Continue reviews, diagnosis, other requested work or checks
   using existing outputs while waiting. Further input edits update your request
   unless those inputs are frozen for a running build. When only a future build
   or result remains, [schedule continuation](#schedule-continuation) before
   returning. A timer resumes the work; the user need not send another message.
4. **Observe the shared interval.** A batch may start at least 15 minutes after
   the previous batch started, and only after that batch's compiler processes
   have ended. Success, failure and cancellation all consume that opportunity.
   This applies across targets, configurations and worktrees, so a five-minute
   build does not add another fifteen minutes after its end. With no prior batch,
   the first ready batch is eligible; check existing processes and recent logs
   when adopting the protocol. An empty ready queue needs no build.
5. **Claim compilation exclusively.** The first agent noticing eligibility
   opens `build.lock` with `FileMode.OpenOrCreate`, read/write access and
   `FileShare.None`. The process holding that handle rechecks the interval and
   queue, [selects and freezes the batch](#select-and-freeze-the-batch), records
   its claim, runs compilation and writes the result. Dispose the handle in
   `finally`; hold it throughout compilation. If one request cannot freeze yet,
   consider the next independent ready batch. With none buildable, release the
   claim and continue other work. An agent finding the lock occupied keeps
   working and consumes the owner's result rather than starting a duplicate.

There is no permanent coordinator. The successful claimant is the temporary
build owner. The lock coordinates agents following this protocol; it does not
intercept direct IDE builds or an agent bypassing the protocol. Check for those
before compiling. A user-requested immediate build can override the interval;
record that instruction, while retaining exclusive output ownership.

## Schedule continuation

When useful work is exhausted and compilation or checks are still pending,
schedule one follow-up in this same chat for the next eligible build time. If
that time has passed but another build or writer still owns the inputs, schedule
one check within 15 minutes. Use the platform's native scheduler with a single
occurrence; in Codex, use a thread heartbeat configured for one occurrence.
Confirm that the scheduler accepted the single occurrence before returning.
This resumes an idle task without requiring the user to send another message.

**One automatic resumption per request.** Reuse an existing pending follow-up;
revisions and retries do not grant another one. The scheduler must enforce the
single occurrence even if the callback fails. Cancel an unused follow-up when
the request finishes, is cancelled or becomes genuinely blocked.

The saved prompt names the original request, remaining validation, due time and
shared records. On waking, use the [continuation claim](#use-the-result) before
taking over, then remove or disable the timer. A superseded revision defers to
the request's current revision. Duplicate deliveries leave the accepted owner's
work alone.

Resume the unfinished validation with its existing evidence. If another agent
is building, wait for its result and use it. If a clear repair needs the next
build slot, stay with it and wait in the active turn; ordinary contention does
not send the task back to the user. Use bounded tool waits for a known operation
or eligibility time, and investigate a stalled owner before waiting again.

Keep unattended work bounded: finish the pending checks and, if needed, make a
clear repair and verify it. If that repair still fails or the work has become a
larger investigation, return with the current evidence and exact remaining work.
Further unattended work needs the user's direction. Preserve completed reviews
and checks, and repeat one only when new evidence or a relevant fix warrants it;
a timer is not a fresh assignment to redo the task.

If a single scheduled occurrence cannot be confirmed, use the same bounded
active waits instead of creating a recurring timer. Keep unchanged waits quiet
and notify on completion or required user action. A continuation limit leaves
validation explicitly incomplete, with ownership and next actions preserved.

## Focused review passes

Use the build interval to finish the feature, not to postpone its review until
after compilation. Preserve the user's cumulative steers and their order in the
working plan. These passes supply the default review sequence where the user has
not specified one. Each pass reads the current result of the preceding fixes;
three independent readings of the original diff do not verify those fixes.

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
stop repeating a settled review merely to fill the interval. Scale the work to
the change: a small fix can receive three short, distinct passes without three
reports, extra agents or new tests. Use the existing shared findings record so
each pass builds on earlier evidence rather than restarting it.

When a pass changes a ready revision, withdraw that revision and update the
request. During a source freeze, record corrections and apply them after the
build ends. Completion requires the cumulative requested work, these applicable
passes and the needed validation evidence; otherwise report the precise remaining
work without calling the task done. [Testing](testing.md) still decides which
checks earn their cost.

## Select and freeze the batch

Start with the oldest ready request whose inputs can be frozen, and include all
compatible ready requests present in that checkout. A request waiting on another
writer keeps its place without blocking independent buildable work. Requests
already covered by a successful build need their remaining checks, not another
compilation. Include affected targets and dependencies; unrelated projects and
configurations earn a build only when a queued request needs them. The claimant
owns the whole selected batch, including problems outside its own edit.

| Evidence required | Default build |
| --- | --- |
| Ordinary compilation | Debug x64, normal |
| UI capture | Debug x64, capture |
| Optimised timing | Release x64, capture or normal as the measurement requires |

Choose the evidence before the build flavour. For an authorized visual or
interaction check, use a current normal app when a targeted inspection can
answer the question. Record the actual interaction and result; a screenshot
alone cannot establish drag smoothness, cancellation or other transitions.
Use capture when repeatable fixtures, themes, sizes or scripted timing journeys
add evidence the targeted check lacks. A UI change does not automatically
require a capture build, and a capture is not automatically more thorough.
Build both flavours only when distinct required checks justify each one.

A capture build may cover ordinary compilation requests when the changed code
and required build paths are included. It does not prove normal-only conditional
code or packaging. A Release capture may also serve visual review requests when
they accept it. Keep genuinely incompatible requests queued; do not build every
flavour by habit. Debug and Release are the only configurations. Release is
reserved for optimised measurements, the Release engine checks, release work or
an explicit owner request because it adds link work and another output set.

One batch covers one selected configuration and its required target/dependency
sequence. For example, a changed engine followed by the app that copies it is
one batch. A failed step ends compilation, then its claimant immediately starts
diagnosis and repairs. The corrected batch and incompatible flavours use the
next eligible opportunity; the interval delays compilation, never fixing errors.

Before compiling, agree the included revisions and source freeze with affected
writers, then record the agreement. A ready request alone does not establish
that its writer has stopped editing for this build. Writers keep the agreed
inputs unchanged until the claimant releases them; resuming edits first needs
agreement with the claimant. Resolve unclear ownership or readiness with the
agents involved before starting compilation.

Freeze only the affected inputs while compilation reads them; agents continue
reviews, documentation or work outside those inputs. Release the freeze when
compilation ends, including failure, so edits and repairs can continue during
the interval. The lock alone does not freeze source files. If inputs change
during the build, resolve the writer conflict and queue a settled revision;
record the attempted checkpoint's evidence as unusable.

Prefer the existing `artifacts/` outputs and incremental Build, so repeated
requests reuse prior compilation. Reuse a current engine for app-only changes;
never clean or rebuild merely to be certain. Use Visual Studio MSBuild for WinUI.
If another build owns the output, consume its result through this protocol.
An output awaiting its requesters' checks is reserved just like an output used
by a running program, so the next build cannot overwrite it before it is used.
When an output is reserved, use the lowest free `artifacts/lanes/<n>`, starting at 2,
and pass the same `/p:ArtifactsPath` to engine and app. Return to the default
output when free. Every executable set must have a known source/configuration;
a fresh lane is not evidence that its dependencies are current.

## Finish the result and own failures

The claimant reads the full log and summary, collects all reported errors and
new warnings, groups repeated diagnostics by cause, and investigates unexpected
rebuilds or duration. A compiler may stop before exposing later errors; report
what it found without claiming it caught every possible problem. Compilation
does not establish runtime correctness or replace an authorized capture.

**The claimant owns a failed batch through repair and a successful retry.** Read
the complete diagnostics, inspect their causes and direct consumers, and fix
clear compiler and integration errors once the source freeze is released,
including errors outside the claimant's own edit. For another agent's active
change, agree who will repair it and record acceptance; protect that writer's
work. The claimant still tracks every finding, collects the corrected revisions
and puts the repaired batch back in the queue for the next eligible opportunity.
A repair is finished when reviewed, queued and subsequently compiled
successfully, not when its diagnostic is copied into a report. Transfer batch
ownership only to an agent that accepts this follow-through.

Inspect the whole diagnostic set and direct consumers before queuing repairs;
fixing only the first error wastes the next opportunity. A compiler failure or
the interval is not an external blocker. Stop only for a concrete obstacle such
as unavailable `3rdParty/` dependencies, an explicit user restriction, or a
material decision that cannot be resolved from current source and requirements.
Record the exact obstacle, responsible owner and next action, and complete
unaffected work. Missing `3rdParty/` dependencies remain a stop-and-report
condition under the root rule.

A retry needs a correction that addresses the diagnosed cause, or new evidence
that the cause has changed. If the same failure returns, compare it with the
previous attempt before spending another build. An unchanged failure without a
new explanation needs investigation, not another identical command. Automatic
continuations also obey their bounded scope above; preserve the failed batch's
owner and next action when that allowance is exhausted.

Record the exit code, end time, covered targets, all findings and next action on
failure as well as success. Older binaries left by a failed build do not validate
the attempted revision. Preserve earlier successful records and use their
outputs only if unchanged and appropriate to the requesting task. Continue with
repairs on failure and [use the result](#use-the-result) on success.

If a build owner disappears, the next claimant checks the recorded process and
any surviving compiler processes before taking over. A released lock alone does
not prove that an orphaned compiler stopped. Record an interrupted attempt and
its end time once termination is established. If its start time is unknown,
use the time termination was established for interval eligibility.

## Use the result

The claimant publishes the covered request revisions, successful output paths
and findings, and notifies requesters through the available coordination channel.
Each requester reads the result at its next checkpoint and owns completing its
recorded checks with that output. The shared build saves every requester another
compilation; it does not finish those tasks for them.

Before a manual or scheduled continuation takes over, briefly hold `build.lock`
to check what remains and claim it. An already consumed scheduled delivery exits;
otherwise mark the delivery consumed even if no work can be taken over. Leave
work with an active continuation alone; completed or cancelled requests need no
work. Record ownership when accepting the remaining work, then release the lock
before doing it or waiting. On return, release active ownership while preserving
the request's results and any unfinished responsibility. Check an interrupted
owner's session and surviving processes before taking over.

Run the focused tests, application inspection or capture needed for the request
under the root validation ruling, checking the
[build provenance](#verify-the-build-before-launch) before launch. Reuse the
same executable across checks and capture modes; changing the mode is not a
compilation request. For a capture, acquire `capture.lock` before the pre-launch
check and hold it through launcher cleanup, using the same exclusive-handle rule
as `build.lock`. Record the capture owner and process in the request, and release
the handle in `finally`. Before launch, resolve conflicting use of the shared
engine session with the agents using it. The capture launcher can stop
development instances across lanes, so establish who owns running instances
and let their checks finish first. Preserve the owner's active application;
use another inspection path or report the blocker when capture cannot safely
run. Clean up an existing instance only when it is known to be abandoned or its
owner has released it. Recover an interrupted capture using the same process
checks as an interrupted build. Investigate a failed check, repair its cause and
queue changed inputs through the same procedure.

Record each check's actual result and release the output reservation when those
checks and any running processes using it finish. When a revision is superseded,
cancelled or externally blocked, release the outputs it no longer needs once
its processes finish; retain its historical evidence. If a newer revision will
use that same output for a remaining check, transfer the reservation explicitly.
Recover an abandoned reservation only after establishing that neither a live
owner, scheduled continuation nor a running process still needs it. Other
requesters sharing the output retain their reservations. Complete the request
only when its required evidence covers the intended revision, or report a
concrete external blocker and the exact unfinished work. A compiler success alone
is not a reason to stop before the behavior checks the task needs. When only a
future result is missing, apply the continuation rule and its single-use limit.
Preserve ownership and next actions through an interruption so the next turn
resumes the request rather than starting over.

## Verify the build before launch

Before any authorized app inspection, capture or measurement, identify the successful build
record covering the intended request revision. Match its configuration,
architecture, capture setting and output directory to the launcher's actual
resolved paths and options, including `-ArtifactsPath` and `-Configuration`.
Verify the runtime output hashes against that record before launching. A normal
build does not refresh the separate capture output; file existence, timestamps
and a successful build in another directory cannot establish coverage.

If the record is missing, recover provenance only from evidence that establishes
the source checkpoint, build properties and output contents. Otherwise queue the
required build. A mismatch is a pre-launch failure, not permission to run whatever
binary exists or to bypass the interval with another build.

Include the build record and resolved output path in capture evidence. Check
that the requested journeys actually ran before interpreting screenshots or
timings. Missing journeys or obsolete behavior invalidate claims about the new
code. Old results qualify as a before-baseline only when their source,
configuration and workload are established and comparable.

This is an agent preflight requirement. The current capture launcher checks that
its output exists; it does not enforce build-record or hash matching. Perform
the check explicitly until the launcher implements it.

## Output check

Check output containment once per build/test batch. Repeat after project,
copy-step or test-output changes, or when unexpected output appears. From the
repository root this must print nothing:

```powershell
& "C:\Program Files\Everything\es.exe" -path $PWD /ad "wfn:bin|wfn:obj|wfn:bin-fl|wfn:TestResults" "!*\artifacts\*" "!*\3rdParty\*"
```

Generated output belongs in `artifacts/`. The precompiled `3rdParty/` tree is
excluded because its own tools contain such folders and rebuilding dependencies
costs over an hour. Investigate any result's producing path; deleting it without
fixing that path only hides the defect. [Testing](testing.md) owns which runtime
checks earn their cost and how their evidence is reported.
