# Pipe API

Communication between the engine and WinUI in the
[architecture](architecture.md). The sections before
[Wire representation](#wire-representation) establish required behavior and
name a field only where that behavior depends on it; that last section records
the concrete message fields.

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
requests, Exit confirmation, toasts, and opening Settings after the person selects a
notification about a broken torrent handler. One receive dispatcher separates them from replies;
one writer per connection serializes whole frames so notifications and replies
cannot interleave. A close request for Exit runs asynchronously, outside the
request slot and receive loop, so committed edits are sent and unfinished input
can be prompted before the window closes. A clean window closes without prompting;
the [UI](ui.md#committing-edits) owns that behavior. A window whose
user keeps unfinished input replies that Exit is cancelled. The same close reply
distinguishes waiting for the person from continuing closure, so an unanswered
draft or Exit confirmation cannot trigger an unresponsive-window warning. The engine's
[shutdown sequence](engine/lifetime.md#closing-and-shutdown) owns the rest. Do not add
another transport or event bus.

Commands take priority over optional refreshes. Bound the command queue and
report overload. A slow or absent window cannot block transfers or create an
unlimited notification backlog.

A request timeout currently closes the connection and settles all outstanding
requests. Partial frame writes also invalidate the connection. A future change
to isolate read timeouts must retain outstanding capacity and dispose of late
stateful replies; simply abandoning a timed-out task would lose that ownership.

## Encoding and validation

[Wire representation](#wire-representation) records the concrete fields and
units that both codecs implement.

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
keeps its store. A second launch that opens the window, adds torrents or changes the Windows
registration compares
it with its own store and does not forward to an engine of another store; like a
different version, that error says where the other engine runs.

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
[notification](engine/notifications.md#notifications-and-sleep), and a move failure shows on
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
Publish a snapshot only when it is coherent and complete; no caller observes a
copy another thread is still filling. Membership and application state form one
consistent copy; transfer telemetry is sampled, not a promise to freeze every
swarm at one instant.

<a id="pieces-detail"></a>While the Pieces map is visible, its detail replies
include verified pieces, the connected-peer count per piece and downloading
pieces with their received share. Opening the map also supplies where each
file starts in the piece sequence. Hidden maps request no piece detail, because
a large torrent has tens of thousands of per-piece availability counts.
Replies include a `wanted` boolean per piece, derived from the chosen
non-padding files. A boundary piece shared with a wanted file remains wanted,
so completion conclusions exclude skipped content without hiding required bytes.

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

**Owner requirement: display available facts immediately, even when they are
not current; fresh facts follow in a later packet and the UI never waits.**
Cheap cache access and short state mutations remain synchronous on their owner.
Slow collection, file I/O and lookup must not occupy that dispatch path. Actions
such as Open may asynchronously await their own complete-data reply; this never
holds the pipe or another request. Successful saves still require persistence.

Inspector reads return immediately with available facts and a readiness flag;
they never hold the request slot for libtorrent. Retained facts remain available
for display regardless of age, until their torrent or generation is invalidated;
readiness still requires recent, complete facts. Missing values remain unknown,
not zero or empty sets. The existing detail collector pushes partial updates as
queries answer, then a complete fresh result when all have answered. All messages carry the
inspector context, and the pushed result also identifies its session and torrent.
The immediate reply's readiness describes available facts, not completion of the
fresh demand. Views apply those facts immediately and retain known values while
missing ones arrive. Peer rows are matched within the affected torrent, so an
ordinary rate or state change updates existing rows and keeps their identity
instead of rebuilding the whole list. PipeClient keeps background file reads behind that demand until
its matching complete push arrives or the demand ends through failure, replacement,
release or disconnect, so a Library read cannot replace Inspector's awaited update.
PipeClient forwards only the current Inspector push to the window's coalesced
detail update; background file results settle their own wait inside PipeClient,
so they cannot overwrite an Inspector result awaiting the UI dispatcher.
The window invalidates that context on command replies before applying later
pushed data, so a queued pre-command result cannot overwrite a confirmed edit.
Reads used by actions such as Open retain their complete-data replies because
they need confirmed disk paths. They use the same collector.

Immediate inspector replies and command replies answer within 50 ms at p95 and
100 ms at p99 under active Release load of 100 or more peers, also at the
longest refresh interval and after invalidation. No request dispatch or reply
callback makes a synchronous libtorrent call on the engine owner; workers may
wait while completing an operation, and that wait belongs to the operation's
own completion time. Adding content, moving files and confirming file
priorities keep their completion semantics. The cost of the Pieces reply is
unmeasured: a stall of 50–100 ms of the engine thread on every read would
justify sending `verified` as a bit string; a few milliseconds would not.

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
[lifecycle and restoration](engine/settings.md#connection-measurement); losing the
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

No command is refused because files are busy, following
[the busy-files ruling](engine/files.md#removal-and-moves). A command on torrents
whose own files are being prepared, renamed or moved replies once its choice
is saved. `verify`, a file priority `edit` and `remove` then wait for those
files and apply when they are free; the other commands change no files and
apply at once. A `move` that waits for busy files or for its
[drives](engine/files.md#drive-turns) replies at once, and its rows show the
`waiting_move` status until the move starts; a refusal then shows on the rows.
A check that waits for its drive shows `waiting_check`. The rows of both carry
`waiting_drive`, the drive letter or server of the waiting torrent's own folder
that the job waits for, and `waiting_torrent_id`, the torrent whose running
job uses that drive. Both are empty on every other row, and on a waiting row
whose job waits only for busy files. While the engine still looks up the
drives of the folder, `waiting_drive` names the folder's drive letter or
server and `waiting_torrent_id` is empty. A waiting move whose drives are free carries `waiting_files`, the name
of the torrent whose file work it waits for; it is empty on every other row. A move that continues after a restart shows `waiting_move` while it
waits. A check or a move whose drive stops responding shows the error `drive_stalled`.
A check that waits because a stalled check's reads block every check names
that stalled check and its drive. A `move` that waits for the metadata of a
torrent whose save folder overlaps its destination also replies at once, and
its rows show the error `metadata_unavailable` until it starts. `preview` and `preview_detail` do not wait for other
torrents' file names: until they are read, `shared_with` compares only final
names.

`move` reads `torrent_ids` and the destination parent folder. Its engine preflight
detects files already at the destination. It replies after the recovery marker
commits, without occupying the pipe while files move. Rows carry `moving` and
`move_destination`. While a row is `moving`, its `progress` is the share of its
bytes that the move has placed, and 0 while it waits for its turn. The
snapshot's `move_rate` is the bytes per second all running moves copied over
the last second, which leaves out renames and the holes of a sparse file, and
null while no move runs. Its `check_rate` is the bytes per second libtorrent checked over
the last second across the torrents whose files it checks, and null while it
checks none. Each torrent
leaves `moving` and clears its marker when its own move ends, and failures
remain visible on the torrents. Source sharing and destination use by an outside
torrent have distinct refusals, so each offers an action that can resolve it.
A saved move that Exit or a crash cut off is an
[unfinished task](engine/files.md#unfinished-tasks) at the next start. A move whose
files a failure left in two folders waits until the person chooses its
location through Move, as [engine moves](engine/files.md#removal-and-moves)
describes.
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
are notified and logged, and the toast carries the `folder` that Open folder
shows. It never refuses because files are busy, following
[the deletion ruling](engine/files.md#removal-and-moves): a torrent whose own
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

## Wire representation

Protocol version 25 uses a four-byte little-endian UTF-8 JSON frame length,
bounded to 16 MiB. The endpoint is `TinyTorrent.<logon SID>`; each connecting
client first receives `{type:"hello",version:25,session_id:"...",data_directory:"..."}`. The absolute data directory
keeps the same store for explicit Restart, which starts the engine beside the
window rather than a path a pipe peer reports. Requests are
`{request_id:signed-64-bit-integer,command:string,...}`. Replies repeat `request_id` and have
`ok:boolean`, either `data` or `error:{code:string,detail:string}`. Native control
notifications are `{type:"activate"}`, `{type:"close"}`, `{type:"confirm_exit"}`, and `{type:"activations"}`.
`{type:"settings",setting}` opens Settings at that card of the General page and
highlights it, as search does. The pipe
passes the engine each request's connection beside the request, so no request field
can claim another connection's previews.

Toasts use `{type:"toast",kind,torrent_id,name,detail,code,folder,count,switch}` on that
same connection while the window is open. `switch` names the Settings switch
that turns the toast off: `finished`, `added`, `problems` or `background`, or
null when none does. The window's More menu sends it back in `turn_off`, so
the engine alone decides which switch a toast has. `count` is one: a toast queued in
the tray stays there, and the window never receives a tray burst. The window
keeps completion feedback bounded and uses the current
torrent identity to resolve Open folder. Windows delivery is reserved for a
closed window and obeys the corresponding notification preference.

`snapshot` answers immediately with known state. With `subscribe:true`, it also
registers that connection for `{type:"snapshot",ok:true,data:...}` pushes until
disconnect. Pushes use the same snapshot data as replies, including `session_id`
and a signed 64-bit `sequence` that increases whenever the engine builds a
snapshot. The client discards a snapshot whose sequence is not newer than the
last received on that connection. One replaceable summary slot bounds unsent
pushes. The writer alternates ordered frames with optional updates and alternates
summary and detail when both are pending. Routine publication follows
`refresh_interval` on one-second engine ticks, preserving the interval's phase
instead of rounding every interval up; a connection test publishes each second.
Successful mutation replies include `data.snapshot_sequence` when subscribers
exist: the sequence of the known-state summary published after that command.
Consumers use it to recognize a qualifying summary that arrived before the reply
or retain their optimistic state until its push arrives. Commands also post a
status query; fresh status is pushed on arrival. The window retains explicit
refreshes but no summary timer or post-command polls.

Snapshots include `transfers: {session: {downloaded, uploaded}, all_time:
{downloaded, uploaded}}`, with nonnegative signed 64-bit payload byte counts.
`settings.json` retains the lifetime pair under `transfers`; older documents
without it start at zero. The engine owns sampling and persistence; Home only
formats these snapshot values.

Commands are `snapshot`, `preview` (source, destination), `preview_detail`
(preview_id, destination), `add` (preview_id, destination, paused, optional
priorities, optional sequential, first_last and queue_top booleans that default to false),
`cancel_preview` (preview_id), `merge_trackers` (preview_id,
torrent_id), `torrent` (torrent_id), `pause`, `resume`, `force`, `verify`, and
`remove` (torrent_ids), `queue` (torrent_ids with direction: up/down/top/bottom,
or before_torrent_id: string/null for a row drop; null means end),
`piece_order` (torrent_ids with sequential, first_last or both as booleans; an
absent one keeps each torrent's choice),
`session_pause` (paused), `settings` (changes), `reset_settings`, `turn_off`
(switch: a toast's `switch` word, or `all` for every switch; it saves those
notification settings as off, as the popup's More menu does), `check_proxy` (proxy), `open`, `ready`,
`window_closed`, `activate_reply` (available boolean), `close_reply` (state:
waiting/closing/cancelled), `exit_reply` (answer: confirmed/cancelled/unavailable),
`unfinished_tasks` (action: continue/abandon), and `exit`. Activation acknowledgement lets Open wait through an
old window's close path without losing the request. Current settings changes accept language (`en`, `es`)
and theme (`system`, `light`, `dark`); unknown fields or values are refused. A
settings acknowledgement confirms the same durable replacement as membership.
Settings also accept `default_destination` (absolute path), `torrent_dialog` and
`magnet_dialog` (independent booleans, default true). Settings also accept
`confirm_delete` (boolean, default true, added in protocol version 12) and
`limit_mode` (`none`, `speed` or `alternative` for a manual choice, default
`none`, or null to clear its temporary override while scheduling is enabled).
The product's [mode selector](ui/settings.md#speed-limits) commits
`schedule_enabled: false` with a fixed `limit_mode`; Weekly schedule commits
`schedule_enabled: true` with `limit_mode: null`. The nullable wire value does
not make a fixed UI choice expire at the next schedule boundary.
Settings also accept `download_limit`, `upload_limit`,
`alternative_download_limit`, `alternative_upload_limit` (bytes per second,
integer 0 through INT_MAX; 0 means unlimited). Alternative limits initially use
10 KiB/s in each direction. Settings also accept `notify_problems` (default true),
`notifications_enabled` (finished downloads, default false), `notify_added`
(default false), `notify_background` (the toast when the window closes,
default true), `prevent_sleep` and `prevent_sleep_seeding` (booleans).
Settings also accept `encryption` (`preferred`, the default, `required`,
`allowed` or `disabled`) and the proxy: `proxy_type` (`none`, the default,
`socks5`, `socks4` or `http`), `proxy_host`, `proxy_port` (0 through 65,535),
`proxy_username` and `proxy_password` (at most 255 bytes each). A proxy type
other than `none` without a host and a port is refused. settings.json holds
`proxy_password` encrypted with DPAPI for the current Windows user, as base64;
the settings reply and the snapshot carry it as plain text.
`check_proxy` takes `proxy`, an object with the settings command's proxy
fields, and starts a check that connects to that proxy and signs in within 10
seconds without changing the session. It replies at once with `check_id`,
because the check can take longer than a client waits for a reply; the
snapshot reports the result.
Settings also accept `incomplete_folder` (absolute path), `use_incomplete_folder`
(default false), `append_suffix` (default true), `confirm_exit` (default true),
`show_external_ip` (default false), and `disk_buffer_mib` (integer 1–1024,
default 100). Each added torrent saves `append_suffix` and, when using a separate
incomplete folder, `final_folder`; `save_path` remains its current location.
Advanced also accepts `checking_memory_mib` (integer 1–1024, default 4),
`hashing_threads` (integer 1–64, default 1, threads per running check), and
`file_pool_size` (integer 1–10000, default 40). The existing `disk_buffer_mib` key is unchanged when
its control moves to Advanced. These settings use the existing settings reply
and snapshot object.
Existing saved notification choices keep their value.
Session pause is persisted as `all_paused` through
its command and preserves individual torrent intent.
Settings also accept `unfinished_tasks` (`ask`, the default, `continue` or
`abandon`): what the engine does at start with the disk work that Exit, the
10-second end of Exit or a crash left unfinished, as
[unfinished tasks](engine/files.md#unfinished-tasks) describes. With `ask`, the
snapshot's `unfinished_tasks` is `{moves, checks, deletions}` while that work
waits for the person's answer: the number of torrents whose move, check or
file deletion waits, each 0 or more. It is null at every other time,
including while the engine continues or abandons the work by itself. The
`unfinished_tasks` command takes `action`, `continue` or `abandon`, and
answers for all of that work together. It replies once the answer is saved;
moves, checks and deletions then continue or are abandoned without the pipe
waiting, and their failures show on the rows and as toasts. With nothing
waiting it replies success and changes nothing. The command does not change
the setting: Don't ask again sends the chosen word as `unfinished_tasks` in a
`settings` change. While the answer waits, that work uses no drive: a torrent
whose move started stays stopped and shows the error `move_unfinished`, a move
that waited for its turn shows `waiting_move`, and a check stays stopped and
shows `waiting_check`.
`registration` takes an `action` string: `observe`, `register_handlers`,
`unregister_handlers`, `enable_startup`, or `disable_startup`. Its data contains
`handlers`, `handlers_target`, `startup`, `startup_target`, `startup_disabled`,
`torrent_default`, `magnet_default`, `torrent_app` and `magnet_app`. The two
registration states are `none`, `this` or `other`; an unavailable default query
is null. `startup_disabled` is true while Windows has disabled the Run entry.
The app fields name the app Windows opens each kind with, and are empty when
Windows asks instead.
A partial failure includes the observed data with the refusal.
The same operations are available through the engine executable's
`--registration OPERATION` option without starting transfers or WinUI.
Snapshot settings contain the live language, with `language_saved` indicating
whether it matches the saved preference. The store retains only saved settings;
a failed save leaves the live choice selected and reports the failure.
Preview replies contain preview_id, name, size, files (index, path, size,
priority, padding), metadata_ready, hashes (full v1/v2 hexadecimal strings),
trackers (URLs), merge_available, error, shared_with (the names of listed
torrents, and of removed torrents whose files are being deleted, that use a
file of this content at the destination), and
torrent_id of the existing torrent when the source is a duplicate. Add returns torrent_id after storage
commit with duplicate:false, or the existing torrent_id with duplicate:true.
`preview` and `add` fail with `torrent_removing` while a removed torrent with
the same content has not yet been given to libtorrent for removal, as
[Removal and moves](engine/files.md#removal-and-moves) describes.
File preview parses
metadata without creating a payload handle; destination is applied at Add.
Another instance of the storage worker reads and parses preview sources, so a
slow share cannot hold up metadata commits; destruction cancels its blocked read.
Snapshot contains session_id, torrents, settings, language_saved, download_rate,
upload_rate, move_rate, check_rate, paused, limits, has_incoming, proxy, proxy_check, stopping, loading, storage_failed, startup_error, and unfinished_tasks.
Protocol version 7 adds `proxy` and `proxy_check`. A proxy check's outcome is
`connected`, `sign_in_failed`, `unreachable`, `not_found`, `wrong_type` or
`timed_out`. `proxy` is the outcome of the engine's check of the proxy in use,
or null while no proxy is in use and until the check ends. `proxy_check` is the
check that `check_proxy` started last: `check_id`, and `outcome` and
`milliseconds`, which are null while it runs. It is null before the first
check and when that check could not run.
Protocol version 8 adds `external_ipv4` and `external_ipv6` to snapshots. Each
is an address string or empty when unavailable. Torrent rows add `final_folder`,
empty unless they will move on completion, so recent download folders do not
mistake the incomplete folder for the chosen final destination.
Protocol version 9 extends settings with queue, network, checking, history,
window-refresh and addition defaults. `refresh_interval` is milliseconds;
`recent_interval`, `history_interval`, and `slow_wait` are seconds;
`slow_download` and `slow_upload` are bytes per second. Capacity facts are Mbps.
`addition_destination` is the derived destination for new additions and is not
stored in settings.json. Existing keys and defaults keep their prior meanings.
Protocol version 10 adds the remaining addition/file policies and `connection_test`.
Its snapshot reports `phase`, with the active test's `id`, measured `download` and
`upload` in Mbps, `remaining` hold seconds, and a stable `failure` code. The command
accepts `action` as `start`, `cancel`, or `release`; it does not apply transfer
limits. `delete_files` accepts an explicit `deletion` choice (`recycle` or
`permanent`) so the confirmed dialog and the asynchronous file operation agree.
Torrent rows contain torrent_id, name,
size (wanted bytes), completed (wanted bytes present), progress (0..1 for the
current task, including verification), status (stable code), paused, download_rate and
upload_rate (bytes/second), save_path, error (stable code), diagnostic detail,
added (Unix seconds), seed_count and peer_count (connected; peers include seeds),
swarm_seed_count and swarm_peer_count (tracker scrape totals, else known peers),
downloaded/uploaded (bytes), queue
(libtorrent position), complete, completed_on (Unix seconds when libtorrent
finished the wanted files; 0 until complete), incoming, forced, sequential, first_last, and
hashes. Protocol version 6 adds `completed`, so content totals and remaining time
do not mistake verification progress for downloaded bytes. `torrent` returns
the torrent's facts, name, metadata_ready, files, hashes, current content folder,
and magnet link.
All identities are strings. Settings are intended changes rather than replacement
snapshots. `reset_settings` saves every setting at the default a new
installation starts with and replies like `settings`. It keeps Pause all,
because that is a choice about transfers, and the programs already reported
missing, so a reset does not repeat those toasts. Torrents, their choices and
the lifetime transfer totals are not settings and stay. Input sources are bounded to 32 KiB and retained previews/parses to
256. `activate_sources` forwards sources, preserving relative-path meaning at
the launching process. `pending_activations` returns activations with activation_id
and sources; `activations_received` acknowledges activation_ids after the UI owns
them. The engine retains each accepted batch until that acknowledgement. A
window that has begun closing takes no more sources, so they stay with the
engine.

Persistence uses format 1 settings.json with authoritative membership and user
intent, plus per-identity libtorrent resume files. The ordered writer atomically
renames files using Windows replacement; a reply acknowledges saved membership
only after the replacement succeeds. Checkpoints run every 30 seconds, retaining
failed-save work for retry. This bounds crash loss of transfer progress without
turning a transfer tick into a disk write.
