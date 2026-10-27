# Torrent control: architectural audit

Audit of 2026-10-27. I only read code. Nothing was built, run or changed.

- **Engine:** the working tree of `engine/`, including the uncommitted storage
  work. Exit is outside this audit, except where torrent control depends on it.
- **Fork:** `3rdParty/libtorrent`, branch `Synapse/TinyTorrent`, at HEAD
  49f7fa319 plus its uncommitted changes.
- **Installed libraries:** `3rdParty/Release` and `3rdParty/Debug`, built
  2026-10-26 at 12:50 and 12:58.

This audit does not repeat the state-model review (findings 1 to 7 and its
clarity items) or the diff reviews of the storage work. It cites them where they
matter. Section 7 checks the separate storage review of the committed work
(`storage-review.md`, nine findings) against the current code.

## 1. Verdict

**Sound, with local defects.** The engine's core rules are right and must be
kept:

- One thread owns all state.
- Workers return values. Their completions run on the engine thread.
- Pause and resume come from one function, `Torrent::ApplyIntent`.
- Drive claims are derived on each pass, never stored.
- A late result is dropped by handle, recovery and generation guards.
- Outcomes are observed after a libtorrent fence, not taken from droppable
  alerts.
- Saved state is written atomically, in order, by one writer.

Nothing found requires a different architecture. The defects are local, with
three exceptions that are wider than one function, but each has a single owner
to fix:

1. **The installed libtorrent is three revisions behind the code that depends
   on it.** The engine and the contracts rely on fork behavior that the running
   binary does not contain (section 5).
2. **No exception boundary.** One exception in any completion or alert handler
   drops the rest of that batch and ends the engine without the Exit save
   (finding C2).
