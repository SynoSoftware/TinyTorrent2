# Library implementation plan

## First directive: use common sense

Implement the current [Library spec](library.md) and the page rulings in the
[interface contract](interface.md), not an earlier version of either. The
[Library prototype](../app/library-prototype.html) shows the presentation; its
browser code and sample data are not production design. Apply
[Usability comes first](architecture.md#usability-comes-first) before each
stage: inspect the current owner, reuse working behavior, and remove work from
this plan when the agreed outcome stays intact. Report a conflict with the spec
or an owner ruling; do not resolve it by changing the feature.

**Owner ruling: the goal is easy-to-read, low-bloat code, not preservation of
the current structure.** Refactor when it materially simplifies the code,
removes duplication, clarifies ownership or makes responsibilities easier to
understand. Do not add an abstraction or split a function where that only adds
indirection, ceremony or navigation cost. Apply this judgment to work underway
and deferred decisions alike; calling a necessary correction a redesign is not
a reason to preserve bad code. Existing code, committed or not, is evidence to
inspect, not a structure to protect. Keep the focus on Library and the shared
owners it directly needs; do not restructure unrelated code.

Each responsibility has one owner and each operation one implementation. When
a refactor replaces an implementation, move its direct callers and remove the
replaced path in the same stage, so the feature cannot grow two authorities.
Split a module when that makes its responsibility easier to understand; keep
work together when a split only makes the reader follow forwarding calls.

Apply [naming and structure](naming.md) and the [product vocabulary](../CONTEXT.md)
before settling every new or changed declaration and file location. Review
names at their use sites after ownership changes; an old qualifier may no longer
carry meaning. Names below that identify current source are navigation aids,
not exemptions from that review. Rename affected callers together without
aliases or compatibility wrappers for unshipped internal code.

Prepared 2026-10-08 against the working tree. The proposed types, fields and
messages are implementation decisions, not claims that code exists or that
checks have passed.

Success means every stored fact, state transition and user operation has one
owner, and the delivery stages cover the spec through those owners. The checks
below establish implementation evidence; this document alone does not.

## Ownership change and current source

The earlier native `Library`, `Videos`, `Clues`, `Tmdb`, `LibrarySource.cpp` and
`Enrichment.cpp` attempt is no longer in the current working tree. Its removal
is source evidence only, not a build or runtime result. The owner now places
Library, TMDB and subtitle downloading in C#. Both features run while the product window
is open and use one C# SQLite owner. The [subtitle plan](subtitles-implementation.md)
owns subtitle behavior. C# creates, migrates, populates and queries SQLite and
downloads TMDB information and subtitles. No Library or subtitle feature code,
provider/search/job API, native helper or hidden C# process is part of the engine.

The engine supplies torrent and file facts through its existing pipe and owns
torrent commands and payload operations. It holds no Library rows, provider
cache, SQLite handle or enrichment scheduler. Its installed SQLite dependency
in `3rdParty/` stays untouched; remove project references when native callers
are removed.

The page prototype is presentation evidence, not production behavior. Existing
capture modes named Library exercise torrent-table fixtures. No successful
implementation or performance check is implied by this plan.

## Owners and reuse

These are responsibilities in the existing C# app, not one project, interface
or forwarding class per row. Place code by [app role](naming.md#app-folders);
the data-source seam explicitly required by the spec is the one future seam.

| Owner | Responsibility |
| --- | --- |
| C# torrent adapter | Seed and synchronize accepted membership and file facts in SQLite through PipeClient, including unfinished and unwanted files. It decides neither Library eligibility nor subtitle eligibility and exposes no separate file catalogue. |
| Library | Query finished entries from SQLite; own identification decisions, enrichment acceptance and SQL search/filter/count queries. |
| C# database owner | One connection, migration sequence and serialized transaction path. Library and subtitles keep their domain SQL with their behavior; the shared owner does not accumulate all feature queries or rules. |
| Filename interpretation | One file-kind classification and filename interpretation used by both features, without provider or scheduling decisions. |
| TMDB lookup | Provider requests and response decoding; Library accepts the returned identification evidence and video information. |
| File facts reader | Windows property reads for dates and audio tags; Library accepts and saves the returned facts. |
| Library presentation owner under MainViewModel | Query/configuration input, stable row objects, selection, navigation and opening outcomes. TableView performs the only sort. |
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
- **Migration preserves facts.** Inspect whether a retained native database
  exists before changing its schema. Implement an import only for an actual
  retained schema and saved user facts; an abandoned native attempt alone does
  not require a compatibility layer. C# becomes the database's sole owner;
  never open it concurrently with an old engine or discard corrections as cache.
  Advance schema versions at that owner. Native settings cease owning enrichment consent;
  transfer an existing saved choice only when its disclosure still describes
  the new route, otherwise require the feature's normal enablement.

## Delivery order

Each slice moves its callers and removes the superseded native implementation
together. Do not add C# beside a still-active native fallback. Re-read shared
files before editing because the working tree contains other work. Consume the
existing torrent protocol; this plan adds no C++ source catalogue, feature
invalidation, byte-availability API or subtitle-aware file operation. Calling a
feature helper generic does not move it into engine scope.

### 1. Move Library ownership to C# and prove source synchronization

**Result:** the engine supplies facts only; C# owns the database and reconciled
Library. Closing the window leaves no Library work in the engine.

**First gate: prove the existing read path before building the full feature.**
The existing torrent Files reply reads one torrent at a time; PipeClient
serializes requests. Inspect the current detail-read implementation rather than
assuming its internal scheduling. `Torrent::Row` has aggregate progress and paths, but no
file-list revision. Temporary SQLite tables therefore require a cold collection
read on every new window. Benchmark that real path at both fixture sizes,
including command responsiveness; a synthetic SQLite benchmark cannot establish
startup readiness. Do not promise the 500 ms target, hide this cost in window
startup, or add C++ machinery to make the numbers pass. A failed target is a
reported design constraint before dependent UI work, not an excuse to display
stale cached files as confirmed.

- Inspect existing snapshots, file replies and PipeClient. Build the C# adapter
  from their accepted-torrent identity, paths, names and completion facts.
  All feature translation, change comparison and projection building stays in
  C#. Do not extend C++ to prepare Library rows or subtitle inputs.
- Initialize during window connection, off the dispatcher. Obtain the complete
  contribution set before cleanup; file metadata still pending is not removal.
  Apply source changes arriving during initialization before publishing results.
  Disconnect retains a non-actionable last view and pauses target work;
  reconnect reconciles it against the new engine session.
- Compare incoming source facts with the current SQLite projection and refresh
  affected file facts through existing reads. Synchronize once for both features
  and bound outstanding reads; do not fetch every file in every torrent on each
  telemetry tick. At 1,000 torrents, measure source transfer separately from
  local queries. Commands run
  between reads and partial data is never complete. Optimize scheduling and local
  processing in C#, without a new native catalogue or bulk feature endpoint.
  Use one read consumer for the shared adapter and await each requested torrent
  before submitting the next: PipeClient replaces an unsent read for the same
  consumer, so queueing every torrent under that consumer loses requests.
  Refresh on admission/reconnect, relevant summary transitions and completion
  of the window's own file commands. While incomplete torrents make progress,
  schedule bounded file refreshes through this same owner. Aggregate progress
  is a hint, not a file revision; retry unfinished final-name transitions until
  settled, and verify idle renames/priority changes with unchanged byte totals.
  Record any change the existing contract cannot observe rather than claiming
  event-driven freshness that it does not supply.
- Put Microsoft.Data.Sqlite in the existing app. C# owns database creation,
  schema, migrations, population, indexing, cleanup and every query. Its
  [async methods execute synchronously](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async),
  so use one connection on its serialized background work path. Network and
  property-system work are separate bounded operations; neither holds that connection.
- Reconcile contributions and populate temporary search tables in set-based
  transactions. One withdrawal transaction applies each feature's cleanup SQL,
  removing source associations and unreferenced facts even when the features
  are off. The source owner orders the transaction; each feature owns its rules.
  Reconcile removals missed while the window was closed through that same path.
  Publish changes only after commit; feature queries and worker-result validation
  read this SQLite state. Do not hand pipe replies to either feature or retain
  another current-file collection in the adapter.
- Port required native identification/storage behavior into its C# owners;
  implement the behavior from the current spec where the earlier code is gone.
  Verify that no native Library classes, workers, commands, settings or project
  references return. Preserve unrelated engine networking, torrent persistence
  and other working-tree changes; no C++ subtitle copy is retained.
- Keep normal torrent startup, activation and shutdown independent of the C#
  database. No database initialization or provider work occurs without a window.

**Check:** exercise the C# owner with a temporary database and deterministic
source facts. Cover unfinished contributions, shared locations, remove/re-add,
late writes, restart and removals while closed. Verify source reads with the
unfinished suffix on and off, one finished episode inside an unfinished pack,
and name/priority changes that leave aggregate bytes unchanged. Derive shared
file-read readiness in the source SQL projection under the
[spec's rule](library.md#sqlite-and-video-information); byte counts do not
establish it. Property reads and subtitle hashing query that one condition.
Use a pipe round trip only
for the source facts the C# adapter actually needs; no provider key is necessary.

### 2. Implement SQL search and file facts, then measure the costly path

**Result:** one C# implementation supplies searchable rows and counts for all
three configurations, with no extra engine search memory.

- Normalize text once on change; preserve original display values. Implement
  literal whitespace-AND fragment matching, case/accent folding, filters and
  counts in SQL. Different terms may match file fields and shared video text.
  Unknown dates/tags stay absent. Start without FTS; add an index only when
  measurements justify its disk/write cost and preserve one-character behavior.
- Run the 10,000-file fixture and a 1,000-torrent/100,000-file fixture before
  building more search machinery. Measure source reads, SQL, row allocation and
  TableView sorting separately. A fast SQL query is not an end-to-end pass.
- Classify file kind and derive fallback titles once in the C# filename owner.
  Library classification and subtitle eligibility use that vocabulary; title
  parsing failure must not turn a recognizable video into an ineligible file.
- Read dates and audio tags through the Windows property system off the
  dispatcher/database worker, selecting targets through SQLite's shared
  file-read readiness condition. Bound reads, coalesce shared locations, and save
  successful empty results. Failed reads leave facts unread and retry once on a
  confirmed location change or a later window initialization, through the same
  reader; never retry them on each summary refresh.
  A file waiting for readiness has not been attempted; never save an empty result
  merely because its torrent is still writing.
  Discard results for obsolete identity/evidence, not merely obsolete paths.
- Return facet counts using the spec's query and other-section rules, including
  selected choices with zero matches. Query other configurations' counts from
  SQLite too, using their retained filters and the shared text query.
- Materialize compact typed rows: entry identity, origins, file facts and
  applicable video/music fields. Multi-episode information remains one file.
  Derive waiting/off states from current work/settings, not persisted flags.
  Compose effective titles in one place for rows, search and details.
- Read full cast/synopsis from SQLite only for selected details; provider
  enrichment saves searchable text before selection, so actor/subject search
  finds unseen files. Reuse unchanged row
  objects; update affected rows without rebuilding the entire collection.

**Check:** short/accented/punctuation and cross-field matches, SQL facet counts,
a shared location, saved empty tag reads and a tagged audio fixture. Record
initial performance evidence; do not assert targets from the design alone.
Exercise search, counts and recorded details with inaccessible payload folders:
they must use SQLite without opening those paths or sending pipe requests.

### 3. Pages and title bar

**Result:** Torrents and Library are peer pages, as
[interface.md](interface.md) rules.

- Add `WindowPage.Library`. Add the page switcher SelectorBar after the MenuBar,
  with Ctrl+1 and Ctrl+2. Its items show only icons when the search box would
  drop below 200 pixels. Add its icon width to `UpdateMinimum` in Chrome.cs.
- The second MenuBar item shows the current page's menu: Torrent or Library.
  View shows the current page's items. Library's View has no Toolbar item.
- Back returns to the page that opened Settings or About. Show in Torrents
  also shows Back on Torrents, which returns to Library.
- Search serves the current page. On Library, it has the placeholder "Search
  Library", no suggestion list, Escape clears it, and Down or Enter moves to
  the rows. The Torrents search offers both pages.
- MainViewModel owns each page's retained state; MainWindow routes native
  controls and focus to that owner. Preserve the existing draft guards when
  changing pages or Inspector targets. Add English and Spanish text with each
  surface, rather than leaving localization until the last stage.

**Check:** review the title bar at the minimum width in English and Spanish.

### 4. The Library table

**Result:** a person can find and open a finished file.

- One table host, with a shared construction path and row renderers. Videos,
  Music and Files each have their own `ColumnLayout`, sort and filters, saved in
  `window.json` next to the torrent layout. Videos is the default, and the
  window remembers the last configuration.
- TableView's [schema is fixed after loading](../lib/TableView/docs/tableview-contract.md#5-public-control-contract).
  A union of every column would expose irrelevant choices in its column menu.
  On configuration change, save the outgoing layout, detach its table and
  create the one active TableView with the chosen configuration's columns.
  Reuse row objects and retained presentation state, restore the saved layout,
  and recover selection, focus and scroll position. No three hidden tables,
  copied page implementations or runtime schema mutation are needed.
- Each configuration exposes only its applicable columns in the column menu.
  TableView sorts one column; give Artist a composite Artist, Album, Track key
  through its existing `SortKey` comparer to implement the Music default.
  TableView still performs the only sort, and `ColumnLayout` saves Artist as
  the active column. No multi-column sorting framework is needed.
- Filters use the existing drawer, with the sections and nesting date ranges in
  [filters](library.md#filters). The status footer names the configuration, its
  count and the active filters.
- The states in [states](library.md#states). There is no loading state.
- Open, Enter, double-click and Retry invoke the same command, bound to stable
  entry identity and its current recorded path. Extend `Opening.cs` to return
  its actual outcome to the caller instead of only calling `Model.Report`;
  existing torrent callers keep the same Windows execution path. The Library
  presentation owner records Missing, Unavailable or Couldn't open only as
  supported by that outcome, with its time, and clears it after a successful
  Open. No existence probe is added.
- Open folder, Properties and Show in Torrents use the same commands and
  availability from the page menu, context menu and card. When a shared entry
  offers several torrents, Show in Torrents offers those named targets rather
  than selecting an arbitrary torrent; the File page already identifies each.
- Hold rows by entry id, so a refresh updates changed rows and keeps selection
  and scroll position.
- Disable the specified actions while disconnected without clearing the last
  collection. Reconcile engine session and current membership on reconnect;
  discard removed-target detail and opening outcomes.

**Check:** the off-screen [capture review](../app/AGENTS.md#capture-review) of
the three configurations and the empty and no-match states, at the minimum
width and at a wide width, in English and Spanish, light and dark.

### 5. The details card

**Result:** selecting a file shows its information and its file facts in the
Inspector card.

- Reuse one InspectorPane card, splitter, section selector, responsive states
  and error presentation. Its target supplies header, sections, content and
  commands. Keep torrent file/tracker drafts with the torrent Inspector model
  and Library selection details with Library's presentation owner; sharing the
  card must not turn either into a nullable mixture of both domains. Move any
  extracted card mechanics with their existing torrent caller in this stage.
- The Inspector takes its section list from its target, so a Library entry
  offers Video information or Music information, and File. Other file kinds
  open on File with the information item disabled, as the spec requires. Add
  the actions slot for Open, Edit and More, which moves Open and Edit into More
  in the Narrow state.
- The information pages use General's Strip, heading and Field grid. Add one
  wrapping value style for the synopsis.
- The File page uses the same Field grid. One window-owned shell lookup supplies
  Type of file, Opens with and the app icon to both the File page and Open's
  tooltip. Resolve association information without opening or inspecting the
  payload, and handle both desktop and packaged default apps; do not assume
  every association is an executable path. Missing association information does
  not prevent Open from asking Windows to handle the file.
- Show in Torrents switches page, selects the torrent and opens its Inspector
  on its last section, from the File page, the Library menu and the row context
  menu.
- Read only the visible selection and section through the existing context
  checks, discarding replies after a target, section, session or page change.

**Check:** in the combined capture review, inspect Torrents and Library with
the card open, including both Library sections, a non-media file, a shared file
and a failed Open. Confirm the existing torrent edits still use their original
owners after the card is shared.

### 6. Identification and video information

**Result:** a person can turn video information on, identify a file, correct it
and clear it.

- The Identify dialog calls the C# Library owner directly. Edit opens the same
  dialog, titled "Edit identification", with Save and Cancel. Clear
  identification is in More.
- Provider operations use cancellable C# asynchronous I/O, with one active
  request and bounded pending work. Closing the dialog cancels its search;
  closing the window cancels network work and preserves already committed
  decisions. No provider operation crosses the engine pipe. The database worker
  never waits for HTTP, and late replies cannot replace a newer explicit choice.
- Automatic identification, Identify, Edit and Clear use Library's single
  decision path from stage 1. Provider requests remain bounded, explicit work
  goes before pending automatic work, and disabling enrichment invalidates
  pending provider results. Reuse shared series information and fetch only
  missing referenced records; a partially warm cache must not refetch them all.
- Clear is a local saved decision. It remains available for an identified
  entry when enrichment is off or provider access is unavailable.
- "Turn on…" opens the consent dialog with the disclosure. The video
  information setting also appears in Settings, disabled when the build has no
  access token.
- TMDB's attribution goes under Credits on the About page.
- Use the same C# HTTP owner as the subtitle adapters, keeping TMDB request
  parsing and identification decisions at their own owners. Verify and disclose
  its actual route; no provider operation is delegated to C++.
- Provider release requirements remain the [spec's gate](library.md#privacy-and-provider-release-requirements).
  A token proves access only, not distribution or retention permission. Local
  search can be completed and verified while that release gate is pending.
- Finish the live English/Spanish review of the integrated page, card and
  dialogs, including theme changes and retained navigation state. Text is added
  with each surface in stage 3's workflow, using existing keys for shared facts
  and commands.

**Check:** with a build that has an access token, identify one movie and one
episode, edit one, clear one, restart, and confirm that each decision stays.

### 7. Measurement

Measure every [measurement gate](library.md#measurement-gates) on the Release
build with both collection sizes in the spec. Verify that closing the window
releases SQLite and feature workers and leaves the engine at its baseline.
A failed target is a defect in the work above, not a reason to add a loading state.

## Tests that earn their place

These are candidate checks, not a mandatory suite. Follow [testing](testing.md):
use the cheapest production seam that exposes the failure, C# logic and SQLite
before a pipe round trip. Add a test only when the compiler, an existing check or the
focused stage evidence does not already guard it. No running window is needed
to prove database or membership rules.

| Failure the test watches | What the person would see |
| --- | --- |
| Video information without a current entry produces a result | A file no current data source reports appears in Library. |
| Removal or a late enrichment write leaves contribution facts in `library.db` | Information about a removed torrent stays and is used again. |
| Startup cleanup uses only finished files | An unfinished file loses its early identification and repeats provider work. |
| A late automatic decision replaces a manual one | The person's correction disappears. |
| A shared location goes or changes row identity when its first contributor is removed | A still-reported file disappears or loses selection. |
| Text matches and rows come from different Library versions | Search displays an obsolete match or misses a newly matching file. |
| Short or accented queries do not match | Typing "e" or "amelie" does not find "Amélie". |

The prototype is presentation evidence only. Its browser sorting,
hard-coded shell associations and incomplete translations do not override the
spec or the existing native owners. Existing capture modes named Library still
exercise Torrents; extend capture coverage deliberately and review the images.
