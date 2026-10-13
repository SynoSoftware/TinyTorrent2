# IMDb datasets plan

This is a design for review. It is not implemented yet. The
[Library master plan](library.md) stays the authority for Library behavior.

## Summary

Import the IMDb data into SQLite, in a separate file, `imdb.db`, beside
`library.db`. Do not put the data into `library.db`.

- The person selects **Download** in Settings. The app downloads the IMDb files
  once and builds `imdb.db` from them. After that, every lookup reads the local
  file and sends no request.
- A new **IMDb** provider searches and describes movies from `imdb.db`. Library
  matching, saving and display stay as they are.
- The same change removes the Public plugin, which reads the IMDb and Rotten
  Tomatoes websites through a browser.
- The person selects **Update** to get newer data. Nothing downloads unless the
  person asks for it.

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
3. **The schema needs no migrations.** `imdb.db` records its own
   `PRAGMA user_version`. A file with another version counts as missing, and
   the person downloads again. `library.db` versioning does not change.
4. **Deleting it is simple.** Deleting the file frees all the space. Delete
   database in Settings does not touch it.

**A damaged `imdb.db` must never count as a damaged `library.db`.**
`Database.Run` treats `SQLITE_CORRUPT` and `SQLITE_NOTADB` as damage to the
Library database, and the app then offers to delete `library.db`. An error
from the IMDb file must never reach that path, because it would offer to
delete the person's manual matches because of a cache file. Phase 1 keeps the
two files on separate connections for this reason. Phase 2 adds one rule for
the join.

## Phase 1: the IMDb provider

Phase 1 downloads `title.basics` and `title.ratings`: about 237 MB.

### Download and import

