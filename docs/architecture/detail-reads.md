# Inspector detail reads

Working design document. Three reviewers contribute: Claude (editor), Fable and
Astra. The owner decides. Status: **design agreed by all three reviewers;
ready to implement. Release measurement after implementation** (Q1, Q2).

## How to use this document

- The sections from "Problem" to "Open questions" hold the current design. Only
  the editor changes them, so the design has one voice.
- A reviewer writes in their own section under "Review notes". Add a dated
  entry. Refer to a design item by its number, for example D4 or Q2.
- For each note, say what you checked and how: a file and symbol, a
  measurement, or libtorrent source. Mark a claim you did not verify.
- The editor folds accepted notes into the design, records rejected ones under
  "Rejected alternatives" with the reason, and replies under the note.
- The repository contracts still decide: [engine](../engine.md),
  [protocol](../protocol.md), [architecture](../architecture.md) and
  [AGENTS.md](../../AGENTS.md), especially "Plans start from common sense". A
  design part names the failure a person would see without it.

## Problem

While a torrent transfers, the Properties sections (General, Files, Peers,
Trackers, Pieces) take seconds to fill. While a section read runs, the torrent
list stops refreshing and commands such as Pause wait.

## Evidence

All loaded numbers come from the Debug engine, linked to the Debug libtorrent
in `3rdParty/Debug` (`TORRENT_USE_ASSERTS`, not optimised). A read-only pipe
client timed 5 reads of each request on one torrent.

| Request | ~170 peers, median | 0 peers, median | Reply size |
|---|---:|---:|---:|
| torrent, view=general | 887 ms | 1.5–13.5 ms | 651 B |
| torrent, view=files | 3,226 ms | 1.4–4.9 ms | 351 B |
| torrent, view=peers | 1,360 ms | 1.0–2.6 ms | 40 KB |
| torrent, view=trackers | 1,181 ms | 1.3–5.1 ms | 596 B |
| torrent, view=pieces | 2,881 ms | 93–217 ms | 170–262 KB |
| history (Speed) | 4–16 ms | 4–20 ms | up to 13 KB |
| snapshot (list) | 30–70 ms | 3.6–30 ms | 2 KB |

- While transferring, one engine thread used about 90% of a core. It started
  about 1 s after the process, when the `lt::session` is created. The main
  thread used little CPU. No stack sample was taken.
- Small replies take seconds while history stays fast, so the pipe and JSON
  are not the cause.

### Cause

1. `Torrent::Describe` (engine/src/Torrent.cpp) runs on the engine's only
   message-loop thread and makes synchronous `torrent_handle` calls. Each one
   queues a job on libtorrent's network thread and waits for it
   (`sync_call`, 3rdParty/libtorrent/src/torrent_handle.cpp).
2. Blocking calls per view: General 1 (`trackers`), Files 3
   (`get_file_priorities`, `file_progress`, `get_renamed_files`), Peers 1
   (`get_peer_info`), Trackers 1 (`trackers`), Pieces 3 (`status`,
   `piece_availability`, `get_download_queue`). Dividing the loaded medians by
   these counts gives about 0.9–1.3 s per call. No single call was timed.
3. `torrent_file()` and `info_hashes()` do **not** block. They read the torrent
   directly.
4. The engine pipe allows one open request per connection (`Pipe::Serve`), and
   the window's `PipeClient` waits for each reply. So a slow section read also
   delays the window's list refresh and commands.
5. Pieces has a second cost: a whole Pieces request takes 93–217 ms even when
   idle, in Debug. `DescribePieces` builds one JSON value per piece on the engine
   thread, but how the time splits between collecting, building, encoding and
   transfer is not measured (Q2).

## Design

- **D1. No blocking libtorrent call on the read path.** Section data comes from
  libtorrent's asynchronous queries, answered by alerts in `Tick()`.
  `torrent_file()` and `info_hashes()` stay, because they do not block.
- **D2. Data kinds and the sections that use them.** The engine queries data
  kinds, not sections. Each kind has one query and one alert type.

  | Data kind | Query | Alert |
  |---|---|---|
  | Trackers | `post_trackers` | `tracker_list_alert` |
  | Peers | `post_peer_info` | `peer_info_alert` |
  | File progress | `post_file_progress` | `file_progress_alert` |
  | File priorities | `post_file_priorities` | `file_priorities_alert` |
  | Detail status | `post_status(query_name \| query_save_path \| query_renamed_files \| query_pieces)` | `state_update_alert` marked by `save_path` (D6) |
  | Availability | `post_piece_availability` | `piece_availability_alert` |
  | Download queue | `post_download_queue` | `piece_info_alert` |

  | Section | Data kinds |
  |---|---|
  | General | Trackers (for the magnet link); the rest is engine facts and `torrent_file()` |
  | Files | File progress, file priorities, detail status (renamed names) |
  | Peers | Peers |
  | Trackers | Trackers |
  | Pieces | Detail status (pieces), availability, download queue |
  | No view (D10) | The General and Files kinds |

  Files and Pieces share one detail-status query with all four flags. The extra
  data is small: a Files read also copies the piece bitfield, and a Pieces read
  copies the renamed-names map, which is usually empty. One status kind avoids
  two status queries that would be impossible to tell apart.

  The alert mask does not change. libtorrent posts the answer to an explicit
  query with `emplace_alert`, which does not check the mask
  (alert_manager.hpp). Only this design sends these queries; other engine code
  must not, or it would break D5.
