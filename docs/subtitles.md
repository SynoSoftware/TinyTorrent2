# Automatic subtitles

Implementation design. The owner approved the Plugins settings presentation on
2026-10-11: move the Library card unchanged, group subtitle settings, show SQLite
coverage, and keep batch scope and matching separate from automatic downloads.
The prototype is archived at `app/subtitles-prototype.html` on
`codex/subtitles-prototype` (commit `c60e02b`). It supersedes the subtitle
presentation in the earlier Settings handoff. This document specifies behavior,
not implementation evidence. Existing ownership and recovery
rules come from [architecture](architecture.md), [engine](engine.md), and
[protocol](protocol.md). The C# ownership ruling below replaces the earlier
native acquisition, early-piece and companion-file proposals.

## Outcome

**Owner ruling: subtitle matching, downloading, settings and SQLite live entirely
in C#, alongside Library in the product window process.** No supplier operation,
credential, subtitle job or database command crosses into C++. Closing the window
pauses this work and releases its workers; reopening reconciles current torrent
facts before resuming saved work. There is no hidden managed process or native
fallback. C# creates, migrates, populates and queries SQLite, downloads and
publishes subtitles, and manages the subtitle files it created. The engine gains
no feature code, piece requests, sidecar rules or new feature API. This replaces
the earlier closed-window acquisition and native companion-file requirements,
keeping the resident engine focused on torrent work.

**Owner ruling: after configuration, subtitles appear beside the movie without
interaction, preferably before the movie finishes downloading.** Early arrival
lets the person start watching with subtitles. Supplier availability and useful
filename/release evidence determine when that is possible; movie completion is
not a prerequisite for searching or saving subtitles.

