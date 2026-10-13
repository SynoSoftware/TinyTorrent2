# Library

Agreed feature plan, updated 2026-10-09. Library is a searchable view of the
finished files reported by its data sources. Video information comes from the
selected provider, and music information comes from the audio files' own tags. Torrents
are the first and only data source in this implementation. SQLite stores facts
about those files; it does not establish that a file belongs in Library.

This is the master plan for the feature, not implementation evidence or an
instruction to begin implementation. The
[implementation plan](library-implementation.md) orders the work. It supersedes earlier conversational
proposals to retain removed torrents as Library entries, watch directories, or
check file existence automatically. The [architecture](architecture.md) owns
product scope and process responsibilities; the [engine](engine.md),
[protocol](protocol.md), [interface](interface.md),
[localisation](localisation.md), and [testing](testing.md) contracts continue to
govern their respective concerns. Feature-specific decisions live here.

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
[file facts and music information](#file-facts-and-music-information).

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

Four parts divide the work. Each one has one job and does not know the
internals of the others:

| Part | Owns | Does not know |
| --- | --- | --- |
| Data source (the torrent adapter now) | Seeding and synchronizing current contributions and file facts in SQLite, including unfinished targets, confirmed withdrawal and shared filename interpretation: kind, title and normalized filename text | Provider behavior, Library queries and feature eligibility |
| Library | Finished-entry eligibility, effective identification decisions, fact lifetime and its SQL text/filter queries | Torrent internals, provider transport, how a source finds files |
| C# database owner (`library.db`) | Connection, authoritative schema, initialization and versioning, transactions and shared consistency rules | Provider behavior, presentation and feature decisions |
| TMDB lookup and file facts reader | Returning video information or local file facts for Library to validate and save | Source membership, search, presentation |

Library and subtitles are sibling modules supplied with these shared resources
by the existing application lifetime owner. Subtitle work queries current
torrent files in SQLite, including unfinished files; Library's visible rows and
enrichment setting never gate it. Domain queries stay beside their feature's
behavior instead of accumulating in a general database class. Sharing the
connection does not require sharing provider scheduling or job states.

Stored facts belong to the contribution of their entry. Source synchronization
applies confirmed withdrawal and cleanup in one SQLite transaction, using the
database owner's shared rules to remove associations and unreferenced facts. This runs
even when Library enrichment and subtitles are off, so disabling either cannot
leave obsolete membership behind. A contribution can have no finished
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
Show those files as their torrent facts arrive, so a pending torrent does not
hold back the files already collected. The torrent source reads a torrent's
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
[work lifecycle](#work-lifecycle) describes reconciliation and readiness.
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
path, so a known move or rename can preserve the entry and selection.

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
executes local searches over a derived current-entry projection. It is never an
independent file inventory. Library removes a torrent's stored facts when it
observes removal or next reconciles, including video information no remaining
entry uses. This keeps Library useful without adding permanent search or
database work to the resident engine.

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

> Every Library result has a current data-source entry. Cached video information
> or an identification association alone can never produce a result.

The single C# database owner keeps current contributions, entries and paths in
temporary SQLite tables populated from source facts. They disappear with its
connection and are rebuilt on reconnect from confirmed source state. Persistent
tables hold enrichment and decisions, not a second saved torrent/file catalogue.
Queries join the current projection to those facts. C# subtitle work uses this
same connection, schema and transaction owner; the native engine never opens
the database. Subtitle records are independent of Library enrichment enablement.

The current-source SQL projection derives file-read readiness once for both
features: the file is complete, at least one current contributing torrent
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

## File facts and music information

**Owner ruling: music information comes from the tags inside the audio file**,
for example MP3 ID3 tags, read once after download writes have finished. The
file names of music releases do not reliably carry artist, album and track.

When SQLite's [file-read readiness](#sqlite-and-video-information) condition
holds, Library's C# file facts reader reads the file once, off the UI dispatcher
and database worker, through the Windows property system that Explorer uses:

- For every file: its created and modified dates.
- For an audio file: Title, Artist, Album, Track, Year, Genre and Duration.
- For a video file: Duration, total Bitrate and frame Width and Height.
- For an audio file: Bitrate alongside its tags.

The saved file-facts row records a successful read, even when Windows returns
no media values. Browsing, sorting, inspector access and restarting never
repeat a successful read. The first-version schema includes
all media-property columns; no earlier database format needs an upgrade.

The store saves these facts with the entry, and they go with the torrent like
every other stored fact. They are not read again: a file is finished, and
TinyTorrent's own moves and renames do not change its content. A file finished
before this feature existed, or while the window was closed, is read once after
window initialization, a few files at a time, once read readiness holds.
Until then, its date and tag cells are empty and its row is still shown. For
a finished track in an unfinished torrent this may wait for the torrent's
completion, because saving a premature empty tag read would lose its searchable
information permanently. Waiting does not count as an attempted read.
A successful read with no tags is a saved empty result. A failed read leaves
the facts unread: an unavailable drive or a sharing violation does not establish
that a file has no tags. Retry through the same bounded reader when its source
confirms a different location or during a later window initialization, once per
trigger; summary refreshes do not retry it.

Music tags are searchable like video information. An audio file without tags
shows its cleaned file name as its title and leaves the other music cells
empty. Reading never writes to the file.

## Local search

Normal typing searches current entries and their available video information
locally. Searchable fields are filename, recorded path, movie/series/episode
titles, genres, years, cast, synopsis, available subject keywords and music
tags.

The initial text behavior is deliberately simple:

- Update results as the person types.
- Require each whitespace-separated term to match somewhere in an entry's
  searchable fields; different terms may match different fields.
- Match without case differences or Latin accent differences, preserving
  original text for display.
- Support filename and title fragments, including one- and two-character
  queries.
- Treat punctuation as text, not database query syntax.
- Show the current source-backed collection when the query is empty.

Filters provide precise narrowing without a query language; each configuration
has its own, described in [filters](#filters). Year labels must identify their
meaning: a series premiere year must not be presented as an episode's air year.
Unknown values remain unknown rather than receiving invented values to fit a
filter.

Typing neither contacts providers nor reads file paths or payload bytes. An
explicit Identify action may perform an online search. Movie information still
being fetched is absent from search until available; filename search continues
to work throughout.

Use one SQLite query implementation in C# for text, filters and their counts.
Update the current projection when source facts or enrichment change, not on
every transfer-speed refresh. Normalize searchable text on change and keep
shared video text once. Do not also maintain a custom in-memory search index,
duplicate filter predicates in the view model, fetch source files on each
keystroke, or perform a database read per row.

Start with parameterized literal substring matching over normalized current
file fields and referenced video text. Each term can match either, preserving
AND-across-terms semantics. An empty query bypasses text matching. Ordinary
indexes serve identities, joins and filters; they are not assumed to accelerate
arbitrary substrings. Measure short and broad searches before adding an index.

Allow at most one executing search and one pending latest query. Replace older
pending queries and stop obsolete executing work at safe points. Start an idle
search promptly rather than adding a long typing debounce; a background worker
alone does not make obsolete work free. No spelling correction, fuzzy matching,
semantic expansion or relevance-ranking pass is required by the initial literal
search experience.

Use TableView's normal sorting behavior for the matching collection. Do not
silently truncate results. Rows and counts come from the same database read
version. Discard obsolete query results so an earlier query cannot replace a
later one. Search and selection details make no engine requests.

Return compact row summaries, not full cast lists, synopsis or provider objects
for every match. Read full details only for the current selection and discard
outdated detail replies. Reuse stable row objects where their facts have not
changed and retain TableView's virtualization. Do not add a second sort before
TableView, rerun search because transfer speed changed, or clear and repopulate
unchanged rows on each ordinary torrent refresh. Source synchronization uses
the existing protocol's bounded, coherent reads. Source-transfer work does not
authorize a new native catalogue or feature transport.

Searching stored text or maintaining an index over it is not filesystem
discovery. It must never introduce folder enumeration. The short-query path
must be verified explicitly: SQLite FTS5 trigram full-text matching alone does
not match strings shorter than three characters, as documented in the
[SQLite trigram reference](https://www.sqlite.org/fts5.html#the_trigram_tokenizer).

## Identification and early enrichment

Choose one provider: TMDB, Wikidata, OMDb, MDBList, IMDb or Rotten Tomatoes.
Provider selection is exclusive, so changing it cancels work belonging to the
previous provider. For the IMDb and Rotten Tomatoes request triggers, cache
rules and browser access, read the [website provider contract](movie-websites.md).
That plugin supports movies from the selected website. Each enabled provider
uses the same background identification workflow; TMDB also supports series
and episodes, and the others support movies only.

The API providers differ in what they return:

| Provider | Access | Information |
| --- | --- | --- |
| TMDB | Built-in key, or the person's own | Localized titles, synopsis, genres, cast, keywords, TMDB rating, and recommended titles that Fetch metadata offers as choices |
| Wikidata | No key | Localized titles, genres and cast, a one-line description as the synopsis, and the IMDb rating that Wikidata editors last recorded |
| OMDb | Built-in key, or the person's own | English titles, full plot, genres, main cast and IMDb rating |
| MDBList | Built-in key, or the person's own | English titles, synopsis, genres, and IMDb and TMDB ratings; no cast |

Each saved response also keeps the Rotten Tomatoes and Metacritic scores that
Wikidata, OMDb and MDBList return, although Library does not show them.

Use names and structured clues such as title, year, season and episode numbers
without reading video contents.

Associate automatically only when the available evidence supports an
unambiguous match with title similarity greater than 80%, consistent sequel
numbers and no conflicting known year. More than one qualifying candidate
remains ambiguous. A first search result is not sufficient evidence by itself.
Unmatched and ambiguous files remain usable under their original filenames,
with the type and year their names give
([identification](../CONTEXT.md)): a name with a season is a TV episode, and
every other video is a movie.
Provide Fetch metadata and Erase metadata actions without prompting
for every file. Erasing an incorrect association preserves the user's decision
instead of immediately recreating the same automatic match.

Manual decisions survive restart and take precedence over automatic work.
Fetch metadata and Erase metadata apply to all current source entries at the selected
location in one SQLite transaction, so cross-seeded files do not show competing
identifications. When locations meet, the most recently saved explicit choice,
including Clear, applies to all contributors. It remains with each if their
locations later separate. Resolve that effective choice once for search, rows
and details; removing one contributor must not undo a surviving entry's choice.
A late reply cannot overwrite a newer correction or recreate an entry removed
from its data source. A file's video information stays only while a current
entry uses it. Everything else a provider returned, its raw pages and every
movie its pages listed, stays saved, so a later lookup of the same movie needs
no request ([keep everything a provider returns](library-implementation.md#replaceable-movie-providers)).

With multiple movies selected, Fetch metadata processes only missing
identifications using the automatic matching policy and opens no dialogs.
It works in the background: the status bar shows "Fetching metadata 3 of 7"
until it finishes, on every page, with an X that stops it and cancels the
request in progress. Fetching more files while it runs adds them to the same
count, and a file already waiting is not added twice. Automatic lookups wait
until the batch ends. A provider failure ends the rest of the batch and reports
the failure as other Library commands do.
Library remembers each automatic search for the session, so files of one movie
search the provider once.
Library retains the complete selection across result refreshes and remembers
it separately for each configuration.

The Subtitles column reads the [subtitle inventory](subtitles.md#subtitle-inventory-and-lookup-counts)
from SQLite: one language is shown by name and multiple languages by code.
Browsing Library never inspects folders.

Share movie and series information across associated files rather than fetching
and storing a separate copy per file. Bound background work, honor provider
throttling, and prioritize explicit identification over routine enrichment.
Try an unidentified video once per session, with the persistent per-provider
search allowance limiting requests across restarts. **Owner ruling: a
provider's no-match answer is remembered.** Background lookups never ask that
provider about the file again while its name is unchanged, because the answer
will not change and each request costs the person's provider allowance. Fetch
metadata and the Fetch metadata dialog still search, because the person asked.
The table shows an em dash in that file's empty provider cells, so a file the
provider does not have differs from one not looked up yet. Provider failure must not
block local search, torrent operations or shutdown.

### TMDB early and background enrichment

With TMDB selected and enrichment enabled, identification begins when a new
torrent supplies enough filename information; video completion is not a
prerequisite. Process only recognizable video candidates for movie/TV identification; other
files retain ordinary filename search without provider requests. Reuse common
series identification for a season pack and fetch episode-specific information
only for represented episodes, rather than importing an entire series catalogue.
Fetch searchable cast and synopsis during enrichment, not only when the details
pane opens, so an actor or subject search can find an unselected file. Do not
retrieve artwork, trailers or unrelated credits as part of it. Recommendations
arrive in the same request through `append_to_response`, so they add no
request; Fetch metadata offers them as choices.

Enrichment runs in C# only while the product window is open and the feature is
enabled, within the [per-movie limit](architecture.md#third-party-provider-access).
Closing the window cancels optional work; the next window tries the files
without a match again. There is no hidden managed process or native fallback. Start with one provider request in flight; concurrency increases only if measured
enrichment delay warrants it without harming interactive work. A provider
request must not occupy the worker that serves interactive Library queries.
Save completed provider information once per coherent result, not as one durable
write per cast member or field. Background work waits when idle; it does not
periodically revisit the whole collection looking for something to refresh.

### Subtitle independence

Movie identification and [subtitle matching](subtitles.md) are distinct C#
responsibilities. Identifying a title does not establish that a subtitle matches
a release. Share applicable identifiers through their C# owners without a native
provider API. Subtitle work never enables or waits for Library enrichment, and
a movie match never establishes timing compatibility.

## File access and unavailable entries

Library performs no folder walking, drive scanning, directory watching,
startup path sweep or periodic file-existence check. The torrent adapter needs
no elevated process, privileged indexing service or Everything installation.
Existing unrelated watched-folder torrent-import behavior is not changed by
this feature.

Selecting a row displays recorded details without accessing its file. Open
uses the existing Windows opening implementation for the selected entry's
current recorded path. Classify an opening failure only as specifically as the
returned outcome supports:

- A confirmed missing file is Missing.
- An inaccessible or disconnected location is Unavailable; an ambiguous path
  failure is not evidence of deletion.
- Failure to find or launch an associated application is an opening error,
  not evidence that the file is missing.
- Another Open attempt is allowed; a successful opening clears its earlier
  failure indication.

These observations do not withdraw source membership or delete a torrent.

External moves and deletions may leave stale recorded paths until opening
fails. Do not search for replacement locations. This accepted limitation keeps
Library free of filesystem discovery and background validation work.

## Presentation and interaction

The [Library prototype](../app/library-prototype.html) opens directly and reuses
the canonical assets in `resources/`. Its layout selector compares A — Reading,
B — Inspector and C — Sections. C is the owner's approved implementation target.
The [implementation review](#prototype-implementation-review)
records the accumulated decisions and outstanding work. Sample data and browser
code are not production architecture.

Every part of the page reuses what the product already has for the same job:
the same controls, the same implementation and the same labels. A second
search box, card, file renderer or name for an existing command is a second
implementation that drifts from the first. A label is new only where the noun
is new.

Help text follows the [interface contract](interface.md#text-icons-and-typography).
Each provider's information flyout carries the disclosure required by
[privacy requirements](#privacy-and-provider-release-requirements).

### Prototype implementation review

This is the acceptance checklist for the current Library design work, recorded
on 2026-10-10. It owns the pending prototype-to-app review, not a second UI
contract. The owner approved C — Sections and authorized native implementation.
Builds and tests need separate owner
authorization; prototype screenshot review is already authorized.

An unchecked item means native implementation and its appropriate evidence
have not both been established. A prototype edit, source review or successful
build alone does not complete a visual or interaction requirement. Record the
changed owner and evidence against each item when implementing; carry unfinished
items forward rather than asking the owner to rediscover them.

#### Shared ownership and prototype scope

- [ ] Reuse the existing Inspector shell, Section, Field, responsive layout,
  typography, spacing, brushes, icons and commands. Audit their owners before
  adding anything; Library must not introduce parallel layout authority.
- [ ] Where a heading composition is missing, polish the shared composition
  and move its applicable consumers to that owner. Do not copy a Library-only
  implementation or add tokens without a distinct, demonstrated need.
- [ ] Keep one Library prototype, `app/library-prototype.html`. The former
  `library-details-prototype.html`, wrapper, duplicate renderers and obsolete
  references must stay removed. Canonical app assets remain referenced, not
  copied into a second icon catalogue.
- [ ] Preserve all three Inspector alternatives during review. Implement the
  finally approved composition; the earlier B selection does not override the
  later approval of C. Keep the prototype's labels, commands and surrounding
  shell aligned with the mature app except for explicitly requested experiments.

#### Inspector composition

- [ ] Use the approved C — Sections composition: Synopsis and Cast form
  the main content, with a separate right metadata column. At narrower widths,
  sections reflow without clipping, collisions or excessive empty gaps.
  Cast uses its own column only when the side-by-side content fits the inspector's
  available height. Otherwise it stays below Synopsis with names wrapping across
  rows, even in a wide window, so the column does not introduce avoidable scrolling.
- [ ] Use one full-width heading above content and metadata. **Refresh metadata**
  sits in the card's More menu beside Fetch and Erase metadata, and retains its
  established command and availability rules. A file without information gets
  a Fetch button at the heading's far right, not stranded at the edge of the
  left content column. It opens the same provider list as Fetch metadata, then
  Settings.
- [ ] Place the media type beside the title, using the latest shared text
  baseline alignment and clear separation. Movie, TV episode and Audio use the
  same composition; do not restore a whole subtitle row merely for type or Year.
- [ ] For episodes, show the episode title first and Series · Season · Episode
  context beneath it. Series and Season are filter links; episode numbers remain
  readable context. Multi-episode files must not repeat the same series/season
  information for every episode.
- [ ] Put video metadata in this order: available IMDb and TMDB ratings, Year,
  Genres. Music uses Track, Duration, Year, Genres. Year appears only in this
  metadata group. This supersedes the earlier Genres-before-Year arrangement.
- [ ] Keep genres last and allow all their filter links to wrap into the space
  below. Keep each genre name together. No clipped links, hidden remainder or
  "+N more" menu; spacing separates links without leading or stranded dots.
  This explicit genre-link treatment is an exception to ordinary single-line
  metadata, not permission to wrap every Field.
- [ ] Keep series ratings identified as series ratings. Preserve missing-score
  behavior and the existing information/provider states rather than inventing
  scores or new provider requests to fill the layout.
- [ ] Match the app's action glyphs and button treatment: Play, Show in Torrents,
  More and Close. Fetch metadata belongs in More at every width. A vertical divider
  separates these item actions from the inspector's Pin and Close controls. Show in
  Torrents uses LocateFixed, never the software logo or a download action glyph.
  The prototype must not replace Play with an external-window glyph.
- [ ] Retain readable synopsis line spacing, heading-to-body spacing and the
  established File tab and Inspector shell behavior. Review the entire window,
  not just the changed text block.

#### Filters and navigation

- [ ] Year and every genre link use the existing filter operation and reveal
  the filter pane, retaining unrelated filters.
- [ ] Music Artist and Album are clickable filters using complete values, not
  pieces split at punctuation or display separators.
- [ ] Episode Series selects that show and clears an earlier Season choice.
  Season selects both its series and season atomically, never the same season
  number across unrelated shows. Use canonical series identity, not its label.
- [ ] All these filters participate in the existing choices, counts, active
  filter display and Clear behavior, including Files when such a filter is
  active. Keep active zero-result choices visible and human-readable.
- [ ] View starts with Filters in both modes. Its open/closed state has one
  shared window owner and survives switching between Library and Torrents;
  the selected filter criteria still belong to their respective page.
- [ ] Resolve the reported filter-pane opening jump/flash. Inspect the initial
  frame, motion direction and simultaneous content/Inspector movement; a smooth
  tail after an initial layout jump is not a fix. Native runtime diagnosis is
  still outstanding; the earlier static trace did not establish a cause.
- [ ] Double-click and Enter invoke Properties and never launch an external
  player. Playback requires an explicit Play/Open action, using the existing
  command owner.
- [ ] Preserve the existing Show in Torrents command: select the owning torrent
  and reveal the exact file in its Inspector's Files section, clearing a blocking
  file search and expanding its ancestors. Back / Alt+Left returns to the same Library selection,
  query, filters, scroll and Inspector context. Reuse the command from every
  offered surface; account for files held by more than one torrent through the
  existing origin model.
- [ ] Offer Show in Torrents directly in the Inspector title row beside the
  existing actions, with a tooltip and accessible name. Retain its More route
  when the narrow layout hides header actions. The owner approved this placement.
- [ ] Share Inspector Pin, Close and View visibility behavior across both modes.
  Pin retains the current content in a ten-pixel bottom bar with a horizontal
  Ellipsis; showing after hiding restores the collapsed or expanded presentation.
  Properties and exact-file navigation expand it. Collapsing preserves drafts;
  hiding uses the existing draft guards.

#### Approved title-bar navigation

The [title-bar contract](interface.md#native-controls-and-visual-authority) owns
the approved navigation and caption behavior.

- [ ] Remove the Torrents/Library page selector and add no navigation row or
  mode-selection menu. Use the existing app-icon position as the direct page
  switch; keep the File/current-page/View/Help menus.
- [ ] Use the actual `resources/tinyTorrent.svg` for Torrents and the Library
  face for Library. Do not substitute a Lucide download glyph for the brand.
- [ ] At rest, show the current page's face. Hover or keyboard focus previews
  the destination with the two-sided flip demonstrated by LabServer. Respect
  reduced motion and provide a destination tooltip and accessible name.
- [ ] Clicking commits the displayed destination and leaves that face visible
  through the rest of the same hover/focus session. Leaving keeps the current
  face; only the next hover/focus entry previews the other destination. Verify
  both directions, including after rerendering, so click does not flip back.
- [ ] Keep Ctrl+1 / Ctrl+2, search, current-page menus, focus and Settings/About
  return behavior consistent with the existing navigation owner.

#### Outstanding decisions

- Library status-bar emphasis is under discussion. Review file counts and
  active filters against transfer/environment readings, including the
  prototype's repeated filtered count. Preserve one shared status-bar owner;
  no new arrangement has been approved.

#### Implementation evidence and handoff

Prototype evidence on 2026-10-10: movie, episode and music compositions were
visually inspected; an eight-genre sample was inspected at 1280 and 900 pixels.
The actual-brand icon flip was captured through hover, click, leave and re-entry
in both directions after fixing the post-click reversal. The new Inspector
Show in Torrents action was captured selecting the sample's source torrent,
opening its Inspector, and returning to the same Library selection and open
Inspector through Back. These observations do
not establish native parity or validate the outstanding native filter animation.

The canonical prototype was committed at `8b4b3ca`, with the final icon/action
corrections at `ef37eb4`. It now opens C by default and reads the vendored Lucide
font instead of keeping copied icon outlines. The later HTML capture review
inspected the movie and 900-pixel episode compositions and the More menu with
Edit. Show in Torrents uses the locate glyph in these renders.

Native source now composes C through `Details`, shared `Section`, `FieldColumns`
and `WrapPanel`; `Browser` and `Store` own the extended filters. `OriginActions`
shares the one-source/direct and multiple-source/choice presentation across the
Inspector and Library menus, retaining `Browser.ShowTorrent` as the operation
owner. `MainWindow` retains page navigation and owns the app-icon switch. These
changes have source review only; no build, test or native capture has run for
this batch. The filter-opening jump remains unresolved pending runtime evidence.

The subsequent menu review added the missing choice icons and moved Filters
first in both modes. Native pane visibility now has one owner in
`MainViewModel.FilterPane`; Library requests opening instead of storing a
second value. The pane distinguishes hidden, compact and open; Close hides it,
while Minimize keeps a ten-pixel bar with EllipsisVertical and leaves View > Filters checked.
Hiding preserves the compact or expanded presentation, and View restores it.
HTML captures confirmed the menu order and icons, and opening in
Library persisted into Torrents while closing there persisted on return. Native
source review found no remaining issue; runtime verification is still pending.

- [ ] Audit all unbuilt native changes against the approved composition and
  interactions; earlier partial edits do not establish parity.
- [ ] Compare native code with this checklist and the approved prototype item
  by item. Resolve drift in the owning control, formatter or command, and remove
  implementations made obsolete by the change.
- [ ] After authorization, build and run only the checks justified by
  [testing](testing.md). Use the app's existing off-screen capture layer for
  native visual evidence; capture before/after when useful, at least after.
- [ ] Visually inspect movie, episode, multi-episode, music and long-genre
  content, missing information, wide/narrow layouts, and the affected language
  and theme states. Inspect interactions as well as still frames where motion
  or navigation is involved.
- [ ] Judge regression by whether the whole app and the changed area look and
  work worse, not pixel equality. Fix related problems found during review;
  record unrelated or deferred findings explicitly with their remaining work.
- [ ] Report implementation, source review, build/test evidence and visual
  evidence separately. Close this checklist only when the relevant evidence
  exists, and update the existing Library/interface contracts to the approved
  result rather than retaining competing specifications.

### Page and menus

**Owner ruling: Library is a page, a peer of Torrents, not a View of Torrents.**
The [interface contract](interface.md) owns the title bar, which holds the page
switcher, and the rule that the second menu is the current page's menu. On
Library, the menus are:

| Menu | Items |
| --- | --- |
| File | Unchanged. |
| Library | Open, Open folder, Properties, Show in Torrents; Fetch metadata, Download Subtitle; Erase metadata. The row context menu has the same groups and a selection heading. Download Subtitle is a direct action; multiple selections use Download subtitles. Single and multiple selections use the same command owners and Lucide icons. |
| View | Filters first; then Videos, Music, Files as choices. Library has no toolbar, so View has no Toolbar item there. |
| Help | Unchanged. |

### Search

The title bar follows the product's [search behavior](interface.md#main-window).
Its Library placeholder is "Search Library". Live filtering uses the stored
Library fields described above; no suggestion list covers the filtered rows.

### Configurations

**Owner ruling: Videos, Music and Files are configurations of one table on one
page.** Each configuration is a row set and its own saved column layout, sort
and filters. Nothing else on the page changes. The person chooses one in View;
Videos is the default, and the window remembers the last choice.

| Configuration | Rows | Default columns and sort | Hidden columns, available from the column menu |
| --- | --- | --- | --- |
| Videos | Video files | Title, Type, Released, Duration, Resolution, Bitrate, Subtitles, IMDb, TMDB, Genres; sorted by Title | Cast, Size, Name, Folder, Date modified |
| Music | Audio files | Title, Artist, Album, Track, Duration, Year; sorted by Artist, then Album, then Track | Bitrate, Genre, Name, Folder, Size, Date modified |
| Files | Every file | Name, Folder, Size, Date modified; sorted by Name | Kind, Title, Duration, Resolution, Bitrate |

**Owner ruling: the table leads with what the person remembers.** In Videos and
Music, that is the title, not the file name or location, so Name and Folder
are hidden by default and appear at the end when the person adds them.

- Title is never blank. A file without video information or tags shows its
  cleaned file name. An episode's title includes SxxEyy and the episode name.
- Type, in Videos, is Movie or TV episode, from the provider match or else
  the file name.
- Released is a movie's release year or an episode's air year, or else the
  year in the file name.
- **Owner ruling: IMDb and TMDB have separate columns in Videos and both
  available scores appear in the Info details.** Each column holds only the
  number; the header identifies its source. Details group the scores with
  their source labels and scale, so they read as related facts. A series rating
  is identified as such rather than presented as an episode rating. A missing
  score stays empty; it is not zero.
  The selected provider supplies the scores it knows through its existing
  metadata response; showing ratings does not start another provider lookup.
- Kind, in Files, is the file's kind from its extension, as Explorer names it:
  Video, Audio, Picture, Document, Archive or Other.
- Date modified is the file's modified date from its
  [file facts](#file-facts-and-music-information).

The status footer names the configuration and its row count, for example
"Videos · 19", and the active filters.

### Filters

Filters use the existing filter drawer, opened from View > Filters, never a
toolbar button. Each configuration has grouped single-choice sections with
counts. Sections combine: a row must match every chosen section. Each section
lists choices that have rows. Counts apply the query and other sections' choices;
they ignore their own section's choice, so a person can see the alternatives.
Keep a selected choice visible even when its count reaches zero, so it can be
cleared. Clear resets every section.

| Configuration | Sections |
| --- | --- |
| Videos | Type: All, Movies, TV episodes. Genre: the genres present. Year: the years present, newest first. |
| Music | Genre and Year, from the tags. |
| Files | Kind: the kinds present. Date modified: Today, Yesterday, This week, Last week, This month, Last month, This year, Older. Size: Explorer's ranges, Empty (0 KB) to Gigantic (> 4 GB). |

The date choices use calendar boundaries and can overlap: This week includes
Today but can begin in the previous month or year. A file counts in every range
that contains its date, so a range is never empty while it holds files.

### Details card

**Owner ruling: the details card is the torrent Inspector card**, with the same
implementation and layout: the card under a splitter, its title row, the
SelectorBar in that title row, the Wide and Narrow states, the empty state and
the error InfoBar with Retry. Library adds no second card.

- The title row shows the file's type glyph, its name and its size. The
  SelectorBar offers "Info" and "File" for both video and audio. A file that is
  neither opens on File, with the first item disabled.
  Play, Show in Torrents and More follow, then Close. In the Narrow state, Play
  and Show in Torrents move into More. Fetch metadata stays in More at every
  width.
- The approved C — Sections Info composition is recorded in the
  [prototype implementation review](#prototype-implementation-review).
  The File page and shared Inspector shell retain their existing
  structure.
- Year and individual genres in Info apply the corresponding table filter and
  open the filter pane, so the person can see and change the active filters.
  They retain other filters and use the same filtering operation as the pane.
  In Files, these additional filters stay visible in the pane while active.
- The File page describes this one file, not its torrent, because General
  describes the whole torrent. It uses the same `controls:Field` grid:

| Left column | Right column |
| --- | --- |
| Name | Size |
| Folder, with Open folder | Type of file: Windows' name and the extension, for example "MKV Video File (.mkv)" |
| Torrent, with Show in Torrents; one row for each torrent that holds the file | Opens with: the default app's icon and name |
| | Created |
| | Modified |

Windows supplies the type name, the default app and its icon, as it does for
Explorer. The Open button's tooltip names the same app.

**Show in Torrents** is one command, from the Inspector title, its narrow More
fallback, the File page, the Library menu and the row context menu. Its LocateFixed
glyph means reveal the source; the app logo is not a torrent glyph. A single
origin opens directly; multiple origins offer named choices. It switches to
Torrents, selects the torrent and reveals the exact file in its Inspector's Files
section. Reveal clears a blocking file search, expands the file's ancestors and
selects and scrolls to the file, so a large torrent does not leave the person
searching for it. Back then returns to Library with the
same row selected.

**Metadata.** In the Library menu, the row context menu and the card's More
menu, Fetch metadata is a submenu that lists every provider, so the person
chooses the provider for this request; the card's Fetch button uses the one
saved in Settings. Fetch metadata opens the same dialog for one file with or without
an existing match, starting at the chosen provider. It is titled "Fetch metadata", with Save and Cancel.
With multiple movies selected, the command silently fetches confident missing
identifications from the chosen provider and leaves existing matches and
ambiguous results unchanged.
While it searches or saves, an indeterminate progress bar under the search box
shows that the provider request is running.
A Provider choice above the search box lists every provider and starts at the
one saved in Settings. Choosing another searches it again; the search and the
saved match use that provider for this file only, and Settings stays unchanged.
When a provider cannot use the saved network route, the error offers a Settings
link that closes the dialog and opens Network settings. Other provider errors
offer no link, because Settings cannot fix them.
More holds Erase metadata. The Library menu and row context menu use these
same commands and labels.

### States

The page has no loading state; see the owner ruling under
[performance](#performance-and-implementation-constraints).

| State | Presentation |
| --- | --- |
| No files | "No files yet", with a button that opens Torrents. |
| No matches | "No videos match" and the query (or music, or files), with one button for each other configuration that has matches, and its count, plus Clear search. The action buttons share the widest button's natural width through the shared ActionButton alignment. |
| Engine disconnected | The window's [message bar](interface.md#message-bar). Open and Identify are disabled. |
| Database unavailable | The window's [message bar](interface.md#message-bar) reports the failure and offers Retry. Any last view stays non-actionable; normal torrent work remains available. |
| Download off | Saved video information stays visible and can be erased. Fetch metadata names its provider, so it works; Refresh metadata uses the provider in Settings, so it is unavailable. A file without information shows "Download off" in its Metadata field. Its Fetch button lists every provider, then Settings, which opens Settings with focus on Download video information. The status bar names the state. There is no notice above the table. |
| Provider failure | "Waiting for TMDB" in the card and the status bar. No dialog. |
| Failed Open | The title-cell and card glyph show the failure, and the card shows an error InfoBar with Missing, Unavailable or Couldn't open, the time it was checked, and Retry. |

TMDB's attribution is under Credits on the About page, not on the Library page.

### Interaction

Enter and double-click show and expand the selected file's inspector through
Properties, including when it was hidden or compact, so browsing does not
launch an external application. Only an explicit Open action
opens the file, targeting stable entry identity. Sorting or a late reply must
not change which file the action opens.
Preserve query, configuration, filters, selection and scroll position across
page navigation. When an entry disappears, recover focus predictably and stop
presenting its old details as actionable.

Follow existing keyboard, screen-reader, localization and theme conventions,
including English and Spanish. Routine enrichment errors never open a modal
prompt. A disconnect does not prove that the data source has withdrawn all its
files; reconcile with current engine state on reconnect.

## Privacy and provider release requirements

Normal browsing and typing stay local. Online enrichment starts Off. Choosing a
provider in Settings is the one explicit enablement; there is no confirmation
dialog and no per-file consent, because the choice is already the person's
decision, as with Windows' own online settings. Choosing another provider in
Fetch metadata is the person's explicit request for that one file and needs no
further consent. Choosing Off stops further
provider work and cancels pending requests where possible. Cached information
remains usable only as permitted by the applicable terms; source entries and
filename search remain usable regardless.

Settings presents Download video information as one choice: Off, then the
providers. Off stops downloading only; saved information stays. Its information button opens the selected provider's disclosure, the
inputs it receives, and links to its privacy policy and terms, in the same
flyout as the subtitle supplier's. For a provider that takes an API key, a key
button beside it opens the [account dialog](architecture.md#third-party-provider-access),
where the person enters their own key or, when the release has one, keeps the
built-in key. Choosing such a provider when it has no usable key opens that
dialog: Save stores the key and turns the provider on, and Cancel leaves the
choice unchanged. Any other provider that is unavailable when chosen
changes nothing and shows its error in the row. Library's Settings button opens
this row.

Send only the matching inputs a request needs. Do not upload a Library
inventory, absolute paths, torrent info hashes, trackers, peers or movie bytes.
Use HTTPS and keep credentials and search contents out of routine logs. State
the actual network route before enablement. The shared
[network-route rule](architecture.md#network-route) governs every provider;
disclosure never permits a direct fallback. A provider that cannot honor the
configured route remains unavailable. The current website reader runs
only with confirmed unrestricted direct access and stops when that route changes.

Follow [third-party provider access](architecture.md#third-party-provider-access).
TMDB, OMDb and MDBList use the free application keys supplied with the
build; the person does not need an account or key. A person who wants their own
allowance enters their own key, which replaces the built-in one for that
provider. No paid access is part of this feature. Without a key, local Library
search and saved information remain usable, but no requests to that provider
run. Provider limits use the existing wait and retry behavior rather than a
paid fallback; a refused key stops that provider's lookups until the person
changes the key. Wikidata needs no key. IMDb and Rotten Tomatoes
availability is independent of these keys; its [request policy](movie-websites.md#requests-and-saved-information)
owns retries and saved data.

Verify applicable API/content permission, attribution, credential distribution,
quotas, retention and cache-use requirements. Do not substitute an arbitrary
180-day expiry for verified terms. TMDB's [published FAQ](https://developer.themoviedb.org/docs/faq)
describes attribution and commercial/noncommercial access; the applicable
agreement must cover the shipped integration. The attribution goes under
Credits on the About page. It adds no text or movie artwork to the Library
page.

The repository's MPL 2.0 code license is distinct from permission to use provider
content. No copyright-ownership checkbox, per-file legal confirmation or
unrelated content restriction is added without a concrete applicable
requirement. Provider release work does not prevent implementing or using the
local torrent-backed search. This plan is not a certification of legality.

## Performance and implementation constraints

**Owner ruling: Library is ready when the person opens it.** The page never
shows a loading or preparing state, because a person who opens Library came to
find a file now. Initialization uses existing torrent facts and local SQLite
reads, never a scan of download folders. C# ownership alone does not prove it fast. If
building the data takes long or needs minutes of scanning, the feature
is badly designed or badly implemented. That is a defect to fix, not a state to
show the person.

Fast file finding is the feature's primary requirement. Moving slow work to a
background thread is insufficient if it still competes for the same CPU, disk,
database connection or command queue. Keep discovery, enrichment, search and
presentation costs separate enough that the optional work cannot hold up the
user's next action. This does not call for a new scheduling framework.

Keep blocking database and network work off the engine message loop and the
UI dispatcher, following existing completion and shutdown ownership. Bound
background requests and pending reads so rapid typing and enrichment cannot
build an unbounded work queue. Source facts and cached video information are
the inputs; total disk capacity and unrelated folders do not affect the work.

### Work lifecycle

- Engine startup restores torrent work only. It does not open SQLite, construct
  Library entries or start provider work, whether or not a window will open.
- During normal window initialization, the C# torrent adapter reads the complete
  accepted-torrent set and needed file facts through the existing pipe. It
  reconciles current contributions, including those with no finished files,
  before deleting stale facts or admitting enrichment. A disconnect or partial
  read is never evidence that missing contributions were removed.
- Populate temporary membership and search tables on the database worker.
  Reconcile source changes received during initialization before publishing the
  collection. Read stored facts in sets, not once per row. No payload read or
  provider request is a prerequisite for filename search.
- C# synchronization compares source snapshots and updates affected torrents and
  files through existing coherent reads; do not ask for every torrent's file
  progress on each summary tick. Source retrieval stays bounded and allows
  commands through. The existing pipe reads one torrent's files at a time and
  supplies no file-list revision. Aggregate progress does not identify every
  file change; synchronization must cover those changes.
- SQLite has one C# connection owner and short serialized database jobs, shared
  by Library and subtitles. Searches retain only the active and latest pending
  query; writes and source updates must still make progress during rapid typing.
  Membership, decision changes and reads use that same ordered path; workers
  return proposed facts for acceptance there. Network requests and Windows
  property reads never occupy that worker.
- Hidden Library pages cancel queries/detail reads and stop rendering and
  sorting. Source synchronization and enabled enrichment may continue while the
  window remains open. Keep only current row/navigation state, no query history.
- Closing the window cancels provider/property work, finishes short accepted
  durable edits through the normal close guard, and releases the database,
  projection and rows. Do not keep a hidden process or wait for network timeout.
  The next window reconstructs current membership before resuming saved work.
- With no source changes or pending work, Library has no periodic folder checks
  or cache refresh loop. Provider-required expiry is handled off the query path.

Use a managed SQLite binding in the existing C# application. SQLite's native
runtime dependency belongs to that process alone. The engine project removes
its SQLite compile/link references when its old Library implementation is
removed; installed dependencies in `3rdParty/` remain untouched. This work
neither adds a managed runtime to the engine nor shares a database across
processes.

One connection, schema and transaction owner serves Library and subtitles because
both use this C# database. Keep their domain decisions with their respective
owners. No ORM, generic repository, second index service, pooled connection per
query or automatic WAL requirement is needed. Add derived indexes only for
measured queries; a trigram index must still preserve short-query behavior.
Manual corrections and subtitle outcomes survive projection rebuilding. Never
delete the database to repair a disposable search index.

### Cost and user benefit

The following work is retained because it serves an agreed user task. Its cost
must be paid at the indicated time, rather than repeatedly while typing.

| Work | Cost and why it is needed | Limit on that cost |
| --- | --- | --- |
| Keep current file entries | A temporary SQLite projection while the window is open; required to search across torrents. | Build during window initialization from existing facts; update affected entries, with no folder access or per-keystroke engine enumeration. |
| SQLite cache and identification writes | C# dependency size and local disk I/O; prevents repeated downloads and preserves corrections. | Read in sets, save coherent changes, reconcile removal when connected, and keep maintenance off the first-table path. |
| Early provider enrichment | Network traffic, parsing and small database writes; enables genre, actor and subject search before download completion. | Enabled explicitly, one request in flight initially, cached records reused, shared requests deduplicated, no unused-series harvesting. |
| Cast, synopsis and keyword search | More text to store and match; directly required by actor and subject searches. | Keep only useful fields, normalize on change and match distinct shared records once per query rather than once per file. |
| One- and two-character matching | Can require examining active text because a trigram index cannot answer every short query; necessary for results from the first character. | Examine current entries and referenced titles only and cancel obsolete queries. |
| Combining shared file locations | Path comparison and a membership lookup; prevents duplicate rows for cross-seeded files. | Reconcile when a contribution changes using existing path rules, without file hashing or pairwise comparisons of every file on each query. |
| Complete results and sorting | Summary serialization, row state and ordering; users must find every matching file and use the existing table. | Compact summaries, selected details only, existing virtualization and one sorting owner; optimize transport only when measured. |
| Details card | One local detail read and visible text layout; lets users choose and open the right file. | Fetch only the current selection, discard obsolete reads, and do not instantiate details for hidden rows. |
| Pluggable data-source interface | A small integration seam; explicitly requested to support future origins of files. | One torrent adapter, no runtime plugin loader, extra process or source polling framework. |

Continuous Library per-file progress, proactive disk validation, artwork,
unreferenced provider refreshes and speculative search features do not earn
their recurring cost. They are excluded rather than merely assigned a lower
background priority. On-demand Windows opening can still wait on the target
drive or associated application; that latency is not part of local search and
must not occupy the database worker or UI dispatcher.

### Measurement gates — cancelled

**Owner ruling, 2026-10-09:** the numerical search, first-open, memory and
startup gates, both large-collection benchmarks, baseline comparisons,
profiling and transfer measurements are **cancelled for this delivery**.
They were delaying the feature. Existing failed measurements remain failed;
no numerical performance claim is made.

Keep the implementation constraints above: complete results, bounded work,
local search independent of enrichment, and no resident feature work after
window closure. Cancellation does not require a TableView redesign, a saved
stale file catalogue or new engine communication.

## Verification

Follow [testing](testing.md) and the
[current delivery plan](library-implementation.md#finish-the-delivery).
Comprehensive checklists and agent interaction runs are **cancelled**. Reuse
existing valid evidence and review the settled change once. A new check must
address a concrete failure introduced or exposed by that change, especially
data loss, stale writes or incorrect file operations; it is not a coverage
exercise. The approved prototype remains the presentation reference.

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
are also outside this plan.