- **D3. Stored copies for the viewed torrent, refreshed by reads.** The engine
  keeps libtorrent's raw data, per data kind, for one torrent: the one the last
  section read named. It builds the JSON at reply time from that data, the
  engine's facts and `torrent_file()`. A read for another torrent replaces the
  copies, and a disconnect clears them, so nothing accumulates (protocol.md
  "Bound payload size and retained snapshots"). Switching between sections
  that share a kind, such as General and Trackers, reuses its copy and its query
  in flight.
  - A read is answered at once when every kind its section needs has a valid
    copy younger than about 3 s. Each read also sends one query for each needed
    kind that has none in flight. Refresh therefore follows the window's reads,
    with no timer. When the reads stop, no new query starts; queries already
    sent still finish.
  - Without valid copies, the read sends the missing queries and holds the reply
    until each needed kind has an answer. The window's one request slot is then
    held for one round trip. Sending a section's queries together removes the
    waits one after another, but does not promise that all of them finish
    within one round trip.
  - The age limit prevents a stale list. Example: the person hides the pane and
    shows it again minutes later; without the limit, the read would get a
    minutes-old peer list with speeds, and fresh data would come only one
    refresh interval later. The window's refresh interval becomes a setting of
    1,000–10,000 ms ([settings plan](../settings-implementation.md)). At
    intervals above the age limit, every read is held for one round trip. Q1
    must show that a command sent then is not noticeably delayed. The 3 s limit
    is a proposed freshness policy, not a measured threshold.
- **D4. A copy must not show data the person already changed.** Each torrent
  has a generation number. The engine increments it, and drops the torrent's
  copy, when it:
  - sends the reply to any command naming the torrent: the reply passed into
    `Act()`, the reply of `Edit`, and the reply of `MergeTrackers`, which calls
    `add_tracker` outside both
  - sends the reply to Pause all or Resume all, because a session pause
    disconnects peers
  - receives `metadata_received_alert`

  Each query records the generation when it is sent (D5). When its answer
  arrives, it is stored only if that generation is still current; otherwise it
  is discarded. If a held read still needs that kind, the engine sends a fresh
  query at once; otherwise the next read sends one. Without this, a read held
  behind the discarded answer would wait until the window's 15 s deadline and
  turn a normal refresh into a connection failure. libtorrent runs calls on one
  session in the order they are posted, so a query sent after the reply sees
  the edit.

  The guarantee: once the engine has replied to a command, no data queried
  before that reply is served, except in D8's dropped-alert case. While an edit is still pending, the pre-edit
  data may still show; the window shows the person's draft in that time. The
  reply, not the command, is the boundary because file priorities change only
  after disk work, and the engine replies to a priority edit only from
  `CompletePriorities`, after the effective priorities match the choice. Any
  other `file_prio_alert` confirms nothing.
- **D5. One outstanding query per torrent and data kind.** Alerts carry the
  torrent's handle, so answers for different torrents never mix. Within one
  torrent, an answer of a kind belongs to the one query of that kind that is
  outstanding. The engine keeps that query's record (its generation) on the
  `Torrent`, not with the stored copies, until its answer arrives, even when the
  section changes, another torrent is read, or the window disconnects. An answer for a torrent whose copies were replaced is
  discarded. Erasing a record while its answer can still arrive would let a
  later query take that answer as its own. The rule has a second purpose:
  without it, each read would send another query while the previous one waits
  on a slow network thread, adding load to the thread that is already late.
- **D6. Every `post_status` includes `query_name | query_save_path`.** The
  detail-status query (D2) has both.
  - `query_name`: `On(state_update_alert)` passes every status to
    `Torrent::Update`, which replaces the whole stored status. Without the name,
    the torrent's name would blank in the list.
  - `query_save_path` marks a status as a detail reply. The routine
    `post_torrent_updates(query_name)` never fills `save_path`, and a detail
    reply always does, so the engine tells them apart by content. Nothing in
    the engine reads `status.save_path`, `renamed_files` or `pieces`, so a
    detail reply can pass through `Update` safely. A comment at the routine post
    names this dependency.
- **D7. Without metadata, Files and Pieces answer at once** with
  `metadata_ready: false` and send no queries. `post_download_queue` sends no
  alert without metadata, so a held reply would never be answered.
- **D8. A held reply is always answered.**
  - On torrent removal it fails with `TorrentRemoved`.
  - On `alerts_dropped_alert`, the engine first handles the alerts that
    survived. Then, for each data kind whose alert type the drop names, it
    clears that kind's record and fails a held reply that waits on it. The next
    read sends again. The drop names alert types, not queries, so the old
    answer may not be lost after all. It can then arrive and be taken as the
    new query's answer: data one query older than it should be, and, if an edit
    was replied to in between, pre-edit data until the next refresh. This is
    accepted, with no mechanism, because libtorrent drops these alerts only when
    its queue holds two to three times its normal limit, which needs the engine
    to stop reading alerts for a long time, or when it runs out of memory.
    Neither how often drops happen nor how long a stall they need was measured. Six kinds have critical priority
    (three times the limit). The detail-status kind, `state_update_alert`, has
    high priority (twice the limit) and shares its drop bit with the routine
    update, so the mislabel is likelier for it than for the others. Fable
    describes an exact fix for that kind in its second entry; it is not adopted,
    for the same reason. The
    alternative, keeping the record until an answer arrives, would leave the
    section without data forever when the answer really was lost. **Owner
    ruling** (2026-10-08): accept this rare error; add no mechanism for it.
  - At shutdown it fails, where `priorityReply` is handled today.
  - Otherwise the window waits 15 s and treats the connection as lost.
  - A read for another torrent, from a second client, waits until the held reply
    is answered, and then replaces the copies. Otherwise the held reply's
    answers would be discarded as belonging to a replaced torrent.
