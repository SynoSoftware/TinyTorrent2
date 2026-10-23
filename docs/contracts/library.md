# Library

Library is a searchable view of the finished files reported by its data
sources. Video information comes from the selected provider, and music
information comes from the audio files' own tags. Torrents are the first and
only data source. SQLite stores facts about those files; it does not establish
that a file belongs in Library.

This is the contract for the feature. Library does not retain removed torrents
as entries, watch directories, or check file existence automatically. The
[architecture](architecture.md) owns product scope and process responsibilities;
the [engine](engine.md), [pipe API](pipe-api.md), [UI](ui.md),
[localisation](localisation.md), and [testing](../agents/testing.md) contracts govern
their respective concerns. Feature-specific decisions live here.

## Read for the change

Read the purpose, ownership, membership and SQLite rules here for Library work.
Then read the references for the behavior being changed; each is part of this
contract with the same authority.

| Change | Reference |
| --- | --- |
| File properties, music tags, local search or opening outcomes | [Files and search](library/files.md) |
| Identification, provider requests, saved metadata, credentials or privacy | [Identification and providers](library/providers.md) |
| Page navigation, table configurations, filters, Inspector or prototype review | [Presentation and interaction](library/presentation.md) |
| Initialization, synchronization, background work, closing, performance or validation | [Lifecycle and validation](library/lifecycle.md) |

## Purpose and experience

People need to find files they have downloaded without remembering which torrent
contains them. For movies and TV, they also need to search by information such
as title, genre, year, actor or subject. They should be able to do this as they
type, without waiting for filesystem indexing or turning TinyTorrent into a
media player.

