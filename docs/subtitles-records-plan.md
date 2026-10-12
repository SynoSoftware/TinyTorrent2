# Subtitle records and acquisition plan

Review and implementation in the working tree on 2026-10-11.
The owner asked for these changes:

- find bundled subtitles once, including in nested folders;
- identify their language, and read the subtitle text when the name does not
  show the language;
- an explicit Settings action that scans the torrent folders again;
- a record of how often TinyTorrent searched the supplier, and a configurable
  limit, so that TinyTorrent cannot abuse a supplier.

These changes are implemented. The [subtitle contract](subtitles.md) remains
the authority; this document records the design and verification.

## Owner rulings, 2026-10-11

**Owner ruling: every movie keeps a count of lookups for each language.** A
lookup is one search of the supplier for that movie and language. The count
exists only to prevent abuse of the supplier. It is never shown on screen.

**Owner ruling: Find skips a movie and language whose count has reached the
configured limit, in every scope, including All movies.** The limit is per
movie and language. There is no separate limit on how many movies one Find
searches. The first Find on a large library searches every movie once, and the
supplier's own quota slows that traffic.

## What the person sees today

| Defect | Cause |
| --- | --- |
| A movie with a bundled `Movie.eng.srt` or `Subs/English.srt` shows English in Library, but TinyTorrent still downloads `Movie.en.srt` beside it. | Discovery writes found languages only to `subtitle_languages`. Acquisition checks only `subtitle_outputs` and the file at the exact destination name. |
| Bundled subtitles in folders not named `Subs` or `Subtitles` are missed. A file such as `2_English.srt` gets no language. | Discovery recurses only into those two folder names. Language identification stops at the first token that is not a language when the name does not start with the video's name. |
| "Up to 4 subtitles" searches the supplier up to 4 times for one movie and language. | Each extra subtitle is its own job, and each job calls `Supplier.Match`, which searches again. |
| Repeated Finds with No exact match or All movies search the same movies again every time, with no limit. | Nothing records how often a movie was searched. `subtitle_jobs.attempts` is retry backoff: `Accept` resets it to 0. |
| Download subtitles, opened from the Library context menu for a selection, says "0 movies to search" with All movies, and Find stays disabled, although the supplier is configured. | `Count` answers from `_candidates`, a snapshot rebuilt only at fixed moments, while Library reads SQLite on every query. Traced in a harness: the same complete movie counts 0 before `Reconcile` and 2 after, with every other gate passing. See [the dialog](#the-download-subtitles-dialog). |

Current pacing is adequate and stays unchanged. SubSource paces requests before
sending them, and every supplier's quota waits are saved in `subtitle_settings`,
so reopening the window does not bypass them. If a supplier documents a limit
the code does not apply, add that limit to the supplier's `Reserve` override, as
SubSource does.

## Module design

Each change deepens an existing module. The plan adds no new seam. The only
real seam is `Supplier`, because three adapters sit behind it.

| Module | Interface after the change | What it hides |
| --- | --- | --- |
| `Supplier` | `Match(lookup)` returns the ranked candidates of **one** search: the exact match alone, or up to `lookup.Guesses` likely matches, or none. The `rank` parameter goes. `Download(candidate)` stays. | Hash search, title search, release ranking and the distinct-release rule. Adapters keep only `Search`, `Download` and `Check`. |
| `Acquisition` lookups | One private path sends every supplier search: automatic jobs, batch jobs and the choice dialog's `Candidates`. That path records each answered search, including misses. | Where the count rises. No caller can search without counting. |
| `Acquisition.Recorded` | Answers "does this video already have a subtitle in this language" from `subtitle_output_sources` joined to `subtitle_outputs`, including a found `Subs/English.srt`. `Reconcile` and `SaveMatch` keep calling it. | Where a found or created subtitle lives. |
| `Acquisition.Find` | Unchanged signature. It skips a movie and language that has reached the limit. `Count(scope)` skips them too, so the Batch status and Find agree. | The limit rule. |
| `Acquisition.Rescan` | One new public operation for Settings. It returns a named result: subtitles found, and folders that could not be read. | The same discovery code that reconciliation uses, forced to rescan every current video. |
| `Acquisition.ReconcileFiles` | Moves a file only when its record is created. When a video moves, a found record's association to that video is deleted and the file is left where it is, so the old file no longer satisfies the language at the new location; the scan at the new location finds whatever is there. Today every association whose path is not the destination counts as a moved file and its rows are copied to the destination, which would carry a found record to a place it is not. | Which files follow a movie. |
| `SubtitleFile.Identify` | Takes the subtitle path and the video it belongs to, and returns a language tag or none. It replaces `IdentifyLanguage`. The tag is the one the supplier catalogues and `Strings.LanguageName` use, such as `pt-BR` or `zh-Hans`, so a found language compares equal to a wanted one. | Name tokens, folder names, the forced-only label, and the text detector. |
| `subtitle_languages` | Becomes a view with the same columns. Library's query in `Library/Store.cs` does not change. | That found and created subtitles are now one set of records. |
| `Batch` | Owns the two choices, the status line, `CanFind` and `Find()`. It no longer builds a Find button, so the `inlineAction` flag goes. Each host places its own button and wires it to `CanFind` and `Find()`: Settings an `ActionButton` in the batch row, the dialog its primary button, as the contract rules. Both read the `find` and `batch_hint` keys. | The scope, choice and count rules, and the search request. |

The text detector has one adapter, so it gets no interface. `SubtitleFile`
calls it directly.

## Existing owners the change calls

Each rule below has one owner today. The change calls that owner and removes
the copies named here; it adds no second implementation.

| Rule | Owner | Copies to remove |
| --- | --- | --- |
| Is this video still current | `Acquisition.Current` in `Acquisition/Storage.cs` | The inline membership query in `Discover`, and the hash readiness query in `Acquire`, which adds `complete`, `readable` and `size`; give `Current` that option. |
| Current paths, content folder and readiness | The `FileSources` views `collected_files`, `current_files`, `locations` and `contributions`. The torrent's content folder is the common parent of its confirmed file paths when that parent lies below `save_path`. The torrent name is not used: the engine's `ContentPath` in `engine/src/Torrent.cpp` applies the Keep, Create or Strip layout, so the name folder may be absent or added, and only the mapped paths say where files are. `readable` is the file-read readiness rule in [library](library.md#sqlite-and-video-information) | None. Discovery reads only what these views list and walks no further. |
| Library row invalidation | `Store.Invalidate` in `Library/Store.cs` | The two inline `DROP TABLE IF EXISTS library_rows` statements in `Discover` and `Record`. |
| Cleanup on torrent withdrawal | `Acquisition.Cleanup`, run by `FileSources` from `MainViewModel/Features.cs` | None. `subtitle_lookups` gets its `DELETE` in that constant. |
| Subtitle file extensions | One list in `SubtitleFile`, used by discovery and identification | The list in `Discover`. `FileName.Kind` keeps classifying files for Library and is not changed. |
| Filename evidence | `FileName.Interpret` and `Release` in `Models/FileName.cs` | None. Association uses `Release` for the video's name; no second parser. |
| Decoding subtitle text | The byte-order-mark and charset logic in `SubtitleFile.Decode` | None. Split that logic out so the detector's sample and supplier downloads share it; `Decode` keeps its SRT cue check for downloads. |
| Language names | `Strings.LanguageName` for display; supplier `Codes` for the supplier's own code | The private `CultureInfo` dictionary in `SubtitleFile` stays only as the alias-to-tag resolver for `Identify`, because no owner maps names to tags today. It never produces a display name. |
| Supplier waits and allowance | `Supplier.Send`, `Wait`, `Allowance` and `Restore`; persisted by `Acquisition.SaveAllowance` | None. The lookup count never gates a send; `Send` stays the one place a request waits. |
| Settings presentation | `Subtitles.Settings`: `SettingsRow`, `ActionButton`, the status line and `CheckStatus`; the field inventory in `Views/SettingsPage.xaml.cs` | None. Rescan is an `ActionButton` row whose result is the status line; the limit is a numeric `SettingsRow`. Both join the field inventory. |
| Feedback and text | `Model.Report` with a catalogue key; the `subtitles` group in `Resources/en.json` and `es.json`; enums in `Models/Enums.cs` | None. A rescan failure is reported with a key, and new states are enums. |

## Searching once for all extras

1. A job for rank 1 does the search. `Match` returns the ranked candidates.
2. Rank 1 downloads at once, as today.
3. Each extra becomes a pending job that holds its candidate reference in a new
   `subtitle_jobs.reference` column, prefixed with the supplier as
   `subtitle_outputs.reference` already is. Every adapter's `Download` uses only
   the reference, so an extra job downloads without searching again. A
   reference is only valid at the supplier that returned it, so saving a
   different supplier deletes extra jobs that hold one.
4. Extra jobs still run only after every due rank-1 job. This keeps the contract
   rule that extras never delay a first subtitle.

`Find` and automatic acceptance then create only rank-1 jobs. `Targets` still
expands ranks, because recorded extra files must follow their movie when it
moves.

## Lookup count and limit

- Store `subtitle_lookups(origin, file_index, language, total)`. Key it by the
  movie's stable identity, `origin` and `file_index`, as `subtitle_scans`
  already is. Do not keep it on `subtitle_jobs`: jobs are keyed by output path,
  so moving the movie starts a new row, and reconciliation deletes jobs.
- Raise `total` in the database write that follows a search the supplier
  answered, with or without candidates. A search that ends in a
  `SubtitleException` does not count: the supplier did not receive it, as with
  a broken proxy or a refused connection, or refused it, as with a quota or
  authentication failure, and the allowance owner already slows refusals.
  Counting failures would let one bad day of proxy trouble, with hourly
  retries, carry every movie to the limit while the supplier received nothing.
  A crash between the answer and the write loses one count; that errs by one
  and is rare.
- One lookup is one call to `Match` or `Candidates`, however many HTTP requests
  the supplier needs. The download of its result is part of the same lookup.
- When a job serves the same video in two torrents, raise the count for each
  torrent's movie.
- The lookup path checks the limit before every batch lookup, not only `Find`,
  because a batch job accepted below the limit can run after other lookups
  raised the count. At the limit the job completes as a miss.
- Automatic work and the choice dialog raise the count, but the limit never
  blocks them. Automatic work makes two lookups, release name and then hash or
  completion; failed attempts do not count, so retries add nothing. The choice
  dialog is the person's request for one movie.
- Add the existing `Cleanup` statement's rule for `subtitle_lookups`: a row
  goes when its torrent is confirmed removed. A re-added torrent starts at 0.
- Save the limit as `subtitle_settings.lookups`, default **5**. With the two
  automatic lookups, that allows three batch searches of a movie. Settings shows
  it as a number in the Subtitles card's Batch download group, using the
  existing numeric setting row. Its explanation goes in a tooltip. A damaged
  saved value is repaired to the default under
  [saved-value recovery](architecture.md#saved-value-recovery).

## Discovery

1. **Which folders.** For a torrent with its own content folder, walk that
   folder recursively, whatever its subfolders are called. The content folder
   is the common parent of the torrent's confirmed file paths when that parent
   lies below its `save_path`; the engine's Keep, Create and Strip layouts
   decide whether such a folder exists. When the files sit directly in the
   save path, as a single-file or stripped torrent does, inspect only the
   video's siblings and its `Subs` or `Subtitles` folders, as today, because
   walking the save path would read other torrents. Do not follow directory
   links out of the content folder.
2. **Which video.** A subtitle belongs to a video when its name, or the name of
   a folder between it and the content folder, starts with the video's name, or
   when the content folder holds only that one video. The folder case covers the
   common `Subs/<video name>/2_English.srt` layout. Otherwise it is ambiguous
   and gets no record. A plain `English.srt` in a season folder therefore covers
   no episode.
3. **Which language.** Read every token of the subtitle's name after the
   video's name, and the names of the folders between the video and the
   subtitle. Accept a language when exactly one language appears. A `forced`
   token means the file is forced-only, so it gets no record and does not stop a
   full subtitle from being downloaded.
4. **Text, when the name does not tell.** Read the first part of a text subtitle
   (`.srt`, `.ass`, `.ssa`, `.vtt`) through the shared decoding in
   `SubtitleFile`, remove cue numbers, timestamps and markup, and run the
   detector. Read off the database worker and the dispatcher, as `Discover`
   already enumerates folders. Accept only a confident result, and never a
   regional variant. A binary `.sub` or a weak result gets no record.
5. **Records.** Write each identified subtitle as a found record in
   `subtitle_outputs` (`created=0`) with its `subtitle_output_sources` row, after
   `Current` confirms the video, and invalidate Library rows through
   `Store.Invalidate`. Created records never become found.
6. **Once.** The existing `subtitle_scans` markers stay and gain the torrent's
   `stamp` from `contributions`, which already changes when the torrent's path,
   completion, settled state or selected size changes. A video is scanned once
   per path and stamp, never on an ordinary refresh or reopening. A moved video
   is scanned at its new location; `ReconcileFiles` has already dropped its
   found associations at the old one. A bundled subtitle that was not selected
   at the first scan and is downloaded later changes the stamp, so it gets its
   identification without routine folder walks.
7. **Failures.** A folder that cannot be read keeps its existing records and
   gets no marker, so the next reconciliation tries again. A late result for a
   torrent that was removed during the scan is dropped, as the current code
   already does.

**Scan torrent folders for subtitles** in Settings calls `Rescan`. It scans
every current video, adds new files, removes found records whose files are gone,
and shows one result line. It needs no supplier access, works while automatic
downloading is off, and never downloads, moves or deletes a file.

**Text detector.** `LanguageDetection` calls Windows Extended Linguistic
Services (`elscore.dll`) directly, with no package or model files. ELS supplies
ranked languages without confidence scores, so accept its first language only
when the sample contains at least 160 letters and the whole sample and both
halves agree. English and Spanish dialogue fixtures exercise the native call.
Filename variants resolve through the supplier catalogues: `en-US` becomes
`en`, while a supported distinction such as `pt-BR` stays distinct.
Bibliographic codes such as `ger` and `fre` resolve too. `hi` and `sdh` after
another language mark a hearing-impaired subtitle, not Hindi. A sample without
a byte-order mark that is not UTF-8 is read in the system ANSI code page; the
detected language must use that code page, so Russian read as Windows-1252 is
rejected rather than misnamed. Samples and unselected files never count as the
folder's one movie.

## The Download subtitles dialog

The dialog and the Subtitles card in Settings share one control, `Batch`, one
`Count` and one `Find`, so the plan's limit applies to both and neither needs
code of its own.

**Cause: `Count` answers from a copy, not from SQLite.** `Count` reads
`_candidates`, a snapshot the subtitle worker rebuilds only at fixed moments,
while Library reads SQLite on every query. The two disagree whenever a source
change reaches Library before the next `Reconcile`, or `Reconcile` stops at an
error it swallows. Traced in a `SourceChecks.ps1`-style harness: the same
complete movie counts 0 before `Reconcile` and 2 after, with every other gate
passing. This breaks the rule that SQLite is the sole data source and that
display values are derived on read.

**Fix, implemented in the working tree on 2026-10-11.** `Count` derives its
answer when read, with the selection `RefreshCounts` ran before, and
`_candidates` is gone; `Batch` reads it asynchronously and discards an older
read. `Reconcile` evaluates `Advance` before its `Enabled` gate, so a manual
job still reaches its hash stage with automatic downloading Off. The harness
now counts the selected movie before any `Reconcile`, and `SourceChecks.ps1`
passes. Each host now owns its Find button and wires it to `Batch.CanFind` and
`Batch.Find()`; `inlineAction` is removed.

**Find keeps working on finished movies only.** Library, the Plugins coverage
count and Find then describe one set of movies, and a movie still downloading
is automatic work's job.

## Database schema

The app is unreleased, so `Acquisition.Schema` creates the current tables and
view directly, without upgrades for earlier development databases:

- `subtitle_lookups` stores search counts;
- the shared lookup limit lives in `library_settings`, and
  `subtitle_jobs.reference` is declared with its table;
- `subtitle_languages` is a view of the output records and their associations;
- `subtitle_scans` includes `stamp` from creation.

## Out of scope

- Moving target and count selection into SQL, or storing release facts at
  source ingestion. No measurement shows a performance problem. Measure
  `Targets` and `RefreshCounts` on a large library before planning that work.
- A visible download history, and a record of individual download attempts.
  The owner ruling keeps one count per movie and language; the supplier
  reference on each created record already prevents a repeat download.
- A limit on movies per Find.
- Engine, protocol or native changes.

## Implementation order

1. **Contracts.** Add the two owner rulings and the discovery, identification
   and lookup rules to `subtitles.md`. Amend its subtitle-record ruling, which
   says a record identifies an output path and language, to say that found
   records also live at paths TinyTorrent did not name. Replace the
   language-inventory paragraph in `library.md` with a link to it. Fix the
   `interface.md` paragraph and the `subtitles-implementation.md` sentence that
   say changing the supplier turns automatic subtitles off: `subtitles.md` and
   `SaveAccount` keep the switch.
2. **Batch candidates.** Done: `Count` on read, `Advance` before the automatic
   gate, and each host owning its Find button.
3. **Search once.** Change `Supplier.Match` and the extra jobs.
4. **Lookup count and limit.** Add the table, the count in the lookup path, the
   check in `Find` and `Count`, and the Settings number.
5. **Records and discovery.** Change `Discovery.cs` and `SubtitleFile`, write
   found records and add the view to the schema. Change `Recorded` to the
   association lookup and `ReconcileFiles` to move created files only and drop
   a moved video's found associations, and remove the copies listed under
   existing owners. This fixes the duplicate downloads.
6. **Rescan.** Add the Settings action and its result line, with English and
   Spanish text and accessibility names.
7. **Verify once.** Build the app Debug x64 and run the relevant existing
   offline checks. Then review the Settings layout with the off-screen capture
   review.

## Verification

Extend the existing fixtures. Do not add a new suite.

- Using local HTTP replies in `SourceChecks.ps1`: "Up to 4 subtitles" sends
  one search. Repeated Finds stop when the limit is reached, including after a
  restart. The choice dialog is not blocked. No real supplier quota is used.
- Using the inventory fixture in `SourceChecks.ps1`: a bundled `Movie.eng.srt`
  stops the automatic download of `Movie.en.srt`. A nested `Subs/English/2_English.srt`
  is found. A plain `English.srt` in a season folder covers no episode.
  An unchanged refresh reads no folder and keeps a found `Subs/English.srt`
  record through reconciliation. The fixture's existing movie move leaves the
  found file on disk, drops its association to the moved video so the old
  file no longer counts for it, and finds subtitles at the new location. A
  batch job accepted before its count reaches the limit makes no new search.
  A failed connection leaves the count unchanged. An unreadable scan keeps
  its records.
- Reopening keeps created records, references and pending jobs.
- Using the `Batch.N.2024.mkv` fixture in `SourceChecks.ps1`: after a file is
  replaced into the source and before any `Reconcile`, `Count` with that
  file's entry id returns 1 in Not searched and All movies, so the dialog
  enables Find and the Settings card counts it too. With
  automatic downloading Off, a manual job that missed at the release stage
  advances to the hash stage once its file is readable. A failed connection
  leaves the lookup count unchanged.

## Implementation evidence

- `SourceChecks.ps1` passes against the capture and ordinary Debug x64 builds. The fixtures
  cover reopening, shared count selection, one search for four
  downloads, the saved limit and individual-choice bypass, named and text
  languages, forced-only exclusion, nested and ambiguous folders, unchanged
  markers, stamp-triggered discovery, unreadable scans and found-file moves.
- English and Spanish resource keys and format placeholders agree.
- The ordinary Debug x64 build succeeds with no warnings or errors. Both builds
  reuse the existing native engine; generated-output checks find no recursive
  output outside `artifacts/`.
- The Debug x64 capture build succeeds with no warnings or errors. The
  off-screen review completes the subtitle Settings and batch-dialog journeys
  in English/Spanish and Light/Dark; their captured controls fit correctly.
  The broader `library-files` journey later times out waiting for the torrent
  Inspector at `CaptureLibraryFiles.cs:203`, so the whole review is not a pass.
  Evidence: `artifacts/evidence/Capture-library-files-15c1618b-2521-41a5-aae0-ab6a9120b82e`.