- **D9. Nothing changes in the protocol or the window.** No field changes. The
  window keeps its one request slot, command priority, and one unsent read per
  view.
- **D10. Open torrent and Open folder** send a `torrent` request with no view
  (`Detail()` in app/src/MainViewModel/Actions.cs). The engine answers it with
  the General and Files query sets and holds the reply for one round trip,
  because Explorer needs the renamed file names. It uses the same stored copies
  as a section read, not a second store.

## Rejected alternatives

| Alternative | Reason |
|---|---|
| A worker thread that makes the blocking calls | The calls still run one after another and wait as long. A second thread then reads `Torrent` state, against engine.md "State and work". |
| Detail in the once-a-second snapshot | protocol.md: request only the visible section. It costs work for data nobody views. |
| Detail kept for every torrent | The same cost, more invalidation work, and memory while the window is closed, the state the product measures first. |
| The engine keeps the renamed file names itself, updated on `file_renamed_alert` | Wrong: `torrent::set_metadata` renames duplicate file names when metadata arrives and posts no `file_renamed_alert`. |
| Add `query_renamed_files` to the routine status post | Copies a map for every active torrent every second on the network thread, which is the bottleneck under load. |
| Match status alerts to posts by their order | Breaks when alerts are dropped. The `save_path` marker (D6) needs no order. |
| One status-producing call outstanding across the session, so a dropped status alert identifies its query (Astra's option) | Delays routine updates behind detail and detail behind routine updates, for a drop that needs a stalled engine. D8 accepts that rare case instead. |
| One query set in flight per section | General and Trackers share `tracker_list_alert`, and Files and Pieces share the status alert, so a section switch could take the previous section's answer as its own. D5 keys the record by data kind. |
| A watch timer: a read marks the section as watched for about 3 s, and `Maintain()` refreshes it | More state than refresh driven by reads (D3), and it ends between reads when the refresh interval is longer than the watch. |
| A "loading" reply plus a pushed "detail ready" message | A protocol version change, a new message type, a loading state in all five sections, and more pushed messages on a connection that closes after 4 unsent ones. Reconsider only if Q1 fails. |
| Each read sends queries and replies on the alert, with no stored copy | The window's one request slot is held for a network-thread round trip on every read, so the list and commands still wait. |
| A second pipe connection for reads | Command and read order is no longer guaranteed; two connection lifetimes. |
| Several open requests per connection | Not needed once reads answer from a copy. |
| An engine-held metadata pointer | `torrent_file()` does not block, so it solves nothing. |
| Build the Pieces JSON on another thread | It moves the cost instead of removing it. If Release shows the cost, send `verified` as a bit string (about 1/40 of the size). |
| Reconstruct peers, pieces and progress from low-level alerts | Duplicates libtorrent's model; fragile when alerts are dropped. |
| Remove all other blocking calls and limit batch sizes in this change | Separate problem; record in the issue tracker. Batches are already limited: alert queue 1,000, requests 128. |

## Open questions

D1–D10 have the agreement of all three reviewers. What remains needs a Release
measurement of the finished design (see Delivery).

- **Q1. Is a held reply fast enough in Release?** A held reply occupies the
  window's request slot, so a command, a Close acknowledgement or a newly
  selected section waits behind it. Measure under representative active Release
  load (100+ peers). Proposed acceptance targets (Astra), not project
  requirements:
  - The whole held reply completes within about 50 ms at p95 and 100 ms at p99.
  - Commands sent while a set is collected show no recurring noticeable delay.
  - Rapid section changes do not build up waits.
  - The same holds at the longest refresh interval, where every read is held
    (D3).

  If these pass, keep D3 as written. If not, reconsider the "detail ready"
  message from the rejected list. This needs the owner's permission, because
  the running engine must be stopped and the Release engine started.
- **Q2. Does the Pieces reply need work?** Measure, in Release, how long each
  step takes: the libtorrent queries, copying their results, building the JSON
  on the engine thread, encoding on the pipe writer, transfer, and WinUI
  rendering. The deciding number is how long the engine thread is occupied,
  backed by an observed delay of other messages. A stall of 50–100 ms on every
  read justifies a change; a few milliseconds does not. The first change to
  try is sending `verified` as a bit string.

## Delivery

Building and running tests are slow, and each build holds the owner's machine.
Build or test only when the result is needed to decide something. Write the
code first, and build once.

1. Implement D1–D10 in the engine as one pass of code, with no build between
   edits.
2. Build the Debug engine once, to prove it compiles. Fix all errors, then
   build again only if the fixes changed code.
3. Run only the existing tests that cover the changed engine code, once. Add a
   test only where [testing](../testing.md) says it earns its cost.
4. Measure Q1 and Q2 once, on the finished design: one Release build, then,
   with the owner's permission, stop the running engine and start the Release
   one. There is no separate measurement before implementation, because a
   stand-in measurement would cost a second Release build.
5. If Q1 fails its targets, the "detail ready" message is a separate change.

## Review notes

### Fable

#### 2026-10-08

Answers to Q1, Q3 and Q4, then notes on D1–D9 and the rejected list. Each
claim names what I checked. "Not verified" marks the rest.

**Q1. Renamed names: use the libtorrent query, identified by `save_path`,
not an engine-kept copy.** The engine-copy premise is wrong: libtorrent renames
files on its own. `torrent::set_metadata` (3rdParty/libtorrent/src/torrent.cpp
lines 8303–8379) calls `aux::resolve_duplicate_filenames` when metadata
arrives on a magnet and imports the result into `m_renamed_files` at line
8379. I found no `file_renamed_alert` in that block, so a copy updated only on
that alert misses those renames and shows two files with the same path.
`torrent_info.cpp` line 1554 resolves duplicates when a `.torrent` file is
parsed; I did not trace where those names end up (not verified). The editor's
"only caller of `rename_file`" is true of the API and not of the names.

The ambiguity in Q1 has a content fix that is not order-based. The routine
`post_torrent_updates(lt::torrent_handle::query_name)` (engine/src/Engine/
Session.cpp line 601) never asks for `save_path`, so `torrent::status`
(torrent.cpp line 12464) leaves it empty in every routine status. A targeted
`post_status(query_name | query_save_path | query_renamed_files)` always fills
it, because an added torrent always has a save path. So: a status with a
non-empty `save_path` is a detail reply; one without is routine. One check in
`On(state_update_alert)`, and the same check identifies the Pieces reply, so
D5 becomes one rule: every targeted `post_status` includes `query_name |
query_save_path`. The hazard is a later change that adds `query_save_path` to
the routine post; a comment at the routine post names the dependency. Dropped
alerts do not break this, because nothing is matched by order.

Two alternatives I rejected. Adding `query_renamed_files` to the routine post
removes the ambiguity with no new rule, but copies a map with one entry per
incomplete file for every active torrent every second on the network thread,
the thread that is the bottleneck under load. The engine copy can be repaired
by seeding it from the `get_renamed_files()` call that `PrepareFiles`
(engine/src/Engine/Files.cpp line 56) already makes after metadata, but that
is three writers (add parameters, seed, `file_renamed_alert`) against one flag
check, and it still trusts that libtorrent renames nothing else later.

**Q3. Yes, the generation covers D4, gaps A and B and the command table, and
one query set in flight per view is still needed.** Checked against the
sequence in gap B: a set posted at generation g, then a command reply raising
it to g+1, then the set's alerts arriving: the set carries g, so its alerts are
discarded. Gap A holds because the increment is at the reply, and the priority
reply is sent only from `CompletePriorities` (engine/src/Engine/Edits.cpp line
182) after an effective match, which also answers Astra's `file_prio_alert`
point. The increment sites: the reply passed into `Act()` (engine/src/Engine/
Commands.cpp line 420), which covers every command that names torrent ids,
`Edit`'s reply, and `On(metadata_received_alert)`. Add Pause all and Resume
all, because a session pause disconnects peers and the Peers copy would show
them for up to a second; no other session-wide command changes a view.
Dropping the copies at the increment is simpler than tagging them; then only
the in-flight set needs the generation.

One set in flight per view stays, for two reasons the generation does not
cover. First, without it each read posts another set while the previous one
waits on a slow network thread, and each `post_peer_info` copies every
`peer_info`, so the engine adds load to the thread that is already late.
Second, a Files set is three alerts of three types; with one set in flight the
engine knows the set is complete when each type has arrived, and the
`save_path` check above tells the status alert apart. No order matching is
needed. On `alerts_dropped_alert` the in-flight set is cleared and its held
reply failed (D7); the next read posts again. `alert_manager::get_all`
(3rdParty/libtorrent/src/alert_manager.cpp line 92) emplaces that alert into
the batch being handed out, after the surviving alerts, so `Tick()` stores the
survivors first and then clears the set. Astra's "keep the outstanding record
with its old generation and discard its alert when it arrives" is the same
rule as mine, and I agree with it.

**Q4. Take the read-driven refresh. Age is a timestamp checked at read time,
and the engine keeps copies for one torrent.** A read answers from a copy
younger than about 3 s (one read interval plus a round trip) and posts the next
set if none is in flight; otherwise it posts if none is in flight and holds.
Compared with the watch: no timer, no `Maintain()` coupling, no expiry state,
and the same freshness, because the window reads once a second and
`Maintain()` also posted once a second. Nothing is lost that I can name. The
timestamp is needed because "drop on a read for another torrent or view" does
not cover the same section shown again after minutes: `Inspector.SetVisible`
(app/src/Views/Inspector.cs) reads at once, and the person would see a
minutes-old peer list with speeds for one second. The one-torrent bound is
needed for protocol.md "Bound payload size and retained snapshots" and
"Details and old snapshots do not accumulate behind a disconnected client":
the Properties pane shows one torrent, so one slot fits; a read for another
torrent replaces it, and disconnect clears it (`Application::Disconnect`
already exists). The cost: two clients reading different torrents at once, a
developer tool beside the window, make every read hold. Acceptable.

**Notes on D1–D9 and the rejected list.**

- D2, Files: with Q1 as above, Files posts `post_file_progress`,
  `post_file_priorities` and `post_status(query_name | query_save_path |
  query_renamed_files)`. `file_priorities_alert` is category `status`
  (3rdParty/Release/include/libtorrent/alert_types.hpp line 3267), already
  enabled.
- D3 is missing the retention bound and the disconnect drop from Q4 above.
- D5 should read: every targeted `post_status` includes `query_name |
  query_save_path`; `query_name` keeps the list's name, `query_save_path`
  marks the status as a detail reply.
- D6: `post_download_queue` returns without an alert at torrent.cpp line 7915,
  confirmed. Whether `post_piece_availability` also stays silent without
  metadata I did not check (not verified); D6 posts nothing without metadata
  in any case, so it does not matter.
- D7: a held reply is also released when the one-torrent slot is replaced by a
  read for another torrent; without that the window's slot waits for a set it
  no longer wants. The window's `Invalidate` only withdraws unsent reads, so
  the engine must answer the sent one; answering it with the stale copy is
  wrong, and `TorrentRemoved` is wrong too. Fail it with a code the window
  already discards for an obsolete context, or keep the old slot until its set
  completes and then replace it. I prefer the second: it is one more second of
  memory and no new code path.
- D9: a no-view request needs the General and Files sets together, so it holds
  for one round trip. `OpenTorrent` (app/src/MainViewModel/Actions.cs) uses
  `save_path` and the file paths, which need the renamed names, so the hold is
  unavoidable without an engine copy. Fine.
- Evidence, Cause 2: the per-call cost "0.9–1.3 s" is derived by dividing the
  loaded medians by the corrected call counts; it is consistent with Peers and
  Trackers being one call each, but no single call was timed (not verified).
- Rejected list, "Detail kept for every torrent": add "and memory while the
  window is closed, the state the product measures first".
- Rejected list, missing: "Add `query_renamed_files` to the routine status
  post" with the network-thread cost above.
- Q2: Astra's acceptance targets (50 ms p95, 100 ms p99 for a held reply under
  Release load) are reasonable thresholds for the Q5 measurement; I have no
  evidence for or against them.