3. **One drive still makes other drives wait.** libtorrent's shared disk pools
   (the plan's H), the shared `payload` worker, and the global file gates (the
   plan's #541) all do this.

## 2. Threads and workers

| Thread | What runs there | State it writes | libtorrent calls | Can it wait on a drive? |
|---|---|---|---|---|
| Engine thread (the message loop in `Application`) | `State::Tick`: drain completions, `pop_alerts` and `Handle`, `Maintain`, pipe requests | Every `Torrent` field and all engine state | Asynchronous handle calls. Synchronous ones wait only for the network thread: `session->add_torrent` in `Start` and `RestorePending`, `handle.flags()` in Exit | **No.** Every `ReleaseFiles` and every file call runs on a worker. `RetryStorage` polls the release future with `wait_for(0)` |
| Pipe threads (8 `Serve` threads, one `Deliver` writer per connection) | Read and write frames | The request queue (a mutex, at most 128), then `PostMessage` | None | No |
| `store` | `settings.json`, the document, resume files, and the verify proof in `Checkpoints` | None; it returns values | `handle.status(query_pieces)` | Only the application data drive |
| `payload` (one for all drives) | `QueryFiles`, the rename's recovery observation, `Reload`, `RecoverAdditions`, `Delete`, `DeleteTorrentFiles`, `GiveBackRemoved` | None | `status`, `file_progress`, `get_resume_data`, `get_renamed_files`, `remove_torrent`, `add_torrent`, and fences through `ReleaseFiles` | **Yes, on any drive.** See C3 |
| `volumes[root]` (one per drive root or UNC share, from `VolumeWorker`) | Drive lookups, probes (`Reachable`, `LostData`), the minute check (`Usable`, `HasRoom`, `LostData`), name preparation, `PrepareAddition`, the rename's Moving phase, the mark | None | `status`, `file_progress`, `get_file_priorities`, `get_renamed_files`, and fences | Its own root. Also any drive, through the shared libtorrent pools (C4) and through `HasRoom`'s source reads (the plan's N5) |
| One `Store` per move and per abandonment | Preflight, copy, `Payload::Move` and `Reconcile`, `ReadPiece`, the observation, `ReloadMove` | `Move::progress` atomics (`cancel`, `placing`, `parked`, progress counts) | `ReleaseFiles`, `ReadPiece` through the disk thread, `status`, `get_resume_data`, `remove_torrent`, `add_torrent` | Source and destination |
| `sources`, `checks` | Previews and the watch folder; proxy checks and the connection test | None | Previews only | Their own folders |
| libtorrent network thread | Session, torrents, alerts | libtorrent state | Not applicable | **No** in the paths I traced. `pread_storage`'s destructor flushes the part file and closes handles, but `on_torrent_aborted` runs it only after the `stop_torrent` fence already released the files on a disk thread |
| libtorrent generic disk pool (`aio_threads`, default 10; the engine does not set it) | Reads, writes, every fence job, cache flushes, each storage's `tick` | Disk cache | Not applicable | **Yes. One pool for every drive** (the plan's H) |
| libtorrent hash pool (`hashing_threads`, set to `hashingThreads` × running checks in `Policy`) | Check reads, and a download's hash read-back when the cache cannot hash the piece | Not applicable | Not applicable | **Yes. One pool for every check** |

**Late and reordered completions.** Each completion has a guard:

| Completion | Guard | Status |
|---|---|---|
| Release future from `RecoverStorage` | Polled through the `Recovery` it belongs to; `ReplaceHandle` resets that object | Sound |
| Probe in `RetryStorage` | Same handle, same `Recovery` object, and `SameFolder` against the current save folder | Sound |
| Minute check in `CheckStopped` | Same handle, no recovery, not `FilesBusy`, retry level still Stopped; `stoppedChecks` allows one job per folder | **Not sound:** no check that `facts.savePath` still names the folder it checked, unlike `RetryStorage`'s `SameFolder`. A Move of a Stopped torrent keeps the handle, so the old folder's answer continues the torrent at the new folder (plan A2) |
| `Reload` | Same handle and same `Recovery` object | Sound |
| Name preparation | `namePhase == Preparing`. That phase makes `FilesBusy` true, so no reload or move replaces the handle meanwhile | Sound |
| `QueryFiles` | Same handle and same `generation` | Sound |
| Alerts of a replaced handle | `ReplaceHandle` erases the handle from `handles`, so `Find` drops them. A `file_error_alert` stamped before `diskClearedAt` is ignored | Sound (see R2) |
| Checkpoints | `pendingCheckpoint` keeps the newest request; `ReplaceHandle` resets the checkpoint state | Sound |
| `torrent_paused_alert` taken as "the check's reads ended" | None | **Wrong after a check error** (R1) |

## 3. Concurrency findings

### Confirmed defects

**C1. The installed libtorrent lacks the storage behavior the engine relies on.**
Section 5 gives the evidence. These failures follow in today's binary:

- **A file is renamed complete while a piece is missing.** In
  `torrent::piece_passed`, `we_have` counts the piece in `m_file_progress`
  before it is written. `piece_picker::write_failed` corrects the picker but
  not the file progress. `on_counted_piece_unwritten` (3490467a2) fixes this,
  but it is not in the binary: its name has 0 hits in both libraries. The
  rename's Moving phase judges completeness from
  `file_progress(piece_granularity)` after the release fence. That count still
  includes the unwritten piece, so the check passes and `MoveFileExW` gives the
  file its final name. With a share that drops before the flush, the person
  can open a file with a hole while libtorrent downloads that piece again.
- **A magnet addition to an absent share fails.** The preview handle calls
  `move_storage(savePath, reset_save_path)`. In the installed revision this
  fails when the destination does not answer. `On(storage_moved_failed_alert)`
  then abandons the addition with `AddFailed`. The contract says that such an
  addition joins the list and waits. The fix (49f7fa319) is not installed.
- **A check after a disk error starts again from piece 0**, because the
  position-keeping change (49f7fa319) is not installed.
- **A seed's pending recheck does not run when its error clears.**
  `m_recheck_on_clear` (3490467a2) is not installed.

**C2. One exception ends the engine or loses the rest of its pass.**
`Store::Drain` swaps out the whole batch of completions and runs them in a loop
with no `try`. `Tick` and the window procedure have no `try` either.
`Application::Run` sits inside `Main`'s `catch (std::exception)`, which reports
a start failure and exits. So one throw has two effects:

- It ends the engine without the Exit save. The cost is the progress since the
  last checkpoint.
- If Windows discards the exception at the window procedure instead, the rest
  of that batch is lost. I did not run this case.

A lost completion leaves state stuck with no cause that ends it:

- A probe completion: `phase` stays `Probing`, and `RetryStorage` never probes
  that torrent again.
- A minute check: the folder stays in `stoppedChecks`, so it is never checked
  again.
- `QueryFiles`: `queryingFiles` stays true.

The state-model review's finding 2 is one concrete throw:
`flush_cache` on a removed handle in `FinishFiles` throws
`invalid_torrent_handle`. Any `async_call` on an invalid handle throws the same
way, and so does a `Working(id)` or `.at()` whose entry is gone.

**C3. The `payload` worker serves every drive.** `Delete` (in `Erase`) and
`GiveBackRemoved` (in `GiveBack`) do file I/O on the torrent's own drive, on
the same worker as `Reload`, `QueryFiles` and `RecoverAdditions` for every
other drive. Here is the failure:

1. The person removes a torrent with its files, on a share that hangs. `Erase`
   waits until Windows returns.
2. A torrent on `C:` loses its files, and its recovery calls `Reload`. The
   reload waits behind the deletion.
3. A new torrent receives its metadata. Its `QueryFiles` waits too, so its
   `names` stay unknown.
4. `NamesReady()` is false while any torrent's names are unknown, so
   `FinishFiles` holds the final-name rename of every torrent on every drive.

**C4. libtorrent's disk pools are shared by all drives.** This is the plan's H,
and my trace agrees with it. It also reaches the engine's per-drive workers:
`ReleaseFiles` is a fence job, and it needs a generic disk thread. When the
jobs of a share that hangs occupy all 10 threads, `VolumeWorker(C:)` waits in
`ReleaseFiles` for name preparation, renames and moves on `C:`.

The hash pool has `hashingThreads` × running checks threads, so it grows per
check. It is still one first-in, first-out queue, and a check that stalled
holds as many threads as it has reads in flight. A download whose piece must be
read back for its hash uses the same pool.

**C5. Global gates make every torrent wait for any file work.**

- `ContinueNames` and `PrepareAddition` wait while any deletion or the single
  `rename` runs.
- `RestorePending` also waits for any move.
- `FinishFiles` waits for any move, deletion, rename or addition, for a busy
  document writer, and for every torrent's names (`NamesReady`).

The plan lists this as #541. The plan's text says that names wait for moves.
They do not: only `FinishFiles` and `RestorePending` do.

**C6. A share that hangs causes waits with no timer and no notice.** Only check
reads and a move's copy have a 2-minute stall timer (`StopStalledJobs`). These
waits run as long as Windows does:

- the release fence;
- the probe's `CreateFileW`;
- the rename's Moving phase (the row shows Moving);
- name preparation;
- `Reload`;
- the `ReleaseFiles` before a move places its files (the row shows Moving 0%).

The location notice needs `recovery->absent`, which only a returned probe sets.
`NeedsPerson` is false while a recovery exists. So no notice appears until
Windows returns (section 7, finding 4).

### Risks, not shown to fail

- **R1. `torrent_paused_alert` arrives before a stopped check's reads end.**
  After a hash failure, `on_piece_hashed` sets `m_checking_piece` and
  `m_num_checked_pieces` to the same piece and pauses. `do_pause` in
  `checking_files` posts the alert at once when they are equal. Hash reads of
  the old pass are still in flight. `On(torrent_paused_alert)` then sets
  `checkReads = None`, so `DrivesInUse` frees the drive early, and another
  check on that drive can start while the old reads still hang. The recovery's
  release fence waits for those reads, which limits the overlap.
- **R2.** A `file_error_alert` that libtorrent stamps just before
  `ClearDiskError` sets `diskClearedAt` is ignored. One that arrives just after
  starts an extra recovery and can move the ladder toward Person. This is
  rare.
- **R3.** A `Store` refuses work beyond 64 queued jobs. On a share that hangs,
  its worker can refuse new probes and names with `Overloaded`. Those retry on
  the next pass or after 30 seconds, so nothing is lost.

## 4. Failure recovery by class

"Kill" means the process ends at any moment. Progress is the saved piece
state.

| Failure | Ends or loops | Visible when the person is needed | Keeps the person's intent | Keeps saved progress across a kill |
|---|---|---|---|---|
| Storage error, folder answers | Ends. Automatic → Retried → Stopped (the minute check) → Person. Each step needs no progress since the last resume | Yes, but Stopped also counts as "waits for the person" (section 7, finding 8) | Yes. No storage path writes `facts.intent` | Yes |
| Share that is absent and fails fast | Ends when it returns. The probe sets `absent` and the level back to Automatic | Yes. The location notice after 2 minutes | Yes | Yes, except for a save path that is a link to an absent volume (section 7, finding 3), which saves 0% |
| Share that hangs | Ends only when Windows returns. No timer, except for checks and move copies | **No notice** until a probe returns (C6) | Yes | Yes |
| Disk full | Ends. libtorrent enters upload mode; the engine pauses and probes; the minute check needs room for the remaining wanted bytes | Yes (Stopped, then Person) | Yes | Yes. **In today's binary, a piece whose write failed keeps its file counted complete**, and the file can be renamed (C1) |
| Lost files | Ends. `LostData`, then `Reload` without those pieces | Not needed | Yes | **No** for a link save path to an absent volume, and for a share that drops between the probe and `LostData`. Both drop every piece (section 7, finding 3) |
| Check error | Ends. `ContinueStorage` reloads a check that a disk error stopped | Yes | Yes | Yes. The installed binary restarts the check at piece 0 |
| Stalled check | Ends after the reads return. `checkError = DriveStalled` | Row only, until a probe finds the folder absent | Yes | Yes |
| Move error | Ends. Ladder on `moveRetry`. Open defects: review finding 1, N5, R-a | Yes | Yes | Yes. The marker is committed before placement |
| Rename error (`Unreachable`) | **Loops** every 30 seconds with no error shown when the probe finds the folder answering (N2, still in the code) | **No** | Yes | Yes |
| Handle replacement (`Reload`) | **Loops** once a second when the reload fails before it removes the handle and progress grew since the last resume (R-c) | Partly | Yes | Yes. A failure after the removal rebuilds the `Torrent` and loses its retry level, move error and history (R-c) |
| `alerts_dropped_alert` | Ends. `RecoverDetail`, `RecoverFiles`, a forced checkpoint, `flush_cache`, `completion.Lost`, `ApplyIntent`, `RecordHashes`, `RecoverAdditions` | Not applicable | Yes | Yes |
| Kill | Not applicable | Not applicable | Yes. Saved intent, tasks and markers | Yes, to the last 30-second checkpoint. A reload in flight leaves the old resume file, which libtorrent's check of resume data corrects |
| Exception in a handler | **The engine ends** (C2) | Reported as a start failure | Yes | As for a kill |

## 5. The fork

### What is committed, what is installed

- **History.** The history is squashed. Root 39e37b746 contains upstream v2.1.2,
  the pause correction, the known peers and the first storage change. Upstream
  6da363d2 is not an ancestor (`git merge-base --is-ancestor` exits 1). The
  commits that FORK.rst cites as tested (21aec1b, 627695b) exist as objects,
  but no branch contains them. FORK.rst says "Merge upstream changes into this
  fork". Git cannot do that merge without a common ancestor.
- **Commits after the root.** a4015b235 changes FORK.rst only. Then come
  3490467a2 and 49f7fa319. The uncommitted changes touch `path.cpp`,
  `torrent.cpp`, `aux_/torrent.hpp`, `storage_utils.cpp` and FORK.rst.
- **The installed libraries contain only 39e37b746.**
  - The installed headers hash-match 39e37b746, not 3490467a2
    (`aux_/path.hpp` and `aux_/torrent.hpp` differ).
  - `3rdParty/libtorrent.pin` records 39e37b746.
  - Both libraries contain `check_storage_root` and `post_peer_info`, and do
    not contain `on_counted_piece_unwritten`.
- **The pin is a third revision.** `Dependencies.ps1` pins 3490467a2. That is
  neither what is installed nor HEAD. The next dependency build would install
  3490467a2: without the `reset_save_path` fix, without the kept check
  position, and with the link problem of section 7, finding 3.
- **FORK.rst is still true** when it says "Not yet built or tested".

### Each change

| Change | Commit | Installed | Sound | Belongs in |
|---|---|---|---|---|
| Pause bookkeeping correction | Root (from 627695b) | Yes | Not audited again; FORK.rst records its reviews and test results | libtorrent: a bookkeeping bug there |
| `known_peers` in `post_peer_info` | Root | Yes | Not part of torrent control | libtorrent: the copy must run on the session thread |
| `check_storage_root` at the root only (stat cache, pread and mmap opens, `rename_file`, `move_storage` source, `part_file`) | Root | Yes | Yes for an absent drive or share root. It misses a removed volume reached through a link under an answering root | libtorrent: only the storage layer knows when it treats ENOENT as "no data" |
| Failed check of resume data keeps its pieces; `m_check_files_on_clear` | Root | Yes | Yes | libtorrent |
| Walk to the nearest existing parent | 3490467a2 | No | No for links (section 7, finding 3); the uncommitted change fixes it | libtorrent |
| `on_counted_piece_unwritten` | 3490467a2 | No | Yes by reading. The storage review checked its `counted` test | libtorrent: a bookkeeping bug there |
| `m_recheck_on_clear` (a seed checks its files when its error clears) | 3490467a2 | No | Yes. The uncommitted change makes `force_recheck` clear it, so only one check starts | libtorrent |
| A check keeps its position at the failed piece | 49f7fa319 | No | **No** with two or more hash threads (section 7, finding 5) | libtorrent |
| `async_clear_piece` when read-back fails | 49f7fa319 | No | Not traced in this audit | libtorrent |
| `check_fastresume` without resume data reports the error | 49f7fa319 | No | Yes | libtorrent |
| The part-file guard in pread `initialize`; `set_file_priority` reports the error | 49f7fa319 | No | Yes | libtorrent |
| `move_storage` with `reset_save_path` succeeds when the destination does not answer | 49f7fa319 | No | Yes. The engine's magnet addition depends on it (C1) | libtorrent |
| Rename and part-file checks; the seed's resume-data rule | 49f7fa319 | No | Read in FORK.rst only | libtorrent |
| Open the folder with `CreateFileW` (follows links); `ACCESS_DENIED` answers; a link that cannot be followed does not answer | Uncommitted | No | Yes by reading. I did not measure which Windows code a link to a removed volume returns | libtorrent. **The engine's `AnsweringFolder` must apply the same rule, and it does not** (R-b) |
| `check_storage_root` on the `move_storage` destination | Uncommitted | No | Yes | libtorrent |
| `m_hashing_pieces`: restart at the lowest piece in flight; the same bound in `write_resume_data` | Uncommitted | No | Yes. Pieces are issued in ascending order, and a completed piece is erased, so `front()` is the lowest piece in flight | libtorrent |

**Two owners for a stopped check.** The fork continues a check in place when
`clear_error` runs. The engine does not use that path for a check: for a
torrent in `checking_files` with an error, `ContinueStorage` reloads it from
resume data. The engine uses only the fork's resume-data part. Either path is
correct alone. The plan should choose one owner.

## 6. Invariants

| # | Invariant | Holds? | Evidence | How to measure it |
|---|---|---|---|---|
| I1 | Only the engine thread writes torrent and engine state | Yes | Workers return values, and `Drain` runs their completions on the engine thread. Pipe threads only queue requests. The move's shared fields are atomics | Review. No test can fail for a reason that review misses |
| I2 | The engine thread never waits on a drive | Yes | Section 2. Every fence and file call runs on a worker. The network thread does no file I/O in the traced paths | Manual: pipe latency (`Latency.ps1`) while a real share hangs |
| I3 | A held torrent is paused in libtorrent | Yes | `ApplyIntent` unsets `auto_managed` and pauses when `IsHeld`. Each hold I traced calls it, and `IsHeld` now includes `StorageStopped` | Pipe test T6 |
| I4 | A torrent that is not held follows its intent | **Only when someone calls `ApplyIntent`** | 36 calls. `StopStorage` changes a hold input without the call; harmless today except N2 | The plan's transition check, if `Decide` is built; otherwise T14 |
| I5 | A late result changes nothing | Yes, except R1 | Section 2 | Covered by T3, T15 and T16 |
| I6 | Saved progress never drops data the disk still holds | **No** | Section 7, finding 3 and its gap | T3, T3b |
| I7 | A file is reported complete only when its bytes are on disk | **No in the installed binary**; yes in the fork source | C1 | libtorrent simulation T10 |
| I8 | Work on one drive never waits for another drive | **No** | C3, C4, C5 | Manual with a real share (T13) |
| I9 | Every failure ends: it resumes by itself or reaches the person with Retry; it never loops | **No** | N2, R-c | T14, T15 |
| I10 | The person's intent survives every failure and restart | Yes | Only `SetIntent`, `AbandonTasks`, `Extract` and the document reader write `facts.intent` | Review |
| I11 | A kill at any moment leaves saved state consistent | Yes | Atomic writes in order; the marker is saved before placement; tasks are saved before the work | T16 |
| I12 | A failure in one handler never drops other torrents' work | **No** | C2 | T12 |
| I13 | A torrent that waits for a location shows that wait | **No** for a share that hangs | C6 | T4 |

## 7. The storage review's nine findings in the current code

`storage-review.md` reviewed the committed work. Its line numbers are for the
morning's HEAD. Each finding below is judged on the current working tree.

| # | Finding | Holds now? | Evidence |
|---|---|---|---|
| 1 | A stopped torrent makes the engine query it on every pass | **Fixed** | `QueryCompletions` computes `retries = !recovery && !StorageStopped() && Recoverable(...)`. That is the same condition under which `RecoverStorage` returns early, so a stopped torrent gets no query |
| 2 | A recovery drops the boundary pieces beside a skipped file | **Fixed** | `LostData` skips a file whose priority is `dont_download` |
| 3 | A save folder that is a link to absent storage counts as answering | **Holds** | Engine: `AnsweringFolder` now opens the folder, which follows the link. But on `ERROR_PATH_NOT_FOUND` it walks to the parent, `C:\` answers, and `Reachable` is true. `LostData` counts `ERROR_PATH_NOT_FOUND` as lost, so `Reload` drops every piece. Fork: the installed binary checks only the root (`C:\`), so a check at start ends at 0%; 3490467a2 and 49f7fa319 walk with no link stop; only the uncommitted `path.cpp` stops at a link that exists but cannot be followed |
| 3, gap | A share that drops between `Reachable` and `LostData` | **Holds** | `LostData` does not check the folder again, and counts `ERROR_PATH_NOT_FOUND` as lost |
| 4 | Some torrents wait for a location with no notice | **Partly fixed** | `UpdateProblems` now keys the location notice on `recovery->absent`, not on the problem kind, so `DriveStalled` and `ERROR_SEM_TIMEOUT` get it. It still holds for a share that hangs: the notice needs a returned probe (C6) |
| 5 | A stopped check can skip pieces below the failed one | **Fixed in the uncommitted fork source**; present in committed 49f7fa319 | The restart is now `min(piece, m_hashing_pieces.front())`. The installed binary restarts at 0, so it does not have this defect. The pinned 3490467a2 does not have it either |
| 6 | A Stopped or Person torrent is not held | **Fixed** | `IsHeld` includes `StorageStopped()`. What remains is N2: a stop whose cause was only in `recovery->cause` has no `StorageProblem`, so it is not held |
| 7 | Verify does nothing for a torrent that waits for Retry | **Fixed** | `Verify` calls `AnswerStorageProblem`, then `ClearDiskError` when there is no recovery, then `Recheck`, then `ApplyIntent` for a stopped torrent |
| 8 | Stopped counts as "waits for the person" | **Holds** | `NeedsPerson` is true for any storage problem without a recovery, and Stopped has none. The related gap holds too: in `CheckStopped`'s completion, a failed `Usable` is skipped (`continue`). So a location that stops answering while the torrent is Stopped does not reset the level to Automatic, which engine.md requires |
| 9 | The "stopped" popup keeps the one-torrent action after the count rises | **Holds** | The Updated path calls `popup_.Update`, and `Popup::Update` replaces only the title, body, detail and location. The actions and `select` stay |

The review's suspicion about `Reload` on `payload` is real; C3 gives the failing
sequence. Its note on `retryRequested` lost during a reload is harmless, as it
says.

## 8. Transition tests

Each failure sequence above has a test that would catch it if it returned, or
a stated reason why it has none yet. testing.md decides the layer: the
cheapest one that can fail for that reason.

### The checks

The checks are in `engine/tests/Checks.ps1`. Run one with
`pwsh engine/tests/Checks.ps1 -Check <name> -EnginePath <engine>`.

- **The build.** On 2026-10-27, each check ran once on
  `artifacts\bin\Engine\Release\TinyTorrent.exe` (SHA-256 44A02558…, the
  engine of the build recorded at 14:54Z). No engine source was newer than
  that executable. The build includes the Exit agent's change.
- **The faults.** Each check removes its faults in the script's `finally`,
  also when it fails.
  - A byte-range lock on the payload file fails every read with
    `ERROR_LOCK_VIOLATION`, which the engine retries. The folder still
    answers and the file still opens, so the engine's probes find the storage
    usable. This replaces the ACL in the first draft of this list.
  - A `subst` drive that the check removes and maps again.
  - A junction to such a drive.
- **Reruns.** Three runs of `AbsentLink` and one of `AbsentNotice` stopped on
  errors in the test itself:
  - a drive letter passed to `Assert` as a string;
  - the info dictionary's `pieces` key read as the resume file's own;
  - that `pieces` string read as bytes instead of a bitfield;
  - for `AbsentNotice`, toasts read from a stand-in window. A headless engine
    prints a raised condition and does not send it to the window
    (`Toasts::Queue`), so the check now reads the engine's log line for each
    notice it sends. Only repairs and these checks run the engine headless.
  
  Each check's last run is the result below.

| Test | Check | The failure it watches | Result |
|---|---|---|---|
| T1 | `StorageStop` | A stopped torrent makes the engine query it on every pass. Asserts less than 3 s of engine processor time in 30 s while the torrent is Stopped | **Pass** |
| T7 | `StorageStop` | Verify does not check a torrent that waits for Retry. Asserts that, once the lock is gone, Verify ends at 100% with no error and no `retry` | **Pass**. The error in this sequence is retryable: the torrent reaches Person through the whole ladder. No local fault gives an error that cannot be retried |
| T3 | `AbsentLink` | A start with a junction's drive absent saves the torrent's pieces as lost. Asserts 16 of 16 pieces in the resume file after that start, and that the torrent resumes once the drive returns | **Fail**, as expected: 0 of 16 pieces saved, and the torrent was downloading with no error. The log has no storage entry, so libtorrent alone counted the files as missing (the fork half). No recovery ran, so the Exit change does not explain it |
| T8 | `StoppedLocation` | After the location was absent and returned during a stop, the next failure goes straight to the person. Asserts that the torrent continues by itself once its file reads again | **Fail**, as expected. After the drive returned, the minute check continued the torrent once, and the next failure was logged `retry_needed` |
| T4 | `AbsentNotice` | A torrent that waits for an absent drive raises no notice. Asserts one notice within 3 minutes | **Pass** for an absent drive. The error was `download_path_unavailable`. No local fault keeps a recovery with the kinds of finding 4 (`DriveStalled`, or an SMB timeout code) on an absent drive, so those kinds are not covered |
| T16 | `KilledCheck` | A check that a kill interrupts is lost. Asserts that after the restart, with the setting "continue", nothing is asked and the torrent ends complete | **Pass** |
| T6 | `StoppedHeld` (`-Transfer`) | A stopped torrent resumes when a speed limit reapplies its intent. Asserts that the seed, which tries to connect every second, gets no connection for 10 s | **Not run.** It needs a real transfer, which runs only when the owner asks |
| T11 | `MagnetAbsent` (`-Transfer`) | A magnet added to an absent drive fails. Asserts that the addition is accepted and the torrent is listed with that folder | **Not run**, for the same reason. Expected to fail with today's binary |

None of the results disagrees with the "Today" column of the first draft.

The move case of T16 already has a check: `UnfinishedTasks` kills the engine
during a move and checks that the move continues.

### Not written

- **T3b, T12, T14, T15.** No deterministic trigger exists without a seam in
  the engine, and testing.md forbids adding one. They wait for the
  correction's design: if `Control::Decide` becomes the production structure,
  they are decision-level tests of it.
  - T3b: the folder drops between the probe and `LostData`.
  - T12: a handler throws.
  - T14: a rename fails with `Unreachable` while the folder answers.
  - T15: a reload fails before it removes the handle.
- **T2** (storage review finding 2; fixed). The state needs a downloaded piece
  that a skipped file shares with a wanted file. No fixture has one: the
  Transfer peer's `seed-files` torrent makes the skipped file exactly one
  piece. A new peer mode needs a Transfer.exe build, which this step does not
  allow.
- **T5 and T10.** These belong in the fork's tests, but no existing libtorrent
  fixture fails a read or write of the `pread` storage.
  - The simulation's `test_disk` (simulation/disk_io.cpp) can fail writes
    with a full disk. It hashes only blocks it has written, so it cannot
    produce T10's order, in which the piece passes before its write fails.
  - It cannot fail a read, so it cannot produce T5.
  - `test/disk_io_test.hpp` tests the disk backends without a torrent.
- **The rest of T16.** A reload lasts milliseconds, so a kill cannot be aimed
  at it.
- **T9** stays with review. The popup has no test seam.

### Manual checks for the owner

Making a share stop answering needs a change outside this machine's test
folders, so these two checks are the owner's.

**T13, one drive holds up another.**
1. Add torrent A on a network share, and torrent B on `C:`, with B close to
   finishing and "Add .!tt to unfinished files" on.
2. Unplug the network cable of the share's server, not of this PC, so that
   Windows waits for its SMB timeouts instead of failing at once.
3. Remove A with its files, and let B finish.
4. **Expected:** B's file takes its final name within a minute, and B's Files
   tab lists its files.
5. **Today, from source review:** B waits until Windows gives up on the
   share.

**T4, a share that hangs.**
1. Add a large torrent on the share, and Verify it.
2. While it checks, unplug the server's network cable.
3. **Expected:** once the check stalls for 2 minutes, and the notice delay of
   2 more minutes passes, the window shows one notice for `\\server\share`.
4. **Today, from source review:** no notice appears until Windows returns the
   stalled read and the probe.

## 9. The plan's section 1 checked against the code

The plan's audit is accurate where its design depends on it, with these
corrections:

- **1.5 N1, N2, N5, R-a, R-c, #541: confirmed in the current code.**
  - N2: `ContinueRename` and `On(file_rename_failed_alert)` still pass
    `Unreachable()` only to `recovery->cause`.
  - R-c: `ContinueStorage` still sets `storageRetry` before it calls `Reload`,
    and a failure before the removal returns to `Waiting`.
  - #541: names wait for deletions and the rename, not for moves (C5).
- **1.5 R-b is half the story.** It describes the uncommitted fork. In the
  installed binary, both libtorrent and the engine count a link to a removed
  volume as answering, and that loses progress (section 7, finding 3).
- **1.5 H is confirmed.** It misses two things:
  - The same pools block the engine's own per-drive workers through
    `ReleaseFiles` (C4).
  - The engine's `payload` worker is a second shared queue, outside libtorrent
    (C3).
- **1.6 is wrong about `torrent_file()`.** It does not wait for the network
  thread; it reads a weak pointer under a mutex. The rest of that point holds:
  the network thread does no file I/O in the traced paths.
- **1.6 credits the fork with behavior the binary lacks.** "`clear_error` runs
  … a seed's pending recheck again (fork)" is 3490467a2, which is not
  installed. The plan's summary says the storage changes are not installed, but
  section 1.6 reads as current behavior.
- **1.6 misses an alert that does not mean what the engine assumes.**
  `torrent_paused_alert` after a check error does not mean that the check's
  reads ended (R1).
- **1.6, `handle_disk_error`, is right.** One detail: `not_enough_memory` posts
  the alert without setting an error or pausing.
- **1.2 omits alerts that write control inputs.** The controller must also
  take these alerts:
  - `storage_moved_alert` and `storage_moved_failed_alert` (move and addition
    outcomes);
  - `torrent_checked_alert` (`filesPending`, completion);
  - `file_prio_alert` (priorities);
  - `save_resume_data_alert` and `save_resume_data_failed_alert` (checkpoints
    and the verify proof);
  - `torrent_removed_alert`, `torrent_deleted_alert` and
    `torrent_delete_failed_alert` (deletions);
  - `state_changed_alert` (completion).
- **1.4 and 1.5 do not list two defects:** the missing exception boundary (C2)
  and the shared `payload` worker (C3).

## 10. What to keep

- The single owner thread, with workers that return values and completions
  that run on it.
- `Torrent::ApplyIntent` as the only code that pauses or resumes a listed
  torrent, with `IsHeld` derived from facts.
- Drive claims derived from running work on each pass (`DrivesInUse`).
- The guards: same handle, same `Recovery` object, same `generation`, `Find`
  after `ReplaceHandle`, and `SameFolder` on probes.
- Outcomes observed after a fence (`FinishMove`, the rename's completeness
  check, `Reload`), never taken from droppable alerts.
- The release future from `async_release_files`, polled without blocking.
- Checkpoint coalescing that keeps the newest data.
- The reconciliation after `alerts_dropped_alert`.
- The storage ladder bounded by progress (`FileProgress::Beyond`), and the
  minute check that does not start the torrent.
- The marker saved before placement, and the saved tasks.
- In the fork: `check_storage_root` and its uncommitted link rule,
  `on_counted_piece_unwritten`, the kept resume-data pieces,
  `m_recheck_on_clear`, the `reset_save_path` fix and the `m_hashing_pieces`
  restart. Each one fixes libtorrent bookkeeping that the engine cannot see,
  so each one belongs in libtorrent.

## 11. The plan's section 2 compared with this audit

I read section 2 only after I wrote sections 1 to 10.

### What the findings support

- **The failure episode (2.2).** A stop that always has its cause fixes N1,
  N2 and the rest of the storage review's finding 6. Treating a rename that
  found its folder not answering as `Failed` ends the 30-second loop of N2.
- **The executor (2.3, 2.4).** The executor compares the state before and
  after each event and calls `ApplyIntent` when the state changed (invariant
  9). This makes I4 hold without 36 callers that must each remember the call.
- **Invariant 4.** A failed reload counts as a continue. This ends the loop in
  R-c.
- **The final-folder move's own episode.** It fixes the state-model review's
  finding 1.
- **Tickets (2.5).** Tickets are sound. They replace guards that are already
  sound today (section 2), so no confirmed defect depends on them.

### What the findings contradict

- **"A release or a reload always finishes" (2.5).** It finishes only when
  Windows returns. On a share that hangs, that can take minutes or longer
  (C6). With at most one live job per torrent, the episode then cannot advance
  and cannot report anything. The design names no timer and no visible cause
  for that wait, so I13 still fails.
- **`NeedsPerson` (2.7)** still counts Stopped as "waits for the person". The
  storage review's finding 8 says this contradicts pipe-api.md's `retry` flag.
  notifications.md can be read either way. The plan should say which text
  wins, because the code follows one and contradicts the other.
- **`Checked{usable, lost files}` (2.3)** cannot say "absent". `Usable` is
  false both for a folder that does not answer and for one without room. So
  the rule in 2.2, "a location that does not answer also ends it", cannot
  apply while a torrent is Stopped. That is the related gap of the storage
  review's finding 8.

### What the design misses

- **C1, the build.** The design depends on fork behavior that the installed
  binary lacks: the recount after a failed write, `reset_save_path` to an
  absent share, the seed's recheck, and the kept check position. The pin names
  a third revision. Until the pin, HEAD and the installed libraries agree, the
  design's tests measure a different library from the one they assume.
- **C2, the exception boundary.** Tickets drop a late result. They do nothing
  for a result that never arrives because an earlier handler in the batch
  threw. Then the one live job never ends, and the torrent waits with no
  cause. `Decide` and the executor run inside the same `Drain` loop that has no
  `try` today.
- **C3, the shared `payload` worker.** Invariant 6 says "a job on one root's
  worker touches only that root". `Reload`, `QueryFiles`, `Delete` and
  `GiveBackRemoved` run on `payload`, which is no root's worker. Steps 1, 5
  and 6 do not move them, so invariant 6 cannot hold after all steps.
- **The gap in the storage review's finding 3.** `Probed` carries "answered"
  and the lost files, but they come from two calls: `Reachable`, then
  `LostData`. A share that drops between them reports every file lost, and
  the reload drops every piece. The design must treat "every file missing" as
  "not answering", or probe again before a reload.
- **The check that a disk error stopped.** Today `ContinueStorage` reloads such
  a check even when no file was lost. The Recovering stage in 2.2 lists a
  reload only "when files were lost". If the design drops the other reload,
  the fork's in-place continue on `clear_error` takes over, and that needs
  49f7fa319 and the uncommitted `m_hashing_pieces` fix in the installed
  binary. One owner should be chosen on purpose.
- **R1.** Drive turns stay outside the controller (2.1). They still take
  `torrent_paused_alert` as "the reads ended", which is false after a check
  error.
