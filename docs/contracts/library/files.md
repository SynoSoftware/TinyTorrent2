# Files and search

Part of the [Library contract](../library.md). Read its shared ownership,
membership and SQLite rules before the sections here that the change affects.

## File facts and music information

**Owner ruling: music information comes from the tags inside the audio file**,
for example MP3 ID3 tags, read once after download writes have finished. The
file names of music releases do not reliably carry artist, album and track.

When SQLite's [file-read readiness](../library.md#sqlite-and-video-information) condition
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
has its own, described in [filters](presentation.md#filters). Year labels must identify their
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
AND-across-terms semantics. SQL wildcard characters in the typed text never
change how punctuation matches. An empty query bypasses text matching. Ordinary
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
fails. A file moved while the window was closed keeps its old path until its
torrent's file list is read again; Open in that gap reports Missing with
Retry, as any recent-files list does. Do not search for replacement locations. This accepted limitation keeps
Library free of filesystem discovery and background validation work.
