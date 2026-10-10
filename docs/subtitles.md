# Automatic subtitles

Implementation design. The owner approved the [Settings prototype](../app/settings-options-prototype.html)
on 2026-10-08; its [handoff](../app/settings-options-prototype.md#subtitles) owns
the approved presentation. This document specifies behavior, not implementation
evidence. Existing ownership and recovery
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

Normal operation has no dialogs, notifications, torrent columns, inspector
statuses, or manual selection workflow. Persistent problems and their remedies
appear in subtitle Settings, because background subtitle work must stay
out of the person's download workflow. A subtitle failure never changes torrent
completion, seeding, or the availability of the movie.
Shared source and database failures use the existing persistent workspace
feedback and Retry instead, because they affect Library and subtitles together.

**Owner ruling: every wanted video file in a managed torrent is treated alike.**
A movie, one of several movies in a torrent, and an episode in a season pack
all get the same release-name and hash matching, because neither method needs
to know which kind of video it is. Detecting episodes only to skip them would
add logic and misjudge some movies. Series identification, archives, disc
images, translation, and subtitle generation are outside this design because
they require identification or processing beyond file matching. "Movie" in
this document means any such video file.

## Configuration

The Settings prototype uses these inputs in a dedicated Subtitles category. These
are product concepts, not settled wire keys or type declarations.

| Input | Behavior and reason |
| --- | --- |
| Enabled | Off until the selected supplier is configured and ready; the person then turns it on to authorize automatic matching. |
| Supplier | OpenSubtitles, SubDL, or SubSource. The supplier dialog selects a supplier and saves its account details together. |
| API key | OpenSubtitles uses the free application key without customer setup. SubDL and SubSource require the person's own key, following [third-party provider access](architecture.md#third-party-provider-access). |
| User name and password | OpenSubtitles optionally accepts a personal login alongside the application key. Both blank omits user login; entering either requires the complete pair. Other suppliers show only the key. |
| Wanted languages | When unset, use the interface language. An explicit choice downloads one suitable subtitle for **each** selected language, so this is not an ordered fallback list. |

**Owner ruling: when subtitle languages are unset, use the current interface
language.** Do not save a copy of that default: an interface-language change
continues to apply until the person explicitly chooses subtitle languages.
Explicit choices remain independent of later interface-language changes.
Reset restores the default. Offer the supplier's complete
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

**Owner ruling: supplier selection and account details belong in an explicit
Save/Cancel dialog, like proxy configuration.** They form one coherent edit: a
partial account or a supplier change must not take effect while the person is
still typing. This follows the compound-edit exception in the existing
[Settings commit behavior](interface.md#committing-edits). Automatic subtitles
and language choices remain immediate, independent settings.

Edit opens the saved supplier and its account as a draft. Check tests the typed
configuration without saving, enabling downloads, or sending movie information.
Save validates the fields and commits the supplier and credentials together;
it does not require a preceding Check. Cancel and Escape discard the entire draft.
Changing the draft clears its previous check result. Switching supplier clears
the draft credentials so the previous supplier's account cannot be sent to the
new one; the saved configuration keeps running until Save. On a failed save,
keep the draft and explain the failure inside the dialog.

Apply [saved-value recovery](architecture.md#saved-value-recovery) when loading:
an unreadable secret becomes no saved secret, malformed language choices use
the interface-language default, and an invalid saved date becomes none. Repair
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
dialog mechanics, as the [handoff](../app/settings-options-prototype.md#subtitles)
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

Enabling applies to new torrents and currently unfinished downloads. Changing
languages reconsiders missing subtitles for those downloads and accepted work.
Saving a different supplier turns the feature Off and cancels pending work;
enabling it again enrolls unfinished downloads, while finished files need Find.
Torrents that finished before the feature was enabled are not searched
automatically, avoiding an unexpected backlog against the supplier's allowance.
The same applies to files added and finished entirely while the window was
closed: reopening resumes accepted work, but previously unseen finished files
need Find. No engine completion history or hidden worker fills that gap.

**Owner ruling: the person can find subtitles for every movie already
downloaded.** The Automatic subtitles card has a Finished downloads row whose
Find action queues a lookup for each missing subtitle, defined below. Lookups
follow the matching, language, and saving rules below, and run in the
background like any other subtitle work. The action needs Automatic subtitles
On, because it sends movie information to the supplier; while Off it is
disabled. Pressing it again adds no duplicate queued or in-flight lookup.
For a terminal no-match, an explicit Find accepts one new attempt with the best
currently eligible evidence; it does not start automatic no-match polling.
The allowance limits below make a large
collection wait for quota resets instead of failing. Turning the switch Off
cancels the remaining lookups.

Estimate one download for each missing subtitle; retries may use more of the
supplier's allowance. A missing subtitle is a distinct intended output path and
supported wanted language without a current subtitle record, for at least one
wanted, finished movie file with an unambiguous output name. Shared source files
count once; skipped files and unsupported languages never count. The row shows
the total, for example "3 subtitles missing", which
stays short however many languages are wanted. Find's tooltip breaks it down
per language and estimates the cost, for example "Find 3
missing subtitles: English 2, Spanish 1. Estimated downloads: 3. Retries may use
more." This is an estimate, not a billing cap or a charge ledger. The counts
compare synchronized torrent facts with subtitle records and are derived when
read, never stored. Showing them
never reads the drive, because reading every movie's folder is slow. With
nothing missing, Find is disabled. While lookups remain, the row shows how many
subtitles are left. When none remain, it shows the result, for example "Found
52 of 62", until the next Find or restart.

**Owner ruling: C# owns subtitle records in its SQLite database.** A record
identifies a subtitle output path and language, its created/found origin and
the source files it serves. Cross-seeded torrents using that same output share
one record and one acquisition. Created/found describes the output, not each
contributor's opinion of it. That path is an output fact, not a second
torrent inventory: C# needs it to distinguish a subtitle left at an old location
from one beside the movie after a move. Counts consider only records at the
current intended location. C# reconciles known moves as described below; normal
counts never probe the drive.

Accepted work likewise keeps one evidence stage, retry time and outcome per
output/language, with its accepted source associations. Removing one source
cannot cancel work still needed by another accepted source. Different video
paths that produce the same subtitle name are ambiguous; leave that collision
alone rather than acquiring or relabeling a subtitle for the wrong video.

Find counts and Recheck targets use distinct unambiguous output paths/languages,
so two torrents sharing a video do not double the displayed cost, network work
or file checks. Separate copies at different paths remain separate targets.

Removing a torrent deletes its associations and records no remaining source
uses when C# observes confirmed removal or next connects. Plain Remove leaves
subtitle files on disk. Re-adding a
torrent cannot inherit the earlier addition's records. The database owner is
shared with Library, independently of enrichment enablement. Never reconcile
against an incomplete source set. Drive reads occur only for lookup, completed
video hashing, explicit Recheck, or a known subtitle-file operation.

The Automatic subtitles card has a third row, Subtitle files, whose Recheck
action reads the drive on request: one existence check per distinct unambiguous
output path and wanted language for finished movies, never a folder listing.
It includes wanted languages unsupported by the saved supplier, because checking
local files requires no supplier access. A
file present without a record gains a found record; a record whose file is gone
is removed, so that language counts as missing again. Recheck downloads nothing
and sends nothing to the supplier, so it works while Automatic subtitles is Off.
It can take minutes on a large or slow drive, so it runs off the UI dispatcher
and database worker. Closing the window cancels the pass; an interrupted pass
does not claim success. The row shows progress and then when it last ran.
That time is saved with the records, so a
restart does not make a checked collection read "Not checked yet". Find and
Recheck do not
run together, because each changes the records the other reads.

Accepted subtitle work continues after movie completion, including recovery
after corrected credentials or a quota reset, so finishing the movie cannot
discard an outstanding request. Apply language changes to that pending work;
correcting the same supplier's account preserves accepted targets. Disabling,
including saving a different supplier, cancels pending subtitle work and leaves
saved subtitles in place. Re-enabling does not revive cancelled finished targets;
Find explicitly accepts them again.

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

The ordinary switch is the affirmative permission for automatic requests to
that supplier; Off withdraws it and stops new requests, including queued retries.
Check can validate access but sends no movie information
before On. Saving configuration does not silently turn the switch on. Keep the
saved choice through temporary outages, and recover quietly when access returns.
Saving a different supplier returns the switch to Off and updates the disclosure
before permission is given for the new recipient. Changing languages uses the
existing permission.

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
   match is insufficient for automatic download because different cuts can
   have different timing. Require full subtitles: forced-only tracks omit most
   dialogue and cannot satisfy a language request. Use the supplier's explicit
   full/forced field when available. For SubDL, which has no such field, reject
   forced-only labels in release names, file names and descriptions while still
   requiring the exact release and requested language. Among otherwise equivalent
   matches, use the supplier's quality ordering.
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
intended name gains a found record and is never replaced, so a subtitle the
person added is never searched for or paid for. Apart from reading a movie's
hash bytes and known subtitle-file operations, this is the only drive read
outside Recheck. Other subtitle files beside the movie are left alone and do
not count. The first version does not
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
A failed subtitle move leaves the file in place and drops its association rather
than failing the movie move. After accepted Delete files, a subtitle cleanup
failure is reported separately in subtitle Settings; it never returns the torrent
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

| Responsibility | Existing owner to reuse or extend |
| --- | --- |
| Dialog shell, buttons, settings layout | [Dialog](../app/src/Controls/Dialog.cs), [ActionButton](../app/src/Controls/ActionButton.cs), [SettingsRow](../app/src/Controls/SettingsRow.cs), [SettingsSection](../app/src/Controls/SettingsSection.cs), and their shared App.xaml styles. Equal action sizing belongs in that shared styling, not a subtitle-only dialog template. |
| Compound account edits and checks | [Proxy editor](../app/src/Views/Proxy.cs) and [ProxyDialog](../app/src/Views/ProxyDialog.xaml.cs) already handle drafts and obsolete check results. Reuse their common behavior while keeping supplier validation and credentials separate. |
| Ordinary settings, search and text | [Settings](../app/src/Views/Settings.cs), [SettingsPage](../app/src/Views/SettingsPage.xaml.cs), [Strings](../app/src/Services/Strings.cs), and existing English/Spanish catalogues. Extend their inventory, navigation and save path. |
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
Settings shows a compact explanation of persistent problems, including account
failure, exhausted allowance, an unusable proxy route, or
a subtitle that could not be saved, with the affected file when relevant and a remedy or known
automatic retry time. The [handoff](../app/settings-options-prototype.md#subtitles)
places it in critical text on the Supplier card and the Subtitles category card.
This lets the person understand missing subtitles without reading logs; routine
success and transient retries remain invisible. Diagnostics retain technical
detail without credentials.
File save and format failures retain their affected paths independently of the
last supplier request, so another movie's success cannot hide them. Success at
that path clears its failure; an explicit Recheck acknowledges cleanup failures
that have no remaining acquisition job.
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

- Access is the person's own free API key from their SubDL account. SubDL's
  terms allow an application in which each user brings their own key, and
  forbid sharing one free key across an application's users and asking for
  SubDL passwords. TinyTorrent therefore ships no SubDL key and asks only for
  the API key, never a SubDL password.
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

- Access is the person's own API key, generated under My Profile on
  subsource.net and sent as `X-API-Key`. TinyTorrent ships no SubSource key.
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
