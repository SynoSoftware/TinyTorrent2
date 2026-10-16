# Background work and status bar progress

Accepted architecture, 2026-10-13. The shared reporting module, feature wiring,
and status-bar consumers are implemented in the working tree. Source review
does not establish build correctness or native rendering; those checks remain
outstanding under the owner's no-build/no-test instruction.

## Purpose and settled scope

The person can see which app-wide operations are running, what each is doing,
whether progress is measurable, and how to cancel it. Work remains visible
through cancellation and cleanup. IMDb preparation runs off the UI thread at
low priority, including its immediate FTS5 `optimize` pass.

**Owner ruling, 2026-10-13:** app-wide long operations share one reporting
owner and expose progress and cancellation in the status bar. Torrent downloads
and checks keep their existing row presentation. This extends the earlier
metadata-only cancellation exception in [Interface](interface.md).

The [interface contract](interface.md) owns the status bar's product behavior;
the [status bar design](10-status-bar-design.md) records the design and review.
This proposal owns the reporting interface and integration inventory, including
the client-reviewed revisions below. The ordinary Library query remains ready
on opening; this does not introduce a Library loading screen.

Success means that a person can start IMDb preparation, continue using the
window, see parsing, indexing and optimization separately, and cancel without
losing the installed dataset. Concurrent metadata and subtitle work stays
independently identifiable and cancellable. The same lifecycle rules apply to
the other eligible operations in the inventory.

## Research findings before implementation

The existing owners already perform much of the work off-thread. The gap is a
consistent reporting lifecycle, plus missing cancellation scopes:

- [Dataset.Build and Import](../app/src/Library/Imdb/Dataset.cs) run the import
  through `Task.Run`. Parsing reports approximate progress through compressed
  sources. SQLite has a cancellation progress handler. `Task.Run` does not
  establish low scheduling priority. The current working copy omits `optimize`;
  the implementation of this plan restores it as a separately reported phase.
- [Browser.Download](../app/src/Library/Browser/Information.cs) already retains
  a completion task through discard/cleanup. Its private progress dispatch and
  formatted status currently feed Settings and the footer directly.
- [VideoLookup](../app/src/Library/VideoLookup.cs) owns both automatic enrichment
  and explicit batches. `EndBatch` clears counters and changes cancellation
  sources before the active request necessarily unwinds. Separate reads of
  batch counts also cannot guarantee a coherent observation.
- [Acquisition](../app/src/Subtitles/Acquisition.cs) shares a request token
  across acquisition, rescan and interactive requests. Its `Finding` property
  means accepted unfinished jobs, including persisted jobs. It does not prove
  a network request is running. Cancelling that common token for one footer
  item could stop unrelated work.
- [Database.Run](../app/src/Shared/Storage/Database.cs) already serializes SQL away
  from the UI and installs a connection-scoped cancellation handler. It must
  remain the sole owner of `library.db` transactions. A SQL call is a step of
  an operation, not a separate status item.
- The footer currently combines direct metadata, IMDb and subtitle properties.
  Keeping those paths alongside a new module would leave several reporting
  authorities. Adoption must replace them.

These are source findings, not timing measurements. This review has not run
the application, a build, tests, or an import benchmark.

## One reporting owner, existing execution owners

Add one concrete window-lifetime `BackgroundWork` module under
`app/src/Shared/`, created by the existing composition owner and passed to
the feature owners that need it. Its depth comes from handling registration,
atomic observations, cancellation presentation, coalescing, UI dispatch and
completion once for all consumers.

| Owner | Responsibility |
| --- | --- |
| `BackgroundWork` | Active operation identities, reporting lifetime, coherent observations, delivery to the UI and one shared cancellation command per operation |
| Feature owner | Admission, execution, its cancellation source, retries, saved state, safe completion, errors and outcome notifications |
| UI presentation adapter | Localized full/compact labels, icons, native bars and buttons, accessible names, responsive layout and focus; reused by the footer and Settings |
| `Database` and `Dataset` | Their own connections, transactions, SQLite interruption and file validity |

The module does not schedule jobs, create provider requests, decide retry
policy, own durable queues, or wrap every `Task`. No persisted activity table,
new engine-wide progress protocol, dependency-injection framework, or generic
job hierarchy is needed. Existing feature queues remain authoritative.

### Reporting interface

The working-tree implementation provides this interface.

