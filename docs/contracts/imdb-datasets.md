# IMDb datasets

The IMDb provider and IMDb ratings for every provider's movies. The
[Library contract](library.md) stays the authority for Library behavior.

## Summary

The IMDb data goes into SQLite, in a separate file, `imdb.db`, beside
`library.db`. It is never put into `library.db`.

- The person selects **Download** in Settings. The app downloads the IMDb files
  once and builds `imdb.db` from them. After that, every lookup reads the local
  file and sends no request.
- The **IMDb** provider searches and describes movies and series from `imdb.db`.
  Library matching, saving and display are the same as for every provider.
- No plugin reads the IMDb or Rotten Tomatoes websites.
- The person selects **Update** to get newer data. Nothing downloads unless the
  person asks for it.

IMDb offline is managed only in Settings
([owner ruling](search-protection/local.md#imdb-datasets-and-local-answers)).
Download and Update obtain the complete dataset as one operation: the import
builds the one installed database from the basics, ratings and episode archives
below, and lookups only read it. There are no small per-movie IMDb downloads.
Dataset transfers follow the shared search protection without an IMDb-specific
bypass. A provider that answers from a local file declares its `Dataset` in
`ProviderOption`; only a provider that answers over the network fetches an
individual movie (`ProviderOption.IsOnline`, owned by the same
[search protection section](search-protection/local.md#imdb-datasets-and-local-answers)).
Installing IMDb data does not enable per-movie downloads.

The result: IMDb titles, years, genres and ratings for every movie, read in
milliseconds from a local file instead of about ten seconds of browser work for
each movie.

## What IMDb publishes

IMDb publishes its
[non-commercial datasets](https://data.imdb.com/non-commercial-datasets/)
at `https://datasets.imdbws.com/`. IMDb refreshes them daily. Each file is
gzip-compressed, tab-separated UTF-8 text, and `\N` marks an empty value.
Titles contain literal `"` characters and the files use no quoting, so the
import splits each line on tabs only. A CSV parser would break on those titles.

On 2026-10-13, `title.basics.tsv.gz` was 228.5 MB. The other figures are
approximate, from 2025.

| File | Holds | Compressed | Rows |
| --- | --- | --- | --- |
| `title.basics` | Type, title, original title, adult flag, years, runtime, up to three genres | 228.5 MB | about 11 million |
| `title.ratings` | Average rating and vote count | about 8 MB | about 1.5 million |
| `title.episode` | Series, season and episode number for each episode | about 50 MB | about 9 million |
| `title.principals` | Main cast and crew for each title | about 700 MB | about 90 million |
| `name.basics` | Person names | about 280 MB | about 14 million |
| `title.akas` | Titles by region and language | about 450 MB | about 50 million |

**The data has no plot, synopsis or poster.** The IMDb provider therefore shows
no synopsis. The other providers keep supplying it.

## Where the data goes

**One separate SQLite file, `imdb.db`, in the same folder as `library.db`.**

| Option | Result |
| --- | --- |
| Separate `imdb.db` | Chosen. See the reasons below. |
| Tables in `library.db` | Adds hundreds of MB to the file that holds the person's own decisions. A rebuild locks the Library while it runs. |
| Keep the TSV files and read them | Each lookup reads up to 1 GB of text. Too slow. |
| Load the data into memory | Uses hundreds of MB of RAM for the whole session. |

Reasons for the separate file:

1. **The data is a replaceable copy.** In the terms of the
   [database contract](database.md), all of it is reconstructed state: a
   download rebuilds it completely. `library.db` holds durable state, such as
   manual matches, which the app cannot rebuild.
2. **A failed update leaves the old data.** The import writes `imdb.db.tmp`.
   Only a complete import replaces `imdb.db`.
3. **Replaceable schema.** The file name carries the schema
   version: `imdb-2.db`. A schema change uses a new name, so a file with
   another version counts as missing, and the person downloads again. The
   install deletes data that an earlier version built, because nothing reads
   it again. The name
   lets the provider check for the file without opening it. `library.db`
   versioning does not change. The rest of this document calls the file
   `imdb.db`.
4. **Deleting it is simple.** Deleting the file frees all the space. Delete
   database in Settings does not touch it.

**A damaged `imdb.db` must never count as a damaged `library.db`.**
`Database.Run` treats `SQLITE_CORRUPT` and `SQLITE_NOTADB` as damage to the
Library database, and the app then offers to delete `library.db`. An error
from the IMDb file must never reach that path, because it would offer to
delete the person's manual matches because of a cache file. The two files stay
on separate connections for this reason.

## Phase 1: the IMDb provider

Phase 1 downloads `title.basics`, `title.ratings` and `title.episode`: about
290 MB.

### Download and import

1. A build that an earlier crash left behind is deleted when the import
   starts.
2. Each file downloads through the HTTP owner, so the saved proxy and network
   adapter apply ([network route](architecture.md#network-route)).
   `ProviderHttp.Send` reads a whole reply into memory, up to 16 MB, and cancels
   any request that takes more than 60 seconds. The HTTP owner therefore has a
   `Download` method that writes the reply to a file. Its timeout counts from
   the last bytes received, so a 228 MB file on a slow line does not fail. A
   separate `HttpClient` would bypass the saved route, so it is not an option.
3. Both compressed files download beside the data, each to a `.part` file
   that is renamed when it is complete. The import then reads them, through
   `GZipStream` when they start with the gzip signature and as plain text when
   the network already unpacked them. The progress therefore has two clear
   stages, download and import. An install deletes the archives. A failed
   import keeps them, and a build within a day imports them again without a
   download, because IMDb updates the files once a day and must not see the
   same download twice. A damaged archive, or one whose format changed, is
   deleted, because it would fail again. **Delete data** deletes kept archives
   too. A failed download starts again from zero.
4. The import checks that each file's header names the columns it reads, and
   reads one line at a time. It keeps only the rows Library can use:
   - `titleType` is `movie`, `tvMovie` or `tvSpecial`, kept as movies, or
     `tvSeries` or `tvMiniSeries`, kept as series. `video` stays out,
     although it holds direct-to-video films, because IMDb files music videos
     under the same type. Adult titles stay in.
   - A movie or series has a `startYear`, so matching can distinguish releases
     with the same name. Missing or populated years do not establish release
     status; IMDb also gives planned films a year, so some unreleased titles
     stay.
   - A `tvEpisode` has a series, a season and an episode number in
     `title.episode`, because a file names an episode only by those. The
     import reads `title.episode` first, into memory, and writes the
     episodes under their numbers; the one pass over `title.basics` then
     keeps the title of each episode that has numbers. A bitmap retains
     membership by numeric IMDb ID, so the episode records can be released
     after linking without sorting them again or searching them for each title.

   An import that keeps no title, no rating or no episode fails, so data that
   IMDb changed never replaces the installed copy with an empty one.
5. The rows go in one transaction, with reused native SQLite statements, which
   avoid parameter lookup and reader allocation for each row, and with
   `journal_mode=OFF` and `synchronous=OFF`. These settings are safe because
   the file is temporary until step 7. Inserts use 100-row `VALUES` batches;
   the last batch prepares exactly the remaining rows. The widest insert
   uses 1,000 parameters and about 2 KB of SQL. Text stays in bound values,
   so long titles do not enlarge the SQL statement. The import reads
   `title.ratings` into memory, sorted by title, before the pass over
   `title.basics`, and writes each title's rating and votes with its row;
   updating the rated titles afterwards read a random page for each of them.
   The sorted ratings stay in memory through the titles pass, which costs about
   65 MB of peak working set. The episodes go in primary-key order,
   sorted in memory, because inserting millions of rows in file order into a
   `WITHOUT ROWID` table touches a random page for every row, while sorted
   rows extend the table at its end. Each episode's title is a separate table
   keyed by episode and written in file order; the provider joins the two. The
   phase counts the rows it writes.
6. After the rows, the import builds the two key indexes and fills the
   full-text index in batches of 10,000 titles. Reading the ordered title keys
   gives each batch its bounds and exact progress, and SQL copies the text, so
   strings do not cross into managed code and back for every title. The index's
   `rebuild` command does the same work and reports nothing. The one
   transaction commits after indexing, before `optimize`; that commit is not
   publication of the installed dataset.
   `INSERT INTO title_search(title_search) VALUES('optimize');` then merges the
   index structures. It can take substantial time and SQLite reports no
   percentage for it, so it is its own indeterminate, cancellable phase
   ([SQLite FTS5 optimize](https://www.sqlite.org/fts5.html#the_optimize_command)).
   Scheduling, priority and cancellation are in
   [import execution](#import-execution-and-sql-design).
7. The import closes its connection, flushes the file to disk, and moves it over
   `imdb.db` with `File.Move` and overwrite. `File.Replace` fails when
   `imdb.db` does not exist yet. The flush prevents a power cut from leaving a
   renamed file with missing data. Installation is the irreversible publication
   point.

### Import execution and SQL design

The disposable build file and `VideoLookup.Reload` coordinate the install. The
installed dataset stays readable while the new one is prepared. The importer
never holds the shared Library database lock through downloading, parsing,
indexing or optimization.

| Phase | Progress | Cancellation |
| --- | --- | --- |
| Downloading each source | Received bytes over the length the response declares; the received count alone when the response declares none or sends more than it declared | Cancel network/file copy through the existing token |
| Reading episodes, titles, ratings | Approximate current compressed-source position/length; kept below 100% until that source is fully consumed | Check between input records and through the SQLite handler |
| Linking episodes | Episodes written over episodes read | Check between rows and through the SQLite handler |
| Indexing titles | Titles added to the search index over titles kept; the two key indexes build first and report nothing, so the count holds at zero meanwhile | Check between rows; the SQLite progress handler interrupts the key indexes |
| Optimizing search index | Unknown | SQLite progress handler interrupts `optimize` |
| Flushing prepared data | Unknown | A synchronous flush must return before cancellation cleanup can finish |
| Installing data | Unknown; short Finishing state | Existing coordinated publication finishes once committed |

A stop during an uninterruptible flush remains a cancellation request that
awaits that call's return, not a promise of instant completion.

One connection-scoped SQLite progress handler covers the interruptible SQL
phases and is unregistered before the connection is disposed. It only inspects
cancellation and returns the interruption decision, with no SQL, UI calls or
formatting, so a cancel during the key indexes or the full-text `optimize`
statement does not wait for it to finish. Interrupting the build discards the
disposable file. Interrupted SQL becomes cancellation only when the matching
token was requested. The handler's instruction interval is not a progress total.
[SQLite progress handler](https://www.sqlite.org/c3ref/progress_handler.html).

The entire synchronous `Import` body runs on one dedicated worker thread at
`ThreadPriority.Lowest`, set on that thread before it opens its private
files/connection. **Owner ruling: the import gives up the processor, not the
disk.** The import runs in the background anyway, so it must not take the
processor from the person's other work; Windows background mode
(`THREAD_MODE_BACKGROUND_BEGIN`) also lowers disk and memory priority, and
under that mode the import waited far longer than it computed whenever other
programs were busy, so the import keeps normal disk and memory priority. Do
not change process priority or a reusable thread-pool thread. Do not put an
asynchronous delegate on this thread and assume its continuations retain the
priority.
[Thread.Priority](https://learn.microsoft.com/en-us/dotnet/api/system.threading.thread.priority).

The small worker implementation sits next to the importer until a second real
caller needs it. It returns an awaitable completion with asynchronous
continuations, propagates exceptions, and cleanup awaits its exit. The native
call's result is checked; failure to establish the requested mode surfaces
through the existing error path. Low priority improves coexistence; it does not
promise a shorter elapsed import or instant interruption of an operating-system
file call.

Global WAL settings, pooling and durability stay unchanged on the strength of a
source review. Further SQL tuning starts from a new measurement.

### Measured import

Measured on the owner's machine, Release build, through `Dataset.Build` outside
the window with cached archives. The data kept 1,208,696 titles, 7,861,478
episodes and 7,861,484 episode-title rows (16,931,658 rows), in a file of
568,459,264 bytes. Downloading the three archives took 2 min 16 s on that
connection.

| Measurement (2026-10-16) | Result |
| --- | ---: |
| Episodes | 7.50 s |
| Linking | 11.27 s |
| Ratings | 1.04 s |
| Titles | 40.67 s |
| Indexing | 10.03 s |
| Optimizing and flushing | about 1.6 s |
| Whole import, elapsed | 71.97 s |
| Whole import, process CPU | 59.03 s |
| Peak process working set | 476 MB |

These runs shared the machine with other work, so they bound the
implementation's cost; they do not isolate the effect of one change. All retained
rows matched an earlier build in every column and SQLite value type, and
`quick_check`, the FTS5 external-content integrity check and search-result
comparisons passed.

A CPU sample profile of the batched import, taken under competing load, put
about 90 of its 119 traced seconds under native SQLite calls: 51 s under the
batched inserts, 29 s under the index phase and 10 s under the ratings updates
that the ratings pre-join later replaced. Time under a native call includes any
waiting for I/O. All managed work, including decompression, UTF-8 decoding,
parsing, title keys and the episode sort, was under 25 s. Candidates that
profile suggested were tested in a replay of the pipeline outside the app:

- Per-row inserts cost 6 to 8 more CPU seconds than 100-row batches on the
  episode links and about 1 s on titles, so batching stays.
- A 64 MB page cache, 8 KB pages, UTF-16 text binding and a struct or
  `IComparable` sort comparer each measured within noise of the shipped code.
  The larger cache cut cache misses from 75,562 to 68,853 but raised writes from
  36,177 to 42,867, so the default cache stays.
- 16 KB pages measured 27 s then 37 s for the same titles phase on two runs, so
  the page size stays at the default.

Under Windows background mode, the import's phases took up to 2.5 times longer
than their CPU time whenever builds ran. This is consistent with, but does not
prove, lowered I/O priority as the cause; no controlled measurement of the move
to `ThreadPriority.Lowest` exists.

Import comparisons record machine-wide CPU use and overlapping build, benchmark
and diagnostic processes, because other sessions share the laptop.
`artifacts/imdb-performance/Monitor.ps1` wraps the existing import harness and
samples those workloads about once a second; the replay harness and the records
are in `artifacts/imdb-performance/` and its `replay/` folder. Its CPU bands
describe how much CPU the importing process used, not how much scheduling time
it was denied, because waiting for I/O also lowers CPU use. An elapsed time
from a busy machine does not establish the improvement from a code change.
Measurements read saved archives; IMDb never sees repeated downloads for
development.

[ImdbChecks.ps1](../../app/tests/ImdbChecks.ps1) covers nullable and Unicode
bindings, ratings, duplicate and unnumbered or out-of-range episode IDs, phrase
matching, reused and partial insert batches, the final partial index batch, and
cancellation after a batch, including discarding the temporary file and
preserving the installed copy byte for byte. It runs against disposable fixtures
without starting WinUI or the engine.

### Schema

```sql
CREATE TABLE titles (
    title_id INTEGER PRIMARY KEY,   -- tt0133093 is saved as 133093
    kind TEXT NOT NULL,             -- 'movie' or 'series'
    title TEXT NOT NULL,
    original TEXT,                  -- NULL when it equals title
    title_key TEXT NOT NULL,        -- title as Dataset.Key writes it
    original_key TEXT,              -- original as Dataset.Key writes it
    year INTEGER NOT NULL,
    genres TEXT,                    -- IMDb's genre names, comma-separated
    rating REAL,
    votes INTEGER
);
CREATE TABLE episodes (
    series_id INTEGER NOT NULL,     -- the series' title_id
    season INTEGER NOT NULL,
    number INTEGER NOT NULL,
    episode_id INTEGER NOT NULL,    -- the episode's own tconst number
    PRIMARY KEY(series_id,season,number)
) WITHOUT ROWID;
CREATE TABLE episode_titles (
    episode_id INTEGER PRIMARY KEY,
    title TEXT NOT NULL,
    year INTEGER
);
CREATE VIRTUAL TABLE title_search USING fts5(
    title, original, content='titles', content_rowid='title_id',
    tokenize='unicode61 remove_diacritics 2', columnsize=0
);
CREATE INDEX titles_by_key ON titles(title_key);
CREATE INDEX titles_by_original ON titles(original_key) WHERE original_key IS NOT NULL;
```

`Dataset.Key` writes a title in lower case without accents, apostrophes or
other punctuation, because a file name lacks them: `Ocean's Eleven` and
`Oceans.Eleven` both give `oceans eleven`. The two indexes serve
[rating by file name](#rating-by-file-name); the import builds them after the
rows, which is faster than updating them for each row.

`title_id` saves the number from the IMDb ID as an integer, and the provider
writes it back with the `tt` prefix. An integer key makes the file smaller than
a text key would. The full-text index searches words, with accents removed, so
`amelie` finds `Amélie`. It retains token positions for phrase matching but
omits per-document token counts (`columnsize=0`): search orders by exact title
and votes, not BM25. Existing files with token counts remain readable.

### Provider

The folder is `app/src/Library/Imdb/`.

- `Provider` implements `IProvider`, and its `ProviderOption` has the provider
  ID `imdb`, which also names its catalogue, and the `Dataset`. It answers
  movies and series, and its information does not depend on the language.
- The provider reads `imdb.db` on its own read-only connection, not on the
  Library connection, so a damaged file fails as a provider failure, with the
  text "The IMDb data is damaged. Download it again.", and never touches
  `library.db`.
- The provider opens that connection for each Search or Describe call and
  closes it at the end. `VideoLookup` creates provider objects and never
  disposes the old ones, so a connection held by a replaced object would keep
  the file open, and Windows would refuse to replace or delete it. This
  connection and the import's connection use `Pooling=false` for the same
  reason: a pooled connection keeps the file open after it is closed.
- Library counts IMDb as available while `Dataset.IsInstalled` is true, a flag
  that `Dataset` keeps: whether `imdb.db` exists. `Dataset` sets it with one
  `File.Exists` check when it is created, before Library starts, and again
  after each install or deletion. The UI reads availability on every refresh,
  so it must not open the file.
- `Search` puts each word of the query in quotes before the full-text query.
  Without quotes, ordinary names fail: `Spider-Man` and `Mission: Impossible`
  are full-text syntax errors.
- `Search` returns 50 full-text matches. The SQL puts first the titles whose
  title or original title equals the query under `FileName.Normalize`, the
  comparison that Library's confidence rules use, and orders each group by
  votes before the limit. The import stores each title in that form, because
  SQLite's `lower()` changes only ASCII letters and keeps accents. Ranking
  exact titles before the limit keeps an obscure film with a common title,
  such as "Home". Without the vote order, the full-text index returns matches
  by ascending IMDb ID, so a common word would give the oldest entries and miss
  a well-known newer film. The full-text rank alone puts famous films below
  obscure ones: for "the matrix" it ranked a 2010 film with 20 votes above the
  1999 film. Library's existing confidence rules then choose the match, as
  they do for every provider. If a correct film can fall outside the first 50,
  `Search` also needs the release year.
- `Describe` reads one row and returns the title, year, genres and the IMDb
  rating with its votes. For a series it also returns the release's
  episodes with their names, as TMDB does, and the series' rating. An episode
  that the data lacks makes the description missing. The synopsis and cast
  are empty. Genres stay in English, as OMDb and MDBList already store them.
- `Dataset` owns the download, the import, the file replacement and the
  deletion. It replaces or deletes the file inside `VideoLookup`'s change, so
  no lookup has the file open at that moment.
- Installing new data goes through `VideoLookup.Reload`, which replaces the
  file off the window thread and, when IMDb is the selected provider,
  recreates it, as saving an API key does, and forgets its failures. Another
  selected provider keeps its failure. A running batch keeps its waiting
  files.

[Search protection](search-protection/local.md#imdb-datasets-and-local-answers) owns
how remote requests, local dataset reads, installation, reset and Settings'
Download and Update relate: local reads are not remote requests, Download and
Update follow the standard search protection, and an installation makes local
matching use the new dataset while keeping accepted matches and useful saved
metadata.

### Settings

The provider's `ProviderOption` declares that it needs a download, as an API
provider declares that it needs a key. Settings keeps this configuration
independent of automatic download intent. Starting automatic downloads offers
the data setup or key setup that the chosen provider needs:

1. Choosing **IMDb** in Provider preserves automatic download intent. Turning
   **Download automatically** on when `imdb.db` does not exist opens a dialog
   stating the download size, about 290 MB, and asks **Download** or **Cancel**.
   Cancel leaves automatic downloads off. The size is
   fixed text, because it changes slowly and reading it would cost a request.
2. The **IMDb data** row under Video information (phase 2, point 5) shows each
   file's download as received over declared bytes, then the current
   preparation stage as the [phase table](#import-execution-and-sql-design)
   lists it: an approximate percentage while parsing each source, and an exact
   one while the episodes and the search index are written. Optimization and
   the final disk flush stay indeterminate. Each stage restarts the
   percentage, so the row names the current stage, for example "IMDb titles ·
   ≈45%". The shared observation supplies Settings and the status bar with
   the same phase and cancellation command. The status bar names IMDb on
   every page. Narrator hears each new
   stage. When the import is complete, automatic downloads start, unless the
   person chose another setting meanwhile, because that choice is newer. Only
   this setup flow turns automatic downloads on; the row's own Download and
   Update preserve the provider and automatic download setting.
   Library owns this pending activation. A later accepted provider choice
   revokes it even when the final setting equals the original one. Installation
   and conditional activation use the existing serialized provider-change path
   and preserve accepted API batches. If activation fails after installation,
   the data stays installed and the activation error is reported.
3. The row shows the number of titles, the size and the age of the data, for
   example "912,345 titles · 438.0 MiB · 1 day old", and has two commands.
   Counting reads the whole titles table, so the count is kept until the file
   is replaced, and the row adds it when the count arrives:
   **Update** downloads and replaces the data. It first opens the download
   dialog of point 1 as Update IMDb data?, which states the data's age,
   because the person cannot otherwise judge whether about 290 MB is worth
   transferring. For data less than a day old, the body warns that the person
   likely has the latest data, because IMDb publishes once a day and the
   download would most likely transfer the same files. Data less than a week
   old adds that little has changed. Older data only states its age.
   That download does not change the provider.
   **Delete data** asks first,
   naming the size, then removes `imdb.db` while preserving the provider,
   automatic download setting and saved video information.
   Delete retained archives before removing the installed file, so an archive
   error leaves the usable data and selected provider intact.
4. When IMDb is chosen but `imdb.db` is missing, for example after an app
   update changed the file version, the row's **Download** gets it, because a
   provider that is already chosen cannot be chosen again. Missing data holds
   only IMDb's automatic work. Saved information stays visible; a file without
   metadata shows "No metadata" and Fetch offers online providers and Settings.

### Ending a download

A running download ends in one way, whatever ends it: the person's Cancel, a
protection reset or route change, a feature reset, or closing the window. The
caller cancels the download and waits until it has finished before Library
releases anything. The download's end then deletes a build that was not
installed. So no exit leaves `imdb.db.tmp` behind, and the Settings row returns
to its idle state while Library is still attached. Complete archives stay, as
step 3 of [Download and import](#download-and-import) describes. A protection
reset or a route change that stops the download is reported, because the
person did not ask for it.

The overall operation invokes staged-file cleanup once; import does not have a
second cleanup path that can replace the original failure. Completion is signalled
after cleanup even if a final UI notification fails. Once installation commits,
cancellation does not discard the installed data or hide a later failure.

Waiting cannot deadlock. The caller holds none of Library's gates while it
waits, and the cancelled download either stops before `VideoLookup.Reload` or
lets `Reload` drain the lookups and then refuses the install.

### No website reader

IMDb's dataset conditions say: "The data must be taken only from the datasets
made available", and they forbid screen scraping of the website. No plugin
therefore reads the IMDb website. Reading it needed an installed Chrome-based
browser to load a whole web page for a few facts, which used much memory and took
about ten seconds for each movie.

Rotten Tomatoes is not offered either. Its HTTP option needed no browser and is
not under IMDb's conditions, but it gave nothing that another provider lacks, and
keeping the shared website-reading code for it would carry that code for no
visible benefit.

| Fact | Source |
| --- | --- |
| IMDb title, year, genres | IMDb provider, OMDb, TMDB |
| IMDb cast | OMDb, TMDB |
| IMDb plot | OMDb (full plot), TMDB, MDBList |
| IMDb rating | IMDb provider; for every provider's match in phase 2 |
| Rotten Tomatoes synopsis, genres, cast | TMDB, OMDb |
| Rotten Tomatoes score | OMDb and MDBList save it; no option shows it |
| Related titles, saved as choices for Fetch metadata | TMDB recommendations |
| Keywords | TMDB |

Every fact has a source, but not always in one provider, because the person
chooses one provider at a time. The IMDb provider alone gives no cast or
synopsis; TMDB gives both, and phase 2 adds the IMDb rating to it.

## Phase 2: IMDb ratings for every provider

TMDB, Wikidata, OMDb and MDBList all return the IMDb ID of a movie. Library
saves that ID in the `video_information.imdb_id` column and, when it reads the
rows, takes each movie's rating from `imdb.db`. The rating is not copied into
`library.db`:

- Wikidata's IMDb rating is the value an editor last recorded, often years old.
  The local rating is at most as old as the last update.
- TMDB returns no IMDb rating, so the IMDb column fills from the dataset for
  TMDB matches.
- Without `imdb.db`, or when it cannot be read, the rating that the provider
  returned stays in use.

**The ratings are read in C#, not joined in SQL.** `Store` reads the rows from
`library.db`, then asks `Dataset` for the ratings of their IMDb IDs in one query
on `imdb.db`. The table and the Details card use the same rule. Library sorts
ratings in C# and no SQL filter reads them, so a SQL join would gain nothing.
Attaching `imdb.db` to the Library connection would need four mechanisms in
the shared Database owner: attaching, telling a damaged `imdb.db` from a
damaged `library.db`, a file swap outside transactions, and invalidating the
row snapshot. The C# read needs none of them:

1. **Damage.** `Dataset` reads on its own connection, so an error from
   `imdb.db` never reaches the Database owner. A rating read that fails returns
   no ratings.
2. **Replacement and deletion.** Reads share a reader-writer lock, and Install
   and Delete hold it alone while they replace the file, because Windows
   refuses to replace an open file.
3. **Refresh.** After an install or a deletion, Library refreshes its rows and
   Settings shows the data's new state.
4. **Saved matches.** The complete initial schema includes each match's IMDb
   ID when available. The [database contract](database.md) owns initialization
   and versioned upgrades of released schemas.
5. **Managing the data.** A TMDB user also uses `imdb.db`, so the data has its
   own row, **IMDb data**, under Video information. It shows the title count,
   size and age, or what the data is for and its size, and has **Download** or
   **Update** and **Delete data**. While the data downloads, the row shows the
   progress and a button that stops it. **Delete data** asks first and preserves
   the provider and automatic download intent. Starting automatic
   downloads without the data asks to download it, as phase 1 describes.

Library owns this rule, because it applies to every provider.

### Rating by file name

**Owner ruling: downloaded IMDb data rates every video it can name, whatever
the provider** ([Library](library/providers.md#identification-and-early-enrichment)).
A video without an IMDb ID gets its IMDb rating from the title, year, season
and episode that its file name gives. Nothing is saved: `Store.Rate` asks
`Dataset.RatingsByName` each time it reads the rows, in one query for all of
them, and the video stays unidentified.

A rating is shown only when the name fits exactly one title, because a wrong
score would look as trustworthy as a right one:

- **A movie** needs the exact title and the year. Two films with one title and
  one year give no rating, and a name without a year gives none, because a
  title alone often names several films.
- **An episode** gets its series' rating, as an IMDb match gives it. The
  series has the exact title and lists that season and episode; a season pack
  needs only the season. When several series still fit, the year in the name
  decides, and otherwise no rating shows.
- The title is compared by `Dataset.Key`, not by the word similarity of
  automatic matching, because the lookup runs on every read: an exact key is
  one index lookup, and a similarity search for each file would slow every
  refresh.

## Risks

- **Licence.** IMDb allows the datasets for personal and non-commercial use
  only. Its conditions forbid using the data to create "any kind of
  online/offline database of movie information (except for individual personal
  use)". `imdb.db` is such a database, built by each person for themselves. It requires
  this attribution: "Information courtesy of IMDb (https://www.imdb.com). Used
  with permission." Each person downloads the data to their own computer,
  which fits personal use. The same condition as for SubDL applies: this holds
  only while TinyTorrent is not a commercial service
  ([third-party provider access](architecture.md#third-party-provider-access)).
  The owner confirms the terms before release. The attribution goes under
  Credits on the About page.
- **More ambiguous matches.** IMDb lists many more films than TMDB, including
  many with the same title and year. Library keeps an ambiguous match
  unassigned, so the risk is fewer automatic matches, not wrong ones. The match
  rate on a real Library is measured before release.
- **Import time.** The measured time excludes downloading and varies with the
  computer's load.
- **Disk space.** The import briefly needs space for both `imdb.db` and
  `imdb.db.tmp`. If the disk is full, the import fails with a message that
  says so, and the old file and the downloaded archives stay, so the person
  frees space and tries again without a new download.
