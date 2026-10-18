# Product vocabulary

Terms for TinyTorrent. The [table glossary](lib/TableView/CONTEXT.md) defines TableView's
separate, domain-neutral vocabulary. Decisions belong in the
[architecture](docs/contracts/architecture.md).

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

**Handshake**: the initial BitTorrent exchange that identifies the torrent and
peer before ordinary piece exchange begins. An open connection can still be
waiting for its handshake.

**Choke**: a peer's decision to withhold piece uploads to the other side of a
connection. Each side makes its own decision independently of whether it wants
pieces from the other.

**Tracker**: a service that introduces peers sharing a torrent. Its completed
download count is the completions it reports, not the number currently downloading.

**Web seed**: an HTTP or HTTPS source of a torrent's content. It provides
downloads without participating in BitTorrent piece uploads or choke negotiation.

**Peer location**: the approximate country and, when available, coordinates
and city that a peer's address resolves to on this computer.
_Avoid_: geolocation, in product text.

**Estimated distribution**: a country's measured peer activity spread over
representative populated regions. These are illustrative destinations, not
observed peer coordinates or network routes.

**Peer place**: the geographic grouping used to filter peers: a whole country
in Countries detail, or shared coordinates or a country's records without
coordinates in Cities detail.
_Avoid_: destination, when referring to the peers included by a place filter.

**Map destination**: a position representing peers on the map, either at supplied
coordinates or in an estimated populated region. Several estimated destinations
can represent the same peer place.

**Chosen place**: the peer place chosen on the map that limits the table's peers.
It is distinct from the individual peers selected in that table.

**Inspected destination**: the map destination whose details the person is
examining. An estimated destination does not establish the actual location of
any individual peer.

**City data**: DB-IP's address-to-city data, downloaded when the person asks
and kept on this computer as a replaceable copy, like IMDb data. The country
data that resolves flags is installed with the app.

**Seed**: a torrent or a peer with the entire payload available for sharing,
rather than only its wanted files complete.

**Leecher**: a peer that does not yet have the entire payload. The torrent
list's Peers column counts leechers beside Seeds, as people expect from that
column pair.

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

**Stopped**: a torrent whose own intent holds it still until the person
selects Start. Stop and Start are its commands; a seeding limit and Don't start
in the Add dialog also stop a torrent. The protocol, saved files and code call
this intent `paused`.
_Avoid_: paused, for one torrent's own choice.

**Paused**: held by something outside the torrent, such as a session pause or
a scheduled pause period. The torrent continues by itself when that hold ends.

**Session pause**: a pause of transfers across all torrents that preserves each
torrent's own intent.

**Recovery hold**: a torrent's block on transfers because previously present
payload is missing or its prior state cannot be established. It ends through
the person's recovery choice or validated adoption authorized by movement tracking,
not an ordinary Start or scheduled resume. Reusing existing files does not
authorize downloading missing payload.

**Session totals**: payload bytes downloaded and uploaded since TinyTorrent's
engine started, including transfers while its window was closed.

**Lifetime totals**: payload bytes downloaded and uploaded across engine sessions,
including torrents that have since been removed. Protocol overhead and local
verification are not payload transfers.

**Download queue**: the engine-owned order in which unfinished torrents are
considered for downloading.

**Queue position**: a torrent's place in the download queue. A torrent outside
that queue has no queue position.
_Avoid_: priority, when referring to the order of torrents.

**File priority**: the relative download preference of a wanted file within one
torrent, separate from that torrent's queue position.

**Queue limits**: the maximum numbers of automatically managed downloading,
seeding, and overall active torrents. They limit activity, not transfer speed.

**Bandwidth limits**: caps on download and upload rates, separate from how many
torrents or peers may be active.

**Limit mode**: the choice of Standard limits, Alternative limits, No limits or
Weekly schedule that determines which bandwidth limits apply.

**Limit profile**: the bandwidth limits currently applied: Standard limits,
Alternative limits or No limits. A weekly schedule selects a profile.

**Standard limits**: the ordinary download and upload caps, also used outside
alternative or pause periods in the weekly schedule.

**Alternative limits**: the second pair of download and upload caps, selectable
directly or during alternative periods in the weekly schedule.
_Avoid_: transfer preset, which also includes queue limits.

**Transfer preset**: a named combination of bandwidth and queue limits. Custom
means the current choices do not match a predefined combination, not a saved profile.

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

**Adoption**: updating a torrent's location to use its files already present
elsewhere, without physically moving them. It is distinct from permission to
download missing payload.

### Library

**Library**: the searchable collection of finished files reported by its
current data sources, together with their video or music information.

**Library data source**: an origin of Library file entries and their current
membership, such as managed torrents.
_Avoid_: source alone, which means a torrent file or magnet supplied for addition.

**Disk discovery**: Library's automatic finding of video, music and subtitle
files on selected local NTFS drives, including changes to their locations.

**File finder**: the helper process, `TinyTorrentFileFinder.exe`, that reads
the NTFS metadata and USN change journal of selected drives for disk discovery
and locates missing torrent files.
_Avoid_: Find, which is the subtitle batch command.

**File presence**: the observed existence or absence of a file at its location,
distinct from a torrent's continued claim to that file. An unavailable drive or
failed check does not establish absence.

**Library entry**: a known file location represented by one or more current
data-source contributions.

**Contribution**: the files one data source reports for one origin, such as
one torrent. Library removes its stored facts when it observes that contribution's
withdrawal or reconciles after reconnecting.

**Library catalogue**: the last reported file lists retained for browsing, including
files whose current location or completion has not yet been confirmed.

**File confirmation**: a current data source's confirmation of a file list and its
locations. It permits file work; a retained catalogue alone does not.
_Avoid_: verification, which checks payload against torrent piece hashes.