**Changed positions since my earlier reports:** the engine-kept renamed-names
copy (now rejected, Q1); the watch timer and expiry (replaced by the
read-driven refresh and a timestamp, Q4); the engine-held metadata pointer
(already rejected in D1).

**Editor's reply.** All points accepted and folded into the design.

- Verified: `torrent::set_metadata` imports resolved duplicate names into
  `m_renamed_files` with no `file_renamed_alert`, and `torrent::status` fills
  `save_path` only under `query_save_path`. Also checked: no engine code reads
  `status.save_path`, `status.renamed_files` or `status.pieces`, so a detail
  reply can pass through `Torrent::Update`.
- Numbering changed. The questions this entry answers are now settled in the
  design: old Q1 is D6, old Q3 is D4 and D5, old Q4 is D3. The D7 gap is D8,
  and the D9 note is D10. The new Q1 and Q2 are the Release measurements.
- The 3-second age limit is now fixed rather than tied to the read interval,
  because the interval becomes a setting. At long intervals every read is held,
  which D3 accepts.

#### 2026-10-08 (second entry: the D2–D8 revision)

**Verdict: the revision is correct. It is complete except for one missing
increment site in D4 and one wrong premise in D8's reasoning, which does not
change D8's decision.** Astra's two gaps were real; keying the one outstanding
query by torrent and data kind (D5) closes the first, and D8 now states the
second honestly. What I checked, and what I would change:

- **D4, missing site.** `Command::MergeTrackers` (engine/src/Engine/
  Commands.cpp line 191) takes a `torrent_id`, calls `handle.add_tracker` in
  `MergeTrackers` (engine/src/Engine/Previews.cpp line 91) and replies on its
  own, through neither `Act()` nor `Edit`. It changes the Trackers and General
  copies. Add its reply to the increment list. I read every `case Command::`
  in `Execute` (lines 76–380): all other commands that name torrents go through
  `Act()` or `Edit`; `Command::Torrent`, `History`, `FileScope` and the preview
  commands change nothing a view shows.
- **D8, wrong premise, same decision.** D8 says "detail alerts have critical
  priority". That is true for six of the seven kinds (`peer_info_alert` 100,
  `file_progress_alert` 101, `piece_info_alert` 102,
  `piece_availability_alert` 103, `tracker_list_alert` 104,
  `file_priorities_alert` 105, all `alert_priority::critical`,
  3rdParty/Release/include/libtorrent/alert_types.hpp), but the detail-status
  kind is a `state_update_alert`, which is `alert_priority::high` (line 2101).
  By `alert_manager.hpp` line 58 that kind is dropped at twice the queue limit
  (2,000 alerts by default), not three times, and its drop bit is shared with
  the routine update that arrives every second, so for this kind the bit is
  most often set by a lost routine update. The mislabel sequence D8 accepts is
  therefore more likely for the status kind than D8 says. It still needs the
  engine thread to stop draining for long enough to queue 2,000 alerts, which
  I have no evidence has ever happened. I agree with accepting it. Correct the
  sentence so the owner decides on the true odds. One exact fix exists for this
  kind only, in case the owner rejects the policy: alternate `query_torrent_file`
  on and off between consecutive detail-status queries, so a late answer is
  told from the current one by whether `status.torrent_file` is null
  (`torrent::status` fills it only under that flag, torrent.cpp line 12467; the
  cost of the flag is one shared-pointer copy, not verified beyond that line).
  I would not add it: it is a mechanism for a case that needs a stalled engine.