Library is a separate page alongside Torrents. It uses the existing TableView
and the application's navigation, visual language and keyboard conventions.
The normal journey is to open Library, type to narrow the file list, select a
file to read its details, and open it in its Windows-associated application.
Finding a known title, choosing something to watch and opening it take priority
when deciding what is prominent. Titles, synopsis, ratings and Open support
those everyday tasks; file properties and maintenance actions remain available
without dominating them. Apply the product's
[usability principle](architecture.md#usability-comes-first) to that hierarchy.

Library lists only finished files. A file appears as soon as it finishes
downloading, without waiting for movie identification or provider access.
Online enrichment improves search and details but is not a prerequisite for
using Library. The details card is a readable text presentation of the file and
its identified movie, episode or music track. Posters, thumbnails and embedded
playback are outside this feature. The only reading of a file is the one-time
read of its dates and, for audio, its tags, described in
[file facts and music information](library/files.md#file-facts-and-music-information).

## Ownership and data sources

**Owner ruling: all Library work lives in C#, in the product window process.**
Library owns local search, identification and file facts. Application wiring
supplies shared source synchronization and one database service. The
[database owner](database.md) defines the complete schema, initializes its
version, and serializes every query, write and cleanup through one transaction path.
Its work ends when the window closes, so the resident engine
keeps no Library projection, database connection or enrichment worker. WinUI
owns presentation, navigation, selection and scroll position; blocking Library
work runs off its dispatcher. Existing engine file operations remain the only
owners of torrent moves, renames and deletion.

A small data-source interface feeds SQLite independently of the mechanism
supplying files. A data source supplies a coherent current contribution and
changes to it:

- Stable entry identity within the data source.
- Current file path, filename, size, completion and wanted-file choice,
  including unfinished files for early identification and subtitle work.
  **Owner ruling:** Library queries show only finished files under their final
  name, because Library is for files the person already has. Download progress
  belongs to Torrents.
- Membership changes, and withdrawal of the whole contribution when its source
  ends, for example when a torrent is removed.

Library consumes these facts through SQLite without depending on torrent-specific
objects. The C# torrent adapter translates existing engine facts received through
the existing pipe and seeds and synchronizes SQLite. The engine supplies current
torrent/file facts, including filename evidence before completion; it does not perform Library
queries or keep another copy of the file catalogue for Library. These features
consume existing torrent/file replies; they add no C++ catalogue, enrichment,
subtitle-file handling or feature API, including under a generic helper name. A data
source's assertion of membership means that it reports the entry, not that
Library has independently verified the file on disk.

Filename interpretation, title and normalized text are computed once on
ingestion, because moving them into queries adds query work without improving
behavior. The filename is the engine's final name, used unchanged. The engine
distinguishes that name from the file's current disk path, and stripping a
textual `.!tt` suffix would also change genuine filenames. One file-kind table
classifies extensions for ingestion and the file tree, and keeps every format
either already recognizes, including NFO, Markdown, subtitles and ISO files. One
glyph map serves both file presentations; Library keeps its
identified-episode TV icon.

These parts divide the work. They are responsibilities in the existing C# app,
not one project, interface or forwarding class per row. Each has one job and
does not know the internals of the others. Feature code lives in
`app/src/Library/` and `app/src/Subtitles/`, following
[app folders](../agents/naming.md#app-folders).

| Part | Owns | Does not know |
| --- | --- | --- |
| Data source (the torrent adapter now) | Seeding and synchronizing current contributions and file facts in SQLite through the pipe client, including unfinished and unwanted files, confirmed withdrawal and shared filename interpretation: kind, title and normalized filename text. It decides neither Library eligibility nor subtitle eligibility and exposes no separate file catalogue | Provider behavior, Library queries and feature eligibility |
| Library | Finished-entry eligibility, effective identification decisions, fact lifetime and its SQL text/filter queries | Torrent internals, provider transport, how a source finds files |
| Library Browser | Query and configuration input, stable row objects, selection, navigation and opening outcomes; Library initialization, worker events, failure pause, retry/resume and worker drain. TableView performs the only sort | Provider transport, SQL |
| Video lookup | Selected-provider requests, cancellation and background eligibility ([replaceable movie providers](library/providers.md#replaceable-movie-providers)). Each provider owns its API decoding; the IMDb provider owns the download, import and local search of the IMDb data. Library accepts identification evidence and saves video information | Source membership, search, presentation |
| File facts reader | One-time Windows property reads for dates, audio tags, media duration, bitrate and video dimensions; Library accepts and saves the returned facts. It lives with Library, its only consumer | Source membership, search, presentation |
| C# database owner (`library.db`) | Connection, authoritative schema, initialization and versioning, transactions and shared consistency rules ([database](database.md)) | Provider behavior, presentation and feature decisions |
| Inspector and opening owners | The shared card mechanics and Windows file execution. Library supplies a target and consumes the outcome | What the target is |

Library and subtitles are sibling modules supplied with these shared resources
by the existing application lifetime owner. That owner starts source
synchronization and supplies the shared database; neither feature creates or
owns the other's lifetime. Subtitle work queries current torrent files in
SQLite, including unfinished files; Library's visible rows and automatic
download setting never gate it. SQLite holds one current-source projection, not
an adapter catalogue beside separate Library and subtitle catalogues. The
database owns transaction mechanics; each feature owns the meaning of its
queries, and domain queries stay beside their feature's behavior instead of
accumulating in a general database class. Sharing the connection does not
require sharing provider scheduling or job states, and these responsibilities
need no common enrichment interface, host class, service layer or repository.
The application shell forwards source readiness and requests fresh snapshots; it
disposes shared resources only after Library has drained. Concrete construction
and provider registration happen in one place, `Features.cs`.

Library reuses what the product already has: the title-bar search, the
workspace and column-layout owners, the filter drawer, the Inspector pane with
its Field and Strip controls, the opening owner, Settings and Strings. Shared
behavior is extracted only where these direct callers need it, and the old
caller moves in the same change. Domain state does not belong in XAML
code-behind or a general-purpose helper.

Stored facts belong to the contribution of their entry. Source synchronization
applies confirmed withdrawal and cleanup in one SQLite transaction, using the
database owner's shared rules to remove associations and unreferenced facts. This runs
even when automatic video information and subtitle downloads are off, so either
choice cannot leave obsolete membership behind. A contribution can have no finished
files: an early identification waits for its file while the torrent remains
managed. Closing or disconnecting the window is not withdrawal.

When the window connects, source synchronization reconciles the complete current
contribution set before serving results or starting enrichment. It deletes facts for absent
contributions then, including torrents removed while the window was closed or
during a crash. Those facts may remain on disk until that reconciliation, but
can never establish membership or appear as current results. No cleanup task
is left in the engine merely to maintain the window's database.

The pluggable interface is an explicit owner requirement so another discovery
mechanism can later supply the same Library. It does not require a dynamically
loaded plugin system, source-discovery framework, separate process or source
configuration screen. Implement only the torrent adapter now.

## Torrent membership

The torrent data source reads files from the metadata of currently managed
torrents. It never discovers them by enumerating the filesystem.

An accepted torrent seeds its known files into SQLite. Library's query includes
each file once synchronized facts show it finished and under its final name.
With a saved catalogue, publish the surviving rows once after reconciling the
complete engine summary; background reads then confirm or replace each list.
Without one, show files as their torrent facts arrive, so a pending torrent does
not hold back the files already collected. The torrent source reads a torrent's
files again whenever the engine reports a change and at most a minute after
its last read, so Library needs no refresh command.
A finished file stays even when the person later
marks it unwanted, because changing its priority must not hide a file the
person already has. No disk probe is added to decide this.

A magnet awaiting metadata cannot yet contribute individual file entries. An
unaccepted Add preview contributes no entries and triggers no Library
enrichment, because previewing a source does not add it to the person's Library.

| Torrent event | Library effect |
| --- | --- |
| Addition with known metadata | Introduce its finished files. |
| A file finishes downloading | Introduce that file. |
| File choices change | No change to finished files. |
| TinyTorrent moves or renames files | Update recorded locations through the existing operation owner. |
| Torrent is removed, keeping payload | Withdraw its contribution and delete everything stored for it. |
| Torrent is removed with file deletion | Withdraw its contribution and delete everything stored for it; the existing deletion operation determines the disk outcome. |
| Another managed torrent still refers to the same location | Keep the location represented through that contribution. |

Membership withdrawal follows confirmed torrent removal, including removal
whose payload cleanup is still pending. Cached video information never delays
or prevents removal. Library needs no retention-before-removal transaction.

There is no separate Remove from Library command. With the torrent adapter as
the only data source, torrent membership explains Library membership.

Engine startup restores torrents without initializing Library. During window
initialization, the C# adapter obtains current membership and file facts;
[work lifecycle](library/lifecycle.md#work-lifecycle) describes reconciliation and readiness.
Enrichment processes available filename evidence while the window is open.
None of these paths inspects download folders. Files kept from torrents removed
before this feature existed cannot be recovered through this data source,
because those torrents are no longer managed.

## Entry identity and file locations

Present one row per known file location. Two torrents referring to the same
location contribute to one row. Separate copies at different locations remain
separate rows even if they contain the same movie.

Reconcile locations using the application's established path-comparison
behavior. Do not read contents, calculate hashes, resolve hard-link identity or
search for duplicate files. Source entry identity is separate from current
path, so a known move or rename can preserve the entry and selection. Removing
the first contributor to a location does not replace a surviving row, and
selection is reconciled explicitly when locations merge or split.

A multi-episode video remains one row. Its video information may describe
several episodes but must not create several apparently separate files.

## SQLite and video information

**Owner ruling: SQLite is the sole data source for Library and subtitles.**
The C# torrent adapter seeds it from accepted torrents, then synchronizes
additions, file changes and removals through the same write path. Both features
read current file facts, eligibility, work and saved outcomes from SQLite;
neither reads torrent objects or keeps a parallel catalogue. This gives every
feature decision the same committed facts instead of competing copies.

The flow is existing engine facts → C# synchronization → SQLite → feature
queries and work. Provider and file workers take bounded query results and
return outcomes for validation and commit against current SQLite state. Query
results, UI rows and in-flight inputs are temporary consumers, not another
source of current membership. Seeding is followed by synchronization, because
a one-time import would miss later completion, moves and removal.

Search, filters, counts, details and work selection use only SQLite. They do
not enumerate folders, probe file existence or ask the engine for data. Exact
file access belongs only to the specified one-time fact reads, explicit Open,
and subtitle acquisition/file operations; those operations use paths read from
SQLite and never discover membership by searching a drive. Blocking ingestion,
SQL and file work runs in C# background work while the GUI is open, so none of
it occupies the dispatcher or survives closing the window.

Existing application settings, such as the torrent proxy, network adapter and
interface language, retain their existing owner. The features consume those
confirmed settings without saving a shadow copy in SQLite; SQLite owns their
file facts, feature settings, decisions and work, not a second application
settings implementation.

**Owner ruling: torrents establish membership; C# owns SQLite.** Managed torrents
decide which files belong. SQLite saves reusable facts and user decisions and
executes local searches over the retained catalogue, whose membership the
torrent summary decides. It is never an independent file inventory. Library
removes a torrent's stored facts when it observes removal or next reconciles,
including video information no remaining entry uses. This keeps Library useful
without adding permanent search or database work to the resident engine.

SQLite stores current torrent/file facts and these feature facts:

- Movie, series and episode information, with provider identities and retrieval
  dates.
- Titles, genres, years, cast, synopsis and subject keywords used by search and
  the details card.
- Associations between source entries and video information, including manual
  corrections and the decision to clear an incorrect identification.
- Each finished file's created and modified dates, and each audio file's tags.
- Derived normalized text and indexes needed by local search.

The governing invariant is:

> Every Library result has a retained data-source file entry. Cached video information
> or an identification association alone can never produce a result.

The single C# database owner retains accepted contributions, entries and paths
alongside enrichment and decisions. Temporary state holds the current summary,
confirmation and retry timing. The former temporary-only catalogue made stale
membership structurally impossible; saved-list browsing replaces that guarantee
with shared confirmation predicates for work and cleanup. The engine remains
authoritative. Queries join the saved catalogue to its metadata. C# subtitle work uses this
same connection, schema and transaction owner; the native engine never opens
the database. Subtitle records are independent of automatic video information downloads.

The current-source SQL projection derives file-read readiness once for both
features: the file list is confirmed in the current connection, the file is
complete, at least one current contributing torrent
reports its existing `complete` fact, and its paths are settled with no move
in progress. Byte counts and an unsuffixed name alone do not prove that pending
writes have ended. Library's one-time file facts reader and subtitle hashing
use this same derived condition; do not persist another readiness flag or add
an engine helper. It describes observed state, not a lock on engine file work.
Library listing still uses per-file completion and final naming, so an episode
can appear before the rest of its pack finishes.

Identification uses source-provided entry identity, not an absolute path alone,
because another file can later reuse that path. Re-adding a removed torrent
creates new torrent membership under the existing identity contract; it does
not resurrect old membership or attach an old association solely by filename.
An identification can reuse provider information that another current entry
already uses, for example a second episode of the same series. Cache reuse is
checked before downloading those details again. Provider-required expiry or
removal still applies to that stored information.

The database contains no media bytes. Its derived search index is rebuildable
from stored information. Manual identification decisions are not disposable
index data and must survive ordinary restarts and index rebuilding.

Apply [saved-value recovery](architecture.md#saved-value-recovery) to readable
saved settings without discarding manual identifications or subtitle records.
If the shared database cannot open or serve queries, report the storage failure
and pause feature work. Keep any last view non-actionable; do not present an
empty collection or open an alternate database that silently omits saved
corrections. Retry uses the same open and source-reconciliation path. Failed
edits stay unsaved, the database stays intact, and normal torrent work remains
independent of this failure.

## Future data sources and exclusions

**Owner ruling:** a later disk data source finds files the way Everything does,
from the file system's own index, never by walking folders. It contributes
through the same data-source interface and owns its discovery method,
permissions and membership lifecycle. Removing a torrent would remove only its
contribution; an independently reported location could remain through the disk
data source.

That adapter, its drivers, elevation flows and configuration screens are future
work. Do not build them or a framework for managing them in the initial feature.
Whole-disk deduplication, automatic recovery of externally moved files, embedded
playback, artwork, recommendations, watch history and online torrent discovery
are also outside this feature.
