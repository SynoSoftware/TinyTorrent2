# TableView performance plan

> **Current status: open.** Commit 171674a recorded this plan as complete. A
> review against the plan as approved at db12cfd found that the plan is not
> complete. Stages 5 and 6 were built as a second pipeline beside the whole
> rebuild, where the plan says "no second pipeline is written beside it". That
> second pipeline has defects:
> - a duplicate row and a `KeyNotFoundException`;
> - a lost settle for string keys;
> - edits applied to a pinned view;
> - a same-identity Replace that moves inside the settle interval;
> - a hierarchy `StructureChanged` that is always true.
>
> The work is done in three batches, in this order:
> 1. Stages 5 and 6 as one pipeline. This batch also fixes the defects above and
>    the review findings in the same code (A1, A3, A4, A7, A8, A9, A10, A11, F4).
>    Then the table slides reordered rows itself. WinUI 2.6.4 has no move
>    for an `ItemsSource`: only the list's own drag-reorder plays its reorder
>    transition (`ListViewBase::ReorderItemsTo`). So a row that a sort settle
>    or a host `Move` reorders is removed and inserted, and it fades out and in.
>    The table turns off that fade for a reinserted row and moves the row from
>    its old place to its new one, as `Motion.SlideFrom` does for columns.
> 2. Sorting follow-ups:
>    - A5: remove the measurement-only state from production sorting.
>    - A6: make the sort commit sequence one routine.
>    - F6: move the first-sort UI work off the UI thread.
>    - F7: measure the memory that each row keeps. Measured with
>      `RetainedMemoryTests.BytesPerRow`: the table's own fields keep 583 bytes
>      per row sorted by names and 545 by numbers, about 1.2 MB at 2,000 rows.
>      The owner's lead keeps this trade: most of it is buffers a publish needs,
>      kept between publishes, and releasing the placement buffers cost 15–17%
>      more cycles on a sorted Move at 100,000 rows.
> 3. One measurement home (A2) and Build.ps1 (A12). Both are done: the sample's
>    `Performance.cs` and its sections are deleted, so `PublishCostTests`,
>    `SelectionCostTests` and `RetainedMemoryTests` are the measurement home.
>    The history below names those sections as they were.
>
> Rules for every batch:
> - Nothing is added to the public API. Each batch leaves fewer internal seams
>   between the pipeline types than it found.
> - Each batch shows, in thread cycles before and after, that it is faster.
> - A row that is on screen before and after a change moves to its new place.
>   This includes a row that a sort or a host `Move` reorders. It never fades
>   out and in, and it never flashes.
>
> F3 is settled by the owner's goal: when there is nothing to animate, the rows
> swap at once. F5 stays as it is.
>
> **Batch 1 status:** implemented, and reviewed in four rounds until no high or
> medium finding remained (`batch-a-review-r1.md` to `r4.md`). Every audit
> defect is gone, and the equivalence test passes in every mode. Five
> alternating run pairs against 6726d9f show every case at or below the old
> code except four small ones (+0–4%: a switch to a different list at 20 and
> 200 rows, an unchanged refresh at 20 rows, and Replace at 1,000 rows), which
> are being traced. A flip to a new ListView source for a list with no row in
> common was built and removed, because WinUI drew one empty frame. The owner
> decided that such a switch resets to the top: the table swaps in place and
> sets the offset to 0 in the same publish. This is implemented (contract 5.3)
and waits for review.
>
> **Step 1b status:** implemented and reviewed in two rounds
> (`step-1b-review-r1.md`, `r2.md`). Reordered on-screen rows slide (Sliding.cs).
> No staying row fades in any paint scenario. Each staying row's largest
> single-frame step is its slide's own first step. Two jumps remain:
> - An Add or Remove within 1 s of a reorder jumps. WinUI's fast-mutation rule
>   causes this, and the old code jumps the same way.
> - A reorder during the list's own Add or Remove transition (M3) starts from
>   the layout slot. In the test it jumps about 4–8 px; the size depends on the
>   timing. The owner decides between keeping it and the options in
>   `step-1b-review-r2.md`.
>
> A reorder publish costs more in frame cycles: +12% to +56% in five pairs
> against 51ece76. That cost comes from the composition calls for each sliding
> row and from WinUI recycling containers inside the publish. The owner decides
> whether to accept it.
>
> **Final review status:** the final review
> (V:/temp/TableViewPerformance/final-review) found medium defects in the reset
> (a notification during a hold dropped the host's `ScrollTo`; an emptied and
> refilled list reset to the top), a slide after a switch in the same frame,
> and two costs: a pointed row under a sort made each publish O(n), and a child
> notification under a collapsed parent ran a whole publish. The fixes were
> committed as 673c022. Its delta review (`final-review/delta-1.md`) found four
> paths the fixes left open: a pointed row displaced by a settle, a `ScrollTo`
> on a shown list, a row-by-row refill, and a sorted Replace that crosses rows.
> They are fixed in source: the frame now decides the reset once. One
> measurement round at the end of the plan follows.
>
> Review records: V:/temp/TableViewPerformance/review-db12cfd (spec,
> paths-flow, architecture, audit-flat, audit-hierarchy, and the approved plan
> text `plan-db12cfd.md`). Each
> completed batch gets one independent adversarial review. After the last
> batch, a deep review with six specialists checks the integrated result.
> Earlier evidence: V:/temp/TableViewPerformance/sorted-notification-evidence.md.

### Final correction results

- Independent final review: `sorted-notification-review-r2.md`, all five passes
  clean. The earlier marquee, convention and comment findings were corrected
  and validated before this correction.
- Combined Release build of the sample and test projects: 85.363 seconds,
  zero warnings and errors. Focused sorting and selection tests: 83 passed,
  none failed, skipped or aborted; the runner exited normally.
- The same measurement assembly drove the preserved pre-correction library and
  the corrected library. At 2,000 displayed rows, 64 withheld arrivals with
  1,000 held leavers took a median 31.2858 ms before and 4.2620 ms after
  (7.34 times faster). Distinct-key arrivals improved about 9.6%; equal-key
  arrivals were essentially unchanged. Every measured batch preserved order.
- These measurements isolate this final correction, not the complete A-E
  change against the original implementation. Actual held-membership changes
  still copy held membership for transactional acceptance; custom comparers,
  recovery and global settling retain their documented full scope.
- Both benchmark processes and the test runner exited normally. Final process
  inspection found no TableView sample, test executable or testhost left running.

### Reopened stage 5: settled correction

*Superseded by batch (a).* What this correction settled now stands as
follows; the implementation map owns the details.
- Still in force: `SortedOrder`'s candidate/accept boundary; rows that arrive
  or move are placed by binary search among the rows that stay, which are
  copied in native blocks; source-index shifts are deferred and rebased at
  about √n; a custom comparer without key equality first proves the kept
  order before placement (contract section 9); unchanged held membership
  keeps the accepted set, copied only when a held row changes.