| Member | Contract |
| --- | --- |
| `Begin(initial, cancel)` | Register one accepted operation with a complete initial observation and its owner's cancellation request delegate; return an `Operation` handle. A noncancellable operation has no delegate. |
| `Operation.Report(observation)` | Replace the complete observation atomically; callable from the worker without waiting for the UI |
| `Operation.Dispose()` | End reporting after the owner has finished its work and cleanup; safe in the outer `finally` |
| `Operation.Current` | Latest complete immutable observation published to the UI; one notification replaces the whole value |
| `Operation.Cancel` | Shared command for every UI consumer; eligibility comes from the current observation and whether a request is already being dispatched |
| `Items` | Read-only UI-observable collection of those stable operation objects |

An operation object is its identity. A second run gets a new object even when
the work and display name match. Late reports cannot recreate a disposed
operation or overwrite a new run. Keep collection order stable for the whole
lifetime; progress changes must not sort it again. The handle has its complete
initial observation before it is exposed through Items. Registration may occur
off-thread; UI publication never exposes a partially initialized item.

An immutable `Observation` contains `Description`, `Progress` and `Execution`.
`Description` is a small closed set of typed descriptions for the actual
workflows. An IMDb description carries its named phase; metadata distinguishes
an explicit batch from an automatic pass or an inspector lookup; subtitles
distinguish acquisition from discovery. Wait detail is typed, such as a known
provider and optional retry time, and may be absent when only a general wait
is established. No arbitrary object, JSON, worker-formatted text or universal
phase enum paired with an untyped payload crosses this interface.

Keep one canonical definition of an existing phase, such as `ImdbPhase`;
follow the naming contract if its shared use requires moving that definition.
Report numeric progress once, as `WorkProgress`, from the owner that has the
numbers, such as the HTTP download and the IMDb import; a description carries
no numbers that Progress repeats.
Give metadata/subtitle owners a coherent observation where independent counter
reads are currently insufficient. Workflow descriptions remain fixed-purpose
for an operation's lifetime; a new run or unrelated task gets a new identity.

The presentation adapter reads these immutable facts and the current `Strings`
catalogue. It owns full/compact text and cancellation names. Settings, dialogs
and the footer consume the same observation for a given operation, so language
changes and phase transitions cannot leave their messages disagreeing. Domain
code supplies facts; the module never reads controls or calls back into a
worker to calculate a label.

### Progress and execution state

`Progress` has three explicit cases: `Unknown`, `Amount(completed, unit)` and
`Fraction(completed, total, unit, approximate)`. Amount preserves useful facts
such as 284 MB received or 12 files inspected when the total is unknown; its
bar remains indeterminate. A Fraction has a positive total and a completed
amount between zero and that total. A zero-work phase finishes directly.
The reporting boundary validates these invariants rather than the UI hiding
bad values by clamping. Both numbers belong to one observation. Percentages
are derived in the presenter, never independently stored beside the numbers.
An existing source that supplies only an approximate percentage maps once to
a fraction of 100 with the approximation flag; this does not claim an overall
workflow percentage.

| State | Meaning and display |
| --- | --- |
| Running | Work is executing; the owner supplies its cancellation availability; determinate bar for a measured phase, indeterminate bar otherwise |
| Waiting | Owner confirms a queue, provider, connection or retry wait; show that reason and the owner's cancellation availability; do not animate unknown work as executing |
| Cancelling | A stop request was accepted; disable repeat cancellation and retain the item until the owner finishes cleanup |
| Finishing | Owner has crossed a short irreversible publication/commit point; show that phase and disable cancellation until completion |

Execution is represented by these distinct cases. Running and Waiting carry
their cancellation availability; Cancelling and Finishing are not cancellable
by construction. A noncancellable running step does not get renamed Finishing.
Lack of a wired token alone is not a reason to remove Cancel from interruptible
work. Actual filesystem calls may still have to return after a stop request.

Completion removes the item. Existing feature notifications report success or
actionable failure; the reporting module adds no second toast or error policy.
An operation that is merely queued has a truthful Waiting observation from
registration, including while acquiring a cancellable semaphore.

Use progress for the **named current phase**. Parsing 80% of a ratings archive
is not 80% of preparing IMDb. A new stage may reset the bar with its new label.
Do not derive percentages from elapsed time, SQLite instruction counts,
successful matches, or the approximate 290 MB download description. When a
batch accepts more work during execution, report the changing processed/total
counts as such; avoid implying a fixed original completion estimate.

### Delivery and thread affinity

