# Product vocabulary

Terms for TinyTorrent. The [table glossary](lib/TableView/CONTEXT.md) defines TableView's
separate, domain-neutral vocabulary. Decisions belong in the
[architecture](docs/architecture.md).

## Language

### Torrents and content

**Torrent**: a download or seed managed by TinyTorrent, together with its content
identity, files, transfer state, and user choices.
_Avoid_: torrent file, when referring to the managed torrent.

**Source**: a torrent file or magnet link supplied to add a torrent.

**Metadata**: the description of torrent content, including its file names,
sizes, and the hashes used to verify its pieces.

**Payload**: the file contents downloaded or seeded by a torrent, distinct from
its metadata.

**Info hashes**: the full v1 and/or v2 hashes identifying torrent content. A hybrid
torrent has both; either can reveal a duplicate.

**Torrent identity**: the engine-owned identity of one accepted addition, retained
across restarts. Removing and re-adding the same content creates a new identity.
_Avoid_: info hash, when identifying an accepted addition rather than content.

**Duplicate**: a source whose known info hashes match a torrent already managed
by TinyTorrent. Shared file paths alone do not make torrents duplicates.

**Wanted file**: a payload file chosen for download, with low, normal, or high
priority; an unwanted file is excluded from that choice.
_Avoid_: selected file, when referring to download choice rather than UI selection.

**Completion**: the point when all currently wanted files have finished
downloading and their received payload is ready on disk; unwanted files can
remain incomplete.

**Verification**: checking the files on disk against the torrent's metadata to
establish which pieces are present and valid.
_Avoid_: download, when referring to checking existing bytes.

### Peers

**Peer**: another BitTorrent client exchanging a torrent's pieces with
TinyTorrent. Peers include seeds and leechers.

**Seed**: a torrent or a peer with the entire payload available for sharing,
rather than only its wanted files complete.

**Leecher**: a peer that does not yet have the entire payload.
_Avoid_: peer, when seeds are excluded.

**Swarm**: every peer sharing a torrent, whether TinyTorrent is connected to it
or not.

### Addition and edits

**Activation**: a later launch's request, such as a source to open, forwarded to
the running TinyTorrent instead of starting a second copy.

**Preview**: an unconfirmed addition whose metadata and file choices can be
inspected without downloading payload or creating payload files. It may still
be waiting for metadata.

**Addition**: acceptance of a source and the user's choices as a managed
torrent; an existing duplicate remains the same torrent.

**Draft**: an uncommitted user edit, kept separately from confirmed engine state.
An accepted command still in progress is pending work, not a draft.

### Transfer choices and state

**Intent**: a torrent's saved paused, resumed, or forced choice, distinct from
whether it can currently transfer. Forced intent bypasses automatic queue limits,
but a session pause can still stop transfers.
_Avoid_: status, when referring to the saved choice.

**Status**: a torrent's current transfer condition, such as checking, queued,
downloading, or seeding, taking pauses, file work, and blocking errors into account.

**Session pause**: a pause of transfers across all torrents that preserves each
torrent's own intent.

**Download queue**: the order in which unfinished torrents are considered for
downloading. It is distinct from the table's displayed order.

### Files and folders

**Save path**: the base folder against which a torrent's relative file paths are
resolved.

**Content folder**: the folder containing the torrent's files; it can be a
subfolder of the save path.
_Avoid_: save path, when referring to that content subfolder.

**Final folder**: the chosen destination for a torrent initially downloading into
an incomplete-download folder, separate from its current save path.

**Shared files**: payload files whose paths are used by more than one torrent.
Torrents with different info hashes can share files.

**Removal**: ending TinyTorrent's management of a torrent. Removal by itself
leaves its payload files on disk.
_Avoid_: deletion, when no payload files are being deleted.

**File deletion**: removal of a torrent with deletion of the payload files not
used by torrents outside the operation. Removal and deletion have separate outcomes.

**Move**: changing a torrent's save path by moving its files, or by using and
verifying files already at the destination.
_Avoid_: relocation.

### Engine work and presentation

**Engine**: the background application that manages torrents independently of the
product window.

**Product window**: the WinUI window that shows torrents and settings.
_Avoid_: UI, when naming the window.

**Close**: ending the product window only; the engine keeps running in the tray.
_Avoid_: exit, shutdown.

**Exit**: ending both applications: the product window and the engine.
_Avoid_: quit, close, shutdown.

**Shutdown**: the engine saving and ending. It is the last stage of Exit;
Windows sign-out starts it directly.
_Avoid_: stop, exit, when only the engine ends.

**Command**: a request to change engine-owned state. Acceptance does not imply
completion.
_Avoid_: mutation, when naming the request rather than the state change.

**Operation**: engine-owned work accepted from a command, which may continue
after the originating window closes.

**Outcome**: the known completion or failure of an operation.
_Avoid_: result, when naming an operation's outcome.

**Snapshot**: a coherent, completed copy of engine state for presentation.
_Avoid_: observation, when it means the same copy.

**Tray**: the application's Windows notification-area icon and menu.

**Splash window**: the temporary window shown while the product interface opens.

**Settings**: the saved, application-wide choices for how TinyTorrent behaves.
_Avoid_: preferences.

**Network adapter**: the Windows network connection that torrent traffic is bound
to.
_Avoid_: interface.

**Page**: a whole view of the product window: Torrents, Settings, or About.
_Avoid_: form.

**Dialog**: a surface over the product window for one task, such as Add or Proxy.
_Avoid_: form.

**Pane**: a side area of a page, such as the Inspector beside the torrent list.
_Avoid_: form, panel.

**TableView**: the reusable, domain-neutral table library used to present the
product's collections.
