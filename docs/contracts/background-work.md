# Background work and status bar progress

## Purpose and scope

The person can see which app-wide operations are running, what each is doing,
whether progress is measurable, and how to cancel it. Work remains visible
through cancellation and cleanup. IMDb preparation runs off the UI thread at
low priority ([import execution](imdb-datasets.md#import-execution-and-sql-design)).

**Owner ruling, 2026-10-13:** app-wide long operations share one reporting
owner and expose progress and cancellation in the status bar. Torrent downloads
and checks keep their existing row presentation. This extends the earlier
metadata-only cancellation exception in [UI](ui.md).

The [UI contract](ui.md) owns the status bar's product behavior.
This contract owns the reporting interface and which work registers. The
ordinary Library query is ready on opening; there is no Library loading screen.

A person can start IMDb preparation, continue using the window, see parsing,
indexing and optimization separately, and cancel without losing the installed
dataset. Concurrent metadata and subtitle work stays independently identifiable
and cancellable, and the same lifecycle rules apply to every other registered
operation.

## One reporting owner, existing execution owners

One concrete window-lifetime `BackgroundWork` module lives under
`app/src/Shared/`. The composition owner creates it and passes it to the feature
owners that need it. Its depth comes from handling registration, atomic
observations, cancellation presentation, coalescing, UI dispatch and completion
once for all consumers. It replaces every feature-specific footer progress
property, so no second reporting authority exists.

| Owner | Responsibility |
| --- | --- |
| `BackgroundWork` | Active operation identities, reporting lifetime, coherent observations, delivery to the UI and one shared cancellation command per operation |
| Feature owner | Admission, execution, its cancellation source, retries, saved state, safe completion, errors and outcome notifications |
| UI presentation adapter (`WorkStatus`) | Localized full/compact labels, icons, native bars and buttons, accessible names, responsive layout and focus; reused by the footer, the work list and Settings |
| `Database` and `Dataset` | Their own connections, transactions, SQLite interruption and file validity |

The module does not schedule jobs, create provider requests, decide retry
policy, own durable queues, or wrap every `Task`. There is no persisted activity
table, engine-wide progress protocol, dependency-injection framework or generic
job hierarchy. Existing feature queues stay authoritative. `Database.Run` stays
the sole owner of `library.db` transactions; a SQL call is a step of an
operation, not a separate status item.

### Reporting interface

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
operation or overwrite a new run. Collection order is stable for the whole
lifetime; progress changes do not sort it again. The handle has its complete
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

An existing phase has one canonical definition, such as `ImdbPhase`; the naming
contract decides where a shared definition lives. Numeric progress is reported
once, as `WorkProgress`, from the owner that has the numbers, such as the HTTP
download and the IMDb import; a description carries no numbers that Progress
repeats. Metadata and subtitle owners give a coherent observation where
independent counter reads are insufficient. Workflow descriptions are fixed for
an operation's lifetime; a new run or unrelated task gets a new identity.

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
bar is indeterminate. A Fraction has a positive total and a completed amount
between zero and that total. A zero-work phase finishes directly. The reporting
boundary validates these invariants rather than the UI hiding bad values by
clamping. Both numbers belong to one observation. Percentages are derived in the
presenter, never stored beside the numbers. An existing source that supplies
only an approximate percentage maps once to a fraction of 100 with the
approximation flag; this does not claim an overall workflow percentage.

| State | Meaning and display |
| --- | --- |
| Running | Work is executing; the owner supplies its cancellation availability; determinate bar for a measured phase, indeterminate bar otherwise |
| Waiting | Owner confirms a queue, provider, connection or retry wait; show that reason and the owner's cancellation availability; do not animate unknown work as executing |
| Cancelling | A stop request was accepted; disable repeat cancellation and retain the item until the owner finishes cleanup |
| Finishing | Owner has crossed a short irreversible publication/commit point; show that phase and disable cancellation until completion |

Execution is represented by these distinct cases. Running and Waiting carry
their cancellation availability; Cancelling and Finishing are not cancellable
by construction. A noncancellable running step is not renamed Finishing.
Interruptible work keeps its Cancel even where no token is wired yet; the
Finishing exception is a correctness constraint, not a place to park work whose
cancellation is missing. Actual filesystem calls may still have to return after
a stop request.

Completion removes the item. Existing feature notifications report success or
actionable failure; the reporting module adds no second toast or error policy.
A merely queued operation has a truthful Waiting observation from registration,
including while acquiring a cancellable semaphore.

Progress describes the **named current phase**. Parsing 80% of a ratings archive
is not 80% of preparing IMDb. A new stage may reset the bar with its new label.
Percentages are never derived from elapsed time, SQLite instruction counts,
successful matches, or the approximate 290 MB download description. When a batch
accepts more work during execution, the changing processed/total counts are
reported as such, without implying a fixed original completion estimate.

### Delivery and thread affinity

Worker reports only replace the latest immutable observation under a short
lock. At most one UI flush is queued for the reporting owner; it publishes the
latest dirty operation observations. Numeric samples coalesce to at most ten
deliveries per second while work is active; start, stage, cancellation and
completion changes request prompt delivery. There is no idle polling loop and no
dispatcher callback for every imported row. The collection is a current-state
view, not an event log: a short phase may finish before a UI flush without an
artificial display delay. One shared scheduled flush runs only while numeric
observations are pending, so the final pending sample arrives even when the
worker reports nothing more. The dispatcher reads each operation's latest
observation, never an old snapshot captured when it queued the callback. A
begin/dispose pair completed before first UI publication need not appear at all.

`BackgroundWork` alone marshals collection and item notifications to the window's
`DispatcherQueue`. It invokes no cancellation delegates, feature events or
localization code while holding its lock. A late numeric sample never reverts
Cancelling or recreates a completed item. UI queue shutdown stops presentation
delivery; it does not prevent the worker from disposing its handle.

Microsoft documents that `await` alone does not move application code off the
UI thread, and that background work must marshal UI changes through the
dispatcher. Native asynchronous I/O is used, and CPU or blocking work is
explicitly offloaded. [Keep the UI thread responsive](https://learn.microsoft.com/en-us/windows/apps/develop/performance/keep-ui-thread-responsive),
[DispatcherQueue](https://learn.microsoft.com/en-us/windows/apps/develop/dispatcherqueue).

### Cancellation has one path

The domain owner retains the cancellation source. Both the footer and the
feature surface invoke the same operation cancellation command; that command
calls the domain's stop implementation. The module introduces no token that
competes with a batch, route or lifetime token.

The delegate registered by Begin targets that accepted run, never an unqualified
CancelCurrent or StopBatch that could now refer to its successor. Under its
run guard, the feature treats a request after completion as a no-op and never
touches a disposed cancellation source or cancels the next run. Stable registry
identity does not replace this owner check because completion can race delegate
invocation outside the registry lock.

The shared command prevents duplicate dispatch while it invokes the owner's
request delegate outside the registry lock. The feature owner alone accepts
the stop under the same synchronization point that arbitrates publication,
reports Cancelling there, and signals its cancellation source with
[`CancelAsync`](https://learn.microsoft.com/en-us/dotnet/api/system.threading.cancellationtokensource.cancelasync).
This marks the token synchronously while running registered callbacks
asynchronously, so the owner can capture the signalling task under the same
gate without executing callbacks inside it. If publication already won, it
reports/retains Finishing instead; this is an ordinary no-op for a stale click,
not an error. The reporting module never invents Cancelling optimistically or
restores an old Running snapshot.

The delegate yields promptly: no SQL, file deletion, synchronous waits or
expensive cancellation callbacks on the UI thread. Asynchronous cancellation
callbacks remain observed by the feature's error owner. The delegate handles
expected failures through that owner and reports its current state; the registry
has no rejection/rollback state machine. **Returning from the delegate is not
completion of the operation.** Only the executing owner's outer finally, after
cleanup, ends its reporting lifetime.

The run's cancellation source stays alive while an accepted request is signalling
it and its cancellation callbacks are unwinding. Teardown drains that signalling
work before disposing the source, because the worker can finish while callbacks
are still unwinding. Teardown does not await an entire stop delegate if that
delegate awaits the same run's Finished task; only the signalling/callback work
participates in this lifetime guard.

The feature serializes lifecycle transitions and their Report calls through
its existing gate. A previously captured Running observation cannot publish
after the owner has accepted cancellation or publication. Report does no
callbacks, dispatch waits or domain work while holding that gate. The registry
also ignores reports after disposal; it does not decide domain transition order.

Automatic disconnect, route change, supersession and shutdown continue through
the domain's invalidation paths. They also report their actual transition; a
provider failure is never relabelled as user cancellation.

The owner arbitrates the race between cancellation and irreversible publication
at its existing synchronization point. Before publication, cancellation discards
the incomplete result. After publication has begun, the operation completes the
required finalization and the UI reports Finishing; it never promises rollback.
Long parsing, indexing and optimization stay outside this short barrier. For a
batch, publishing one item's result does not cross the publication barrier for
the whole batch. Cancel still stops admission of the remaining items while the
current required save finishes; Cancelling is retained through that save and
cleanup. Terminal Finishing is used only once the accepted operation as a whole
has no remaining interruptible work. Saved responses and completed items remain.
A cancelling operation retains its phase identity and freezes useful-work
fractions. An indeterminate indicator labelled Cancelling can describe cleanup
still underway; it is never labelled as continued parsing or downloading.

This follows .NET cooperative cancellation: requesting a stop and observing that
the operation has stopped are different events.
[Task cancellation](https://learn.microsoft.com/en-us/dotnet/standard/parallel-programming/task-cancellation).

## Registered work

Registration is at the operation owner, once per accepted workflow; helper calls
and nested SQL/network steps update that item instead of adding children.
User-requested work registers immediately and automatic passes register when
actual work is accepted. Delivery is the ordinary coalesced one; there is no
appearance delay timer. If short completed operations ever produce distracting
flicker, deferral belongs solely to presentation and never delays cancellation
or recovery feedback.

### App-wide operations

| Workflow and owner | Reporting and cancellation |
| --- | --- |
| IMDb download/import/install — [Browser.Information](../../app/src/Library/Browser/Information.cs), [Dataset](../../app/src/Library/Imdb/Dataset.cs) | One operation from acceptance through final cleanup. `CancelDownload`/`Finished` drive it; phases and cancellation per [import execution](imdb-datasets.md#import-execution-and-sql-design). Settings and footer consume the same operation. |
| Explicit metadata batch — [VideoLookup.Fetch/RunBatch](../../app/src/Library/VideoLookup.cs) | One item per accepted batch, including retries and appended entries. Processed/total and the wait reason are captured under the existing gate. `StopBatch` stops admitting entries immediately but ends reporting only when the active request and required saves settle. An old run cannot remove its successor. |
| Automatic metadata enrichment — [VideoLookup.Run](../../app/src/Library/VideoLookup.cs) | A finite enrichment pass registers when work exists, with processed count and unknown total unless cheaply known. When an explicit batch takes precedence, the item shows its owner-confirmed wait. The current automatic pass is cancellable; the provider preference and completed results stay. |
| Media file properties — [FileFacts.Run](../../app/src/Library/FileFacts.cs) | Each nonempty pass registers and reports files inspected; a pass token links with the existing lifetime/context tokens. Stop prevents further file admission; an in-flight Windows property call may need to unwind. The idle worker is never labelled busy. |
| Manual subtitle batch — [Acquisition.Find/StopFinding/Pump](../../app/src/Subtitles/Acquisition/Acquire.cs) | One operation for accepted manual jobs, including restored jobs and confirmed provider waits. It keeps the durable cancellation semantics of `StopFinding`, with a request scope isolated from rescan and interactive work. Processed attempts are reported separately from successful `Found`. |
| Automatic subtitle acquisition — [Acquisition.Pump](../../app/src/Subtitles/Acquisition/Acquire.cs) | One operation for admitted automatic jobs, distinct from manual work because cancellation has a different scope. Newly eligible jobs join the active operation until cancellation starts, so an older blocked job cannot starve them. Cancellation freezes its accepted set and marks its pending jobs with the existing cancelled outcome; it does not switch off the saved automatic preference. |
| Subtitle discovery/rescan — [Acquisition.Rescan/Discover](../../app/src/Subtitles/Acquisition/Discovery.cs), [Reconcile](../../app/src/Subtitles/Acquisition/Storage.cs) | One item for an explicit rescan or a substantial automatic discovery pass. Queue wait is cancellable. An operation-specific token passes through semaphore acquisition, enumeration and identification; global `Invalidate` is not this item's Cancel. Once groups are known, it reports completed over total folders, and commits a folder's scan stamp only after its work is complete. |
| Inspector metadata request — [Browser.RequestInformation](../../app/src/Library/Browser/Information.cs) | Tracks the single accepted lookup with the existing request identity/token and `CancelInformation`; the inspector and footer cancel the same operation. Selection/visibility invalidation applies: reporting does not extend the request's lifetime across navigation. When it delegates to a batch, ownership transfers without displaying two copies of that lookup. |
| Update check — [MainViewModel.CheckUpdates](../../app/src/Shell/MainViewModel/Updates.cs) | Registers only when a network check actually begins. Unknown progress; cancelling stops the current request through its existing timeout/request token. Cache and update preference stay. The daily admission prevents immediate retry after a user stop. |
| Torrent source preparation accepted without an Add dialog — [MainViewModel.Actions](../../app/src/Shell/MainViewModel/Actions.cs), [AddDraft](../../app/src/Torrents/AddDraft.cs) | Reports the accepted group while source previews resolve; cancel uses the existing preview-release/cancel path for that group. Completion means ownership has passed to torrent rows. The preview polling loop does not register separately, and footer Cancel never removes an added torrent. |

For automatic metadata and file-facts passes, cancellation survives an ordinary
refresh notification. Each feature defines the finite accepted set using its
stable work/input identity and suppresses its remaining cancelled members until
explicit retry/refresh or changed inputs for those members. If A/B/C were
accepted and cancelled, discovering D may admit D; it does not re-admit B/C
merely because a global source revision changed. An ordinary snapshot or
metadata notification is not an explicit retry. This policy lives at the
feature owner, with no general revision framework in the registry. Subtitle jobs
already have durable cancelled outcomes, which are reused instead of a second
suppression ledger; reconciliation does not return old jobs to Pending.

There is no generic Retry where the feature has no such action. FileFacts has no
user Refresh command: its cancelled unchanged inputs stay suppressed for the
window lifetime, genuinely new or changed files are admitted, and the next
window lifetime retries. Metadata resumes through its explicit fetch/refresh or
changed eligible inputs. The registry has no Retry command, and no product
setting exists for this reporting.

### Work with a different presentation or cancellation contract

| Work and owner | Decision and reason |
| --- | --- |
| Metadata search/choice in [MetadataDialog](../../app/src/Library/MetadataDialog.cs), subtitle candidates/download in [Acquisition.Choose](../../app/src/Subtitles/Acquisition/Choose.cs), account checks in [AccountDialog](../../app/src/Shared/AccountDialog.cs) | Modal foreground work already has progress and cancellation in the active surface. Shared reporting, if used, uses the same operation observation, and the modal keeps its usable cancel control; an inaccessible footer cannot be its only control. Shared provider methods do not register twice. |
| Connection test — [ConnectionSetup](../../app/src/Settings/ConnectionSetup.cs), [engine ConnectionTest](../../engine/src/Engine/ConnectionTest.cpp) | The staged interaction owns its progress, cancel and restoration. If it continues after navigation, it registers its existing identity and routes cancellation to `connection_test cancel`, retaining restoration until the engine confirms it. |
| Proxy test — [Proxy](../../app/src/Settings/Proxy.cs) | Dialog-scoped engine check; `check_proxy` has no user cancel path, and closing a local observation would not cancel engine work. Dialog feedback stays; true engine cancellation would be a separate protocol change if this became app-wide. |
| Interactive Add preview — [AddDraft](../../app/src/Torrents/AddDraft.cs) | Progress and source-specific cancel stay in the Add dialog; its operation covers any background continuation, not a second footer-owned preview. |
| File-list synchronization — [TorrentSource.Run](../../app/src/Shared/TorrentSource.cs), [FileSources](../../app/src/Shared/FileSources.cs) | Essential membership/consistency maintenance. User cancellation could leave current-file state incomplete, so no cancel pretends synchronization finished. Generation/disconnect cancellation stays. Snapshot polling is no perpetual activity, and Library has no initial-loading status. |
| Database recovery, reset protection and dataset deletion — [MainViewModel.Features](../../app/src/Shell/MainViewModel/Features.cs), [Browser.Information](../../app/src/Library/Browser/Information.cs) | Accepted state transitions complete their cleanup/commit. If prolonged, they report a Finishing observation through the same module with cancellation unavailable and a specific label. A cancel may abort a cancellable wait before mutation, but cannot undo an accepted reset/delete. |
| Accepted subtitle deletion and file publication — [Acquisition.Files](../../app/src/Subtitles/Acquisition/Files.cs), [SubtitleFile](../../app/src/Subtitles/SubtitleFile.cs), [RecycleBin](../../app/src/Shared/Desktop/RecycleBin.cs) | Owned steps of acquisition/removal with their own protection and publication semantics. They report through the parent operation where one exists; a second independent Cancel could imply undoing a committed file change. |
| Library query/filter, image and piece rendering, disk-space sampling, language preparation, association lookup, window placement, pipes and timers | Interaction rendering or supporting infrastructure, not accepted app-wide jobs. Existing off-thread execution/cancellation stays; a footer item for every task would stay permanently busy. |
| Engine transfer, verification, move, preview internals, persistence, networking and pipe workers | Transfer/verification/move belong to torrent rows; preview belongs to Add; the remaining workers are engine infrastructure. No duplicate footer progress and no generic engine task channel. |

## Consumer contract for the status bar

The [UI contract](ui.md) owns the footer's layout, work list and
overflow behavior. The reporting side of that contract:

- The footer observes one collection on the UI thread. It never reads feature
  counters such as `BatchDone`, `BatchTotal`, `HasDataWork`, `DataStatus`,
  `HasSubtitleWork` or `IsScanning` to reconstruct progress. Settings and the
  feature Stop actions use the same operation.
- Every active operation stays discoverable on every page with its own phase,
  bar and cancellation target. Unrelated percentages are never averaged and
  labels never rotate on a timer. Measurable work uses the stock WinUI
  `ProgressBar` Value and Maximum; unknown running work uses IsIndeterminate.
  Confirmed waiting and cancelling states do not imply continuing useful
  progress. [Windows progress guidance](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/progress-controls).
- The overflow list is a second view of the same observations, not a second
  registry. The UI never re-registers an operation when it moves between inline
  and list presentation.
- The UI owns focus recovery when an item completes, full accessible operation
  and cancel names, and restrained announcements for major transitions. Numeric
  samples generate no stream of screen-reader announcements. Errors stay at their
  established owner/surface. Shared progress text carries no file path, provider
  key or account secret.

## Lifetime and shutdown

One operation covers the accepted workflow across page changes and intermediate
awaits. It registers before a long queue wait and disposes after its outer
cleanup, including temporary-file discard, required saves and release of domain
gates. Registration and disposal do not wait for the UI thread.

During window shutdown, existing feature owners stop admission, request their
own lifetime cancellation and await workers. Only after feature teardown does
the reporting owner release its collection/dispatcher subscription. A failed
dispatcher enqueue cannot strand a feature's completion task. Route changes
invalidate request scopes without confusing their eventual completion with a
new operation using the same display name.

## Review checks

Changes to this contract are checked for: cancellation during each IMDb phase;
preservation of the previous dataset; indexing/optimization failure cleanup;
cancel versus commit races; a late report from an old run; disconnect and
shutdown; cancellation while queued; independent simultaneous jobs; dynamic
batch totals; and cancellation of an automatic pass without immediate restart.
English/Spanish, narrow widths, scaled text, keyboard focus and screen-reader
output are reviewed with the UI owner. Performance claims rest on phase timings
and UI responsiveness measured during a real import.