1. A leftover `imdb.db.tmp` from an earlier crash is deleted first.
2. Each file downloads through the HTTP owner, so the saved proxy and network
   adapter apply ([network route](architecture.md#network-route)).
   `ProviderHttp.Send` reads a whole reply into memory, up to 16 MB, and cancels
   any request that takes more than 60 seconds. The HTTP owner therefore gets
   one streaming method. Its timeout counts from the last bytes received, so a
   228 MB file on a slow line does not fail. A separate `HttpClient` would
   bypass the saved route, so it is not an option.
3. `GZipStream` decompresses the file while it downloads. No `.gz` file is kept
   on disk. A failed download starts again from zero.
4. The import reads one line at a time and keeps only the rows Library can use:
   - `titleType` is `movie` or `tvMovie`.
   - `isAdult` is `0`. TMDB lookups also exclude adult titles.
   - `startYear` is set. A title without a year is announced but not released,
     so no file of it can exist. Without this filter, those titles would match
     on title alone and make real matches ambiguous.
5. The rows go in one transaction, with prepared commands and with
   `journal_mode=OFF` and `synchronous=OFF`. These settings are safe because
   the file is temporary until step 7.
6. After the insert, the import fills the full-text index with its `rebuild`
   command and runs `PRAGMA optimize`.
7. The import closes its connection, flushes the file to disk, and moves it over
   `imdb.db` with `File.Move` and overwrite. `File.Replace` fails when
   `imdb.db` does not exist yet. The flush prevents a power cut from leaving a
   renamed file with missing data.

Estimated result: under a million movies and a file of about 150–250 MB. Step 1
of the work measures this.

### Schema

```sql
CREATE TABLE titles (
    title_id INTEGER PRIMARY KEY,   -- tt0133093 is saved as 133093
    title TEXT NOT NULL,
    original TEXT,                  -- NULL when it equals title
    year INTEGER NOT NULL,
    genres TEXT,                    -- IMDb's genre names, comma-separated
    rating REAL,
    votes INTEGER
);
CREATE VIRTUAL TABLE title_search USING fts5(
    title, original, content='titles', content_rowid='title_id',
    tokenize='unicode61 remove_diacritics 2'
);
```

`title_id` saves the number from the IMDb ID as an integer, and the provider
writes it back with the `tt` prefix. An integer key makes the file smaller than
a text key would. The full-text index searches words, with accents removed, so
`amelie` finds `Amélie`.

### Provider

The folder is `app/src/Library/Imdb/`. The Public plugin's `Public.Imdb` class
goes in the same change, so no type shares the name of the namespace.

- `Provider` implements `IProvider` with the provider ID `imdb` and the catalog
  ID `imdb`. The settings are `Background`, movies only, and
  `IsLocalized = false`.
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
- `IsAvailable` reads a flag that `Dataset` keeps: whether `imdb.db` exists and
  has the expected version. `Dataset` checks the file once at startup, off the
  UI thread, and again after each install or deletion. The UI reads
  `IsAvailable` on every refresh, so it must not open the file. When the
  startup check ends, `Dataset` asks `VideoLookup` to refresh. Background
  lookups start only on a refresh, so without this request they would not run
  in a session whose check ended after Library's first refresh.
- `Search` puts each word of the query in quotes before the full-text query.
  Without quotes, ordinary names fail: `Spider-Man` and `Mission: Impossible`
  are full-text syntax errors.
- `Search` reads the 500 full-text matches with the most votes: the SQL orders
  by votes before its limit. Without that order, the full-text index returns
  matches by ascending IMDb ID, so a common word would give the 500 oldest
  entries and miss a well-known newer film. C# then puts first the titles whose
  title or original title equals the query under `FileName.Normalize`, the
  comparison that Library's confidence rules use, keeps the vote order inside
  each group, and returns the first 50. SQLite's `lower()` cannot do this
  comparison, because it changes only ASCII letters and keeps accents.
  The full-text rank alone puts famous films below obscure ones: for "the
  matrix" it ranked a 2010 film with 20 votes above the 1999 film. Library's
  existing confidence rules then choose the match, as they do for every
  provider. Step 1 counts how many films share a common title. If a correct
  film can fall outside the first 50, `Search` also needs the release year.
- `Describe` reads one row and returns the title, year, genres and
  `ImdbRating`. The synopsis and cast are empty. Genres stay in English, as
  OMDb and MDBList already store them.
- `Dataset` owns the download, the import, the file replacement and the
  deletion. It replaces or deletes the file inside `VideoLookup`'s change, so
  no lookup has the file open at that moment.
- Cancel interrupts the import's connection, as the Library connection's
  progress handler already does, so a cancel during the long full-text `rebuild`
  statement does not wait for it to finish.
- Installing new data goes through `VideoLookup`, which recreates the
  provider, as saving an API key does, and clears its failures. Today that
  change also ends a running Fetch metadata batch, as saving a key does. When
  [request protection](request-protection.md) adds pause and drain, the install
  uses it instead, so a batch keeps its waiting files.

**Remembered no-matches.** Until request protection lands, the IMDb provider
follows today's rules like every provider: Library remembers its no-match
answers, and an update does not clear them. The person tries the new data
with Fetch metadata. `VideoLookup` also keeps search results for the session,
so the install drops those for the `imdb` catalog.

Request protection changes this. It counts only remote answers: "Only a
validated remote response establishes missing data", and its search limit
charges "completed remote lookup outcomes". An IMDb search reads a local file,
so it creates no remembered no-match and no search count. After an empty
answer, Library searches `imdb.db` again in the next session, which costs
nothing and finds titles that an update added. Its pacing applies at the
outbound send path, which IMDb lookups never reach, so they get no wait.

Request protection also saves answered searches with their candidates and
reuses them across restarts. An ambiguous IMDb answer has candidates, so its
next search would reuse them instead of reading `imdb.db`, and would not find
a title that an update added. **Owner decision:** whether request protection
exempts a local provider from that reuse. Recommended: exempt it, because
reading `imdb.db` costs nothing and the saved candidates go stale with each
update.

### Settings

The provider's `ProviderOption` declares that it needs a download, as an API
provider declares that it needs a key, and Settings owns the flow. It is a new
branch beside the key flow, which applies only to `Access.Key` providers:

1. The person chooses **IMDb** in Video information when `imdb.db` does not
   exist. A dialog states the download size, which a HEAD request reads, and
   asks **Download** or **Cancel**. Cancel leaves the choice as it was.
2. The Video information row shows the download progress in MB, then
   "Building" without a percentage while the import runs, with a Cancel button.
   When the import is complete, the provider turns on.
3. A button beside the provider, where the key button is for API providers,
   opens a flyout. The flyout shows the date and size of the data and has two
   commands: **Update** downloads and replaces the data, and **Delete data**
   removes `imdb.db` and turns Video information off.
4. When IMDb is chosen but `imdb.db` is missing, for example after an app update
   changed the file version, the row shows the Download button. The Library
   page shows the existing message for an unavailable provider, whose button
   opens Settings.

Closing the window cancels a running import and deletes `imdb.db.tmp`.

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

The removal deletes:

- the website reader, its browser control and
  [movie-websites.md](movie-websites.md);
- `ProviderHttp.BeginDirect`, which only the browser reader uses;
- the external-browser paragraph under
  [network route](architecture.md#network-route);
- the `websites` and `websites_http` text sections;
- the website paragraphs in [library.md](library.md) and
  [library-implementation.md](library-implementation.md);
- the website checks in `app/tests/SourceChecks.ps1`. They load
  `Public.Provider.Options` and run the website parsers. The store checks
  "late reply overwrote a newer Clear" and "late reply overwrote a merged
  identification" use the Rotten Tomatoes result as their fixture, and one
  check expects `rottentomatoes` to survive as the saved provider. These
  checks need another provider's saved record, such as a TMDB response.

The removal makes parts of [request protection](request-protection.md), the
owner's plan in progress, out of date. The owner updates them: the provider
list, the `Public.Provider` row, the website reader as a transport, the Rotten
Tomatoes readers' use of Library's execution owner, the shared Rotten Tomatoes
reader history, the browser launch failure, the website readers paragraph, and
step 2's "Integrate all API and website paths".

What existing users see:

- A person whose saved provider is `imdb` keeps that choice, which now names
  the IMDb provider. Without `imdb.db`, background lookups skip an unavailable
  provider without a word, so Library shows, in the workspace notification:
  "IMDb information now comes from downloaded IMDb data. Download it in
  Settings." Its button opens Settings. The notice appears at each start while
  IMDb is chosen and the data is missing, because lookups stay stopped until
  the person downloads the data or chooses another provider. The new provider gives no
  synopsis, cast or keywords, which the website gave; TMDB gives all three.
- While IMDb is chosen and `imdb.db` is missing, the Details card's
  unavailable-provider tooltip says that the IMDb data is not downloaded. The
  general text, "Choose another provider, or try again later", is wrong here,
  because waiting never helps.
- A person whose saved provider is a Rotten Tomatoes option finds video
  information turned off at the next start. `VideoLookup.Initialize` finds no
  provider with that ID and turns it off. Today this happens without a word, so
  Library also shows once, in the workspace notification: "The video
  information provider you chose was removed. Choose another in Settings."
  `Initialize` turns lookups off only while they are on, so the notice appears
  once.
- Saved information from the removed options stays in `library.db` and stays
  visible.

## Phase 2: IMDb ratings for every provider

TMDB, Wikidata, OMDb and MDBList all return the IMDb ID of a movie. Library
saves that ID in a new `video_information.imdb_id` column and joins it with the
IMDb ratings when it reads the rows. The rating is not copied into
`library.db`:

- Wikidata's IMDb rating is the value an editor last recorded, often years old.
  The local rating is at most as old as the last update.
- TMDB returns no IMDb rating. With this change, the IMDb column fills for TMDB
  matches too.
- Without `imdb.db`, the rating that the provider returned stays in use.

**The join.** The Library connection runs
`ATTACH DATABASE '<path>\imdb.db' AS imdb`, but only when the file exists,
because ATTACH of a missing file creates an empty one. A TEMP view,
`imdb_ratings`, reads `imdb.titles`. Without the file, the same view has no
rows, so the Library query does not change. SQLite rejects a view stored in
`library.db` that names an attached file. The Library's `library_entries` view
is already a TEMP view, so it can join `imdb_ratings`:

```sql
LEFT JOIN imdb_ratings r ON r.title_id = v.imdb_id
-- shown rating: COALESCE(r.rating, v.imdb_rating)
```

SQLite looks up the attached table by its primary key, as tested.

**Rules for the attached file:**

1. **Damage.** A damaged `imdb.db` can fail in two places: `ATTACH` itself
   fails with `SQLITE_NOTADB`, and a later query can fail with
   `SQLITE_CORRUPT`. The Database owner handles both before its general catch,
   which would mark `library.db` incompatible:
   - An error from `ATTACH` leaves the file detached and the no-rows
     `imdb_ratings` view in place.
   - On `SQLITE_CORRUPT` or `SQLITE_NOTADB` while the file is attached, it
     recreates `imdb_ratings` as the no-rows view, detaches the file, and runs
     the action once more. The view comes first, because a view that names
     `imdb.titles` fails with "no such table" after `DETACH`.
   - If the retry succeeds, the IMDb file was at fault, and the provider
     reports it as damaged. Only an error that remains marks `library.db`
     incompatible.
2. **Replacement and deletion.** `DETACH` fails in a transaction that has read
   the attached file, and `Database.Run` wraps every action in a transaction. The Database owner therefore gets
   one method that takes its access lock without a transaction, detaches the
   file, lets `Dataset` move or delete it, attaches it again and recreates the
   TEMP view.
3. **Refresh.** `library_rows` is a snapshot of `library_entries`. After a
   replacement, the Database owner invalidates it and Library refreshes, so the
   new ratings appear.
4. **Existing matches.** The `imdb_id` column starts empty for saved
   information. The saved provider responses already hold the IDs: TMDB's
   `imdb_id`, OMDb's `imdbID`, MDBList's `ids.imdb`, Wikidata's IMDb ID, and the
   addresses of the removed `public/imdb` matches. The schema change fills the
   column from them once.
5. **Managing the data.** With phase 2, a TMDB user also uses `imdb.db`, so the
   date, size, **Update** and **Delete data** move from the provider's flyout to
   their own row under Video information. **Delete data** turns video
   information off only when IMDb is the selected provider.
6. **Schema.** Rule 4 needs a migration, because an edit of the initial schema
   raises the version and makes every existing `library.db` incompatible,
   which leaves no saved rows to fill. The [database contract](database.md)'s
   owner ruling says to edit the initial schema before the V1 release, while
   the code already migrates versions 2 and 3. The owner decides which applies
   when phase 2 starts.

Library owns this rule, because it applies to every provider. Do phase 2 only
after phase 1 has been used.

## Phase 3, only if needed: cast, series and localized titles

Each addition is a large download for a smaller benefit, so none is in the
plan until someone asks for it.

| Addition | Extra download | Benefit |
| --- | --- | --- |
| Cast | about 1 GB (`title.principals` and `name.basics`) | The Cast column. Keep the first five actors for each kept title, and only the names they reference. |
| Series and episodes | about 50 MB (`title.episode`) and about 9 million rows | Matches for episode files. TMDB already covers series. |
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
  `imdb.db.tmp`. If the disk is full, the import fails with its error and the
  old file stays.

## Work order

1. Download `title.basics` and `title.ratings` once and save them locally for
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
8. Phase 2, after phase 1 has been used, including the changes to
   [database.md](database.md) for the attached file.
