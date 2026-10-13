# Library implementation plan

**Implementation done**, including C — Sections and the title-bar changes.
[Current status](#current-status) records the evidence and remaining verification
limits. Release permissions remain in the
[release assessment](subtitles-release.md).

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
[Library prototype](../app/library-prototype.html). Those owners define the
behavior; this plan does not repeat their requirements.

## Current status

On 2026-10-10 the owner approved **C — Sections**, the single Library prototype
and the app-icon page switch. Their native implementation is done. The
[acceptance checklist](library.md#prototype-implementation-review) records the
current target and outstanding evidence. The earlier B build and captures below
do not validate these new changes. Later authorized builds and app checks are
recorded in the [Settings implementation record](settings-implementation.md#authorized-compilation-and-checks--13-october-2026).
The reported filter-opening jump remains a runtime follow-up, not unfinished
implementation of the selected composition.

The earlier delivery selected **B · Inspector** from the details prototype: one shared
identity header, descriptive fields on the left and media facts on the right,
with separate numeric IMDb and TMDB table columns. The throwaway comparison is
saved on `codex/library-inspector-prototype` at
`b7561bb2a90447eada3fe16d977febfdd200f4b5`; the native implementation follows the
[selected composition](library.md#details-card). The new slice passed colleague
source and adversarial review, the Debug diagnostic build (zero warnings or
errors, 262.18 seconds), and production database checks (17.62 seconds), including
version 1 data retention, rating persistence and structured episode identity.
The first check attempt exposed a script reflection-access mistake; correcting
that script required no product rebuild. The off-screen review completed its
existing 23 scenes in 67.14 seconds, with no provider requests or desktop input.
Evidence is `Capture-library-files-d780676d-fb65-4672-b9c3-7c507ff49de6` under
`artifacts/evidence/`; populated video and music pixels passed independent
colleague review. Their scores are explicitly labelled local fixtures, not live
provider evidence. The ordinary Debug build also passed with zero warnings or
errors in 182.56 seconds. Each app build used the expected two C# passes and
four WinUI XAML calls; Lucide and TableView compilation stayed up to date, and
neither the engine nor native dependencies were rebuilt. Generated-output
checks were empty after the runs. Logs are
`artifacts/library-inspector-capture-build.log`,
`artifacts/library-inspector-source-checks.log` and
`artifacts/library-inspector-capture.log`; the ordinary build log is
`artifacts/library-inspector-debug-build.log`.

The subsequent WinUI code review found that transient fetch, association and
torrent notifications replaced Library's details controls, losing text selection
even when their content had not changed. Fetch feedback and command availability
now update in place; unchanged detail results do not announce new content, and
the inactive torrent bindings stop tracking until the person returns to Torrents.
Selection, changed metadata, provider configuration and language still refresh
the card. Independent colleague source review closed the findings, including
the retained per-torrent buttons' disconnect and reconnect states. Existing
layout and storage evidence above remains applicable; these notification fixes
have not had a new native interaction run. Their ordinary Debug x64 window
build passed with zero warnings or errors in 152.75 seconds
(`artifacts/library-inspector-review-debug-build.log`). The two app C# passes
ran, Lucide and TableView compilation stayed up to date, and no engine or
dependency build ran. The generated-output check was empty.

Info's Year and individual Genres now apply the existing table filters and open
the pane. Files exposes these additional facets while active, including with
zero matching rows, so they can be changed or cleared. Colleague source review
closed with no remaining finding. The Debug x64 window build passed with zero
warnings or errors in 130.89 seconds; the existing storage checks, including
the new Files facet regression case, passed in 18.32 seconds. Logs are
`artifacts/library-info-filters-debug-build.log` and
`artifacts/library-info-filters-source-checks.log`. Generated-output checks were
empty. No native interaction or provider request ran for this change. The
following evidence describes the earlier integrated delivery.

Library is implemented in C#: source synchronization, shared SQLite, complete
local search, filters, file details and opening, identification, optional video
providers, and native page/dialog integration. Subtitles shares the source and
database owners, not Library enablement.

The earlier integrated builds, focused production storage/file checks, supplier
checks and native reviews in `artifacts/evidence/` remain valid for the code
they exercised. These are existing results, not instructions to repeat them.
The settled ownership, routing, naming and presentation corrections passed
colleague source review with no open finding. Debug x64 engine and window builds
passed with zero warnings and zero errors. The successful engine compile took
58.26 seconds; the latest ordinary window build took 255.02 seconds. Its two
C# compiler passes and four WinUI XAML task calls account for the compilation
work; Lucide and TableView compilation were skipped as up to date. No native
dependency or engine was rebuilt for the visual corrections. Compiler failures were
corrected before completion: the tray's locale pointer type, stale generated
Proxy bindings, and application-template font lookup. Only affected compilation
was repeated; dependencies and Release were not rebuilt.

`app/tests/SourceChecks.ps1` passed against the preceding settled assembly in 3.23 seconds,
covering source membership, identification, database recovery and subtitle file
ownership. `app/tests/ProviderChecks.ps1` passed in 0.80 seconds: HTTP CONNECT,
SOCKS4a and SOCKS5 reached local proxy listeners, and an unavailable adapter
failed without direct fallback. The required generated-output checks were empty.
Logs are `artifacts/library-subtitles-final-engine-debug-build.log`,
`artifacts/library-subtitles-final-debug-build.log`,
`artifacts/library-subtitles-final-source-checks.log` and
`artifacts/library-subtitles-final-provider-checks.log`.

A fresh focused off-screen review completed all 23 scenes in 41.37 seconds:
`Capture-library-files-2ecd4e52-7d44-463d-aa22-986a41557e86` under
`artifacts/evidence/`. No live provider requests or desktop input ran. The first
attempt caught a startup failure missed by compilation: the canonical Lucide
font registration accessed application resources inside the constructor. The
registration now runs first in `OnLaunched`, before views are created;
colleague review and the fresh runs confirm the correction. The final diagnostic
rebuild passed without warnings or errors in 88.32 seconds, with no dependency
or engine rebuild.

Pixel comparison with the approved HTML references confirms readable sections,
compact off/unidentified details, aligned File fields and unclipped English and
Spanish supplier buttons. The fresh images verify Library's restored leading
type/failure glyph and subtitle language values aligned beneath their input.
The row also notifies these bindings after an Open failure or successful retry.
Two added scenes use the existing local fixture helper to verify identified
movie statistics, cast and synopsis, and tagged music statistics and fields.
They establish populated layout, not supplier or Windows metadata acquisition.
Independent colleague pixel review has no remaining actionable finding.
Narrow stacked details, High Contrast, native window chrome and desktop acrylic
are outside this evidence. No timing diagnostic ran.
The approved prototype composition and interactions remain the authority;
AI reports do not authorize redesign.

The cancelled 10,000-file benchmark did not pass: worst warm p95 was 421.55 ms
for Videos, 376.81 ms for Music and 612.87 ms for Files. First navigation was a
single 1,083 ms observation, not a percentile. Memory observations were not a
controlled before-feature comparison. Cancellation does not make these targets
passed, and this delivery makes no “instant at 10,000 files” claim.

The owner removed the IMDb and Rotten Tomatoes providers that read web pages
through an installed browser, because a browser costs too much memory and time
for one score; do not reopen browser experiments. IMDb now reads
[IMDb data](imdb-datasets.md) downloaded to this computer.

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
| C# database owner | One connection, authoritative schema, initialization and versioning, serialized transactions and shared consistency rules. Library and subtitles keep domain queries with their behavior. See the [database contract](database.md). |
| Filename interpretation | One file-kind classification and filename interpretation used by both features, without provider or scheduling decisions. |
| Video lookup | Selected-provider requests, cancellation and background eligibility; Library accepts identification evidence and saves video information. TMDB, Wikidata, OMDb and MDBList each own their API decoding; Imdb owns the download, import and local search of IMDb data. |
| File facts reader | One-time Windows property reads for dates, audio tags, media duration, bitrate and video dimensions; Library accepts and saves the returned facts. |
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
the same Library workflow. The following seam replaces concrete provider
branches. The source implements this seam; existing evidence is summarized in
[Current status](#current-status).
This is compile-time composition in the existing app, because no runtime plugin
installation or discovery is required.

A provider owns only what differs between providers: how it builds its
requests and reads their answers. It knows neither current torrent membership
nor whether an answer may be saved, and it receives no `Store`, request
protection or network route:

```csharp
internal interface IProvider
{
    string Prepare(string query) => query.Trim();
    Task<VideoSearch> Search(VideoQuery query, CancellationToken cancellation);
    Task<VideoInformation> Describe(
        VideoChoice choice, VideoRequest request, CancellationToken cancellation);
}

internal sealed record ProviderOption(string ProviderId, string Name,
    Uri Privacy, Uri Terms, Func<string, IProvider> Create);

internal sealed record VideoQuery(string Text, string Language, ProviderGet Get);
internal sealed record VideoRequest(Release Release, string Language, ReadRecord Read, ProviderGet Get);
```

Each provider folder declares its own `ProviderOption`: the provider ID, which
also names its catalogue and its text section, the name, the policy links,
`Access`, `BuiltInKey`, `Kinds`, `IsLocalized` and, for a provider that
answers from downloaded data, `Dataset`. Library owns what every provider
shares. It chooses the key, the person's own or else the built-in one, and
passes it to `Create`. It decides whether a provider is available. It sends a
request through `ProviderGet`, which applies the saved network route and the
request protection of the key and of the operation Library runs; a 404 for a
detail means the video is missing. A provider reports a failure as a
`VideoFailure`, and Library writes its text with the provider's name.
`VideoInformation` carries `Related`, the other choices its page listed, and
Library keeps them.

Library owns automatic identification, and SQLite answers before the provider.
It first reads the saved choices of the provider's catalogue whose normalized
title or original title equals the release's title, through an index on each.
One saved choice of the release's kind and year is described without a search.
Otherwise it searches the provider for the
release's title, keeps choices of the release's kind whose title or original
title and year agree, and describes the one remaining choice. When the search
keeps none, one saved choice still answers before the lookup reports no match.
A saved choice decides alone only with the year, because without it the
provider's search could list a second movie of the same title that no page has
shown yet. An automatic search runs once a session: the files of one movie
reuse its result. Fetch metadata's dialog always searches again, because the
person repeats a search after an empty or failed answer. It lists the saved
choices of the typed title with the search results, and only those when the
provider fails. The description
must agree with that choice, because a search result can lack its year or carry
a looser title. A provider whose `Kinds` lack the release's kind is not asked.
The series hint comes from Library's current, accepted sibling decisions, and
Library describes it directly. No unique match ends the attempt as a miss, so
restarting does not repeat the lookup. `Describe` returns information or a
failure; it does not return null. Library describes a series choice only for a
file whose name gives a season, and refuses any other as `EpisodeRequired`.

`VideoKind` names a movie, a series or an episode; Library stores the existing
words for them. `VideoInformation` carries facts: genres, cast and keywords are
lists, and an episode carries its series title and each episode's number, name
and synopsis. Library composes the stored title, such as
`Show · S01E01 Pilot`, and the joined text its search and filters read.

The provider ID persists selection and qualifies information, such as `imdb`
or `tmdb`. It is an implementation-owned stable string, because removing a
provider must not require changing central enum ordinals. A saved provider ID
that no option matches turns downloading off rather than refusing to start.

The provider's text section holds its disclosure. Settings binds to the
provider options. MainWindow imports no provider implementation.

Providers have no settings of their own. A provider that answers from
downloaded data, such as IMDb, declares it on its `ProviderOption`, and
Settings offers the download, update and deletion.

VideoLookup has one guarded execution/commit path for background work, the
batch, Fetch, Refresh and explicit identification. It captures the provider,
catalogue, language and current entry evidence, serializes requests, reuses
matching normalized information unless refreshing, and commits only through
Library's current-membership and latest-decision guards. A provider change,
Disable, a connection change, a language change and a network route change
cancel that context; a route change also clears failures without a retry time. VideoLookup also owns Fetch metadata's batch: one
queue that identifies its files in order on a worker thread. The background
sweep finishes the file it is reading and then waits until the batch ends, so
no page read is wasted and automatic lookups do not start and cancel between
batch files. A batch file follows the automatic policy, except that it retries
an earlier miss: its own failure is a miss and its match is automatic. The
Library view only adds files, stops the batch and shows its progress.
VideoLookup reports a stored match or miss separately from state changes such
as the provider, its failure or the batch. Saved data can answer many files a
second, so the view refreshes its rows at once and then at most once a second
while such results arrive. Visibility and dwell remain with Library Browser;
providers do not subscribe to the window. Expected provider failures carry a
`VideoFailure`. A failure of one video, such as an unreadable page or record,
uses `VideoException`; it ends an automatic or batch lookup as a
miss and an explicit one with its error. A provider that cannot serve any
request now uses
`ProviderException` with an optional retry time. So one bad page cannot suspend
unrelated enrichment. Cancellation stays OperationCanceledException; storage
errors stay with the existing failure owner.

The background sweep takes every video file that `Eligible` leaves: no saved
decision, no attempt in this session and no `video_misses` row for the provider
under the file's current name. Opening the database returns earlier failures,
search-limit stops and unusable pages to the sweep; only the provider's own
no-match answer is permanent. Automatic and batch
lookups search the provider only while `video_lookups` counts fewer answered
searches of that text at that provider than the
[shared limit](architecture.md#third-party-provider-access), which
`library_settings` keeps and subtitles read through `VideoLookup`. The sweep starts
only after the delay that `ObserveFiles` creates for it and for the subtitle
worker. `FileSources` saves the torrents it has seen finished in `known_torrents` and lists
this session's new ones in the session table `new_origins`; a database without
history takes its first list as known. Only subtitles read them: `Reconcile` creates
automatic jobs only for new torrents and advances only their earlier misses,
and `Load` cancels unfinished automatic jobs for previously finished torrents
when the window opens, retaining their history. New unfinished torrents remain
eligible; the worker runs pending jobs once the switch and delay allow it.

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
dwell and selection requests. Each adapter observes the supplied cancellation.
The current adapters need no separate provider lifetime interface, because
resources are shared or scoped to a call.

Normalized videos, identification associations and attempts carry catalogue
identity. Raw records use catalogue, record key and language; providers interpret
their content. Library keeps a raw record as soon as the provider returns it,
also when the lookup rejects the video, and saves its references atomically with
the accepted VideoInformation response. **Owner ruling, 2026-10-11: keep
everything a provider returns.** Raw records and saved choices stay after
their files leave the Library, because reading a page again costs seconds and
risks the website refusing the person's address. Library saves every search
result, every related choice and every described video as a saved choice, with
title, original title, year and kind; a later listing of the same choice keeps
a known year that the new one lacks. Library supplies the language: it saves
information from a provider that is not `IsLocalized`, such as OMDb, as valid
for every language. Cached normalized facts remain readable
after a provider is removed. Episode information records its reusable series
choice explicitly, so the live shared Series query does not decode TMDB's
`tv/...` identifier. That query retains its contribution/directory/evidence and
conflict checks and restricts the hint to the captured catalogue.

The existing feature construction is the only registration location. Adding an
independent provider adds its folder, its text section and its option to that
list. Removing an online provider deletes its folder, list entry, resources and
dedicated checks; shared lookup, storage, settings, details and dialog code
require no edits. IMDb is the exception: its data also gives every provider's
movies their IMDb rating, so `Store` and the Library view know `Imdb.Dataset`,
and removing IMDb changes them too.
When a saved provider is absent, enrichment is durably disabled with no selected
provider; silently falling back would change the person's disclosed network
behavior.

The provider refactoring replaces concrete provider enums and branches. Ratings are part of the first-version schema.
Preserve current decisions, cached facts, failed-refresh retention, stale-result
rejection and cancellation through this change. Runtime loaders,
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
- **One complete first-version schema.** The database creates every permanent
  table, view and index in one transaction at version 4. Opening a version 2
  file adds `video_misses` and `provider_keys`, and a version 3 file adds
  `provider_keys`; both keep their data. There are no other upgrades for
  earlier development databases. The complete schema lives in
  `Services/Database/Schema.cs`; the database owns future versioned migrations.
  `source_state.initialized` records whether the first confirmed torrent list
  has been reconciled, including an empty list. That first list establishes
  `known_torrents` without searching the person's whole collection. Reopening
  keeps saved facts and decisions and rebuilds only temporary projections. A file
  with another version, or one that SQLite reports as damaged, is incompatible:
  feature work pauses, and the failure surface offers Delete database instead
  of Retry, because a retry cannot repair either file. C# is the sole database
  owner.

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
  to IProvider. Preserve LibraryTable, LibraryRow
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
- Give identification search the native query
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
- Keep the authoritative schema and shared consistency SQL with Database.
  `MainViewModel/Features.cs` constructs owners without supplying SQL fragments.
- Keep `FileFacts` with Library, its only consumer. Video and file-read attempts
  are temporary session work; opening the database starts an empty work set.
  A provider's no-match answer is the exception and stays in `video_misses`.
- Report shared source failures through the app error surface. Both features
  follow source readiness; Library keeps its own property/provider failures and
  shows unavailable while the shared source cannot serve either feature.
  Report once per source failure episode and clear suppression only after
  successful readiness, so ordinary snapshot retries do not repeat the same
  announcement. Keep retries and cancellation unchanged; use the existing source
  gate to suppress failures from obsolete or disposed work.
- Keep concrete construction and provider registration
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
  does not need. Database cleanup consumes the shared currency views for both
  features; no registration framework is required.
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