**Import**: the one path by which a torrent Library does not know becomes
known: its files are read and listed, each video is identified from its name,
and, when the saved preferences allow, its videos get subtitles. A provider
lookup is not part of it: every video without a match is looked up, whichever
torrent it belongs to.

**Library configuration**: one way of showing Library's table: Videos, Music or
Files, each with its own rows, columns, sort and filters.
_Avoid_: mode and tab; the person chooses a configuration in the View menu.

**Library metadata**: descriptive facts about a file's content, such as a
movie's title, cast and synopsis or a song's artist and album. Within Library,
the term is metadata; torrent metadata describes the download's contents.

**Video information**: Library metadata about a movie, series or episode,
from the provider.

**Automatic video information downloads**: background provider work allowed by
the saved setting. Off stops only this work; saved video information, manual Fetch
metadata and Refresh metadata remain available, including accepted manual work.
_Avoid_: enabling or disabling video information, Library or the provider.

**Provider**: an online service supplying video information, such as TMDB, or
IMDb, which answers from IMDb data.
_Avoid_: source, which means a torrent file or magnet.

**Default provider**: the saved provider choice for automatic work and the
starting choice for Fetch metadata. Selecting it preserves automatic download
intent. Refresh metadata uses the provider saved with that file's metadata.

**IMDb data**: IMDb's movie and TV data, downloaded when the person asks and
kept on this computer as a replaceable copy. The IMDb provider answers from it
without sending requests, and movies from every provider show their IMDb rating
from it.
_Avoid_: IMDb database, which suggests the person's own saved information.

**Series**: a TV show whose files Library identifies by season and episode
number.
_Avoid_: show, TV.

**Episode**: one numbered part of a series' season.

**Music information**: the artist, album, track and other tags inside an audio
file.

**Identification**: deciding what movie or TV content a Library video
represents. Its name decides first: a name with a season, such as S01E02,
1x02 or Season 1, is a TV episode, and every other video is a movie, with the
name's four-digit year. A provider match then replaces that with the
provider's title, type and year; the user fetches or erases metadata.

### Subtitles

**Automatic subtitles**: finding and saving subtitles beside managed movies
without interaction, once the person turns it on.

**Subtitle supplier**: the online service that subtitle searches and downloads
use, such as OpenSubtitles or SubDL.

**Subtitle lookup**: one supplier search for a movie and language, including a
search that finds no match. Downloading its returned candidates is separate.
_Avoid_: provider, which means a source of video information in Library.

**Subtitle record**: one subtitle output's path, language and created/found
origin, associated with the source files it serves. Shared torrents use the same
output record.
_Avoid_: added subtitle, which means any subtitle file a person put beside a
movie.

**Created subtitle**: a subtitle file TinyTorrent saved and may follow when its
movie moves, while retaining its association with that movie.

**Found subtitle**: an existing subtitle file recognized beside a movie;
recognition does not give TinyTorrent ownership of the file.

**Subtitle job**: accepted work to find subtitles for one movie location and
language, together with its progress and outcome.

**Blocked subtitle job**: unfinished accepted work waiting for search protection
to permit its request. It has not established that no matching subtitle exists.

**Search protection**: saved local request holds and completed-search counts
that prevent repeated requests for the same unresolved question. Remote waits
and outbound pacing remain binding after a local reset.

**Force fetch**: the person's request for one fresh answer despite local
protection. It does not clear history or bypass a provider's remote wait.
Subtitles show it as Search again.

**Library subtitle search**: the person's request to download subtitles for
finished movies, chosen as New, Missing data or All selected. Its command is
Find, which becomes Stop while the batch runs.
_Avoid_: file finder, which is disk discovery's helper process.

**Missing data**: unresolved questions, including both New and locally blocked
ones. Subtitle matching options still determine what satisfies the request.

**Reset search protection**: a Settings action for one provider or all providers.
It clears local suppression and counts without fetching data or deleting
metadata, subtitle files, user decisions, credentials or remote waits.

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

**Detail**: what one inspector section shows about a torrent, such as its peers
or pieces, or across torrents when no torrent is selected. It is separate from
the torrent list's snapshot.

**Refresh interval**: how often the product window receives routine engine updates.
It does not determine how often the engine records speed history.

**History interval**: the period represented by an average speed sample in a
history range, independent of whether the product window is open.

**Tray**: the application's Windows notification-area icon and menu.

**Splash window**: the temporary window shown while the product interface opens.

**Popup**: the acrylic surface the engine draws itself near the notification
area to show a notice or a prompt. **Owner ruling:** the engine's notices and
prompts use the popup; a Windows dialog, such as a task dialog or message box,
never replaces it.
_Avoid_: toast, balloon, task dialog.

**Notice**: a popup that tells the person something, such as a finished
download or a failure, without taking focus.
_Avoid_: toast; notification, when naming the popup rather than its setting.

**Prompt**: a popup that asks the person to choose before the engine continues,
such as whether to exit another TinyTorrent. It takes focus.
_Avoid_: question, dialog, message box.

**Settings**: the saved, application-wide choices for how TinyTorrent behaves.
_Avoid_: preferences.

**Network adapter**: the Windows network connection that torrent traffic is bound
to.
_Avoid_: interface.

**Page**: a whole view of the product window, such as Torrents, Library, Settings,
or About.
_Avoid_: form.

**Dialog**: a surface over the product window for one task, such as Add or Proxy.
_Avoid_: form.

**Pane**: a side area of a page, such as the Inspector beside the torrent list.
_Avoid_: form, panel.

**TableView**: the reusable, domain-neutral table library used to present the
product's collections.
