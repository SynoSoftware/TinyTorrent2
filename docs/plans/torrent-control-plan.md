# Torrent control

Design plan, 2026-10-27, revised after the
[architecture audit](torrent-control-audit.md) and a two-stage plan review. No
code has changed for it. The [engine contract](../contracts/engine.md#state-and-work)
and its [files reference](../contracts/engine/files.md) own current behavior;
each step updates them where it changes behavior.

The plan corrects how the engine decides, for each torrent, whether the torrent
may run, what the engine does about a storage failure, and which work on its
files is in flight. It keeps the libtorrent fork and the storage behavior that
already works. Exit's unfinished file work is part of this plan
([step 5](#step-5-unfinished-file-work-e)); the rest of Exit stays with
[lifetime](../contracts/engine/lifetime.md), and [Exit](#28-exit) says where
Exit reaches the controller.

## Owner decisions, 2026-10-27

This plan is a proposal. No step in it is authorized. Architecture is
validated before it is implemented, so the work runs in this order:

1. **Independent audit.** Done: [torrent-control-audit.md](torrent-control-audit.md).
   Its verdict is that the design is sound with local defects. Three problems
   reach beyond one function, and each has one owner to fix: the installed
   libtorrent is behind the code that depends on it, there is no exception
   boundary, and one drive can still make other drives wait.
2. **Invariants and executable tests first.** The audit's
   [transition tests](torrent-control-audit.md#8-transition-tests) run on
   today's build before any step changes code, so each step's effect is
   measured. See [Baseline](#step-b-baseline).
3. **A focused correction.** The design is corrected where the audit and the
   tests show it fails. Working code is kept. `Control::Decide` is directionally
   sound but unproven; it takes only the decisions whose defects the evidence
   shows, and its transition check proves it.

[Step 6](#step-6-disk-work-per-drive-in-the-fork-h), disk work per drive in
the fork, needs separate authorization. It addresses drive isolation inside
libtorrent, a problem distinct from torrent state correctness.

## Summary

- **Target.** Each torrent has one storage controller, `Control`. Its data
  changes only in `Control::Decide`, which takes an event and returns the
  effects. One executor in `Engine::State` runs the effects and calls
  `ApplyIntent` once when the control state changed. The person's intent stays
  in `Facts` and libtorrent's state stays in `status`; `Decide` writes neither.
- **Storage failures** become one failure episode per torrent. The episode
  keeps its cause and its retry level until it ends, and it ends in the same
  event that removes its cause, or when the person answers. This fixes N1, N2,
  R-c, review 1, and the lost Retry during a probe (D1).
- **Kept as they are:** the guards that drop late results (handle equality,
  the `Recovery` identity, `FindWorking`), the 36 `ApplyIntent` calls, and
  `IsHeld` on `Torrent`. The guards drop late results correctly, but a result
  they drop does not end its job, so the handle swap ends it (2.5).
- **Steps.**
  - P: the fork's work is committed, pinned and rebuilt (owner).
  - B: the audit's checks run on today's build.
  - 0: a torrent without a handle, and an exception boundary.
  - 1: probe and notice rules.
  - 2: the failure episode.
  - 3: path work.
  - 4: file work per drive in the engine.
  - 5: unfinished file work with one owner, which takes over the open
    defects of the Exit work.
  - 6: disk work per drive in the fork (separate authorization).

  Each step ships alone.
- **No estimate.** The earlier 52-hour total is withdrawn. Steps 0 to 4 are
  smaller than the earlier steps 0 to 5, because the tickets, the routing of
  every hold through one function and the move of `IsHeld` are gone, and they
  add the audit's missing fixes.
- **Exit is no longer patched on its own** (owner, 2026-10-27). Its open
  defects are in unfinished file work, which step 5 gives one owner.

## 1. Audit

The [audit](torrent-control-audit.md) is the evidence for the current code: its
threads and workers, findings C1 to C6, invariants, the fork, and the storage
review's findings. This section keeps the per-torrent facts the steps change,
and the defects they fix.

### 1.1 Per-torrent state and its writers

`struct Torrent` holds the control state below. Writers are named by
function. "Kind" says what the field is: the person's or a policy's choice
(intent), what libtorrent or the disk reports (reality), or what the engine
decided (control).

| Field | Kind | Writers |
|---|---|---|
| `facts.intent` | intent, saved | `SetIntent` (also for seeding limits), `AbandonTasks` (an abandoned check leaves the torrent stopped), `Extract` (in memory only, to make `ApplyIntent` pause a removed torrent) |
| `waitingMove`, `needsRecheck`, `prioritiesPending` | intent that waits | `Execute`, `StartMove`, `StartWaitingMove`, `FinishDownload`, `ContinueTasks`; `Verify`, `Recheck`, `ApplyWaiting`, `FinishNames`; `CommitEdit` |
| `status` | reality | `Update`, `ReplaceHandle`, `Install` |
| `diskError`, `diskClearedAt` | reality: the last `file_error_alert` newer than the last clear | `On(file_error_alert)`, `ReplaceHandle`, `ClearDiskError` |
| `facts.marker` | reality: where a move left the files, saved | `StartMove`, `ContinueMove`, `SaveMove`, `FailMove`, `AbandonMove`, `AbandonMoves`, `Unfinished` |
| `recovery` (`phase`, `released`, `cause`, `retryRequested`, `absent`, `lost`) | control: the storage job and part of the failure | `RecoverStorage`, `RetryStorage`, `StopStorage`, `ContinueStorage`, `Reload`, `ReplaceHandle`; `retryRequested` by `ClearDiskError` |
| `storageRetry` | control: retry level | `RetryStorage`, `StopStorage`, `ContinueStorage`, `CheckStopped`, `AnswerStorageProblem` |
| `retriedProgress` | control: progress at the last continue | `ContinueStorage`, `Reload`, `Torrent::Update` |
| `checkError` | control: a stalled check's cause | `StopStalledJobs`, `ContinueStorage`, `ReplaceHandle`, `AnswerStorageProblem` |
| `moving` | control: work that changes file paths | `StartMove`, `ContinueMoves`, `EndMove` (move); `FinishFiles`, `EndRename` (suffix rename); `AbandonMove` and its completion (giving back) |
| `moveError`, `moveRetry` | control: a failed move and its retry level; `moveError` also holds a waiting move's missing-metadata refusal | `Execute`, `StartMove`, `RefuseMove`, `EndMove`, `FinishDownload`, `CheckStopped`, `AssignDrives`, `AnswerMoveProblem`; `moveRetry`: `EndMove`, `CheckStopped`, `AnswerMoveProblem` |
| `conflict` | alias conflict hold | `Restore`, `On(torrent_conflict_alert)`, `Torrent::Retry` |
| `restore` | no libtorrent handle installed | `Restore`, `Reload` (handle removed), `ReloadMove` |
| `namePhase`, `nameError` | names owner | `ContinueNames`, `FailNames`, `FinishNames`, `RecoverNames`, `QueryFiles`, `CommitAddition`, `SaveMove`, `Install`; `nameError`: `FailNames`, `FinishNames`, `EndMove` |
| `checkTurn`, `checkReads` | drive turns | `AssignDrives`, `StopStalledJobs`, `ReplaceHandle`; `checkReads` also `On(torrent_paused_alert)`, `RetryStorage` |
| `unfinished` | unfinished tasks owner | `Unfinished`, `Answer`, `Unask`, `AbandonTasks`, `AbandonMoves`, `AbandonMove` |
| `cacheWrite` | completion owner | `FinishFiles`, `On(cache_flushed_alert)` |
| `renameAt`, `renaming`, `marking`, `completedFiles` | suffix rename and mark | `Files.cpp`; `renameAt` also `ContinueStorage`, `RetryStorage`, `Reload`, `EndMove` |

For a torrent in the list, `Torrent::ApplyIntent` is the only engine code that
pauses or resumes it; a restore or reload adds it paused first. It pauses when
`IsHeld` is true, which ORs ten holds of six owners: `recovery`, `conflict`,
`moving`, `facts.marker`, `CacheWrite::Flushing`, `CheckWaits`, `checkError`,
`StorageStopped`, `unfinished == TaskKind::Check`, and names not ready.
`ApplyIntent` has 36 callers. The audit and the review checked every writer of
a hold input: the only one that skips the call is `StopStorage`, which is
harmless because `StorageStopped()` keeps the torrent held. Step 2 removes it.

Engine-level state also controls torrents: `moves`, the global `rename` gate,
`deletions`, `abandonments`, `turns`, `drives`, the per-root workers in
`volumes`, the shared `payload` worker, `stoppedChecks`, `storageCursor`,
`stoppedCheckAt`, `removed`, `handles` and `shutdownPhase`. Torrents share it,
so a per-torrent controller cannot own it. The controller receives its
outcomes.

### 1.2 Event sources

- **Person:** Start, Stop, Force (`SetIntent`), Verify, Retry, Move, Remove,
  file choices, the answer to unfinished work, Pause all.
- **Policy:** seeding limits (`LimitSeeds` sets the intent to stopped),
  schedules and adapter rules (session pause).
- **libtorrent alerts that write control inputs:** `file_error_alert`,
  `state_update_alert`, `state_changed_alert`, `torrent_paused_alert`,
  `torrent_checked_alert`, `cache_flushed_alert`, `file_completed_alert`,
  `file_renamed_alert`, `file_rename_failed_alert`, `file_prio_alert`,
  `storage_moved_alert`, `storage_moved_failed_alert`,
  `save_resume_data_alert`, `save_resume_data_failed_alert`,
  `torrent_removed_alert`, `torrent_deleted_alert`,
  `torrent_delete_failed_alert`, `torrent_conflict_alert`,
  `alerts_dropped_alert`.
- **Worker completions:** the release future from `ReleaseStorage`, volume
  worker jobs (probes, the minute check, names, rename and mark, drive
  lookups), the payload worker (`Reload`, `QueryFiles`, deletions, give-backs),
  move and abandonment workers, document commits.
- **Timers in `Maintain`:** probes once a second, the minute check, the
  2-minute stall, the 30-second rename retry, checkpoints, and the 2-minute
  notice delay in `UpdateProblems`.
- **Lifecycle:** `Start` (restore, unfinished tasks) and Exit.

### 1.3 Decisions with two owners

1. **Is the torrent stopped by storage?** `StorageStopped`, `NeedsPerson` and
   the reset at the top of `CheckStopped` each answer it from different
   fields.
2. **The retry ladder** has two implementations: `storageRetry` in
   `RetryStorage`, `StopStorage`, `ContinueStorage` and `CheckStopped`, and
   `moveRetry` in `EndMove` and `CheckStopped`.
3. **The shown storage problem** comes from six fields in `StorageProblem`:
   `checkError`, `nameError`, `status.errc`, `diskError`, `recovery->cause`
   and upload mode.
4. **Path work.** `moving` has three writers for three kinds of work. A
   comment, not the type, keeps them apart.
5. **A Retry during recovery** is stored in the job record
   (`recovery->retryRequested`) and read only by `RetryStorage`, so a Retry
   during a probe is lost (D1).
6. **Replacing a handle** keeps the `Torrent` object in `ReplaceHandle`, but
   rebuilds it in `Restore` through `Install`.
7. **A check that a disk error stopped** has two owners: the engine reloads
   it from resume data (`ContinueStorage`), and the fork can continue it in
   place on `clear_error`. Step 2 keeps the engine's reload as the one owner,
   as engine.md requires.
8. **Busy files.** `Torrent::FilesBusy` is per torrent; the global `rename`
   and `DeletingOrRenaming` gate every torrent (#541), and `NamesReady` holds
   every final-name rename while any torrent's names are unknown.

### 1.4 Combinations the data allows and the rules forbid

- `storageRetry` at Stopped or Person with no storage problem: a stop without
  a cause. `StorageStopped` and `CheckStopped` work around it (N1, N2).
- A stop whose cause lives only in `recovery->cause`, which `StopStorage`
  deletes (N2).
- `moving` true for a move and a suffix rename at once. Only a comment forbids
  it.
- `moveRetry == StorageRetry::Retried`. Only a comment forbids it.
- `moveRetry` from the final-folder move together with the `moveError` of a
  later move the person asked for (review 1).
- A torrent without a handle (`restore`) that keeps the removed handle and the
  state bound to it (review 2).
- A torrent in `moves` or `abandonments` with `moving` false (review 3).
- A reloaded torrent rebuilt without its retry level, progress, move state or
  history (R-c).
- A `Usable` result that is false: the folder may not answer, or it may lack
  room, and the minute check cannot tell which (B7).

### 1.5 Known defects, verified in the code

Defects from the independent state-model review keep its numbers (review 1, 2,
3 and 6). C1 to C6 are the [audit's](torrent-control-audit.md#3-concurrency-findings).
B and D numbers come from this plan's review. A1 and A2 come from a second
read-only audit of the same code on 2026-10-27.

**N1. A new failure inherits the old stop.**
1. A torrent stops at `StorageRetry::Stopped` or `Person`.
2. Other work removes its cause: `FinishNames` resets `nameError`, or a
   move's reload resets `checkError` in `ReplaceHandle`. `StorageStopped()`
   turns false, the torrent runs, and `storageRetry` keeps the old level until
   `CheckStopped` resets it, up to one minute later.
3. A new failure in that minute: `On(file_error_alert)` sets `diskError` and
   calls `RecoverStorage`, which returns at once because `StorageStopped()` is
   true again. For a write refused for space or permission, libtorrent only
   sets upload mode, so the torrent still runs in libtorrent while `IsHeld`
   says held.

The new failure gets no release and no probe, and it loses its automatic
retry.

**N2. A stop loses a cause that only the job held.** `ContinueRename` and
`On(file_rename_failed_alert)` call `RecoverStorage(torrent, Unreachable())`,
so the cause lives only in `recovery->cause`. When the ladder stops the
torrent, `StopStorage` resets `recovery` and the cause with it.
`StorageProblem()` is then empty: the row shows no error, `NeedsPerson` is
false, `CheckStopped` sets `Automatic` again, and `FinishFiles` tries the
rename every 30 seconds. The ladder starts over each time and never reaches
Person.

**N5. A minute check touches the source from the destination's worker.** For
a failed move to the final folder, `CheckStopped` runs `Payload::HasRoom` on
`VolumeWorker(finalFolder)`. `HasRoom` reads each source file, in the
torrent's current folder. When that folder is on a share that hangs, every job
of the destination's worker waits.

**R-a. Two rules decide whether a move copies.** `Placement::Place` copies only
on `ERROR_NOT_SAME_DEVICE`. `HasRoom` compares mount-point strings from
`GetVolumePathNameW`. A volume reached through two mount points makes
`HasRoom` count the full size of a move that `Place` renames, so the
final-folder move stays stopped with disk full.

**R-b. The engine and the fork disagree on a link that does not answer**
(storage review finding 3). `AnsweringFolder` opens the folder, which follows
a link, but on `ERROR_PATH_NOT_FOUND` it walks to the parent, and `C:\`
answers. `LostData` then counts every file as lost, and `Reload` drops every
piece. The fork's uncommitted `check_storage_root` stops at a link that exists
but cannot be followed; the installed binary checks only the root, so a check
at start ends at 0%.

**B5. The probe can drop every piece** (the gap in storage review finding 3).
The probe job calls `Reachable`, then `LostData`. A share that drops between
the two calls makes `LostData` report every file missing, and `Reload` clears
their pieces. The files are still on the share.

**R-c. A failed reload consumes the retry, loops, or rebuilds the torrent.**
`ContinueStorage` advances `storageRetry` before it calls `Reload`.
- When the reload fails before it removes the handle, the next probe judges
  the failure at the advanced level: the torrent stops although it never ran,
  or `ContinueStorage` calls `Reload` again once a second.
- When `add_torrent` fails after `remove_torrent`, `RestorePending` adds the
  torrent again through `Install`, which replaces the whole `Torrent`: the
  retry level, progress, move error, unfinished work and speed history are
  lost.

**B1 and C6. A share that hangs never gets its notice.** `UpdateProblems`
shows the location notice only when `recovery->absent` is set, which only a
returned probe sets. On a share that hangs, the release fence or the probe's
`CreateFileW` blocks, `NeedsPerson` is false while `recovery` exists, and no
notice appears until Windows returns. A rename or name preparation that hangs
on a share has no recovery at all, so it shows nothing either.

**B7. A Stopped torrent whose location disappears stays Stopped.**
`CheckStopped`'s completion skips a result that is not usable, so the level is
not reset to Automatic, which engine.md requires, and the torrent stays in the
Retry notice instead of the location notice (storage review finding 8).

**D1. A Retry or Verify during a probe is lost.** `ClearDiskError` only sets
`retryRequested`, which the probe's completion never reads. With an error that
`Retryable` rejects, the completion calls `StopStorage(Person)`, which resets
`recovery` and the flag, and the Retry button shows again. A Verify in the
same window leaves `needsRecheck` waiting for a Retry the person already gave.

**C2. No exception boundary.** `Store::Drain` and `Tick` have no `try`, and
`Restore` throws on an add error. One throw drops the rest of the batch, and
`Main`'s catch ends the engine without the Exit save.

**A1. Every reload opens a window in which the old handle throws.** `Reload`
runs `ReloadStorage` on the `payload` worker, which calls `remove_torrent` and
then `add_torrent`. `torrent.handle` changes only when the engine thread drains
the completion. Between the two, libtorrent can destroy the removed torrent,
and then any engine call on the old handle throws `invalid_torrent_handle`:
`CheckpointUnsaved` calls `Torrent::Checkpoint` (`save_resume_data`) for a
torrent that is `unsaved` and not `moving`, and `QueryCompletions` can post a
status query. `ReloadMove` has the same window through `QueryCompletions`. So
C2's crash does not need a failed re-add; a successful reload is enough.

**A2. A minute check can act on the folder the torrent left.** The
`CheckStopped` completion checks the handle, `recovery` and `FilesBusy`, but
not that `facts.savePath` still names the folder it checked, as `SameFolder`
does for the probe in `RetryStorage`. A Move of a Stopped torrent keeps the
handle, so the old folder's answer continues the torrent at the new folder and
uses its last automatic continue. The audit's table of late results marks this
completion as sound.

**C3 and C5. One drive's file work holds up other drives.** Deletions,
`GiveBackRemoved`, `Reload` and `QueryFiles` all run on the one `payload`
worker. `FinishFiles` waits while `NamesReady()` is false, which is true while
any torrent's names are unknown. A deletion on a share that hangs therefore
holds the reloads and file queries of every drive, and through `NamesReady`
every final-name rename. The global `rename` and `DeletingOrRenaming` gates do
the same for names and renames (#541).

**C1 and H.** The installed libtorrent is 39e37b746 (C1), and libtorrent runs
every torrent's disk jobs on one shared set of threads (H). The audit and the
earlier trace in [step 6](#step-6-disk-work-per-drive-in-the-fork-h) give
the detail.

**E. Unfinished file work is derived at save time, not recorded when it
starts.** `Tasks()` rebuilds the list of unfinished work from memory at each
save: the moves, `returning`, the markers of removed torrents, `deletions`,
`removed` and the turns, and Exit freezes that list in `exitTasks`. The marks
of the web are a second list (`unmarked_files`). The second Exit review found
these failures, each a consequence of that design:

1. Mark jobs do not stop at Exit, and Exit saves again whenever the marks
   still being written differ from its last save. A backlog of marks on a slow
   share keeps Exit saving until the 10-second end.
2. `GiveBackRemoved` runs on `payload`, so a destination share that hangs at
   the next start holds every drive's names, reloads and deletions (C3).
3. A removal with "Delete files" during a move whose destination is gone: the
   give-back fails, nothing tries it again, and `ContinueDeletion` holds the
   deletion at every start without telling the person.
4. `Tasks()` saves the give-back of a removed torrent's move only when
   `NamesReady()`. Exit while any torrent's names are unknown leaves the files
   split between two folders with no task.
5. A move that Exit stops after libtorrent took the new folder makes the next
   start clear `have_pieces` and verify the moved files again. No data is
   lost.
6. `CommitFacts` builds the document before it applies the Abandon, so one
   save can hold both the Move task and the Returning marker. The next start
   then continues the move the person abandoned.
7. A give-back that Exit stops after its renames loses the check that follows
   renaming files back.
8. An older engine reads the new Returning stage as a move to continue.

**F. Defects from the second audit** (a 13-agent read-only audit of HEAD
9027e74, confirmed by reading the code; F1, F4 and F7 rechecked here):

1. A dropped `file_error_alert` starts recovery for every torrent with
   metadata, healthy ones included. The ladder then stops an idle seed, and
   `StopStorage` calls no `ApplyIntent`, so the torrent stays paused and shows
   Queued (step 2).
2. A delete-files removal whose commit fails during Exit deletes the files of
   torrents still listed at the next start (step 5).
3. A stopped check whose reads hang is no longer timed, so another drive's
   check is blamed as `DriveStalled` (step 4).
4. `ReplaceHandle` does not reset `cacheWrite`, so a flush pending at a reload
   holds the torrent until restart (step 0).
5. A Move to a new folder overwrites the marker of a cut move, so the files
   in the earlier destination are no longer recorded (step 3).
6. A Remove that runs after Exit starts saves no deletion task (step 5).
7. Automatic work answers stop problems: a seed limit through `SetIntent`, and
   "verify finished downloads" through `Verify` (step 2).
8. Abandon releases its hold before its save lands, so the abandoned move can
   start again (step 3).
9. Continue overwrites the person's later Move (step 3).
10. A torrent removed during a move that is then cut gets no give-back
    (step 5).
11. A give-back that throws lets the deletion run and reports nothing
    (step 5).
12. A copy's second stall is never interrupted (step 5).
13. A cleared error stays on the row until the next status sample (step 2).

**Review 1. `moveRetry` outlives the move failure it belongs to.** The
person's Move resets `moveError` but not `moveRetry`. So:
1. The move to the final folder F fails: `moveRetry` is Stopped.
2. The person moves the torrent to D, and that move fails.
3. `CheckStopped` still tests F, clears the person's error without any
   action from the person, and sets Resumed.
4. `FinishDownload` then queues a move to F.

`moveError` also holds a waiting move's missing-metadata refusal, which four
readers must each tell apart from a failure.

**Review 2. A torrent without a handle can throw in `FinishFiles`.** The
failure branches of `Reload` and `ReloadMove` turn a loaded torrent into a
restore torrent in place, keeping the removed handle and the state bound to
it. `FinishFiles` has no `restore` guard; with `cacheWrite` Pending it calls
`handle.flush_cache()`, which throws `invalid_torrent_handle`, and nothing
catches it.

**Review 3. Path work is set late and ended by the wrong owner.** `moving`
has three writers. `FinishFiles` ignores `abandonments` and the torrent's own
`moving`, so a suffix rename can start while the torrent's files are given
back, and whichever finishes first sets `moving` false while the other still
works.

**Review 6. A waiting abandonment asks the person again.** `AbandonTasks`
sets `unfinished = TaskKind::Move` after the person answered Abandon, so the
row asks the person to choose although nothing is left to choose.

### 1.6 libtorrent behavior the design depends on

Traced in the fork's source. "Installed" means the 39e37b746 build the engine
links today.

- `handle_disk_error` posts `file_error_alert`. A write that fails for
  permission or space only sets upload mode; every other error calls
  `set_error` and `pause()`.
- `should_check_files` requires the torrent not to be paused and not to have
  an error. Pausing therefore holds a check.
- `force_recheck` does nothing while a check runs or while the state is
  `checking_resume_data`.
- In the fork source, `clear_error` runs a failed resume-data check, a failed
  recheck setup, or a seed's pending recheck again. This is not installed.
- `async_release_files` is a per-storage fence; its callback ends the
  release. It runs on the shared generic pool (H).
- `status()` and `get_resume_data()` wait for the network thread, which does
  no file I/O; `torrent_file()` does not wait.
- The alert queue can drop any alert; `alerts_dropped_alert` starts the
  reconciliation the contract describes.

## 2. Target design

### 2.1 Intent, reality and control

| | Holds | Written by | `Decide` |
|---|---|---|---|
| Intent | `facts.intent`, file choices, final folder, waiting move, pending Verify, the answer to unfinished work | person commands, policy, document commits | does not read it: `ApplyIntent` combines it with the holds |
| Reality | `status`, `diskError`, `facts.marker`, probe results | `Update`, alert handlers, worker completions | reads it through events and a `Reality` snapshot |
| Control | the failure episode, the final-folder move's episode, the storage job | `Control::Decide` only | owns it |

Other owners keep their state and their rules: path work, names, drive turns,
unfinished tasks, completion, alias conflicts. `Torrent::IsHeld` stays the
aggregate of every owner's hold; `Control` supplies its part: an episode at
Recovering, Stopped or Person, or a job in flight.

### 2.2 The failure episode

An episode starts with a storage failure. It ends when the torrent gets
further, when the person answers with Retry, Start, Force or Verify, or when
other work removes its cause. "Gets further" keeps its meaning from the
[engine contract](../contracts/engine.md#persistence-and-file-safety): more
data, more upload when finished, or a check that reads further.

| Stage | Cause shown | Torrent | Job | Leaves when |
|---|---|---|---|---|
| Recovering | yes | held | release, then a probe each second, then a reload when files were lost or a check was stopped | the probe answers: continue, Stopped or Person by the ladder |
| Stopped | yes | held | the minute check | the check passes: continue at `Resumed`; the location does not answer: Recovering at Automatic |
| Person | yes | held | none | the person answers |
| Continued | no | runs by its intent | none | it gets further (the episode ends), or the failure returns (Recovering at the same level) |

The ladder is the existing rule: one immediate automatic continue, then a stop
with the Retry button and the minute check, then one more continue when the
minute check finds the cause gone, then the person. A final-folder move uses
the same rule and enters at Stopped. One function implements it for both.

Rules the stages need:

- **A location that does not answer resets the level.** At Recovering, a probe
  that finds the folder absent keeps the episode at Automatic. At Stopped, a
  minute check that finds the folder absent returns the episode to Recovering
  at Automatic, so the location notice replaces the Retry row (B7).
- **A stopped check reloads.** A probe that answers for a torrent whose check
  a disk error stopped reloads it from resume data, as today and as engine.md
  requires. The engine's reload is the one owner of that continue; the fork's
  rerun on `clear_error` serves only a failed resume-data check.
- **Progress after a reload.** While libtorrent checks resume data, `Reality`
  has no progress. `Decide` records the first known progress after a reload as
  the episode's progress. A recorded zero would make any later failure count
  as "got further" and reload again (D2).
- **The same failure at Stopped or Person changes nothing.** The status branch
  reports the error each second; it must not re-enter Recovering (D4).
- **Two episodes on one torrent.** The minute check runs the check each
  episode asks for. Today the move branch hides the storage branch (D3).
- **The person's answer ends the episode at once,** even while a job runs.
  The job's result then finds no episode: a release clears the error, a probe
  does nothing (D1).

The final-folder move has its own episode in `Control`, in place of
`moveRetry`. It ends when that move succeeds, when the person answers, and
when any other move of the torrent starts (review 1). `moveError` keeps only
the last move's failure. The missing-metadata refusal moves to `WaitingMove`,
and `Error()` reads it there, so the row and the pipe do not change.

```cpp
// A storage failure, from its first error until the torrent gets further,
// other work removes its cause, or the person answers.
struct Failure
{
    StorageRetry level = StorageRetry::Automatic;
    // What failed last. At Retried or Resumed the torrent runs and the
    // stage hides it; it shows again if the failure returns.
    Problem cause;
    bool retryable = true;
    // Where the torrent was when the engine continued it. Empty until the
    // first progress after a reload is known.
    std::optional<FileProgress> progress;
    bool absent = false;
};
```

`recovery->cause`, `absent`, `storageRetry`, `retriedProgress` and
`checkError` go into `Failure`. `nameError` stays with the names owner.
`diskError` and `status.errc` stay reality: every error they show opens or
updates the episode through an event. `StorageStopped` goes, because a stop
always has its cause.

### 2.3 Decide

```cpp
class Control
{
public:
    // Changes the control state for one event and returns what the engine
    // must do. Reads no clock, handle or disk.
    Effects Decide(Event const& event, Reality const& reality);
    ...
private:
    std::optional<Failure> failure_;
    std::optional<Failure> finalMove_;
    std::optional<Job> job_;
};
```

`Completion` is the precedent: a class in `Torrent.h` whose private data only
its own transitions change. `Control` sits beside it; its implementation is
`engine/src/Control.cpp`.

**Events** (one `std::variant` of named types):

- `Failed{Problem, retryable}`: a disk error, a status error, a stalled
  check, a names failure, or a rename that found its folder not answering or
  failed with an error that waiting cannot fix.
- `Released`, `Probed{answered, absent, lost files}`,
  `Checked{usable, absent, no room}`, `Reloaded{done, failed, or failed after
  removal}`: job results.
- `Answered`: Retry, Start, Force or Verify.
- `Cleared{Problem}`: other work removed this cause, such as names chosen at
  last.
- `Replaced`: a move installed a new handle. The episode ends; an error the
  new handle shows opens a new episode.
- `Dropped`: the handle changed without a new folder, such as the reload of
  a move or an add that failed. It ends the release, probe or minute check
  that ran on the old handle, and the episode stays.
- `Due{minute}`: the timer, once a second for torrents with an episode.
- `MoveStarted` and `MoveEnded{failure}`, each saying whether the move goes
  to the final folder.

**Reality** is the snapshot `Decide` reads: progress (empty during the
resume-data check), whether every error libtorrent reports now is retryable,
and whether libtorrent checks files.

**Effects:** the job to start (release, probe, minute check, or reload with
its lost files), and whether to clear libtorrent's error and upload mode.
`Decide` has no resume effect.

`FileProgress::Of` stays with the executor, which builds `Reality`;
`FileProgress::Beyond` moves into `Control.cpp`, so the transition check
compiles `Control.cpp` without other engine sources.

### 2.4 The executor

The executor is the existing storage code in `Storage.cpp`, minus its
decisions:

- `Maintain` sends `Due` to each torrent with an episode. The probes that
  `Decide` asks for are grouped per folder into one volume worker job, as
  `RetryStorage` groups them today.
- `ReleaseStorage`, `Reload`, `ReloadStorage` and `ReplaceHandle` run jobs;
  they decide nothing.
- Alert handlers turn alerts into events. `On(file_error_alert)` keeps its
  check against `diskClearedAt`.
- When `Decide` changed the control state, the executor calls `ApplyIntent`
  once and invalidates the row.
- The cadence (one second, one minute, the 2-minute notice) stays in the
  executor and in `UpdateProblems`.

### 2.5 Jobs and late results

Each failure has at most one job: the storage failure its release, probe,
own-folder check or reload, and the final-folder move's failure its
final-folder check. So a final folder whose share hangs never holds the check
of the torrent's own folder. The job record outlives the failure, so the
person can end a failure while its job runs.

- **One handle swap.** One function replaces the handle: it takes the old
  handle and its replacement (a new handle, or the parameters and error of a
  torrent libtorrent did not add), resets the state bound to the old handle,
  installs the replacement, and only then sends `Dropped`. No event runs while
  a torrent has neither a handle nor its parameters. `Dropped` ends the jobs
  that ran on the old handle, also for a removed torrent, because a removed
  torrent's control refuses only the events that start jobs.

- **A job's identity is its record.** `job_` is a `std::shared_ptr<Job>`, and
  each completion captures it and compares by pointer, as completions compare
  the `Recovery` pointer today. The other guards stay: handle equality and
  `FindWorking`. A result whose record is gone, or whose handle was replaced,
  changes nothing. No counter is added; the audit and the review found no
  late result that these guards miss.
- **A job without an episode.** When the person ends the episode while a
  release or reload runs, the job stays until its result. Until then, a
  `Failed` that reports the error the job's completion clears changes nothing,
  so the status branch, `QueryCompletions` and a replayed `file_error_alert`
  cannot open a new episode that the old job's result would then advance.
  Today `!torrent.recovery` gives this; the executor's guards read "no job" in
  its place.
- **How a job ends.** A probe and a minute check end when their volume worker
  returns. A release ends when libtorrent's fence returns. A reload ends when
  `ReloadStorage` returns. On a share that hangs, each of these lasts as long
  as Windows waits. The episode does not wait for them to show its state: the
  location notice comes from the 2-minute delay (step 1), and Exit ends
  without them ([lifetime](../contracts/engine/lifetime.md)).
- **A Retry during a release** ends the episode; the release completes and
  clears the error. `retryRequested` goes.

### 2.6 Invariants

Each invariant has one enforcer.

1. A torrent runs only when its intent wants it and no hold applies.
   Enforcer: `ApplyIntent`, called by the executor after every control change.
2. A stop (Stopped or Person) always has its cause. Enforcer: the type,
   because `Failure::cause` is not optional.
3. Only the end of an episode, or a location that does not answer, lowers the
   level. Enforcer: `Decide`.
4. An episode continues the torrent automatically at most twice: once at once,
   once after the minute check. A failed reload counts as a continue whose
   failure returned. Enforcer: `Decide`.
5. Each failure has at most one job, every job ends, and a result whose job
   record is gone changes nothing. Enforcer: each failure's job record, the
   existing guards, and the one handle swap, which ends the jobs of the old
   handle.
6. Saved progress never drops data the disk still holds. Enforcer: the probe,
   which asks the folder again when files look lost (step 1).
7. `Decide` has no resume effect, so no automatic event resumes a torrent the
   person stopped. Enforcer: the `Effects` type.
8. A torrent that waits for a location shows that wait once a probe of its
   root has not returned for 2 minutes. With a probe once a minute, that is 2
   to 3 minutes after the root stops answering.
   Enforcer: the per-root probe and `UpdateProblems` (step 1).
9. A change to control state applies the running state in the same engine
   turn. Enforcer: the executor.
10. At most one kind of path work runs per torrent. Enforcer: `PathWork`
    (step 3).
11. The final-folder move's episode ends when any other move of the torrent
    starts. Enforcer: `Decide`.
12. A torrent without a handle keeps no state bound to the removed handle,
    and no event or handle call reaches a torrent that has neither a handle
    nor its parameters. Enforcer: the one handle swap (2.5).
13. One handler's failure never drops other torrents' work. Enforcer: the
    boundary in `Store::Drain` and `Tick` (step 0).
14. The engine's file work on one root never waits for another root.
    Enforcer: per-root workers and gates (step 4). Inside libtorrent the same
    rule needs step 6.
15. A move, give-back or deletion is named by a saved record from before it
    changes a file until it ends, and the record says how to continue it. Exit
    adds no record. A move keeps its two records: the marker is the authority
    for one torrent's files, the task for the group and its turn, and
    `Unfinished` reconciles them at start, as today. Enforcer: the
    unfinished-work owner (step 5).
16. At Exit, every file job stops before its next file. Enforcer: the stop
    check in each job's file loop (step 5).

### 2.7 Today's predicates

| Today | Target |
|---|---|
| `ApplyIntent` | Unchanged, with its callers. The executor adds one call after each control change. |
| `IsHeld` | Stays on `Torrent`. Reads `Control`'s hold instead of `recovery`, `checkError` and `StorageStopped`, and `PathWork` instead of `moving`. |
| `Classify`, `ClassifyTransfer` | Unchanged; read `PathWork` and the episode. |
| `Error`, `Diagnose` | Unchanged order; read the episode's cause where they read `checkError` and `recovery->cause`. |
| `StorageProblem` | The episode's cause, then `nameError` and reality, in today's order. |
| `NeedsPerson` | The storage episode or the final-folder move's episode is Stopped or Person. A move the person asked for no longer counts. |
| `moveRetry` | Removed: the final-folder move's episode. |
| `MoveBlocked` and the refusal tests | Read the waiting move's refusal; `moveError` is always a failure. |
| `StorageStopped` and the reset in `CheckStopped` | Removed. |
| `ClearDiskError` | Becomes the executor's clear effect, without `retryRequested`. |
| `FilesBusy` | Reads `job_` instead of polling the release future. |
| `RecoverStorage`, `StopStorage`, `ContinueStorage` | Their decisions move into `Decide`; what remains runs jobs. |

**What Stopped shows.** At Stopped the row shows the Retry button and the
torrent counts in the stopped notice, while the minute check can still
continue it. This is the owner's "retry once automatically, then show a Retry
button". pipe-api.md's description of `retry` ("true while the torrent waits
for the person's Retry") changes to say that it is true from the first stop.

### 2.8 Exit

Exit is not an event of `Decide`. The controller's part is that `Decide`
starts no job once shutdown begins, which the executor's existing
`IsShuttingDown()` checks already give. Exit's unfinished file work belongs to
[step 5](#step-5-unfinished-file-work-e).
After step 0, a reload's completion checks `IsShuttingDown()` before it
removes the handle, so a reload either replaces the handle in one engine turn
or does not start the replacement. [Lifetime](../contracts/engine/lifetime.md)
owns everything else about Exit.

### 2.9 Outside the controller

Persistence, detail queries, speed history, the completion owner, names,
drive turns, unfinished tasks, alias conflicts, path work, and the
multi-torrent owners (moves, the rename and deletion gates, abandonments) keep
their rules.

## 3. Fork review

Branch `Synapse/TinyTorrent`, described in `3rdParty/libtorrent/FORK.rst`.

| Change | Verdict | Reason |
|---|---|---|
| Pause bookkeeping in `set_paused`, resume and graceful pause | Keep | Only libtorrent can keep its tick and scrape bookkeeping consistent. |
| Known peers in `peer_info_alert` | Keep | The Peers inspector needs it; unrelated to control. |
| `check_storage_root`: a missing file under a save path that does not answer becomes `ERROR_DEV_NOT_EXIST`, with the link rule | Keep | Only libtorrent sees the error at the moment it would count a file as empty. The engine adopts its link rule (R-b). |
| pread `initialize` fails, and `set_file_priority` reports, on a save path that does not answer | Keep | Inside storage. |
| `check_fastresume` without resume data reports `fatal_disk_error` | Keep | Without it, existing files download again. |
| `move_storage` with `reset_save_path` succeeds when the destination does not answer | Keep | The engine's additions and moves set the save path this way. |
| A failed resume-data check or recheck setup runs again on `clear_error`; `write_resume_data` keeps unchecked resume data | Keep | Only libtorrent owns the check state. |
| `on_counted_piece_unwritten` recounts file progress; a seed rechecks on clear | Keep | The picker and progress are libtorrent's. |
| A written piece that cannot be read back is cleared and downloaded again | Keep | Inside the hash pipeline. |
| A stopped check keeps its position at the lowest piece in flight | Keep | Required by "a check never restarts from zero". |

Nothing moves to the engine and nothing is dropped.

**Build status (C1).** The engine links 39e37b746 (`3rdParty/libtorrent.pin`).
The fork's work is committed as 21f44eaa9, and `engine/src/Dependencies.ps1`
pins it. None of the fork's later fixes ship until the owner's rebuild in
[step P](#step-p-pin-and-rebuild).

## 4. Migration steps

**Before step 0,** the uncommitted storage and Exit work in the working tree is
committed, because the steps edit the same files. The Exit work is committed
with its known defects (E): it removes Exit's waits on drives, and step 5
fixes the rest.

No step changes the pipe format or product text, except the pipe-api.md
sentence in 2.7. Only step 5 changes saved data; its format number stays 1, and
data saved by earlier versions still loads. Each step updates the engine contract where behavior
changes.

### Step P. Pin and rebuild

The fork's working-tree changes are committed and pushed;
`Dependencies.ps1` and `libtorrent.pin` name that commit; the owner rebuilds
the dependencies once. Agents never run `Dependencies.ps1`.

Fixes C1. Every runtime proof that depends on fork behavior waits for it: T5,
T10, T11, the fork half of T3, and the manual checks. It is needed before step
1's runtime proof, not only before step 6.

### Step B. Baseline

The audit's pipe checks run once on today's build, before step 0 changes code,
and their results are recorded in the audit's section 8. No code changes.
Each later step names the checks it turns green and the checks that must stay
green.

Done on 2026-10-27 for the engine built at 9027e74's source:
- `StorageStop` (T1, T7), `AbsentNotice` (T4, absent drive) and `KilledCheck`
  (T16) pass.
- `AbsentLink` (T3) and `StoppedLocation` (T8) fail, as the audit predicts.
- `StoppedHeld` (T6) and `MagnetAbsent` (T11) need `-Transfer` and wait for the
  owner.
- T2 has no check: no fixture shares a downloaded piece between a skipped file
  and a wanted one.

### Step 0. A torrent without a handle, and the boundary (review 2, C2, A1)

- `FinishFiles` returns for a torrent without a handle (`restore`), as
  `FinishDownload` and `ApplyWaiting` do.
- One function resets the state bound to a handle, `cacheWrite` included
  (F4). `ReplaceHandle` and the failure branches of `Reload` and `ReloadMove`
  call it.
- `Store::Drain` catches an exception per completion, `Tick` per alert, and
  `Maintain` per torrent in its loop; each logs it and continues with the
  next. The torrent whose step threw keeps its state; the others keep their
  work. This is the product rule that one failed item never hides the others.
- `RestorePending` treats a failed add as that torrent's failure instead of
  throwing: the torrent keeps `restore` and shows the error, and Retry tries
  the add again. This is the path R-c reaches when the add after a reload
  fails again.
- A1: the engine thread, which owns `torrent.handle`, removes and adds the
  torrent itself, so no engine call can see a removed handle. The reload's
  worker keeps only the work that waits on a drive: the resume data, the
  dropped pieces and the release fence. Its completion, on the engine thread,
  checks `IsShuttingDown()`, then calls `remove_torrent`, `add_torrent` and
  `ReplaceHandle` in one step, as `Restore` already adds torrents on the
  engine thread. `ReloadStorage`'s second fence goes: the paused torrent
  issues no disk job after the worker's fence, and the engine thread never
  waits on a drive. `ReloadMove` follows the same split. Exit's rule "a reload
  that has not replaced its handle stops" becomes that `IsShuttingDown()`
  check. A rule that every handle caller skips a reloading torrent would need
  about 80 call sites to remember it.

Enforces invariants 12 and 13. Proof: source review, because the crash and a
throwing completion need a failed re-add or A1's short window, and no check
can aim at either. T12 joins
the transition check if a seam for it appears in step 2.

### Step 1. Probe and notice rules (R-b, B5, R-a, N5, B1, C6, storage review 9)

- `Reachable` calls the fork's exported `lt::aux::check_storage_root` with a
  missing-file error, which returns `ERROR_DEV_NOT_EXIST` when the folder does
  not answer. So the engine and libtorrent share one implementation of the
  link rule: a folder that exists but cannot be opened does not answer, and
  the engine does not walk past it to its parent. The installed 39e37b746
  checks only the root, so this rule reaches the product with step P.
- When `LostData` finds lost files, the probe asks the folder again in the
  same job. If it no longer answers, the result is "absent", not "lost".
- `HasRoom` compares volume names (roots for network volumes) instead of
  mount-point strings.
- `CheckStopped` gathers a move's source facts on the source root's worker,
  then checks room on the destination's worker.
- The location notice no longer needs a returned probe, and no longer needs
  an engine job on the location. Each root that holds a running torrent or
  file work gets a probe once a minute on a thread of its own, outside the
  volume worker, so a long job never delays it. The probe opens the root
  folder. A probe that has not returned after 2 minutes means the root does
  not answer. `UpdateProblems` then shows the location notice for that root,
  counting the torrents whose save folder is under it, with
  `download_location_unavailable`. A recovery whose probe found the folder
  absent shows it through the same 2-minute delay, as today. So a share that
  hangs gets its notice, whether the hang is in a libtorrent transfer, a
  release, a rename or a name preparation. A slow job on a root that answers
  never shows one. Three rules keep the probe bounded:
  - **Which roots:** the roots of listed torrents that are not paused by the
    person and not at Stopped or Person, the roots of file-work records
    (step 5), and every root whose last probe has not returned. The last
    group keeps a hung root in the set after its torrents become held.
  - **One probe per root at a time.** A root whose probe has not returned
    gets no second probe, so a hung root never fills its queue.
  - **Exit.** The probe threads join Exit's list of busy workers in the
    Ending phase, so a hung probe leads to the end without teardown, not to a
    wait in the destructor.
- The stopped popup replaces its actions and selection, not only its text,
  when its count changes.

Enforces invariants 6 and 8. Checks: T3 (after P for the fork half), T4 for an
absent drive; T6 and T7 stay green. T3b and the hung-share half of T4 are
manual (M1, M6). Storage review 9 is checked by review.

### Step 2. The failure episode (N1, N2, R-c, review 1, B7, B6, D1 to D4, A2, F1, F7, F13, #543, #550, #552, #554)

- F1: the reconciliation after dropped alerts re-reads each torrent's status
  and opens an episode only for a torrent whose status shows an error; a
  release without a failure is not a `Failed` and moves no ladder.
- F7: only the person's commands send `Answered`. A seed limit and "verify
  finished downloads" do not answer a stop or a failed move.
- F13: the clear effect also clears the cached error and upload mode that
  `StorageProblem` reads, so the row does not show a cleared error.

- Add `Control` and `Failure` to `Torrent.h`, with the implementation in
  `engine/src/Control.cpp`. `Torrent` holds a `Control`.
- Move the ladder decisions of `RetryStorage`, `StopStorage`,
  `ContinueStorage`, `CheckStopped` and `EndMove` into `Decide`, with the
  rules in 2.2.
- Review 1: the final-folder move's episode replaces `moveRetry`. The commit
  of every other move sends the event that ends it. The missing-metadata
  refusal moves from `moveError` to `WaitingMove`.
- `recovery->cause`, `absent`, `storageRetry`, `retriedProgress` and
  `checkError` move into `Failure`. `FailNames`, `StopStalledJobs`,
  `ContinueRename`, `On(file_rename_failed_alert)`, `On(file_error_alert)` and
  the status branch send `Failed`. `FinishNames` and `EndMove` send `Cleared`.
  `AnswerStorageProblem`, `Torrent::Retry` and `Verify` send `Answered`.
- A rename sends `Failed` when its folder does not answer, as today, and
  when waiting cannot fix its error, such as a final name that already exists.
  The row then shows the error and Retry, instead of a rename every 30
  seconds. A file that another program has open keeps the quiet retry every
  30 seconds without an error, as files.md says. `FinishFiles` starts no
  rename while the episode holds the torrent (#543).
- `Recheck` clears `needsRecheck` when it sends the recheck, so a waiting
  recheck never sends a second one that restarts the check from zero (#552).
- Each answer (Retry, Start, Force, Verify) has one per-torrent rule for
  whether it can act, used both by the row's offer and by the command. A
  command acts on every torrent it can and reports the ones it refused, so one
  torrent never blocks the others, as in "Retry all". Retry cannot act on an
  alias conflict while the other identity is still listed, so it is not
  offered then. Today Retry clears `conflict` without this rule, and the
  conflict comes straight back (#550). Verify cannot act on a torrent without
  metadata.
- `UpdateProblems` keeps `notifiedError` while a storage problem shows. Today
  the storage problem replaces it, so when the storage problem clears, an
  earlier toast, such as Move interrupted, shows again (#554).
- R-c: the level advances only when the torrent continues. A re-add after a
  removed handle installs the new handle into the existing `Torrent`, as
  `ReplaceHandle` does.
- A2: a change of save folder drops a running minute check, as `Replaced`
  drops a probe, so its result changes nothing.
- The transition check (5.2) is written with this step: its sequences come
  from engine.md's rules and are written before `Decide`'s body.

Enforces invariants 1 to 5, 7, 9 and 11. Checks: T8 turns green; T6, T7 and
the baseline's passing checks stay green; the transition check's sequences and
invariant run, which take over T3b, T14 and T15. `DiskError` once, if the owner
allows it.

### Step 3. Path work (review 3, review 6, F5, F8, F9, #553)

- `PathWork { Move, Rename, Abandon }` on `Torrent` replaces the bool
  `moving`. A torrent takes it when it joins `moves` (before the commit),
  `abandonments` or the suffix rename, and only that owner ends it.
  `FinishFiles` checks the torrent's own path work.
- `AbandonTasks` no longer sets `unfinished`, and neither does `Unfinished`
  for a Returning marker at start. A waiting abandonment holds
  `PathWork::Abandon`, so its row shows Moving and nothing asks again.
- Abandon takes `PathWork::Abandon` before its save, not after, so no move can
  start in between (F8).
- The person's own Move replaces an asked move task: a Move before Continue
  wins over Continue (F9).
- A Move of a torrent whose earlier move was cut first gives back the files at
  the earlier destination, then moves to the new folder. So no file stays in a
  folder that no marker records (F5).
- `FinishFiles` removes a `completedFiles` entry whose name is no longer
  unfinished, instead of skipping it. A dropped rename alert leaves such an entry, and
  `FinishDownload` then waits until the next start (#553).

Enforces invariant 10. Checks: `UnfinishedTasks`, `MoveAndVerify`, and
`RemoveDuringMove` if the owner allows it.

### Step 4. File work per drive in the engine (#541, C3, C5)

- The global `rename` and `DeletingOrRenaming` become one gate per root,
  keyed like `VolumeWorker`: a drive, or a server for UNC paths. A server that
  hangs usually hangs all its shares, because Windows keeps one connection per
  server, so a finer key would only add gates. A rename, deletion or move
  holds names and renames only on the roots it touches; a move touches two.
  Notices stay per share (`NoticeRoot`), because the person fixes a share.
- Deletions, `GiveBackRemoved`, `Reload`, `QueryFiles`, the rename's
  Observing job and `RecoverAdditions` run on the worker of the root they
  touch, not on `payload`.
- `FinishFiles` waits only for the names of torrents whose files the rename
  can reach, not for every torrent's names. The `additions.empty()` gates in
  `FinishFiles`, `CanRemove` and `RestorePending` become per root in the same
  way.
- A check whose reads are still in flight counts for `StalledCheck` until its
  reads end, even after it was stopped or removed. So a hung check is timed
  and named itself, and another drive's check is not blamed as `DriveStalled`
  (F3).

Enforces invariant 14 for the engine's own queues. This is engine work inside
the owner's rule that a share that hangs never holds up other drives, so it
needs no separate authorization. Checks: `FileNames`, `DriveTurns`,
`RemoveDuringMove` if allowed; T13 and M2 (engine half) manually after P.

### Step 5. Unfinished file work (E, F2, F6, F10 to F12, #551)

One owner, the unfinished-tasks code in `Tasks.cpp`, keeps the records of file
work: moves, give-backs and deletions. Their owners create and end their
records through it. Today it rebuilds these from memory at each save, and Exit
freezes the list in `exitTasks`; after this step, it keeps the records it was
given.

Two kinds of task stay as they are:

- **Checks, waiting moves and pending Verifies** are not file work in
  progress. They follow from each torrent's saved state and its turn, so
  `Tasks()` keeps deriving them, and the `ask` answer keeps counting them.
- **Marks of the web** stay saved with other saves, as engine.md says, because
  a whole-document save before each finished file would delay that file's
  final name. At Exit, mark jobs stop before their next file like other work.
  Exit saves twice, each once: the first save stays where it is, at the start
  of Exit, so a kill loses no task; the second comes after the torrents are
  paused and that turn's alerts are handled, when no completion can start a
  mark. A file that completes between
  the two joins `waitingMarks` instead of starting a mark, and the second
  save records it. So no mark is lost, and Exit no longer saves once per
  finished mark (E1). A mark recorded and also written before the end is
  written again at the next start, which changes nothing.

The rules for file-work records:

- **A record is saved before its work changes a file.** It holds what the next
  start needs: the kind, the torrents, the roots, the files, and for a move
  the folder libtorrent uses (E5).
- **A record holds only what is known when it is saved.** The give-back of a
  torrent removed during its move is saved at the removal, with the torrent's
  own paths, the move's destination root and the files the move found. It does
  not wait for `NamesReady()`. The files that other torrents keep are computed
  when the give-back runs, after their names are known (E4).
- **A give-back's record says the torrent needs a check from the moment it is
  saved,** for any move past Staged. A give-back that Exit stops after its
  renames therefore still leads to the check (E7).
- **A change of plan replaces the record in the same save.** Abandon replaces
  the move's record with a give-back's record. One save never holds both
  (E6).
- **The record ends in the save that records the work's end.**
- **Exit adds no record.** It stops each job before its next file, and its
  one save marks the saved tasks as cut, as `!exitTasks` does today for
  `tasks_continued`.
- **Every job runs on the worker of the root it touches** (E2, with step 4).
- **A record whose root does not answer waits visibly.** Its root joins the
  per-root probe of step 1, whether or not a listed torrent uses it, so the
  root's location notice shows the wait. The record runs again when its root
  answers. A deletion that waits for a give-back follows the same rule (E3).

**Saved data.** The format number stays 1, because `Document::Read` refuses
any other number and a raised number would make an older engine refuse the
whole store. A give-back's marker is saved as `move_stage` 2 with a separate
field that says it returns. The new engine reads that field, and also reads
the stage 3 that 9027e74 saves, as a give-back. An older engine reads stage 2
as a failed move and waits for the person instead of continuing the move
toward the destination (E8).

This step replaces `exitTasks` and the second mark save at Exit. lifetime.md
and files.md change from "Exit saves the tasks" to "the tasks are already
saved".

The same rules close the second audit's file-work defects:

- **F2, F6.** A deletion's record is saved in the commit that removes its
  torrents, and only that commit. A removal whose commit fails leaves no
  deletion record; a removal that runs after Exit starts still saves its
  record, because Exit no longer freezes the list.
- **F10, F11.** A removed torrent's give-back record outlives the removal and
  every failure of its job: a cut move or a failed give-back keeps the record
  and waits visibly, and a deletion that waits for it does not run before it.
- **F12.** Every stall of a copy is interrupted, including a stall of the
  undo that followed the first one.
- **#551.** A `torrent_delete_failed_alert` without an error ends the
  torrent's wait without a toast. libtorrent posts it for a torrent without
  storage, such as a magnet without metadata, which has no files to delete.

Enforces invariants 15 and 16. Checks: `UnfinishedTasks`, `RemoveDuringMove`
if allowed, `KilledCheck` (T16), and M8, which includes a removal during a move
while another torrent's names are unknown, and a give-back stopped after its
renames.

### Step 6. Disk work per drive in the fork (H)

**Separate authorization.** This is the earlier plan's step 6, unchanged in
content.

libtorrent's `pread_disk_io` runs every torrent's disk jobs on one generic
pool and one hash pool, flushes every storage's cache and closes files on the
same threads, and applies one write-cache limit to all torrents. On a share
that hangs, ten blocked jobs hold all ten generic threads, and then no torrent
on any drive reads, writes, releases, renames or moves.

**Fork,** in `pread_disk_io`, `disk_io_thread_pool`, `disk_cache` and
`file_pool`:

- Each storage records its root, computed as `check_storage_root` computes
  it, and records it again when its save path changes.
- Each root has its own generic pool and hash pool, created on its first job.
  `aio_threads` and `hashing_threads` apply to each root. Idle pools end their
  threads after 60 seconds, as today.
- `pool_for_job` picks the job's root. A job without a storage uses a default
  pool. `move_storage` runs on its source root's pool.
- A root's threads flush only that root's cache blocks and fenced storages,
  and run only that root's storage ticks and file closes.
- The write cache applies back pressure per root.
- `FORK.rst` describes the change.

**Engine,** in `Turns.cpp`: a stalled check holds only the checks on its own
root. The files reference drops "A read that never returns stops every
check".

**Options that do not meet the rule:** the engine cannot bound libtorrent's
blocking jobs per drive; more threads move the limit but do not remove it;
cancelling a blocked read depends on the SMB redirector, which is not
established.

**Risk.** This changes libtorrent's disk scheduling, where the fork's own
comments record earlier fence and flush deadlocks. The step runs libtorrent's
disk tests (`test_disk_io`, `test_fence`, `test_disk_cache`, `test_storage`),
then a second dependency rebuild, then M2.

Enforces invariant 14 inside libtorrent. Checks: libtorrent's disk tests and
M2.

## 5. Testing

### 5.1 The audit's checks

The [audit's section 8](torrent-control-audit.md#8-transition-tests) lists
T1 to T16. They run as named checks in `engine/tests/Checks.ps1` where a pipe
check can produce the fault, as manual steps where only real hardware can, and
in the transition check where only a decision-level test is deterministic
(T3b, T14, T15). The audit's section 8 maps each T number to its check name
and records its result; step B fills in the results on today's build. T9 is
reviewed, not tested.

libtorrent's own tests (T5, T10, and the disk tests of step 6) need a build in
`3rdParty/`, which agents never make. The owner builds and runs them with the
dependency rebuild.

### 5.2 The transition check

`engine/tests/Transitions.vcxproj` builds a console program that feeds event
sequences to `Control::Decide` and asserts the results. It follows
`Transfer.vcxproj`, compiles only `Precompiled.cpp`, `../src/Control.cpp` and
`Transitions.cpp`, uses no framework and no clock, and runs in seconds.

It earns its place under [testing](../agents/testing.md#what-earns-a-test):

- **What it watches:** a torrent that retries forever, stops without its
  automatic retry, stops with no cause and no Retry, ignores the person's
  answer, or starts work after the person answered.
- **Why nothing else catches it:** the pipe checks cannot order a share
  disconnect, a release callback and a probe result deterministically, and the
  overlaps depend on that order.
- **Production keeps its shape:** `Decide` exists because the owner asked for
  a central transition function, not for the test.

Each case asserts the literal stage, level and effects that the rule predicts.

| # | Sequence | Expected | Guards |
|---|---|---|---|
| 1 | `Failed` (retryable), `Released`, `Probed` (answered) | clear; Continued at Retried | the automatic retry |
| 2 | 1, then `Failed` with no progress, `Released`, `Probed` (answered) | Stopped; cause kept; held | no loop |
| 3 | 2, `Checked` (usable), then `Failed` with no progress, probe answers | Person; held | the ladder ends |
| 4 | 2, `Cleared` (its cause), `Failed` (disk) | Recovering at Automatic; release starts | N1 |
| 5 | `Failed` (rename found its folder not answering) up to Stopped | cause kept; held | N2 |
| 6 | `Failed`, `Released`, `Probed` (absent) | Recovering at Automatic; absent; held | waiting share |
| 7 | 6, `Probed` (answered) | clear; Retried | share returns |
| 8 | `Probed` (answered, lost files), `Reloaded` (failed) | Stopped; no reload before `Checked` | R-c loop |
| 9 | `Probed` (answered, lost files), `Reloaded` (failed after removal) | Stopped; the episode kept | R-c rebuild |
| 10 | release running, `Answered`, `Released` | clear; no probe; no episode | Retry during a release |
| 11 | probe running, `Answered`, then that `Probed` (answered, error not retryable) | no episode; no stop; no reload | D1 |
| 12 | probe running, `Replaced`, then that `Probed` | the episode ends; the late probe changes nothing | handle replaced by a move |
| 13 | Verify (`Answered`) while a reload runs, then `Reloaded` (done) | busy until the result; then clear | Verify, recheck |
| 14 | `Failed` (stalled check), `Released`, `Probed` (answered) | reload from resume data; Continued | B6 |
| 15 | `Failed` again during a release, as after dropped alerts | cause updated; no second release | dropped alerts |
| 16 | final-folder move fails, `Checked` (usable), fails again | Stopped, Resumed, Person | move ladder |
| 17 | Start (`Answered`) at Person | clear; no episode | Start |
| 18 | final-folder move fails, the person's Move starts and fails, minute check due | no final-folder episode; no minute check | review 1 |
| 19 | 2, `Checked` (absent) | Recovering at Automatic; absent | B7 |
| 20 | `Reloaded` (done), `Due` with no progress, then progress 40%, `Failed`, `Probed` (answered, lost files) | progress recorded at 40%; no second reload without further progress | D2 |
| 21 | 2, `Failed` with the same cause each second | Stopped; no job | D4 |
| 22 | storage episode and final-folder episode both Stopped, `Due{minute}` | both checks requested | D3 |
| 23 | release running, `Answered`, `Failed` (the same error), `Released` | clear; no episode; no probe | a job without an episode |

A Stop during recovery sends no event to `Decide`; the intent changes, and
`ApplyIntent` keeps the torrent stopped (invariant 7).

### 5.3 Invariant run

One case runs every sequence of up to five events from a fixed set of about
18 event variants, plus a late result. After each event it checks invariants
2 to 5 and 7. That is about two million `Decide` calls, which take seconds.

### 5.4 Manual checks with a real share

These need the owner's hardware: a network share that can be disconnected,
made to hang, and reconnected. M1, M3 to M8 run once after step P and the
step they prove; M2 runs after step 4 for the engine and again after step 6.

| # | Do | Expect |
|---|---|---|
| M1 | Disconnect the share during a download; reconnect after 3 minutes. Repeat with the share made to hang | The error shows; the location notice appears after 2 minutes in both cases; the torrent resumes by itself and the notice disappears. |
| M2 | Make the share hang while a torrent seeds from it, during a download, a rename, a deletion, a check, and the minute check of a failed final-folder move from it | Torrents on other drives keep their names, renames, deletions, reloads and minute checks (after step 4) and their transfers and checks (after step 6). |
| M3 | Delete one file on the share while it is away; reconnect | Only that file downloads again; no full check. |
| M4 | Fill the download disk; free space after the stop | One automatic retry, then Stopped with Retry; the minute check continues it. |
| M5 | Verify a torrent on the share; disconnect mid-check; reconnect | The check continues from its position, not from 0%. Stop during the check cancels it. |
| M6 | Save into a folder link to a removable drive; remove the drive | The torrent waits with the location notice, not Stopped; no progress is lost; it resumes when the drive returns. |
| M7 | Fail a move to the final folder on the share | It waits, runs once more when the share answers with room, then waits for Retry. |
| M8 | Choose Exit during: a move of a removed torrent to the share, the same while another torrent's names are unknown, a give-back after Abandon and after its renames, a Recycle Bin deletion, and a backlog of finished files on the share; start again, once with the share answering and once without | Each Exit ends in about 2 seconds. The next start continues each piece of work from where it stopped; work on the absent share waits with its location notice and runs when the share returns. |

## 6. Decisions for the owner

**Step 6: changing libtorrent's disk scheduling.** It is the only change found
that keeps a hung share from stopping transfers and checks on other drives.
The risk is a deadlock in the fork's disk code, which libtorrent's disk tests
and M2 must rule out. It needs a second dependency rebuild. Recommended: do it
after steps 0 to 5.

**Transfer checks.** `DiskError` downloads real payload, so it runs only when
the owner asks. It is the one automated proof that the executor recovers a
real failed write, a held seed file and a failed Verify. Recommended: once,
after step 2.

**What a waiting abandonment shows.** After review 6 it shows Moving, as a
move does. A separate "Giving back" status would add a status and its text in
every language. Recommended: Moving.

Decided:

- **Exit's file work** (owner, 2026-10-27): Exit is not patched on its own.
  Its open defects (E) go to step 5, so one design owns unfinished file work.

Defaults, not open questions:

- Step P is the owner's action and comes first; it takes over an hour of the
  owner's machine.
- A failed reload counts as a continue whose failure returned.
- When two failures overlap, the episode shows the newest, as today.
- At Stopped the row shows Retry, as 2.7 says.

## 7. Where the evidence departs from the direction

- **One authority for all state changes.** The controller is the only writer
  of the storage episode, the final-folder move's episode and the storage job.
  Path work, names, drive turns, unfinished tasks, completion and alias
  conflicts keep their owners, and `IsHeld` stays their aggregate. A
  controller that owned them would own the whole engine, and the audit found
  no defect in their ownership that one controller would fix.
- **Routing every hold through one function.** Not done. The audit found one
  writer that skips `ApplyIntent`, and step 2 removes it; the executor's own
  call covers every control change.
- **Systematic transition testing.** [Testing](../agents/testing.md) allows
  high-return tests only. The plan pins the invariants once over generated
  sequences, and names only the sequences that guard a defect or a contract
  rule.

## 8. Separate work

These findings are verified in the code but are not part of torrent control,
or cost only clarity.

| Finding | Where | Why separate |
|---|---|---|
| Review 2, full fix: a `Torrent` that is either loaded or waiting to be added again | `Torrent.h` and every reader of `handle` | It changes about 80 uses of `handle`; step 0 already removes the crash. |
| Review 5: a failed watch folder is reported once, ever | `Watching.cpp` | Watch folders are not torrent control. |
| Review 7: `checkpointPhase` mixes the request with the write; `ReplaceHandle` can end a newer write early | `Checkpoints.cpp` | Persistence, outside the controller. |
| Review 8: `saveFailure` uses an empty string for "unknown cause" | `Shutdown.cpp` | Exit's code. |
| Reviews 9 to 14: schedule period fields, `ConnectionTest::outcome`, `Toasts` failure fields, `CacheWrite::Written`, `AdditionPhase::Adding`, `conflict` as an empty string | policy, connection test, toasts, suffix rename, additions, conflicts | Clarity only. |
| R1: `torrent_paused_alert` can arrive while a stopped check's reads are in flight | drive turns | No data path fails; the release fence still waits for the reads. |