- Replaced: the reuse of the pinned presentation for a preparation without
  edits. The order's producer states how its order differs from its previous
  version when it knows, and the view applies that difference only when it
  shows that version; otherwise it compares itself with the new order by one
  reference walk (stage 5's bound gives the costs).
- The files under `V:/temp/TableViewPerformance/` from this correction
  (design, review rounds, the preserved `sorted-before` sample) describe the
  replaced implementation. Batch (a)'s baseline and final measurements are in
  `V:/temp/TableViewPerformance/batch-a/`.

**Open.** This plan keeps TableView responsive with medium and large sources:
10,000 to 100,000 rows that the host updates often, usually under a sort.
Scrolling, selection, sorting, filtering, updates and every other interaction
stay responsive as the row count grows, and processing cost follows the work
actually required, not the size of the source. Memory is a stated trade: the
table keeps the buffers a publish needs between publishes, at the size of the
largest publish, because allocating them again costs cycles on every large
publish (F7). The
[TableView contract](../../lib/TableView/docs/tableview-contract.md) owns the
behavior. Each stage below says which contract text it changes. When a stage
ships, its rules move into the contract and the stage leaves this plan.

Sources, each checked against the code before it was listed here:
- the owner's own review agent: F1–F6;
- the Fable review: R1–R18, corrections C1–C4, host patterns H1–H5, limits
  L1–L7, and the WinUI 3 list evidence E1–E6 for the no-flash ruling;
- the Opus review: local fixes O1–O7, redesigns P1–P6, second-pass findings
  N1–N11 and corrections A1–A4, and a third pass on the WinUI 3 list source
  (W1–W5, B1);
- cold reviews the owner ran: K1–K11, Q1–Q11 and S1–S8;
- a comparison with Microsoft's TableView in `3rdParty/microsoft-ui-xaml`
  (`controls/dev/ShapedItemsSource`): M2.

TableView is a generic control, so every stage changes the table, never a
host. The product's hosts (the torrent list, the Library, the file browser)
appear only as evidence of real workloads. Work a host could do to call the
table less often belongs to that host, not to this plan.

A cost marked **estimate** comes from reading the code. Nobody has measured
it yet. Where reviews estimate the same cost differently, the range covers
both.

## The table never flashes

Every stage keeps the contract's owner ruling in section 5.3: the table never
flashes and never breaks the list's animation, so the view never raises
`Move` and never resets the list. A stage
that changes the reconciliation or the notifications the view raises (stages
3, 4, 5 and 9) is accepted only after a code review of the table and of
the WinUI 3 list source in `3rdParty/microsoft-ui-xaml/src/dxaml`, and a visual
test that shows the rows still animate and nothing flashes. This holds for any
idea a review proposes, however much it saves.

**Owner ruling, 2026-11-01:** the owner switched the inspection checkout to
`winui3/release/2.6.4` to match the installed release, specifically to trace
animation and prevent flashing. The lead verified the exact tag, clean
checkout and commit `c29edd64ae8b4412d772c5768d835ebfd19b438e`. This source
is for inspection only: no agent edits or builds it. It does not change the
TableView project's package references or invalidate accepted TableView
builds/tests. C rechecks the native assumptions against this tag before
implementation; its final visual check still proves the actual built runtime's
animation and no-flash behavior.

The following native findings were recorded against the previous inspection
commit `8da148dcd`; C's dependency preparation revalidates the affected paths
at `2.6.4` and records any changed conclusion (current files under
`src/dxaml/xcp/dxaml/lib`):
- **E1.** The list chooses the animation per layout tick from the number of
  inserts and removals in that tick
  (`ModernCollectionBasePanel_ICTCP_Partial.cpp`,
  `TransitionContextManager`), not from their order or indices. It distinguishes
  single, multiple and mixed changes, so preserving only event kinds is not
  enough to infer identical choreography when a sparse script changes a
  category. Record that change and judge actual motion and gaps; do not add
  artificial events solely to force the old counters.
- **E2, E3, revalidated and corrected at 2.6.4.** Each `VectorChanged` is
  handled inside the call: the panel reads the change kind and index and
  updates its containers. Insert/remove bookkeeping does not retain event
  arguments for later replay. Replacement also generates and prepares the
  current row synchronously (`OnDataElementReplaced`, lines 1938–2060),
  including focused and pinned containers. So the view and its identity/index
  answers must match each event when it raises it; building the final list
  first and raising events afterwards cannot work.
- **E4.** A reset recycles every container, pinned ones included
  (`OnCollectionReset`). That is the flash.
- **E5.** Every `SelectRange` or `DeselectRange` call makes the list call the
  view's `GetSelectedRanges()` and then refresh every cached and pinned
  container from `IsSelected(index)`, whatever range the call named
  (`Selector_Partial.cpp`, `InvokeDataSourceRangeSelection`).
- **E6.** An event raised during layout, or while the list is handling
  another, is a guard failure. The table raises events only from
  `RebuildView`, one at a time, and every stage keeps that.
- **W4.** One off-screen event costs a constant amount of native work: the
  panel shifts the indices of its realized and pinned containers and sets a
  measure flag. Nothing costs O(n) and nothing runs a layout. The 56–113 µs
  per notification in the `Body.View.Reconcile` remarks included layout, so it
  does not price an off-screen event.
- **W5. The list does not animate a change that comes less than 1 second
  after the previous one.** `AddDeleteThemeTransition` skips its storyboard
  while `IsCollectionMutatingFast` is true: the previous layout tick that
  carried a collection change was less than `fastmutationThreshold` (1,000
  ms) earlier (`ModernCollectionBasePanel_ICTCP_Partial.cpp`). The source
  comment gives the reason: interrupted add and delete animations look very
  bad. Such a row appears or disappears at once, without a flash: the other
  containers stay. No stage of this plan changes how often this happens. See
  the limits below.

## What already scales

Scrolling, column resize, column moves, fit, the marquee, pointer movement and
`RealizedItems` cost work in proportion to the realized rows (40–80), not to
the row count. Memory does not follow the work: a sorted table with a key keeps
583 bytes per row in its own fields (545 sorted by numbers). About 210 to 245
are needed at rest: source membership, the view's positions and rows, and the
sorted order's positions and keys. The rest is publish buffers, kept by the
trade F7 states. The problem is the
work done again on every update, and a few gestures that walk every row or
every selected row.

## Where the cost is

A publish is one run of `Table.RebuildView`. It runs after every source
notification, every `RefreshView()` and every settle tick.

The torrent list makes this a cost per engine update. After every engine
snapshot in which any torrent changed, it calls `Torrents.RefreshView()` under
every sort except ascending queue order (`MainWindow`, the `SnapshotApplied`
handler). So under a name or speed sort, the whole pipeline runs on the UI
thread on every update, even when no row moved. The inspector's peer and
tracker tables do the same on smaller sources (H5).

One publish of n rows, flat table, local sort:

| Step | Owner | Work for n rows |
|---|---|---|
| Check row type, key and duplicates | `Table.ValidateRows` | One `HashSet` of n; the key selector runs twice per row |
| Hold the rows under the pointer | `Table.Hold` | While the pointer is over the rows: two `HashSet`s and one list of n |
| Keep the shown order inside the settle interval | `Table.KeptOrder` | One `Dictionary` and one list of n |
| Freeze the pointed row | `Table.Pin` | An array and a list of n, and a linear search for the pointed row |
| Sort, when the interval ended or a row arrived or left | `Table.SortedSnapshot`, `Schema.SortKey` | LINQ `OrderBy` over all rows, then `.ToList()` |
| Record each row's index | `Table.AcceptOrder` | One `Dictionary` of n (`_positions`), built before the check below |
| Skip the reconcile when nothing changed | `RebuildView`'s `order.SequenceEqual(View)`; `Body.View.Shows` checks again only after that fails | n reference comparisons; the selection steps below still run on the skip path (A4) |
| Index the new order | `Body.View.Reconcile` | One `Dictionary` of n (`place`), the same content as `_positions` |
| Apply arrivals and removals | `Body.View.Membership` | Each `Insert` or `RemoveAt` shifts every later row |
| Carry the selection to the new instances | `SelectionState.Reconcile` | All n rows; up to nine key-selector calls per row (C2) |
| Publish the selection | `Table.CommitSelection` | Sorts and copies the packet twice, builds two sets of it, and makes every realized row update its states, even when nothing changed |

Every hash and every identity comparison calls the host's key selector, and
nothing keeps the key between steps: 10–20 selector calls per row per
publish. With a value-type key, such as the Library's `long` entry id, every
call also boxes the key.

**Estimate.** At 100,000 rows: 25–100 ms of UI thread and 14–25 MB of
garbage per publish when nothing moved. At 10,000 rows: 2–10 ms and 1.4–3 MB.
Most of that garbage is on the large object heap, because each n-sized
structure passes 85,000 bytes at about 3,500 rows, and only full collections
reclaim it. At one publish per second and 100,000 rows, that probably means a
full collection every few publishes.

A host that raises one notification per row, such as an
`ObservableCollection` filled in a loop, pays the whole table once per row:
appending 1,000 rows to 100,000 runs it 1,000 times (F1, K8).

## Owners

Every stage changes one of these owners, or adds to it. A coder never creates
a second owner for a concept below, never writes a second implementation of
one, and never makes a decision listed here outside its owner. A concept a
stage needs that this table does not list is a change to this plan, decided
before the code is written. The table names owners, not new types: a stage
may implement an owner as an existing class.

Four rules apply to every row:
- **State flows one way.** An owner publishes its state or a version of it,
  and the code that depends on it reads it. An owner never calls into code
  that depends on it to make it load, refresh, reset or cancel, because each
  new writer would then have to remember that call. `Table.RebuildView` is
  the one exception by design: it orchestrates the publish and calls its
  steps in order (validate, order, reconcile the view, reconcile the
  selection, commit, the hierarchy's steps, raise `RowVisualsChanged`),
  because the contract requires each publish to finish all of them before
  it returns. The rule governs every call outside that sequence.
- **A condition names its decision.** Code that guards work reads the
  owner's answer, never a nearby fact that usually agrees with it.
- **The core holds no extension state.** The core owners hold none of the
  hierarchy's or the drag's state. `RebuildView` may call the hierarchy's
  steps, and the pipeline may branch on whether a hierarchy exists, as it
  does today.
- **Dependencies point down this order:** the identity part; then
  `Body.Source` and `Body.View`; then `SelectionState`; then the table's
  pipeline. `Body.View` answers `ISelectionInfo` through the delegates the
  table hands it, as it takes `IsSelected` today, so it never depends on
  `SelectionState`.

| Concept | Owner | Replaces, or must not be confused with | Written by stages | Read by | Does not |
|---|---|---|---|---|---|
| Row identity: key read, equality, hash, null-key check; the map and set types every row-keyed collection uses | The identity part. A reference-identity instance exists from `Table`'s construction; schema capture installs the keyed one that `Schema<TRow>.Key<TKey>` creates, as `RehashIdentity` does today | `ItemIdentity` and `Schema.KeyEquality`, which it replaces | 1a | every owner below | Hold any rows; nothing else hashes or compares rows |
| A position index over an ordered sequence: row to index, with a stale window sized from the sequence's own counts of inserts and removals, rebuilt after about √n changes and after any publish that moves rows without an event | One type, with one instance per sequence, owned by that sequence's owner: `Body.View` for the view, the sorted order for the kept order | `_positions`, `Body.View.Reconcile`'s `place`, `Hold`'s `shown`, `SelectionState`'s `indexOf` delegate | 1a, 5 | `Hold`, `Release`, `Pin`, `SelectionState`, `GetSelectedRanges`, the sorted order | Get written twice |
| The new order of one publish and its index | `Table.RebuildView` builds it and hands it to `Body.View.Reconcile`; after the reconcile the view adopts it as its position index | — | 1a | `Body.View.Reconcile` | Get rebuilt by the view |
| The source's rows by identity: membership, current instances and captured typed keys, no positions | `Body.Source`, with accepted and candidate maps; view and selection maps copy captured keys through the identity part's typed map seam | `Hold`'s and `KeptOrder`'s `live` | 1a, 5 | `Hold`, `KeptOrder`, `Pin`, the duplicate check | Hold positions; put row storage in the identity part |
| Any other map or set keyed by row | Its own owner, built from the identity part's map and set types | `HashSet<object>(identity)` and `Dictionary<object, …>(identity)` built in place | 1a, 8 | — | Use a comparer of its own |
| Rows entering the table: null row, row type, null key | `Table.ValidateRows`, a check per row, run on rows that entered: an assignment, a notification's added rows, a hierarchy capture | — | 1a, 5, 6 | — | Exist as a copy in any path |
| Unique identity of rows entering the table | The failed `Add` into the source's identity map for a flat table, or into the node index for a hierarchy, made before the snapshot or the hold is accepted | `ValidateRows`' own `HashSet` | 1a, 5, 6 | — | Get a set built only to check it, or a check of its own in `Hierarchy` |
| Whether the stored snapshot passed validation | `Body.Source`, recorded when the snapshot passed | — | 1a | `RebuildView` | Get assumed from how `RebuildView` was called |
| Which source assignment a capture belongs to | `Body.Source`, carried by each capture, including the accepted capture | A hierarchy invalidation call from the source | 1a, 6 | `Hierarchy`, to recognize a new root assignment | Hold extension state; make a failed candidate change the accepted capture's assignment version |
| Held, released, pointed and menu rows | `Table/Holding.cs` | — | 1a, 5 | `KeptOrder`, `Pin`, the row element | — |
| One entry into the pipeline | `Table.RebuildView`, which takes the change: a whole source, one notification's action, indices and items, or rows whose values changed | — | 1a, 5, 10 | — | Get an `ApplyChange` or other second pipeline beside it |
| Whether one source notification can be applied incrementally, and its prepared membership/entrant/leaver facts | `Body.Source.Preparation`, from the validated accepted predecessor and matching assignment; only `Source.Accept` adopts it | Inference from the notification action or an aliased current list | 5 | The existing pipeline's hold, order, view and selection steps | Clone the full accepted map for one delta; accept a candidate before all throwing preparation succeeds |
| Column sort definition: key selector and comparer | `Column`, set by `Schema.SortKey` | `Column.Sort`, which it replaces | 2 | the sorted order | Hold sort state |
| Sorting: the one stable sort routine (flat rows, and each sibling group of a hierarchy); for a flat table also the kept order before `Pin`, its kept keys and collation keys in arrays, its position index, whether the kept-key check is allowed, and the one placement routine | The sorted order of the active column, in `Table/Sorting.cs` | — | 2, 3, 5, 10 | `KeptOrder`, `SortedSnapshot`, `Hierarchy.Project` | Hold the shown order after `Pin`; place rows of a hierarchy by binary search; get a second sort or placement routine |
| The prepared order and whether preparing it performed a full sort | The sorted-order owner returns this operation's facts; the pipeline consumes them | Permission to move rows, which does not say whether a fallback actually sorted them | 2 | `ViewOrder`, to advance the settle clock after an actual full sort | Store another pending flag or derive the completed operation from the caller's permission |
| Detached worker input, private sorting indices and UI-only associations used to accept or rebase the result | The sorted-order owner; the worker receives only immutable supported keys, base ordinals and sibling ranges, and takes text keys' collation keys itself | Active/candidate workspace buffers and live source/identity maps | 2.6 | The queued-sort owner, whose existing validity check gates completion | Run identity/host callbacks or touch row/UI objects on a worker; add a second sort algorithm or commit workflow |
| Matching a queued hierarchy sort's sibling groups to a newer capture | An operation-local hierarchy-owned capture, with a parent-to-group map from captured node keys and a separate root group | A persistent per-parent key history or hierarchy state in generic sorted-order work | 2.6 | Hierarchy's existing projection/rebase owner | Enter the detached worker payload or add another pending table field |
| A header sort waiting for the frame, and a worker sort's result | One queued-sort field in `Table/Sorting.cs`; that owner publishes a revision for actual committed-sort changes, and `Table` publishes a lifetime revision for actual detachments | `_pendingSort`, which holds a `Sort` assigned before the schema is captured: a different concept, kept as it is | 2 | the header | Get cancelled by other writers: it checks the captured revisions, that the table is loaded and its column is still shown |
| The criterion the header shows | `Table/Sorting.cs`: the queued sort while it is still valid, otherwise the committed sort | — | 2 | `Header` | Get computed by the header |
| Waiting for the next frame | `Table.OnLaidOut`'s `CompositionTarget.Rendering` step | — | 2 | the queued sort, the reveal | Get a second implementation |
| Native transitions temporarily suppressed for a disjoint visible-window replacement | `Table/View.cs`, with one `SuppressedTransitions` record holding the exact surface and original local dependency-property value; eligibility reads final visible rows through the old position index with captured keys | A second animation system, a second reconciliation preview, or changes to native notification counters | C visual correction | Existing version-current post-Rendering dispatcher branch; unload and template detachment restore the captured surface | Clear a shared transition collection, pin a formerly styled value locally, restore before actual layout, or claim isolation from other updates sharing that layout episode |
| Failure escaping deferred frame work | `Table.OnLaidOut`'s one dispatcher callback boundary, forwarded through the existing WinRT error reporter | A raw dispatcher exception that bypasses the host's XAML failure handler | 2 | The host's `Application.UnhandledException` policy | Swallow the failure, add a public failure event, or replace the application's synchronization context |
| The view's rows and the notifications it raises; the view version and its counts of inserts and removals | `Body.View` and its gap-buffer list | — | 4, 5, 9 | `SelectionState`, the list | Raise `Move` or a reset; depend on `SelectionState` |
| Selected set and selected view indices | `SelectionState`; the indices record the view version they were built from and are rebuilt when it differs | — | 1b, 5, 8 | `GetSelectedRanges`, the packet, the marquee | Get told by view writers that they changed |
| The published selection packet | `Table.CommitSelection` | — | 1b | the host, the drag | Know about the host's positions |
| The selected state a realized row shows | The row's container, `ListViewItem.IsSelected`, which the list sets | — | — | `RefreshContainers`, which compares it with the selection model | Get a second record |
| The publish version | `Table.RebuildView`, incremented on every run | — | 1b | `Order`, the drag | — |
| Whether the order allows a reorder | `Order`, recomputed on read when the publish version it recorded differs | `Order.HasMovableRows`'s walk of the host's order on every read | 1b | `Table.CanStartDrag` | Get refreshed by the pipeline |
| Whether the selection holds a numbered row | The drag owner, `Table/Reordering.cs`, recomputed on read when the packet or the publish version changed | the scan of the selection in `CanStartDrag` | 1b | `Table.CanStartDrag` | Get computed by the selection commit |
| Whether a row can start a drag | `Table.CanStartDrag`, from the two facts above | — | 1b | the press, the row element | Get decided again by a row element |
| The `RowVisualsChanged` event | `RebuildView` raises it once per publish, because a publish can change any row's selection, drag cue, interactivity or expander. Outside a publish, a fact's owner raises it when its fact changes: a selection commit from a gesture, the drag gesture, expansion, and `Hierarchy` when a child change alters a shown row's expander | today's raise on every commit | 1b | the row element, `Body.HierarchyCell` | Get raised by one owner for another owner's fact |
| The states a realized row shows | `Body.Row`: it reads the facts on each event and applies only the states that changed, so it is also the record of what the row shows | — | 1b, 7 | — | Get a second record in another owner |
| The drag distance | One internal value read from the Windows setting, read by `Table` and `Header.Strip` | the two `DragThresholdDips` constants | defect Q10 | row and header gestures | — |
| Drag capture and drag membership | `Table/Reordering.cs`: `Order.Capture`'s entries in an identity-part map, compared by reference, so a replaced instance cancels the drag as today; the packet in an identity-part set | — | 8 | `IsDragCurrent`, `IsDragging` | — |
| Hierarchy membership: the node index, child collections, sibling positions | `Hierarchy`. It adopts a new capture whenever the capture is new, whatever the visible order; it computes sibling positions from the order when the order changed; from stage 6 it applies each child notification to the kept node index at every depth | `_positions`' sibling field | 1a, 6 | `HierarchyPosition`, `SortedSnapshot` | Live in the core position indexes |
| A hierarchy child source's captured items, parent associations, current handler and accepted predecessor | One `Hierarchy.ChildSource` per enumerable, retaining all parents even for shared empty sources | Separate source-to-parent maps or sender-based subscription eligibility | 6 | Hierarchy capture, child preparation and observation | Reuse rejected/suspended predecessors or accept an old invocation after root reassignment |
| Atomic hierarchy preparation and captured entrant/leaver keys | Operation-local `Hierarchy.Preparation`, journaling only touched accepted nodes and source relations; existing table acceptance/finally commits or restores it | A full kept-node map clone, a second duplicate validator, or a second publish pipeline | 6 | Existing hierarchy/table preparation and captured-key consumers | Lose removed keys before synchronous native consumers finish, or mutate accepted state after failed preparation |
| Visible hierarchy groups and membership for one operation | Operation-local `Hierarchy.Projection`, collected in one expansion/tree walk, flattened once using the existing sibling sorter; reused by worker capture | Discarded unsorted flat projection followed by a second projection | 6 | Existing keep/settle decision, hierarchy projection and worker capture | Keep persistent per-parent sort-key history or add flat-table hierarchy work |
| Membership and current instances of the hierarchy's visible projection | `Hierarchy.Project`, with a reusable identity-part map whose keys come from the node index | Flat `Body.Source` membership, which cannot describe child rows; the all-node index, which includes collapsed descendants | 1a, 6 | `KeptOrder` | Hold positions; create a second identity rule; serve as accepted source state |

## Stages

Stages 1a, 1b and 2 to 8 are built in the order listed, stage 9 and stage 10
only if the measurements named in them require it. "How to execute" groups
them and says when each group is reviewed, compiled and tested.
Ranked by return on effort. Stages 1–4 keep today's pipeline: one full pass
per publish, but each pass costs much less. Stage 5 changes the pipeline and
builds on stages 1–4. No stage changes what the person sees, except stage 2's
header acknowledgement and stage 3.

### 1a. A publish where nothing moved costs almost nothing

F2, R1, R4, R8, O1, O4–O6, P3, K2, K3.
This is the path the torrent list runs on every engine update. It needs only
the existing tests.

- **Each row's key is read once per publish (R1, P3).** Read `keys[i]` once
  at the start of `RebuildView`. `ValidateRows` becomes the check per row
  (row type, null row, null key). The duplicate check is the failed `Add`
  into `Body.Source`'s identity map, which the same pass builds anyway, so no
  set of n is built only to check identity. The pass validates into a
  candidate map and accepts the snapshot, the map and the hold together
  only when it succeeds, as the new-order index does, so a rejected source
  is never stored, leaves the accepted map whole, and consumes no release.
- **Validate only what entered (review).** `RefreshView()`, every settle
  tick, `Release`, a hold release and `SortInterval = 0` all rebuild from the
  stored snapshot. `Body.Source` records when a stored snapshot passed
  validation, and `RebuildView` skips validation only for a snapshot with
  that record: about 2–4 ms of the floor at 100,000 rows (**estimate**). The
  record, not the way `RebuildView` was called, decides, because a source
  set before the schema is captured, the usual XAML setup, is stored without
  validation and first validated by the rebuild at schema capture.
- **Kept maps instead of maps built on every step (F2, R1, O1, P3, K2,
  review).** One publish needs three key spaces at once, so one map cannot
  serve every step:
  - the view as it stands, before the change (`Hold`'s and `Release`'s
    `shown`): `Body.View`'s position index;
  - the new snapshot (the duplicate check, `Hold`'s and `KeptOrder`'s
    `live`, and `Pin`'s current instance): `Body.Source`'s identity map,
    membership and current instance only;
  - the new order (`Body.View.Reconcile`'s `place` and `_positions` after
    `AcceptOrder`): built by `RebuildView` and handed to `Reconcile`; after
    the reconcile the view adopts it as its position index, so the next
    publish's "view as it stands" costs nothing to build.

  Today `AcceptOrder`'s `_positions` and `Reconcile`'s `place` are the same
  map built twice. Each map is a field that is cleared, not reallocated, so
  it keeps its capacity. If one map served every step, `Hold` would see the
  new rows as already shown and would never hold a row. "Owners" names each
  map's owner and the one position-index type.

  `Table` stays non-generic for XAML, so the identity part sits behind an
  internal interface. A reference-identity instance exists from `Table`'s
  construction, because the selection, the view and the hold are built
  with it before any schema; schema capture installs the keyed one that
  `Schema<TRow>.Key<TKey>` creates, as `RehashIdentity` does today. It
  replaces `ItemIdentity` and `Schema.KeyEquality`, holds no rows, and gives
  every other row-keyed map or set, such as the drag's (stage 8) and
  `Hierarchy.Capture`'s node index, its map and set types, so no one builds
  one with a comparer of its own. `_positions`, `place`, `Hold`'s `shown`
  and `live`, and `SelectionState`'s `indexOf` delegate are deleted in this
  stage.

  The new-order index holds the index only: today `_positions` also stores
  each row's sibling position for the hierarchy, and that moves into the
  hierarchy. `Hierarchy` keeps two jobs apart. It adopts a new capture
  (sets `Captured`, clears its pending recaptures, subscribes to newly
  reachable child collections) whenever the capture is new, whatever the
  visible order, because a child change under a collapsed folder or a new
  root array with the same visible rows still brings a new capture. It
  computes sibling positions from the order only when the order changed.
  `HasChildren`, which reserves the expander slot, stays computed on every
  publish, as today, because a new capture or a `RefreshView` that reads
  `hasUnrealizedChildren` again can change it with the order unchanged.
  `HierarchyPosition` reads the positions, so a flat table's core state
  holds nothing of the hierarchy.
- **Check "nothing changed" first (K2).** Today `RebuildView` builds
  `_positions` in `AcceptOrder` before it checks whether the order equals the
  view. Check first, and keep the view's position index as it is when the
  final instances and order are the same. Selection and held-row
  reconciliation still run, and so does the hierarchy's adoption of a new
  capture. Candidate state stays separate until validation and the
  comparisons succeed.
- **No boxing of value-type keys (R4, O4, K3).** The identity part hashes and
  compares `TKey` directly and does the null-key check. The Library's `long`
  key boxes on every hash and comparison today: an **estimated** 7–35 MB of
  short-lived garbage per publish at 100,000 rows. Without a key, identity
  stays the object reference. *Superseded in batch (a):* the line that kept
  each row's key once per publish is dropped. Identity maps read the key
  through the selector on every lookup, which the contract keeps pure, stable
  and inexpensive, and no map keeps captured keys. The kept keys had to travel
  with every map, set and position index as an extra parameter, and the
  measurement showed no cost from reading again. With int and string keys at
  1,000 and 2,000 rows, every whole publish and every notification kind was
  cheaper or within the run-to-run spread of two runs. An unchanged
  `RefreshView()` states no difference, so it does not walk the view either.
  Raw output is in `V:/temp/TableViewPerformance/batch-a/`.
- **Pooled working arrays (R1).** `present`, `slots` and similar per-call
  arrays come from `ArrayPool`. `Pin` builds its output once instead of a
  slots array plus a result list (K2).
- **Read a typed list directly (O6).** `Body.Source.Rows` reads every `IList`
  through the non-generic indexer, which for an array goes through
  `Array.GetValue`. The torrent host publishes arrays. Use a source that is
  already an `IReadOnlyList<object>` as it is (any `T[]` or `List<T>` of a
  reference type is one), and move the null-row check into `ValidateRows`'
  pass. A hierarchical table never calls `ValidateRows` today, so
  `Hierarchy.Capture` calls the per-row check for each row it captures, and
  its node index, which it builds anyway, is where a duplicate fails; it
  keeps no checks of its own. Without the null check a null root fails first
  in the row-type check with the wrong message, in the host's key selector,
  or in `nodes.ContainsKey`.
- **The remaining whole-view searches (R8, O5, K2)** use the index:
  `RebuildView`'s search for the focused row (`ResolveItem`), `Pin`'s search
  for the pointed and menu rows, `Release` (checks only the supplied items
  instead of building two sets of n; the torrent list calls it on every
  filter change), `ScrollIntoView`'s searches of the snapshot, the context
  request's `View.Any`, and `Body.View.GetSelectedRanges`.
- **`GetSelectedRanges` from the selected positions (R8, E5).** The list
  calls it on every range call, the table's and its own (E5), and today it
  scans all n rows with a key-selector call per row: an **estimated** 5–10 ms
  per call at 100,000 rows, several per click, key step, marquee step or
  publish that prunes a selected row. Build every range from the selected
  view indices, in index order. The list's selection properties read only the
  first range, but its Ctrl+A handler (`ListViewBase_Partial_Interaction.cpp`)
  and `SelectedRanges` read them all, so returning one range would break them.
  No event changes. The ranges come from stage 1b's selected view indices;
  1a and 1b are written together, so no interim builder is written.

**Gain. Estimate.** From 25–100 ms and 14–25 MB per publish to a few
milliseconds and near zero garbage at 100,000 rows. The floor is one key read
per row per publish (L3, L5): about 1–2 ms at 100,000 rows.

**Contract.** None. The order, the notifications, the hold and the pointed
row stay the same. A null row still throws the same exception from the same
`RebuildView`, flat or hierarchical. The implementation map changes.

### 1b. Selection costs the change, not the selection

F3, R3, R13, R14, R17, R18, O2, K4, K5, C2, N1, N2, N7, A1, B1, Q2–Q4, S1, S5,
S6. Builds on 1a's maps. Needs the selection check named in its range-call
item.

- **The selection model applies the change, not the whole set (N2, Q3).**
  Today every selection change costs the whole selection: `Apply` clears and
  re-adds every row, `Toggle` copies the set and runs `RemoveAll`,
  Ctrl-navigation copies and re-adds it only to move the current row, and
  `Range` rebuilds the whole range on every Shift+arrow. **Estimate:** 25–35
  ms per key at 100,000 selected rows, so a held key fills the UI thread.
  `SelectionState` keeps, beside the selected set, the selected view indices
  in ascending order, rebuilt only when the view changes. `Toggle`, `Range`
  and `Navigate` apply only the change to both. `Range` adds or removes only
  the rows between the old end and the new one when the gesture continues
  the same range, a Shift+arrow or Shift+click after another from the same
  anchor. Any other range rebuilds from the anchor as today: after "select A,
  Ctrl+click D, Shift+click C" the result is C–D and A must go, and a
  Ctrl+Shift range adds to rows that earlier gestures selected, so moving its
  end inward removes nothing those gestures chose. A change of only the
  current, anchor or focus row keeps the set and reuses the packet's items
  array, but publishes a new packet, because `Selection.Current` is part of
  it.
- **Carry the selection over through the selected rows (F3, R1, O2, K4, Q4).**
  `SelectionState.Reconcile` finds each selected row, and the current, anchor
  and focus rows, in the new-order map, rechecks interaction only for those, and
  keeps them in view order. Its cost becomes the number of selected rows, not
  n.
- **Build the packet once (O2, K4, R18).** `BuildSelectedPacket` uses that
  order instead of sorting again with `OrderBy(IndexInView)`, and `Selection`
  gets an internal constructor that takes the array instead of copying it.
  When most rows are selected, as after select all, build the packet by one
  walk of the view. The marquee's coverage step does not sort what the commit
  sorts again.
- **Publish only what changed, and make the row event cheap (R3, O2, K4, A1,
  K9).** `CommitSelection` keeps the published packet when its instances,
  order and current row are the same. Today the commit raises
  `RowVisualsChanged` on every commit, and every realized row then runs
  `GoToState` and sets its cursor: about 120,000 `GoToState` calls for 1,000
  notifications (C3). The cost is the state changes, not the event: a
  publish can change any row's selection, drag cue, interactivity
  (`Table.CanInteract`'s remarks promise that `RefreshView` makes rows read
  it again) or expander (`Body.HierarchyCell` reads `HasChildren` and
  `IsExpandable`, which a child change or a new root array changes). So
  `RebuildView` raises the event once per publish, as the publish's own
  fact, and outside a publish each fact's owner raises it for its fact: a
  selection commit from a gesture, the drag gesture (which already does),
  expansion, and from stage 6 `Hierarchy` when a child change alters a
  shown row's expander. The row element applies only the states that changed: it
  skips `GoToState` and the cursor when they are the same (K9), so a raise
  costs a read of each realized row's facts, and it is the record of what
  each row shows, so no other owner keeps one.
- **One range call per commit (R3, E5, B1, S1).** `RefreshContainers` makes
  one `SelectRange` or `DeselectRange` per run of changed indices, off-screen
  rows included, and each call makes the list scan all n rows through
  `GetSelectedRanges` and re-read every realized container. Selecting every
  other row makes 50,000 calls at 100,000 rows. A plain click
  that clears 1,000 selected rows spread over the list, which is what a sort
  does to a selection, makes about 1,000 calls: an **estimated** 0.3 s at
  10,000 rows and 3 s at 100,000. One call already makes the list re-read
  `IsSelected` for every cached and pinned container, so make one call per
  commit, at any realized index (our `SelectRange` and `DeselectRange` do
  nothing), and only when a realized row's state changed. The list's own
  `SelectionChanged` then fires once per commit, which the table already
  treats as unsolicited. The containers take their state through the same
  `put_IsSelected` path as today, so the view's notifications and the row
  animations do not change. Check that click, Ctrl+click, Shift range,
  marquee, and a publish that prunes a selected row each leave every realized
  and pinned row with the right selection state, including a commit that
  changes rows far apart. To find whether a realized row changed,
  `RefreshContainers` compares each realized container's `IsSelected`, which
  the list already holds, with the selection model, instead of building two
  sets of the whole selection or keeping a record of its own.
- **Drag-ability is computed once (R17, K5, N1, Q2).** `Table.CanStartDrag`
  runs for every realized row on every commit, and on every container bind,
  so on every row a scroll recycles. Section 20 forbids work that scales with
  all rows during scrolling, and this breaks it: after Ctrl+A in a seeding
  list, every bound row scans the whole selection, an **estimated** 1–3.5 ms
  per row at 100,000 selected. Beside that scan, `Order.HasMovableRows` walks the
  host's whole order until it finds two numbered rows: O(n) when fewer than
  two torrents are queued, which is usual for a seeding list. `RebuildView`
  keeps a publish version, which it increments on every run, so on every
  publish and every `RefreshView`: the points where the table reads the host
  again, because the table cannot see when the host's order callback would
  answer differently. `Order` answers "the order allows a reorder" from a
  value it recomputes when the publish version it recorded differs; no
  pipeline step calls `Order`. The drag owner (`Table/Reordering.cs`) answers
  "the selection holds a numbered row" the same way, recomputed on the first
  `CanStartDrag` after the published packet or the publish version changed,
  because "numbered" asks the host's order callback, which the selection
  commit knows nothing of. `Table.CanStartDrag` stays the one decider and
  reads the two facts instead of walking the order or the selection; the
  press and the row's visuals both ask it. A publish can change a row's
  answer without changing the selection, for example when a torrent enters
  the queue under a name sort; the event `RebuildView` raises per publish
  makes the row read it again.
- **Selection gestures stop copying the whole set (R13, R14, S6).** Every
  press copies the selected set (`SelectionState.Capture`), in case the press
  becomes a marquee that Escape must undo: 800 KB per press with 100,000 rows
  selected. The checkpoint must hold the state before the press, so it cannot
  wait until a marquee begins. Take its rows from the published selection
  packet, which is immutable and already exists, and keep only current,
  anchor and focus separately. Skip the checkpoint when no marquee is
  possible. This holds because every selection change commits before the
  next press, so the packet and the model agree at every press; a later
  change that lets the model run ahead of a commit must take the checkpoint
  from the model instead. `Toggle` and Ctrl-navigation change the set in place.
  `Apply` takes ownership of its list. A context request on a selected row
  changes the current, anchor and focus rows and leaves the set alone, as
  section 15 already describes.
- **Automation follows the change (N7).** While a UI Automation client
  listens, `Body.Surface` builds two sets of the whole selection per commit
  to raise its events; compute them from the selection change instead. In a
  hierarchical table, `RefreshHierarchy` walks every live item peer on every
  publish; walk them only when expansion or the hierarchy's capture changed,
  because a peer's `Expansion` (leaf or collapsed) follows the children too. It removes dead weak
  references one `RemoveAt` at a time, which is quadratic when many are dead
  (S5); compact the list in one pass instead.

**Gain. Estimate.** With all rows selected, select all drops from 40–60 ms
and 6 MB to about 5–8 ms, and Ctrl+click, Ctrl+arrow and Shift+arrow drop
from about 25 ms to about 1 ms at 100,000 rows. Clearing a scattered
selection drops from about 3 s to under 1 ms.

**Contract.** None. `Selection.Items` stays an immutable packet in visual
order. The implementation map changes.

### 2. Sorting: only what changed, and the click answers first

F4, R2, R9, P1, K7, C1, N4, A4, Q6, S2.

**Cost now.** The table sorts every row with LINQ:
- at the end of each settle interval in which anything was published, and
  on every publish when `SortInterval` is zero; usually the result is the
  order already shown;
- on every header click, inside the `Sort` setter, before the header can
  paint (C1, K7).

A sort of n rows calls the comparer about n·log₂n times: 133,000 calls at
10,000 rows and 1.7 million at 100,000. The torrent Name column compares with
`StringComparer.CurrentCultureIgnoreCase`. **Estimate:** 20–130 ms per sort at
10,000 rows and 0.2–1.7 s at 100,000, on the UI thread. A numeric sort costs
about 2 ms and 15–30 ms, mostly allocation: LINQ allocates four buffers of n
and the `.ToList()` a fifth.

Section 1.1 names "a sort header that shows nothing for more than 100 ms
after the click" as a defect. A name sort at 10,000 rows is at that limit
today, and at 100,000 rows far over it. The table cannot do better today,
because `Column.Sort` is a function that returns an `IOrderedEnumerable`: it
never sees the keys or the comparer.

**Change.**
1. **Typed sort.** `Schema.SortKey` gives the table the key selector and the
   comparer instead of a sort function. A full sort sorts a reused index array
   over a reused key array with `Array.Sort` and the base index as the
   tie-break, so it stays stable as section 9 requires; descending reverses
   only the key comparison (F4, P1, K7). Keep the workspace for the active
   column only.
2. **Sort only the rows whose key changed (R2).** Flat tables only: a
   hierarchy's shown order is a tree whose sibling groups are sorted apart,
   so it is not sorted by key and a binary search over it would separate
   rows from their parents; a hierarchy keeps today's projection, sorted per
   sibling group. The table keeps each row's key from the last sort. On a publish where the rows and their base order
   did not change, it reads each key and compares it with the kept one using
   `EqualityComparer<TKey>.Default`, an ordinal check much cheaper than the
   sort comparer. This check is used only for known immutable built-in key
   types and `string`, including their nullable forms, when the comparer is
   the default one for a non-string key or a well-known `StringComparer`.
   `Comparer<string>.Default` uses the current culture through `String.CompareTo`,
   so it uses the actual-comparer fallback: changing the UI thread's culture
   can change ordering without changing a key. This keeps the existing comparer
   contract without another culture-invalidation mechanism. See
   [default comparer](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.comparer-1.default?view=net-10.0)
   and [String.CompareTo](https://learn.microsoft.com/en-us/dotnet/api/system.string.compareto?view=net-10.0).
   An arbitrary struct
   can define equality that disagrees with its ordering, so it uses the
   actual-comparer fallback below. For the known types the key is immutable and the comparer is a fixed
   function of it, so a key equal to the kept one keeps its place. Any other
   case may break that: the selector can return the same mutable object with
   a changed value (`SortKey(column, row => row, byRank)`), equality can
   differ from the comparer, or a custom comparer can read host state that
   changed. For those, check whether the kept order is still sorted, using
   the actual comparer and the base order as the tie-break (n−1 comparer
   calls), and sort everything when it is not. This proof also precedes
   arrival placement for those keys inside the interval: mutable key references
   and host-dependent comparers cannot preserve historical ordering values.
   If that proof fails, the same full sort runs; copying host objects or
   inventing another placement policy would change the unrestricted comparer
   contract. An unchanged-membership publish can still wait for the interval.
   `SortKey`'s contract then needs no new restriction. No key changed: skip
   the sort. k keys changed: first
   check whether any row must move (M2). The kept order was sorted, and the
   rows that did not change keep their keys and base order, so a pair of
   neighbours can be out of order only when it holds a changed row. The pass
   that finds the changed rows reads the keys in kept order, and compares a
   pair of neighbours, with the current keys of both and the base order as
   the tie-break, only when one of them changed: at most min(2k, n−1)
   comparer calls, in that same pass. When no pair is out of order, keep the
   order and store the current keys as the kept keys, so the next publish
   compares against the values the order was last proven sorted for;
   otherwise a row that changed once would count as changed on every later
   publish. This saves the steps below when values change but keep their
   places, as progress values usually do. Microsoft's
   `ShapedItemsSource.TryReshapeStaleSortedFlatProjection` makes the same
   neighbour test but switches to a full scan above one changed row in eight,
   because it learns only which rows changed, not where they are, and pays a
   lookup for each; this pass knows each changed row's place, so it needs no
   switch. When a row must move: take those k
   rows out and place them with the sorted order's **placement routine**:
   sort the k rows, find each one's place by binary search over the kept
   keys of the rows that stay (k·log n comparer calls), and build the new
   order in one pass that copies the runs between those places (n moves, no
   comparer calls). This routine is written once, here, in the sorted order;
   stages 3, 5 and 10 call it and write no placement of their own. Inserting each row into the array instead would move
   k·n/2 references: at 10,000 changed rows of 100,000, 0.25–0.5 s, slower
   than the full sort (review). A membership change keeps the kept keys:
   stage 3 drops the leaving rows' keys and places the arrivals. A changed
   relative base order of surviving rows sorts everything, including when
   rows also arrive or leave, because the base order breaks ties. Membership
   alone does not change the relative base order of survivors.
   The kept order is
   the sorted order before `Pin`: `Pin` puts held rows and the pointed row
   back at their old indices, so the shown order is not sorted while a row is
   pinned, and a binary search over it would place rows wrongly (A4).
3. **Culture string keys sort by collation key (R9).** When the comparer is
   a culture-aware `StringComparer` (`StringComparer.IsWellKnownCultureAwareComparer`
   gives its `CompareInfo` and options), keep each row's
   `CompareInfo.GetSortKey` and sort by ordinal byte comparison. The order is
   the same as the comparer's. A null key has no sort key
   (`GetSortKey(null)` throws), so it sorts before every other key, where the
   culture `StringComparer` places null, and only non-null keys get one. The keys cost one `GetSortKey` per row when a
   name first sorts or changes, and about 5–10 MB at 100,000 rows while that
   column is sorted. Opus proposed a public `SortKey` overload that takes a
   `CompareInfo` instead (N4); detecting the comparer gives the same result
   with no API change.
4. **The header click answers first (R9, K7).** The header gesture stores a
   **queued sort**, the header shows its glyph, and the sort runs after the
   next frame, through the same `CompositionTarget.Rendering` step that
   `Table.OnLaidOut` uses for a reveal, not a second one. A sort queued
   straight from the click can run before the next frame and block it, so the
   glyph would not paint first (the remarks on `Table._layoutPending` record
   why the wait goes through `Rendering`). The `Sort` setter itself stays
   synchronous, as section 5.3 requires; only the gesture waits for the frame.

   One field in `Table/Sorting.cs` holds the queued sort, with the committed
   sort and the committed-sort and lifetime revisions it was made against.
   It is a different concept from the existing
   `_pendingSort`, which holds a `Sort` assigned before the schema is
   captured; the two keep separate names. A second header click replaces the
   queued sort. No other code cancels it: when it runs, it applies only if
   the table is loaded (`IsLoaded` is true and `_detached` is false), both revisions still match,
   the committed sort is still the one it was made against, and its column
   is still shown. An actual sort commit advances the sorting owner's
   revision; an actual detachment advances the table's lifetime revision.
   Returning to an earlier criterion or reloading cannot revive an old click.
   So a `Sort`
   assignment, a layout restore or `ResetLayout` that changes the sort,
   hiding the column, or unloading makes it drop itself. One case is
   accepted: a host request for the sort already in force, in the frame
   after the click, changes nothing and so loses to the click. `ActivateSort`
   keeps the toggle rule and computes the queued criterion at the click; the
   validity test runs before the queued sort calls the `Sort` setter, so the
   setter, which throws for a hidden column, never sees one. It sorts the
   source as it stands then, not as it stood at the click.

   The header shows one criterion, which `Table/Sorting.cs` answers: the
   queued sort while it is still valid by that test, otherwise the committed
   sort. The header reads it and never computes it. If the comparisons throw,
   the setter commits nothing, the queued sort is gone, and the header shows
   the committed sort again.
   The shared dispatcher callback forwards exceptions with
   `WinRT.ExceptionHelpers.ReportUnhandledError`, preserving the host's XAML
   unhandled-exception policy after the glyph is restored. A raw exception
   escaping `DispatcherQueueHandler` terminates the process before that policy
   sees it, as B's isolated deliberate-failure run confirmed. Synchronous host
   assignments still throw directly to their caller.
6. **A first sort that would block too long runs on a worker (R2, R9, N4,
   L2).** At 100,000 rows the first sort of a name column still takes an
   **estimated** 100–250 ms after the glyph shows, and the UI does not respond
   during it. The keys are read on the UI thread, as section 5.3 requires;
   sorting collation keys or numeric keys runs no host code, so that part can
   run on a worker. The worker's job is held in the same queued-sort field as
   change 4, and its result is committed on the UI thread only through that
   field's test, so a later click or `Sort` assignment supersedes it with no
   second mechanism. The job records the publish version it sorted at, not
   the snapshot: `Body.Source` reads an indexable source in place, so a
   recorded snapshot already shows the host's new contents. `RebuildView`
   does not check that a prepared order has the snapshot's rows, so when the
   publish version has moved, the sorted-order owner reconciles the job
   against the current snapshot: remap surviving identities to current
   instances, reread surviving keys, drop leavers and place arrivals and
   changed keys with its one placement routine. It also uses the current
   base order for stable ties; when a changed base order requires a full
   sort, the same sort routine handles it. Membership equality alone does
   not establish freshness: a value or a keyed instance can change during
   the worker job. No second reconciliation is written. **Decided: build it
   when stage 2's measurement, extended to 100,000 rows, puts one sort's
   uninterrupted UI-thread work above 100 ms.** A host comparer that is not a
   culture `StringComparer` still sorts on the UI thread, because it is a
   host callback.
   **Gate result: enabled for group C.** B's first-sort medians were 2.7859
   and 5.1831 ms at 1,000 and 2,000 rows. The whole-block n log n projections
   are 464.32 and 392.54 ms at 100,000; fitting a fixed overhead plus n log n
   growth still estimates 333.54 ms. These are extrapolations for the measured
   culture-name fixture, not full-size measurements or arbitrary-host bounds.
7. **A settle that is no longer needed does not run (S2).** A full sort sets
   `_orderSettledAt` but leaves a pending settle timer running. After a
   header click has sorted everything, the old timer still fires, finds the
   interval not over, keeps the order and schedules another settle, which
   then sorts every row again with no source change. Stop the timer when a
   full sort has consumed the work, and let a tick that is already queued
   check whether any work remains. A row still held or pinned keeps its
   pending settle.

**Gain. Estimate.** A settle where a few names change: from 20–130 ms to
about 0.1 ms at 10,000 rows. A click on Name: the glyph shows at once; the
rows follow in 5–25 ms at 10,000 rows, and 100–250 ms for the first sort at
100,000 rows, then about 30 ms.

**Contract.** Section 9: the header shows the new criterion before the rows
reorder. `O(n log n)` in section 20 stays the upper bound. `Hierarchy.Project`
sorts each group of siblings with the sorted order's one stable sort
routine, which replaces its call of `Column.Sort`; it gets no sort of its
own.

### 3. A row that arrives or leaves does not re-sort the table

Builds on stage 2's kept keys and collation keys: the search for each
arrival's place reads them, so it never calls the culture comparer per row.
Flat tables only, as stage 2's change 2: in a hierarchy an arrival or an
expansion still sorts the projection per sibling group, as today.

**Cost now.** Under a reordering sort, a publish that adds or removes even one
row re-sorts every row at once (`KeptOrder` returns null when the row count
changes). This costs a full sort, and it reshuffles the whole table
immediately: the reshuffle that the 3-second settle interval exists to stop.

**Change.**
- A row that leaves: drop it from the shown order. Removing a row cannot make
  the remaining order less sorted, so no sort is needed.
- Rows that arrive: place them with stage 2's placement routine over the kept
  keys of the last sorted order, before `Pin`: k log k to sort the arrivals,
  k·log n comparer calls to find their places, and one pass that copies the
  runs (n moves). This stage adds no routine of its own; it calls that one
  from `KeptOrder` when the rows changed. The other rows stay where they are
  until the next settle. A merge that compares at every
  step would call the Name column's culture comparer about 100,000 times for
  one arriving torrent at 100,000 rows: 30–100 ms (review).

**Decided: place the arrivals, do not re-sort.** Inside the interval the shown order is the order of the
last settle, placed by the keys kept from that settle, and values change in
between. So an arriving row lands where those kept keys put it. That is its
sorted place when the values around it did not change, and it can be far from
it when they did: in an ascending order whose shown values are now
`[100, -1, -1, …]`, a new row with value 0 lands before 100, at the top. The
next settle, at most 3 seconds later, moves it to its place with the other
rows. Today the arriving row is exact and every other row jumps at once,
which is the reshuffle the settle interval exists to stop. When the arrivals
are placed, nothing else moves, and only the new row may need to move at the next settle.
That is the better trade for a person whose pointer and eyes are on the
rows.

For keys outside the immutable fast path, placement first proves the kept order
with the actual comparer as stage 2 specifies; if it is no longer ordered, the
shared full-sort fallback applies. Those host keys/comparers cannot retain the
historical values needed for binary placement, so this preserves correctness
without copying or constraining host objects.

This changes the positions at which `Membership` raises its inserts, not
their kind or number. By E1 and E3 the animation and the panel's work stay the
same. The no-flash checks apply: on screen, an arrival under a live sort
animates in at its placed position and nothing else jumps.

The `KeptOrder` remarks record why an earlier merge was removed: it scanned
the shown order once per arrival, about 1.4 million comparer calls when a
filter brought 900 rows back. The placement routine finds each arrival by
binary search, so that cost does not return.

**Contract.** Section 9, the paragraph "The settling interval exists because…",
which says a membership change takes the sorted order whole. The `KeptOrder`
remarks change with it.

### 4. Membership changes without shifting every later row

F5, R5, O3, K1, A3, Q1. Subject to the no-flash checks.

**Cost now.** `Body.View` stores its rows in the `List<object>` of
`Collection<object>`. `Membership` calls `Insert` and `RemoveAt` one row at a
time, and each call moves every later row. Removing half of n rows spread
over the list moves about n²/8 references; removing the first half, the worst
case, moves about n²/4. A large batch of arrivals at the front costs the
same. Worst case:
- 10,000 rows: 25 million moves, about 200 MB of copying, about 10–20 ms;
- 100,000 rows: 2.5 billion moves, about 20 GB of copying, 1–3 s before the
  list control does anything.

`RemoveFromRuns` and `InsertIntoRuns` do the same during a reorder: a
reversal with about 60 realized rows shifts almost the whole list about 120
times.

**Change.** Every pass moves through the list in one direction: `Membership`
raises removals in descending index order and inserts in ascending order, and
`PlaceQuietly` and `InsertIntoRuns` go upward. A gap buffer serves exactly
that pattern in n + k moves per pass, and indexing stays O(1).
`Collection<object>` takes its backing `IList<object>` in its constructor, so
`Body.View` can pass a small gap-buffer list. The notifications, their order,
the true intermediate counts, the pinned containers and the silent updates
outside realized positions stay exactly as they are, and each event is still
raised after the backing list has taken that one change (E3). By E1 the
animation stays the same. The `Change` args stay one object per event: the
list passes them to every subscriber of its own `VectorChanged`, and not every
subscriber was traced, so reusing one could hand a kept object new contents
(W4).

**Measured in group A.** The list's native event-only cost was 5.964 and
9.118 microseconds at 1,000 and 2,000 rows. A linear projection of 50,000
events is about 0.30–0.46 seconds, so stage 9's 2-microsecond gate is exceeded.
This stage alone still raises one event per arrival/leaver because
`IObservableVector` has no range event and a reset flashes (E4). Stage 9
joins group C, subject to its shipped-runtime no-flash acceptance.

**Contract.** None.

### 5. Apply the change, not the whole source

F1, F6, K8, C3, C4, N8, N11. Subject to the no-flash checks.

**Cost now.** Every notification runs the full pipeline, whatever changed.
`Body.Source` does not read the event args.

**Change.** `RebuildView` stays the one entry into the pipeline and takes
the change: a whole source, or one notification's action, indices and items.
Every step reads that change; no second pipeline is written beside it. Apply
`Add`, `Remove`, `Replace` and `Move` from the args, and keep full validation
for `Reset` and a new source. A host `Move` reaches the view as a removal and
an insert, never as a `Move`, because the view never raises one. Each step
then costs the size of the change:
- `ValidateRows` checks only the added rows, and a duplicate of a row
  already in the source is a failed `Add` into `Body.Source`'s identity map;
- under a sort, the sorted order places arrivals with stage 2's placement
  routine;
- the position indexes are updated for the change instead of rebuilt: the
  pipeline's change step enters each arrival with its insert position and
  removes each leaver, and the stale window below covers only the shift of
  the other rows' stored positions;
- carry the selection over only for rows that left or were replaced;
- the same hold, selection and notification owners do the rest.

Each notification is applied when it arrives, not collected and applied once
per dispatcher tick, as Opus proposed (P4). Section 5.3 requires this: after
every notification the table takes the source's current order, and it may
update the view incrementally only if the observable result stays the same.
With collected notifications, `ScrollIntoView` or a selection of a row the
host just added would not find that row until the tick ends. A WinUI
`ListView` also applies each `ObservableCollection` notification at once.

By E1, host events applied in one layout tick give the same counts, and so
the same animation, as today's one reconcile of the same change; events spread
over several ticks each get their own, as a plain `ListView` over an
`ObservableCollection` shows. By E6, each host event is applied outside
layout and never while the list is handling another.

**Depends on** stage 1b's row element that applies only changed states (K9)
(C3). Without it, 1,000 arrivals in one tick make every realized row run
`GoToState` 1,000 times: about 120,000 calls. With it, each notification
costs a read of the realized rows' facts.

**What it does not fix.** Both product hosts publish a new list: the torrent
list's `Finding.Project` and the Library's `Browser` (H1, H3). A replacement
list must still be read whole, so they gain nothing from this stage alone. To
gain, a host must raise per-row notifications, and the torrent host would
then need its own diff of its projection, which is O(n) unless it tracks
membership per torrent (C4). This stage is mainly for general use of the
library.

Child-collection notifications in a hierarchy are stage 6's, because they
need its kept node index.

**The position indexes after a change (N11, review).** Every removal needs
the row's view index, because `Body.View` removes by index and the view
index differs from the source index under a sort, a held row or a
hierarchy. After each change, a stored index is off by at most the number of
changes since the last rebuild: a row stored at p now sits between
p − removals and p + inserts. The position index type in "Owners" owns this:
it records its sequence's counts of inserts and removals when it was built,
reads the current counts to size the window, searches the sequence by
reference inside it, and rebuilds itself once the change count reaches about
√n (about 316 at 100,000 rows). That costs about 1 µs per change at 100,000
rows. Rebuilding on every lookup below a stale mark instead would cost O(n)
per removal in random order: 1.5–2.5 s for 1,000 removals at 100,000 rows.
The window holds only while every move of a row is a counted insert or
removal. `Body.View.PlaceQuietly` moves rows outside the realized runs
without an event, so a publish whose order is not the previous order plus
this change (a settle, `SortInterval = 0`, a changed key that moves a row)
rebuilds the indexes, as today's publish does.

`Body.View` owns the view's index. The sorted order owns a second instance,
over the kept order: a removal under a sort takes the row out of the kept
order and its keys, which needs its place in the kept order, not its view
index while rows are pinned or held, and the kept order leaves out held rows
that the view still shows, so the view's counts cannot bound it. The kept
keys stay in stage 2's arrays: an arrival or a removal shifts them once, a
block copy within this stage's bound below, so stage 5 does not rewrite
stage 2's storage. `Body.Source`'s identity map holds no positions, so a
notification only adds and removes its rows there; a duplicate among the
added rows, or a held row that returns, is decided against it, not against
the view.

Stage 1b's selected view indices are not shifted on every change: they
record the view version they were built from, and the next read rebuilds
them from the selected set through the view's index when the version
differs, once per commit, not once per notification. No writer of the view
tells the selection anything. An order-statistic tree is not needed; it
would make every `View[i]` read, the list's included, cost O(log n).

**The bound this stage gives.** One notification no longer runs the whole
pipeline. Its cost is the change's own comparisons (log n per arrival under a
sort), its one native event, at most one move of the view's gap to the
change's position. The view learns what changed from the order's producer:
the natural order states the notification's own indices, and the kept sorted
order states its removed, placed and replaced rows, a moved row as one of
each. The view applies a stated difference only when it shows the version of
the order the difference starts from, which it checks by reference, so a
notification that adds, removes or replaces rows never walks the view. The
gap move is a block copy of at most n
references, about 40–80 µs at 100,000 rows (**estimate**). Under a sort, the
kept order copies the rows that stay into its candidate in native blocks,
about n × 32 bytes, 0.2–0.4 ms at 100,000 rows (**estimate**). That copy is
this bound's one block copy for a sorted notification: it lets a failed
publish leave the accepted order as it was, as before batch (a). The order
map's rebuild
every √n changes adds O(√n) per change when spread over them. Measured on the
release build at 100,000 rows, two runs: an unsorted Add, Remove or Replace
costs 0.6–1.0 million cycles per publish; under a sort, 1.7–2.5 million, a
Replace that moves its row included. An Add or Remove half the list away from
the previous change, so that the gap moves, costs 1.1–1.2 million unsorted and
3.1 million under a sort.

While rows are held without a sort, the natural order is the view with its
new instances, copied only when an instance changes, and states them against
the view.

Where no difference is stated (pinned rows, held rows under a sort, a resort
that places every row again, a Move or Reset notification, a hierarchy, a new
sort or assignment, each already O(n)), the view walks itself
against the new order: one reference per row, looking up a view index only
where the two differ. Measured on the release build: a median 0.027 million
cycles at 1,000 rows and 0.053–0.079 at 2,000 (two runs), about 27–40 cycles
per row.

**Contract.** Section 5.3 already allows an incremental update. Section 20
gains this bound: a source notification costs its own comparisons and event
and at most one block copy of the view, never a rebuild of the view; under a
sort, the kept order's block copy of the rows that stay is added. The
implementation map changes.

### 6. Hierarchy capture reuses what did not change

R7, R10, O7, K6, A2.

**Cost now.** Every root assignment and every child-collection notification
walks every supplied node, collapsed ones too. It creates a new `Node` and a
dictionary entry per node, and copies each child collection unless the host
passed the same instance again. `MatchesStructure` then walks every node a
second time, with a `SequenceEqual` per node that allocates two enumerators.
`PrepareHierarchy` projects the visible tree unsorted, and under a local sort
`SortedSnapshot` projects it again sorted and discards the first result.
**Estimate:** a notification that changes one leaf recreates every node:
0.5–1 MB at 10,000 nodes and 5–8 MB at 100,000. The file browser's window
caps it at 10,000 rows, so for TinyTorrent this is a few milliseconds per
publish; it is large for a general host with a big tree.

The file browser never reuses the child-list cache (A2). `FileSelection.Publish`
assigns a new `Roots` array on every publish; `Body.Source.Set` then calls
`Hierarchy.Invalidate`, and `Capture` reuses a cached child list only when no
invalidation is pending. So every publish copies every child list, although
`FileNode.Children` returns the same `List` each time. The host then calls
`Files.RefreshView()`, a second full rebuild in the same turn.

**Change.**
- A new root list keeps the cached lists of child sources that raise
  notifications, because those report their own changes. A plain child list
  is compared with its cached copy by an indexed loop, and the copy is reused
  only when they are equal. A plain list has to be read again, because the
  hierarchy extension makes a new root list the way a host publishes changes
  to it, and `RefreshView()` reuses captured membership. A host that changes
  a plain child list in place, as the product's file browser does when a
  folder loads or closes, relies on that recapture, so keeping the cache
  without the comparison would hide the loaded children. The hierarchy
  extension's claim that "the Files model already supplies" stable notifying
  collections is wrong today and is corrected with this stage.
- Project the visible tree once, with the sort, and answer membership from
  that list. The projection keeps each expanded parent's sibling order from
  the previous publish as stored derived state. A group sorts again when that
  order may be out of date: a change of sort column or direction forgets every
  kept group; a group sorts again when it is newly shown (its parent expanded),
  when its list changed which rows it holds or their order, when moves are allowed for a whole
  change or a pending settle (every group), and when moves are allowed for
  rows `RefreshView(changed)` names (their groups). A group that is not shown
  loses its kept order. `HierarchyTests` checks the expansion case.
- Compare children with an indexed loop.
- Keep the node index and the child-source relations between captures. A
  child notification updates that collection and the descendants it adds or
  removes. `ValidateRows` checks each added row, and a duplicate anywhere in
  the tree is a failed `Add` into the kept node index, as "Owners" says;
  `Hierarchy` writes no check of its own. The first capture and a new root
  source still validate everything.
  `Hierarchy.Prepare` keeps the changed relations in the preparation until
  `Accept`, so a rejected change leaves the accepted hierarchy as it was,
  with nothing to undo. A list that missed a notification, because its change
  was rejected or the table was not observing it, is read again in full at
  the next change, so later changes cannot apply indices to an older accepted
  child list.
- **Apply child notifications, not a full capture (N8, Q11).** Every
  child-collection notification runs a full capture of the supplied tree and
  a full rebuild today, so a host that fills a child `ObservableCollection`
  with k rows pays k full captures: adding 1,000 children to a 10,000-node
  tree visits and allocates over ten million nodes, even under a collapsed
  folder. Collecting the notifications per dispatcher turn is rejected for
  the same reason as for the root source (stage 5). A child notification is
  applied to the kept node index at once, at every depth, collapsed or
  expanded: today's full capture reads a changed collection at once too, so
  a collapsed folder that gains its first child shows an expander, one that
  loses its last child loses it, and a duplicate identity added under a
  collapsed folder is rejected before publishing, as the hierarchy
  extension requires. Every child notification, collapsed or expanded,
  enters `RebuildView` as one change (`Change.Children`), so validation,
  acceptance and the expander and automation updates have one path. When no
  view row changes, the view's comparison finds the order unchanged and the
  reconcile does nothing. The kept node index is then the one record of child
  membership. The one set of lists, `_stale`, holds only lists that missed a
  notification no accepted change applied, so the next change reads them
  again in full.

**Contract.** None. The [hierarchy extension](../../lib/TableView/docs/hierarchy.md)
keeps its rules: a repeated identity anywhere in the supplied tree, collapsed
rows included, is rejected before publishing, and child collections under
collapsed rows stay observed. `HierarchyTests` checks the first.

**Not in this stage: capture only expanded nodes (R7, P5).** It breaks both
of those rules, and no host supplies large collapsed trees today.

### 7. Fewer elements per realized row

R6, P6, K9. Needs a visual check.

**Cost now.** A realized row is `ListViewItem` → presenter → `Body.Row` →
`Grid` → `ContentPresenter` → `CellsPanel`, then a `ContentPresenter` and a
template root per column. The torrent list's 15 columns make about 36
elements per row. `Body.HierarchyCell` creates a templated `Button` and an
icon on every row, leaves included, and hides them. Every realization during
a scroll builds these, and every layout pass measures them. This cost does
not grow with n, but the person feels it on every scroll.

**Change.**
- Keep `Body.Row` as the one row-level state owner; make its existing
  presenter the template root and remove its single-child `Grid`. Preserve
  the visual-state target name `Root` and stage 1b's unchanged-state skips.
  This is the settled fallback: `CellsPanel` is also the header's Panel,
  while the existing states belong to a Control template. Moving states
  there would add a theme/state mechanism to remove one more wrapper.
  `Body.HierarchyCell` keeps its own subscription and reads expansion on
  each event, as today; the event carries no reason, and none is added.
- `Body.HierarchyCell` creates its expander only when the row has children,
  keeps the indentation and slot geometry for leaves, and reuses the expander
  when a container changes rows.
- The per-cell `ContentPresenter` stays. Removing it would cut about 15 more
  elements per row, but the host's template root owns its own `Margin` and
  alignment, which may be bound, so the table cannot write the cell padding
  into them. Revisit only with a measured realization cost and a design in
  which `CellsPanel` applies padding and alignment in its own arrange and
  never writes a property of the host's template.

**Risk.** Expander hit testing, theme changes, and native selection and focus
visuals.

**Contract.** None; section 8's visuals stay the same.

### 8. Gestures do bounded work once, not on every step

R11, R12, R15, K5, K10, K11, N3, N5, S7, S8. Each item is small and local. Only the
drag check and the marquee grow with n or the selection.

- **Row drag (R11, K5, N5).** During a drag, every publish calls
  `IsDragCurrent`, which calls `Order.Capture`: it walks the host's whole
  order, builds two sets and sorts. Under a live sort that runs on every
  engine update while the button is held. **Estimate:** 30–60 ms and 5 MB per
  update at 100,000 torrents. At `BeginDrag`, keep `Order.Capture`'s entries
  in a map of the identity part's map type, and check the current order
  against it in one pass, keeping the duplicate check and today's reference
  comparison of each entry, so a row the host replaces with a new instance
  still cancels the drag. `IsDragging` searches the drag packet linearly for
  every realized row on every visuals change; keep the packet in the
  identity part's set type instead, which matches as today's identity
  search does.
- **Marquee (R15, N3).** Every pointer move that changes coverage rebuilds the
  whole resulting selection, resolves it, sorts it and commits it. A
  Shift-marquee from a distant anchor rebuilds the range from the anchor each
  time: an **estimated** 45 ms per move at 100,000 rows, so the rectangle
  falls behind the pointer. `Body.Marquee.UpdateCoverage` already knows each
  index it adds or removes; on stage 1b's selection model, apply only that
  change. Shift mode adds or removes the rows between the old end and the new
  one.
- **Column move (R12, S8).** `Motion.Slide` creates one compositor animation and
  one easing function per realized cell: about 700–1,200 per move. Every cell
  of one column slides the same distance, so create one animation per
  distance and one easing function, and start it on each cell. The motion
  stays the same; check it on screen.
- **Ctrl-marquee (R15, N3).** Each coverage step builds a set of the press-time
  selection again. Build it once when the marquee begins and derive the
  toggled set from the change in coverage.

**Contract.** None.

### 9. Notify only the realized rows and the change in count

P2, N10. Subject to the no-flash checks; built only if its measurement
calls for it.

**Cost now.** Section 5.3 requires one notification for every row that
arrives or leaves, at its true position. A search that replaces 20,000 rows
with 20,000 others raises 40,000 notifications, although the count does not
change. The `Body.View.Reconcile` remarks record 56–113 µs per notification,
layout included, on the original host. Even at 10 µs, 40,000 notifications
take 0.4 s.

**Change.** Outside the realized rows, the list keeps nothing but the count.
So raise notifications only for the realized runs, as a reorder already does,
plus one per row of net count change, at positions after the last run. Place
every other position without a notification, as `PlaceQuietly` already does
for a reorder. After the last run, because the panel shifts its realized
containers for an event before them and not for one after them
(`OnItemAdded`, `OnItemRemoved`): the realized rows then keep their
containers and indices, and the visual test checks that side.

**Decided: build it only when the measurement requires it.** Measure the
list's cost per notification at 1,000 and 2,000 rows, on group A's build (see
"How to execute"). The sample measures it without product code: a plain
`ListView` over a collection of the sample's own that implements
`IObservableVector<object>`, the native event path `Body.View` uses (an
`ObservableCollection` would add interop work the table never pays), timed
per insert and removal at a position outside the realized rows, less the
collection's own cost. That is the list's work for one event, which does
not depend on stages 1 to 4, and the internal `Body.View` stays out of the
sample's reach. Publish time divided by the
number of notifications is not this figure: it includes the whole
pipeline. Build this stage if that cost is above 2 µs, because 50,000 events, a filter change at
the product's 100,000-row target, would then exceed 100 ms in the list, the
limit section 1.1 sets for a visible answer. Rows already on screen
would then change through remove and insert animations inside the runs, where
today the panel shifts its containers. So the stage is accepted only after the
no-flash checks: a review of how the WinUI 3 list
(`3rdParty/microsoft-ui-xaml/src/dxaml`) handles a row placed without a
notification beside animated runs, and a visual test that shows the rows
animate and nothing flashes. If the visual test shows any flash, the stage is
dropped.

An accepted single Add/Remove whose one true-position event already meets the
bound keeps that path, so the panel can shift its existing containers and
preserve its normal anchoring. The broader run script handles bulk changes and
net-zero offscreen edits. The visual scene covers both notifying collections
and whole-source membership publishes, including any changed transition category.

**Contract.** Section 5.3's "every membership change, at its true position",
and section 20's bound, which becomes realized containers plus the net count
change. The sample's `Jobs/JobsPage/Diagnostics.cs` checks the
container-content invariant this stage must keep.

### 10. `RefreshView` names the rows that changed

N9. A public API addition; built only if stages 1 and 2 leave a cost that
matters.

**Cost now.** The torrent list calls `RefreshView()` after every engine
update, and the host knows which torrents changed. The table does not, so
after stages 1 and 2 every update still reads n keys and walks n rows:
**estimated** 1–10 ms at 100,000 rows.

**Change.** Add `RefreshView(IEnumerable<object> changed)`. It passes
`RebuildView` a change of "these rows' values changed", through the same
entry as stage 5's notifications, not a pipeline of its own. For that change
source, sort and selection preparation read keys and interactivity again only
for those rows, move each with stage 2's placement routine, and skip validation
and the index rebuild, because membership did not change. `RefreshView()` stays
for "anything may have changed".
The existing row-visual event can still read the bounded realized rows' facts,
including interactivity for drag or hierarchy visuals. This is O(realized),
not a reason to scan all selected/source rows or retain a new policy cache.

The named-key bound applies while the accepted flat delta is eligible. A due
global settle, held/released membership recovery, stale-source recovery or an
actual-comparer fallback can require the existing whole preparation. The lead
retains those shared paths: keeping another history of current versus kept
keys solely for those episodes adds lifetime and acceptance rules to the
settling owner. Ordinary accepted named updates do not scan all source keys.

**Gain. Estimate.** With about 100 changed torrents per update at 100,000
rows, from 1–10 ms to 0.1–0.5 ms per update.

**Decided: wait for stages 1 and 2.** The overload adds public API that every
host must use correctly: a host that leaves a changed row out of the list
shows that row in the wrong place until the next full refresh. After stages 1
and 2, measure the cost of one update where nothing moved at 1,000 and 2,000
rows and extend it to 100,000. Build this stage only if that cost exceeds one
frame at 60 Hz (16 ms); the estimate of 1–10 ms says it will not.

**Gate result: enabled for group C.** B's unchanged RefreshView medians were
1.1784 and 1.5991 ms at 1,000 and 2,000 rows, with zero comparisons and a
constant 1,880 allocated bytes. Whole-block linear projections are 117.84 and
79.96 ms at 100,000; fitting a fixed overhead plus linear growth still
estimates 42.83 ms. Both methods exceed 16 ms. These are extrapolations from
the measured fixture; no 100,000-row fixture is run.

**Scope decision.** The incremental named-values path is flat-table work,
consistent with stages 2 and 3's retained flat keys and the measured gate.
A hierarchy uses the existing full sibling projection for this overload.
Retaining a second history of every sibling group's keys would add unmeasured
state and invalidation rules to optimize a different workload. This fallback
preserves correct behavior without that cache. The contract states the scope.
Unavailable valid identities are ignored, duplicate inputs are coalesced, and
a null enumerable throws. The overload never adds source membership and keeps
the identity part's existing invalid-input failures.

**Contract.** Sections 5.3 and 9 gain the overload.

## Defects found during the review

- **A fill can give a column NaN width (Q7).** `Table.ScaleToWidth` divides
  the room by the sum of the fitted widths. When that sum is zero (zero cell
  padding, zero minimum width, an empty header and no rows) and the room is
  positive, the factor is infinite and `0 × ∞` gives NaN, which
  `SetColumnWidth` keeps and `CellsPanel` measures with. The arithmetic is
  verified from the code; the runtime failure was not exercised. Fix: handle
  a zero sum before computing the factor.
- **A button that is out of the Tab order loses its pointer isolation (Q8).**
  `Table.HitTest` treats a descendant as interactive only when it is a
  `Control` with `IsTabStop` true. A rich cell's `Button IsTabStop="False"`
  is then row surface: pressing it on an unselected row replaces the
  selection, and moving starts a marquee. Section 7 gives descendant input
  controls their own input, whatever their keyboard behavior. Fix: recognize
  native input controls by kind, keep the `IsRowGestureEnabled` attached
  property for custom controls, and keep passive controls such as
  `ProgressBar` part of the row.
- **Page Up and Page Down are wrong for rows of different heights (Q9).**
  `Table.RowsPerPage` divides the viewport by the first visible row's height.
  A 400-DIP viewport whose first row is 40 DIPs and whose next rows are 200
  DIPs moves ten rows: several viewports. Fix: find the page boundary from the
  realized rows' positions, then move to the nearest interactive row there.
  The torrent list's rows have one height, so it does not show this.
- **The drag threshold ignores the Windows setting (Q10).** Rows and headers
  both use a fixed 4 DIPs (`DragThresholdDips` in `Table` and
  `Header.Strip`). Windows lets the person set the drag distance
  (`SM_CXDRAG`, `SM_CYDRAG`, `SPI_SETDRAGWIDTH`), so a person who raised it
  for steadier clicks still starts a marquee or a column move with a small
  movement. Fix: one internal value reads the system drag distance once,
  converted to DIPs, and replaces both constants; `Table` and `Header.Strip`
  read it.

These are not performance items, and each is independent of every stage.

## Limits that remain after every stage

- **One key read per row per publish (L3, L5).** A host that publishes a new
  list makes the table read every row once: about 1–2 ms at 100,000 rows.
  Host callbacks run on the UI thread (section 5.3), so this cannot move to a
  worker.
- **One notification per row that arrives or leaves (L1),** until stage 9.
- **The selection packet is an array in view order (L4).** Every change to a
  selection of s rows costs O(s), in the table and in the host's
  `SelectionChanged` handler. It follows what the person selected, not n.
- **Realized rows update on every engine update (L7).** `Torrent` raises
  `PropertyChanged("")` on every snapshot, so every realized row evaluates
  every binding again. It is bounded by the 40–80 realized rows and is host
  behavior.
- **Automation reads the whole selection (N7).** `GetSelection` returns a
  peer for every selected row. Returning fewer would be a contract decision;
  the platform list returns them all.
- **A row changed less than 1 second after the previous change appears or
  disappears without animation (W5).** This is the WinUI list's own rule, not
  a flash: the other containers stay. The owner ruling is that the table never
  flashes and never breaks the list's animation; this is the list choosing
  not to animate, so it is within the ruling. The torrent list changes the
  view about once per second at most, and
  only when rows arrive, leave or settle, so the gate closes only when such
  changes fall within a second of each other. The only way around it inside
  `ListView` is to hold changes back until a second has passed, which breaks
  section 9's rule that an arrival appears at once. The plan keeps the
  platform's behavior. The gate counts layout ticks that carry a change, not
  events per tick, so no stage changes how often it closes.
- **No type-ahead (W3).** Neither the table nor the list searches by typed
  characters, so there is no cost today. A type-ahead added later follows
  the owner's word-start search ruling in the root instructions.

## Not planned

- **Millions of rows (F6).** A source whose indexer creates rows on demand
  from a bounded cache, `SortMode.External` for every large source, and a
  selection stored as index ranges, which would change the public `Selection`
  type. `ISelectionInfo.SelectRange` and `DeselectRange` are empty because
  the table's own model owns the selection. No host has such a source today.
- **Sorting in slices between dispatcher turns (Q6).** It would need a
  revised update contract with pending feedback, cancellation and one coherent
  commit, because section 5.3 commits setters synchronously. The worker sort
  of stage 2 reaches the same goal inside the contract.
- **A reset above a size threshold (L1).** It would remove the floor of one
  notification per row, but a reset recycles every container (E4), which is
  the flash the owner ruling forbids.
- **Replacing `ListView` with an `ItemsRepeater` (N10).** It would remove the
  floor of one notification per row, but lose the list's selection, focus, UI
  Automation and item transitions, and the transitions are what the no-flash
  ruling relies on.

## Removed after review

Each of these was proposed and checked, and costs more in code and risk than
it saves at 10,000–100,000 rows:
- **The view's `IndexOf` answering from an index (O5, N6, W1).** The list's
  `ScrollIntoView(item)` reaches the view's `IndexOf`, a linear scan of about
  0.2–0.4 ms at 100,000 rows per key press or reveal: far under a frame. An
  index behind it would also be read while a reconcile has left it out of
  date. `ContainerFromItem` searches only the realized and pinned containers
  (W2), so it needs nothing either.
- **Shrinking the selected set after a large selection (S4).** Clearing its
  bucket array is a memset of about 40 µs at 100,000 rows, and after stage 1b
  `Apply` no longer clears the set on every change.
- **One rebuild on reload (S3).** After stage 1a the second `RebuildView` in
  `Table.OnLoaded` takes the skip path, a few milliseconds per load; merging
  the two calls would touch the detached state, focus restore and the list's
  re-attach.
- **Testing only the containers at the marquee's moving edge (R15).** The
  full walk is about 60 transforms, 0.1–0.3 ms per move, and an edge test
  must stay correct through auto-scroll and through a publish that reorders
  rows under the rectangle.
- **Reusing the drag wheel's slots (K11).** About 36 elements per wheel step,
  at the pace of the person's own wheel: under 1 ms.
- **Fit and column lookups (K10).** Columns times realized rows, only on
  explicit fit commands and column changes.
- **Stopping marquee auto-scroll at the end of the list (S7).** Under 1 ms
  per 50 ms tick, only while the pointer rests in the edge band.

## Execution status

Execution started 2026-11-01. The lead is `/root`; `coder` is the one
continued implementer for groups A–E. The user's model substitution is settled:
use Sol 6.1 with high effort wherever this plan names Opus or Fable.
The lead alone edits this plan and `Build.ps1`; the coder owns `lib/TableView`.
The coder alone builds after the lead releases each reviewed group; the lead
runs the interactive and visual checks. No push is authorized.

- Independent plain plan review and its corrective delta are complete:
  **Sound**, no remaining material findings. Record:
  `V:\temp\TableViewPerformance\plan-review.md`.
- Group A's complete owner list is approved; implementation and source review
  are complete. Initial deep review and focused deltas are clean; latest
  record: `V:\temp\TableViewPerformance\group-a-review-r5.md`.
  The corrected Release project-only build passed with no warnings or errors.
  All 124 tests in the prescribed eight classes passed and the runner exited.
  The lead's nine `RealPointerTests` passed and exited; report:
  `V:\temp\TableViewPerformance\group-a-pointer.trx`.
  P's no-move allocations are constant at both sizes (1,880–2,360 bytes),
  with exactly n key reads on assignment and none on stored refresh. S's
  model-only Ctrl and continued Shift work is constant, with no container
  selection mismatches. The corrected Q check established a focused container
  outside the cache at both sizes, with zero mismatches through Ctrl, Shift,
  scattered/far replacement, marquee, plain selection and source pruning.
  Raw process-heap and map-probe heap deltas are unisolated. Actual retained
  map/list backing is 387,496 and 810,536 bytes: approximately 38.75–40.53 MB
  at 100,000 rows by linear projection, excluding host rows, object overhead,
  visual/native state and caches; future dictionary capacities vary. Three
  populated maps contribute 162,360 and 340,272 bytes of captured-reference
  dictionary backing, rather than one map's cost for the whole table.
  M/Q report: `V:\temp\TableViewPerformance\group-a-mq.txt`.
  The lead inspected all five corrected V baseline scenes and confirmed held
  and drag setup; report and images: `V:\temp\TableViewPerformance\group-a-v.txt`
  and `group-a-{rows,selection,held,drag,hierarchy}.png`. All processes exited.
  Group A is accepted and committed locally as `666f861`; group B follows it.
  Native event-only N measured 5.964 and 9.118 microseconds per event: stage 9
  is enabled for group C, subject to its shipped-runtime no-flash acceptance.
  Initial P/S/N report: `V:\temp\TableViewPerformance\group-a-psn-first.txt`.
  The sample-only correction adds Q for the missing pinned evidence and M for
  actual map/list backing capacities, without repeating accepted timings.
  The retained-memory report counts every populated owner map; the per-map
  captured-key cost is not the whole control's overhead. This plan sets no
  retained-byte ceiling, so the measured retention is a reported tradeoff,
  not a reason to add a shared-key lifetime mechanism outside these owners.
  V uses a backed, themed synthetic table and a small direct hierarchy;
  the earlier `PlanPage` resource failure is a fixture failure, not evidence
  against the hierarchy implementation. Only reviewed, inspected final images
  count as the baseline.
  Typed key extraction and null-key validation live in the identity part;
  source/node maps capture keys once during their shared per-row validation
  pass, and view/selection maps copy them. Identity holds no rows. Ordinary
  candidate `TryAdd` captures and inserts once, with no separate capture state;
  identity sets reuse the map core. Reference identity needs no captured-key
  dictionary. Extra retained keyed-map memory is measured with group A.
  The other owners follow the table above: view-owned index, pipeline-owned
  candidate index and publish version, selection-owned indices and range state,
  hierarchy-owned sibling positions and capture/expansion revisions,
  publication-owned packet, drag-owned selected-numbered fact, row-owned shown
  visual states, and sample-owned measurements. The coder's list names each
  map, field and helper under these owners; no core extension state is added.
- Group B's implementation, source review and required runtime checks are
  complete after group A's local commit. Column holds a stateless typed sort
  definition; one active sorted-order owner holds accepted/candidate keys,
  order and positions, with shared full-sort and placement routines. Its one
  monotonic comparison counter measures the actual algorithm; sample-owned
  selectors count key reads. The sorting owner holds the queued criterion,
  committed-sort revision and settle decision; Table owns the detachment
  revision and the one shared frame step. A frame snapshots its queued request,
  so a newer request waits for its own frame. Held/pinned shown work keeps
  settling pending independently of the already-settled sorted workspace.
  Initial source review confirmed three corrections before the B build:
  explicit sorts prepare the current capture through the pipeline, because a
  rejected notifying update can leave its aliased rows ahead of accepted
  membership; an actual fallback full sort restarts the settling interval;
  and a dispatched frame completion checks the existing lifetime revision
  before clearing, revealing or requesting another frame after detachment.
  Initial record: `V:\temp\TableViewPerformance\group-b-review-r1.md`.
  These corrections and their focused regressions passed all five delta
  review passes; clean final record:
  `V:\temp\TableViewPerformance\group-b-review-r2.md`. The lead read the
  source and both records and released B's one Release project-only build,
  its four prescribed test classes and T measurements. B's first Release
  build passed with zero warnings/errors. Its test run exited after 47 passes
  and one queued-sort reload failure (expected committed b, observed a), then
  aborted because the host crashed. The coder is tracing the loaded/detached
  validity window and crash before a reviewed correction batch. Reports:
  `V:\temp\TableViewPerformance\group-b-build1-{sample,tests}.log`,
  `group-b-tests.trx`, and `group-b-tests-console.log`. T has not run and B is
  not accepted; C's conditional stages await those measurements.
  Native source confirms that `IsLoaded` can become false before deferred
  `Unloaded` advances the logical detach revision. Queued/frame eligibility
  therefore checks the immediate platform fact as well as the existing
  revision; no new lifetime flag is added. The failed reload TRX does not
  record the flag at consumption, so that mismatch's exact ordering remains
  unverified. The CoreMessaging crash is diagnosed separately by an isolated
  unchanged-binary run of the deliberate queued-comparer failure test.
  That isolated run crashed with only
  `AFailedQueuedComparisonRestoresTheCommittedGlyphAndWorkspace` active,
  independently of reload; blame/diagnostics are under
  `V:\temp\TableViewPerformance\group-b-queue-diagnostic` and the adjacent
  `group-b-queue-diagnostic-console.log`. The corrected boundary forwards the
  exception through the existing WinRT reporter instead of letting it escape
  CoreMessaging. The pinned runtime still needs to verify that delivery.
  The IsLoaded and exception-forwarding batch passed all five focused review
  passes; record `V:\temp\TableViewPerformance\group-b-review-r3.md`. The
  lead read the corrected source, test outcomes and record and released one
  corrective build plus the four-class retry, with separate retry artifacts.
  The corrective Release build passed with zero warnings/errors; its retry
  passed all 79 tests (FeatureProof 24, Hierarchy 7, LayoutState 9, Sorting 39)
  in 11.278 seconds and exited normally. The lead checked the logs/TRX.
  Runtime checks confirm original-exception delivery, immediate not-loaded
  refusal, actual lifetime advancement and reload recovery. Reports:
  `V:\temp\TableViewPerformance\group-b-build2-{sample,tests}.log` and
  `group-b-tests-retry\group-b-tests-retry.trx`. T completed and the sample
  exited normally; report `V:\temp\TableViewPerformance\group-b-t.txt`.
  Actual comparison counts at 1,000→2,000 rows were: first 6,942→15,879,
  unchanged 0→0, one changed key 11→12, one arrival 10→11. First-sort selectors
  read exactly n keys. Median allocations were first 441,296→904,944 bytes,
  unchanged 1,880 at both sizes, changed 3,632 at both, arrival 5,304 at both.
  Changed/arrival blocks grew 1.6773×/2.1037×, consistent with linear copying
  rather than another full comparison sort; no exact 2× timing claim is made.
  First-frame medians for all four operations were 11.963–17.813 ms.
  Actual retained backing, including six populated maps and sorting buffers,
  was 950,968→1,972,856 bytes: approximately 95.10–98.64 MB at 100,000 by
  linear projection, excluding object/host/visual/native/cache overhead and
  subject to future dictionary capacities. Other accessible process CPU
  snapshots were nearly unchanged; one process was inaccessible.
  The lead read the raw report and computed both whole-block and fixed-overhead
  projections. Both first-sort and full-refresh gates are exceeded, so change
  6 and stage 10 join stage 9 in C. The two newly enabled items add the plan's
  estimated 4–8 hours; all three conditional items total 6–12 hours beyond
  the base estimate. B is accepted and committed locally as `0ee6a0a`.
  Final record: `V:\temp\TableViewPerformance\group-b-evidence.md`.
  C's approved owners and implementation release are recorded below.
- The owner's WinUI inspection source is now tag `winui3/release/2.6.4`,
  commit `c29edd64ae8b4412d772c5768d835ebfd19b438e`; native files now live
  under `src/dxaml`. C's targeted source read confirms synchronous guarded
  event handling (IICG2 1133–1256), realized/pinned index changes (1341–1498),
  reset recycling (2070 onward), transition-category counts (ICTCP 104–314)
  and the 1,000-ms mutation suppression (14–15, 166–172). The lead confirmed
  the guarded event and suppression paths. No native source is edited/built.
  The lead also confirmed transition retargeting to a
  `CLayoutTransitionElement` (`core/core/elements/LayoutTransition.cpp:1165`).
  `TransformToVisual` includes transition transforms, but an original row
  need not expose the proxy's painted motion. C therefore combines per-render
  container/count telemetry with composed-window video/frame evidence using
  the available `winapp ui record` tool; it adds no sample capture framework.
  The lead also checked native pinned-bound updates, reset recycling and
  transition-category selection in this tag. `core/hw/hwwalk.cpp:207` confirms
  that the transition proxy renders the target subgraph instead of the original
  element, reinforcing the need for composed-pixel evidence.
  Replacement is also synchronous: `OnDataElementReplaced` (IICG2 1938–2060)
  generates/prepares a current realized or pinned row and restores focus/pin.
  This corrects E2's older blanket claim that events never read row content.
  Same-key new-instance replacement must preserve content and focus/pin
  correctness, but cannot promise native container-reference reuse.
  Consolidated native evidence and capture protocol:
  `V:\temp\TableViewPerformance\group-c-native-evidence.md`.
  A further targeted trace identifies a runtime question for L: mixed insert/
  remove load choreography delays incoming rows by 600 ms (`LayoutTransition_partial.cpp`
  1453–1558), while `ThemeGenerator.cpp` 539–562 can hold initial opacity
  through that delay. A full reversal can move every old cached row outside
  the new run. L therefore also captures a disjoint-visible-row sort through
  that delay and arrival motion; nonempty container telemetry alone cannot
  prove there is no painted gap. The gap is a risk, not a confirmed runtime
  failure; no custom animation or overlay is added on that inference alone.
- C's complete ownership proposal is approved with the flat-only named-values
  fast path and correct full hierarchy fallback above. Record:
  `V:\temp\TableViewPerformance\group-c-owners.md`. `Change` carries typed
  whole/collection/values facts without workflow; `Source.Preparation` owns
  incremental eligibility and recovery; sequence owners publish insertion/
  removal counts to the shared `PositionIndex` count reader. All quiet logical
  moves are counted, and arbitrary whole-order rewrites rebuild the index.
  Known immutable built-in default comparisons and well-known ordinal/culture
  `StringComparer` are worker-eligible; host comparers and default-string culture
  fallback stay on UI. Both flat and sibling first sorts are covered, using
  detached worker storage and one stable algorithm. Worker creation/collation,
  identity association, stale-result rebase and commit remain UI work, measured
  separately. Source/ordering candidates are accepted only after throwing
  preparation succeeds. The sample owns bounded capture coordination and
  telemetry; the lead owns composed-window capture and judgment. C coding is
  released; no build/test/launch is released before its deep review.
- C's deep source review and corrective delta are complete and clear.
  Initial consolidated record: `V:\temp\TableViewPerformance\group-c-review-r1.md`;
  final record: `group-c-review-r2.md` in the same folder. The lead read the
  records and affected source. Corrections cover active-sort/visible-target
  and geometry evidence in L, existing `StopPointing` fixture support,
  callback-time glyph measurement in W, qualified named-refresh obligations,
  and small comment/naming/formatting defects. No production behavior defect
  was confirmed by the four specialists or delta reviewer. The lead released
  one Release tests/sample build, then the three-class C test batch and U/W/X.
  Results remain pending; the lead's RealSort and WGC L checks follow the
  coder's normal exit. No C commit is released yet.
- C build 1 completed in 152.019 seconds with exit 0 and no errors. Library
  and sample were warning-free; the tests reported CS8600/CS8602 at the new
  stale-subscription fixture cast, repeated by its two compiler passes.
  `SortHarness.LoadAsync` assigns that nonnull collection immediately before
  the setup callback, so one boundary annotation is justified. No runtime
  check uses this warning-bearing output. The coder corrects the annotation,
  obtains a focused continued-reviewer delta, and reports before the lead
  releases one corrective build (estimated 2–3 more minutes). Logs:
  `V:\temp\TableViewPerformance\group-c-build1-{sample,tests}.log`.
  The annotation's continued-reviewer delta is clear:
  `V:\temp\TableViewPerformance\group-c-review-r3.md`. The lead read it and
  released build 2, followed by the unchanged focused C tests and U/W/X only
  if the warnings disappear. The source remains frozen at that reviewed result.
- C build 2 passed with zero warnings/errors in 176.356 seconds. Its focused
  batch exited normally: 89 passed, one failed, 90 total; all three classes
  appeared (Reconcile 11, Selection 30, Sorting 49). The failed replacement
  guard observed one extra identity read. Native replacement defers clearing
  its old container, whose row remains subscribed briefly. After transient
  keys are cleared, its `IsHeld` query fell back to the host selector.
  The lead rejected an empty-set-only fix as incomplete when another row is
  held. The complete correction keeps captured-key matching and answers an
  unknown reference from the held set's captured references, without calling
  the selector. No cache or longer scratch lifetime is added. Continued delta
  review is pending; no measurements or lead runtime check has started.
  Evidence: `V:\temp\TableViewPerformance\group-c-build2.md` and
  `group-c-tests-first.md` in the same folder. One further build is expected
  (about 2–3 minutes). The continued review found one affected alias consumer:
  `IsDragCurrent` can receive a complete-order supplier's different instance
  of a selected identity. It must resolve that alias through `ResolveItem`
  before testing current interactivity, so a newly held row still cancels the
  drag. Supplier packet/reference semantics remain unchanged. The lead
  approved that one predicate correction and one focused SelectionTests
  regression before the build. Record: `group-c-review-r5.md` in the same
  folder. After clean delta review, retry the failed Sorting method and
  SelectionTests; unchanged Reconcile and other Sorting evidence carries
  forward. U/W/X still waits for those passes and normal runner exit.
  The final correction delta is clear: `group-c-review-r6.md` incorporates
  r5 and the earlier review records. The lead read its affected code, test,
  harness and native deferred-clear paths, and released build 3 plus that
  reduced retry, then U/W/X if successful. No lead capture or commit is yet
  released. The possible pre-existing supplier/current interaction-policy
  distinction in `BeginDrag` is reserved for E's affected gesture-owner read.
  Build 3 passed with zero warnings/errors in 98.537 seconds. The reduced
  retry passed all 32 cases, with zero skipped/aborted cases and normal runner
  exit; the lead read its counters and confirmed no test process remained.
  Together with unchanged first-batch evidence this covers 91 distinct C
  cases. Logs: `group-c-build3-{sample,tests}.log`; result:
  `group-c-tests-retry\group-c-tests-retry.trx`, all in the evidence folder.
  U/W/X is released from that output; RealSort, WGC L and commit remain pending.
- U/W/X completed on build 3 with normal sample exit; raw results are
  `V:\temp\TableViewPerformance\group-c-uwx.txt`. The lead's RealSort batch
  exited normally with two passes and one failure: first mouse click committed,
  but the second rapid click became `DoubleTapped`, which supplies no second
  `Tapped`. The fixture claims independent primary clicks, so it will separate
  them by configured `GetDoubleClickTime`, without changing product gestures
  or extending result timeouts. The WinUI DoubleTapped documentation confirms
  the routing. Enter/Space and drag-without-sort passed.
  The first L recording used WGC successfully (420 samples at 29.98 fps),
  but its baseline had returned to offset 0 after a queued focus reveal; the
  intended index-996 arrival was offscreen and the scene correctly failed.
  Artifacts: `group-c-live-1` in the evidence folder. No animation/no-flash
  conclusion follows. The lead approved the existing A pinned-probe frame
  boundary after focus and before far scroll, plus premise guards. Both fixture
  corrections form one reviewed batch and one expected rebuild (about 2–3
  minutes), then only the failed mouse method and L retry. Production,
  successful functional tests and U/W/X remain unchanged.
- C fixture delta r7 is clear (`group-c-review-r7.md`). Build 4 passed with
  zero warnings/errors in 133.060 seconds; binary fingerprint
  `DF3C27A8C351B21BEB82DB891BB1FDF2FB2B5358A2654631B45879A930C68DCF`.
  The failed mouse method passed on retry with normal exit; earlier keyboard
  and drag passes stand. L2 completed all six phases with the intended
  far-scroll/focused-pin setup, current replacement content and retained-run
  container continuity. WGC recorded 420 samples at 29.985 fps with 49 unique
  images, without truncation. Evidence: `group-c-live-2` in the evidence folder.
  **C is not accepted:** composed frames confirm the full descending reversal
  paints an empty panel. Images at recorder elapsed 7,958 and 8,526 ms are
  empty, between fading old rows at 7,897 ms and subsequent incoming rows.
  This is at least 568 ms of sampled blank time and matches the native mixed
  transition delay identified above. Nonempty containers do not prevent it.
  The lead holds D/E and C's commit. Coder and lead trace the confirmed gap and
  choose the simplest platform-supported correction before editing; no
  artificial notifications, overlay or custom animation is justified merely
  to conceal the native gap. The correction receives source review, one build
  and the affected L retry; successful unrelated checks are not repeated.
- The bounded source investigation found no public native seam that preserves
  every current requirement for a disjoint reversal. Native reorder selects
  suitable choreography through internal methods; private COM calls and fake
  gestures are rejected. The public candidate for immediate updates is an empty
  `ItemContainerTransitions` collection before reconciliation, retained through
  actual layout, with the prior dependency-property value source restored in
  the existing version-current post-Rendering dispatcher callback. Native
  panel lookup reads that collection dynamically; it is not copied per row.
  The suppression covers the layout episode, including any other updates in
  that same tick, and does not itself cancel existing storyboards. Never clear
  a shared effective collection. Detach, retemplate and failure restoration
  must belong to the same table owner. This is source-supported but its pixels
  remain unverified. The lead reported the conflict with contract 5.3 to the
  owner. The owner could not choose the technical mechanism, would ideally
  retain some animation and asked what the correct approach is. Applying the
  plan's delegated design judgment and the owner's no-flash priority, the lead
  settles the narrow immediate-update exception: retain ordinary native motion
  when visible rows survive; suppress the layout episode only for completely
  disjoint visible replacement. This is a lead design decision, not a new
  Owner ruling or a claim that the owner selected a button. It avoids a second
  animation system and a native dependency change. Proposal and independent
  assessment: `group-c-gap-proposal.md` and `group-c-gap-review.md` in the
  evidence folder. Before implementation, settle the concrete eligibility seam
  and lifecycle owners. The contract gains the qualified exception in the same
  correction. D/E and commit remain held until C's visual evidence passes.
  Four C builds have cost 559.972 seconds in total; the fifth is not released.
- The C gap-correction owner proposal is approved and coding is released.
  `Table/View.cs` owns eligibility, the saved surface/local-value record and
  restoration. The final rows over the native panel's actual current visible
  interval are resolved through the old position index using captured keys,
  falling back only to old captured keys for held references. Any identity
  overlap retains native transitions. Empty final sources retain their normal
  empty-state behavior. For shrink, clip the old interval conservatively;
  this does not predict the later native scroll clamp. No duplicate survivor
  algorithm, set, full delta position map or host selector call is added.
  Save once, install a separate empty collection and arm the existing frame
  before reconciliation. Restore in its guarded current-version dispatcher
  branch before reveal/queued-sort consumption; version mismatch rearms the
  same episode. Genuine unload/template detachment restores the recorded
  surface, and original UnsetValue restores with ClearValue. Existing error
  forwarding remains. L adds one separated ordinary visible arrival after
  reversal to prove normal native motion returns. A retained-reviewer delta
  precedes the one corrective build and affected L retry. No other check is
  reopened merely by having another output.
- C gap-correction review r8 is clear (`group-c-review-r8.md`). The lead read
  its full affected-state/lifetime trace and the integrated implementation.
  Captured-key eligibility, original property-value-source restoration,
  overlapping publishes, version mismatch, retemplate between Rendering and
  dispatch, failure, unload and queue refusal have no source blocker. The lead
  released one corrective two-project Release build, then L3 only. Remaining
  questions are disjoint pixels with no empty samples, restored ordinary
  native motion and the fixture's existing content/pin/visibility outcomes.
  Successful functional, RealSort and U/W/X evidence is not repeated. D owner
  preparation may continue source-only; D code and C commit await L acceptance.
- Build 5 passed with zero warnings/errors in 170.841 seconds; fingerprint
  `8143F743281168CB89A3905F63F82EA0558A18B0B612E5A2ADF67FDFACD7ADFA`.
  The lead's L3 attempt failed before its ready marker, at the same far-scroll
  setup predicate: offset 0, visible 0..16, keyboard focus at row zero. No
  recorder started and no owned process remains. Evidence:
  `group-c-live-3\sample-results.txt`. The frame-boundary setup correction had
  passed L2, so this establishes a nondeterministic fixture, not evidence about
  the new transition suppression. The same-failure-after-fix stop applies to
  this fixture path: no further timing patch, rebuild or repeat is released.
  C's transition pixels remain unverified. Source-only workaround preparation
  will isolate the two changed questions (disjoint pixels/restored native
  motion), retaining L2's prior focus/pin/current evidence, rather than keep
  repairing the keyboard-focus/scroll probe. Five C builds total 730.813 s.
- The lead releases a source-only fixture workaround: replace existing L's
  unstable keyboard-focus/far-scroll setup with an ordinary near-viewport
  scene, not another mode or parallel diagnostic. Reuse its handshake,
  per-Rendering content/geometry, bounded phases and WGC recorder. Keep
  baseline, visible notification add/remove, whole-source membership with
  continuity, disjoint descending reversal and a separated restored ordinary
  arrival. The distant-pin/current-instance phase is explicitly outside this
  narrowed retry; its valid L2 evidence stands, without claiming the corrected
  binary reran it. All applicable membership/visibility/container/disjoint and
  restored-property checks remain. This works around the stopped failing
  fixture dependency; it adds no focus delay/retry and changes no production
  code. A retained source review precedes a sample-only build and L retry.
- The near-viewport fixture passed retained review r9, read in full by the
  lead (`group-c-review-r9.md`; scope/evidence limits also in
  `group-c-near-fixture.md`). Production r8 hashes are unchanged. The lead
  released one sample-only Release ProjectsOnly build, then L4 from its printed
  output. No tests, U/W/X, RealSort or stopped keyboard-focus setup repeat.
- C is accepted. Sample-only build6 passed zero warnings/errors (66.984 s
  outer;60.755 s build metadata). L4 completed all six near-viewport phases
  with normal sample/recorder exit. WGC420 samples at29.976fps,48 unique images,
  no truncation. Lead inspected all unique images plus critical full-size
  frames: no sampled empty panel during reversal, immediate current high-name
  rows, and restored ordinary native movement/fading on the next arrival.
  Container/content/membership/visibility checks all passed; whole-source
  membership retained all25 surviving realized containers. Artifacts:
  `group-c-live-4`; full consolidated lead record:
  `group-c-lead-runtime.md`. L2's earlier distant-pin evidence and L4's narrower
  corrected-binary scope remain explicit. Source review r8 covers restoration
  lifetime and captured keys; r9 covers the fixture workaround. Functional91
  distinct cases, complete real-header cycle and U/W/X evidence stand.
  Six C build commands cost797.797 s outer. The first-sort worker leaves linear
  UI capture/commit; projected100k sub100ms UI response is not established.
  C local commit is released, excluding root AGENTS. D's complete owner proposal
  is next; no D implementation before its gate. D's review needs no build and
  its validation joins E as planned.
- The owner challenged roughly eight hours spent on this optimization. The
  lead agrees execution overhead is excessive: repeated design/review cycles
  and fixture repair, rather than the roughly13.3 minutes of C build commands,
  account for the delay. Remaining D/E use concise owner decisions, one
  continued coder, proportional retained review and the one combined final
  build. No further measurement expansion or redundant check is released.
  Existing results and settled design stay settled; the remaining agreed
  stages are not silently dropped.
- D's complete owner proposal is approved; implementation is released. Detail:
  `group-d-owners.md` in the evidence folder. The kept node index and child
  source relations remain Hierarchy-owned; preparation journals touched facts
  and captured keys, rolls back rejected changes, and ends at existing pipeline
  acceptance/finally points. Root assignments still validate the full candidate,
  comparing plain child lists by indexed references and folding structural
  comparison into capture. Child handler identity rotates on accepted root
  reassignment and detach; stale invocation and null sender are handled by that
  identity. Shared empty sources retain every parent; a later duplicated child
  fails the one tree-wide identity-map insertion and rolls back.
  **Lead refinement to stage6:** an expanded child change may run one visible
  projection/selection/reconcile through existing RebuildView/Change.Collection.
  It must not recapture/walk all supplied nodes or clone the kept node index.
  A collapsed change with no visible result accepts membership and forwards one
  presentation/UIA fact without publishing. This follows the hierarchy contract's
  allowed visible projection and avoids a separate incremental tree-edit engine.
  The operation-local grouped projection removes the discarded unsorted flat
  list and repeated expansion/children reads, using the existing sort routine.
  No new flat source/view algorithm or persistent sibling-key cache. One focused
  retained review follows; no D build, tests or measurements before E's build.
- D implementation and retained plain review are complete. Records:
  `group-d-review-r1.md` and clean corrective `group-d-review-r2.md`. The one
  medium finding was a child event bypassing a rejected root capture; the
  handler now marks its child source stale before any throwing capture and
  gives invalid/mismatched current roots priority over child-only preparation.
  The combined rejection/recovery regression covers that sequence. The lead
  read both records and the main implementation/acceptance/cleanup paths.
  Iterative traversal preserves deep-tree behavior; reference parent sets and
  per-relation journals avoid copying shared empty-source parent populations.
  Only two focused regression methods were added; HierarchyTests run with E.
  Preparation.Apply's five inputs are accepted at its existing concrete
  hierarchy/identity/validation seam without an arbitrary options bag.
  D source is accepted and its local commit is released, excluding AGENTS;
  runtime validation remains explicitly pending the combined E build.
- E's concise owner list is approved and implementation may start after D's
  local commit: Row owns its template states/root; HierarchyCell owns lazy
  expander/glyph creation and unchanged geometry. Order retains provider
  enumeration/duplicate validation while gesture state retains captured order
  and packet identity collections; provider aliases resolve current row policy.
  Marquee owns coverage deltas and SelectionState owns incremental modifier and
  range application. Motion owns per-SlideFrom compositor/easing/distance reuse,
  without a global animation cache. Columns owns zero-sum fill; Input owns native
  control isolation and realized page geometry; one shared system-metric value
  owns configured drag distance in DIPs at96DPI as already settled. No alternate
  implementation or new measurement/visual framework. Existing V images and
  ordinary native UI suffice for final appearance/slide inspection. One focused
  retained review and one combined D/E build/check batch remain.
- Q9's concrete navigation choice follows matching WinUI native code
  (`ModernCollectionBasePanel_Interaction_Partial.cpp:222–343`): first move to
  the last fully visible row in the requested direction; at that boundary,
  page the viewport and choose the next realized boundary. Oversized rows still
  advance one item. No first-row-height or index-count approximation remains.
  `Table/Input.cs` owns one operation-local pending `PageNavigation` request
  only when public ChangeView realization is deferred: current item,
  direction/modifiers, view version and exact scroller, with native final
  ViewChanged readiness. It subscribes before ChangeView; rejection or a
  clamped/no-op ends it immediately. Final ViewChanged arms the existing frame
  owner, whose guarded post-Rendering callback consumes the current request
  using actual geometry and the existing interactive-row navigation. New
  requests replace it; changed current/view, new input, unload or retemplate
  clear it and remove its handler. No new scheduler, timer or counter.
  The Rendering handler captures both the exact ready request and its stage;
  its queued callback acts only while both still match. Scroll readiness can
  change without a view version change, so an older frame cannot consume a
  request that became ready after that frame's realization boundary.
  Native ScrollIntoView is void and can produce no ViewChanged event. An
  offscreen reveal therefore also arms the same frame boundary: if the current
  row is visible after layout, paging continues; otherwise the request ends.
  A changed view or current item cancels it at the existing acceptance owner,
  so neither a no-op nor a host change leaves a scroll handler waiting forever.
- E source is accepted after the retained plain review
  `group-e-review-r1.md` (clean, including the WinUI review lens) and the lead's
  affected-owner and consumer pass. The lead found one stale sample reflection
  call to removed SetMarqueeSelection; it now uses the existing checkpoint and
  BeginMarquee/UpdateMarquee/EndMarquee owners. Focused regressions cover zero
  fitted widths, native input outside the tab order, unequal-height paging,
  late readiness after Rendering, and provider aliases' current-row policy.
  One combined D/E Release two-project build and the seven planned functional
  classes are released. Final real-input and appearance/column-slide checks
  remain pending; earlier measurements and visual evidence are retained.
  The first E build found CS1501 in ResetMarquee: PositionIndex has no
  captured-key Resolve overload. No runtime ran. The correction uses its
  existing captured-key IndexOf and the current view row, with retained delta
  review before one corrective build; no new position-index API is needed.
  The correction passed `group-e-review-r2.md`; the combined corrective build
  passed both projects with zero warnings/errors in94.626s (metadata).
  Artifacts are the root `artifacts/bin` release_win-x64 outputs. The coder
  has started the seven-class functional batch on that exact test assembly;
  no runtime result is claimed yet.
  That batch passed167/167 with normal VSTest exit; the lead's real pointer
  and column-drag batch passed12/12 with normal exit. Evidence is in
  `group-e-evidence.md`, `group-e-tests` and `group-e-interactive`.
  V's first capture completed but its one-frame hierarchy image is blank and
  some selection content is mid-transition, so it is not accepted as settled
  appearance. The existing ordinary Project plan fallback instead exposed a
  missing BodyTextStyle resource and closed; its crash log is preserved.
  A sample-only correction is released: use the existing platform text style,
  and disable entrance transitions only for V's static probe before adding it,
  awaiting actual realized content through the existing probe helper. One
  focused delta review/sample-only build/V retry follows; unchanged library
  functional and input evidence carries forward. No production animation
  change, delay, new capture mode or measurement expansion is authorized.
  The lead's ordinary column-move WGC recording already shows intermediate
  header and row positions at1187/1251/1312ms and the correct final order;
  `group-e-slide2` completed240 samples in8010ms, normal recorder exit.
  The static V correction exposed a second fixture setup defect: unrooted
  ApplyTemplate has no row surface. It was removed, restoring the exact
  previously compiled V/WithProbe code; no further setup mechanism or build.
  The verified PlanPage resource correction remains. On that sample-only
  build the lead inspected eight painted hierarchy rows, nested expanders and
  leaf alignment,40-DIP row pitch and native Draft selection. The task sample
  then closed normally. Full lead evidence and scope limits:
  `group-e-lead-runtime.md`. D/E and the full A-E plan are accepted;167
  functional and12 real-input cases passed, with normal process exit.
  First-sort UI capture/commit remains linear; sub100ms at100k is not established.
  Current behavior lives in the library contracts/implementation map. This
  completed execution record is backed up with the evidence before the plan
  and its documentation-guide link are retired in E's local commit.
- C's captured-key seam is approved as a replacement, not a second route:
  replace `IIdentityKeys.Captured` with `CapturedFor(object item)`. Ordinary
  maps/sets/position indexes return their typed map; `Source.Preparation`
  routes to its additions, retained leavers or accepted membership as needed.
  The identity part remains the sole typed-key extractor. This lets existing
  consumers transfer prepared keys without cloning the whole membership map
  or learning source routing. Retain leaver keys through post-accept consumers;
  captured-key availability does not mean the row is in resulting membership.
- C's internal arities are accepted by the lead under the plan's delegated
  design authority. `Source.Preparation` takes eight inputs describing one
  prepared operation and is constructed only by its source owner. The shared
  static `SortIndices` takes six inputs for detached storage, direction and
  comparer/collation semantics. Another argument bag would duplicate the
  existing prepared concept or hide the one UI/worker algorithm's inputs.
  `PrepareSort` keeps its four synchronous preparation inputs plus the queued
  operation context; L's private five-input phase helper adds immediate
  membership proof alongside its existing continuity and disjointness checks.
  Both remain at their existing owners without another options type.
- Source subscription identity is part of the predecessor proof. An earlier
  listener can reassign ItemsSource while an old collection's invocation list
  is being delivered; its already-captured TableView callback must not apply
  old indices to the new assignment. Source retains its subscribed handler,
  and each handler forwards only while it is still that current handler.
  Detach clears that identity before unsubscribing. This also rejects a stale
  callback after suspend/re-attach to the same source and accepts a valid null
  sender, without inferring subscription identity from event sender values.
- W's timing facts live only on its queued operation: `QueuedSort.CaptureTime`
  covers UI preparation/key/collation/association, and `CommitTime` covers UI
  acceptance or rebase through `RebuildView`. The detached result reports its
  own ordering duration. The sample retains the queued object by reflection
  and reads it after observable completion. No public diagnostic API, global
  last-result cache, callback or second event workflow is added; glyph/answer
  frame latency remains a separate measurement.
- Hierarchy worker handoff is approved: `Hierarchy.CaptureWork` gathers sibling
  groups in its projection traversal and owns their parent-to-group association
  in operation-local `Hierarchy.Work`. The root is ordinary group ordinal 0,
  not a fabricated identity key. Generic `SortedOrder.Work.Project` takes an
  ordinal, current siblings/keys and refresh fact; it knows no parent. The
  existing queued-sort context retains workspace/job/capture version and may
  reference that hierarchy-owned operation object. Keep one authoritative
  worker handle and no persistent per-parent sort-key cache.
- New C transient references end in the pipeline's cleanup after view,
  selection and hierarchy consumers, before row/UIA/host delivery:
  `Source.Complete` clears additions/leavers and `SortedOrder.Complete` clears
  operation edits/prepared-key references, retaining only reusable capacity.
  This timing prevents an older publish from clearing a reentrant publish's
  facts. Worker associations keep their own captured keys, not references to
  source scratch maps. Existing A/B accepted/candidate row-buffer retention
  remains unchanged and is reported separately.
- Queued completion captures the actual dispatcher reference on UI before its
  continuation. If that queue refuses posting during shutdown, the queued
  owner atomically compare-exchanges `_queuedSort` to null only while it is
  still that exact request. It reads no UI property and mutates no buffers
  there; a newer request survives, and an already-entered UI callback can
  finish its local operation. No extra cancellation state or wait is added.
  The operation context holds one nonnull generic worker and an optional
  hierarchy-only parent-map association, rather than two nullable job modes.
- Sparse deltas pass final order plus operation-local `Change.Edit` edits
  to `Body.View`. It resolves old edit indices and realized target indices,
  deriving untouched targets from those edits; an O(realized) reference map
  feeds the same run/remove/quiet/insert script as whole reconciliation.
  Ordinary single Add/Remove can use its one true-position mutation instead,
  preserving native shifting/anchoring with the minimum event count; same-
  position replacements retain their existing path. Net-zero offscreen moves
  and bulk replacements follow the sparse policy. All quiet logical moves
  count toward the position window. During partial edits, the existing view-
  local transient lookup verifies canonical references and can scan; it does
  not run a position reindex over an incomplete map or reset its count
  baseline. `SelectionState.EnsureIndices` uses that view-owned lookup, so
  native selected-range reads take the same path. After edits/maps complete,
  ordinary counted lookup resumes. No second sparse algorithm is introduced.
- C capture preparation is settled without another app run: installed
  `winapp ui record` is 0.6.1 and supports `--frames --fps 30 --json`.
  Its [versioned documentation](https://github.com/microsoft/winappCli/blob/v0.6.1/docs/ui-automation.md#record)
  reports the backend and emits `recording-started` on stderr after the first
  frame. The lead releases the fixture's go signal only after that event;
  use its HWND, a bounded duration, timestamped frames and Rendering telemetry.
  Require the reported `wgc` backend for composed-window proof. A PrintWindow
  fallback is not equivalent pixel evidence; screen capture is an available
  alternative only with an unobscured target. Thirty-fps samples show motion
  alongside every Rendering callback's container/count record; report actual
  cadence and gaps instead of claiming every physical display frame was seen.
- Stage 7 uses its permitted fallback: keep `Body.Row` as the one row-state
  owner and make its existing presenter the template root, removing the
  single-child Grid. `CellsPanel` is a Panel shared with headers, while the
  existing row states are Control-template visual states. Moving those states
  into the panel would require a new theme/state mechanism to remove one more
  wrapper. The fallback removes an element while preserving the established
  WinUI state, theme and cursor behavior; lazy hierarchy expanders still apply.
- Q10's drag-distance interpretation is settled for E. Keep axis thresholds:
  horizontal for header moves, horizontal/vertical for row gestures, with
  today's boundary comparisons. Read the configured `SM_CXDRAG`/`SM_CYDRAG`
  values once at 96 DPI through
  [GetSystemMetricsForDpi](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getsystemmetricsfordpi)
  so the shared value is in DIPs without binding it to the first monitor's
  rasterization scale. Use the whole configured distance on each axis, not a
  half-width or diagonal distance. Matching-source `AutomaticDragHelper.cpp`
  67–83 and `ListViewBaseItem_Partial.cpp` 1862–1877 confirm axis comparisons;
  their extra 2x multiplier is a separate native drag policy and is not added
  to Q10's replacement of the existing configured-distance threshold.
- Group A review confirmed that passing flat source membership to a hierarchy
  makes `KeptOrder` reject every kept update and sort inside the interval.
  `Table/Sorting.cs` needs membership of the visible projection, not all nodes:
  a simultaneous collapse and expansion can preserve the count while changing
  that membership. The added `Hierarchy.Project` owner row supplies this map
  from already-captured node keys, with no repeated validation or selector read.
  It is reusable projection workspace, not another accepted source snapshot.
- Group A's paths/flow review confirmed a native replacement callback that
  resolves an index during reconciliation. `Body.View` must verify a stored
  position against its current intermediate rows; only while reconciling it
  may resolve captured old/new instances and scan by reference when the stored
  position is stale. Ordinary lookups remain indexed. Stage 5 replaces this
  temporary linear bound where its counted position window applies.
- A publish rechecks host interactivity, so it ends selection range continuity
  even when row order/version stays unchanged; otherwise a newly interactive
  interior row is missed by a later edge-only range update.
- A notifying source can mutate a snapshot that is read in place before an
  update is rejected. `Body.Source` invalidates that capture's validation record
  before processing a new candidate, while keeping the accepted membership map
  and held-row releases intact; a later refresh then validates the live rows.
- Core/extension integration belongs to `Table`: source lifecycle and assignment
  no longer call `Hierarchy`. The hierarchy observes the assignment version of
  the capture being processed, not a newer rejected assignment. `Table` resumes
  the hierarchy before source resume can publish, preserving child recapture.
- Selection's existing owner separates packet/container commit from UIA/host
  delivery. `RebuildView` finishes hierarchy and its row event before delivering
  the committed notification, because a throwing host handler must not skip
  mechanics. Gesture commits use those same two implementations. No deferred
  notification field or second selection pipeline is introduced.
- Lead source review also confirmed that `ReconcileDrag` cancels through
  `CancelGesture`, whose own row event would duplicate the pipeline's final
  event. Cancellation keeps one implementation and lets this pipeline call
  defer that delivery to the final publish event.
- Project-only reuse fingerprints the evaluated primary target of every named
  project; a missing or changed output cannot count as current. Output-lane
  selection also probes these targets for benchmark/test-host locks. This is
  implemented at the existing `Build.ps1` output/hash owners and joins A's delta.
- Group A's memory harness drains the removed probe's queued realization work
  before the next retained-memory baseline, so collection of that old probe
  cannot be mistaken for savings by the next fixture.
- Confirmed preimplementation corrections for group B: custom structs need
  the actual-comparer fallback (`Schema.SortKey` is unconstrained); queued
  sorts need actual-commit/detach revisions to reject A→B→A and unload/reload;
  worker completion needs current instances, keys and stable base ties,
  because `RebuildView`'s prepared order bypasses the normal sorting path.
  The affected owner row and stage 2 text incorporate these corrections.
- The lead's project-only build switch is `-ProjectsOnly`; it and its usage
  in `docs/agents/compilation.md` join group A's review and commit. Use one `-Project` argument with both project paths as
  an array, because PowerShell does not accept a repeated named parameter.
- Groups C–E remain open. The base estimate remains 10–15 hours, four builds and
  five group reviews; optional stages follow their measurement gates.

## How to execute

The aim is code that needs no refactoring later: every concept in the owner
"Owners" names, implemented once, with state flowing one way. The protocol
checks owners where a wrong one is cheapest to fix: in the coder's list
before the code exists, then in a review before the next group builds on it.
A build is a small cost beside that, so each group builds once.

**Owner ruling: no build or test run during coding or between fixes.** While
an agent writes code or fixes review findings, it does not compile or run
tests to check an edit, a fix or a compiler error it suspects; it reads the
code. Each group's one build, after its review, checks all of it.

### Staffing

One coder writes every group, in order, as one continued agent, because the
groups extend the same owners and their design must stay in one head. The
coder runs on Opus or Fable, named at launch, because it decides owners and
the root instructions let only those models judge a design, with effort set
to `high`: the plan already decides the owners, so the coder applies a
settled design and needs care rather than the deliberation of `xhigh` or
`max`, whose cost repeats on every group. The coder runs
the review commands on its own code, so the review skill launches the
reviewer as the coder's subagent; the coder continues that reviewer from
group to group, so it keeps what it learned about each owner. The lead
checks the coder's owner list before each group is written and the coder's
report after it, and releases the next group.

**Owner ruling: the work never stops to ask the owner.** The plan holds
clear rules, so a question to the owner would only keep the owner busy. The
lead and the coder decide every design, ownership, technical and UX
question themselves, from this plan, the repository's `AGENTS.md` files and
contracts, and Fluent 2 and Windows conventions. Where a judgment needs a
point of view those do not give, the lead roleplays the expert or the user
it needs (a WinUI and Fluent reviewer, a busy person with 10,000 torrents),
decides, and writes the decision and its reason into the plan. Starting this plan authorizes
everything "Before group A" lists. The only stop is one the rules already
name: the same build error or test failure after a fix, which the lead
reports in its final report and works around where it can.

### Skills

- `/codebase-design`: in step 1 of every group, for each type, enum,
  interface or seam the group adds (the identity part, the position index,
  `Body.Source`'s identity map, the sorted order, the queued sort, the gap
  buffer), as the root instructions require.
- `/domain-modeling`: when a group's code names a concept the
  [table vocabulary](../../lib/TableView/CONTEXT.md) lacks, such as the
  identity part, the position index or the queued sort, so the term enters
  the vocabulary once, before the code uses it.
- `/winui:winui-code-review`: added to the reviews of groups A and E, the
  groups that change WinUI surfaces: automation peers in 1b, and the row
  template, `Generic.xaml`, `CellsPanel` and `Body.HierarchyCell` in stage 7.
- `/winui:winui-design`: the lens for stage 7 and 8's visual checks, in
  place of a roleplayed WinUI and Fluent reviewer, because those stages must
  leave the rows looking exactly as they do.
- `/ui-ux-pro-max` is not used: the plan designs no new UI, and Fluent 2
  and Windows conventions decide this product's look.

### Groups

| Group | Stages, and the harness code their measurements need | Review | Build | Test classes | Measurements | Decides |
|---|---|---|---|---|---|---|
| A | 1a, 1b; allocations per publish, the selection model's time, the plain-`ListView` measurement of one event over the sample's own `IObservableVector<object>`, and a notification counter that subscribes to the view's `VectorChanged` (today's attaches only to `INotifyCollectionChanged` and always reads 0) | deep | yes | `SchemaValidationTests`, `ReconcileTests`, `SelectionTests`, `HierarchyTests`, `KeyboardAndGestureTests`, `SortingTests`, `FeatureProofTests`, `AutomationTests` | stages 1a and 1b; the list's cost per notification | stage 9 |
| B | 2, 3; comparer calls per settle and per arrival, the first sort's block, one `RefreshView()`'s block | plain | yes | `SortingTests`, `HierarchyTests`, `FeatureProofTests`, `LayoutStateTests` | stages 2 and 3 | change 6 of stage 2, and stage 10 |
| C | 4, 5, and change 6, stage 9 and stage 10 when decided; the time to remove half the rows, the time per `Add` | deep | yes | `ReconcileTests`, `SortingTests`, `SelectionTests` | stages 4 and 5 | — |
| D | 6 | plain | no: group E's build checks it | — | — | — |
| E | 7, 8, and the defect fixes | plain | yes, for D and E | `HierarchyTests`, `SortingTests`, `SelectionTests`, `KeyboardAndGestureTests`, `ColumnMoveTests`, `ColumnFitTests`, `FeatureProofTests` | — | — |

Why this shape:
- Group A creates most of "Owners" (identity, the position indexes, validation, the
  pipeline entry, the selection model, the publish version, the row event), so
  it gets a deep review before anything is built on it. Group C finishes the
  view and the pipeline, and gets the second deep review, of its own code
  only. Groups B, D and E extend owners that a deep review has settled, so a
  plain review is enough.
- The list's cost per notification does not depend on stages 1 to 4, so
  group A measures it, and stage 9, when needed, joins stages 4 and 5 under
  one review of the WinUI list source and one visual test.
- Stage 10, when decided, joins group C because it adds an entry to
  `RebuildView`, a core owner.
- Group D has no measurement or visual check, so its code waits for group
  E's build; a `HierarchyTests` failure there points to stage 6. A plain
  review is enough for it because stage 6 enters `RebuildView` through stage
  5's notification change, which group C's deep review settled, and adds no
  entry of its own; stage 10, which does add one, joins group C.
- No group runs the whole test project: [testing](../agents/testing.md)
  keeps a full suite for release work or an owner request, and group E's
  classes cover every area the plan changes.

**Estimate.** Five groups: five group reviews (two of them deep), about two
fix rounds each, four builds of about 5 minutes, and perhaps two rebuilds
after a failure. About 10–15 hours of agent time in all (**estimate**), and
none of the owner's. Stages 9 and 10 and change 6, when their measurements
call for them, add about 2–4 hours each to group C.

The lead runs the interactive and visual checks itself, at three points:
- **After group A:** `RealPointerTests`, for 1b's press checkpoint.
- **After group C:** the no-flash visual test for stages 3, 4, 5 and 9 (one
  test, as "Proving each stage" describes), and `RealSortTests` for stage
  2's header click; group C does not build on the header input path.
- **After group E:** `RealPointerTests`, `RealColumnDragTests`, and the
  visual checks of stages 7 and 8.

How:
- The `Real…Tests` classes inject real input, so `.runsettings` excludes
  them; they run with `TestCategory=Interactive` in the filter. They take
  the pointer and keyboard for the minutes they run, so each point runs its
  classes in one batch. A run that reports inconclusive because the session
  is locked is repeated at the next point, and the last point's result is
  reported, not waited for.
- The no-flash visual test records rendering-frame samples in the sample's
  harness, as the Settings capture review does for its motion, because
  settled screenshots alone do not show animation
  ([testing](../agents/testing.md)). Each frame records the realized
  containers and row geometry. Composed-window video/frame samples also
  record the painted motion, because WinUI can retarget the animation to a
  transition proxy while the original row already reports final geometry.
  It passes when rows retained by the reconcile's run/pin decisions keep their
  containers and no recorded frame shows an unexpectedly empty panel (the
  flash, E4), while painted rows take intermediate offsets or opacity across
  frames (the animation). Changes come at least 1
  second apart, or the list does not animate them (W5).
  A genuine replacement of all visible identities does not require reusing
  their departed containers; the arrival, removal and sort scene keeps visible
  retained rows so their continuity can be checked. Same-key new instances
  legitimately raise `ItemChanged`, which regenerates a native container;
  these must show the current content and preserve focus/pin state, rather
  than satisfy an impossible container-reference assertion.
- The visual checks of stages 7 and 8 compare screenshots before and after
  the change, judged with `/winui:winui-design`.

### Before group A

1. One local commit per group, authorized by starting this plan, made by
   the coder when the group's checks pass (group D: after its review, because its checks come with group E's build), holding only `lib/TableView` and
   this plan, never pushed. Each group's review then reads exactly that
   group's code: the uncommitted changes under `lib/TableView`. Without the
   commits, every review would read every earlier group again, and the
   review skill cannot narrow an uncommitted change by lines.
2. `Build.ps1` gains a switch that builds only the `-Project` list, keeping
   its lock, output reuse, folder choice, priority and record. Today `-App`
   always compiles the product app first, and a Release engine when none is
   current: minutes per build for products no check here uses, and a broken
   app change in another session stops every TableView build. The lead
   writes it, reviews it with the group A review, and it ships in group A's
   commit.
3. One `/my-code-review plan docs/plans/tableview-performance-plan.md`,
   unless the plan has not changed since its last plan review, so "Owners"
   is checked against the code before anyone writes against it.

### Each group

1. **Name the owners.** Before writing, the coder lists, for each concept,
   decision and visual the group touches, the owner from "Owners" it
   changes, and for each type, field, map or helper it means to add, the
   owner that will hold it. The lead checks the list against "Owners" before
   any code is written: a concept mapped to the wrong owner costs minutes
   here and a refactoring later. A concept "Owners" lacks is a plan change:
   the lead adds the row before the code is written. Each type, enum,
   interface or seam the group adds goes through `/codebase-design`, as the
   root instructions require.
2. **Write the group's stages and the harness code** its measurements need,
   so the review covers the harness too and no second build follows. No
   build, no test.
3. **Review.** `/my-code-review fix`, with `deep` where the table says, on
   the uncommitted changes under `lib/TableView`. The brief carries this
   plan, not the coder's list, because the coder's view of where things
   belong would anchor the reviewer. It states these rules for this plan:
   - "Owners" already decides the design. A responsibility outside its
     owner, a new owner for a concept "Owners" lists, a second
     implementation of one, an owner that calls into code that depends on it
     outside `RebuildView`'s sequence, a condition that stands for another
     owner's decision, and a core owner that holds an extension's state are
     each a **medium** finding, fixed in the round by moving the code to the
     named owner, whether or not a defect is reachable yet. Such a finding
     does not start the skill's escalation to an audit; escalation applies
     only when "Owners" itself is wrong or has no row, and then the lead
     changes the plan.
   - A row of "Owners" or a requirement of the group's stages that the code
     leaves unmet is in scope, whatever its origin, so an old map or check
     the stage was to delete is not left as "already there".
   - A convention finding in code a later group rewrites is reported, not
     fixed.
   - Check reflection-based test calls against the changed members, as
     [testing](../agents/testing.md) asks: `ReconcileTests` builds
     `ItemIdentity` and `Body.View` by reflection, and `SelectionHarness`
     calls private `Table` methods by name.
   - Each group whose stages change the reconciliation or the notifications
     (B for stage 3, C) checks the WinUI list source for them, as "The table
     never flashes" requires.
   - The fix rounds only read. The skill's build and focused tests after its
     last round are steps 4 and 5 below, done once.
4. **Build once**, from the repository root, with `-Release`, because the
   measurements decide stages and a Debug build overstates them, and every
   group uses it so one output serves all of them. In PowerShell, use
   `& .\Build.ps1 -Release -ProjectsOnly -Project @('lib\TableView\tests\TableViewTests.csproj', 'lib\TableView\sample\TableViewSample.csproj')`.
   On a failure, fix every
   error by reading and build again; the same error after a fix stops the
   group and is reported, as [compilation](../agents/compilation.md) says.
5. **Test and measure from that build.** Run the group's test classes once:
   `dotnet test lib\TableView\tests\TableViewTests.csproj -c Release
   -p:Platform=x64 --no-build --filter <the classes>`, adding
   `-p:ArtifactsPath=<the path the build printed>` when that is not the
   repository's `artifacts\`, so the tests run this build and not an older
   one. Check that the run reports tests from every named class: a filter
   that matches nothing still passes. The test process must exit when its
   tests end. Take the measurements "Proving each stage" lists from the same
   output.
   - **A failing test:** classify it as [testing](../agents/testing.md) says,
     fix it by reading, have the continued reviewer review the fix, build
     once, and run again the group's classes whose subject the fix touched.
     The same failure again stops the group and is reported.
   - **A measurement that misses its target:** the stage is not accepted.
     The coder reports the figures to the lead, who decides the fix by
     reading; it is reviewed as one more round and gets one build.
   - **A visual test that shows a flash or a stopped animation:** the stage
     is not accepted. The fix is designed by reading the table and the list
     source, reviewed as one more round, and gets one build and one visual
     test.
6. **Report and commit.** The coder reports to the lead the review record's
   path, the findings it did not fix or objected to, the test results and
   the measurements. The lead reads the diff where it touches "Owners" and
   checks the material claims, then the coder makes the group's commit and
   the lead releases the next group. Group D commits after its review; group
   E's build checks it, and a fix it needs joins group E's commit.

## Proving each stage

Profile each stage at 1,000 and 2,000 rows, against the built
`TableView.dll`, and record other load on the machine during the run. The
product target is 100,000 rows, but the owner's laptop is shared, so the
small sizes measure how each cost grows, and the 100,000-row figure is that
growth extended. Each measurement records four things separately: the frame
in which the person sees an answer, the longest uninterrupted UI-thread
block, the allocations, and the memory kept afterwards. The sample's
`Jobs/JobsPage/Diagnostics.cs` harness is the starting point.

- Stage 1a: per publish where nothing moved, the allocations stay the same
  from 1,000 to 2,000 rows, and the time grows no faster than the rows (one
  key read and one reference comparison per row remain).
- Stage 2: per settle under a name sort with one changed row, the comparer
  calls stay about the same from 1,000 to 2,000 rows; the glyph shows in the
  frame of the click. The first sort's UI-thread block, extended to 100,000
  rows, decides whether change 6 is built. The UI-thread block of one
  `RefreshView()` under a name sort where no key changed, extended to 100,000
  rows, decides whether stage 10 is built.
- Stage 9's gate: the time inside one `VectorChanged` raise, on group A's
  build, measured on a plain `ListView` as stage 9 describes.
- Stage 3: per publish that adds one row under a sort, the comparer calls grow
  with log n and the time grows no faster than the rows (one copy of the
  order).
- Stage 4: the time to remove half the rows doubles, not quadruples, from
  1,000 to 2,000 rows.
- Stage 5: per `Add` notification, the time grows no faster than the rows
  (at most one block copy), and no notification runs the whole pipeline.
- Stages 3, 4, 5 and 9: one visual test of arrivals, removals and a sort
  under a live source, showing the rows animate and nothing flashes, after
  the code review of the table and the WinUI 3 list source. The lead runs
  it after group C from rendering-frame samples, as "How to execute"
  describes.
- Stage 1b: the selection check listed with its range call. With all rows
  selected, the model update for a Shift+arrow or Ctrl+click stays about the
  same from 1,000 to 2,000 rows; publishing the new packet still grows with
  the selection, and is measured apart.
- Stage 7: a visual check of rows, cells, drag and held states, and a
  hierarchy row.
- Stage 8: a visual check of the column-move slide.
- Each group runs the test classes "How to execute" lists for it. Each stage
  adds a test only for the specific failure the reviews found in it:
  - stage 2: a key selector that returns the row itself, with a comparer by a
    property that changes, re-sorts after `RefreshView`;
  - stage 2: a queued header sort drops itself after a `Sort` assignment, a
    hidden column or an unload, and after a failing comparer the header shows
    the committed sort;
  - stage 1b: "select A, Ctrl+click D, Shift+click C" selects C–D only, and a
    Ctrl+Shift range moved inward keeps rows earlier gestures selected;
  - stage 6: a plain child list changed in place and published with a new
    root array shows the new child.

The 1,000- and 2,000-row sizes stay below the large object heap's 85,000-byte
threshold for some structures. Which structures cross it at 10,000 and
100,000 rows follows from their sizes, so it is computed from the measured
bytes per row, not run at full size.