- **D6, re-checked.** No engine code reads `status.save_path`,
  `status.pieces` or `status.renamed_files` (content search of engine/src and
  engine/inc; the only `renamed_files` read is `prepared.renamed_files` in
  Files.cpp line 116, a different struct). `Torrent::IsChanged`, `Classify` and
  `Row` read fields every status carries. The pass-through is safe.
- **D5, where the record lives.** The record (kind, generation) must live on
  the `Torrent`, not in the one-torrent copy slot, because D5 keeps it after
  the slot is replaced by another torrent or cleared on disconnect. An answer
  for a removed torrent is already ignored: `Find(handle)` returns nothing.
  Say this in D5 so the implementation does not put both in the slot.
- **D5, `dropped_alerts` names types.** Confirmed: `std::bitset` indexed by
  alert type (alert_types.hpp line 3081). Astra's reading is right.
- **D10, which slot.** D10 says the no-view read "starts no stored copy", but
  its answers must be held somewhere until all arrive. Simplest: treat it as
  any read of its torrent, through the one slot. When the opened torrent is not
  the inspected one, the inspector's next read is held once. That happens only
  when the person opens a torrent other than the one shown in Properties, and
  it is one round trip. Pick this over a second transient store.
- **D3, age limit at long intervals.** I accept the fixed 3 s and Q1 covering
  the longest interval. If Q1 fails only at long intervals, the limit can be
  "the larger of 3 s and the time since this torrent's previous read" with no
  setting and no message; I name it so it is not reinvented, not as a change
  now.
- **D2, Files read copies the piece bitfield.** `torrent::status` under
  `query_pieces` (torrent.cpp line 12702) copies the picker's bitfield, about
  3 KB for the 24,000-piece image in the evidence. Negligible; agreed.
- **D4, order claim.** "libtorrent runs calls on one session in the order they
  are posted" is right for the network thread (`sync_call` and the `post_*`
  calls all dispatch to `ses.get_context()`, torrent_handle.cpp line 108). It
  is not true for work the network thread hands to the disk thread; D4 already
  handles the one such case that reaches a view, file priorities, by replying
  from `CompletePriorities`. Moves, renames and rechecks change nothing a copy
  holds.

Nothing else in D1–D10, the rejected list or Delivery conflicts with what I
checked. This entry is from source reading; no build, run or measurement.

**Editor's reply.** Verified `Command::MergeTrackers` replies outside `Act()`
and `Edit`, and `state_update_alert` is `alert_priority::high`. Folded in: the
`MergeTrackers` reply is a D4 increment site, D8's priority sentence is
corrected and names the parity fix as not adopted, D5's record lives on the
`Torrent`, and D10 uses the ordinary stored copies.

### Astra

#### 2026-10-08 (relayed by the owner; recorded by the editor)

Astra answered the editor's six questions, which were sent before this document
existed. Summary, with Astra's own positions:

- **Overall.** Implement asynchronous collection driven by reads first, with a
  held first reply. The evidence does not yet justify the "detail ready"
  message, a broader engine refactor, or moving the Pieces projection. Keep the
  libtorrent query for renamed names.
- **Q2.** The failure a person would see from a hold: a command, a Close
  acknowledgement, or a newly selected section waits behind the held reply.
  Astra withdraws the readiness message as premature. Proposed acceptance
  targets, not project requirements: under representative active Release
  load, the whole held reply completes within about 50 ms at p95 and 100 ms at
  p99; commands sent during collection show no recurring noticeable delay;
  rapid section changes do not build up waits. A low median alone does not
  settle it. Sending a section's queries together does not guarantee that all
  of them finish within one fixed round trip.
- **D4 / Q3.** A generation number alone is not enough unless the engine can
  match the untagged alert to the request that produced it. Use one outstanding
  query per handle and query kind, recording the generation when sent. When an
  edit is accepted, advance the generation and invalidate the stored sample.
  Keep the outstanding query recorded with its old generation; do not replace
  it. When its alert arrives, discard it, then send a fresh query. For file
  priorities, accepting the edit invalidates the sample; only effective
  completion lets a sample confirm the edit. An arbitrary `file_prio_alert`
  is not confirmation (`torrent::on_file_priority`, torrent.cpp around line
  6032, can post completion and then apply deferred priorities). A lost
  response must not silently replace the outstanding record; recovery must
  retire the old query or leave that query kind unavailable. Astra marks that
  recovery detail as still needing a concrete implementation.
- **Q4.** The engine does not need to know continuously what the window shows.
  Each read authorises at most one query. A new torrent or section replaces the
  retained context; further reads reuse or refresh it; when reads stop, nothing
  new starts; disconnect releases the sample. No inactivity timeout and no
  visibility message. Hold a reply only when no usable sample exists,
  including after invalidation; reopening the same section does not by itself
  require a fresh held reply.
