# Protocol contract

Target communication between the engine and WinUI in the
[architecture](architecture.md). Concrete message fields are defined in the
[implementation record](01-implementation.md#wire-representation). This document
establishes their required behavior and names a field only where that behavior
depends on it; it is not a second wire-format specification.

## One local connection

Use a local Windows named pipe, available through Win32 and .NET without a
listening TCP port or web server. It still requires serialization, validation,
and explicit disconnect handling; the transport does not make commands atomic.
See Microsoft's [named-pipe overview](https://learn.microsoft.com/en-us/dotnet/standard/io/how-to-use-named-pipes-for-network-interprocess-communication).

Use one duplex window connection and asynchronous I/O. Send commands without
waiting for earlier replies, so a slow preview, save or action cannot hold up
independent work. Request IDs correlate replies, which may arrive out of order;
one writer serializes complete frames in send order. The engine's existing change
queue preserves the order of persisted changes. The window bounds accepted commands
to 32 and in-flight reads to 32; newer unsent refreshes replace older ones.
Only explicit summary reads wait for their previous reply. A changed inspector or preview
can send its new request while an obsolete read finishes. The engine bounds each
connection to 64 outstanding requests, covering both window limits together.
Request IDs are signed 64-bit integers and remain unique while outstanding;
duplicate IDs or exceeding the connection bound close that connection.
Queue long-running engine work and return promptly; a move must not wait until
all files have moved. Launch forwarding uses bounded short-lived pipe instances
at the same endpoint and contract, so an attached window
cannot prevent another launch from forwarding its request.

The engine also sends bounded control notifications for activation, close
requests, Exit confirmation, desktop notices, and opening Settings after the person selects a
notification about a broken torrent handler. One receive dispatcher separates them from replies;
one writer per connection serializes whole frames so notifications and replies
cannot interleave. A close request for Exit runs asynchronously, outside the
request slot and receive loop, so committed edits are sent and unfinished input
can be prompted before the window closes. A clean window closes without prompting;
the [interface](interface.md#committing-edits) owns that behavior. A window whose
user keeps unfinished input replies that Exit is cancelled. The same close reply
distinguishes waiting for the person from continuing closure, so an unanswered
draft or Exit confirmation cannot trigger an unresponsive-window warning. The engine's
[shutdown sequence](engine.md#closing-and-shutdown) owns the rest. Do not add
another transport or event bus.

Commands take priority over optional refreshes. Bound the command queue and
report overload. A slow or absent window cannot block transfers or create an
unlimited notification backlog.

A request timeout currently closes the connection and settles all outstanding
requests. Partial frame writes also invalidate the connection. A future change
to isolate read timeouts must retain outstanding capacity and dispose of late
stateful replies; simply abandoning a timed-out task would lose that ownership.

## Encoding and validation

The concrete first-screen fields and units are defined in the
[implementation record](01-implementation.md#wire-representation), beside the
code implementing both codecs. This contract remains the authority for their
behavior.

Each message is a 4-byte little-endian length followed by that many bytes of
UTF-8 JSON, at most 16 MiB. The engine reads it with nlohmann/json and WinUI
with System.Text.Json. JSON is readable in a log when something breaks, and at a
few hundred kilobytes a second a binary format saves nothing a user notices.
Define each message's fields, units, and limits once, beside the implementation.

Handle partial reads/writes and reject a length over the limit before
allocating. Keep blocking I/O away from the WinUI thread. Transport validation
checks structure and bounds; the engine checks whether an operation is legal in
current application state. Start with the operations the actual window needs.

The first message carries the protocol version and an engine-session identity.
Both executables ship together, so reject a different version with a usable
error rather than negotiating. Retrying never fixes it, so the error says where
the other engine runs, read from the pipe's server process: an engine from
another folder is exited from the tray, and a folder holding both versions is
reinstalled. Restart starts the engine beside the window, which cannot replace
a running engine, so the window does not offer it. Raise the version with every change to a
message's fields, because developer builds from different commits share one
engine per user and otherwise fail on a missing field instead of this error. The session identity lets a restart invalidate
previews and other transient references; durable torrent identities survive a
restart, and info hashes do not replace them.
The greeting also gives the absolute data-directory path for an explicit
Restart, so a surviving window restarts the same saved store rather than silently
choosing a different developer or user-data location. A window ignores it from
an engine of another version, except a debug build that replaces that engine and
keeps its store.

## Outcomes and reconnection

Every request carries a request ID that its reply repeats, so a late reply cannot
be mistaken for another; a reply nobody is waiting for is discarded. Cancelling
a wait does not cancel work the engine already accepted.

Each command names the state the user wants, not a step: Pause means "make it
paused", and adding a torrent the engine already holds returns that torrent.
Repeating a command after an uncertain reply is therefore safe. After a
reconnect the window reads the list again, and the confirmed list is the outcome; no
store of past outcomes is needed.

Delete-data and moves are the exception, because repeating them is not
safe. Never repeat one automatically after an uncertain reply. The list read
after reconnecting shows whether it was accepted: a deleted torrent has left the
list, and a moving torrent shows its move. A missing row does not prove the files
were deleted; a deletion failure arrives as a
[notification](engine.md#notifications-and-sleep), and a move failure shows on
its torrent.

Edit commands carry the intended fields and target identity defined by
[committed edits](engine.md#committed-edits). Report an engine refusal distinctly
from transport failure. There is no separate draft-conflict message family.

Send stable status/error codes and typed arguments, not English sentences the window
must parse. The [localisation contract](localisation.md) owns rendering, including
a generic message for unknown codes and optional raw diagnostic detail.
Storage queue exhaustion replies with `overloaded`, rather than advice about a
failed data folder. Direct addition preserves a duplicate tracker's merge refusal
code and detail, so the window can explain the actual reason.

## Snapshots and detail

Library and subtitle acquisition run in C# and use the existing pipe only for
torrent/file facts and ordinary torrent operations. Search, filters, details,
TMDB and supplier requests, credentials, SQLite edits and job progress stay in
C#; they have no dedicated engine command or snapshot family. Shared source
reads belong to the C# torrent adapter, which seeds and synchronizes SQLite.
Both features consume that committed state through the
[SQLite-only data flow](library.md#sqlite-and-video-information), without direct
torrent reads of their own.
There is no new feature endpoint, subtitle byte demand, publication hold or
companion-file list in the protocol; C# uses the existing torrent contract.
The same coherence, cancellation and payload bounds below apply to those reads.

Start with complete summary snapshots while WinUI is open, and request only the
visible detail section. Fetching files must not also fetch all peers and pieces.
Pieces replies include a `wanted` boolean per piece, derived from the chosen
non-padding files. A boundary piece shared with a wanted file remains wanted,
so completion conclusions exclude skipped content without hiding required bytes.
Publish a snapshot only when it is coherent and complete; no caller observes a
copy another thread is still filling. Membership and application state form one
consistent copy; transfer telemetry is sampled, not a promise to freeze every
swarm at one instant.

The window's initial summary read subscribes to pushed snapshots and replies
immediately with known state. The engine publishes fresh summaries at the saved
refresh interval (1000–10000 milliseconds, default 1000), sampled on the engine's
one-second tick. Deadlines retain their phase, so tick rounding does not lengthen
the chosen average interval. It also publishes known state after a successful
command and requests fresh status immediately. If a
status query was already outstanding, its answer triggers one follow-up query,
because it may predate the command. Routine engine ticks and history sampling
keep their own cadence. The window has no summary timer or post-command polls;
explicit refresh and reconnect reads remain available.

Each connection retains one undelivered summary, replacing it with the latest.
The writer alternates ordered frames with optional updates, and alternates summary
and detail updates when both are pending, so none can starve the others. Every
snapshot, including an explicit reply, carries a sequence number assigned when
built within its engine session. Successful mutation replies identify the summary
published after that command when subscribers exist. The window can therefore use
a qualifying summary already received, or wait for its push, without another read.
It ignores an older snapshot after receiving a newer one. Disconnect releases the
subscription.

Allow at most one explicit summary read in flight. Commands go before
refresh and detail reads, and each view keeps at most one unsent read: a newer
read replaces it, and a closed or changed view withdraws it. Use stable torrent
identity to preserve the window's selection, focus, and drafts across refresh/reconnect.
If the torrent was removed, recover focus predictably and mark its draft target
unavailable; never attach that draft to a re-added torrent with the same hashes.
The window owns those presentation states, not another torrent database.

A detail reply also belongs to the engine session, torrent, and inspector context
that requested it. Apply it on the UI dispatcher only while that same consumer
and context remain current. If selection, section, or consumer lifetime changed,
request current visible detail without waiting for the obsolete read; discard
that read's eventual reply.
Correct transport correlation alone does not make a reply current for the view.

Inspector reads return immediately with available facts and a readiness flag;
they never hold the request slot for libtorrent. Retained facts remain available
for display regardless of age, until their torrent or generation is invalidated;
readiness still requires recent, complete facts. Missing values remain unknown,
not zero or empty sets. The existing detail collector pushes partial updates as
queries answer, then a complete fresh result when all have answered. All messages carry the
inspector context, and the pushed result also identifies its session and torrent.
The immediate reply's readiness describes available facts, not completion of the
fresh demand. Views apply those facts immediately and retain known values while
missing ones arrive. PipeClient keeps background file reads behind that demand until
its matching complete push arrives or the demand ends through failure, replacement,
release or disconnect, so a Library read cannot replace Inspector's awaited update.
PipeClient forwards only the current Inspector push to the window's coalesced
detail update; background file results settle their own wait inside PipeClient,
so they cannot overwrite an Inspector result awaiting the UI dispatcher.
The window invalidates that context on command replies before applying later
pushed data, so a queued pre-command result cannot overwrite a confirmed edit.
Reads used by actions such as Open retain their complete-data replies because
they need confirmed disk paths. They use the same collector.

Each endpoint retains at most one undelivered detail update per connection or
window, replacing it with the latest. Replies and control messages take priority
over that optional update. Closing or changing the inspector releases its old
demand; no refresh timer runs in the engine for a hidden inspector.

Bound payload size and retained snapshots. Never silently truncate a set or
publish a partial copy as complete. A summary row is a few hundred bytes of JSON,
so the 16 MiB limit holds well over ten thousand torrents; add paging only when a
real set exceeds it. Paging must then still deliver one coherent snapshot, let
commands run between pages, and complete while transfers continue.

Stop refresh work when its window consumer exits. Details and old snapshots do not
accumulate behind a disconnected client. The `history` request accepts
`torrent_id` to read one torrent's samples, or a nonempty `torrent_ids` set to
sum selected torrents; an unknown or removed torrent is refused with
`torrent_removed`. The two selectors are mutually exclusive. Without either it
reads session totals for the global Speed inspector and diagnostics. All accept
`range` as `five_minutes` or `day`; replies echo the torrent selector when present.
Selected totals use the session's sampling timeline and observation counts,
including zero contributions before a selected torrent was added.
Each sample carries its first and last observation times
as `start` and `time`, and the observation `count` represented by its average.
Consumers use those bounds to distinguish coarse samples from missing time and
weight combined averages by represented observations. Speed history is engine state with its
own [bound](engine.md#state-and-work), not data kept for a client, so it continues. Do not add field-level
patches, replay logs, or another cache authority to avoid modest summary copies.
Measure a real payload problem before replacing this design.

## Connection measurement

`connection_test` accepts `start`, `cancel`, or `release` and returns the current
operation state without holding the request slot during measurement. The same
state appears in summary snapshots. The engine pushes snapshots
once per second while the operation is active so phase feedback and the hold
countdown remain usable even with a slower configured refresh interval.

Start accepts `profile`: `quick` (the default) or `longer`. Quick measures five
seconds per direction and Longer fifteen. Only time ends a direction, so
traffic grows with the connection's speed.

State includes `session_id` and `sequence`, even when idle. A command reply
receives a fresh sequence before the following summary, so a queued older
summary cannot replace it. A client ignores a delayed command reply after a newer
snapshot or an engine-session change, so old feedback cannot replace current test state.
State also includes the operation identity, phase, measured download/upload capacity
in Mbps, remaining hold seconds and a stable failure code. Live `download` and
`upload` are the average rate so far, from the end of the warm-up once it
has passed. `download_bytes` and `upload_bytes` count payload transferred,
including unfinished requests. The matching `download_seconds` and
`upload_seconds` count elapsed measurement time, and `time_limit` gives each
direction's length in seconds. `complete` becomes
true only after both directions finish successfully, so a cancelled live reading
never populates the capacity fields. The engine owns the
[lifecycle and restoration](engine.md#connection-measurement); losing the
connection releases its temporary restriction. A start whose reply was lost is
not automatically replayed: observe the next snapshot before offering another
test, because testing consumes network traffic. Results do not commit settings.

## File-operation callers

`file_scope` reads `torrent_ids` and returns `torrents`, the transitive outside
`shared` group, and `kept_files`. Each entry carries its durable `torrent_id`,
`name`, `save_path`, actual content `folder`, and `torrent_file`: the .torrent
file the torrent was added from, empty for a magnet. It answers from engine
state without reading the disk and accepts torrents whose files are busy, so the
Remove torrents dialog opens at once; a recorded .torrent file may have moved or
gone since. This is a review aid; Move and Delete recheck their scope when they
execute.
If physical names are still being observed, the reply describes the known
scope and `kept_files` is null. Unknown names cannot authorize payload deletion.

`move` reads `torrent_ids` and the destination parent folder. Its engine preflight
detects files already at the destination. It replies after the recovery marker
commits, without occupying the pipe while files move. Rows carry `moving` and
`move_destination`; completion clears the group markers and failures remain
visible on the torrents. Source sharing and destination use by an outside
torrent have distinct refusals, so each offers an action that can resolve it.
An interrupted saved move stays stopped until the person chooses its location
through Move; the engine checks that location before resuming.
`recovery_required` remains the general unconfirmed-operation code for other
commands, so file-specific instructions cannot misdirect an Add or priority
edit.

`remove` and `delete_files` read `torrent_ids`, an optional `deletion`
(`recycle`, the default, or `permanent`) and an optional `torrent_files`
boolean. `remove` keeps the downloaded files. With `torrent_files`, both delete
the recorded .torrent files in that mode once removal commits; a .torrent file
that is already gone counts as deleted, and a failure is notified as a payload
deletion failure is.

`delete_files` commits removal and replies with
`kept_files`, or null while physical names are still being observed. Payload
deletion then continues without the removed rows; failures
are notified and logged, and the notice carries the `folder` that Open folder
shows. It never refuses because files are busy, following
[the deletion ruling](engine.md#removal-and-moves): a torrent whose own
files are moving or being renamed, or that is deleted while an addition runs,
leaves the snapshot at once and is removed when that work ends. Cancellation or disconnect cannot undo
accepted file work, and reconnect never repeats an uncertain destructive command.

## Isolation

Scope instance ownership and the endpoint to the current user and Windows logon
session. Protect the pipe with an explicit ACL for that logon, reject remote
clients, and fail visibly if the endpoint cannot be securely claimed. A pipe
name is not authentication. Use the documented
[access checks](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights)
and [creation flags](https://learn.microsoft.com/en-us/windows/win32/api/namedpipeapi/nf-namedpipeapi-createnamedpipew).

Also hold exclusive ownership of the engine data directory. Another logon must
not open that state concurrently; explain the conflict rather than starting a
second writer or retrying forever. Arbitrary same-user code is outside this
isolation guarantee; an extra token would not create a stronger boundary.
