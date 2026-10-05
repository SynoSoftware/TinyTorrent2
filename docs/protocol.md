# Protocol contract

Target communication between the engine and WinUI in the
[architecture](architecture.md). Concrete message fields will be defined with
the first implementation. This document establishes their required behavior; it
is not a second wire-format specification.

## One local connection

Use a local Windows named pipe, available through Win32 and .NET without a
listening TCP port or web server. It still requires serialization, validation,
and explicit disconnect handling; the transport does not make commands atomic.
See Microsoft's [named-pipe overview](https://learn.microsoft.com/en-us/dotnet/standard/io/how-to-use-named-pipes-for-network-interprocess-communication).

Start with one duplex UI connection, asynchronous I/O, and one request awaiting
its reply. Queue long-running engine work and return promptly; a move must not
hold the request slot until all files have moved. Launch forwarding uses bounded
short-lived pipe instances at the same endpoint and contract, so an attached UI
cannot prevent another launch from forwarding its request.

The engine also sends bounded control notifications for activation and close
requests. One receive dispatcher separates them from replies;
one writer per connection serializes whole frames so notifications and replies
cannot interleave. A close request for Exit runs asynchronously, outside the
request slot and receive loop, so committed edits are sent and unfinished input
can be prompted before the window closes. A clean UI closes without prompting;
the [interface](interface.md#committing-edits) owns that behavior. A UI whose
user keeps unfinished input replies that Exit is cancelled. The same close reply
distinguishes waiting for the person from continuing closure, so an unanswered
draft prompt cannot trigger an unresponsive-window warning. The engine's
[shutdown sequence](engine.md#closing-and-shutdown) owns the rest. Do not add
another transport or event bus.

Commands take priority over optional refreshes. Bound the command queue and
report overload. A slow or absent UI cannot block transfers or create an
unlimited notification backlog.

## Encoding and validation

The concrete first-screen fields and units are defined in the
[implementation record](implementation.md#wire-representation), beside the
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
current application state. Start with the operations the actual UI needs.

The first message carries the protocol version and an engine-session identity.
Both executables ship together, so reject a different version with a usable
error rather than negotiating. The session identity lets a restart invalidate
previews and other transient references; durable torrent identities survive a
restart, and info hashes do not replace them.
The greeting also gives the engine executable and absolute data-directory paths
for an explicit Restart, so a surviving UI restarts the same saved store rather
than silently choosing a different developer or user-data location.

## Outcomes and reconnection

Every request carries a request ID that its reply repeats, so a late reply cannot
be mistaken for another; a reply nobody is waiting for is discarded. Cancelling
a wait does not cancel work the engine already accepted.

Each command names the state the user wants, not a step: Pause means "make it
paused", and adding a torrent the engine already holds returns that torrent.
Repeating a command after an uncertain reply is therefore safe. After a
reconnect the UI reads the list again, and the confirmed list is the outcome; no
store of past outcomes is needed.

Delete-data and relocation are the exception, because repeating them is not
safe. Never repeat one automatically after an uncertain reply. The list read
after reconnecting shows whether it was accepted: a deleted torrent has left the
list, and a moving torrent shows its move. A missing row does not prove the files
were deleted; a deletion failure arrives as a
[notification](engine.md#notifications-and-sleep), and a move failure shows on
its torrent.

Edit commands carry the intended fields and target identity defined by
[committed edits](engine.md#committed-edits). Report an engine refusal distinctly
from transport failure. There is no separate draft-conflict message family.

Send stable status/error codes and typed arguments, not English sentences the UI
must parse. The [localisation contract](localisation.md) owns rendering, including
a generic message for unknown codes and optional raw diagnostic detail.

## Snapshots and detail

Start with complete summary snapshots while WinUI is open, and request only the
visible detail section. Fetching files must not also fetch all peers and pieces.
Publish a snapshot only when it is coherent and complete; no caller observes a
copy another thread is still filling. Membership and application state form one
consistent copy; transfer telemetry is sampled, not a promise to freeze every
swarm at one instant.

While WinUI is connected, refresh the summary once a second, as other clients do,
and after each command. Allow at most one refresh in flight. Commands go before
refresh and detail reads, and each view keeps at most one unsent read: a newer
read replaces it, and a closed or changed view withdraws it. Use stable torrent
identity to preserve UI selection, focus, and drafts across refresh/reconnect.
If the torrent was removed, recover focus predictably and mark its draft target
unavailable; never attach that draft to a re-added torrent with the same hashes.
The UI owns those presentation states, not another torrent database.

A detail reply also belongs to the engine session, torrent, and inspector context
that requested it. Apply it on the UI dispatcher only while that same consumer
and context remain current. If selection, section, or consumer lifetime changed,
consume and discard the obsolete reply, then request current visible detail.
Correct transport correlation alone does not make a reply current for the view.

Bound payload size and retained snapshots. Never silently truncate a set or
publish a partial copy as complete. A summary row is a few hundred bytes of JSON,
so the 16 MiB limit holds well over ten thousand torrents; add paging only when a
real set exceeds it. Paging must then still deliver one coherent snapshot, let
commands run between pages, and complete while transfers continue.

Stop refresh work when its UI consumer exits. Details and old snapshots do not
accumulate behind a disconnected client. Speed history is engine state with its
own [bound](engine.md#state-and-work), not data kept for a client, so it continues. Do not add field-level
patches, replay logs, or another cache authority to avoid modest summary copies.
Measure a real payload problem before replacing this design.

## File-operation callers

`file_scope` reads `torrent_ids` and returns `torrents`, the transitive outside
`shared` group, and `kept_files`. Each entry carries its durable `torrent_id`,
`name`, `save_path`, and actual content `folder`. This is a review aid; Move and
Delete recheck their scope when they execute.

`move` reads `torrent_ids`, the destination parent folder, and the optional
explicit `use_existing` choice. It replies after the recovery marker commits,
without occupying the pipe while files move. Rows carry `moving` and
`move_destination`; completion clears the group markers and failures remain
visible on the torrents. Source sharing and destination use by an outside
torrent have distinct refusals, so each offers an action that can resolve it.
An interrupted saved move refuses an ordinary move or deletion with
`move_interrupted`. An active move whose disk outcome cannot be established
shows `move_uncertain`; its path holds remain, so recovery first requires a
normal Exit or explicit Exit anyway and reopening. `recovery_required` remains
the general unconfirmed-operation code for other commands, so file-specific
instructions cannot misdirect an Add or priority edit.

`delete_files` reads `torrent_ids`, commits removal, and replies with
`kept_files`. Payload deletion then continues without the removed rows; failures
are notified and logged. An unresolved move refuses deletion until explicit
recovery establishes the real folder. Cancellation or disconnect cannot undo
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
