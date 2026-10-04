# Protocol contract

Target communication between the engine and WinUI in the
[architecture](architecture.md). Concrete operation codes and byte layouts will
be defined with the first implementation. This document establishes their
required behavior; it is not a second wire-format specification.

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

The engine also sends bounded control notifications for activation, language
changes, and close requests. One receive dispatcher separates them from replies;
one writer per connection serializes whole frames so notifications and replies
cannot interleave. Preparing to close runs asynchronously, outside the request
slot and receive loop, so committed edits or unfinished input can be settled
before agreement. A clean UI agrees without prompting; the
[interface](interface.md#committing-edits) owns that behavior. Close preparation
and final close follow the engine's [shutdown sequence](engine.md#closing-and-shutdown);
agreement alone does not close the window. Do not add another transport or event bus.

Commands take priority over optional refreshes. Bound the command queue and
report overload. A slow or absent UI cannot block transfers or create an
unlimited notification backlog.

## Encoding and validation

Use length-delimited binary messages, operation codes, fixed-width integers,
and length-prefixed UTF-8 strings and arrays. The implementation contract must
define byte order, field meaning, units, and limits explicitly. Encode fields;
C++ object memory and packed struct layouts are not a portable contract.

Handle partial reads/writes and reject malformed or excessive lengths before
allocation. Keep blocking I/O away from the WinUI thread. Transport validation
checks structure and bounds; the engine checks whether an operation is legal in
current application state.

Define the layout once beside the implementation and check both C++ and C# codecs
with the same byte fixtures. Start with operations the actual UI needs. A schema
compiler, Transmission emulation, and speculative message families add no value
to this initial contract.

Ship both executables together. Reject incompatible protocol versions with a
usable error instead of introducing negotiation and compatibility adapters.
Include an engine-session identity so a restart invalidates previews, operations,
and other transient references from the previous session. Durable torrent
identities survive a restart; info hashes do not replace them.

## Outcomes and reconnection

After timeout or cancellation leaves a reply uncertain, discard the connection
before sending another request. A late reply cannot be mistaken for the next
command's reply. Cancelling the wait does not cancel work already accepted by
the engine.

Acceptance, completion, and successful persistence have the meanings defined by
the [engine contract](engine.md). Present pending work until confirmed state or a
known outcome resolves it. Correlate a command using a value the caller knows
before sending it and can retain across reconnects to the same engine session;
losing the acceptance reply must not lose the ability to recognize its retained
outcome. Include active operations and bounded recent outcome records in summary
snapshots, independently of torrent membership, so a removed row does not hide
a deletion failure. Concrete identifiers and retention bounds belong to the
first wire layout.

On reconnect, replace old snapshots and reconcile before retrying. For an
unresolved command whose outcome record has expired, is absent, or belongs to a
previous engine session, report its outcome as unavailable unless recovery
establishes it. Expiry of a record already reported to the caller does not undo
that known outcome. Missing is neither success nor proof that the command was never
accepted. Refresh current facts, stop showing an unknown outcome as indefinitely
pending, and keep the uncertainty visible. A missing row proves neither
successful deletion nor safe reuse of its files. Never automatically replay a destructive command with an
unknown outcome. Bounded outcome records do not replace the engine's durable
recovery facts for unfinished file operations or promise general crash recovery.

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

Allow at most one refresh in flight. Refresh after a command. Use stable torrent
identity to preserve UI selection, focus, and drafts across refresh/reconnect.
If the torrent was removed, recover focus predictably and mark its draft target
unavailable; never attach that draft to a re-added torrent with the same hashes.
The UI owns those presentation states, not another torrent database.

A detail reply also belongs to the engine session, torrent, and inspector context
that requested it. Apply it on the UI dispatcher only while that same consumer
and context remain current. If selection, section, or consumer lifetime changed,
consume and discard the obsolete reply, then request current visible detail.
Correct transport correlation alone does not make a reply current for the view.

Bound payload size and retained snapshots. Small replies need no paging; add it
when a real summary or detail set first needs to exceed the bound, rather than
as scaffolding for the first small round trip. Never silently truncate a set or
publish a partial copy as complete. Page one coherent snapshot in bounded
request/reply units, yielding to commands between units. A giant response split
into pipe writes does not provide that scheduling. Release retained pages on
disconnect. Discard unfinished pages after a command changes the state they
describe, before fetching a fresh snapshot. Ordinary transfer progress does not
invalidate retained pages or a busy download could prevent a snapshot from ever
completing.

Stop refresh work when its UI consumer exits. Details, histories, and old
snapshots do not accumulate behind a disconnected client. Do not add field-level
patches, replay logs, or another cache authority to avoid modest summary copies.
Measure a real payload problem before replacing this design.

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
