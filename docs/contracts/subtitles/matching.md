# Subtitle matching

Part of the [subtitle contract](../subtitles.md). Read its shared outcome,
ownership and recovery rules before the sections here that the change affects.

Acquisition also follows the [record and inventory rules](files.md#subtitle-records-and-accepted-work),
so existing subtitles and accepted jobs govern whether a lookup is needed.

## Early lookup and matching

1. Once metadata is available, inspect wanted video files, excluding recognizable
   samples and trailers. Work per video rather than choosing the largest file,
   so movie collections receive the same behavior as a single movie.
2. Search as soon as the video file exists on disk, using the video's filename
   and release name, with title and year derived when useful. A file not yet
   created is tried again a minute later. Automatic searches start 30 seconds
   after the window opens, so they do not compete with its start for the
   network; requested searches start at once. Torrent identity and file index identify the
   target inside TinyTorrent; the supplier receives search information, not
   TinyTorrent's internal identity or an absolute local path.
3. Accept a result in the requested language as exact when its release name
   equals the video's, ignoring case and accents, with the same year, season
   and episodes, or when the supplier matched the movie hash. A title-only
   match is not an exact match, because different cuts can have different
   timing. Require full subtitles: forced-only tracks omit most
   dialogue and cannot satisfy a language request. Use the supplier's explicit
   full/forced field when available. For SubDL, which has no such field, reject
   forced-only labels in release names, file names and descriptions while still
   requiring the exact release and requested language. Among otherwise equivalent
   matches, use the supplier's quality ordering. Without an exact match, guess:
   see [best guess](#best-guess).
4. If a reliable release match is unavailable and the supplier supports movie
   hashes, wait for the shared [file-read readiness rule](../library.md#sqlite-and-video-information).
   It requires confirmed torrent completion, because per-file byte counts do not
   prove pending disk writes are finished, even with the suffix disabled. C# then
   reads the required ranges and computes the hash locally. For OpenSubtitles
   these are the first and last 64 KiB plus file size. A preallocated file's length
   is not proof of completion. Missing, inaccessible or changed files yield no
   hash; neither torrent info hashes nor partial bytes substitute for it.
5. Download and save a suitable result for each missing language immediately.
   Once that language is satisfied, stop searching for upgrades so background
   work cannot replace a subtitle the person is already using.

OpenSubtitles and SubSource find the movie or show first and then list only
its subtitles. On OpenSubtitles, a title text search matches every title sharing
its words and the API refuses to page past 1,000 results, so searching
subtitles by title text fails for common words. A file name can shorten the
title, as `Brandywine` for *Death on the Brandywine*, so when no title matches
exactly, the one title of the same year that holds the file's title words is
the file's.

### Best guess

**Owner ruling, 2026-10-11: when no subtitle matches the exact release,
TinyTorrent downloads the likeliest one, and in doubt also the next likeliest,
using the saved automatic extras count (0 to 3, default 1).** A person
who wants subtitles wants them at once; a wrong guess costs a player menu
choice, a missing one costs a manual search.

The likeliest subtitle has the highest score. Each release tag it shares with
the video adds 1. The same source family (web, Blu-ray, HDTV, DVD) adds 4 and a
different one subtracts 4, because the source decides the timing. A different
edition (Extended, Director's Cut, Unrated and the like) subtracts 8, because
the edition decides the cut. A hearing-impaired subtitle for a video without
that tag subtracts 1, so it loses ties. Equal scores keep the supplier's order,
and candidates with the same reference or release name count once. An exact or hash match is not in doubt, so it gets
no extras. With a hash supplier, guessing waits for the completed-file hash
search, which can still find the exact file.

The first subtitle is `Movie.en.srt`; extras are `Movie.en.2.srt`,
`Movie.en.3.srt` and so on, so a player loads the likeliest first and lists
the others. Extras use the supplier's allowance only after every first
subtitle due, so they never delay one. Batch status counts movies across requested languages.

One search serves a movie and language, however many extras are wanted. The
first subtitle's job searches and ranks the candidates and downloads at once.
Each extra becomes a pending job that holds its candidate's supplier reference
and downloads without searching again. Find and automatic acceptance create
only first-subtitle jobs, while recorded extra files still follow their movie
when it moves.

### Choosing a subtitle

The person can also choose a subtitle on request. Download Subtitle, in the Library menu, the row's context menu and the Inspector's
More menu for a video, opens a dialog like Fetch metadata. It lists every full
subtitle the supplier offers for that video's title in a wanted language, with a
language choice when several are wanted. Selecting Download saves the chosen subtitle
beside the video under the [saving rules](files.md#saving-and-following-the-movie),
records its supplier reference and created ownership together, and never replaces a file already there. Chosen and batch downloads share
one download, destination-validation and publication owner; a movie moved or removed during the request receives no file at its old destination.
The explicit action authorizes the request independently of automatic
downloading. Nothing opens without the person's request.

The dialog adapts to its current result instead of leaving an empty selection
form on screen. Searching shows a status and native indeterminate progress bar;
Cancel closes the dialog and cancels that search. Results show the selectable
list, bounded by a maximum height, with Download enabled after selection, Search
again when allowed, and Close. Downloading retains the selection and shows
progress under the existing editor's pending-operation close guard.

An empty result shows a short, prominent “No subtitles found” outcome and an
explanation, followed by Search again and Close in the footer. No disabled
Download or Save appears without candidates. The unused list and progress control
collapse, so content determines the height. Errors use the existing inline
InfoBar; a failed download retains its results and selection for retry. A target
invalidated by changed settings or a removed video offers only Close. Supplier
holds remain visible, and Search again never overrides remote waits or pacing.
The next available footer action is the default; asynchronous results do not
move keyboard focus.

There are no subtitle piece priorities, early-byte requests or read_piece hooks
in C++. Early arrival uses filename/release matching; hash matching waits for
confirmed torrent completion. An episode can receive a release-matched subtitle
while its season pack is unfinished; hash fallback waits for the torrent's
completion, rather than guessing readiness from a filename or file length.
Existing torrent priorities, pauses, queue policy and bandwidth
limits remain unchanged. Only accepted files participate, never Add previews.

Subtitle matching uses no Library identification: it establishes release and
edition compatibility itself, and never enables Library enrichment or waits for
it. Filename interpretation is shared with Library through its existing owner,
so subtitles have no second movie-identification system.

The torrent info hash and piece hashes cannot substitute for a supplier's movie
hash: they describe different inputs and use different algorithms. Hash matching
improves release identification; it does not guarantee subtitle synchronization.
