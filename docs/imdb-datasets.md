# IMDb datasets plan

**Phases 1 and 2: implementation done.** The import was timed on 2026-10-15
([measured result](#download-and-import)); live-data validation remains
outstanding. **Phase 3: not implemented; design for review.** The [Library master plan](library.md) stays the authority
for Library behavior.

## Summary

Import the IMDb data into SQLite, in a separate file, `imdb.db`, beside
`library.db`. Do not put the data into `library.db`.

- The person selects **Download** in Settings. The app downloads the IMDb files
  once and builds `imdb.db` from them. After that, every lookup reads the local
  file and sends no request.
- A new **IMDb** provider searches and describes movies and series from `imdb.db`. Library
  matching, saving and display stay as they are.
- The same change removes the Public plugin, which reads the IMDb and Rotten
  Tomatoes websites through a browser.
- The person selects **Update** to get newer data. Nothing downloads unless the
  person asks for it.

**Owner ruling: IMDb offline is available only in Settings.** Do not offer it
in a movie context menu, a Fetch/Force fetch chooser, or a batch fetch action.
Download and Update obtain the complete dataset as one Settings operation;
there are no small per-movie IMDb downloads. Phase 1 builds the one installed
database from the basics, ratings and episode archives described below. Lookups
only read that database. Dataset transfers follow the shared request protection
without an IMDb-specific bypass.

A provider's `ProviderOption` declares the data it answers from, `Dataset`, or
none for a provider that answers over the network. Only a provider that answers
over the network fetches an individual movie, so Settings lists either kind,
while movie actions and their execution checks use only the network kind.
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

**Recommendation: one separate SQLite file, `imdb.db`, in the same folder as
`library.db`.**

| Option | Result |
| --- | --- |
| Separate `imdb.db` | Recommended. See the reasons below. |
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
   versioning does not change. The rest of this plan calls the file
   `imdb.db`.
4. **Deleting it is simple.** Deleting the file frees all the space. Delete
   database in Settings does not touch it.

**A damaged `imdb.db` must never count as a damaged `library.db`.**
`Database.Run` treats `SQLITE_CORRUPT` and `SQLITE_NOTADB` as damage to the
Library database, and the app then offers to delete `library.db`. An error
from the IMDb file must never reach that path, because it would offer to
delete the person's manual matches because of a cache file. Both phases keep
the two files on separate connections for this reason.

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
     keeps the title of each episode that has numbers.

   An import that keeps no title, no rating or no episode fails, so data that
   IMDb changed never replaces the installed copy with an empty one.
5. The rows go in one transaction, with prepared commands and with
   `journal_mode=OFF` and `synchronous=OFF`. These settings are safe because
   the file is temporary until step 7. The episodes go in primary-key order,
   sorted in memory, because inserting millions of rows in file order into a
   `WITHOUT ROWID` table touches a random page for every row, while sorted
   rows extend the table at its end. The phase counts the rows it writes.
6. After the rows, the import builds the two key indexes and fills the
   full-text index one title at a time, so that phase counts titles; the
   index's `rebuild` command does the same work and reports nothing. It then
   runs `optimize`, which has its own indeterminate, cancellable phase,
   because SQLite reports no progress for it. The
   [background-work plan](09-background-work.md#imdb-execution-and-sql-design)
   owns scheduling and cancellation. Runtime cancellation remains
   unverified; the phase durations are measured below.
7. The import closes its connection, flushes the file to disk, and moves it over
   `imdb.db` with `File.Move` and overwrite. `File.Replace` fails when
   `imdb.db` does not exist yet. The flush prevents a power cut from leaving a
   renamed file with missing data.

Measured result, 2026-10-15, Release build on the owner's machine, driven
through `Dataset.Build` outside the window: 1,208,696 titles, 7,861,478
episodes and a 560 MB file. The three downloads took 2 min 16 s on that
connection. The import took 1 min 46 s after them: episodes 4 s, linking 28 s,
titles 49 s, ratings 7 s, indexing 16 s, optimize 2 s, flush under a second.
The importing process grew by about 270 MB at its peak, for the episode
numbers held in memory.

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
    tokenize='unicode61 remove_diacritics 2'
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
`amelie` finds `Amélie`.

### Provider

The folder is `app/src/Library/Imdb/`. The Public plugin's `Public.Imdb` class
goes in the same change, so no type shares the name of the namespace.

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
  they do for every provider. Step 1 counts how many films share a common
  title. If a correct film can fall outside the first 50, `Search` also needs
  the release year.
- `Describe` reads one row and returns the title, year, genres and the IMDb
  rating with its votes. For a series it also returns the release's
  episodes with their names, as TMDB does, and the series' rating. An episode
  that the data lacks makes the description missing. The synopsis and cast
  are empty. Genres stay in English, as OMDb and MDBList already store them.
- `Dataset` owns the download, the import, the file replacement and the
  deletion. It replaces or deletes the file inside `VideoLookup`'s change, so
  no lookup has the file open at that moment.
- Cancel interrupts the import's connection, as the Library connection's
  progress handler already does, so a cancel during the full-text `optimize`
  statement does not wait for it to finish.
- Installing new data goes through `VideoLookup.Reload`, which replaces the
  file off the window thread and, when IMDb is the selected provider,
  recreates it, as saving an API key does, and forgets its failures. Another
  selected provider keeps its failure. A running batch keeps its waiting
  files.

The [request-protection plan](08-request-protection.md#imdb-datasets-and-local-answers)
applies to remote requests. Local dataset reads are not remote requests and
need no bypass or force permission. After installation, local matching must
use the new dataset rather than stale query results, learned choices or the
saved title/year shortcut for unresolved files. Accepted matches and useful
saved metadata remain intact. A session's attempt markers prevent an empty
answer from starting a busy loop; installing new data clears IMDb's unresolved
markers so eligible unfinished work can use it immediately.

Settings' dataset Download and Update use the same request protection as every
other remote transfer: pacing, saved remote waits and standard retry/reset
rules. There is no IMDb-specific override or movie-search charge. A refusal,
401 or 403, needs attention; each Download or Update click tries once past it
and past a backoff, as Retry does for lookups. Reset clears local download
protection without deleting installed data or starting a download; it cancels
and drains the whole download/install command before taking Library's lookup
gates or clearing its policy writes. Local searches
remain usable while a remote dataset download is waiting.

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
   preparation stage: an approximate percentage while parsing each source,
   and an exact one while the episodes and the search index are written.
   Optimization and the final disk flush stay indeterminate, because SQLite
   and Windows report no progress for them. Each stage restarts the
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
lets `Reload` drain the lookups and then refuses the install. A protection
reset already relies on this path.

### Removing the Public plugin

IMDb's dataset conditions say: "The data must be taken only from the datasets
made available", and they forbid screen scraping of the website. Using the
datasets therefore requires removing the IMDb website reader. Its browser
options start an installed Chrome-based browser and load a whole web page to
read a few facts. That uses much memory and takes about ten seconds for each
movie.

The Rotten Tomatoes HTTP option needs no browser and is not under IMDb's
conditions. It goes too, because it gives nothing that another provider lacks
(see the table), and it is the last user of the shared website reader: the
page parsing, the saved-page handling and the website error texts. Keeping it
would keep that code for no visible benefit.

| Public option gives | Source after the change |
| --- | --- |
| IMDb title, year, genres | IMDb provider, OMDb, TMDB |
| IMDb cast | OMDb, TMDB |
| IMDb plot | OMDb (full plot), TMDB, MDBList |
| IMDb rating | IMDb provider; for every provider's match in phase 2 |
| Rotten Tomatoes synopsis, genres, cast | TMDB, OMDb |
| Rotten Tomatoes score | Not shown today by any option. OMDb and MDBList already save it. |
| IMDb "More like this" titles, saved as choices for Fetch metadata | TMDB recommendations, saved the same way |
| Keywords | TMDB |

Every fact has another source, but not always in one provider, because the
person chooses one provider at a time. The IMDb provider alone gives no cast
or synopsis; TMDB gives both, and phase 2 adds the IMDb rating to it.

The removal deleted:

- the website reader, its browser control and `movie-websites.md`;
- `ProviderHttp.BeginDirect`, which only the browser reader uses;
- the external-browser paragraph under
  [network route](architecture.md#network-route);
- the `websites` and `websites_http` text sections;
- the website paragraphs in [library.md](library.md) and
  [05-library-implementation.md](05-library-implementation.md);
- the website checks in `app/tests/SourceChecks.ps1`. They load
  `Public.Provider.Options` and run the website parsers. The store checks
  "late reply overwrote a newer Clear" and "late reply overwrote a merged
  identification" use the Rotten Tomatoes result as their fixture, and one
  check expected `rottentomatoes` to survive as the saved provider. These
  checks now build an IMDb record in the check itself.

The removal also deleted `VideoSchedule`, `IProvider.Schedule` and
`VideoChoice.Content`. The `Manual` schedule existed only for the website
options, which sent requests only when the person asked, and the saved choice
content only for website pages, which are expensive to read. `VideoLookup`
keeps scheduling.

The [request-protection plan](08-request-protection.md) now covers the API providers
and streaming dataset downloads. It removes the former website-reader rules
and preserves the local/remote distinction above. It is implemented; the
phase 2 rating join must preserve its reset and lookup-drain rules.

What existing users see: phase 2 raised `Database.Version` to 6, so every
older `library.db` is recreated, and no saved provider choice or saved
information from the removed options survives the update. The person chooses a
provider again. The IMDb provider gives no synopsis, cast or keywords, which
the website gave; TMDB gives all three.

## Phase 2: IMDb ratings for every provider

TMDB, Wikidata, OMDb and MDBList all return the IMDb ID of a movie. Library
saves that ID in the `video_information.imdb_id` column and, when it reads the
rows, takes each movie's rating from `imdb.db`. The rating is not copied into
`library.db`:

- Wikidata's IMDb rating is the value an editor last recorded, often years old.
  The local rating is at most as old as the last update.
- TMDB returns no IMDb rating. With this change, the IMDb column fills for TMDB
  matches too.
- Without `imdb.db`, or when it cannot be read, the rating that the provider
  returned stays in use.

**The ratings are read in C#, not joined in SQL.** `Store` reads the rows from
`library.db`, then asks `Dataset` for the ratings of their IMDb IDs in one query
on `imdb.db`. The table and the Details card use the same rule. Library sorts
ratings in C# and no SQL filter reads them, so a SQL join would gain nothing.
Attaching `imdb.db` to the Library connection would need four new mechanisms in
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
the provider** ([Library](library.md#identification-and-early-enrichment)).
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

## Phase 3, only if needed: cast and localized titles

Each addition is a large download for a smaller benefit, so none is in the
plan until someone asks for it. Series and episodes, which phase 3 also
listed, are part of phase 1.

| Addition | Extra download | Benefit |
| --- | --- | --- |
| Cast | about 1 GB (`title.principals` and `name.basics`) | The Cast column. Keep the first five actors for each kept title, and only the names they reference. |
| Localized titles | about 450 MB (`title.akas`) | Spanish titles. TMDB and Wikidata already supply them. |

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
  unassigned, so the risk is fewer automatic matches, not wrong ones. Measure
  the match rate on a real Library before release.
- **Import time.** The estimate is under a minute after the download. Measure
  it on the owner's computer.
- **Disk space.** The import briefly needs space for both `imdb.db` and
  `imdb.db.tmp`. If the disk is full, the import fails with a message that
  says so, and the old file and the downloaded archives stay, so the person
  frees space and tries again without a new download.

## Work order

Steps 2 to 9 are done. Step 1 is not, so the sizes, counts and times in this
plan are still estimates. The owner's first download in the app provides them:
the measurements read the installed file, so IMDb sees no extra download.

1. Download `title.basics`, `title.ratings` and `title.episode` once and save them locally for
   development. Do not download them repeatedly from IMDb. From the saved
   files, measure the row counts, the import time, the file size, and how many
   films share a common title.
2. Add the streaming download to the HTTP owner.
3. Write `Dataset` and the import.
4. Write `Imdb.Provider` and register it with the provider ID `imdb`.
5. Remove the Public plugin and everything listed under
   [Removing the Public plugin](#removing-the-public-plugin).
6. Add the Settings flow and its strings.
7. Update `library.md`, `architecture.md` and the About credits.
8. Phase 2.
9. Finish the download lifetime as [Ending a download](#ending-a-download)
   describes, and delete `VideoSchedule`, as
   [Removing the Public plugin](#removing-the-public-plugin) lists, with its
   description in [05-library-implementation.md](05-library-implementation.md).
