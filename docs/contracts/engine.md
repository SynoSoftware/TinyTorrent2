# Engine contract

Behavior of the native engine in the [architecture](architecture.md).
This document owns torrent operations, persistence, activation, and lifetime.
[Pipe API](pipe-api.md) owns their transport representation.

Dependency selection follows [the architecture](architecture.md#dependencies-and-cost).
The version-specific source evidence below was reviewed against
[libtorrent 2.1.2](https://github.com/arvidn/libtorrent/releases/tag/v2.1.2), the
latest stable release checked on 2026-10-03. This is an evidence baseline, not a
requirement to retain that version when a newer stable release is available.
Revalidate the referenced behavior against the release selected for the build.

## Read for the change

Read the shared state, edit and persistence rules here for engine changes. Then
read the sections for the affected behavior, so an unrelated feature does not
hide the safety and recovery rules the change needs.

| Change | Reference |
| --- | --- |
| Adding a torrent, metadata preview or duplicate identity | [Addition and identity](engine/addition.md) |
| Shared files, Remove, deletion, Move, unfinished work, drive turns or incomplete filenames | [Files and recovery](engine/files.md) |
| Startup, activation, tray, Windows registration, Close, Exit or crash reporting | [Lifetime](engine/lifetime.md) |
| Network route, speed limits, schedule, connection measurement or libtorrent tuning | [Settings](engine/settings.md) |
| Notifications or idle-sleep prevention | [Notifications and sleep](engine/notifications.md) |
| Catalogue metadata, DHT collection, catalogue search, exclusion or stale eviction | [Torrent catalogue](indexing.md) |

These references are parts of this contract, with the same authority.

## State and work

libtorrent executes transfers and supplies metadata. The engine's main Win32
message-loop thread owns application state and serializes changes from alerts
and commands. Use libtorrent's queueing and transfer limits; the engine owns the
user's policy, not another transfer scheduler. The tray, pipe, and window cannot
implement their own queue policy or reconstruct state from command
acknowledgements.

Native desktop actions call named Engine operations with typed outcomes, so
tray behavior does not depend on protocol JSON. The protocol dispatcher adapts
the same operation outcomes to replies. Settings validation and persistence
remain in the single settings owner for both callers. A settings change
reapplies only the policy groups it affects (connection, session, per-torrent,
history and limits), compared with the previous settings, so a display
preference reapplies no policy; confirmation still follows the storage commit.

Queue moves operate on libtorrent's download queue. Completed seeds have no
download queue position and are refused as move targets; a placement before a
seed means append to the download queue. Restore applies saved positions only
to torrents currently in that queue, so seeds cannot create gaps in its order.
Background eligibility changes apply the saved order once after the alert batch,
so restoring many torrents does not replay the whole queue for each torrent.
Explicit queue commands apply their accepted order immediately.

Detail answers mark the current reading as changed while the alert batch is
handled, and the engine publishes it once after that batch, building each latest
document once. Publication waits for neither the periodic tick nor a debounce
timer.

Pause all pauses the libtorrent session, which keeps each torrent's own running
or paused state, so Resume all does not start torrents the person paused one by
one. The session pause is saved and survives a restart. While all are paused, a
torrent's own Resume takes effect at Resume all, and the window and the tray say
All paused, so the person sees why nothing moves.

Callbacks and worker completions wake the owner through an engine-owned window.
Create that window before producers start and retain it until they have stopped.
Use window messages rather than [`PostThreadMessage`](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-postthreadmessagew),
whose messages can be lost in native modal loops. Bound each dispatch batch, and
finish a state transition before opening a menu or dialog that pumps messages.
Headless execution uses the same owner without showing tray or splash surfaces.

Keep blocking storage and serialization off the tray message loop and libtorrent
alert callbacks. Use libtorrent's existing asynchronous operations and
[resume-data alerts](https://www.libtorrent.org/tutorial.html); an application
scheduler or event framework is not needed merely to receive them.

Peer limits constrain connected peers; upload slots constrain concurrent
unchoked peers. Expose each setting according to its libtorrent meaning.

Enable the alert categories needed by accepted work and drain alerts even when
WinUI is closed. The pinned [alert queue](https://github.com/arvidn/libtorrent/blob/v2.1.2/include/libtorrent/aux_/alert_manager.hpp)
can drop even critical completion alerts. Bound batches of outstanding work and
handle `alerts_dropped_alert` by reconciling affected operations: regenerate
missing checkpoints and query effective state.
If completion cannot be established, report recovery required; do not infer
success or wait forever for a lost alert. Increasing the queue limit alone does
not solve this. Copy or move borrowed alert data needed after the next
[`pop_alerts`](https://github.com/arvidn/libtorrent/blob/v2.1.2/include/libtorrent/session_handle.hpp);
the notification callback only wakes the state owner.

An operation can be accepted, still running, completed, or failed. Cancellation
of a caller's wait does not undo accepted work. The confirmed list the window reads
again is a command's outcome, so the engine keeps no store of past outcomes. A
move shows its progress and failure on its torrent. A file deletion continues
after its torrent leaves the list, so its failure is
[notified](engine/notifications.md#notifications-and-sleep) and logged. Never discard an operation's
hold on files while it can still affect them. After an engine crash, unfinished
work is unknown until recovered, never inferred as success.

Routine status uses libtorrent state-update alerts without piece bitfields.
Retain the latest status per accepted torrent and classify it once for summary
rows, tray counts and power policy. This avoids synchronous per-torrent queries
on every tray or window refresh. A new or restored torrent starts with the facts
in its add parameters, then joins the routine grouped status updates. Startup
does not post one status query per torrent, because the restore loop cannot
drain their replies until it finishes. Installations and the routine timer request
one grouped update at the end of the current engine turn, so a new torrent's
queue eligibility does not wait for the next timer interval. A new addition is
added with no file wanted, so libtorrent queues it only once its chosen files
apply. Until the first grouped update after that, it counts as queued at its
accepted place, so the list shows it where it will stay instead of moving it
twice. That update settles whether libtorrent queued it, and the accepted order
is applied again, so libtorrent's queue matches the list.

Completed means that received payload is ready on disk. After libtorrent reports
the download finished, keep its completion pending until `cache_flushed_alert`;
the row and completion toast share that acknowledgement. Rechecks do not create
download toasts. Payload counts contribute to completion only during a download
cycle: late duplicate blocks received while finished do not start another one.
The completion phase keeps the active download cycle independently of the last
ordered file-event origin, which becomes unknown only when state-change alerts
were dropped. Unrelated alert loss preserves that evidence. The drop notice is
appended to its alert batch, so completions in a batch with lost state changes
are also excluded from download marking; sampled status cannot identify their
original cause.
File completion while downloading also records receipt, so a counter reset on
resume cannot hide a file that finishes between samples.
The downloading-to-finished transition requests a fresh asynchronous status
sample before closing that cycle, including a download that completes between
summary samples. Rechecks invalidate older observations when dispatched and
when checking ends, so a sample taken before the check cannot settle it.
Dropped-alert recovery queries effective finished state and
requests a fresh flush acknowledgement rather than inferring disk readiness.
Completion demand shares the inspector's status query records and posts only
while fewer than 64 are outstanding, so recovery cannot flood the alert queue.
Replacing a native handle invalidates its ordered completion provenance and
requests a fresh observation through that same owner. Native initialization
can finish while the replacement is paused and before its alerts are mapped;
the current handle's observation closes that gap.

Completed-file discovery runs on the payload worker in batches of at most 64
torrents, with one network-thread collection visit per batch. The observation pairs metadata
and actual names with verified-piece file progress; display progress can include
unverified blocks and cannot establish that a physical file is complete. The
owner accepts only the same handle and generation, so removal, rechecking or a
newer name change cannot be overwritten by an older observation. Before removing
the incomplete suffix, the rename worker releases every shared owner's files and
checks their actual paths and verified progress again.
Lost preparation acknowledgements keep the torrent's files held until a
disk-release fence and a fresh name observation establish their locations.
Discovery needed by an accepted deletion continues after its row is removed
and during shutdown, so closing the window or exiting cannot strand that work.

Each torrent has one checkpoint write in flight. If newer resume data arrives
during it, retain the newest result and write it next. Generating resume data
clears libtorrent's dirty flags, so requesting it again cannot replace retaining
that result. The torrent stays checkpointing until these writes have settled.
When a checkpoint can clear post-move verification, it also owns the worker
observation and the marker commit. A queued successor takes over before that
decision. If one arrives during a successful marker commit, keep the proved
checkpoint on disk and request fresh resume data instead of writing that
pre-proof snapshot. This request bypasses dirty flags, which the discarded
snapshot already cleared. A failed marker commit retains the successor as usual.

The engine records each torrent's payload download and upload rate, and the
session totals, once a second, also while WinUI is closed, so the Speed view
shows the selected torrent's activity while the window was closed. Keep the last
five minutes at a selected 1, 5, or 10 second
average and the last 24 hours at a selected 10, 30, 60, or 300 second average.
Defaults remain one second and one minute. Retain at most 300 recent and 8640
day samples, including the partial bucket, so changing granularity cannot grow
history without bound. Interval changes keep completed samples and finish the
old partial bucket at its last observed timestamp; gaps never gain invented
samples. Consecutive zero samples join into one sample, so a stopped or idle
torrent keeps a few samples instead of thousands. A joined recent sample stays
within one day sample and a joined day sample within one clock hour, so each day
sample still ends where a recent sample ends, and the oldest sample reaches at
most that far before its range. Each torrent owns its bounded history, which is
removed with the torrent; the same implementation records session totals for
diagnostics.
History is not saved: the time while the engine was stopped is
unknown in any case, so after a restart the chart starts empty.

Session and lifetime transfer totals count downloaded and uploaded payload from
libtorrent's cumulative session counters, including removed torrents. They exclude
protocol overhead, local verification and the separate connection-capacity test.
Lifetime totals add the current session to the totals loaded at startup. The
existing document and commit queue save them with settings changes and at the
30-second checkpoint cadence; an abrupt termination can lose the tail since the
last successful checkpoint. Clean shutdown samples after transfers have paused
and waits for the final commit. A missing final sample or failed save uses the
existing shutdown failure path. Accounting starts when this version first runs;
older sessions cannot be reconstructed from the remaining torrents.

## Committed edits

Each edit command carries only the user's intended changes, not a replacement
of an old snapshot. Apply commands in the state owner's accepted order; the
latest explicit choice for an edited field wins. Preserve fields the user did
not edit. An already-satisfied choice needs no further state change, and transfer
telemetry does not invalidate an editor.

Check durable torrent identity and whether the requested operation is legal
when it executes. A removed target or a path held by an unfinished move is still
a reason to refuse. With one active window, there is no general draft-revision or
conflict-resolution protocol. The [UI](ui.md#committing-edits)
decides when an edit is committed; confirmation and persistence still follow
this contract even when the interaction applies a choice immediately.

Some edits apply asynchronously. In particular, [file priorities](https://github.com/arvidn/libtorrent/blob/v2.1.2/include/libtorrent/torrent_handle.hpp)
can remain unchanged until disk work completes, or be only partly applied on
failure. Keep accepted choices, effective state, and saved state distinct while
that work is pending. Apply dependent edits in accepted order without rebuilding
them from stale effective values. An earlier resume checkpoint cannot establish
that newer choices were applied and saved. On partial failure report the actual
state and operation failure; do not promise rollback.

Priority edits and first/last-piece adjustments share the inspector's asynchronous
priority query. An edit succeeds only when the returned effective priorities match
the accepted choice. A disk-priority acknowledgement invalidates older samples,
so an answer sampled before that work cannot settle the edit. No confirmation
blocks the engine thread; maintenance only retries a still-pending observation.

Tracker merging uses the configured URLs and tiers: the add/resume list until
an explicit replacement is saved, including an empty replacement. Libtorrent
owns live tracker responses and endpoint statistics, which the inspector queries
asynchronously; merging does not need to wait for those observations.

File edits accept the same priorities as addition, with padding always unwanted,
but may make every file unwanted. A torrent already in the list can keep its
content and seed while the person changes its selection; only addition needs at
least one wanted file to give the new download useful work.

Download in sequential order and Download first and last pieces first are
choices saved with each torrent in `settings.json`. Addition accepts them, and
a command changes them later for selected torrents. Sequential order is
libtorrent's `sequential_download` [torrent flag](https://github.com/arvidn/libtorrent/blob/v2.1.2/include/libtorrent/torrent_flags.hpp).
First and last pieces is not a libtorrent flag: the engine gives top priority
to 1 % of each wanted file at each end, at least one piece, so a media player
can read a file's header and index early. libtorrent sets
piece priorities again from the file priorities when a file-priority change
completes, so the engine raises those pieces again after each change and after
verification, which can recreate libtorrent's piece picker. Startup
discards the piece priorities in resume files, so a resume file older than the
choice cannot override it.

A torrent's download limit and upload limit are choices saved with it in
`settings.json`, in bytes a second, where 0 means no limit. A command sets
either or both for selected torrents; a limit the command does not carry keeps
each torrent's own value. The engine applies them with `torrent_handle`'s
`set_download_limit` and `set_upload_limit`, which give the torrent a
[peer class](https://github.com/arvidn/libtorrent/blob/v2.1.2/src/torrent.cpp)
of its own. libtorrent throttles each peer by every class it belongs to, so the
lower of the torrent's limit and the current
[global limit](engine/settings.md#network-settings) applies. The schedule, alternative limits
and Pause all therefore need no knowledge of torrent limits, and a torrent
limit above the global one is valid. The class belongs to the torrent, not to a
socket type, so a torrent's limits include LAN and loopback peers, as the
global limits do. libtorrent also writes the limits into resume data; startup
applies the values in `settings.json` instead, so a resume file older than the
choice cannot override it. Snapshots report each torrent's limits, so the
window shows the engine's choice rather than its own copy.

## Persistence and file safety

Library and subtitle acquisition have no engine persistence. Their C# owner
opens SQLite only while the product window is open; see [Library ownership](library.md#ownership-and-data-sources).
Torrent facts remain authoritative, and the engine neither reads that database
nor waits for it before transfers, commands or shutdown.
Library and subtitles add no C++ feature code, piece demand, byte-read interface,
sidecar ownership or file-operation extension. C# consumes existing torrent facts;
downloaded subtitle files stay outside native payload operations.

Keep one persistence owner for engine data. Store each torrent's libtorrent
resume data in its own file, named by its durable torrent identity, and settings and the other application facts in `settings.json`. Write
each file under a temporary name and rename it over the old one, so a crash
leaves the old file or the new one, never a partial file. One writer queue
performs every write in order. Each file changes on its own, so the rename makes
every write atomic and a database is not needed.

Refuse an unknown newer store format without rewriting it. Any required migration
belongs to this persistence owner and must preserve a recoverable last good state
before changing existing data.

A setting's key in `settings.json` is file format, and the settings command and
reply use the same key. Renaming a key therefore needs a migration. Without one,
loading does not find the saved value under the new key, and the setting returns
to its default for every user.

Reset all settings saves `Defaults()`, the same defaults a new store starts
with, through the path that saves a settings change, so the reset applies at
once and keeps the route, schedule and language rules of an ordinary change.
The [pipe API](pipe-api.md) lists what the reset keeps.

An unreadable resume file affects only its own torrent. Startup adds that
torrent again from the hashes `settings.json` saved, as a magnet link: it
fetches the metadata from peers and checks the files already on disk, so no
downloaded data is lost. An unreadable `settings.json` still refuses the whole
store, because it is the list of torrents.

Apply [saved-value recovery](architecture.md#saved-value-recovery) to
`settings.json`. An adapter name that matches no adapter is kept, because it
blocks transfers instead of letting them use another adapter. A torrent record
without its folder takes the one in its resume file, and a record without its
identity comes back from its saved hashes under a new one. The next save writes the
repaired values. Loading keeps the first 128 valid schedule periods, the same
limit that an edit accepts, so an oversized saved schedule cannot block later
edits. The store is refused only when `settings.json` is not JSON, names another format
or holds no readable list of torrents, because then no correct value exists
and a save would drop the person's torrents.

- Checkpoint dirty transfer state periodically; recovery cannot depend on a
  successful final shutdown.
- Order writes so a late save cannot resurrect a removed torrent or replace newer
  state. Receiving an alert or queueing a write is not storage completion.
- Retain an unsaved checkpoint or the need to regenerate it until commit succeeds.
  libtorrent clears its dirty flag when generating resume data; that flag must
  not suppress a retry after the application's write fails. See the pinned
  [resume-data contract](https://github.com/arvidn/libtorrent/blob/v2.1.2/include/libtorrent/torrent_handle.hpp).
- Confirm additions, removals, and settings as saved only after storage commit
  succeeds. The periodic timer may lose recent progress after a crash; it cannot
  be the reason a confirmed removal returns after restart.
- Report write failures honestly. Live settings and successfully saved settings
  are distinct facts until the write succeeds.
- Choose checkpoint frequency and flush semantics against recovery loss and disk
  cost. The rename is the commit; do not add a second durable command log.

The first implementation checkpoints every 30 seconds and limits outstanding
resume requests to eight. Failed writes retain the need for another checkpoint.
Membership and explicit choices commit immediately through the same ordered
writer. The writer flushes temporary metadata before atomic replacement with
`MoveFileEx`; this does not claim payload power-loss durability. Periodic
checkpoints cover libtorrent's dirty state and retained failed saves, so idle
torrents do not require a disk flush every 30 seconds. Queue overload completes
as a failed storage outcome rather than escaping the state owner's dispatch.
Payload disk errors retain their own reason; checkpoint failures do not replace
the actual transfer status. Resume clears libtorrent's disk error and upload
mode before applying the saved running intent.

Recoverable payload I/O errors pause their torrent and release its storage through
libtorrent's disk fence. The engine then checks the location on its volume's
worker once a second, sharing each check between torrents in the same folder.
When it answers, the engine clears the disk error and upload mode and reapplies
the current intent, including forced downloads. This also reconnects seed peers
with fresh piece availability and reopens invalidated network file handles.
Stop, Pause all, schedules and adapter restrictions still apply. Pending Verify
waits for storage recovery. A late probe cannot resume a removed torrent or
apply an old location's answer after a move. Native release callbacks, rather
than droppable alerts, establish that the old disk work has ended.
Fresh generation-checked status samples reconcile missed errors, so an older
status cannot restart recovery after it succeeds. If a disk error strands
libtorrent's recheck setup, reload the native handle through the same owner as
move verification, retaining the torrent's identity, history and current choices,
and check its files again. A failed replacement retains the resume parameters
according to whether the old handle was actually removed, rather than whether a
late callback still holds it alive.
Every replacement restores the status-update subscription that native resume
data omits, so the list and transfer policy keep following the new handle.

Mark each payload file as downloaded from the internet when it completes while
its torrent is downloading, by writing the internet zone to the file's
`Zone.Identifier` stream, as browsers do. Windows then shows its SmartScreen or
Protected View warning before the person runs or edits a file from a torrent.
Files that verification finds already complete are not marked, because they may
be the person's own files. A file that has the unfinished suffix is marked, and
the mark flushed, before its rename to its final name, so a cut never leaves a
final-named file without its mark. Without the suffix, a file is marked when it
completes, so a crash in the moment between the two leaves that file
unmarked. There is no setting: the protection shows nothing
until it is needed, and a person who trusts a file unblocks it in its
Properties, as for any download. Marking is best effort. A volume, network
share, or permission that refuses the stream is written to diagnostics and
never fails the download, because the finished file matters more than the mark.
