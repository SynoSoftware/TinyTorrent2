# Subtitle files

Part of the [subtitle contract](../subtitles.md). Read its shared outcome,
ownership and recovery rules before the sections here that the change affects.

## Subtitle records and accepted work

**Owner ruling: C# owns subtitle records in its SQLite database.** A record
identifies a subtitle file's actual path and language, its created/found origin and
the source files it serves. Cross-seeded torrents using that same output share
one record and one acquisition. Created/found describes the output, not each
contributor's opinion of it. That path is an output fact, not a second
torrent inventory: C# needs it to distinguish a subtitle left at an old location
from one beside the movie after a move. Found records can name bundled files
in nested folders; their association to the movie satisfies the language.
C# reconciles known moves under [file handling](#saving-and-following-the-movie); normal
counts never probe the drive.

Accepted work likewise keeps one evidence stage, retry time and outcome per
output/language, with its accepted source associations. Removing one source
cannot cancel work still needed by another accepted source. Different video
paths that produce the same subtitle name are ambiguous; leave that collision
alone rather than acquiring or relabeling a subtitle for the wrong video.

A request refused by local search protection leaves a blocked, unfinished job,
not a failed download or a completed search with no match. It waits without
polling; Reset search protection reopens blocked work, including manual jobs.
A changed supplier or account reconsiders blocked work against the new request
identity without clearing the old identity's protection. Stop cancels blocked
work and a new Find accepts it through the usual path. A real download failure
remains failed, and only a completed matching answer can record no match and
advance the evidence stage. Reset does not restart completed or failed jobs.

Batch counts use distinct movies, and work uses unambiguous output
paths/languages, so two torrents sharing a video do not double the displayed
cost or network work. Separate copies at different paths remain separate targets.

Removing a torrent deletes its associations and records no remaining source
uses when C# observes confirmed removal or next connects. Plain Remove leaves
subtitle files on disk. Re-adding a
torrent cannot inherit the earlier addition's records. The database owner is
shared with Library, independently of enrichment enablement. Never reconcile
against an incomplete source set. Drive reads occur only for lookup, completed
video hashing, subtitle discovery, or a known subtitle-file operation.

Accepted subtitle work continues after movie completion, including recovery
after corrected credentials or a quota reset. Language changes apply to pending
work. Disabling automatic downloading leaves saved subtitles and manual batches
in place. Re-enabling does not revive cancelled finished targets.

## Subtitle inventory and lookup counts

