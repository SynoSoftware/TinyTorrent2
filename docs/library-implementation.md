# Library implementation plan

## Delivery ruling — 2026-10-09

**Owner ruling: finish the feature; cancel the validation programme.** The
following work is cancelled, because its cost is delaying delivery of an
implemented feature:

- **Cancelled:** all benchmarks, numerical latency and memory gates, baseline
  comparisons, 10,000/100,000-file runs, and further profiling for this delivery.
- **Cancelled:** agent-driven interaction journeys, capture matrices, exhaustive
  keyboard/theme/provider/route combinations, and comprehensive test checklists.
- **Cancelled:** repeated intermediate reviews and builds. Use existing valid
  evidence; review the settled changes once and rebuild only changed code.
- **Cancelled:** a TableView reset/animation redesign or persisted stale file
  catalogue as a prerequisite for delivery. Keep the current behavior.

Cancellation changes delivery scope, not the feature's data-safety rules.
Follow [repository rules](../AGENTS.md), [testing](testing.md), the
[Library spec](library.md), [interface](interface.md), and
[approved prototype](../app/library-prototype.html). Those owners define the
behavior; this plan does not repeat their requirements.

## Current status

Library is implemented in C#: source synchronization, shared SQLite, complete
local search, filters, file details and opening, identification, optional video
providers, and native page/dialog integration. Subtitles shares the source and
database owners, not Library enablement.

The integrated builds, focused production storage/file checks, supplier checks
and 21-scene native review already recorded in `artifacts/evidence/` remain
valid for the code they exercised. The last verified capture app is
`76F011E3`; the native review is
`Capture-library-files-f8426b9a-a43c-4cd6-83a6-e8a261a46299`.
These are existing results, not instructions to repeat them.
The settled ownership, routing, naming and presentation corrections passed
colleague source review with no open finding. Final Debug x64 engine and window
builds passed with zero warnings and zero errors. The successful engine compile
took 58.26 seconds and the window compile 180.27 seconds. Compiler failures were
corrected before completion: the tray's locale pointer type, stale generated
Proxy bindings, and application-template font lookup. Only affected compilation
was repeated; dependencies and Release were not rebuilt.

`app/tests/SourceChecks.ps1` passed against the final assembly in 3.23 seconds,
covering source membership, identification, database recovery and subtitle file
ownership. `app/tests/ProviderChecks.ps1` passed in 0.80 seconds: HTTP CONNECT,
SOCKS4a and SOCKS5 reached local proxy listeners, and an unavailable adapter
failed without direct fallback. The required generated-output checks were empty.
Logs are `artifacts/library-subtitles-final-engine-debug-build.log`,
`artifacts/library-subtitles-final-debug-build.log`,
`artifacts/library-subtitles-final-source-checks.log` and
`artifacts/library-subtitles-final-provider-checks.log`.

No application, live supplier probe or fresh visual capture ran for this batch.
The earlier capture remains evidence for its own source version, not a pixel
verification of the current build. The approved prototype composition and
interactions remain the authority; AI reports do not authorize redesign.

The cancelled 10,000-file benchmark did not pass: worst warm p95 was 421.55 ms
for Videos, 376.81 ms for Music and 612.87 ms for Files. First navigation was a
single 1,083 ms observation, not a percentile. Memory observations were not a
controlled before-feature comparison. Cancellation does not make these targets
passed, and this delivery makes no “instant at 10,000 files” claim.

