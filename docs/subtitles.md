# Automatic subtitles

Implementation design. The owner approved the [Settings prototype](../app/settings-options-prototype.html)
on 2026-10-08; its [handoff](../app/settings-options-prototype.md#subtitles) owns
the approved presentation. This document specifies behavior, not implementation
evidence. Existing ownership and recovery
rules come from [architecture](architecture.md), [engine](engine.md), and
[protocol](protocol.md); the file-operation amendment below is required when
this feature is implemented.

## Outcome

**Owner ruling: after configuration, subtitles appear beside the movie without
interaction, preferably before the movie finishes downloading.** Early arrival
lets the person start watching with subtitles. Supplier availability and the
arrival of necessary torrent pieces determine when that is possible; movie
completion is not a prerequisite for searching or saving subtitles.

Normal operation has no dialogs, notifications, torrent columns, inspector
statuses, or manual selection workflow. Persistent problems and their remedies
appear only in subtitle Settings, because background subtitle work must stay
out of the person's download workflow. A subtitle failure never changes torrent
completion, seeding, or the availability of the movie.

The first implementation covers ordinary movie files in managed torrents,
including several movies in one torrent. Archives, disc images, TV episode
matching, translation, and subtitle generation are outside this design because
they require identification or processing beyond the requested movie workflow.

## Configuration

The Settings prototype uses these inputs in a dedicated Subtitles category. These
are product concepts, not settled wire keys or type declarations.

| Input | Behavior and reason |
| --- | --- |
| Enabled | Off until the selected supplier is configured and ready; the person then turns it on to authorize automatic matching. |
| Supplier | One Settings row shows the label on the left and the clickable supplier name with Edit on the right. The dialog selects a supplier and saves its account details together. |
| User name and password | Keep optional account fields in the supplier dialog for OpenSubtitles; blank uses TinyTorrent's application access. Require credentials only when the selected supplier requires them. A supplier using a personal token presents that field instead. |
| Wanted languages | When unset, use the interface language. An explicit choice downloads one suitable subtitle for **each** selected language, so this is not an ordered fallback list. |

**Owner ruling: when subtitle languages are unset, use the current interface
language.** Do not save a copy of that default: an interface-language change
continues to apply until the person explicitly chooses subtitle languages.
Explicit choices remain independent of later interface-language changes.
Reset, with the tooltip "Use the interface language for subtitles", restores the
default. Keep it visible and disabled while the default is active, so that state
change does not add a row. Offer the supplier's complete
language catalog, independently of TinyTorrent's translated interface languages,
preserving regional distinctions where available; if the interface
language is unsupported, ask for a supported choice inline in Settings rather
than silently substituting another language. At least one supported language and
the supplier's required access make the configuration usable. Readiness means
that the shipped integration is authorized, required credentials are supplied,
and an effective language is available; access status separately reports observed
results. An account-free integration can be configured without a
username or password. Configuration stays editable while automatic downloading
is Off, so setup never depends on first enabling unconfigured work.

**Owner ruling: use an application-funded, account-free OpenSubtitles package
by default.** TinyTorrent supplies the integration and handles access so people
do not need an OpenSubtitles account, password, personal API key, or subscription.
Once the release's package is authorized and provisioned, setup is already ready
with the interface language; the person can enable it directly. Keep User name
(optional) and Password (optional) in the supplier dialog so people may use their own
OpenSubtitles account. Both blank selects application access; entering an account
requires a complete credential pair; actual authentication establishes whether it
works. Clearing both restores application
access. Do not silently fall back after failed personal authentication, because
that would obscure which account and allowance are being used. Developer
procurement and key management never become end-user setup instructions.

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

Use the engine's existing secret-protection approach;
snapshots expose account status rather than saved passwords. An application API
key belongs to the integration, while a supplier-required personal key belongs
to this configuration.
Reopening the editor preserves a saved secret without returning it to WinUI.
Distinguish keeping that secret, replacing it with newly typed text, and clearing
the account; masked placeholder text is never a credential. Check uses the same
draft interpretation as Save. Clearing an account restores application access
only for a supplier that supports it. DPAPI persistence can reuse the existing
settings owner; the current proxy snapshot's plaintext password is not a pattern
to copy into the subtitle protocol.

The supplier dialog shares the proxy editor's implementation, as the
[handoff](../app/settings-options-prototype.md#subtitles) describes. Keep proxy
and supplier configuration, validation, and network operations with their
existing owners; sharing the editor never shares credentials or routes a
supplier check through proxy validation. Supplier Check and saved-access
validation call the same supplier operation. Account requirements remain
supplier-specific.

Store stable language identifiers, not display names. Search and submitted text
resolve against the same supplier catalog; unknown text is not saved as an
unsupported language. Adding a language that is already chosen does nothing.
Removing the last explicit choice restores the interface-language default.

Enabling applies to new torrents and currently unfinished downloads. Changing
supplier or languages reconsiders missing subtitles for those downloads.
Already completed torrents are not scanned automatically, avoiding an unexpected
backlog against the supplier's allowance.

**Owner ruling: the person can find subtitles for every movie already
downloaded.** The Automatic subtitles card has a Find subtitles action for
finished downloads. It queues one lookup for each movie file of every managed
torrent whose download has finished and whose file is on disk. Lookups follow
the matching, language, and saving rules below, and run in the background like
any other subtitle work. The action needs Automatic subtitles On, because it
sends movie information to the supplier; while Off it is disabled. Pressing it
again queues only movies not already queued or satisfied, so it never spends the
allowance twice. While those lookups remain, the row shows how many movies are
left. The allowance limits below make a large collection wait for quota resets
instead of failing. Turning the switch Off cancels the remaining lookups.

Accepted subtitle work continues after
movie completion, including recovery after corrected credentials or a quota
reset, so finishing the movie cannot discard an outstanding request. Apply
supplier and language changes to that pending work as well. Disabling cancels
pending subtitle work and leaves saved subtitles in place.

### Discovery and permission

The Settings index carries a dismissible Automatic subtitles help card; the
[handoff](../app/settings-options-prototype.md#subtitles) owns its presentation.
The help never takes focus or overlays the task. Dismissing it or leaving the
index without using it counts as seen. Save that fact with window settings and
never show it again on restart or update, because ignoring help is a valid
choice. Set up subtitles navigates only; it never turns the feature on.

Each supplier owns its disclosure and privacy/terms links, shown in an
information flyout from the supplier name; never show OpenSubtitles terms for
another supplier. The flyout is not a modal dialog or a consent step.

[Store policy 10.5.2](https://learn.microsoft.com/en-us/windows/apps/publish/store-policy-archive/store-policy-7-19#105-personal-information)
requires explaining applicable third-party data sharing before opt-in, but does
not mandate a particular card or dialog. The provider link makes its information
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
Saving a changed supplier or account returns the status to Not checked, except
that saving the exact values the dialog just checked keeps that result. Check
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
   dialogue and cannot satisfy a language request. Among otherwise equivalent
   matches, use the supplier's quality ordering.
4. If a reliable release match is unavailable and the supplier supports movie
   hashes, request the torrent pieces covering the video's required byte ranges
   early. For OpenSubtitles these are the first and last 64 KiB, plus file size.
   Read verified payload bytes; a preallocated file's length does not prove those
   bytes have arrived. Search by that movie hash as soon as it can be computed.
5. Download and save a suitable result for each missing language immediately.
   Once that language is satisfied, stop searching for upgrades so background
   work cannot replace a subtitle the person is already using.

Early piece requests use the existing torrent priority owner and respect wanted
files, pauses, queue eligibility, and bandwidth limits. Release the temporary
priority when the required bytes arrive or subtitle work is cancelled, so the
feature cannot leave a different download order behind. It needs the containing
torrent pieces, which can be larger than the hash's byte ranges.
Extend `Torrent::PrioritizePieces` to combine current file priorities, the person's
first/last-piece choice, and active subtitle byte needs. Recompute when a need
ends; restoring an old priority snapshot would undo choices made in the meantime.
Only accepted torrent files participate, never temporary Add-dialog previews.

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

Prefer UTF-8 SRT output. Validate the downloaded subtitle and publish it from a
temporary file only after the download succeeds, so a player never opens a
partially written subtitle. Derive the destination name locally, rather than
accepting a supplier-supplied path. A file already at the intended name
satisfies that language and is never replaced, so an existing subtitle is never
downloaded again. Other subtitle files beside the movie are left alone and do
not count. The first version does not inspect embedded subtitle tracks, avoiding
a media-parser dependency solely to suppress sidecars. Request the supplier's
supported SRT conversion where available; an HTML error page, archive, or
unsupported format never becomes a renamed `.srt`. Bound network response size
and time. Publication is an atomic no-replace operation; checking whether a path
exists before overwriting it is not sufficient.

Record subtitles TinyTorrent creates as files associated with the video. This
is a deliberate amendment to [Removal and moves](engine.md#removal-and-moves),
which currently leaves added subtitles behind:

- Move carries recorded subtitles with their movie, including the move from an
  incomplete-download folder, so early downloading does not strand them.
- Remove leaves them on disk, preserving the existing meaning of removal.
- Delete files includes recorded subtitles under the existing shared-file
  protection, so another managed movie's files remain protected.
- Unrelated files remain outside those operations; folder scanning cannot
  establish that TinyTorrent owns a file.

The existing file-operation owner handles these files. Subtitle failures never
block, fail, or prompt during the movie's move. If a subtitle cannot move, for
example because its name is taken at the destination, leave it where it is and
stop recording it; the movie's move continues. Existing payload collision and
recovery rules still apply to the movie itself. Record each created subtitle
through the existing ordered persistence path, not a second command log.
Ownership comes from that record, never merely from finding a matching filename
on disk. If TinyTorrent stops between publishing a subtitle and recording it,
the file stays on disk unrecorded and still satisfies its language.

## Ownership and quiet recovery

**Owner ruling: reuse the existing implementation before adding another.** Inspect
the owners below and their callers before changing code. Extend the owner that
already performs an operation; when shared code must be extracted, move its
existing callers in the same change. Copying a helper into the subtitle module
would leave two implementations free to diverge. Add only the supplier-specific
request, match, and work decisions that do not already exist.

| Responsibility | Existing owner to reuse or extend |
| --- | --- |
| Dialog shell, buttons, settings layout | [Dialog](../app/src/Controls/Dialog.cs), [ActionButton](../app/src/Controls/ActionButton.cs), [SettingsRow](../app/src/Controls/SettingsRow.cs), [SettingsSection](../app/src/Controls/SettingsSection.cs), and their shared App.xaml styles. Equal action sizing belongs in that shared styling, not a subtitle-only dialog template. |
| Compound account edits and checks | [Proxy editor](../app/src/Views/Proxy.cs) and [ProxyDialog](../app/src/Views/ProxyDialog.xaml.cs) already handle drafts and obsolete check results. Reuse their common behavior while keeping supplier validation and credentials separate. |
| Ordinary settings, search and text | [Settings](../app/src/Views/Settings.cs), [SettingsPage](../app/src/Views/SettingsPage.xaml.cs), [Strings](../app/src/Services/Strings.cs), and existing English/Spanish catalogues. Extend their inventory, navigation and save path. |
| Commands and background completion | Existing PipeClient, engine command dispatch and snapshot owners under the [protocol contract](protocol.md); no second transport or polling loop. |
| Saved state and protected secrets | [Engine settings](../engine/src/Engine/Settings.cpp) and [Store](../engine/src/Store.cpp). Reuse DPAPI and the ordered writer. Store's current metadata write replaces its destination; it is not a no-replace sidecar publisher. |
| Byte availability, paths and file operations | [Torrent](../engine/src/Torrent.cpp) owns piece priorities; [engine file operations](../engine/src/Engine/Files.cpp) own renaming, moves, removal and shared-file protection. Extend these owners for associated subtitles. |
| Networking | Inspect [proxy operations](../engine/src/Engine/Proxy.cpp) and [connection test](../engine/src/Engine/ConnectionTest.cpp) before introducing transport code. Their socket/WinHTTP helpers are existing implementations, but neither establishes routed supplier HTTPS support. Share useful primitives through one owner rather than copying them. |

One engine-owned subtitle module takes a managed torrent file and uses confirmed
subtitle settings. It owns supplier requests, matching, and retry decisions;
the existing persistence, priority, and file-operation owners retain their
responsibilities. Blocking network and file reads run outside the engine's state
thread. WinUI configures the feature through the existing pipe, allowing all
subtitle work to continue with the product window closed.
Before issuing each request and before publishing its result, confirm that the
accepted torrent file still exists, remains wanted, the feature is enabled, and
the supplier/account and requested language still apply. A settings change or
removal cancels obsolete work; an uncancellable late result cannot publish a file
or replace newer status. An already transmitted request cannot be recalled.
Shutdown cancels outstanding work without waiting indefinitely for a supplier.

Persist the pending work and created-file associations needed to resume after a
restart. Reconcile existing output before downloading again, so reconnecting or
restarting does not repeatedly consume the supplier's allowance. Associate work
with the accepted torrent identity and file; a removed and re-added torrent must
not receive an old request's result. Shared movie paths use one acquisition for
each language to avoid duplicate downloads and competing writes.

Retry temporary network failures with bounded backoff and respect the supplier's
retry or quota-reset time. Authentication failures wait for corrected credentials.
Apply rate and quota limits across the supplier account's work, not independently
per movie. A large movie collection must not multiply the permitted request rate.
Settings shows a compact explanation of persistent problems, including account
failure, exhausted allowance, or a subtitle that could not be saved or moved,
with the affected file when relevant and a remedy or known automatic retry time.
This lets the person understand missing subtitles without reading logs; routine
success and transient retries remain invisible. Diagnostics retain technical
detail without credentials. When no match exists, try again when a movie hash
becomes available and once at movie completion if still missing; further polling would
spend requests without new local evidence. Unresolved outcomes survive restart
so reopening TinyTorrent does not reset those limits.

## Delivery order

1. Establish the concrete provider request/response contract with authorized
   development access and lawful fixtures: account-free access, optional login,
   non-content Check, language codes, release/hash evidence, SRT downloads,
   quotas, and actual network routing. Record what each response proves; do not
   turn a successful connection into a guarantee of download entitlement.
2. Implement one engine-owned path from a wanted movie to a safely published
   subtitle, including durable settings, protected credentials, restart recovery,
   and stale-result rejection. Reuse the existing pipe and writer. Prove that
   closing WinUI leaves the work running.
3. Add early byte-based matching through the priority owner, all requested
   languages, and associated-file move/delete handling through the existing
   file-operation owner. Verify a subtitle before movie completion with `.!tt`
   both enabled and disabled; verify collisions and interrupted publication.
4. Connect the approved native Settings UI to those engine operations. Use the
   shared Dialog, native AutoSuggestBox and provider Flyout; remove simulated
   outcomes. Finish the focused checks below and the separate release assessment.

These are implementation slices, not permission to ship partial behavior. The
feature is complete only when early arrival, multiple languages, quiet recovery,
and the approved Settings flow work together.

## Supplier access and privacy

OpenSubtitles.com with an account-free application package is the selected first
integration direction, subject to release authorization. Its
[official overview](https://opensubtitles.tawk.help/article/about-the-api)
documents searches by title, release name, IMDb ID, and movie hash. Its
[hash reference](https://opensubtitles.github.io/oshash/) specifies the required
video bytes, and its
[integration guide](https://opensubtitles.tawk.help/article/getting-started)
describes application keys and download quotas. Its
[professional packages](https://opensubtitles.tawk.help/article/pro-packages)
include application access without end-user sign-in. These establish a possible
integration route, not proof that TinyTorrent has acquired the necessary rights.
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

**Owner ruling: all subtitle traffic follows the configured torrent proxy and
network adapter; if that route is unavailable, wait without direct fallback.**
This includes Check, authentication, language-catalog requests, searches, download
links and redirected subtitle downloads. Apply the selected adapter to connections
to the proxy as well. Resolve supplier hostnames through the proxy where its
protocol supports it; an unsupported route must not silently leak a direct lookup
or request. With neither proxy nor adapter selected, use the normal system route.
Changing the route cancels obsolete requests and retries through the new route.
Keep credentials scoped to their intended endpoint across redirects. Verify this
behavior in the transport implementation before describing it as provided.

Keep matching, languages, file work, and recovery independent of a supplier's
account model. Start with one concrete integration whose request and credential
details stay together; a second working supplier is the point at which a shared
supplier interface earns its cost. Legal uncertainty is resolved at release,
without delaying matching until movie completion or disguising torrent features.

## Verification and contract integration

Follow [testing](testing.md) with focused evidence for these observable outcomes:

- Before setup, automatic downloading is Off. Provider-specific setup works
  while Off, and no movie-identifying request precedes affirmative enabling.
  Disabling prevents new requests and retries; supplier changes require enabling
  for the new recipient. Temporary outages preserve the existing choice.
- Discovery navigates directly to Subtitles without enabling it. Dismissed or
  ignored help stays gone; the category and search remain available.
- Check validates the supplier draft without saving, changing proxy credentials,
  or enabling downloads. Cancel/Escape preserve the saved supplier and credentials;
  Save commits them together, and only a saved supplier change resets the switch.
  Language search accepts supported names and codes, adds no duplicate,
  and preserves explicit choices across interface-language changes. Unset choices
  follow the interface language, including after removing the last explicit choice.
- With a known matching release and available supplier, a subtitle appears while
  the movie is incomplete, including with the product window closed.
- When release matching fails, early verified pieces enable hash lookup without
  waiting for the full video; pauses, unwanted-file choices, and priority edits
  made during lookup remain effective when subtitle priorities are released.
- Each requested language gets the correct basename with the unfinished-file
  suffix both enabled and disabled; existing subtitles remain unchanged and
  forced-only tracks do not satisfy a full-subtitle request.
- Restart, shared movie paths, and repeated metadata updates do not duplicate
  downloads. Corrected credentials or a quota reset resume accepted work even
  after movie completion; persistent failures are explained only in Settings.
- Every supplier operation follows the selected proxy/adapter, including Check
  and redirects. An unavailable route waits without direct DNS or HTTPS fallback;
  changing the route or disabling the feature invalidates obsolete work.
- Moving or removing a movie during lookup cannot publish at a stale location;
  recorded subtitles follow removal and shared-file protection. A subtitle
  collision or move failure leaves the movie's move uninterrupted and preserves
  both existing files and an accurate record of subtitle locations.

When implementing, update the owning architecture, engine, protocol, and
interface contracts for their respective changes and replace the corresponding
rules here with links. This keeps the feature design discoverable without
leaving two authorities for implemented behavior. The approved prototype and its
handoff remain the presentation reference; implementation does not reopen those
UI decisions.
