# Review decisions

This is the reasoning behind the [consolidated proposal](README.md), not an
additional set of requirements. A recommendation is selected for what it
simplifies, not for how many reviewers supported it. The useful source evidence
is retained in [Evidence](evidence.md); the original review folders are not
required to understand or implement these decisions.

## Inputs reviewed

The names below identify historical inputs, not documents an implementer needs
to retrieve. Their original text was recorded in Git commit `0e887ba`; the
retained destinations below contain the useful conclusions with corrections
and later owner decisions applied. Old line counts, proposed types and claims of
completed fixes are not acceptance criteria.

| Historical input | Retained contribution and destination |
| --- | --- |
| Opus 1 initial review | Settings duplication and dialog slot: [source evidence](evidence.md#settings-and-editors). Shared draft, typed-message and source-intake alternatives: [dispositions](#disposition-of-all-proposal-groups). |
| Opus 1 architect A | Connection facts, selection and schedule ownership: [target owners](README.md#keep-the-product-boundaries), corrections below and [source evidence](evidence.md#settings-and-editors). |
| Opus 1 architect B | Binding, numeric controls, small duplicated rules and diagnostic limits: [binding plan](README.md#let-binding-remove-mechanical-synchronization), [diagnostics](README.md#keep-diagnostics-proportional) and dispositions below. |
| Opus 1 owner-standard review | Settings departure and pending input: [submission rules](README.md#submitted-input-is-not-the-current-draft); rejected enum, prompt and pooling proposals: dispositions below. |
| Opus 1 final consolidation | Final ownership and issue relationships, corrected by owner rulings: [delivery](delivery.md#github-issue-map). Superseded open questions are not retained as decisions to reopen. |
| Opus 2 findings | UI update call paths and previous measurements: [update evidence](evidence.md#snapshot-and-table-updates) and [measurement limits](evidence.md#earlier-measurements). |
| Opus 2 state review | Projection, notification ownership, dead search and update triggers: [publication design](README.md#publish-meaningful-changes-once); rejected extra change tracking: dispositions below. |
| Opus 2 platform review | Queue delay, native focus work, inspector row replacement and layout motion: [source evidence](evidence.md#snapshot-and-table-updates) and [focused verification](evidence.md#verification-still-needed). |
| Opus 2 owner review | Stable list order, daily update checks and batching dependency: [publication design](README.md#publish-meaningful-changes-once) and second-pass corrections below. |
| Opus 2 final architecture | Ordered performance work and preserved animation: [delivery slice 4](delivery.md#4-remove-redundant-publication-and-drawing). Overstated lag and snap-count claims are corrected below. |
| Astra index | Four structural proposals and deduplicated issue relationships: [issue map](delivery.md#github-issue-map). |
| Astra complete review | Both architect perspectives and the owner-priority assessment: [dialog](README.md#one-complete-modal-lifetime), [selection](README.md#accept-selection-and-inspector-changes-together), [committed edit](README.md#share-the-complete-committed-torrent-edit), [history](README.md#encapsulate-only-genuinely-cohesive-engine-state), [native evidence](evidence.md#native-transactions-and-history) and deferred dispositions below. |

The active architecture, engine, protocol, localisation, interface, naming,
testing and TableView source contracts were used to check proposals against
their existing responsibilities. Current code was inspected where the reviews
disagreed. These checks are source reasoning, not runtime verification.

## Important corrections

### Settings should replace the duplicate Limits editor

Astra preserved modal and immediate edit lifetimes as meaningful distinctions.
That is generally true, but it did not adequately account for the owner's
reported Settings-navigation defect and the current search/scheduler routes.
The two Opus architects make the stronger product case: remove the second
editor, then the artificial requirement to share four-field modal submission
with individual Settings edits disappears.

The owner subsequently selected that change, and the current interface contract
now requires it. The proposal follows that decision and keeps Restart accessible
from the remaining modal editors; it no longer treats Limits removal as open.

### A dialog closes before its interaction necessarily finishes

Opus's slot completed at dialog closure. Astra correctly identified that the
Remove interaction continues through command settlement and cleanup. The chosen
slot represents that complete lifetime. Likewise, draft presence alone cannot
identify which suspended editor should be restored; retain that interaction.
Native occupancy is shorter: a detached prompt can hand its slot to a required
confirmation within the same unfinished operation. Completing the operation
early and retaining a closed prompt as its native occupant are both wrong.
Private members versus a private value is an implementation choice, not a
principle worth forcing into the design.

### Five booleans are not automatically one state machine

Opus A treats connection, loading, storage health, startup readiness and
writability as mutually exclusive phases. Current
[MainViewModel](../../../app/src/MainViewModel.cs) shows that `_ready` is a
startup-presentation latch, while `_writable` is derived from engine facts.
Local closing and the last known engine status have other lifetimes. Remove the
derived copy and separate operation feedback, but do not compress independent
facts into one enum. A displayed phase is a projection of those facts.

### Table selection does not replace product membership checks

Opus A would delete model membership reconciliation because TableView prunes its
selection. Its source is the visible projection, which may exclude a live
torrent, and control lifetime differs from model lifetime. Astra's accepted
selection owner is the safer simplification: remove the window's duplicate
rollback state, retain model reconciliation, and distinguish an unavailable
inspector draft from selectable command targets.

### Binding needs a real notification source

Opus B's constant-argument `Text.Get(...)` binding is supported syntax.
[Strings](../../../app/src/Services/Strings.cs) has no notification of its own,
but the later Opus 1 review inspected generated bindings and found that the
existing parent publication propagates down the path. The first consolidation
unnecessarily required a new notification source. Preserve the language Publish
path and verify a representative live switch; narrow routine snapshot work
independently. Native [function bindings](https://learn.microsoft.com/en-us/windows/apps/develop/data-binding/function-bindings)
also support function-name notifications if correction at the owner is needed.

### Performance evidence has boundaries

Opus 2 distinguishes code-path evidence from earlier measurements. Retain that
distinction: bare ObservableCollection timings omit the attached control, and
paused torrents without peers do not represent every active-library workload.
The repeated full capture per collection notification is enough reason to batch
publication. It does not justify a made-up latency saving, off-thread XAML work
or a generalized performance subsystem.

The final Opus 2 review improves both earlier Refresh proposals: retain a
window-only notification for routine work and broader propagation for rare
cross-owner changes. Remove repeated child refresh, without a maintained list of
every computed property. The owner's readability goal justifies that simpler
coordination; it does not justify adding comparison machinery to every row.
Keep batching first among performance changes because it removes amplification.

Both later reports improve the initial batching proposal: replacing a read-only
list only on an actual sequence change is simpler than adding a notifying batch
collection. Adopt that. They also correct the earlier search-keystroke claim:
the actual global-search binding is Query, not the unused table Search filter.
Queue settling, layout motion and focus churn require distinct checks; a busy-
thread probe cannot establish those visual or interaction outcomes.

### Leaving Settings is different from cancelling a draft

The review found that OnFieldDeparture committed only when focus stayed inside
the page, and Navigate could reject Back while that save was pending. The
[owner rulings](../../interface.md#committing-edits) settle the product behavior;
the [submission design](README.md#submitted-input-is-not-the-current-draft)
preserves the requested navigation and distinguishes new typing from explicit
commit intent. These are separate from the explicit-editor prompt in delivery
slice 2. The proposed two-button replacement is rejected: changing the contract
solely to fit today's shorter prompt is not simplification.

### Second-pass corrections to the consolidation

The two final source documents arrived after the first consolidation. Checking
their claims against current source exposed concrete gaps in the proposal:

- [Updates](../../../app/src/MainViewModel/Updates.cs) checks its one-day deadline
  only when ObserveUpdates runs. A setting-change-only trigger would prevent
  repeat checks in a long-lived window. Keep an explicit snapshot trigger and
  prompt enable/disable handling, using the existing in-flight guard.
- [Finding](../../../app/src/MainViewModel/Finding.cs) uses stable OrderBy over
  Torrents. Its list preserves the tie order for completed torrents, whose
  QueueOrder values are equal. Keep a plain ordered list and its lookup index
  under one owner; deleting the list would not remove a duplicate authority.
- Batching must precede the Queue settling exemption. Otherwise the existing
  many-Move path loses its temporary hold and performs more expensive rebuilds.
- Deleting the second queue JSON read does not justify deleting eligibility
  invalidation. A last downloading row can complete without changing the visible
  sequence or global CanReorder. Keep the [eligibility refresh](README.md#publish-meaningful-changes-once).
- #48 already selected separate fixed-width rate fields. Preserve their grouping;
  relocating all variable text to the trailing area would contradict that choice.
- General's downloaded/remaining formatting and stale Inspector context guards
  need explicit preservation when making peer/tracker instances stable.
- The #106 responsiveness probe was temporary, not a checked-in tool. Do not tell
  an implementer to run a nonexistent harness or import the old timing result.

The second pass also removes mandatory per-row comparisons and a new hidden-page
gate. Quiet unchanged settings and stop parent-to-child fan-out first. A loaded
control receiving a genuine change is not by itself a defect. The narrower
proposal still permits a later measured optimization; it no longer requires
extra state before the simpler changes have had a chance to work.

The final Opus 2 architecture calls the three lag causes proven. The source paths
are established, but that does not reproduce the user's lag or measure its cause.
Likewise, 15-minute snapping limits distinct positions, not the number of redraws
in an arbitrarily long back-and-forth drag. Neither claim establishes that local
element reuse must be deleted. Keep the earlier proportionate reuse decision.

## Disposition of all proposal groups

| Proposal | Decision and reason |
| --- | --- |
| Settings owns settings; delete raw settings cache | **Adopt.** One UI owner for confirmed settings, parsing and submission removes actual duplicate knowledge. |
| Delete Limits dialog | **Adopt as a deliberate behavior change.** Use the same Settings target from every entry point. |
| Field kinds and Settings categories | **Adopt minimally.** An enum and explicit field metadata replace repeated name tests; no class hierarchy or generic settings schema. |
| Theme and language | **Unify settings ownership, retain distinct behavior.** Theme can wait for confirmation; language must publish locally before persistence and coalesce choices. Neither needs a competing settings store. |
| Preserve newer input after save acknowledgement | **First priority.** #112 concerns lost input, not optional cleanup. |
| Settings on page/window departure | **Follow the owner's current decision.** Save valid input, restore the saved value for invalid ordinary input, and never ask about a setting. Preserve close admission and IME/Cancel semantics. #120 is distinct from #112. |
| Two-button discard prompt for explicit editors | **Retain the active contract.** Concrete Save/Discard/Cancel applies where saving is meaningful; this is separate from prompt-free ordinary Settings fields. |
| Extract schedule ownership | **Adopt a concrete transaction owner.** Moving 330 lines into a partial alone does not encapsulate state; no new project or generic editor abstraction. |
| One dialog slot | **Adopt with complete settlement and recovery.** Remove repeated membership lists while keeping native lifetime in the window. |
| Move IsAddOpen to AddDraft | **Narrow.** The window owns actual modal visibility; AddDraft owns preview demand. Express their handoff directly without duplicating open flags. |
| Move all source intake into AddDraft | **Reject.** Native picker configuration and model admission/acknowledgement have different jobs. Preserve the close handoff fixed under #13. |
| Universal draft interface/list | **Reject.** Different pending and discard scopes still require explicit decisions; a shared interface adds ceremony. |
| Draft naming | **Apply with touched ownership.** Prefer established draft vocabulary, but Cancel may also release preview/file-operation resources. Do not mechanically rename different operations as if identical. |
| One selection transition | **Adopt.** Model accepts selection and inspector consequence; remove the window rollback copy. |
| Delete all model selection pruning/current state | **Reject as a blanket change.** Reconcile product membership. Remove `_current` only if its actual consumers disappear with a separate product change. |
| One connection enum | **Reject the proposed flattening.** Derive writability and feedback from correctly owned facts. |
| Separate connection and command feedback | **Adopt.** An unrelated old failure must not describe a new disconnection; centralize formatting in Strings. |
| Change when later success clears an error | **Retain current policy in this refactor.** Feedback separation does not require a new outcome-history system. |
| Factory/register every command in a list | **Do not adopt globally.** Refresh only relevant availability groups. If a group's repeated enumeration remains error-prone, a private list is enough; no command registry framework. |
| Refresh every owner on every snapshot | **Use window-only and broad propagation paths.** Keep simple local notifications; stop refreshing every child routinely. |
| Batch visible projection publication | **Adopt.** One source update for one completed membership/order change; no source replacement for unchanged telemetry. |
| Read-only list versus custom batch collection | **Choose the read-only list.** It uses the existing TableView source path and removes incremental loops without adding a collection type. |
| General row change detection | **Not required.** Existing realized-row notification stays. The concrete reorder-eligibility comparison remains necessary; it needs no shadow snapshot or property dependency map. |
| List and dictionary membership representation | **Keep both under one owner.** A plain list preserves stable order; the dictionary is its identity index. Remove unused collection notifications, not ordering semantics. |
| Table Search state and repeated one-time setup | **Remove dead state and fix empty-state text.** Global Query is not a filter; placement is a transition, daily update checks still need periodic opportunity. |
| Remove the two queue/table sorts | **Reject.** Natural source queue order and selected display sort are distinct responsibilities. |
| Queue column held by the settling interval | **Exempt DefinesRowOrder after batching lands.** Amend the contract; apply confirmed host order promptly while preserving live-value settling. |
| Focus and hosted-selection churn on unchanged rebuilds | **Fix inside TableView.** Skip unnecessary native work while retaining logical reconciliation, changed-instance handling and gesture rules. |
| Status controls / graph shift with changing label width | **Follow #48's fixed-width rate fields.** Preserve rates/limits grouping. Keep the graph scale label right-aligned above the plot, removing its influence on the plot's horizontal position. |
| Instant live-sort settles with animated arrivals | **Defer the animation change.** Retain current contract until a focused comparison and simple mechanism justify revising it. |
| Hidden/unrelated scheduler redraws | **Fix at the notification source first.** Quiet unchanged settings and remove parent fan-out; no mandatory visibility gate. Retain invalid-gesture cancellation. |
| PeriodDraft setters versus SetSpan | **Unify mutation semantics.** Publish coherent time changes through one narrow path; no duplicate schedule state. |
| Delete visual reuse / add separate pools | **Neither by default.** Keep straightforward local reuse with complete initialization; justify a different arrangement by clarity and interaction evidence. |
| Stable peer/tracker rows | **Adopt the existing Torrent pattern in Inspector.** Avoid replacing every bound row per reply; no flicker claim without observation. |
| File-node/pieces optimizations | **Defer to a focused remaining bottleneck.** Notify changed leaves/ancestors if needed; earlier results do not support a wholesale detail-model rewrite. |
| Background every synchronous UI call | **Reject.** Preserve measured small one-off operations unless new evidence identifies an input stall. Framework layout remains on its owning thread. |
| Bind translated labels | **Adopt using the existing deliberate language publication.** Verify generated/live bindings before adding another notification source; preserve menus, accessibility, focus and header refresh. |
| TwoWay-bind all selections | **Narrow to synchronous value synchronization.** Draft guards and asynchronous commit/refusal remain explicit. |
| Adapter choices in view model | **Adopt when touching Settings.** Choices and unavailable-selected-value state are presentation data; native picker/focus work stays in the view. No discovery service is required. |
| Replace every NumberBox with TextBox | **Reject blanket replacement.** Raw-text drafts can use TextBox; retain useful native stepping elsewhere. |
| Repeated setting-row templates | **Use only for identical behavior in the Settings slice.** Avoid a row generator, preserve accessible names and focus targets. |
| Typed messages for every reply | **Reject blanket conversion.** No new evidence reverses #12; records do not validate cross-process wire keys by themselves. Use a concrete value only where it removes repeated interpretation. |
| Remove optional-field fallbacks | **Inspect against protocol when touching readers.** Required fields should fail explicitly; genuinely optional payloads retain their defined handling. No source-wide search-and-replace. |
| One torrent error predicate | **Adopt in the list/feedback slice.** Counts and filters use the same Torrent concept; moving the error presentation to Inspector is a separate UI choice. |
| Shortcut registration and displayed hints | **Adopt one definition when touching chrome.** Execution and localized hints must agree; no generic command system is needed. |
| Dead language command, unused keys, direct shell/clipboard path | **Remove demonstrated orphans in the affected slices.** Search current callers; do not treat old counts as proof. Native actions need no pipe/service layer. |
| Reorganize MainViewModel partials | **After behavior consolidation, if navigation improves.** Group state with its decisions; avoid class-per-command or file-length rules. |
| Capture mode enum | **Adopt locally when diagnostics change.** Parse once; enums stay in the project's vocabulary file. |
| Delete capture journeys after recording findings | **Reject that criterion.** Retain valuable regression outcomes; remove redundant or obsolete checks using the testing policy. |
| Separate capture project / exclude Release diagnostics | **No new project; defer packaging choice.** In-process capture has a platform reason and the owner uses Release for evidence. |
| Shared committed merge/Edit operation | **Adopt.** Preserve ordered intended fields, one durable combined commit and truthful asynchronous completion. |
| Concrete speed-history owner | **Adopt at low priority.** Encapsulate cohesive state without changing retention or adding a provider/worker. #89's rejected constants cleanup stays distinct. |
| Split all Engine::State responsibilities into managers | **Reject.** Borrowing State everywhere spreads coupling without hiding it. |
| Merge Changes and Store / collapse Store lanes | **Reject.** Product ordering, blocking execution and independent slow work have different responsibilities. |
| Generic polling abstraction | **Reject.** Existing scheduling is shared; preview, snapshot and inspector demand lifetimes differ. |

## Product issues that remain product issues

The source reviews also discuss #22 (Add validation/focus), #25 (language
presentation), #26 (shortcut hints), #29 (error discoverability), #48 (status
rates), #52 (access keys) and #78 (Settings layout). The selected owners make
those changes easier, but a structural refactor does not complete their user
journeys automatically. Inspect each current issue and implementation before
claiming it resolved. In particular, no automatic layout, accessibility or
responsiveness claim follows from fewer lines of code.

## Source checkpoints for implementation

- [MainViewModel](../../../app/src/MainViewModel.cs) and
  [Actions](../../../app/src/MainViewModel/Actions.cs): settings cache,
  connection facts, source handoff, selection and commands.
- [Workspace](../../../app/src/MainWindow/Workspace.cs): selection rollback,
  native prompts and guarded navigation.
- [Settings](../../../app/src/Views/Settings.cs): field acknowledgement,
  conversion and schedule transaction. `SpeedLimits.cs` was a duplicate editor
  at review time and is already being removed; do not recreate it from an old
  review or treat its absence as missing documentation.
- [Finding](../../../app/src/MainViewModel/Finding.cs) and
  [TableView Source](../../../lib/TableView/src/Body/Source.cs): publication
  granularity and full source capture per notification.
- [Week](../../../app/src/Controls/Week.cs): broad model subscription, schedule
  signals, visibility and gesture lifetime.
- [Merge commands](../../../engine/src/Engine/Commands.cpp),
  [Edit](../../../engine/src/Engine/Edits.cpp) and
  [SpeedHistory](../../../engine/src/SpeedHistory.cpp): native consolidation seams.

These links identify inspected owners. Concurrent implementation can move them;
the behavior and ownership argument is the evidence, not a permanent line number.