The Public websites provider remains optional and removable. Its recorded
browser-access limitations remain in [its status](movie-websites.md#implementation-status).
The owner stopped further browser experiments; do not reopen them.

## Owners and reuse

These are responsibilities in the existing C# app, not one project, interface
or forwarding class per row. Keep feature code in `app/src/Library/` and
`app/src/Subtitles/`, following [app folders](naming.md#app-folders);
the membership source seam and movie provider seam serve distinct decisions.
Movie providers share the request and persistence owners; provider capability
determines whether background population is available.

| Owner | Responsibility |
| --- | --- |
| C# torrent adapter | Seed and synchronize accepted membership and file facts in SQLite through PipeClient, including unfinished and unwanted files. It decides neither Library eligibility nor subtitle eligibility and exposes no separate file catalogue. |
| Library | Query finished entries from SQLite; own identification decisions, enrichment acceptance and SQL search/filter/count queries. |
| C# database owner | One connection, schema version check and serialized transaction path. Existing application wiring supplies each owner's schema; Library and subtitles keep their domain SQL with their behavior. |
| Filename interpretation | One file-kind classification and filename interpretation used by both features, without provider or scheduling decisions. |
| Video lookup | Selected-provider requests, cancellation and background eligibility; Library accepts identification evidence and saves video information. TMDB owns its API decoding; Public websites owns website extraction and installed-browser access. |
| File facts reader | Windows property reads for dates and audio tags; Library accepts and saves the returned facts. |
| Library Browser | Query/configuration input, stable row objects, selection, navigation and opening outcomes; Library initialization, worker events, failure pause, retry and worker drain. TableView performs the only sort. |
| Existing Inspector and opening owners | Shared card mechanics and Windows file execution. Library supplies a target and consumes the outcome. |
| C# subtitle owner | Supplier matching, downloading, configuration, saved work and records as defined in its plan; it queries current torrent files in SQLite, including unfinished targets, independently of Library queries and enablement. |

Reuse the title-bar search, `Finding.cs`, `Workspace.cs`, `ColumnLayout`,
`Placement.cs`, existing filter drawer, `InspectorPane`, `Field`, `Strip`,
`Opening.cs`, Settings and Strings. Extract shared behavior only where these
direct callers need it, moving the old caller in the same change. Domain state
does not belong in XAML code-behind or a general-purpose helper.

Library and Subtitles are sibling modules. The existing application lifetime
owner starts source synchronization and supplies the shared database; neither
feature creates or owns the other's lifetime. Follow the
[SQLite-only data flow](library.md#sqlite-and-video-information): the adapter
writes current facts into SQLite and both features query them there. Keep one
current-source projection in SQLite, not an adapter catalogue plus separate
Library and subtitle catalogues. The database owns transaction mechanics;
each feature owns the meaning of its queries. These responsibilities need no
common enrichment interface, new host class, service layer or repository.

## Replaceable movie providers

The owner requires optional providers to be removable and new providers to use
the same Library workflow. The following seam replaces concrete TMDB/Public
branches. The source implements this seam; existing evidence is summarized in
[Current status](#current-status).
This is compile-time composition in the existing app, because no runtime plugin
installation or discovery is required.

A provider owns only what differs between providers: how it searches, how it
describes a choice, which kinds it knows, when Library may ask it, and its own
settings. It knows neither current torrent membership nor whether an answer may
be saved, and it receives no `Store`:

```csharp
internal interface IProvider
{
    string CatalogId { get; }
    bool IsAvailable { get; }
    VideoSchedule Schedule { get; }
    bool IsLocalized { get; }
    IReadOnlySet<VideoKind> Kinds { get; }

    Task<IReadOnlyList<VideoChoice>> Search(
        string query, string language, CancellationToken cancellation);
    Task<VideoInformation> Describe(
        VideoChoice choice, VideoRequest request, CancellationToken cancellation);
}

internal interface IConfigurable : IProvider
{
    VideoEditor Edit(Strings text, Func<IReadOnlyDictionary<string, string>, Task> save);
}

internal sealed record ProviderOption(string ProviderId, string TextSection,
    Func<IReadOnlyDictionary<string, string>, IProvider> Create);

internal sealed record VideoRequest(Release Release, string Language, SavedRecord Saved);
```

Library owns automatic identification. It searches the provider for the
release's title, keeps choices of the release's kind whose title or original
title and year agree, and describes the one remaining choice. The description
must agree with that choice, because a search result can lack its year or carry
a looser title. A provider whose `Kinds` lack the release's kind is not asked.
The series hint comes from Library's current, accepted sibling decisions, and
Library describes it directly. No unique match ends the attempt as a miss, so
restarting does not repeat the lookup. `Describe` returns information or a
failure; it does not return null. Library describes a series choice only for a
file whose name gives a season, and refuses any other as `episode_required`.

`VideoKind` names a movie, a series or an episode; Library stores the existing
words for them. `VideoInformation` carries facts: genres, cast and keywords are
lists, and an episode carries its series title and each episode's number, name
and synopsis. Library composes the stored title, such as
`Show · S01E01 Pilot`, and the joined text its search and filters read.

`VideoSchedule` is `Background`, `Selection(Delay)` or `Manual`, so a zero delay
never stands for a second behavior. The provider ID persists selection, such as
`public`; `CatalogId` qualifies information, such as `public/imdb`. Changing
Public's website keeps the selected provider but changes its catalogue. These
IDs are implementation-owned stable strings, because removing a provider must
not require changing central enum ordinals.

Provider names and consent use the existing Strings owner and their text
section. Settings binds to the provider options; details reads the schedule.
MainWindow hosts the editor of an `IConfigurable` provider through the existing
Interact/ShowEditor owners and imports no provider implementation. A provider
without settings implements no editor.

Library saves each provider's settings under its provider ID and creates the
provider from them. The settings cross the seam as text, because that is their
stored form; only the provider reads them, through its typed settings. Public
owns those typed settings, its editor, browser access and website
implementations inside `Library/Public/`. Its editor submits new settings;
Library cancels old work, serializes the change, saves the settings and replaces
the provider. A failed save leaves the previous provider intact. An editor left
open while the person chose another provider saves nothing. TMDB reuses saved
raw records through the request's `Saved` lookup, which returns none when
refreshing.

VideoLookup has one guarded execution/commit path for background work, delayed
lookup, Fetch, Refresh and explicit identification. It captures the provider,
catalogue, language and current entry evidence, serializes requests, reuses
matching normalized information unless refreshing, and commits only through
Library's current-membership and latest-decision guards. Provider and settings
changes cancel that context. Visibility and dwell remain with Library Browser;
providers do not subscribe to the window. Expected provider failures carry a
localized message reference. A failure of one video, such as an unreadable page
or record, uses `VideoException`; it ends an automatic lookup as a miss and an
explicit one with its error. A provider that cannot serve any request now uses
`ProviderException` with an optional retry time. So one bad page cannot suspend
unrelated enrichment. Cancellation stays OperationCanceledException; storage
errors stay with the existing failure owner.

A miss permits background work on the next eligible file. A provider failure
with a retry time suspends background work until that time; without one, it
suspends automatic work until explicit Retry or a relevant configuration change.
This prevents a blocked provider receiving the same request for every file.
An on-demand failure ends the attempt without scheduling another request. A
later explicit request before a retry deadline reports the remaining wait
without contacting the provider. Library owns these decisions and cancellable
waits; adapters own no retry loop.

VideoLookup cancels and drains background and interactive requests before the
application disposes storage or shared HTTP access. Library Browser cancels its
dwell and selection requests. Each adapter observes the supplied cancellation;
Public's browser reader finishes its per-call process, stream and temporary
profile cleanup before completing. The current adapters need no separate
provider lifetime interface, because resources are shared or scoped to a call.

Normalized videos, identification associations and attempts carry catalogue
identity. Raw records use catalogue, record key and language; providers interpret
their content, while the shared store preserves their references atomically with
the existing VideoInformation response. Library supplies the language: it saves
information from a provider that is not `IsLocalized`, such as a website page,
as valid for every language. Cached normalized facts remain readable
after a provider is removed. Episode information records its reusable series
choice explicitly, so the live shared Series query does not decode TMDB's
`tv/...` identifier. That query retains its contribution/directory/evidence and
conflict checks and restricts the hint to the captured catalogue.

The existing feature construction is the only registration location. Adding an
independent provider adds its folder, text section and one `ProviderOption`;
Public owns the implementations of its two websites; adding a website there
changes only that provider's implementation and choices.
Removing Public deletes its folder, construction entry, resources and dedicated
checks. Shared lookup, storage, settings, details and dialog code require no edits.
When a saved provider is absent, enrichment is durably disabled with no selected
provider; silently falling back would change the person's disclosed network
behavior.

Migration replaces concrete provider enums/branches and the three shared-owner
partials under Public. No release has written `library.db`, so it has one
current schema and no SQL upgrade steps. Preserve current decisions, cached facts, failed-refresh retention,
stale-result rejection and cancellation through this change. Runtime loaders,
new projects, pluggable databases and provider-specific schedulers add no required
behavior.

## Decisions that keep one implementation

- **SQLite performs local matching.** Text, filters and counts use one query
  owner, not SQL plus LINQ predicates or a second custom in-memory index. Match
  normalized literal substrings with parameters; SQL wildcards must not change
  punctuation semantics. Share normalized title/cast/synopsis text by video
  identity. Ordinary indexes do not promise fast arbitrary substring matching.
- **Source membership stays derived.** Temporary tables contain current
  contributions and entries; durable tables contain enrichment and decisions.
  Keep unfinished contributions, including magnets with no entries, so cleanup
  cannot erase early identification. No table discovered in SQLite creates a
  torrent or makes a file current.
- **One effective decision serves rows, details and search.** Apply the
  [identification precedence](library.md#identification-and-early-enrichment)
  in one SQLite transaction, including shared locations and newly joined
  contributors. Resolve it once, not separately in the query, card and provider
  completion. Discard provider results with no current reference.
- **Shared locations keep stable row identity.** Use established Windows path
  comparison semantics without disk probes or hashing. Source identity follows
  known moves; removing the first contributor must not replace a surviving row.
  Reconcile selection explicitly when locations merge or split.
- **One acceptance path guards writes.** Recheck contribution, source entry,
  filename evidence and latest decision when applying worker results. Ordered
  database work places withdrawal after already accepted writes and rejects
  later stale results. Pending edits are not reported as saved before commit.
  Apply source updates, decision changes and query reads on the same serialized
  database work path. HTTP and property workers return captured facts to it;
  they never mutate live entries. Only presentation rows cross to the dispatcher.
- **One coherent read serves one query.** Return compact rows and facet counts
  from the same database version. Keep at most one executing and one latest
  pending search; cancel or discard obsolete results. Detail reads belong to
  current selection. No engine call occurs while typing or selecting details.
- **Database failure is honest.** Failed durable edits remain unsaved. If the
  shared database cannot open or serve queries, pause feature work and report
  the failure through the existing error surface. Keep any last view
  non-actionable. Retry uses the same open and source-reconciliation path;
  never substitute an empty collection, an alternate database or a destructive
  reset. Torrent operations remain independent of the database.
- **One unshipped schema.** Create the current version 1 schema in one step;
  refuse higher versions. There is no released feature database requiring
  historical migrations or an import. C# is the sole database owner.

## Finish the delivery

- Resolve #265 at the existing workspace feedback owner: retain shared source
  and database failures until recovery, offer Retry there, and remove the
  Library table's duplicate failure/loading bar. Supplier-specific lasting
  problems remain in Settings; the shared database condition appears once.

- Keep native tray command names aligned with the interface's Pause all and
  Resume all. Format rates and counts using Windows regional settings, and
  render stable notice codes through the engine catalogue while preserving
  diagnostic details. Localize actionable startup and command-line errors at
  their existing native surface. Keep Windows-owned dialog buttons in the
  platform's language; this does not require a universal native dialog framework.
- Resolve the accepted #249 UI defects at their existing owners: expose
  feature settings through the existing global search targets; show Library
  facts independently of torrent filters; preserve focused controls during
  settings saves; and cancel unfinished tracker input with composition protection.
  Keep the prototype's Library search without suggestions, file activation,
  page switching and editor layouts. Correct table text readability without
  redesigning columns. Do not add a shared recovery bar to existing dialogs.
  Source review covers these paths; builds and execution remain with the owner.

- Take every displayed shortcut key and numeric unit from the catalogue, and name
  KiB/s beside both torrent speed limits. Keep Settings as the owner of regional
  whole-number and decimal parsing, with rate conversion using the decimal rule;
  ports share whole-number grammar while durations retain fractional input.
  Describe the proxy and adapter as product-wide routes in supplier disclosure
  and connection-test help. Preserve ranges, unlimited values and no direct fallback.

Complete the confirmed shared-owner corrections in the same batch:

- Use namespace-scoped names for Store, Entry, Query, Matches, Detail, Facet and
  Origin, with OriginId identifying a contribution. Rename the provider interface
  to IProvider while retaining IConfigurable and VideoEditor, because configurable
  providers own an editor the host can remove. Preserve LibraryTable, LibraryRow
  and concept-qualified keys; update owner paths and reflection consumers together.
- Apply the product [network route](architecture.md#network-route) through one
  shared HTTP lifetime configured from confirmed settings before update or feature
  requests. Apply acknowledged proxy and adapter changes at the completed settings
  notification, and defer snapshot callbacks until all settings are confirmed.
  Keep update-check cancellation, daily caching and quiet failures.
  Gate external browser reads through that owner before launch and link their
  cancellation to its route lifetime; an unsupported route sends no request.

- Keep keyboard focus on the active table when Filters closes, and update Library
  facet controls in place so count refreshes preserve focus and open choices.
  Preserve Library's separate facet dimensions. Match its empty-state typography
  and spacing to the torrent table while retaining its recovery actions. Shared
  failures use the workspace feedback without shrinking the rows.
- Keep website-delay drafts as typed text, including invalid input, using the
  existing NumberBox editor pattern. Give identification search the native query
  icon and initial text focus through the shared Dialog owner. Add stable
  automation IDs to actionable controls in these paths, not decorative content.
- Share General and Library's field-column geometry through `Controls.FieldColumns`:
  two content slots, fixed 3:2 columns with a 48-pixel gap, stacked below 760 pixels.
  Keep content, headings and actions with their current owners. Trace initial
  narrow layout, resizing and Library section replacement in source. Batched
  compilation is authorized; cancelled interaction programmes stay cancelled.
- Keep TextBox lookup at the existing `TextEditor` helper. Add's editable
  destination uses its exact `EditableText` name; existing callers still match
  any TextBox, including the supplied root. Password reveal lookup stays separate.
  This removes Add's duplicate traversal without introducing a visual-tree framework.
- Compose feature schemas, source cleanup and cache-invalidation SQL in the
  existing `MainViewModel/Features.cs` wiring. The database executes supplied SQL
  through its one connection and transaction path without importing features.
- Keep `FileFacts` with Library, its only consumer. Recover interrupted video
  attempts at the start of `VideoLookup.Initialize`, before creating providers
  or allowing requests, so a restart does not leave unfinished attempts stuck.
- Report shared source failures through the app error surface. Both features
  follow source readiness; Library keeps its own property/provider failures and
  shows unavailable while the shared source cannot serve either feature.
  Report once per source failure episode and clear suppression only after
  successful readiness, so ordinary snapshot retries do not repeat the same
  announcement. Keep retries and cancellation unchanged; use the existing source
  gate to suppress failures from obsolete or disposed work.
- Keep concrete construction, provider registration and shared SQL composition
  in `Features.cs`. Browser owns Library initialization, its worker events,
  failure pause, retry/resume and worker drain, so these policies no longer
  cross into the window. The app forwards source readiness and requests fresh
  snapshots; it disposes shared resources only after Library has drained.
  Capture builds gate feature startup on the model before requesting snapshots;
  ordinary builds carry no capture state in this path.
- FileSources exposes collected file rows and pending origins through temporary
  views. Library and Subtitles consume those views rather than interpreting
  `contributions.collected`, so source currency has one owner. Preserve cleanup
  retention while synchronization is pending and the existing late-result
  guards; this changes no durable schema and needs no migration.
  Keep contribution scheduling, readiness and checked timestamps at FileSources;
  TorrentSource coordinates engine reads without its own database SQL. Retain
  its range-checked verification projection: the Pieces display model also
  parses availability/downloading and classifies every piece, which this source
  does not need. Feature cleanup consumes the shared currency views for each
  feature's own tables. The current schema/hook composition is complete and
  ordered; no checked-registration framework is required without an omission.
- Keep shared filename interpretation, title and normalized text on ingestion,
  and correct the master plan's owner table. Moving it adds query work without
  improving behavior. Keep `library.db` so existing saved records remain in use.
  `FileName.Kind` owns the extension table used by ingestion and the file tree;
  retain the formats either table already recognizes, including NFO, Markdown,
  subtitles and ISO files. `FileName.Glyph` maps each kind once for both file
  presentations; Library retains its identified-episode TV icon. Use the engine's
  final filename unchanged, because stripping a textual `.!tt` suffix also
  changes genuine filenames.
- Use the existing accent- and case-insensitive normalization for title-bar
  suggestions on both pages, so the same query finds the same spelling. Keep
  the subtitle category's summary and search terms in the existing Settings
  suggestion builder rather than repeating its navigation command.
- Library Browser owns identification action text and glyph. Table/window
  menus and Inspector actions consume the same conditional Identify/Edit label;
  responsive placement stays with the Inspector.
- Share the application assembly version and HTTP User-Agent at `App`, and the
  provider date-prefix year parsing at `VideoInformation`. Torrent's display
  mapping does not duplicate the source's settled predicate; TMDB's object-array
  names and Public's recursive JSON-LD names have different contracts and stay
  with their providers. `ProviderReply` parses the HTTP Retry-After fact once;
  Library and Subtitles retain their retry defaults and quota/reset decisions.
  Keep the full retry deadline and bound each cancellable worker wait, because
  a valid long provider delay can exceed the platform timer's supported duration.

Trace startup/recovery, retry, withdrawal, replacement/moves, cache invalidation,
source failure and teardown in the final source diff. These traces establish
source reasoning; validation evidence is recorded in Current status. Repeated
intermediate builds, benchmarks and interaction programmes remain cancelled.

The current batch contains source changes for the accepted ownership and
presentation findings: compact absent-information details, shared Inspector
layout, source invalidation, one subtitle suggestion, refreshed menu language,
duplicate-error handling and unchanged-selection detail reads. It also routes
Clear through the existing page command, shares its tooltips/count formatter,
localizes the typed database downgrade failure and subtitle timestamps, handles
malformed TMDB responses and route failures, exposes source-currency views and
applies the accepted local naming corrections in Schedule, PeriodEditor and
private TableView code. Current status distinguishes compilation evidence from
the earlier pixel review. Other proposed architectural changes are not accepted
by inclusion in an AI report.

The lifecycle and source-failure changes passed colleague review of the settled
batch for correctness and ownership. Reuse valid earlier evidence; do not
reinstate benchmarks, capture journeys, comprehensive tests or a new validation
framework for this delivery.

Final source review found and closed two readiness races:
provider-change completion must read and resume its current connection state
under VideoLookup's existing gate; the app must serialize source-readiness
acceptance and both features' activation with immediate disconnect. Browser's
retry and initialization continuations use its current readiness rather than
replaying captured flags. These corrections keep worker cancellation immediate,
notifications on the dispatcher and every await outside the app's lifetime gate.

The shared-route source review also closed acknowledged-settings publication,
update-cache continuation and shutdown gaps. The browser gate and update check
now consume that same confirmed route. These are colleague-reviewed source
results; Current status records the relevant compilation and offline checks.

Implementation is delivered when the required operations are implemented and
no concrete correctness finding remains open. Benchmark closure, exhaustive
coverage and supplier distribution paperwork are not implementation gates.
Keep release permissions and privacy publication in the
[release assessment](subtitles-release.md).
