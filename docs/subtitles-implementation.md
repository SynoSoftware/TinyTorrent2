# Subtitles implementation plan

## First directive: use common sense

Implement the current [behavior design](subtitles.md), not an earlier version of
it. The [handoff](../app/settings-options-prototype.md#subtitles) owns presentation.
Apply [Usability comes first](architecture.md#usability-comes-first) before each
slice: inspect the current owner, reuse working behavior, and remove unnecessary
work from this plan when the agreed outcome remains intact. Report a conflict
with the design or an owner ruling; do not resolve it by changing the feature.

**Owner ruling: optimize for readable, low-bloat code, not the current structure.**
Refactor code touched by subtitles when it materially simplifies the operation,
removes duplication or clarifies responsibility. Move its callers and delete the
replaced implementation in the same slice. Preserve required behavior; do not
preserve an awkward structure or add a wrapper solely to avoid changing it.
Split a function or introduce an abstraction when it makes the responsibility
easier to follow, not when it merely adds another file to visit. Keep unrelated
restructuring outside this feature. Apply the same judgment to deferred technical
findings when their owning slice is implemented.

Use the codebase-design vocabulary: the subtitle module hides acquisition and
recovery behind a small interface; the supplier seam exists because OpenSubtitles,
SubDL and SubSource actually differ. Existing storage, file, settings and presentation
responsibilities each have one owner; the current class/file boundaries are not
fixed. The concrete arrangement below is a starting decision, not a requirement
to retain a type or interface that adds no useful behavior. Follow the
[naming and structure rules](naming.md) at every changed declaration and call site.

Prepared 2026-10-08 against the working tree. This is a revised plan, not
implementation evidence. The owner's latest ruling moves Library, TMDB and all
subtitle acquisition to C#. It supersedes the previous native scheduler,
Network/Supplier/Database extraction, subtitle pipe commands and closed-window
acquisition. Existing supplier evidence remains evidence only for the operation
actually observed.

## Ownership and lifetime

C# owns supplier configuration, protected credentials, matching, downloading,
publication, retries, Find/Recheck and saved subtitle records. It shares the
[application's C# database owner](library-implementation.md#owners-and-reuse), source
adapter and filename interpretation. The product window process is their
lifetime: close cancels I/O and releases workers; reopen reconciles current
torrents before resuming accepted work. Disabling subtitles cancels accepted
pending work; closing the window preserves it for resumption. No hidden process
or engine fallback continues acquisition.

Library and Subtitles are siblings under the existing application lifetime
owner. Subtitles queries current torrent files in SQLite, including unfinished
targets, following the [shared data flow](library.md#sqlite-and-video-information).
It reads neither torrent objects nor Library search results. Enabling subtitles
does not create a Library page, perform a Library query or start TMDB. Shared resources provide
connection/transaction and source-read mechanics, not a common feature scheduler.

The engine owns torrent identity, transfer policy and payload operations. Its
existing pipe supplies torrent/file facts and ordinary torrent commands only.
There is no subtitle-settings, supplier Check, Find, Recheck, credential,
provider-search, job-status or database protocol. Views call their C# owners
directly. SQLite is not linked into the target engine.

| Owner | Decision hidden from callers |
| --- | --- |
| C# subtitle module | Which current file/language needs work; evidence stages, retry and quota waits, stale-result rejection, publication and saved outcomes. |
| Supplier adapters | OpenSubtitles, SubDL and SubSource request shapes, language mappings, authentication, matching evidence and response decoding. These are real alternatives, so one supplier interface earns its place. |
| C# HTTP owner | Platform HTTP/proxy/TLS configuration, request limits, route enforcement and cancellation, shared with TMDB. No title or subtitle decisions. |
| C# database owner | Connection, schema, migrations and short serialized transactions. Subtitle queries stay beside subtitle behavior; no pass-through repository. |
| Shared torrent adapter | Seed and synchronize accepted membership and file facts in SQLite once for both features. Feature work reads SQLite; the adapter supplies no parallel catalogue. |
| Existing Settings and dialog owners | Drafts, focus, language entry, errors and presentation. Supplier values are saved by the C# subtitle owner, torrent settings by their existing engine owner. |

Use existing app role folders and Models/Enums.cs. Do not create a separate
project, generic job framework or a C# class for each previous C++ class.
Transfer the useful behavior, then delete the replaced native callers and code.
The current working tree has other writers; re-read shared files before editing.

## Settled C# scope

**Owner ruling: no Library or subtitle feature is implemented in C++.** C#
creates and migrates SQLite, constructs the current projection, performs every
query and write, downloads TMDB information and subtitles, and manages created
subtitle files. The existing engine pipe supplies ordinary torrent/file facts
only. There is no exception for a small native helper, generic byte interface,
subtitle-aware payload operation or feature-specific invalidation cache.

Early lookup uses filename/release evidence. Hash fallback reads a completed
video only under the shared [file-read readiness rule](library.md#sqlite-and-video-information);
per-file progress is not a disk-ready signal. No engine piece demand or
early-hash interface is added. C# handles
created subtitle moves and window-initiated deletion under the
[subtitle-file rules](subtitles.md#saving-and-following-the-movie).
No subtitle operation runs while the window is closed. On reopening, C#
reconciles recorded output paths for still-managed torrents; missed removal
deletes records without inferring permission to delete files. Native payload
operations remain unchanged. These decisions replace the earlier open native
ownership alternatives.

Find counts and queues only supported chosen languages, retaining unsupported
choices visibly. Saving a different supplier turns Off and cancels pending
work; finished targets need Find after re-enabling. Correcting the same
supplier's account preserves accepted targets. Cost text estimates one download
per missing subtitle and explains that retries may use more; no billing ledger
or unconditional maximum is introduced. These decisions are settled in the
[feature contract](subtitles.md#configuration), not deferred to the Settings slice.

## Existing evidence and release gates

- The approved Settings prototype/handoff remains the presentation authority.
  Existing capture fixtures are not evidence of working suppliers.
- SubDL and SubSource development access was recorded in the earlier plan;
  keys stay in ignored secrets, never source, arguments, fixtures or resources.
  Re-read access locally only when authorized live checks need it.
- A SubSource probe accepted a key and rejected no key. It did not prove the
  planned fixed-title Check, release matching, routed access or ZIP download.
- OpenSubtitles application access remains unprovisioned. Fixtures can exercise
  the adapter; live account-free behavior and release authorization remain gates.
- Native proxy probes and WinHTTP do not prove the C# route. Verify it separately
  without rebuilding dependencies or refactoring unrelated engine networking.

## Delivery order

First pass the shared [source and startup feasibility gate](library-implementation.md#1-move-library-ownership-to-c-and-prove-source-synchronization).
Exercise per-file completion inside an unfinished pack, suffix disabled,
priority/name changes with unchanged aggregate bytes, and window reconnect.
Prove that hash eligibility uses confirmed torrent completion and settled paths,
not per-file byte counts. If existing facts cannot establish a required condition,
report the contract conflict; do not add a native helper or infer missing state.

Then establish shared persistence and deliver one complete subtitle acquisition
alongside local Library search, before expanding Settings and the remaining
suppliers. The numbered sections below group implementation work by owner; they
do not require all three supplier adapters before the first end-to-end path.

### 1. Establish one C# HTTP and supplier path

**Result:** the adapters execute through one cancellable C# HTTP owner with
accurate route behavior and bounded responses. TMDB uses that same owner.

Use HttpClient/SocketsHttpHandler and framework JSON/ZIP facilities. .NET has
[SOCKS4a and SOCKS5 support](https://devblogs.microsoft.com/dotnet/dotnet-6-networking-improvements/);
do not port the proposed Boost/OpenSSL HTTP stack or write another proxy
handshake. Reuse a handler for an unchanged effective route; replace it and
cancel obsolete work when that route changes, so pooled connections cannot
continue on the old route.

Subtitles retain their agreed torrent proxy/adapter policy. Consume confirmed
route values through the existing settings owner; do not copy saved proxy
settings into a second store. TMDB's disclosure must describe its actual route.
Use [ConnectCallback](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.socketshttphandler.connectcallback)
only for required adapter/socket binding the standard handler cannot express.
Resolve the selected interface from Windows, bind connections to it and verify
DNS also follows the agreed route. With a proxy, send destination hostnames to
the proxy; resolve only its address locally. If interface-scoped DNS needs
Windows interop, keep that small operation in this C# HTTP owner. A .NET proxy
setting alone is not evidence that adapter-scoped DNS is correct. Unavailable
routes fail without direct fallback. SOCKS4a refusal gives the existing remedy.

Let the platform perform TLS validation and HTTP framing. Disable automatic
cross-origin redirects when credentials could follow; follow at most five
authorized HTTPS redirects through the same route with fresh origin-scoped
headers. Keep proxy credentials in proxy authentication, and provider credentials
only at the adapter's allowed origins. Start with 32 KiB headers, 2 MiB JSON and
16 MiB subtitle/archive bounds, a 10-second connection limit and a 60-second
whole-operation deadline. Page/redirect chains share that deadline; stream actual
bytes under the bound and honor cancellation between calls and decode chunks.
Verify limits against lawful fixtures rather than guessing success.

Supplier operations are Check, Search and Download. Their typed values carry
release filename/optional known identity, canonical language, optional movie hash,
accepted matching evidence, bytes/encoding and observed allowance. Failures carry
a classified reason, retry/reset time and safe diagnostics, not English parsed
as state. Keep these declarations with their owner and enums in Models/Enums.cs.
No registry, endpoint editor, generic response wrapper or native DTO is needed.

One active subtitle acquisition, from pre-lookup check through publication and
record commit, bounds work. A quota/evidence wait releases that active slot;
it is saved pending work, not a sleeping operation. Explicit Check precedes the
next acquisition; Library search uses no network slot. TMDB and subtitles share
HTTP implementation, not one global request queue or account allowance. Each adapter enforces its
account's observed limits before every HTTP step, including login, pages and
download-link requests, and observes quota headers on failures. Return dated
waits rather than sleeping on a worker. A different draft cannot reset the saved
account's allowance. No required startup Check is added.

OpenSubtitles owns application access, optional personal authentication and token
renewal; a failed personal login never falls back to application access. SubDL
uses filename/release evidence and its documented score threshold; conflicting
language/edition or forced-only evidence still rejects a match. SubSource's
two-step contract is below. One C# filename interpretation serves Library and
subtitles; parsing failure never makes an otherwise eligible video disappear.
A known Library identifier helps matching but never enables TMDB or waits for it.

#### SubSource integration and evidence

The [official reference](https://subsource.net/api-docs), including its published
endpoint examples, establishes base `https://api.subsource.net/api/v1`,
`X-API-Key` authentication, title search followed by `/subtitles`, ZIP downloads from
`/subtitles/{id}/download`, and request limits of 60/minute, 1,800/hour and
7,200/day. These are request counts; no separate download quota is documented.

Check uses `GET /movies/search?searchType=text&q=TinyTorrentConnectionCheck`,
the design's fixed title search unrelated to the person's files. Discard its
results; acceptance of the key establishes access, not a match or download
entitlement. The earlier development probe used `/users/search`, not this Check;
its 200/401 and rate headers are evidence of key acceptance only. Verify the fixed
title call through the planned client before marking Check passed.

SubSource search calls `/movies/search` with an already known IMDb identifier
or the shared C# filename interpretation's title, checking year/episode evidence where available. Then it calls
`/subtitles` with the accepted `movieId` and mapped language. Compare returned
`releaseInfo` locally to the full local release; do not send the filename as a
release filter. If no usable title or identifier exists, return no match for this
supplier rather than inventing an identity. Preserve episode
numbers/edition distinctions, reject `foreignParts` and ambiguous full/forced
evidence, and use the supplier's rating order among equivalent results. Its
documented optional filters have incomplete imported metadata: verify returned
evidence instead of treating a filter as proof. No hash capability is established,
so this adapter does not compute movie hashes. Record actual fields, pagination,
language mappings, conflicting and forced-only cases in stage 1 fixtures.

SubSource download accepts a ZIP only when its directory contains exactly one
SRT file, then returns that file's unchanged bytes. Other entries
are never extracted. Zero or multiple SRTs fail even if a member name appears to
match an episode. Ignore the SRT's member name when deriving the destination;
preserve its text and credits. This is supplier-response decoding, not support
for movies inside archives.

Use [System.IO.Compression.ZipArchive](https://learn.microsoft.com/en-us/dotnet/api/system.io.compression.ziparchive?view=net-10.0)
to inspect entries and open the sole SRT as a stream. Do not extract archive
paths or write a custom central-directory/Deflate parser. Bound both compressed
input and actual streamed output to 16 MiB, honor cancellation/deadlines and
reject invalid archives or unsupported encodings/formats. Verify corrupt and
truncated input against the platform behavior instead of claiming unchecked
integrity. Preserve the SRT's original bytes and credits.

The [terms](https://subsource.net/terms) protect translator ownership/credits;
the [privacy policy](https://subsource.net/policy) is the supplier information
link. Stage 1 records the fixed Check's status and request cost, actual
`releaseInfo`, movie/episode and conflicting/forced-only fixtures, pagination,
complete language mapping, reported rate-window units/resets, download endpoint
and redirect hosts, ZIP members/method/flags/sizes/CRC and unchanged SRT bytes and
encoding. Distinguish documented limits from observed ones. Keep keys, signed
links and search text out of retained logs. Rate failures can use supplied
fixtures instead of exhausting the key. The release assessment separately
requires SubSource's written application-use permission before shipping; it
does not block development testing.

#### SubDL evidence checklist

The key has now arrived. Record the following when running the authenticated
contract checks; key presence alone does not mark any of them passed.

Use a lawful movie/episode and subtitles with known release and language. Record
the following in the stage's evidence, with the key, authorization fields, signed
URLs and identifying search text redacted from retained request logs:

- API version/base URL, date, developer key plan and the applicable per-user-key
  integration terms already identified in the design. Do not record the key.
- Exact key placement; `/api/v2/me` request shape, status and response field names
  for plan, remaining searches/downloads and reset timestamps, including units
  and timezone. Record usage before and after Check to establish that Check
  consumes no subtitle download.
- `/api/v2/files/search` filename/language parameters, paging/ordering, returned
  match-score field and evidence at, below and above 0.8. Record same-release,
  conflicting-release/year/edition, episode and no-match fixtures; identify the
  actual full/forced-only and language fields. Unknown required evidence is a
  finding, not permission to accept title-only or forced-only results.
- Download endpoint, method, selected file identifier, option for one raw SRT,
  content type/disposition, redirect hosts, encoding/conversion declaration and
  byte-level result. Record allowance before and after one download. Confirm
  the single-file path; do not infer its behavior from default ZIP downloads.
- Invalid/missing key, authentication failure, quota exhaustion and transient
  failure response shapes, HTTP codes, headers and reset/retry fields. Capture
  rate-limit evidence from documentation or supplied fixtures when necessary;
  do not deliberately burn 50 downloads merely to provoke it.
- Same Check/search/download through direct, SOCKS5, accepted SOCKS4a and HTTP
  CONNECT routes; adapter binding, DNS observation, refusal, cancellation and
  redirects. Local tunnel fixtures can cover failure cases without live quota.
- A redacted mapping from each observation to parser, matching and error rules,
  plus which assertions remain unverified. Keep lawful response fixtures small;
  no copied subtitle collection or persistent mirror.

**Complete when:** local route fixtures establish tunnel negotiation, adapter
binding, TLS rejection, bounded reads, cancellation and no direct fallback;
the delivered adapters use one interface; SubDL's live checklist establishes
a real early filename match and SRT download; SubSource establishes its fixed
Check, two-step release/full-subtitle match and bounded ZIP decoding. Verify one,
zero and multiple SRTs, bad CRC, truncated headers/data and expanded-size overflow.
Mark observations not yet exercised as unverified and continue independent work
with labeled fixtures.
OpenSubtitles live evidence remains pending its package, not silently passed.


### 2. Save configuration and work in the C# database

**Result:** accepted work, user choices and outcomes survive window closure and
restart without a native database or a second persistence implementation.

Use the shared C# database owner's connection and migration sequence. Confirmed
source identity is durable torrent identity plus metadata file index; re-addition
has a new torrent identity. Do not prune against incomplete source restoration
or treat metadata still pending as invalid indices.

| Saved facts | Purpose |
| --- | --- |
| Supplier configuration, enabled choice, explicit languages or null, protected secret | One coherent user choice, independent of engine settings and Library consent. |
| Subtitle output path/language and created/found origin, with source-file associations | One ownership fact for an output even when torrents share it; distinguish a created output left at an old location from a subtitle beside the current movie. |
| One acquisition per intended output path/language, with accepted source associations, evidence stage, supplier, retry time and classified wait/outcome | Resume accepted work and preserve exhausted searches once per output, without per-source retry copies, response bodies, tokens or signed URLs. |
| Last successful Recheck time | Show an honest last-completed date; counts and partial progress are derived. |

One transaction saves a created/found record and finishes corresponding work.
Coalesce source targets that name the same subtitle output path and language
when accepting work, not only at dispatch. One output has one saved acquisition
state, one created/found fact and its current source
associations; adding a cross-seeding torrent must not turn an app-created output
into an external file or start another paid lookup. Reuse the shared current-file
path comparison. A source association is not a second subtitle output record.
When an accepted source moves, reconcile its association to the new output;
retain old work only while another accepted source still needs it. Do not copy
in-flight results or retry authorities to both locations. Different video paths
colliding on the same subtitle name are ambiguous and receive no automatic
acquisition or relabeling; no collision-resolution framework is needed.
Removal withdraws that source's associations and work; retain outputs and
acquisition still needed by another current source. Apply this when the C#
adapter observes removal or next reconciles. SQLite never creates membership. Keep
transactions short; HTTP, payload reads and Recheck do not occupy the database
worker. Configuration saves complete before callers present them as saved.

Protect personal secrets with user-scoped Windows DPAPI in C#, saved in the same
database transaction as configuration. Keep/replace/clear are editor intent,
not placeholder passwords. Settings views receive presence/status only. Share
draft Check and Save interpretation and compare actual credential changes,
not a redacted projection. No secret or supplier preference is written through
the engine settings command.

**Check:** temporary SQLite verifies saved decisions, queued work, absent-source
cleanup, remove/re-add and stale completions. A failed transaction leaves the
previous configuration and draft intact. No real provider or window is needed.

### 3. Acquire and publish through one C# owner

**Result:** one acquisition path serves automatic work, every supported language
and explicit Find. C# owns matching, hashing, publication and subtitle-file
handling; the engine needs no feature implementation.

At enablement consider current unfinished wanted videos, including episodes and
opaque filenames, excluding recognizable samples/trailers. Completed files
require Find unless their work was already accepted. Reconcile saved pending
work on window reopening before issuing any acquisition request. While Off send
no acquisition requests; explicit supplier Check remains available.
Closing the window preserves accepted scope; it is not the Off command.
The gap while C# was absent does not create a separate completion history in the
engine: files never observed/accepted before completion use the normal Find path.

Persist acceptance before lookup. Check the current movie path and intended
subtitle path once immediately before a lookup. A present subtitle satisfies its
language without download and retains created origin if already recorded;
otherwise record found. Missing movie waits for relevant file evidence;
inaccessible is an error, not absence. No folder enumeration or existence checks
merely to display counts. Keep final basename independent of the unfinished
suffix.

Try filename/release evidence early. For a supplier that supports hashes, query
the shared file-read readiness rule before reading the ranges in C#.
For other suppliers, per-file completion permits
one release-evidence retry. This is one completion attempt; a lookup already made
with the same eligible evidence consumes it. Never hash an incomplete preallocated
file, request torrent pieces, or change torrent priorities for subtitle work.
Persist terminal no-match/format outcomes, and resume temporary
failures with bounded backoff or the supplier's reset time. Account problems wait
for corrected credentials; no-match polling adds no evidence. One waiting file
never occupies the request slot. Retried downloads follow the same bounded
backoff and allowance rules; the displayed cost remains an estimate because a
failed download may already have consumed allowance.

Before requests and before publication, check current membership, wanted-file
choice, enabled setting, supplier/account, route, language and operation lifetime.
For a shared output, at least one captured source target must still be current
and eligible at that path. Removing one contributor neither cancels work still
needed by another nor lets a re-added torrent inherit a stale result.
Off followed by On does not revive a cancelled reply. Disconnect pauses target
work; reconnect reconciles engine identity and locations before resuming it.
Changing or removing a target invalidates in-flight work.

Keep one C# destination calculation for lookup, Recheck and publication: final
video stem plus stable language tag and .srt. Validate SRT/encoding without
rewriting credits or guessing conversion. Stage beside the destination and
publish atomically without replacement; an existence check followed by overwrite
is insufficient. A collision preserves the existing file and records it as
found. A crash between publication and recording leaves an ordinary external
file that the next lookup finds, not another download.

The C# subtitle owner also owns its file operations. Use the saved output path
and confirmed source identity to reconcile created subtitles after observed
moves/renames, including after reopening. Reuse its one destination calculation
and no-replace publication rules; do not extract feature work into C++.
Found files stay put. A subtitle move failure leaves the file and drops its
association without changing the torrent operation's outcome.

Connect window-initiated Delete files through the existing
[FileDraft.Submit](../app/src/Views/FileDraft.cs) path, delegating subtitle rules
to the subtitle owner:
capture created subtitle targets, call the ordinary torrent command, then
handle eligible subtitle deletion in C# after confirmed acceptance, preserving
the existing recycle/permanent choice. Check current shared references and
never infer deletion from uncertain replies, missing membership or a removal
performed while C# was absent. Every gesture calls this same command path;
the engine receives no subtitle paths or ownership.
After confirmed acceptance, subtitle cleanup failure stays outside Submit's
torrent-command failure path: close the accepted draft and report the subtitle
problem in subtitle Settings. Never invite retrying the accepted destructive
command or add a cross-process rollback.

Check both current subtitle associations and torrent payload paths before moving
or deleting an output: an .srt may itself be another torrent's payload. If the
current source view cannot establish that the operation is safe, leave the file.
Do not reproduce the engine's held-path ledger in C# or infer safety merely
because a removed torrent disappeared from its summary. The cross-process race
below remains an explicit limit, not a solved transaction.

Serialize C# publication and its own file-operation requests. Source
reconciliation corrects paths after independent engine moves. A snapshot is
not a native file lock: a concurrent engine move/removal can leave a subtitle
temporarily at its old path or untracked after removal. Apply the design's
accepted limitation instead of adding a native hold, journal or sidecar API.
An interrupted publication with no saved record is later found as an external
file, never treated as owned merely because its name matches.

On window close stop scheduling, cancel network/file work, finish short accepted
database commits and release the connection. Do not keep WinUI hidden or wait on
a supplier's full timeout. Recheck cancellation preserves completed records but
does not advance its successful timestamp.

**Check:** one lawful unfinished-video release match produces a subtitle while
the window is open. Verify no-replace publication, stale replies, explicit Off,
window close/reopen and saved quota/no-match outcomes. Check completed-file
hashing and C#-only move/delete handling, including shared files, collisions,
uncertain command replies, accepted deletion followed by cleanup failure, and
changes missed while closed. A completed episode in an unfinished pack can use
release matching but must wait for torrent completion before hash fallback,
including with the unfinished suffix disabled. Do not claim atomic
coordination with native file operations or closed-window acquisition.

### 4. Find and Recheck

**Result:** counts and work come from synchronized torrent facts in SQLite joined
to subtitle records; Settings does not enumerate folders.

Find uses the same distinct eligible output/language query for counts and work,
joining accepted acquisitions so repeat presses cannot duplicate queued or
in-flight lookups. Explicit Find may accept one new attempt after terminal
no-match, using the best currently eligible evidence; automatic work does not
poll no-match. Keep one transient run for Found X of Y, no durable
job history. Terminal no-match ends a target without incrementing found; quota
and access waits remain pending. On reopen saved jobs resume, but do not invent
a completed-run total. Count only supported effective languages and use the
contract's estimated-cost wording, including its retry qualification.

Recheck runs while Off too. It uses the same destination/distinct-output owner
as Find, but includes wanted languages unsupported by the supplier and requires
no supplier access. Both exclude ambiguous output names. It performs one
exact-name check per target off the dispatcher/database worker in bounded
batches. Present files add found records;
existing created origin stays. Confirmed absence removes a record; inaccessible
preserves it and reports a problem. Revalidate targets before saving each batch.
Only a completed successful pass advances its saved time.

Find and Recheck exclude one another at the C# owner, not just disabled buttons.
Recheck pauses new acquisition dispatch and lets a bounded in-flight publication
settle before checking. Closing the window cancels Recheck. Normal torrent
operations stay available, and no status/progress message crosses the engine pipe.

**Check:** a two-language fixture verifies counts without file I/O, joined work,
quota waits, completed results and exact-name Recheck. Verify interrupted Recheck
does not claim completion or purge inaccessible entries.

### 5. Connect the approved Settings and finish integration

Reuse SettingsPage, SettingsRow, SettingsSection, ActionButton, Dialog, Strings,
Finding and window preferences. MainViewModel owns the subtitle presentation
state and calls its C# owner; no second settings store or native job protocol.
The handoff still owns visual layout, help, flyout and English/Spanish behavior.

Share existing field presentation and dialog mechanics where behavior matches;
move both callers when extracting shared behavior. Proxy and supplier drafts,
validation, secret handling and network operations stay with their own owners.
Do not introduce a configurable editor workflow with mode flags or callback lists.
Supplier Save is explicit and failed Save keeps the draft; Cancel/Escape discard.
Do not copy proxy password snapshots or close-time draft submission.

Use the native AutoSuggestBox with packaged supplier language mappings. Windows
supplies localized/native names for the active display language; use the existing
Strings preparation owner, without shipping another ICU or changing global
language preferences. Stable tags stay stable; unsupported saved choices remain
visible. Add/Enter use one operation, duplicate adds do nothing, and Reset or
removing the last explicit choice saves null.

Both Check surfaces call the same supplier operation, with saved or draft access.
Obsolete draft results cannot update saved status. Exact checked Save may keep
its result; a route/account change clears it. Status never claims entitlement
or clears unrelated disk failure merely because Check succeeded. The supplier
flyout uses that actual supplier's disclosure and links before enabling.

Persist one-time help with window preferences, not SQLite feature jobs. Navigation
never enables subtitles. Add text with each surface in the English and Spanish
catalogues, reusing shared commands/formatters. No simulated counts, toolbar
outcomes or SOCKS refusal become production behavior.

**Check:** one focused capture review after the integrated page/dialog changes.
Review both languages, Light/Dark/High Contrast, narrow layout, keyboard input,
secret placeholders, failed Save, cancellation, Find/Recheck and one-time help.
Recheck the proxy journey after changing shared controls. No application launch is
authorized merely by saving this plan.

## Completion and release

Follow [testing](testing.md): C# logic, SQLite and lawful supplier/route fixtures
are the primary seams. Verify the existing source-read contract without adding
native feature code or tests for a native feature path. Build Debug x64 once
after coherent edits; Release is
for measurements. Respect artifact lanes and never rebuild or modify 3rdParty
as incidental feature work.

Before calling implementation complete, verify one owner per operation, remove
superseded native Library/provider/SQLite code and references, and inspect the
engine with the window closed: no feature worker, database connection or search
projection remains. Check the full design against the C# ownership rule and
resolved count/cost findings. Search engine source, project references and pipe
commands for leftover feature integration; no native fallback, byte-demand
interface or subtitle-aware file operation is accepted.

Finish the existing [release assessment](subtitles-release.md) with actual grants,
application access, key-distribution terms, credits, accurate privacy policy and
observed routing. SubDL/SubSource live evidence and OpenSubtitles provisioning
remain separate from local implementation. A key, fixture or prototype proves
neither distribution permission nor a correct shipped network route.
