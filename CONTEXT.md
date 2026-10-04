# Product vocabulary

Terms for TinyTorrent. The [table glossary](lib/TableView/CONTEXT.md) defines TableView's
separate, domain-neutral vocabulary. Decisions belong in the
[architecture](docs/architecture.md).

**Torrent**: a download or seed managed by TinyTorrent, together with its content
identity, files, transfer state, and user choices.

**Info hashes**: the full v1 and/or v2 hashes identifying torrent content. A hybrid
torrent has both; either can reveal a duplicate.

**Torrent identity**: the engine-owned identity of one accepted addition, retained
across restarts. Removing and re-adding the same content creates a new identity.

**Engine**: the background application that manages torrents independently of the
product window.

**Command**: a request to change engine-owned state. Acceptance does not imply
completion.
_Avoid_: mutation, when naming the request rather than the state change.

**Operation**: engine-owned work accepted from a command, which may continue
after the originating window closes.

**Outcome**: the known completion or failure of an operation. A retained outcome
record is evidence of that outcome; losing the record does not undo the work.
_Avoid_: result, when naming an operation's outcome.

**Storage claim**: the association reserving affected file paths for a torrent
or an unfinished file operation.
_Avoid_: file claim, path claim, payload claim.

**Snapshot**: a coherent, completed copy of engine state for presentation.
_Avoid_: observation, when it means the same copy.

**Preview**: an unconfirmed addition whose metadata and file choices can be
inspected without downloading payload or creating payload files.

**Draft**: an uncommitted user edit, kept separately from confirmed engine state.

**Tray**: the application's Windows notification-area icon and menu.

**Splash window**: the temporary window shown while the product interface opens.

**TableView**: the reusable, domain-neutral WinUI table library. Its control is
the `Table` type.