Normal operation has no dialogs, notifications, torrent columns or inspector
statuses. A manual choice opens only on request, as described under
[choosing a subtitle](#choosing-a-subtitle). Problems use the window's existing
[feedback placement](interface.md#feedback-placement), because one place for
every problem lets the person find it without opening Settings. A subtitle failure never changes torrent
completion, seeding, or the availability of the movie.
Shared source and database failures use the existing persistent workspace
feedback and Retry, because they affect Library and subtitles together.

**Owner ruling: every wanted video file in a managed torrent is treated alike.**
A movie, one of several movies in a torrent, and an episode in a season pack
all get the same release-name and hash matching, because neither method needs
to know which kind of video it is. Detecting episodes only to skip them would
add logic and misjudge some movies. Series identification, archives, disc
images, translation, and subtitle generation are outside this design because
they require identification or processing beyond file matching. "Movie" in
this document means any such video file.

## Configuration

Settings groups third-party features in Plugins, with SQLite coverage counts.
The Library card moves from General unchanged. One Subtitles card groups
supplier access, languages, download preferences and explicit actions.
Persistent configuration comes first: supplier access, wanted languages, then
automatic downloading and its extra subtitle count. The search limit is the
[shared Plugins setting](architecture.md#third-party-provider-access) above the
Library and Subtitles cards. Spacing separates
configuration groups, with supplier and access together and language entry beside
the wanted languages. Dividers separate configuration from Batch
download and supplier operations from the local subtitle-folder scan, so the
saved limit stays distinct from the choices for one batch.

**Owner ruling: Download automatically controls automatic downloading only.**
It does not enable the subtitle feature. Supplier setup, language selection,
batch downloads and local scans remain available while it is Off, so choosing
manual downloads never requires enabling automatic work.

| Input | Behavior and reason |
| --- | --- |
| Download automatically | Saves automatic intent independently of readiness. Missing credentials or temporary failures pause work without changing the switch. |
| Extra subtitles when unsure | Saves 0 to 3 alternatives for new automatic searches, default 1. Accepted jobs retain their matching choice, and batch choices remain independent. |
| Supplier | Selection applies immediately, with empty credentials for a different supplier. Only Edit opens the account dialog. Selection preserves automatic intent. |
| API key | OpenSubtitles uses the free application key without customer setup. SubDL and SubSource use the free application key built into the release, and the person may enter their own key instead, following [third-party provider access](architecture.md#third-party-provider-access). |
| User name and password | OpenSubtitles optionally accepts a personal login alongside the application key. Both blank omits user login; entering either requires the complete pair. Other suppliers show only the key. |
| Wanted languages | When unset, use the interface language. An explicit choice downloads one suitable subtitle for **each** selected language, so this is not an ordered fallback list. |
| Batch download | Movies: New, Missing data, All selected, with nested movie counts. Download: Exact match only, Best match, Up to 2, 3, or 4 subtitles. These choices apply only to the requested batch. |
| Scan torrent folders for subtitles | Finds bundled subtitles again without supplier access or changes to files. Reports files found and read errors. |

**Owner ruling: when subtitle languages are unset, use the current interface
language.** Do not save a copy of that default: an interface-language change
continues to apply until the person explicitly chooses subtitle languages.
Explicit choices remain independent of later interface-language changes.
Removing the last explicit language restores the default. Offer the supplier's complete
language catalog, independently of TinyTorrent's translated interface languages,
preserving regional distinctions where available; if the interface
language is unsupported, ask for a supported choice inline in Settings rather
than silently substituting another language. At least one supported language and
the supplier's required access make the configuration usable. Readiness means
that the shipped integration is authorized, required credentials are supplied,
and an effective language is available; access status separately reports observed
results. OpenSubtitles can be configured without personal credentials when the
free application key is available. Configuration stays editable while automatic downloading
is Off, so setup never depends on first enabling unconfigured work.

OpenSubtitles uses the existing free application key with an optional user login. Its
[getting-started guide](https://opensubtitles.tawk.help/article/getting-started)
requires a key on every API request and describes limited free downloads without
user login. No paid application package is part of this plan. Actual replies
establish available access and allowance; quota exhaustion waits for the supplier's
reset without purchasing access. Clearing the login restores anonymous use with
the application key. Failed login never silently
falls back to anonymous access, because that changes the allowance being used.

**Owner ruling: supplier selection is immediate and never opens a dialog.**
Edit opens the selected supplier in the
[account dialog](architecture.md#third-party-provider-access). A draft never
changes live credentials. Check validates the complete draft without saving it
or sending movie information. Save does not require a preceding Check; Cancel
and Escape discard the draft. Failed saves retain the draft and explain the
failure in the dialog. Check is disabled while checking or when the required
account values are incomplete; saved secrets count toward readiness.

Apply [saved-value recovery](architecture.md#saved-value-recovery) when loading:
an unreadable secret becomes no saved secret, malformed language choices use
the interface-language default, and an
out-of-range extras count is clamped to 0 to 3. Repair
only damaged values and retain subtitle records, pending work and valid quota
deadlines, so recovery cannot cause repeat downloads.

Use Windows user-scoped DPAPI protection in the C# database owner. Presentation
exposes account status, never a saved secret. Personal supplier keys and optional
login belong to the person's configuration; the free OpenSubtitles application
key belongs to the integration.
Reopening the editor preserves a saved secret without returning it to its fields.
Distinguish keeping, replacing and clearing that secret; masked placeholder text
is never a credential. Check uses the same draft interpretation as Save.
Configuration and protected secrets commit together in C# SQLite. No subtitle
secret belongs in engine settings or the pipe.

The supplier dialog reuses the proxy editor's matching field presentation and
dialog mechanics, as the [handoff](../app/prototypes/settings-options-prototype.md#subtitles)
describes. Proxy and supplier drafts, validation, secrets and network operations
keep their own owners: shared controls do not require a configurable editor
workflow with mode flags or callback lists. Supplier Check and saved-access
validation call the same supplier operation. Account requirements remain
supplier-specific.

Store stable language identifiers, not display names or one supplier's codes;
each supplier maps them to its own codes. TinyTorrent ships each supplier's
language catalog as that mapping, so the language list works offline and
opening Settings never contacts a supplier before the person enables the
feature. Display names come from Windows for each identifier, in the interface
language, so no catalog of language names needs translating. Search and
submitted text resolve against the saved supplier's catalog; unknown text is
not saved as an unsupported language. Adding a language that is already chosen does nothing.
Removing the last explicit choice restores the interface-language default.
After a supplier change, a chosen language that the new supplier does not offer
stays in the list, is marked inline as not offered, and is not requested. It
never vanishes on its own, because the person chose it and may switch back.
Unsupported choices do not enter Find's missing counts or queued work; if none
of the effective languages is supported, the configuration is not usable.

Automatic downloading searches only for
[new torrents](architecture.md#third-party-provider-access), once their files
are known. Changing languages reconsiders missing subtitles for them and for
accepted work. Changing supplier preserves the automatic switch and accepted
work. Work waits until the supplier is configured. Every other file needs Find,
so enabling the feature or reopening the window never starts a backlog against
the supplier's allowance. Reopening cancels unfinished automatic jobs for
previously finished torrents while retaining their search history. Accepted
manual jobs resume, and torrents not previously seen finished remain new.

**Owner ruling: Batch download works while automatic downloading is Off.**
Scope refers to the selected matching goal, not simply files on disk. New
includes unresolved questions without local protection. Missing data adds
locally blocked questions, including earlier searches with no exact match and
downloaded likely matches. All selected also revisits satisfied movies.
The Library selection dialog uses the same
batch control, limiting both counts and requests to its captured selection.
Find is the dialog's primary action beside Close. The host owns attaching the
batch control to acquisition, so a visual reload preserves its selection and counts.
While a batch runs, Find becomes Stop in both places. Stop ends the batch
and the manual jobs kept from an earlier session. Saved subtitles stay, and
automatic work that Find took over returns to the automatic queue.

Find queues complete, wanted movies in every supported wanted language through
the existing acquisition owner. An exact release or hash match gets one
subtitle. Otherwise the choice limits the total likely subtitles, including
existing alternatives. Exact match only permits no guesses. Files are preserved;
supplier references prevent repeat downloads. A newly available exact match can
be saved beside earlier guesses. Changing choices restores the count for the
next search. Status counts movies, not languages or output paths.

Manual jobs retain their matching choice and authorization across restarts.
Switching automatic downloading Off cancels automatic pending work only.
Allowance and retries remain shared with automatic work. The default scope
never repeats a saved miss; Missing data, All selected or an individual Force
fetch authorize one fresh attempt. Reset search protection is a separate
Settings action and enqueues no searches.

**Owner ruling: C# owns subtitle records in its SQLite database.** A record
identifies a subtitle file's actual path and language, its created/found origin and
the source files it serves. Cross-seeded torrents using that same output share
one record and one acquisition. Created/found describes the output, not each
contributor's opinion of it. That path is an output fact, not a second
torrent inventory: C# needs it to distinguish a subtitle left at an old location
from one beside the movie after a move. Found records can name bundled files
in nested folders; their association to the movie satisfies the language.
C# reconciles known moves as described below; normal
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

### Subtitle inventory and lookup counts

Completed, readable movie folders are inspected once per current path and torrent
stamp. Discovery walks the content folder derived from confirmed mapped file
paths, including nested folders; when the files sit directly in the save path,
it checks only siblings and Subs/Subtitles folders to avoid other torrents.
Directory links are not followed. A subtitle must identify one movie through its
name or containing folder, or belong to a folder with only one movie. Language
tags and names take precedence; otherwise a text sample is detected locally.
A hearing-impaired tag beside another language, as `Movie.en.hi.srt`, is not a
second language. Text that is not Unicode is read in the system ANSI code page,
as Windows players read it, and a detected language that Windows writes in
another code page is rejected as a misreading.
Ambiguous, weak and forced-only subtitles do not satisfy a language.
Found files and downloads share the same records and Library language inventory.
Browsing and ordinary counts never inspect folders. Explicit rescanning refreshes
found associations, checks whether recorded downloads still exist, and leaves
files alone. Missing downloads lose their output record while search history
remains. Unreadable paths retain their ownership records and report read errors.

Subtitle searches follow the owner's
[shared search limit](architecture.md#third-party-provider-access), 5 by
default: a hidden count per supplier, search text and language. The search text
is the movie's title and year, with the season and episodes for an episode, so
a second copy or another release of the same movie shares the count. The count
prevents repeated searches from consuming the supplier's allowance. It survives
job resets, moves, removal of the torrent and reopening.
A completed search counts once, including a miss; connection, access and quota
failures do not count. Downloads of the returned alternatives reuse the saved
supplier references and do not search again. Supplier pacing remains shared
across all requests. Automatic work and New respect the cap. Missing data,
All selected and an individual Search again can override local protection for
the captured question; remote holds and pacing still apply. The choice dialog
shows the block and offers Search again when that block allows a fresh attempt. A cached
answer or rejected admission does not consume another search. RequestPolicy
owns the cap and access holds in the existing database.

### Discovery and permission

The Settings index carries a dismissible Automatic subtitles shortcut, with
its explanation in a tooltip rather than body text, following the
[interface](interface.md). It never takes focus or overlays the task. Dismissing it or leaving the
index without using it counts as seen. Save that fact with window settings and
never show it again on restart or update, because ignoring help is a valid
choice. Set up subtitles navigates only; it never turns the feature on.

Each supplier owns its disclosure and privacy/terms links, shown in an
information flyout from the supplier name; never show OpenSubtitles terms for
another supplier. The flyout is not a modal dialog or a consent step.

[Store policy 10.5.2](https://learn.microsoft.com/en-us/windows/apps/publish/store-policy-archive/store-policy-7-19#105-personal-information)
requires explaining applicable third-party data sharing before opt-in, but does
not mandate a particular card or dialog. The supplier link makes its information
available before enabling without forcing users through Edit. Validate this final
permission presentation against the release's actual data flow and agreement under
the existing [release assessment](subtitles-release.md); the prototype does not
establish certification. No checkbox or additional consent step is introduced.

The switch authorizes automatic requests; Find authorizes its requested batch
independently. Saving a supplier or account never changes that choice. Correcting
access resumes accepted work without requiring the person to turn the switch
back on. Check sends no movie information.

The Settings page's Check tests the saved configuration; the dialog's Check
tests its draft. Both call the same supplier check operation. Neither enables
automatic downloading, saves a draft, or changes credentials. Check reports only
what the supplier's non-content endpoint establishes: API reachability and
accepted application/account credentials where supported. It does not spend a
subtitle download to prove access, promise a future match, or clear an
outstanding save error.

Supplier status starts Not checked and reports only results that happened: a
check or request that succeeded or failed. Filled fields never imply access.
Saving a changed supplier, account, proxy, or network adapter returns the
status to Not checked, because the old result no longer describes the saved
configuration. The one exception: saving the exact supplier values the dialog
just checked keeps that result. Check
remains optional; ordinary automatic requests can establish access after
enabling. The switch itself is the saved permission; no separate consent record
exists.

This design uses one clear action, without additional checkboxes, confirmation
dialogs, reminders, or legal banners. Any additional permission needs a specific
applicable requirement, recorded with evidence in the
[release assessment](subtitles-release.md#remaining-release-blockers).

## Early lookup and matching

1. Once metadata is available, inspect wanted video files, excluding recognizable
   samples and trailers. Work per video rather than choosing the largest file,
   so movie collections receive the same behavior as a single movie.
2. Search immediately using the video's filename and release name, with title
   and year derived when useful. Torrent identity and file index identify the
   target inside TinyTorrent; the supplier receives search information, not
   TinyTorrent's internal identity or an absolute local path.
3. Accept a result in the requested language when it identifies the same release
   and has no conflicting movie, year, or edition information. A title-only
   match is not an exact match, because different cuts can have different
   timing. Require full subtitles: forced-only tracks omit most
   dialogue and cannot satisfy a language request. Use the supplier's explicit
   full/forced field when available. For SubDL, which has no such field, reject
   forced-only labels in release names, file names and descriptions while still
   requiring the exact release and requested language. Among otherwise equivalent
   matches, use the supplier's quality ordering. Without an exact match, guess:
   see [best guess](#best-guess).
4. If a reliable release match is unavailable and the supplier supports movie
   hashes, wait for the shared [file-read readiness rule](library.md#sqlite-and-video-information).
   It requires confirmed torrent completion, because per-file byte counts do not
   prove pending disk writes are finished, even with the suffix disabled. C# then
   reads the required ranges and computes the hash locally. For OpenSubtitles
   these are the first and last 64 KiB plus file size. A preallocated file's length
   is not proof of completion. Missing, inaccessible or changed files yield no
   hash; neither torrent info hashes nor partial bytes substitute for it.
5. Download and save a suitable result for each missing language immediately.
   Once that language is satisfied, stop searching for upgrades so background
   work cannot replace a subtitle the person is already using.

OpenSubtitles finds the movie or show first and then lists only its
subtitles. A title text search matches every title sharing its words and the API
refuses to page past 1,000 results, so searching subtitles by title text fails
for common words. A file name can shorten the title, as `Brandywine` for
*Death on the Brandywine*, so when no title matches exactly, the one title of
the same year that holds the file's title words is the file's.

### Best guess

**Owner ruling, 2026-10-11: when no subtitle matches the exact release,
TinyTorrent downloads the likeliest one, and in doubt also the next likeliest,
using the saved automatic extras count (0 to 3, default 1).** A person
who wants subtitles wants them at once; a wrong guess costs a player menu
choice, a missing one costs a manual search.

The likeliest subtitle shares the video's edition (Extended, Director's Cut,
Unrated and the like), which decides the cut, then its source family (web,
Blu-ray, HDTV, DVD) and the most release tags, which decide the timing. Ties
keep the supplier's order. An exact or hash match is not in doubt, so it gets
no extras. With a hash supplier, guessing waits for the completed-file hash
search, which can still find the exact file.

The first subtitle is `Movie.en.srt`; extras are `Movie.en.2.srt`,
`Movie.en.3.srt` and so on, so a player loads the likeliest first and lists
the others. Extras use the supplier's allowance only after every first
subtitle due, so they never delay one. Batch status counts movies across requested languages.

### Choosing a subtitle

The person can also choose a subtitle on request. Download Subtitle, in the Library menu, the row's context menu and the Inspector's
More menu for a video, opens a dialog like Fetch metadata. It lists every full
subtitle the supplier offers for that video's title in a wanted language, with a
language choice when several are wanted. Selecting Download saves the chosen subtitle
beside the video under the [saving rules](#saving-and-following-the-movie),
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

[Library identification](library.md#identification-and-early-enrichment) can supply an
already known movie identifier. Subtitle matching still establishes release and
edition compatibility itself, and never enables Library enrichment or waits for
it. Keep any shared filename identification with its existing owner rather than
creating a second movie-identification system for subtitles.

The torrent info hash and piece hashes cannot substitute for a supplier's movie
hash: they describe different inputs and use different algorithms. Hash matching
improves release identification; it does not guarantee subtitle synchronization.

## Saving and following the movie

Save subtitles beside the video's current location, using its final basename
and a stable language tag: `Movie.en.srt` beside `Movie.mkv`. Obtain that final
name from torrent metadata and the engine's file mapping. The optional
unfinished-file suffix does not participate in naming: the same subtitle name
applies while the video is `Movie.mkv` or `Movie.mkv.!tt`.

Save UTF-8 when the supplier offers that conversion; otherwise save the
supplier's text unchanged, because guessing an encoding can corrupt a correct
file. Validate the downloaded subtitle and publish it from a
temporary file only after the download succeeds, so a player never opens a
partially written subtitle. Derive the destination name locally, rather than
accepting a supplier-supplied path. Before a lookup searches, check two names
once: the movie file, and the subtitle's intended name. A missing movie skips
the lookup, so no subtitle is saved beside nothing. A file already at the
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
continues to move/delete torrent payload only, as [Removal and moves](engine.md#removal-and-moves)
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

## Ownership and quiet recovery

The [product code rules](../AGENTS.md#code-style) govern reuse and ownership.
The table below identifies the existing owners for subtitle work; supplier
implementations add only the request and decoding behavior that differs.

Supplier's operation carries the account, HTTP access and acquisition validity
through each adapter call. The shared send path validates it before reserving
allowance, including login, retries and redirects, so adapters cannot accidentally
spend requests for obsolete work. Account Check carries no movie membership;
its cancellation and network route still apply.

| Responsibility | Existing owner to reuse or extend |
| --- | --- |
| Dialog shell, buttons, settings layout | [Dialog](../app/src/Shared/Dialog.cs), [ActionButton](../app/src/Controls/ActionButton.cs), [SettingsRow](../app/src/Controls/SettingsRow.cs), [SettingsSection](../app/src/Controls/SettingsSection.cs), and their shared App.xaml styles. Equal action sizing belongs in that shared styling, not a subtitle-only dialog template. |
| Compound account edits and checks | [Proxy editor](../app/src/Settings/Proxy.cs) and [ProxyDialog](../app/src/Settings/ProxyDialog.xaml.cs) already handle drafts and obsolete check results. Reuse their common behavior while keeping supplier validation and credentials separate. |
| Ordinary settings, search and text | [Settings](../app/src/Settings/Editor.cs), [SettingsPage](../app/src/Settings/SettingsPage.xaml.cs), [Strings](../app/src/Shared/Strings.cs), and existing English/Spanish catalogues. Extend their inventory, navigation and save path. |
| Commands and background completion | Direct C# asynchronous operations at the subtitle owner; existing PipeClient supplies torrent facts only. No supplier/job pipe protocol. |
| Saved state and protected secrets | The C# SQLite owner shared with Library; one schema and transaction path, Windows user-scoped DPAPI for secrets. |
| Subtitle records and pending work | The same C# database owner, with subtitle decisions at the subtitle module. Library enablement never gates subtitle work. |
| Torrent facts and file operations | The shared C# torrent adapter seeds and synchronizes SQLite from existing file facts. Subtitles queries SQLite. C# owns subtitle publication and file handling; engine payload operations remain unchanged. |
| Networking | One C# HTTP owner used by TMDB and subtitle adapters. Use platform HTTP, proxy, TLS and ZIP capabilities; add only route behavior the platform does not supply. Engine networking stays with its existing callers. |

One C# subtitle module queries current managed-file facts and its confirmed
settings from SQLite, following the [shared data flow](library.md#sqlite-and-video-information).
Shared application settings, such as the effective proxy, adapter and interface
language, still come from their existing owner; they are not copied into another
settings store to satisfy the file-data rule.
It owns supplier requests, matching, retry decisions and direct Settings
operations. Network and file reads run
outside the UI dispatcher and database worker. The engine owns torrent state
and operations; it neither schedules subtitle jobs nor publishes their status.
Before issuing each request and before publishing its result, query SQLite to
confirm that the output still serves at least one captured, current wanted
source file, the feature is enabled, and the supplier/account and requested
language still apply. Publish at the movie's
latest observed location, because the movie can move during a request. A settings
change or observed removal cancels obsolete work; an uncancellable late result
cannot replace newer state. The concurrent file-operation limitation above
applies to publication. An already transmitted request cannot be
recalled. Window closure cancels outstanding work without waiting indefinitely
for a supplier, preserving accepted unfinished jobs for the next window.

Persist the pending work and subtitle records in SQLite so work resumes after a
restart. The records, and the check before each lookup, keep
reconnecting or restarting from consuming the supplier's allowance again. If
the database cannot be used, subtitle work pauses and the shared workspace
feedback offers Retry, because without records TinyTorrent cannot avoid repeat downloads.

Retry temporary network failures with bounded backoff and respect the supplier's
retry or quota-reset time. Authentication failures wait for corrected credentials.
Apply rate and quota limits across the supplier account's work, not independently
per movie. A large movie collection must not multiply the permitted request rate.
While Automatic subtitles is On or requested manual work waits, a lasting
supplier problem, such as refused access, an unusable proxy route or subtitle
files that cannot be saved, shows in the [message bar](interface.md#message-bar)
with Settings. A waiting batch's status names the same problem instead of
Searching. A subtitle that could
not be downloaded or saved for one movie is an event: one error toast names the
latest subtitle file and counts the others, and stays until the person closes
it. Settings shows only state: whether access was checked and when a used-up
allowance returns. This lets the person understand missing subtitles without
reading logs; routine success and transient retries remain invisible.
Diagnostics retain technical detail without credentials.
When early release matching finds no match, retry
once with a C# movie hash after confirmed torrent completion if the supplier
supports it; otherwise retry release evidence once at per-file completion.
Do not repeat a lookup already made with that eligible evidence. Further polling would
spend requests without new local evidence. Unresolved outcomes survive restart
so reopening TinyTorrent does not reset those limits.

## Delivery order

Use the [current implementation plan](subtitles-implementation.md#finish-the-delivery).
The feature is implemented in C# alongside Library's shared source and database
owners. Finish concrete defects and presentation corrections without repeating
the old source/startup feasibility gate.

**Owner ruling, 2026-10-09:** benchmarks, agent interaction runs, exhaustive
supplier/route/failure matrices and comprehensive test checklists are
**cancelled for this delivery**. Reuse useful work and valid evidence. Keep the
behavioral rules in this design, including early arrival, languages, quiet
recovery, source validation, no-replace publication and file ownership.

## Supplier access and privacy

OpenSubtitles.com access follows the free application key and optional login described
above, subject to release authorization. Its
[official overview](https://opensubtitles.tawk.help/article/about-the-api)
documents searches by title, release name, IMDb ID, and movie hash. Its
[hash reference](https://opensubtitles.github.io/oshash/) specifies the required
video bytes, and its
[integration guide](https://opensubtitles.tawk.help/article/getting-started)
describes API keys and download quotas. These establish a possible integration
route, not proof that TinyTorrent has acquired the necessary rights.
The [release assessment](subtitles-release.md) records the evidence and gaps.

Each shipped supplier needs terms covering the intended application distribution,
automated search and local subtitle downloads. Use its authorized API, supported
authentication, quotas, and retry instructions. Request subtitles for managed
movies rather than harvesting a catalog; save them locally without a TinyTorrent
subtitle mirror or redistribution service. Preserve required notices in files.
Use a small supplier credit/link in Settings and any required notices in About;
confirm placement against the agreement rather than inventing branding rules.

Send only matching inputs the selected lookup needs: a movie/release name or
identifier, requested languages, and file size/hash when applicable. Compute
hashes locally. Torrent info hashes, magnets, trackers, peer lists, absolute
paths, unrelated filenames, movie bytes, and library inventories stay out of
supplier requests. Authentication and the required app/version identification
are separate from matching. Use HTTPS, protect saved credentials, and exclude
credentials, tokens, signed download links, and search contents from routine
logs. The supplier necessarily sees the request's network address; a hash is
not a promise of anonymity.

Publish one accurate TinyTorrent privacy policy, accessible from Settings,
About, and the Store listing. The [subtitle privacy draft](subtitle-privacy.md)
specifies this feature's contribution; it needs the publisher's actual identity,
contact, recipients, routing, and retention facts before publication. Supplier
website policies do not establish API-specific retention.

All supplier operations follow the product's [network route](architecture.md#network-route).
Verify the shared transport before describing its behavior as provided.

### SubDL

**Owner ruling: SubDL is the second supplier.** It gives development a supplier
that can be tested with the person's own key alongside OpenSubtitles' free
application-key access. Facts below come from
SubDL's [API documentation](https://subdl.com/developers) and
[terms](https://subdl.com/terms), read 2026-10-08.

- Access is the free API key built into the release, or the person's own key
  from their SubDL account, which replaces it. SubDL's terms, read
  2026-10-13, require a paid plan for commercial use and say a free key must
  not be used as a shared key to power a commercial service for its users;
  they allow an app in which each user brings their own key. They do not
  address a shared key in a non-commercial app. The built-in key follows the
  [owner ruling](architecture.md#third-party-provider-access). TinyTorrent
  asks only for an API key, never a SubDL password.
- Matching uses the release filename search, `GET /api/v2/files/search`, and
  accepts an individual SRT only when its release and language match exactly
  under the matching rules above. Explicit forced or foreign-part labels are
  rejected. SubDL has no movie-hash search, so step 4's completed-file hashing
  does not apply; filename search can start as soon as metadata names the file.
- `GET /api/v2/me` reports the key's plan, usage, and reset times without movie
  information, so Check uses it.
- A free key allows 2,000 searches and 50 downloads a day. Responses report the
  remaining allowance, and quota waits use the reported reset time.
- Downloads default to a ZIP archive; request the single subtitle file and
  accept only SRT, under the saving rules above.

### SubSource

**Owner ruling: SubSource is the third supplier.** Facts below come from
SubSource's [API documentation](https://subsource.net/api-docs) and
[terms](https://subsource.net/terms), read 2026-10-08.

- Access is the API key built into the release, or the person's own key,
  generated under My Profile on subsource.net, which replaces it. Either is
  sent as `X-API-Key`. SubSource limits requests per key, so every
  installation shares the built-in key's limits.
- Matching takes two steps. `GET /movies/search` finds the title by text with
  year, or by IMDb ID when one is known; `GET /subtitles` then lists that
  title's subtitles by `movieId` and language. Accept a result only when its
  release information names the file's release, under rule 3. Results marked
  `foreignParts` are forced-only and never accepted. SubSource has no
  movie-hash search, so step 4's completed-file hashing does not apply.
- Language filters use names such as `english`; the shipped catalog maps
  stable identifiers to them.
- Every call counts against 60 requests a minute, 1,800 an hour, and 7,200 a
  day per key; responses carry rate-limit headers. No separate download quota
  is documented.
- Downloads are always ZIP archives, unpacked under the saving rules above.
- No account-information endpoint is documented. Check sends one fixed title
  search unrelated to the person's movies; a rejected key returns 401.
- The terms say subtitles belong to their translators and must not be
  altered, which the rule to save text unchanged already satisfies. They say
  nothing about API use by applications; the
  [release assessment](subtitles-release.md) records that gap.

Keep matching, languages, file work, and recovery independent of a supplier's
account model. The three suppliers differ in credentials, search steps, the
access check, download format, and quota reporting; one supplier interface
owns those differences.
Legal uncertainty is resolved at release, without delaying matching until movie
completion or disguising torrent features.

## Verification and contract integration

Follow [testing](testing.md) and the current implementation plan. The former
comprehensive verification checklist is **cancelled**. Existing successful
storage, filesystem, supplier and UI evidence remains useful; do not rerun it
for unchanged behavior or probe suppliers to fill every unverified combination.

Keep focused review of concrete correctness findings, particularly unwanted
network requests, stale publication, overwriting an existing file, or moving
and deleting files the feature does not own. These are behavior requirements,
not a mandate to create a test for each scenario.

When implementing, update the owning architecture, engine, protocol, and
interface contracts for their respective changes and replace the corresponding
rules here with links. This keeps the feature design discoverable without
leaving two authorities for implemented behavior. The approved prototype and its
handoff remain the presentation reference; implementation does not reopen those
UI decisions.
