# Library catalogue cache

Design record, 2026-10-15. Library retains its last accepted file lists in
`library.db`, so reopening the window shows the saved list as soon as the engine
summary is reconciled, instead of after every torrent's files have been read
again. The engine stays the authority: the summary decides membership, background
reads confirm or replace each list, and only confirmed lists permit file work.
The [database](database.md) and [Library](library.md) contracts own the rules;
this record explains the design behind them.

## Why

- Opening Library after reopening the window showed nothing until the engine had
  answered one file read per torrent. A person who opens Library came to find a
  file now ([ruling](library.md#performance-and-implementation-constraints)).
- A disconnected engine made Library non-actionable although the files on disk
  had not changed, and Open needs no engine.
- The catalogue lived in TEMP tables, which guaranteed that no stale row could
  survive a connection. The cache replaces that structural guarantee with
  confirmation predicates in the shared SQL views.

This design does not reduce engine or pipe work. Every connection still reads
every torrent's files in the background, because the summary stamp (save path,
completion, settlement, size) cannot see a renamed file or one file finishing
inside an incomplete torrent. A reported slow launch was never attributed; the
cache moves the reads off the display path and claims nothing about launch time.

## Behavior

| Situation | What the person sees |
| --- | --- |
| Reopen with the engine in the tray | The saved list with its saved titles, ratings and tags, as one update, after the summary has removed torrents withdrawn while the window was closed. Changed or missing lists then update their rows as each read lands. |
| First launch, or after a database reset | Files appear as each torrent is read, as before the cache. |
| Cold engine start | The window waits in its engine-loading state, as every page does. The saved list is not shown before the summary can prune it. |
| Engine disconnected | Search, filters, saved details, Properties, Open and Open folder work on the last-known rows, and the status reads "Last known". Metadata commands and Show torrent are disabled; file-property reads and subtitle work pause. |
| Torrent command, such as add, move, remove or a priority edit | Every list is read again in the background. Confirmed lists stay in use meanwhile, so metadata commands, subtitle work and file facts continue. The affected torrent's stamp changes, which drops only its own confirmation until its new list arrives. |
| File moved while the window was closed | Its row keeps the old path until that torrent's read lands. Open in that gap reports Missing with Retry, as any recent-files list does. |
| Any search | Rows appear before the local IMDb rating lookup. A row keeps the rating its unchanged entry already received, so typing neither blanks nor re-sorts the IMDb column; only rows whose rating differs change, once per batch. |

## Data

Durable: `contributions` (origin, name and the stamp of the accepted list),
`source_files` and `locations`. Confirmed withdrawal prunes them.

Temporary `file_reads`: the current summary stamp, `collected` (confirmed in
this connection) and `checked` (a process-relative retry time).

The rules live in the shared views
([reconstructed state](database.md#reconstructed-state)):

- `collected_files` holds confirmed files only. Workers, cleanup protection and
  the sharing of choices across a location use it.
- `pending_origins` holds present but unconfirmed origins. Cleanup never deletes
  their metadata, identifications or subtitle ownership.
- `current_files.complete` is cached completion, for listing.
  `current_files.readable` requires confirmation, completion and a settled path,
  for file work.
- `subtitle_languages` joins cached associations, for display.
- Choices and ownership shared across a location join only confirmed files,
  because a cached file can still name a path its torrent has left, and a file
  arriving there must not inherit decisions through it.

Confirmation: a reconnect clears it for every list; a torrent command does not.
A changed summary stamp clears it for that torrent. A failed or unusable read
keeps whatever confirmation the list had and retries later. Confirming an
identical list updates only `file_reads` and leaves the search snapshot valid.

Schema: revision 9, edited in the initial schema under the pre-release ruling.
An existing development database must be reset, which loses its saved metadata
and choices.

## Verification

`app/tests/SourceChecks.ps1` covers: the saved list is available before engine
replies with the same row identity; no file work before confirmation; identical
confirmation reports no content change and leaves the search valid; an incoming
summary does not overwrite the accepted stamp; withdrawal while closed prunes
without losing shared metadata. These checks were edited, not run. Nothing has
been built, launched or timed.

An authorized app run should check: a reopen with a populated database and
delayed file replies; a first launch with several torrents; a torrent command
with automatic subtitles on, confirming that subtitle work continues; Open after
a disconnect.