A torrent's folders are inspected once when it finishes, under the
[completion ruling](../subtitles.md#outcome), before its completion search. A
movie Library displays has its folder inspected once per path and torrent
stamp, in the background, as the
[no-scan ruling](../library.md#purpose-and-experience) allows, so a move or a
later bundled download shows its languages when the row is on screen. The
person did not start that check, so it shows no work item and no failure. Scan
torrent folders inspects the folders of every finished torrent when the person
chooses it. Nothing else inspects a folder, because a walk over the library
costs disk work the person did not ask for. Discovery walks the content folder
derived from confirmed mapped file paths, including nested folders; when the
files sit directly in the save path, it checks only siblings and
Subs/Subtitles folders to avoid other torrents.
Directory links are not followed. A subtitle must identify one movie through its
name or containing folder, or belong to a folder with only one movie. Language
tags and names take precedence; otherwise a text sample is detected locally.
A hearing-impaired tag beside another language, as `Movie.en.hi.srt`, is not a
second language. Text that is not Unicode is read in the system ANSI code page,
as Windows players read it, and a detected language that Windows writes in
another code page is rejected as a misreading.
Ambiguous, weak and forced-only subtitles do not satisfy a language.
Found files and downloads share the same records and Library language inventory.
Counts never inspect folders. Explicit rescanning refreshes
found associations, checks whether recorded downloads still exist, and leaves
files alone. Missing downloads lose their output record while search history
remains. Unreadable paths retain their ownership records and report read errors.

Whether a video already has a subtitle in a language is answered from the
records and their associations to the video, including found bundled files, so
a bundled `Movie.eng.srt` stops the download of `Movie.en.srt` beside it.
Discovery reads only what the shared source views list. It takes the torrent's
content folder to be the common parent of its confirmed file paths when that
parent lies below the save path; the torrent name is not used, because the
engine's Keep, Create or Strip layout decides whether a name folder exists.

A subtitle belongs to a video when its name, or the name of a folder between it
and the content folder, starts with the video's name followed by the end of the
name or one of `. _-([`. It also belongs when the content folder holds only that
one video, unless its name has the form `<other title>.<language>`, which names
another movie. So `Subs/<video name>/2_English.srt` belongs to its video, and a
plain `English.srt` in a season folder covers no episode.
Samples and unselected files never count as the folder's one movie. The
language comes from every token of the subtitle's name after the video's name
and from the names of the folders between them; it is accepted when exactly one
language appears. A `forced` token marks a forced-only file, which gets no
record and does not stop a full subtitle from being downloaded.

When the names do not tell, the first part of a text subtitle (`.srt`, `.ass`,
`.ssa`, `.vtt`) is read through the shared subtitle decoding, with cue numbers,
timestamps and markup removed, and the detector runs. The read happens off the
database worker and the dispatcher. Only a confident result counts, and never a
regional variant; a binary `.sub` or a weak result gets no record. The detector
calls Windows Extended Linguistic Services directly, with no package or model
files. That service ranks languages without confidence scores, so the first
language is accepted only when the sample holds at least 160 letters and the
whole sample and both halves agree. Filename language names and codes resolve
through Windows culture data, plus a table of bibliographic codes such as `ger`
and `fre`. A regional tag then falls back through its parent cultures to the
first tag a supplier catalog offers: `en-US` becomes `en`, and a supported
distinction such as `pt-BR` stays distinct. A found language uses the same tag as the catalogues and the display
names, so it compares equal to a wanted one.

Each identified subtitle becomes a found record with its association, after
the video is confirmed current. Created records never become found. A scan
marker records each video's path and torrent stamp, so a completion scan that
a settings change interrupted continues at the next reconciliation without
repeating the folders it finished, and a displayed movie is inspected again
only after its path or stamp changes. A folder that cannot be read keeps its
existing records and gets no marker. Its movies, like those of a scan the person
stopped, are not inspected automatically again in that session until their path
or stamp changes, so a displayed movie in an unreadable folder fails once, not
at every scroll. A late result for a torrent removed during the scan is
dropped.

Subtitle searches follow the owner's
[shared search limit](../architecture.md#third-party-provider-access), 5 by
default: a hidden count per supplier, search text and language. The search text
is the movie's title and year, with the season and episodes for an episode, so
a second copy or another release of the same movie shares the count. The count
prevents repeated searches from consuming the supplier's allowance. It survives
job resets, moves, removal of the torrent and reopening.
A completed search counts once, including a miss, however many HTTP requests
the supplier needs; connection, access and quota failures do not count. Downloads of the returned alternatives reuse the saved
supplier references and do not search again. Supplier pacing remains shared
across all requests. Automatic work and New respect the cap. Missing data,
All selected and an individual Search again can override local protection for
the captured question; remote holds and pacing still apply. The choice dialog
shows the block and offers Search again when that block allows a fresh attempt. A cached
answer or rejected admission does not consume another search. RequestPolicy
owns the cap and access holds in the existing database.

## Saving and following the movie

Save subtitles beside the video's current location, using its final basename
and a stable language tag: `Movie.en.srt` beside `Movie.mkv`. Obtain that final
name from torrent metadata and the engine's file mapping, and use it
unchanged: C# never strips `.!tt` itself, because that text can belong to a
genuine filename (see the [filename rule](../library.md#ownership-and-data-sources)).
The optional unfinished-file suffix does not participate in naming: the same
subtitle name applies while the video is `Movie.mkv` or `Movie.mkv.!tt`.

Save UTF-8 when the supplier offers that conversion; otherwise save the
supplier's text unchanged, because guessing an encoding can corrupt a correct
file. Validate the downloaded subtitle and publish it from a
temporary file only after the download succeeds, so a player never opens a
partially written subtitle. Derive the destination name locally, rather than
accepting a supplier-supplied path. Before a lookup searches, check two names
once: the movie file, and the subtitle's intended name. A missing movie skips
the lookup, so no subtitle is saved beside nothing; when its torrent is
complete, the job fails with the subtitle error toast. A file already at the
intended name gains a found record and is never replaced. Automatic acquisition
then skips the lookup; an explicit batch may add different candidates beside
the existing file, up to the requested total. Subtitle discovery records other
sidecar languages for Library without changing those files. The first version does not
inspect embedded subtitle tracks, avoiding a media-parser dependency solely to
suppress sidecars. Request the supplier's supported SRT conversion where
available; an HTML error page or unsupported format never becomes a renamed
`.srt`. A ZIP download is accepted only when it holds exactly one SRT file;
its name inside the archive is ignored, because the destination name is
derived locally. Decode it in C# with the platform ZIP implementation, reading
only the selected entry and never extracting archive paths. No custom ZIP parser
or native decoder is needed. Bound network response size, unpacked size and
time. Publication is an atomic no-replace operation; checking whether a path
exists before overwriting it is not sufficient.

**Owner ruling: subtitle-file handling is entirely C#.** The native engine
continues to move/delete torrent payload only, as [Removal and moves](../engine/files.md#removal-and-moves)
already specifies. It never stores subtitle ownership, accepts a sidecar list or
opens C# SQLite.

- When C# observes a known movie move or rename, including an incomplete-folder
  move, it moves only its recorded created subtitle from the saved output path
  to the new intended name, without replacement. On reopening, the same
  reconciliation handles changes made while the window was closed, provided
  the torrent still exists. No folder scan or engine recovery extension is added.
  Leave a subtitle in place when another current movie still uses that location;
  the moved movie then has no subtitle record at its new location.
- A found subtitle is never moved or deleted. When its movie's location changes,
  its old record no longer satisfies that language at the new location.
- Plain Remove leaves subtitle files. For Delete files initiated in the window,
  the C# command owner captures created subtitle targets, submits the existing
  torrent command and handles eligible subtitle deletion in C# after confirmed
  acceptance. Never delete on an uncertain reply, infer delete intent from an
  absent torrent, or reach a subtitle still referenced by another managed file.
  Check current torrent payload paths too: a subtitle may itself be another
  torrent's payload, which these best-effort operations must leave alone.
  Subtitles follow the payload's deletion mode: the Recycle Bin, or permanent
  deletion when Shift is held. A subtitle that cannot be deleted stays in place
  and the failure is reported.
  All gestures for this command use this one C# path.
  An accepted window-side cleanup waits for a refreshed source and the removed
  origins to disappear. Its receipt exists only in that window, so reconnecting
  can finish it without replaying deletion after a restart. Current movie-sidecar
  destinations protect a caption even before their associations are recorded.
- A removal performed while C# is absent leaves subtitle files in place. The
  next window discards obsolete records; it does not replay destructive work.
  Other files and paths without a created record remain untouched.

These operations are best effort, separate from the engine's payload transaction.
A failed subtitle move leaves the file and its ownership association in place
for another reconciliation. Its association records the old movie location, so
it does not satisfy the language at the new location or fail the movie move.
After accepted Delete files, a subtitle cleanup
failure is reported separately as a subtitle notification; it never returns the torrent
command to a retryable state or attempts rollback. C# coordinates its own publication and commands and
rechecks observed targets, but cannot lock an engine-initiated move using a
snapshot. A concurrent engine move may temporarily leave a just-published
subtitle at the previous location; its recorded output path allows the next
source reconciliation to move it. A concurrent removal or crash before recording
can leave an ordinary untracked subtitle file. Do not delete that file by
guessing ownership or recreate removed membership. This limitation is accepted
instead of adding C++ feature code or a cross-process transaction.
