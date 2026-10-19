# Removed torrents leave the list (issue 540)

Status: designed, not built. The design is settled; build it from this plan.
When it is built and verified, move its live rules into
[the engine contract](../contracts/engine.md) and
[the pipe API](../contracts/pipe-api.md), and delete this plan.

Issue: https://github.com/SynoSoftware/TinyTorrent2/issues/540

## Why this plan exists

A removed torrent whose own files are still busy stays in
`Engine::State::torrents` with `Torrent::deleted` set. 44 reads in 12 files
each repeat "skip removed torrents". `FindDuplicate` does not, so adding the
same torrent again during that time says "already in the list" (defect #7).
Three architecture passes judged the flag fine because one function writes it.
They missed that every reader must remember the rule.

## How the design was decided

The owner asked for several strong models to agree before anything is built.
Each one read the code at commit 0e64a61.

| Model | Proposal | Main conclusion |
|---|---|---|
| Fable | Design A | Only `deleted` is the wrong model. The whole removed `Torrent` moves into a separate `removed` map; file work finds it through one lookup. |
| Fable, second idea | Design E | The removal record keeps only copied data, relying on libtorrent's fence-job ordering and the single payload worker. |
| Fable, independent | Removal holds the torrent | The removal record holds the whole torrent only for a running move or rename; `moving` and `namePhase` merge into one state machine; narrow the waits. |
| Astra, independent | Removal owns the torrent | The removal record owns the whole torrent while its work drains; then three further remodel steps. |
| Opus | Review of Design A | Confirms A with nine corrections; found a crash, a use-after-free and a permanent stall in A as first written. |
| Fable, judge | Decision below | Builds A as corrected by Opus. Rejects E, because renames update the names a deletion deletes after the removal. Rejects the wider remodel as churn. |

Everything below is the judge's decision, checked against the code.

Code read at commit 0e64a61 (`engine/` is unchanged in history since; the working tree only renames Notice to Toast in these files). Paths are under `engine/` unless they start with `3rdParty/`. "Phase 1" is the time from the removal's document commit until the engine calls `remove_torrent`. "Phase 2" is the time from `remove_torrent` until libtorrent's release alert. A "listed" torrent is one in `Engine::State::torrents`.

## 1. The decision in five lines

1. Build Design A as the Opus review corrected it (A′): a removed torrent whose own files are busy moves, whole, from `torrents` into a second map `removed`, and `handles` indexes listed torrents only.
2. The hold is today's `CanRemove` test, unchanged: the torrent's own `FilesBusy()` and, for Delete files, no running addition. Not narrower (fable2) and not captured data (Design E).
3. `torrent_removing` is refused in phase 1 only, from `Inspect`, `Add` and the add-failed alert; phase 2 keeps today's behaviour, where libtorrent accepts the re-add.
4. One wait goes: a delete-mode removal in phase 1 no longer blocks other torrents' name preparation and finishing renames. Everything else in `FilesBusy()`, `FilesReady` and shutdown stays as today.
5. No wider remodel. The FileWork state machine, derived `moving`, Restoring|Loaded, checkpoint and priority-edit variants and the Delete-time kept set are rejected. Gate narrowing to path overlap is separate work, if the owner wants it.

## 2. The model

### 2.1 Declarations

```cpp
// inc/Torrent.h: `deleted` and `deletionMode` are deleted. Nothing else changes.
// IsHeld loses its `deleted ||` term and its comment loses "its removal".

// inc/Engine/State.h
// A torrent the person removed whose own files are still busy: the running
// member of a move, an owner of the running rename, name preparation, a
// Zone.Identifier write, or a deletion that waits for a running addition.
// It is out of the list and the saved document. Its handle goes to
// libtorrent for removal when that work ends.
struct Removal
{
    Torrent torrent;
    // Nothing when the person kept the files.
    std::optional<DeletionMode> mode;
};

std::map<std::string, Torrent> torrents;         // the list: rows, document, commands
std::map<std::string, Removal> removed;          // phase 1
std::map<lt::torrent_handle, Torrent*> handles;  // listed torrents only
std::list<Deletion> deletions;                   // phase 2, unchanged

// A listed or removed torrent: the ones whose files the engine may still
// change. Throws like map::at when neither holds the id.
Torrent& Working(std::string const& id);
Torrent const& Working(std::string const& id) const;
Torrent* FindWorking(std::string const& id);
// For the alert handlers that write names, renames, marking and file
// completion, which a removed torrent's release still depends on.
Torrent* FindWorking(lt::torrent_handle const& handle);
// Visits every listed torrent, then every removed one.
template <class Visit> void EachWorking(Visit visit);
template <class Visit> void EachWorking(Visit visit) const;
// A removal in phase 1 still loads this content in libtorrent, so a second
// copy is refused as torrent_removing.
bool Removing(lt::info_hash_t const& hashes) const;
// Takes a torrent that CanRemove refuses out of the list into `removed`.
void Extract(std::string const& id, std::optional<DeletionMode> mode);

// inc/Enums.h: ErrorCode gains TorrentRemoving. Engine.cpp's word table gains
// {"torrent_removing", ErrorCode::TorrentRemoving}.
```

`Deletion`, `DeletionPhase`, `Move`, `Rename`, `NamePhase`, `moving`, `Torrent::FilesBusy()` and `Engine::State::FilesBusy()` keep their shapes.

### 2.2 Who owns each fact

| Fact | Owner |
|---|---|
| In the list and the saved document | `torrents` membership |
| Removed, own files busy, still loaded in libtorrent | `removed` membership |
| Removed, handle given to libtorrent, not yet released | `Deletion::waiting` membership |
| Keep or delete, which mode | `Removal::mode` in phase 1, then `Deletion::mode` (hand-over at `RemoveHandles`, never both) |
| A torrent's own files are busy | `Torrent::FilesBusy()`, unchanged |
| The engine's file operations are busy | `Engine::State::FilesBusy()` = `move || !deletions.empty() || rename` |
| Which alerts reach a removed torrent | `handles` (none) plus the four handlers that call `FindWorking(handle)` |
| Which content a preview may not add | `Removing(hashes)` over `removed`; `FindDuplicate` over `torrents` |
| The removed torrent stays paused | `facts.intent = Intent::Paused` set once at `Extract`; every later `ApplyIntent` keeps it paused |

### 2.3 Legal transitions

1. Listed → gone through `Deletion`: `Remove`'s commit callback, for each id that `CanRemove` accepts at commit. `RemoveHandles` as today (Removal.cpp:69).
2. Listed → `removed`: the same callback, for each id that `CanRemove` refuses (today's `later`, Removal.cpp:70-76). `Extract(id, mode)` does, in this order: fail `priorityCompletion` with `TorrentRemoved` and `FailReading` if `reading->torrentId == id` (moved here from Removal.cpp:134-141, which keeps them for the ready ids); clear `needsRecheck`, `prioritiesPending` and `waitingMove`; set `facts.intent = Intent::Paused` and call `ApplyIntent`; erase the handle from `handles` and `seedQueries`; take the id out of `move->ids` unless it is the running member (section 3, question 2); move the node into `removed`.
3. `removed` → `Deletion`: `RemoveDeferred`, at its place in `Tick` (Session.cpp:718) and nowhere else, scans `removed` instead of `torrents` with today's tests (`changes.IsIdle()`, `NamesReady()` for a mode, `CanRemove`) and calls `PrepareDeletion` and `RemoveHandles`, which erase the `Removal`.
4. Listed, conflicting restore → `removed`: as 2, when Delete files meets a running addition (`CanRemove` with a mode). It has no handle; `RemoveHandles` skips `remove_torrent` for it as today (Removal.cpp:142). Opus correction 8; fable.md's section 4 was wrong to delete that branch.
5. `Deletion::waiting` → gone: alerts or an invalid handle, unchanged (Removal.cpp:214-252, 397-444).

Unrepresentable after the change: a removed torrent in the list, in the saved document, in a row, in `handles`, in `reading`, in `queueOrder`, in a seed query, or with a waiting move.

## 3. The questions

### Question 1: whole Torrent, subset, or captured data

**Whole `Torrent`, held for exactly as long as `CanRemove` refuses today.** fable.md, opus-review.md, fable2.md and astra.md are right on "whole"; fable-best.md (Design E) is wrong; fable2.md is wrong to narrow the hold to Moving and Renaming.

Evidence:

- The names the deletion deletes are on the `Torrent`, and they change while the torrent is removed. `On(file_renamed_alert)` writes `torrent->names->mappings` (Files.cpp:700-701). `RemoveHandles` → `PrepareDeletion` → `FileScope` reads `torrent.Paths()` from those mappings (Files.cpp:849, Torrent.cpp:194-220). A torrent removed while Preparing has `rename_file` requests queued; their alerts arrive after the commit. Design E captures names at the commit, and its `Capturing` phase re-reads them only when the capture is empty (fable-best.md section 2.3, step 3). So E deletes with names from before the renames. Today's `CanRemove` waits for Preparing to end, so names are current. A′ keeps that.
- Recovering means the mapping is unknown after a lost alert (Files.cpp:259-265); only the `QueryFiles` observation restores it (:331-334). fable2.md releases a Recovering torrent at Remove ("nothing in flight"). Its deletion would then use the mapping that is known to be stale. Hold it, and query it (question 2).
- The move's running step reads and writes the member: `FinishMove` compares the handle (Move.cpp:473), `SaveMove` computes `Relocated(torrents.at(id))` from `names` and `facts.savePath` (:519, :523), `settle` writes `unsaved`, `completedFiles`, `namePhase`, `renameAt` (:537-541) and `relocate` writes `facts.savePath` (:551), `FailMove` reads `FilePaths(member, destination)` (:600). The running rename resolves its owner by `Find(handle)` and `torrentId` (Files.cpp:698-715) and its recovery reads `names`, `renaming`, `completedFiles`, `generation` (:500-536). Captured data would be a second shape of the same facts, written by the same code. Opus section 3 verified the same.
- libtorrent's ordering, verified in `3rdParty/libtorrent/src/pread_disk_io.cpp` (Windows uses `pread_disk_io`, `session.cpp:495-496`): `async_rename_file` (:1054), `async_move_storage` (:974), `async_release_files` (:989), `async_delete_files` (:1021) and `async_stop_torrent` (:1068) all call `add_fence_job`, and a fence job runs only after every earlier job on that storage (`include/libtorrent/aux_/disk_job_fence.hpp:31-36`). `remove_torrent` → `abort()` → `async_stop_torrent` (`torrent.cpp:5378-5382`). So a `rename_file` sent before `remove_torrent` finishes before the storage stops, and `on_file_renamed` posts its alert without an abort check (`torrent.cpp:5522-5543`). This proves that a removed torrent's queued renames complete and report. It does not make the engine's copy of the names unnecessary; it is why the engine must still receive those alerts (question 2).
- The single payload worker (`inc/Store.h:78`, one `worker_`, one `jobs_` deque) runs the Zone.Identifier write (Files.cpp:655) before a deletion's `Erase` (Removal.cpp:352). Opus is right that fable.md's "Delete files can run during the Zone.Identifier write" failure mode is not real. `marking` still counts in `FilesBusy()`, as today: it costs milliseconds, keeps the mark on a kept file, and keeps `CanRemove` unchanged.
- The engine's own copy (Move.cpp:430-433, `copies` worker, `Payload::Move`) has no external cancel; `stop_` in Payload.cpp:280-425 is set only by the copy's own failure. Cancelling it for Delete files is a change to the move step, outside 540 (section 5).

### Question 2: where it lives, how alerts and file work reach it, and the three defects

**`removed`, a map beside `torrents`, not inside `Deletion`.** Opus's corrections 1 to 6, 8 and 9 are confirmed against the code; fable.md as written has the three defects Opus names. fable2's `Deletion::held` and Design E make every keep-files removal a `Deletion` from its commit, so `!deletions.empty()` would then block every torrent's name preparation for the length of a removed torrent's move (fable-best.md section 3.8 admits this and adds the overlap test to compensate). A separate map keeps `Deletion`'s meaning, "handles given to libtorrent", and keeps today's phase-2 gates unchanged. Astra's tagged handle owner and Draining/Releasing/Released member states say the same facts with more machinery than two maps.

How work reaches a removed torrent:

- By id: `Working(id)` where the torrent must exist (move members, rename owners, `PrepareDeletion`, `FileScope` selection, `Holders`), `FindWorking(id)` where absence is normal (the name-preparation completion Files.cpp:167, the `QueryFiles` completion :318, the marking completion :688). A `QueryFiles` observation can answer after its torrent was released, so :318 needs the tolerant form; the other two tolerate for the same reason `torrents.find` does today.
- By handle: `Find(handle)` stays listed-only. Exactly four handlers call `FindWorking(handle)` instead, because they write the facts `NamesReady()`, `FilesBusy()` and the deletion's file list depend on: `On(file_renamed_alert)` (names, `renaming`, rename progress), `On(file_rename_failed_alert)` (`renaming`, Recovering), `On(file_completed_alert)` (`completedFiles`, `marking`), `On(metadata_received_alert)` (`filesPending`, Alerts.cpp:385-386). The fourth is new in this list: without it a removed magnet that gets metadata in phase 1 has `handle.torrent_file()` and no `names`, `NamesReady()` stays false for ever (Files.cpp:814-815), and a delete-mode release waits on `NamesReady()` (Removal.cpp:203). Every other handler ignores removed torrents by construction, which closes Opus's handle-reached paths (`FinishDownload` through `cache_flushed_alert`, `Notify(Completed)`, `Verify` → `torrents.at`, status bookkeeping, checkpoints, seed queries, conflicts, errors).
- By walk: a walk that answers a question about files on disk uses `EachWorking`: `QueryFiles` (Files.cpp:282), `FinishFiles`'s outside-owner walk (:382), `NamesReady` (:811), `FilesReady` (:825), `FileScope`'s outside walk (:877), `SharedFiles` (Previews.cpp:29), `RecoverFiles` (Files.cpp:1001), and `RecoverAdditions`'s `accepted` set (Additions.cpp:406-408). A walk that answers a question about the list uses `torrents`: rows, document, activity, queue, policy, notices, history, `ContinueNames`, `Maintain`, `MoveWaiting`, `ResumeMove`, shutdown's pause loop, `FindDuplicate`.
- `move->ids` holds only working torrents. `Extract` takes the removed id out of `move->ids` unless it is the running member, with today's index rule from `ContinueMove` (Move.cpp:391-393): the running member is `ids[current]` when `move->phase` is not `Preflight` or `Ready`. Erasing an index below `current` decrements `current`. A removed non-running member gets `moving = false` and `facts.marker.reset()` as `ContinueMove` does today (:402-403), and `ContinueMove`'s drop loop (:389-405) is deleted. `Remove` runs inside a queued change and `StartMove` only inside another queued change (Commands.cpp:466, Files.cpp:455, Move.cpp:146, :246), and `StartMove`'s `CommitFacts` completes inside its own change, so at `Remove`'s callback no move is in `Staging`; `StartMove`'s callback keeps `torrents.at`.
- Rename owners stay in `rename->owners` until `EndRename`; a rename lasts milliseconds and `moving` holds the owner. All owner lookups use `Working`.

The three defects, impossible by construction:

1. Crash at Detail.cpp:195 (`torrents.at(reading->torrentId)`, also :339): `Extract` fails the reading, `EndReading` starts waiting reads through `CollectDetail`, which answers `torrent_removed` for an id not in `torrents` (:118-122), so `reading` never names a removed torrent. The `.at` is then always safe and the `deleted` test at :196 is deleted.
2. Use-after-free: nothing releases a `Removal` except `RemoveDeferred` at its place in `Tick`, after `Handle(alert)`, `ContinueRename` and `ContinueDeletion`. `EndMove`, `EndRename` and `FinishNames` call nothing new. `On(file_renamed_alert)` at Files.cpp:712-716 keeps its pointer valid. fable.md's synchronous triggers are rejected; they bought nothing, because `Tick` drains every store and alert first (Session.cpp:701-718).
3. Stall of a Recovering removed torrent: `QueryFiles` walks `EachWorking`, so the observation that ends Recovering (Files.cpp:333-334) reaches it. Also `RecoverFiles` walks `EachWorking`, so a lost preparation alert is recovered for a held torrent.

Two more facts Opus did not list:

- `RestorePending` (Session.cpp:261) today relies on `FindDuplicate` finding the held twin. It becomes `if (!torrent.restore || Removing(torrent.status.info_hashes) || FindDuplicate(...)) continue;`. Without it, `Restore` calls `add_torrent`, libtorrent refuses `duplicate_torrent`, and a fresh conflicting record is written each maintenance tick (Opus item E).
- `RecoverAdditions` excludes "accepted" handles from its userdata scan by reading `handles` (Additions.cpp:406-408). The live torrent's userdata is never cleared (only the saved copy at :273). With `handles` listed-only, a phase-1 removed torrent that was added this session would be scanned, and `session->get_torrents()` still lists it (it is erased from `m_torrents` only at `remove_torrent`, `session_impl.cpp:5531`). Its stale `Addition*` can equal a later addition's address. So `accepted` is built from `EachWorking`. Astra's fourth risk, confirmed.

### Question 3: waits

The table is in section 5. The decisions:

- Required for file safety, kept: the removal's own wait on its torrent's `FilesBusy()`; Delete files' wait on `additions.empty()` and `NamesReady()`; a deletion's wait on another removal's holds (Removal.cpp:237-245); moves' wait on `FilesBusy()` and `additions` (owner's rule "One payload operation runs at a time"); shutdown's wait on `move`, `deletions` and `rename`; the reply's wait on the document commit.
- Not required, removed in this change: a delete-mode removal in phase 1 blocking `RemovingOrRenaming()` (Files.cpp:118-119). In phase 1 the removal reads and writes no file; its own disk work is already gated where it matters (`move`, `rename`, and each torrent's `FilesBusy()` through `EachWorking` in `FilesReady`). The deletion computes its file list at release from `FileScope`, which compares both the logical and the physical name of every outside torrent (Files.cpp:920-927), so a torrent that prepares names or finishes a rename meanwhile keeps its shared file. The comment at State.h:756 states the rule without a reason; Opus found none, and I found none. `RemovingOrRenaming()` becomes `!deletions.empty() || rename.has_value()`, and the scan of `torrents` goes. Opus asked for a safety argument for a rename still running at release: `RemoveDeferred` has never waited for `rename` (Removal.cpp:191-212), so release during a rename is today's behaviour, and the both-names rule above is what makes it safe.
- Not required, removed in this change: the phase-2 refusal of a re-add. `remove_torrent_impl` erases the torrent from `m_torrents` at once (`session_impl.cpp:5531`), so libtorrent accepts the re-add; today the engine accepts it too, and `Holders` names the pending deletion in the Add dialog (Files.cpp:984-988, contract "Shared files"). fable.md's `Deletion::hashes` is dropped. Opus correction 7.
- Not required, removed in this change: an Edit reply waiting for file priorities and a held detail read ending only at release (Removal.cpp:134-141). They end at `Extract`.
- Not required, left for separate work: any move, deletion or rename stopping every torrent's finishing rename (`FinishFiles`, Files.cpp:359) and the phase-2 deletion stopping every torrent's name preparation (`ContinueNames`, :128; `PrepareAddition`, Additions.cpp:157). Narrowing them to path overlap needs the overlap test fable-best 3.8 describes and its own review. The name-preparation wait during a deletion is also what the owner's ruling "joins the list without choosing its file names and stays paused, writing nothing, until that work ends" describes, so it is not a defect of this change. Cancelling the engine's copy on Delete files is the same kind of separate work.
- The refusal itself: `torrent_removing` lasts phase 1 only, that is the length of the torrent's own move, rename, preparation or mark, or of a running addition. libtorrent cannot hold two copies of one torrent, so a refusal is the only honest answer for that window. The owner chose it; it is settled.

Nothing in the UI path waits. Rows come from `torrents`; `Remove` replies at its commit (Removal.cpp:80); `preview` and `add` reply at once with the refusal.

### Question 4: the wider remodel

| Proposal | Verdict | Reason |
|---|---|---|
| `moving` + `namePhase` as one `FileWork` machine (fable2 2.2) | Rejected | The machine needs `namedBeforeMove_` to know whether names were Ready before Moving (fable2.md:275). A hidden second bit means Moving is not a name phase: "which names" and "something owns the files" are independent facts, and AGENTS.md says independent facts are booleans. Today's guards keep the two from conflicting (`StartMove` through `FilesReady`, `FinishFiles` requires Ready). No defect shown. |
| Derive `moving` from `move`/`rename` membership (astra, step 3) | Rejected | `FilesBusy()` runs for every torrent every maintenance tick (Session.cpp:757-767). Derivation is torrents × members per tick, and needs `move->phase` and `current` (a Cleaning member has already ended, Move.cpp:542-543). The cached flag has five paired writers and one reader of the rule. Opus and fable.md agree. |
| `restore` + `handle` as a Restoring\|Loaded variant (astra step 2, Opus observation) | Rejected | One writer (Session.cpp:228), one consumer (:265), every handle use already guarded by `restore`. A variant touches 42 reads and every handle read for a state that occurs only after a startup alias conflict. No defect; a major refactoring by the owner's rule. |
| Checkpoint `pendingCheckpoint` only inside Writing; priority edit as Idle\|Deferred\|Applying (astra step 4) | Rejected | One owner each (Checkpoints.cpp, Edits.cpp), no reported failure, not on 540's path. |
| Delete-time kept set and recomputed deletion scope (fable2 3.1, astra "Removal") | Rejected | Files are computed at release, after the torrent's own work. Between release and `Delete`, moves wait on `FilesBusy()` and new torrents prepare no names (`ContinueNames` gate), so no listed torrent can take a file in that window. No failure shown. |
| `Metadata()` accessor for the four `restore ? restore->ti : handle.torrent_file()` repeats (fable2 2.3) | Not now | Correct and small, but it reaches Previews, Torrent.cpp and Files.cpp for no behaviour. Do it when one of those lines is edited for a reason. |
| Gate narrowing to path overlap (fable-best 3.8, fable2 3.7) | Separate work | See question 3. The safety argument is the both-names comparison; the code is the `Holders` pattern (Files.cpp:960-990). |
| Diagnostic-only gate in the engine to hold a libtorrent job (astra section 7) | Rejected | testing.md: "a member made public, an interface added, or a class split only so that a test can reach it is the test bending the product." |

The counts the documents disagree on: nine joint `deleted && restore` tests (Alerts.cpp:290, Commands.cpp:656, Detail.cpp:76, Files.cpp:136, :446, Move.cpp:201, Policy.cpp:490, Queue.cpp:32, Session.cpp:261), confirmed by `git grep`; 27 `torrents.at` in Move.cpp (Opus), not 31 (fable.md). Astra is right that the `Conflict` check (Checks.ps1:104) tests simultaneous launches, not removal; 540's "done when" should name `Restore` for the conflicting-restore case and drop `Conflict`.

## 4. What changes, file by file

All in one commit. Build once at the end.

**inc/Torrent.h**: delete `deleted`, `deletionMode` and their comments (:221-225). `IsHeld`'s comment drops "its removal".

**src/Torrent.cpp**: `IsHeld` drops `deleted ||` (:475).

**inc/Enums.h**: `ErrorCode::TorrentRemoving` after `TorrentRemoved`.

**src/Engine.cpp**: `{"torrent_removing", ErrorCode::TorrentRemoving}` in the word table (:104).

**inc/Engine/State.h**: `Removal`, `removed`, `Working` ×2, `FindWorking` ×2, `EachWorking` ×2, `Removing`, `Extract`. `handles`' comment says listed only. `RemovingOrRenaming`'s comment (:756) becomes "A deletion or rename runs."

**src/Engine/Removal.cpp**:
- `Remove`: the `later` loop (:70-76) calls `Extract(id, mode)`. The rest is unchanged, including the immediate `RemoveHandles(ready, deletion)` (Opus correction 9).
- `Extract`: new, transition 2 of section 2.3.
- `PrepareDeletion`: `torrents.at(id)` (:104) → `Working(id)`.
- `RemoveHandles`: looks each id up in `torrents` first, then `removed`; erases from the map that held it. Keeps the `priorityCompletion`, `FailReading`, `restore`, `remove_torrent`, `handles`/`seedQueries` and resume-file steps for ready ids; for a removed id the first two are already done and the handle steps are no-ops.
- `RemoveDeferred`: scans `removed` grouped by `Removal::mode` instead of `torrents` by `deleted && deletionMode` (:206-208). Same gates.
- `Removing`: `any_of(removed, Overlaps(Hashes(hashes), removal.torrent.Hashes()))`.

**src/Engine/Files.cpp**:
- `RemovingOrRenaming` (:115-120): `!deletions.empty() || rename.has_value()`.
- `ContinueNames` (:136): drop `torrent.deleted ||`.
- Preparation completion (:167): `FindWorking(id)`.
- `QueryFiles` walk (:282): `EachWorking`; completion (:318): `FindWorking(id)`.
- `FinishFiles` (:357): `torrent.deleted` → `!torrents.contains(torrent.torrentId)`. This is the one listed test that remains in file work: three callers reach it (Maintain, `cache_flushed_alert`, the marking and completion callbacks), and a removed torrent must not start a suffix rename. Outside-owner walk (:382): `EachWorking`. `torrents.at(owner.torrentId)` (:425): `Working`.
- `FinishDownload` (:446): drop `torrent.deleted ||`; (:458) `found->second.deleted` goes.
- `ContinueRename` (:490, :503, :547, :585, :598, :603), `EndRename` (:621): `Working`.
- `On(file_completed_alert)` (:629), `On(file_renamed_alert)` (:698), `On(file_rename_failed_alert)` (:734): `FindWorking(alert.handle)`. Marking completion (:688): `FindWorking(id)`.
- `NamesReady` (:811), `FilesReady` (:825), `FileScope` (:846 `Working`; :877 `EachWorking`; :888, :890 `Working`; :911 `torrents.contains(id)` keeps "nobody is told it shares them"; the comment at :909-910 stays, reworded to "A removed torrent still holds its files"), `Holders` (:972, :980 `Working`), `RecoverFiles` (:1001 `EachWorking`).

**src/Engine/Move.cpp**: every `torrents.at(id)` for a move member becomes `Working(id)` (27 sites), because `move->ids` holds only working torrents. Delete: `!deleted` in `MoveWaiting` (:126) and `ResumeMove` (:201); the filters at `PrepareMove` (:263-265) and (:360-361); the drop loop in `ContinueMove` (:389-405); the `deleted` branch at `FailMove` (:605). Keep as `!torrents.contains(id)`: `SaveMove` (:549, relocate in memory without a commit) and `FailMove` (:655, no document entry). `StartMove`'s callback (:106-114) keeps `torrents.at`.

**src/Engine/Session.cpp**: `RestorePending` (:261) tests `Removing(torrent.status.info_hashes)` instead of `torrent.deleted`. Delete the `deleted` tests in `Saved` (:383), `Activity` (:452), `Snapshot` (:531), `Notify` (:623-626, the whole guard: the only kind that reaches a removed torrent is `EndMove`'s Error, which passes today), `RecordHashes` (:662, `torrents.contains` stays), `Maintain` (:761). `FindDuplicate` is unchanged and now correct. `Tick` is unchanged.

**src/Engine/Alerts.cpp**: delete `deleted` at :223, :232, :252, :290. `On(metadata_received_alert)` (:381): `FindWorking`.

**src/Engine/Additions.cpp**: `Add` (:66): before `FindDuplicate`, `if (Removing(preview.InfoHashes())) { completion({ErrorCode::TorrentRemoving}, {}); return; }`. `On(add_torrent_alert)` failure (:482): the same test before `FindDuplicate`, with `Abandon(found->first, {ErrorCode::TorrentRemoving})`. These two handle 540's "preview opened before, confirmed after" case, so it is no longer out of scope. `RecoverAdditions` (:406-408): `accepted` from `EachWorking`.

**src/Engine/Previews.cpp**: `Inspect`: after `merge()` returns false (:211-212), `if (Removing(preview->InfoHashes())) { completion({ErrorCode::TorrentRemoving}, nullptr); return; }`. This is before the guarded `add_torrent`, which libtorrent would refuse as a duplicate. `SharedFiles` (:29): `EachWorking`.

**src/Engine/Commands.cpp**: delete `deleted` at :360, :382, :610 (`found == end` stays), :656.
**src/Engine/Detail.cpp**: delete :76 (`restore` test stays), :119, :196-200.
**src/Engine/Checkpoints.cpp**: delete `torrent.deleted ||` (:147).
**src/Engine/Policy.cpp**: delete :412, :420 (guards), :490, :501, :557 (`!torrent` stays).
**src/Engine/Queue.cpp**: delete :32, :64, :94.
**src/Engine/Watching.cpp**: delete :136.

**src/Resources/en.json** and **app/src/Resources/en.json**: `"torrent_removing": "This torrent is still being removed. Add it again after that if you want it."` beside `torrent_removed`. The app resolves error sentences by code (app `errors` section; Notifications.cs:108 routes `add_failed`); the implementer confirms in the Add dialog that an unknown-to-the-app code shows its sentence.

**docs/contracts/engine.md**: in "Removal and moves", after the Delete files ruling: a removed torrent whose own files are busy is out of the list, the document and every command at once; while libtorrent still holds it, adding the same content is refused as `torrent_removing`; once `remove_torrent` has been called the content can be added again and the Add dialog names the pending deletion. State that a phase-1 removal blocks no other torrent's name preparation. **docs/contracts/pipe-api.md**: `preview` and `add` can fail with `torrent_removing`.

**engine/tests/Checks.ps1**: `RemoveDuringMove`, opt-in, with a `-Volume` parameter (section 6).

Deleted: `Torrent::deleted`, `Torrent::deletionMode`, all 44 reads, the nine joint tests' first half, the `torrents` scan in `RemovingOrRenaming`, `ContinueMove`'s drop loop, the `Notify` guard, `RemoveHandles`'s duplicate reading/priority failure for removed ids (moved to `Extract`).

Explicitly left for later: gate narrowing to path overlap; cancelling the engine's copy on Delete files; `Metadata()`. Rejected: everything else in question 4.

## 5. The responsiveness table

| Wait | Who waits | Required for file safety? | Today | After |
|---|---|---|---|---|
| Remove / Delete files reply | the person, one document write | yes: membership commits before payload is deleted (contract) | commit | same |
| Rows, snapshot, commands on a removed torrent | nobody | — | guards on `deleted` | not in `torrents` |
| Re-add of the same content, phase 1 | the person | a refusal is required: libtorrent holds the first copy | "already in the list" (the defect) or a merge offer | `torrent_removing` at `preview`, `add` and the add alert |
| Re-add, phase 2 | nobody | no: `remove_torrent` erased it from libtorrent's map | accepted; Add dialog names the deletion | same |
| The removed torrent's own release | its deletion's disk work | yes for its move, rename, preparation and Recovering (names); marking kept at no cost | `CanRemove` | same |
| Edit reply for file priorities; held detail read | the window | no | until release (minutes) | at `Extract` |
| Other torrents' name preparation and new additions' names, phase 1 delete mode | new and restored torrents stay paused without names | no: nothing is read or written in phase 1; the both-names comparison at release keeps shared files | blocked by `RemovingOrRenaming` for the length of the removed torrent's move | not blocked |
| Other torrents' finishing renames, phase 1 delete mode | completing torrents keep `.!tt` names | no, same reason | blocked through `FilesBusy()` | not blocked |
| Other torrents' moves while a removed torrent's own files are busy | the Move waits, shows Moving | yes while its move or rename runs (`move`, `rename`); yes for its own busy files (owner's "one payload operation" rule) | `FilesReady` | same, through `EachWorking` |
| Name preparation, finishing renames, moves and restores, phase 2 | same as today | not for safety where paths do not overlap; the owner's ruling accepts the names wait | `!deletions.empty()` | same; narrowing is separate work |
| A conflicting restore | until its twin is released | yes: libtorrent refuses while the twin is loaded | `FindDuplicate` sees the held twin | `Removing` sees it; no retry loop |
| Exit | the person, window hidden (ruling) | yes for `move`, `deletions`, `rename`; a phase-1 delete-mode torrent whose own work is preparation or marking releases within milliseconds into a `Deletion`, which then holds; a keep-files phase-1 torrent never held | `FilesBusy()` | same; the contract's "if the engine stops before removing it, its files stay on disk" covers the rest |
| A Delete files during the current member's cross-volume copy | the deletion, minutes | no for the copy itself; `Payload::Move` has no external cancel | whole copy | same; separate work |

## 6. The regression proof

| Transition | Existing check (Checks.ps1 at 0e64a61) | Evidence it gives |
|---|---|---|
| Listed → `Deletion` at commit, keep files; no resurrection | `RemoveKeepFiles` (:1078, `-Transfer`), `History` (:304) | payload kept, row gone after restart, `torrent_removed` reply |
| Listed → `Deletion` at commit, delete files, shared file kept; refused commit keeps membership; move refused for shared files | `FilesSafety` (:491) | `kept_files`, survivor's bytes unchanged, `storage_failed` leaves two rows |
| Deletion waits on another removal's hold (phase 2) | `FilesSafety` | deferred cleanup after release |
| Conflicting restore → listed; twin removed at commit | `Restore` (:203) | survivor obtains its handle, hashes kept |
| Group move, interrupted move, collision | `FilesSafety`, `FileNames` (:386) | markers, `moving`, bytes |
| Name preparation; suffix rename retry with a held file; shared owners | `FileNames` | names and bytes after retry |
| Waiting move starts after metadata | `WaitingMove` (:1157, `-OptIn`) | reply at once, one toast, starts later |
| Listed → `removed` (phase 1) → `Deletion`; `torrent_removing`; detail read ended at `Extract` | none | — |

New check, one: **`RemoveDuringMove`**, opt-in, refuses without `-Volume <folder on another volume>`, because a move on one volume is a rename that ends in milliseconds and I found no deterministic one-volume way to hold phase 1 (Opus found none either; Astra's gate is rejected). The failure it watches: a torrent removed while its move runs is left half moved, its files are deleted under the move, it reappears after restart, the engine crashes on an open detail read, or a re-add during the hold is reported as "already in the list". Steps: build a 2 GiB fixture (as `FilesSafety` builds its own), add it paused with `session_pause`, open a `torrent` read with a context on it, `move` it to a folder under `-Volume`, poll `snapshot` until the row is `moving` with progress below 1, then in one burst: `delete_files` (assert ok), `snapshot` (assert no row), `preview` of the same fixture (assert `torrent_removing`), `torrent` read (assert `torrent_removed`). Then poll until `preview` succeeds, assert the destination holds no payload and the source folder holds none, restart and assert no row. A second run with `remove` instead of `delete_files` asserts the payload bytes at the destination equal the fixture. A 2 GiB cross-volume copy lasts well over the burst's few round trips on the owner's laptop; if a machine copies it inside the burst, the check reports the window as missed rather than passing, so a pass is evidence.

Dropped: fable.md's `RemoveWaitingMove` (a removed torrent's waiting move cannot start by construction, `MoveWaiting` walks `torrents`; Opus is right), fable-best's `RemoveBeforeNames` (no `Capturing` phase), Astra's checkpoint-ordering and move-restart cases (not this change).

Verified only by reading: a removed rename owner (milliseconds); a removed Preparing, Recovering or marking torrent; the metadata-received path for a removed magnet; `RestorePending` against a held twin; `RecoverAdditions`'s `accepted` set; the `deleted && restore` removal of a conflicting restore during an addition. Each has its line in section 4 and its reason in section 3.

## 7. Risks

- A file-work site left on `torrents.find` returns early for a removed torrent, and its busy flag never clears: that removal and every deletion behind it stall until restart. The sites are listed in section 4 (Files.cpp:167, :318, :688 and the four handlers). A site left on `torrents.at` throws out of `Tick`, which nothing catches (Engine.cpp:201). The review pass must trace every `torrents.` access in Files.cpp, Move.cpp and Removal.cpp against the rule in question 2.
- The dropped phase-1 clause changes when other torrents prepare names. The argument is the both-names comparison at release; `FilesSafety` pins the kept-file outcome. If the owner wants the old wait back, it is two lines in `RemovingOrRenaming` (`any_of(removed, mode)`), nothing else moves.
- `move->ids` must hold only working torrents. `Extract`'s erase and index adjustment is the one place that keeps this; `FailMove`'s rotate (Move.cpp:615) and `CleanMove`'s `++current` depend on it.
- The working tree already edits these files (Notice → Toast). Build on it, not on 0e64a61.
- Phase 1 for a move is seconds to minutes and the only exercised proof needs a second volume. Everything else in phase 1 is milliseconds and rests on reading.