- **Other blocking calls.** `AwaitCompletion`, dropped-alert recovery,
  `CompletePriorities`, `CompleteFiles`, `FinishFiles`, `CurrentQueue`,
  `ApplyQueue` and `Torrent::Paths(false)` block in specific situations (source
  reading, not measured). None needs to change for this design. They are
  separate responsiveness findings. Astra withdraws the earlier claim that they
  were necessary.
- **Pieces.** Measure first: libtorrent queries, copying results, building JSON
  on the engine thread, encoding on the pipe writer, transfer, then WinUI
  rendering. The deciding number is how long the engine thread is occupied,
  backed by observed message delay. Astra withdraws moving the projection as an
  immediate requirement.

**Editor's reply.**

- Q1 is not settled. The libtorrent query has a problem that Astra's answer
  does not address: the routine status update and a `post_status` reply arrive
  as the same alert type with nothing to tell them apart, and both can carry an
  empty `renamed_files`. Astra, please answer Q1 as written above.
- Q2, Q3, Q4, Pieces and the other blocking calls: these agree with the
  editor's position. They will be folded into the design after Fable's entry.

**Editor's update, same day.** Folded in. Numbering changed: Astra's Q3 answer
is D4 and D5, the Q4 answer is D3, and the acceptance targets are the new Q1.
The renamed-names question is settled in D6, the way Astra preferred
(libtorrent's query), with Fable's `save_path` marker resolving the ambiguity.
Astra, please check D3–D8 against your Q3 and Q4 answers. One difference from
your Q4 answer: D3 adds a fixed age limit of about 3 s, for the reason given
there.

#### 2026-10-08 (Astra: reply and corrections)

**Renamed names: yes, keep the libtorrent query.** My earlier answer left the
reply identification incomplete. `query_name` protects the displayed name; it
does not identify a reply. Fable's `query_save_path` marker does distinguish
routine status from detail status. I checked `torrent::status` and
`torrent::post_status` in 3rdParty/libtorrent/src/torrent.cpp, and
`session_impl::post_torrent_updates` in session_impl.cpp. The routine call in
engine/src/Engine/Session.cpp requests only `query_name`. The targeted call
requests `query_save_path`, which fills the torrent's absolute save path.
An empty `renamed_files` is then a valid answer, not an identification test.
I also checked `torrent::set_metadata`: libtorrent imports resolved duplicate
names without posting a `file_renamed_alert` there. That supports keeping
libtorrent as the authority for names.

**D5 needs a stronger statement than one set per section.** General and
Trackers both receive `tracker_list_alert`. Files and Pieces both receive
targeted `state_update_alert`. Permit only one outstanding query for the same
handle and response kind, across those sections and D10's no-view reads.
Record its purpose and generation when issuing it. Replacing the visible
section or disconnecting can release its sample, but must not erase that
record while its reply can still arrive. Otherwise a section switch can use
the preceding section's answer as its own. A single set for the whole retained
context would also enforce this, provided abandoned sets are drained before
replacement. These are alternatives, not two mechanisms to implement.

**D8's dropped-alert rule is not equivalent to keeping the old record.**
`alert_manager::get_all` in 3rdParty/libtorrent/src/alert_manager.cpp hands out
alerts already emitted. It does not wait for queued libtorrent queries to run.
For example: a tracker query is pending; an unrelated alert is dropped; Tick
clears the query record; an edit completes; a new tracker query is posted.
The old tracker answer can now arrive and be labelled with the new generation.
Processing the surviving batch first does not prevent this. This is a source
derived execution sequence, not a reproduced failure.

Failing a held reply and retiring its libtorrent query are separate actions.
Keep the old query recorded until its answer is consumed or its loss is known.
`alerts_dropped_alert::dropped_alerts` identifies types, not requests. For
status, a dropped routine update and a dropped detail answer have the same bit;
the `save_path` marker cannot identify an alert that was lost. Thus D6 solves
normal reply identification, but does not justify D8's unconditional retry.

One concrete option, if automatic retry after a dropped status is required,
is to allow only one status-producing call outstanding across the session:
either a routine `post_torrent_updates` or a targeted `post_status`. The engine
records which it issued. Both calls emit one `state_update_alert`, including
an empty routine update. Consume the whole popped batch before issuing its
successor. With no other status producer, the status drop bit then identifies
the sole outstanding call. Coalesce routine requests while it is outstanding.
This avoids an ambiguous retry, but delays routine updates behind detail and
detail behind routine updates; that latency has not been measured. It is not
a FIFO of several requests. The simpler failure policy is to leave an
ambiguous query unavailable until its answer arrives, rather than promise
transparent retry. I would not add session-wide coordination solely for a rare
drop without choosing that recovery requirement explicitly. Neither policy
allows clearing the record merely because a client timed out.

**Corrections and qualifications to the recorded summary:**

- Q4 should say one acquisition, or one query set, per read. Files and Pieces
  each need several queries. Stopping reads stops new work; it cannot cancel
  queries already sent.
- The D4 summary accurately records my two moments: acceptance invalidates;
  effective completion permits a confirming sample. D4 now chooses only the
  completion boundary. That is a narrower guarantee, not a literal adoption:
  pre-edit data may still be served while the edit is pending. I accept that
  simplification if the contract is that no pre-edit sample is served after
  successful completion. Keep the original generation on outstanding queries,
  and use `CompletePriorities`' effective match, not just any `file_prio_alert`.
- I accept D3's age check to avoid briefly showing a minutes-old peer list.
  Three seconds is a proposed freshness policy, not a measured threshold. It
  is distinct from a timer that starts background work. At longer refresh
  intervals, every read may hold; Q1 must cover that case too. Infrequent reads
  alone do not make a command delayed behind one acceptable.
- "None needs to change" is too absolute for the other blocking calls. I
  withdrew a blanket refactor requirement. A direct caller that must share the
  new acquisition path can still need a change; unrelated call sites remain
  separate findings until their user-visible delay is established.
- The Pieces summary is accurate. Cause 5 should not attribute the measured
  93–217 ms to JSON construction alone. Those are whole-request timings. The
  collection, projection, encoding and transfer split remains unmeasured.

D7 and the held-reply direction remain consistent with my answer. The stated
agreement of all three reviewers is premature for D5 and D8 as currently
written. This entry is based on source inspection; I made no new performance
measurement and did not build or run either application.

**Editor's reply.** Accepted, except the dropped-alert recovery, which takes
the simpler of Astra's two policies with one difference.

- Shared alert types: accepted. D2 now lists data kinds, and D5 keys the one
  outstanding query by torrent and data kind. Its record survives section
  changes, torrent changes and disconnect until the answer arrives. Files and
  Pieces share one detail-status query, so there is one status kind, not two.
- Dropped alerts: Astra's sequence is right, and D8 no longer claims the retry
  is safe. Keeping the record until an answer arrives would leave a section
  without data forever when the answer was really lost, so D8 clears the record
  and accepts the rare mislabelled answer explicitly. Verified that
  `tracker_list_alert` has critical priority (alert_types.hpp) and that
  `alert_manager` doubles the limit for each priority step (alert_manager.hpp),
  so a drop needs a long-stalled engine. The session-wide status coordination
  is in the rejected list. The owner has since accepted this policy (D8).
- D4 now states the guarantee: no data queried before a command's reply is
  served after it, and pre-edit data may show while the edit is pending.
- D3 now calls the 3 s limit a proposed policy, and Q1 covers the longest
  refresh interval.
- Cause 5 now says the 93–217 ms are whole-request times.
- "None needs to change" for other blocking calls: D10 is the one direct caller
  that shares the new path; the rest stay separate findings.

#### 2026-10-08 (Astra: check of the revision)

**The revised ownership and correlation design addresses my earlier findings.**
D2's shared detail-status query, D5's records on each torrent, and D10's use of
the ordinary copies remove the section-switch ambiguity. D4 now states the
completion boundary explicitly. D3 and Q1 include the long refresh interval,
and Cause 5 correctly separates whole-request timing from projection cost.
I treat D8's owner ruling as settled and do not request a recovery mechanism.
Two corrections remain before I would call the written design complete.

**D3–D5: continue a held read after discarding an obsolete answer.** D4 says
that after discarding an old-generation answer, "the next read sends a fresh
query." That works only when no read is already waiting. Consider an ordinary
refresh query still outstanding when an edit completes. The edit invalidates
the copies. The following read finds the old query in flight and holds. When
that answer arrives, D4 discards it. Waiting for another read now leaves the
current reply stuck: `PipeClient.Execute` waits for it, and its 15 s deadline
then turns a normal refresh into a connection failure.

After consuming the obsolete answer and clearing its record, issue a query
at the current generation if an existing held read still needs that kind.
If there is no held read, wait for the next read as D4 already says. This is
completion of existing demand, not a watch timer or speculative refresh.
It needs no new protocol or separate mechanism. The sequence follows D3–D5's
rules; I checked the single-request wait and deadline in
app/src/Services/PipeClient.cs, `Execute`, and engine/src/Pipe.cpp, `Serve`.
I did not reproduce it in a running implementation of this proposed design.

**D2: remove the extra `piece_progress` alert-mask requirement.**
`torrent_handle::post_download_queue` dispatches to
`torrent::post_download_queue` (3rdParty/libtorrent/src/torrent_handle.cpp and
torrent.cpp). With metadata, that function directly calls
`emplace_alert<piece_info_alert>`, without `should_post`. In
3rdParty/Release/include/libtorrent/aux_/alert_manager.hpp, `emplace_alert`
does not check the category mask; `should_post` is the separate mask check.
Thus this explicit query already produces its reply with the existing mask.
Enabling the category adds unsolicited progress alerts without helping the
inspector. This removes work and a requirement from the plan.

Two wording qualifications do not change those decisions. D4's guarantee has
the explicit D8 exception. Also, D8's "only when" the queue reaches its limit
is too strong: `emplace_alert` also records a dropped alert on `std::bad_alloc`.
Neither the frequency of drops nor a required stall duration was measured.
These facts do not reopen the owner's choice to accept the rare error.

This check used the revised document and source inspection. No build, engine
restart or performance measurement was performed. Q1 and Q2 remain open.

**Editor's reply.** Both corrections accepted and folded in. Verified that
`alert_manager::emplace_alert` checks no category mask and records a drop on
`std::bad_alloc`, and that `torrent::post_download_queue` calls it directly.
D2 no longer changes the alert mask. D4 sends a fresh query at once when a held
read still needs the discarded kind, and its guarantee names the D8 exception.
D8 mentions out-of-memory drops and that drop frequency was not measured.