Worker reports only replace the latest immutable observation under a short
lock. Queue at most one pending UI flush for the reporting owner; the flush
publishes the latest dirty operation observations. Coalesce
numeric samples to at most ten deliveries per second while work is active;
start, stage, cancellation and completion changes request prompt delivery.
There is no idle polling loop and no dispatcher callback for every imported row.
The collection is a current-state view, not an event log: a short phase may
finish before a UI flush without requiring an artificial display delay.
Keep one shared scheduled flush only while numeric observations are pending,
so the final pending sample arrives even when the worker reports nothing more.
The dispatcher reads each operation's latest observation, never an old snapshot
captured when it queued the callback. A begin/dispose pair completed before
first UI publication need not appear at all.

`BackgroundWork` alone marshals collection and item notifications to the window's
`DispatcherQueue`. Do not invoke cancellation delegates, feature events or
localization code while holding its lock. A late numeric sample must never
revert Cancelling or recreate a completed item. UI queue shutdown stops
presentation delivery; it does not prevent the worker from disposing its handle.

Microsoft documents that `await` alone does not move application code off the
UI thread, and that background work must marshal UI changes through the
dispatcher. Use native asynchronous I/O and explicitly offload CPU or blocking
work. [Keep the UI thread responsive](https://learn.microsoft.com/en-us/windows/apps/develop/performance/keep-ui-thread-responsive),
[DispatcherQueue](https://learn.microsoft.com/en-us/windows/apps/develop/dispatcherqueue).

### Cancellation has one path

The domain owner retains the cancellation source. Both the footer and the
existing feature surface invoke the same operation cancellation command; that
command calls the domain's stop implementation. The module does not introduce
another token that competes with a batch, route or lifetime token.

The delegate registered by Begin targets that accepted run, never an unqualified
CancelCurrent or StopBatch that could now refer to its successor. Under its
existing run guard, the feature treats a request after completion as a no-op
and never touches a disposed cancellation source or cancels the next run.
Stable registry identity does not replace this owner check because completion
can race delegate invocation outside the registry lock.

The shared command prevents duplicate dispatch while it invokes the owner's
request delegate outside the registry lock. The feature owner alone accepts
the stop under the same synchronization point that arbitrates publication,
reports Cancelling there, and signals its cancellation source with
[`CancelAsync`](https://learn.microsoft.com/en-us/dotnet/api/system.threading.cancellationtokensource.cancelasync).
This marks the token synchronously while running registered callbacks
asynchronously, so the owner can capture the signalling task under the same
gate without executing callbacks inside it. If publication already won, it
reports/retains Finishing instead; this is an ordinary no-op for a stale click,
not an error. The reporting module must not optimistically invent Cancelling or
restore an old Running snapshot.

The delegate must yield promptly: no SQL, file deletion, synchronous waits or
expensive cancellation callbacks on the UI thread. Asynchronous cancellation
callbacks remain observed by the feature's error owner. The delegate handles
expected failures through that owner and reports its current state; it does
not ask the registry to implement a rejection/rollback state machine.
**Returning from the delegate is not completion of the operation.** Only the
executing owner's outer finally, after cleanup, ends its reporting lifetime.

Keep the run's cancellation source alive while an accepted request is signalling
it and its cancellation callbacks are unwinding. Teardown drains that signalling
work before disposing the source, because the worker can finish while callbacks
are still unwinding. Do not await an entire stop delegate from teardown if that
delegate awaits the same run's Finished
task; only the signalling/callback work participates in this lifetime guard.

The feature serializes lifecycle transitions and their Report calls through
its existing gate. A previously captured Running observation cannot publish
after the owner has accepted cancellation or publication. Report does no
callbacks, dispatch waits or domain work while holding that gate. The registry
also ignores reports after disposal; it does not decide domain transition order.

Automatic disconnect, route change, supersession and shutdown continue through
the domain's invalidation paths. They also report their actual transition; a
provider failure must not be silently relabelled as user cancellation.

The owner arbitrates the race between cancellation and irreversible publication
at its existing synchronization point. Before publication, cancellation discards
the incomplete result. After publication has begun, the operation completes the
required finalization and the UI reports Finishing; it never promises rollback.
Keep long parsing, indexing and optimization outside this short barrier.
For a batch, publishing one item's result does not cross the publication barrier
for the whole batch. Cancel still stops admission of the remaining items while
the current required save finishes; retain Cancelling through that save and
cleanup. Use terminal Finishing only once the accepted operation as a whole
has no remaining interruptible work. Saved responses and completed items remain.
Retain phase identity while cancelling and freeze useful-work fractions. An
indeterminate indicator labelled Cancelling can describe cleanup still underway;
it must not be labelled as continued parsing or downloading.

This follows .NET cooperative cancellation: requesting a stop and observing that
the operation has stopped are different events.
[Task cancellation](https://learn.microsoft.com/en-us/dotnet/standard/parallel-programming/task-cancellation).

## IMDb execution and SQL design

Keep the established disposable build file and install coordination with
`VideoLookup.Reload`. The installed dataset remains readable while the new
one is prepared. The importer never holds the shared Library database lock
through downloading, parsing, indexing or optimization.

| Phase | Progress | Cancellation |
| --- | --- | --- |
| Downloading each source | Received bytes over the length the response declares; the received count alone when the response declares none or sends more than it declared | Cancel network/file copy through the existing token |
| Reading episodes, titles, ratings | Approximate current compressed-source position/length; keep below 100% until that source is fully consumed | Check between input records and through the SQLite handler |
| Linking episodes | Episodes written over episodes read | Check between rows and through the SQLite handler |
| Indexing titles | Titles added to the search index over titles kept; the two key indexes build first and report nothing, so the count holds at zero meanwhile | Check between rows; the SQLite progress handler interrupts the key indexes |
| Optimizing search index | Unknown | SQLite progress handler interrupts `optimize` |
| Flushing prepared data | Unknown | A synchronous flush must return before cancellation cleanup can finish |
| Installing data | Unknown; short Finishing state | Existing coordinated publication finishes once committed |

The import's one transaction commits after indexing, before `optimize`. That
commit is not publication of the installed dataset. The irreversible
publication barrier belongs at installation; a stop during an uninterruptible
flush remains a cancellation request awaiting that call's return, not a false
promise of instant completion.

The search index is filled one title at a time from the titles table, so the
phase counts titles; FTS5's `rebuild` command does the same work and reports
nothing. `INSERT INTO title_search(title_search) VALUES('optimize');` follows
the commit, before closing/flushing/installing the build file. `optimize`
merges FTS index structures and can itself take substantial time; SQLite
exposes no whole-command percentage here. It keeps its own indeterminate phase.
[SQLite FTS5 optimize](https://www.sqlite.org/fts5.html#the_optimize_command).

Keep one connection-scoped SQLite progress handler through the interruptible
SQL phases and unregister it before disposing the connection. Its only job is
to inspect cancellation and return the interruption decision; it performs no
SQL, UI calls or formatting. An interruption of the disposable build discards
that file. Interrupted SQL is translated to cancellation only when the matching
token was requested. The handler's instruction interval is not a progress total.
[SQLite progress handler](https://www.sqlite.org/c3ref/progress_handler.html).

Run the entire synchronous `Import` body on one dedicated worker thread. Enter
Windows `THREAD_MODE_BACKGROUND_BEGIN` on that thread before opening its private
files/connection, and leave with `THREAD_MODE_BACKGROUND_END` in `finally` on
the same thread. This lowers background resource scheduling priority, including
the I/O contention important for this import. Do not change process priority or
a reusable thread-pool thread. Do not put an asynchronous delegate on this
thread and assume its continuations retain the scheduling mode.
[SetThreadPriority](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setthreadpriority).

Keep this small worker implementation next to the importer until a second real
caller needs it. Return an awaitable completion with asynchronous continuations,
propagate exceptions, and await its exit during cleanup. Check the native call's
result; failure to establish the requested mode is surfaced through the existing
error path. Low priority improves coexistence; it does not promise a shorter
elapsed import time or instant interruption of an operating-system file call.

The import keeps one bulk transaction and reused commands; `journal_mode=OFF`
and `synchronous=OFF` remain confined to the disposable build file. The
episodes table is written in primary-key order from episode numbers sorted in
memory, because inserting millions of rows in file order into a `WITHOUT
ROWID` table touches a random page for every row, while sorted rows extend the
table at its end. Each episode's title is a separate table keyed by episode,
written in file order; the provider joins the two. Do not change global WAL
settings, pooling or durability on the strength of a source review. The
[IMDb plan](imdb-datasets.md#download-and-import) records each phase's
measured duration and the resulting file size; further SQL tuning starts from
a new measurement.

## Codebase inventory and wiring plan

The scan covered app execution (`Task.Run`, threads, async entry points,
semaphores, cancellation and long loops), their feature owners, and engine
workers. Registration is at the operation owner, once per accepted workflow;
helper calls and nested SQL/network steps update that item instead of adding
children. The tables distinguish work that can continue across pages from
modal interaction and essential infrastructure.

### App-wide operations to register

| Workflow and current owner | Reporting and cancellation changes |
| --- | --- |
| IMDb download/import/install — [Browser.Information](../app/src/Library/Browser/Information.cs), [Dataset](../app/src/Library/Imdb/Dataset.cs) | One operation from acceptance through final cleanup. Reuse `CancelDownload`/`Finished`; add the phases and low-priority worker above. Settings and footer consume that same operation. |
| Explicit metadata batch — [VideoLookup.Fetch/RunBatch](../app/src/Library/VideoLookup.cs) | One item per accepted batch, including retries and appended entries. Capture processed/total and wait reason under the existing gate. `StopBatch` stops admitting entries immediately but ends reporting only when the active request and required saves settle. An old run cannot remove its successor. |
| Automatic metadata enrichment — [VideoLookup.Run](../app/src/Library/VideoLookup.cs) | Register a finite enrichment pass when work exists, with processed count and unknown total unless cheaply known. When an explicit batch takes precedence, show its owner-confirmed wait. Add cancellation of the current automatic pass; preserve the provider preference and completed results. |
| Media file properties — [FileFacts.Run](../app/src/Library/FileFacts.cs) | Register each nonempty pass, report files inspected, and link a pass token with existing lifetime/context tokens. Stop prevents further file admission; an in-flight Windows property call may need to unwind. Do not label the idle worker as busy. |
| Manual subtitle batch — [Acquisition.Find/StopFinding/Pump](../app/src/Subtitles/Acquisition/Acquire.cs) | One operation for accepted manual jobs, including restored jobs and confirmed provider waits. Reuse durable cancellation semantics of `StopFinding`, but isolate the manual request scope from rescan and interactive work. Report processed attempts separately from successful `Found`. |
| Automatic subtitle acquisition — [Acquisition.Pump](../app/src/Subtitles/Acquisition/Acquire.cs) | One operation for admitted automatic jobs, distinct from manual work because cancellation has different scope. Newly eligible jobs join the active operation so an older blocked job cannot starve them. Cancellation freezes its accepted set and marks its pending jobs using the existing cancelled outcome; it does not switch off the saved automatic preference. |
| Subtitle discovery/rescan — [Acquisition.Rescan/Discover](../app/src/Subtitles/Acquisition/Discovery.cs), [Reconcile](../app/src/Subtitles/Acquisition/Storage.cs) | One item for an explicit rescan or a substantial automatic discovery pass. Queue wait is cancellable. Pass an operation-specific token through semaphore acquisition, enumeration and identification; do not use global `Invalidate` as this item's Cancel. Once groups are known, report completed folders/total folders; commit a folder's scan stamp only after its work is complete. |
| Inspector metadata request — [Browser.RequestInformation](../app/src/Library/Browser/Information.cs) | Track the single accepted lookup with the existing request identity/token and `CancelInformation`; the inspector and footer cancel the same operation. Preserve existing selection/visibility invalidation: reporting must not extend the request's lifetime across navigation. When it delegates to a batch, end/transfer ownership without displaying two copies of that lookup. |
| Update check — [MainViewModel.CheckUpdates](../app/src/Shell/MainViewModel/Updates.cs) | Register only when a network check actually begins. Unknown progress, cancel the current request through its existing timeout/request token, retain cache and update preference. Existing daily admission prevents immediate retry after a user stop. |
| Torrent source preparation accepted without an Add dialog — [MainViewModel.Actions](../app/src/Shell/MainViewModel/Actions.cs), [AddDraft](../app/src/Torrents/AddDraft.cs) | Report the accepted group while resolving source previews; use the existing preview-release/cancel path for that group. Completion means ownership has passed to torrent rows. Do not register the preview polling loop separately or turn footer Cancel into removal of an added torrent. |

For automatic metadata/file-facts passes, cancellation must survive an ordinary
refresh notification. Each feature defines the finite accepted set using its
existing stable work/input identity and suppresses its remaining cancelled
members until explicit retry/refresh or changed inputs for those members.
If A/B/C were accepted and cancelled, discovering D may admit D; it must not
re-admit B/C merely because a global source revision changed. An ordinary
snapshot or metadata notification is not an explicit retry. Keep this policy
at the feature owner, without a general revision framework in the registry.
Subtitle jobs already provide durable cancelled outcomes; reuse those instead
of adding a second suppression ledger. Inspect reconciliation so old jobs do
not immediately return to Pending.

Do not promise a generic Retry where the feature has no such action. FileFacts
has no current user Refresh command: keep its cancelled unchanged inputs
suppressed for this window lifetime, permit genuinely new/changed files, and
allow the next window lifetime to retry. Metadata can resume through its
existing explicit fetch/refresh or changed eligible inputs. No registry Retry
command or new product setting is introduced to support this reporting feature.

Register user-requested work immediately and automatic passes when actual work
is accepted. Use ordinary coalesced delivery initially. Add no 500 ms appearance
timer without evidence that short completed operations produce distracting
flicker; deferral would belong solely to presentation and must never delay
cancellation or recovery feedback.

### Audited work with a different presentation or cancellation contract

| Work and source | Decision and reason |
| --- | --- |
| Metadata search/choice in [MetadataDialog](../app/src/Library/MetadataDialog.cs), subtitle candidates/download in [Acquisition.Choose](../app/src/Subtitles/Acquisition/Choose.cs), account checks in [AccountDialog](../app/src/Shared/AccountDialog.cs) | Modal foreground work already has progress and cancellation in the active surface. Use the same operation observation if shared reporting is needed, but retain the usable cancel control in the modal; an inaccessible footer cannot be its only control. Avoid duplicate registration inside shared provider methods. |
| Connection test — [ConnectionSetup](../app/src/Settings/ConnectionSetup.cs), [engine ConnectionTest](../engine/src/Engine/ConnectionTest.cpp) | Existing staged interaction owns its progress, cancel and restoration. It stays in that workflow; if allowed to continue after navigation, register its existing identity and route cancellation to `connection_test cancel`, retaining restoration until the engine confirms it. |
| Proxy test — [Proxy](../app/src/Settings/Proxy.cs) | Dialog-scoped engine check; current `check_proxy` dispatch has no equivalent user cancel path demonstrated by this review. Closing a local observation would not cancel engine work. Keep existing dialog feedback; adding true engine cancellation is a separate protocol change if this becomes app-wide. |
| Interactive Add preview — [AddDraft](../app/src/Torrents/AddDraft.cs) | Keep progress and source-specific cancel in the Add dialog; use its operation for any background continuation, not a second footer-owned preview. |
| File-list synchronization — [TorrentSource.Run](../app/src/Shared/TorrentSource.cs), [FileSources](../app/src/Shared/FileSources.cs) | Essential membership/consistency maintenance. User cancellation can leave current-file state incomplete, so do not expose a cancel that pretends synchronization finished. Existing generation/disconnect cancellation remains. No perpetual activity for snapshot polling and no new Library initial-loading status. |
| Database recovery, reset protection and dataset deletion — [MainViewModel.Features](../app/src/Shell/MainViewModel/Features.cs), [Browser.Information](../app/src/Library/Browser/Information.cs) | Accepted state transitions must complete their cleanup/commit. If prolonged, report a Finishing observation through the same module with cancellation unavailable and a specific label. A cancel may abort a cancellable wait before mutation, but cannot undo an accepted reset/delete. |
| Accepted subtitle deletion and file publication — [Acquisition.Files](../app/src/Subtitles/Acquisition/Files.cs), [SubtitleFile](../app/src/Subtitles/SubtitleFile.cs), [RecycleBin](../app/src/Shared/Desktop/RecycleBin.cs) | Owned steps of acquisition/removal, with their existing protection and publication semantics. Report through the parent operation where one exists; do not add a second independent Cancel that can imply undoing a committed file change. |
| Library query/filter, image and piece rendering, disk-space sampling, language preparation, association lookup, window placement, pipes and timers | Interaction rendering or supporting infrastructure, not accepted app-wide jobs. Preserve existing off-thread execution/cancellation where present; a footer item for every task would remain permanently busy. |
| Engine transfer, verification, move, preview internals, persistence, networking and pipe workers | Transfer/verification/move belong to torrent rows; preview belongs to Add; the remaining workers are engine infrastructure. No duplicate footer progress or new generic engine task channel. |

The Finishing exception is a correctness constraint, not permission to omit
Cancel from interruptible long work. The implementation review must challenge
any operation placed there merely because cancellation has not yet been wired.

## Consumer contract for the status bar implementer

The footer observes one collection on the UI thread. It never separately reads
`BatchDone`, `BatchTotal`, `HasDataWork`, `DataStatus`, `HasSubtitleWork` or
`IsScanning` to reconstruct progress. Migrate Settings and existing Stop actions
to the same operation; remove the obsolete footer-specific facade properties
and dispatch logic once their callers have moved.

Every active operation remains discoverable on every page, with its own phase,
bar and cancellation target. Never average unrelated percentages or rotate
labels on a timer. Use stock WinUI `ProgressBar`: measurable work uses Value
and Maximum, unknown running work uses IsIndeterminate. Confirmed waiting and
cancelling states must not imply continuing useful progress.
[Windows progress guidance](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/progress-controls).

The client owns how concurrent items fit. Use inline task groups while they
fit. After optional diagnostics, ordinary rates and collection context have
yielded, keep a labelled background-work button with an operation count that
opens a native Flyout list if the remaining operations cannot fit readably.
Each row uses the same item template, operation observation and cancellation
command; do not average unrelated progress or create an aggregate Cancel.
Preserve the settled inline metadata-batch X on every page while that batch
exists. If a successor batch starts before its predecessor finishes cleanup,
the currently actionable batch occupies that slot; the settling predecessor
remains in the list. With no successor, retain the settling batch inline until
disposal. The button's count includes all active operations, and its list shows
all of them in insertion order, including the inline batch. Other work stays
independently discoverable and cancellable in the list. This extends the earlier
no-overflow rule because the inventory now contains more critical operations
than one line can display. Do not raise the
window minimum, hide work silently or shrink names to anonymous ellipses.

The overflow list is a second view of the same observations, not a second
registry. The UI never re-registers an operation when it moves between inline
and list presentation. Completion/removal preserves keyboard focus on the next
item, the work button or the active page. Native rendering of concurrent jobs at narrow widths
and enlarged text remains an implementation acceptance check.

The UI owns focus recovery when an item completes, full accessible operation
and cancel names, and restrained announcements for major transitions. Numeric
samples do not generate a stream of screen-reader announcements. Errors remain
at their established owner/surface. No file path, provider key or account secret
is needed in shared progress text.

## Lifetime and shutdown

One operation covers the accepted workflow across page changes and intermediate
awaits. Register before a long queue wait. Dispose after its outer cleanup,
including temporary-file discard, required saves and release of domain gates.
Registration and disposal do not wait for the UI thread.

During window shutdown, existing feature owners stop admission, request their
own lifetime cancellation and await workers. Only after feature teardown does
the reporting owner release its collection/dispatcher subscription. A failed
dispatcher enqueue cannot strand a feature's completion task. Route changes
invalidate request scopes without confusing their eventual completion with a
new operation using the same display name.

## Implementation sequence and evidence

1. Implement the reviewed interface and concurrency presentation above: the
   service chat owns the reporting module; the UI chat owns the presentation
   adapter and reusable template. Source
   review must establish atomic observations, one cancellation path, no worker
   UI access and no reports surviving disposal.
2. Integrate IMDb end to end, restore immediate `optimize`, split phase reporting
   and introduce its dedicated background-priority import thread. Source review
   must trace cancel through parsing, both FTS commands, flush, install and
   discard while protecting the installed database.
3. Integrate explicit/automatic metadata, file facts, subtitle batches and
   discovery. Fix independent cancellation scopes and cancellation completion
   before adding the footer controls; a visible but misleading X is not done.
4. Integrate update checks, inspector lookup and unattended Add preparation;
   handle prolonged non-interruptible maintenance as described in the inventory.
   Remove superseded direct footer properties and duplicate progress formatting.
5. Reconcile Interface, status-bar design, IMDb and subtitle documentation with
   the implemented behavior. Do not maintain competing active contracts.

Builds, automated tests, runtime checks and measurements require owner
authorization. When authorized, the high-value checks are cancellation during
each IMDb phase; preservation of the previous dataset; indexing/optimization
failure cleanup; cancel versus commit races; a late report from an old run;
disconnect/shutdown; cancellation while queued; independent simultaneous jobs;
dynamic batch totals; and cancellation of an automatic pass without immediate
restart. Review English/Spanish, narrow widths, scaled text, keyboard focus and
screen-reader output with the UI owner. Measure phase timings and UI interaction
responsiveness during a real import before making performance claims.

## Review record and remaining evidence

The status-bar implementation chat reviewed this plan from the consumer side
and iterated with its independent adversarial reviewer. The review changed the
body of the plan rather than retaining a competing set of recommendations:

- Explicit Unknown, Amount and Fraction progress preserve useful counts without
  fake denominators and keep all numeric facts in one observation.
- The feature owner alone arbitrates cancellation versus publication. The
  reporting module forwards one command and performs no optimistic lifecycle
  transition or rejection rollback. That command targets the accepted run;
  a stale click cannot cancel its successor or touch a disposed token source.
- Lifecycle reports are ordered at the feature gate; UI delivery reads current
  snapshots. A pending final numeric sample still gets delivered; disposed
  identities are terminal.
- Automatic cancellation applies to accepted work/input identities, not a global
  revision that can restart cancelled work when a different file arrives.
- Disposable IMDb COMMIT remains cancellable. Irreversible publication begins
  at installation; a slow flush can delay cleanup without disabling the stop.
- The 500 ms appearance delay was removed. Normal coalescing is the initial
  policy; no unmeasured anti-flicker mechanism is required.
- The expanded inventory has an explicit native overflow list while preserving
  the inline metadata-batch Cancel. Every row uses the same observation and
  command, with no second registry or duplicate notification owner.

The independent adversarial reviewer approved the revised implementation plan
on 2026-10-13 with no remaining substantive blockers. This approves the contract,
not its implementation or native rendering; the checks below remain outstanding.

Direct chat tools returned Transport closed. The client received the draft
through the shared repository and wrote this review here; delivery of a chat
message is not claimed.

The reporting-module author reviewed the revised contract on 2026-10-13 and
accepts its ownership and consumer interface. This pass clarified cancellation
source lifetime during signalling and preserved batch cancellation while one
item commits. It also reconciled the IMDb document's obsolete optimize omission
and phase-count example with this plan. These are document reviews; implementation
and runtime evidence remain outstanding.

The owner subsequently assigned implementation of both the service and UI to
this chat. `BackgroundWork` now owns operation reporting and command delivery;
feature owners retain their cancellation sources and execution policy.
`WorkStatus` renders the same operation in the footer, its native work flyout,
and IMDb Settings. Superseded direct metadata/subtitle progress properties were
removed. Automatic subtitle admission extends the active accepted set until
cancellation starts, so blocked jobs do not prevent unrelated new work.

The footer uses content-sized context, activity, and transfer groups. Flexible
space belongs only between those top-level groups. Processes remain together,
with 6-DIP internal spacing and subtle vertical dividers between processes.
Native progress bars retain their platform template; existing transfer and
connection colors remain. The overflow list shares each operation's command.

No builds, tests, native layout review or import measurements were performed.
The independent adversarial source review approved the implementation after
corrections to subtitle admission, disconnect reporting, cleanup ordering and
overflow visibility. XML/JSON parsing and whitespace checks passed. Compilation,
runtime cancellation, and actual English/Spanish narrow-width, enlarged-text
and accessibility behavior remain unverified.

### Expanded source review

Seven reviewers reviewed the full working-tree diff against
`2f41baf146c4b6bcbf8c7224e4cb0d9bab4125f8`, including new files, with surrounding
code and callers traced along these paths:

- Reporting, cancellation and shutdown, including updates and unattended adds.
- Metadata, Inspector requests, IMDb import/install, and local file properties.
- Subtitle admission, discovery, acquisition, path migration and cancellation.
- Native status composition, Settings, focus, localization and accessibility.
- Database, pipe startup, and the changed app/engine check scripts.
- Ownership, readability and obsolete-code cleanup across the diff.
- Contract consistency and the HTML/image design artifacts.

The pass corrected subtitle ambiguity and moved-job cancellation, isolated scan
cancellation from acquisition admission, deduplicated automatic file work, and
made queue reporting follow provider admission. Unattended additions now retain
one operation through submission and cleanup. Settings focus, unavailable
counts, compact torrent scope and major-transition announcements were corrected.
Existing fixtures now supply the reporting dependency and use the current
contracts. Superseded presentation resources were removed. The HTML study was
updated and its stale PNG retired. The owner's build exposed an unsafe-code
requirement in generated interop; the declarations now use the app's established
`DllImport` pattern instead.

The revised Inspector ownership was reread after the concurrent Browser
refactor. Corrected paths received independent source re-review. This record
does not claim an application build, test run, native screenshot review, or
assistive-technology check.
