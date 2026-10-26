# Addition and identity

Part of the [engine contract](../engine.md). Read its shared state, edit and
persistence rules before changing this behavior.

libtorrent is the sole metadata parser, including v1, v2, and hybrid torrents.
The window supplies source and choices and renders the engine's preview. Magnet
metadata acquisition must neither download payload nor create payload files
before confirmation. Establish that guard before adding the preview; reacting
to metadata arrival is too late. libtorrent's
[magnet guidance](https://www.libtorrent.org/manual-ref.html#magnet-links)
warns that `upload_mode` alone can create empty files. Initial zero file
priorities, including unknown magnet files, must survive automatic management
and any priorities supplied by the source. In the pinned build, set
[`torrent_flags::default_dont_download`](https://github.com/arvidn/libtorrent/blob/v2.1.2/include/libtorrent/torrent_flags.hpp)
before adding a preview and replace any explicit nonzero file priorities with
zero; the flag covers only files without an explicit priority. Build preview
parameters under engine control: clear supplied piece priorities and disable
payload-enabling modes such as `share_mode`. The pinned [initializer](https://github.com/arvidn/libtorrent/blob/v2.1.2/src/torrent.cpp#L2044-L2065)
applies piece priorities after file priorities. Validate that no payload is
downloaded or created with the pinned build.

An unconfirmed preview belongs to its window connection and is released on disconnect.
Cancel releases that preview, never the files of an existing duplicate. Confirm
rechecks duplicates and transfers ownership to the engine; the accepted addition
survives closing the window. When Show dialog when adding torrents is off, direct addition
confirms the same workflow without opening WinUI.

Matching sources staged by the same connection share one preview and merge their
tracker URLs. This keeps a file and its magnet from acquiring parallel handles.
Confirmation pauses a magnet preview and waits for libtorrent to acknowledge its
new save path before committing membership; its payload guard remains in place.
Unknown metadata still permits confirmation with all files wanted. Known metadata
accepts priorities 0 (unwanted), 1 (low), 4 (normal), and 7 (high), with at least
one wanted file. A batch shares destination and paused intent; individual file
choices belong to a single-source Add dialog or the Files inspector after addition.

Known-metadata previews page the file list while retaining original torrent
indices, including gaps from hidden padding. Selection is one default priority
plus original-index overrides; opening another page does not replace choices
made earlier. Ordinary sources and the [torrent catalogue](../indexing.md#search-and-add)
use this same preview, selection and confirmation owner. Catalogue metadata
preparation never exports a temporary `.torrent` or changes the ordinary
source-dialog preference.

Add to top of queue commits the new torrent at the front of the saved download
order before releasing its payload guard. The queue owner reapplies that order
when a torrent enters downloading, because libtorrent appends a torrent whose
wanted files change it from finished to downloading. The option defaults to off
and changes neither paused intent nor automatic queue limits. A duplicate keeps
its existing position, because it is not a new addition.

An accepted running magnet acquires metadata before entering automatic queue
management. When metadata arrives, the same intent owner applies normal priority
to every non-padding file for the saved all-files choice, then restores queue
management. In libtorrent 2.1.2, `default_dont_download` is an initial-parameter
flag; unsetting handle flags does not change its delayed initialization. Explicit
priorities after metadata prevent an accepted magnet from remaining finished
with no wanted payload. Unconfirmed previews retain their guard.
Until metadata provides a name, an unnamed accepted magnet shows its full info
hash, so the person can identify its row rather than seeing a blank torrent.

Keep the payload-write guard until membership and the initial user choices
commit. A failed commit must not leave an unrecorded addition writing files. After commit, apply
the choices through the same operation owner. Recovery in that interval uses
the committed choices, not an earlier resume checkpoint with preview priorities.
Direct addition follows the same ordering.

The watched folder remembers unchanged source files while they remain in its
current scan scope, so removing a torrent does not immediately add it again.
Watched paths use the engine's Windows path comparison and retain their original
spelling for messages; comparison does not rewrite the person's path.
A successful complete scan forgets sources no longer in that scope; placing a
source back or selecting a different folder permits admission again. Failed or
incomplete scans retain the records. The 10,000-source bound applies to files
currently in that scope, not lifetime imports; exceeding it reports a failure
and resumes scanning when the folder is reduced. Unrelated files do not consume
the source limit.

Keep full v1/v2 info hashes and discovered hybrid aliases for content and
duplicate detection. Metadata can reveal an alias conflict after initial
addition. Resolve a preview conflict in favor of the accepted torrent. If two
accepted additions conflict, report it before choosing the surviving membership;
never silently merge incompatible choices or delete their files. The pinned
[conflict handling](https://github.com/arvidn/libtorrent/blob/v2.1.2/src/torrent.cpp)
can pause both handles; that pause must not become the user's saved intent.
Receiving an existing handle does not give a preview ownership of that torrent.

Assign each accepted addition a durable torrent identity, distinct from its
info hashes and transient libtorrent handle. Use it for commands, saved state,
and window drafts. Late alerts and file-operation outcomes belong to that addition;
remove/re-add creates a new identity even for the same content. An old draft or
completion must never target the new addition.

On reconnect, preserve the user's preview inputs and draft choices, but reacquire
and validate the preview. If confirmation may have succeeded, reconcile the
torrent first. A stale preview identifier is not reusable.
