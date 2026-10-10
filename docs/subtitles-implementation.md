# Subtitles implementation plan

## Delivery ruling — 2026-10-09

The [Library delivery ruling](library-implementation.md#delivery-ruling--2026-10-09)
also governs this feature: **all benchmarks, agent interaction runs,
comprehensive test matrices, and repeated intermediate review/build cycles are
cancelled.** Their cost no longer earns its place in this delivery.

Keep the behavior in [Automatic subtitles](subtitles.md), the approved
[Settings handoff](../app/settings-options-prototype.md#subtitles), and
[repository rules](../AGENTS.md). Cancelling validation work does not cancel
safe publication, source validation, cancellation or file ownership.

## Current status

The C# feature is implemented: protected supplier settings, offline language
choices, automatic acquisition, saved work and recovery, completed-file hash
fallback, Find, Recheck, created subtitle moves and accepted deletion cleanup.
OpenSubtitles, SubDL and SubSource use the shared HTTP and routing owner.

Existing integrated builds and focused storage/filesystem checks passed.
Production SubDL acquisition and anonymous OpenSubtitles acquisition succeeded;
the latter returned a valid 39,992-byte SRT. The current native review is
`Capture-library-files-f8426b9a-a43c-4cd6-83a6-e8a261a46299`.
Current routing evidence is
`Source-884a5a6c9c8143c98d623eae375a1c81/provider-routes.json` under the Release
capture output: proxy refusal and unavailable adapter had no direct fallback,
and a selected-adapter supplier Check succeeded.

These observations do not establish every supplier, credential, route or hash
matching combination. Such evidence limitations remain honest limitations;
they are not unfinished code or a new test matrix. Retain existing artifacts
and do not repeat live probes or consume supplier allowance to fill coverage.

The current shared-owner, supplier, routing, naming and Settings corrections
have passed colleague source review. No build, test, app or network request ran
for this batch yet. The owner has authorized a batched Debug x64 compilation
and the relevant targeted offline checks.

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
| C# database owner | Connection, current schema and short serialized transactions. Subtitle queries stay beside subtitle behavior; no pass-through repository. |
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
Filename interpretation and subtitle destinations use the engine-supplied final
name unchanged. The engine distinguishes that name from its current disk path;
C# does not strip `.!tt`, because it may be part of the genuine filename.
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

Extract Proxy's existing Check footer into one shared native-control presentation
used by Proxy and Supplier dialogs, retaining each draft's validation, cancellation
and operations. Show progress, success/failure glyphs and one-line accessible
feedback through existing styles. Keep Subtitle Settings' persistent status and
file problems independent, with one classification for persistent account failures;
trim each failure line and expose full text through
tooltips and accessibility. Apply the existing subtle-button and group-heading
styles to language removal and supplier disclosure. Give supplier disclosure its
existing Info glyph and use the subtle style for its icon-only dialog button.
Give subtitle Settings actions, language removal and supplier draft editors
stable automation IDs, so automation can target the existing controls directly.
Retain operation failures as exceptions and validation messages as catalogue keys,
so a language switch translates existing feedback. Keep account Save validation
beside its credential field, separate from the Check outcome.
Validate duplicate speed-mode
markup and Alternative week styling by source only; expand the change only for
concrete drift, because matching markup alone does not establish competing owners.

## Existing evidence and release gates

Existing evidence remains in `artifacts/`; the current observations are
summarized above. Further exhaustive supplier, archive-failure, quota-reset and
proxy/adapter combinations are **cancelled for this delivery**. Keep bounded
responses, safe archive decoding and fail-closed routing in the implementation.

Supplier distribution permission and privacy publication belong to the
[release assessment](subtitles-release.md). They are not implementation
completion conditions and do not authorize supplier contact or purchases.

## Finish the delivery

Use Supplier for subtitle adapters and SupplierId for their enum discriminator,
including acquisition, account and draft state, so the adapter and its identity
remain distinct. Use namespace-scoped Draft, Account, Deletion and Allowance names;
update owner paths and direct or reflection consumers together. Keep persisted
identifiers, SQL names and automation IDs unchanged, and retain shared ProviderHttp.

1. Preserve the working acquisition and Settings flow. Shared host owners retain
   scheduling, matching policy, cancellation, storage and presentation; suppliers
   retain only their differences.
2. Keep hash capability at the supplier boundary and discovery explanations in
   tooltips. At the existing retry owner, let zero or expired quota deadlines
   use the bounded fallback and preserve valid future server deadlines. Interpret
   the release and reject empty titles and extras once before supplier dispatch.
   SubSource may omit a movie year only while exact release matching and unique
   movie identity still protect the download. When saving an account, persist
   that account's known allowance with its credentials, so switching back cannot
   temporarily hide or lose an active limit. Resolve the reported malformed-subtitle
   failure, route-error classification and application-version claims against the
   actual source; fix confirmed defects without expanding the validation scope.
3. Repair damaged saved subtitle settings at load without deleting records:
   clear an unreadable protected secret, default unreadable language choices,
   and clear invalid dates while preserving healthy values and quota deadlines.
   Native settings loading keeps the first 128 valid schedule periods so later
   schedule edits remain available. Subtitle recycling uses recycle-only shell
   operations and reports failure while keeping an unrecyclable file.
4. Review the settled changes with the colleague. Reuse existing successful
   evidence. Compile once only if production code changes; run a narrow existing
   data-integrity check only when that change affects what it protects.

Done means the required feature works and no concrete correctness finding is
open. It does not require another benchmark, automated interaction sweep,
comprehensive suite or live supplier probe.
