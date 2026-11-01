# TableView performance plan

**Open.** This plan keeps TableView responsive with medium and large sources:
10,000 to 100,000 rows that the host updates often, usually under a sort.
Scrolling, selection, sorting, filtering, updates and every other interaction
stay responsive as the row count grows, and memory and processing cost follow
the work actually required, not the size of the source. The
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
- cold reviews the owner ran: K1–K11, Q1–Q11 and S1–S8.

A cost marked **estimate** comes from reading the code. Nobody has measured
it yet. Where reviews estimate the same cost differently, the range covers
both.

## The table never flashes

Every stage keeps the contract's owner ruling in section 5.3: the table never
flashes and never breaks the list's animation, so the view never raises
`Move` and never resets the list. A stage
that changes the reconciliation or the notifications the view raises (stages
3, 4, 5 and 9) is accepted only after a code review of the table and of
the WinUI 3 list source in `3rdParty/microsoft-ui-xaml/dxaml`, and a visual
test that shows the rows still animate and nothing flashes. This holds for any
idea a review proposes, however much it saves.

The source is evidence for the design, not proof for the shipped runtime. The
table ships with `Microsoft.WindowsAppSDK.WinUI` 2.3.9, and the checkout in
`3rdParty/microsoft-ui-xaml` is a later `main`, commit `8da148dcd`. No source
tag maps to 2.3.9, and the files below changed after the source was
published: #11912 (2026-09-24) fixed add and delete transition sequencing,
and #11887 (2026-10-08) added an opt-in for collection `Move` notifications.
So the visual test on the shipped runtime decides every no-flash question.

What the list source shows at `8da148dcd` (files under `dxaml/xcp/dxaml/lib`):
- **E1.** The list chooses the animation per layout tick from the number of
  inserts and removals in that tick
  (`ModernCollectionBasePanel_ICTCP_Partial.cpp`,
  `TransitionContextManager`), not from their order or arguments. So the
  same kinds of event in the same tick give the same animation.
- **E2, E3.** Each `VectorChanged` is handled inside the call: the panel reads
  the change kind and index, updates its own containers and keeps nothing.
  It does not read the view's rows during the event. So the view must be in
  the state that matches each event when it raises it; building the final
  list first and raising the events afterwards cannot work.
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
the row count. The rows the table keeps cost little memory: about 40 bytes
per row, for the view list and one position dictionary. The problem is the
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
| Skip the reconcile when nothing changed | `RebuildView`'s `order.SequenceEqual(View)`; `Body.View.IsAlready` checks again only after that fails | n reference comparisons; the selection steps below still run on the skip path (A4) |
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

## Stages

Build order: stages 1a, 1b and 2 to 8 in the order listed, stage 9 and stage
10 only if the measurements named in them require it, and the defect fixes at
any time.
Ranked by return on effort. Stages 1–4 keep today's pipeline: one full pass
per publish, but each pass costs much less. Stage 5 changes the pipeline and
builds on stages 1–4. No stage changes what the person sees, except stage 2's
header acknowledgement and stage 3.

### 1a. A publish where nothing moved costs almost nothing

F2, R1, R4, R8, O1, O4–O6, P3, K2, K3.
This is the path the torrent list runs on every engine update. It needs only
the existing tests.

- **Each row's key is read once per publish (R1, P3).** Read `keys[i]` once
  at the start of `RebuildView`. The same pass does `ValidateRows`' null and
  duplicate checks: a duplicate is a failed `Add`.
- **Validate only what entered (review).** `RefreshView()`, every settle
  tick, `Release`, a hold release and `SortInterval = 0` all rebuild from the
  stored snapshot, which was validated when it was assigned or notified.
  Section 5.3 makes assignment and notification the only ways rows enter, so
  skip validation when `RebuildView` runs on the stored snapshot: about 2–4
  ms of the floor at 100,000 rows (**estimate**).
- **Three typed maps, kept between publishes (F2, R1, O1, P3, K2, review).**
  One publish needs three key spaces at once, so one map cannot serve every
  step:
  - the view as it stands, before the change: `Hold`'s and `Release`'s
    `shown`;
  - the new snapshot: the duplicate check, `Hold`'s and `KeptOrder`'s `live`,
    and `Pin`'s current instance;
  - the new order: `Body.View.Reconcile`'s `place` and `_positions` after
    `AcceptOrder`.

  The new order differs from the snapshot whenever a sort, a held row or a
  pin applies. Keep two order maps, the old view's and the new order's, and
  swap them after the reconcile; today `AcceptOrder`'s `_positions` and
  `Reconcile`'s `place` are the same map built twice. Skip the snapshot map
  when the order is the snapshot. Each map is a field that is cleared, not
  reallocated, so it keeps its capacity. If one map served every step,
  `Hold` would see the new rows as already shown and would never hold a row.
  `Table` stays non-generic for XAML, so `Schema<TRow>.Key<TKey>` creates the
  typed part behind an internal interface.
- **Check "nothing changed" first (K2).** `RebuildView` builds `_positions`
  in `AcceptOrder` before it checks whether the order equals the view. Keep
  `_positions` when the final instances and order are the same. Selection and
  held-row reconciliation still run. Candidate state stays separate until
  validation and the comparisons succeed.
- **No boxing of value-type keys (R4, O4, K3).** The typed part hashes and
  compares `TKey` directly and does the null-key check. The Library's `long`
  key boxes on every hash and comparison today: an **estimated** 7–35 MB of
  short-lived garbage per publish at 100,000 rows. In the host, `Peer.Key`
  builds a new string on every call; reading the key once per publish removes
  most of those calls. Without a key, identity stays the object reference.
- **Pooled working arrays (R1).** `present`, `slots` and similar per-call
  arrays come from `ArrayPool`. `Pin` builds its output once instead of a
  slots array plus a result list (K2).
- **Read a typed list directly (O6).** `Body.Source.Rows` reads every `IList`
  through the non-generic indexer, which for an array goes through
  `Array.GetValue`. The torrent host publishes arrays. Use a source that is
  already an `IReadOnlyList<object>` as it is (any `T[]` or `List<T>` of a
  reference type is one), and move the null-row check into `ValidateRows`'
  pass. A hierarchical table never calls `ValidateRows`, so add the same check
  to `Hierarchy.Capture`'s root loop; without it a null root fails first in
  the row-type check with the wrong message, in the host's key selector, or
  in `nodes.ContainsKey`. This corrects the no-copy change that is not
  committed yet.
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
  No event changes. Stage 1b supplies the selected view indices; until then,
  build the ranges from the selected set through the new-order map.

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
- **Publish only what changed (R3, O2, K4, A1).** `CommitSelection` keeps the
  published packet when its instances, order and current row are the same,
  and raises `RowVisualsChanged` only when the selection, the view order, the
  held rows or the drag state changed, when `RefreshView` was called, or when
  the hierarchy changed. The last two matter because `Table.CanInteract`'s
  remarks promise that `RefreshView` makes the rows read their interactivity
  and cursor again, and `Body.HierarchyCell` reads expansion.
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
  `RefreshContainers` compares the realized rows before and after, instead of
  building two sets of the whole selection. Stage 5 depends on this gate:
  without it, applying each notification makes every realized row update its
  states once per notification (C3).
- **Drag-ability is computed once (R17, K5, N1, Q2).** `Table.CanStartDrag`
  runs for every realized row on every commit, and on every container bind,
  so on every row a scroll recycles. Section 20 forbids work that scales with
  all rows during scrolling, and this breaks it: after Ctrl+A in a seeding
  list, every bound row scans the whole selection, an **estimated** 1–3.5 ms
  per row at 100,000 selected. Beside that scan, `Order.CanReorder` walks the
  host's whole order until it finds two numbered rows: O(n) when fewer than
  two torrents are queued, which is usual for a seeding list. Compute "the order allows a
  reorder" once per order refresh and "the selection holds a numbered row"
  once per commit; `Row.UpdateStates` reads both. `Row.UpdateStates` also
  skips `GoToState` and the cursor when they did not change (K9).
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
  publish; walk them only when expansion changed. It removes dead weak
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
2. **Sort only the rows whose key changed (R2).** The table keeps each row's
   key from the last sort. On a publish where the rows and their base order
   did not change, it reads each key and compares it with the kept one using
   `EqualityComparer<TKey>.Default`, an ordinal check much cheaper than the
   sort comparer. This check is used only when `TKey` is a value type or
   `string` and the comparer is the default one or a well-known
   `StringComparer`. Then the key is immutable and the comparer is a fixed
   function of it, so a key equal to the kept one keeps its place. Any other
   case may break that: the selector can return the same mutable object with
   a changed value (`SortKey(column, row => row, byRank)`), equality can
   differ from the comparer, or a custom comparer can read host state that
   changed. For those, check whether the kept order is still sorted, using
   the actual comparer and the base order as the tie-break (n−1 comparer
   calls), and sort everything when it is not. `SortKey`'s contract then needs
   no new restriction. No key changed: skip the sort. k keys changed: take those k
   rows out, sort them, find each one's place by binary search over the kept
   keys of the rows that stay (k·log n comparer calls), and build the new
   order in one pass that copies the runs between those places (n moves, no
   comparer calls). Inserting each row into the array instead would move
   k·n/2 references: at 10,000 changed rows of 100,000, 0.25–0.5 s, slower
   than the full sort (review). A membership change keeps the kept keys:
   stage 3 drops the leaving rows' keys and places the arrivals. Only a
   changed base order of the same rows sorts everything, because the base
   order breaks ties.
   The kept order is
   the sorted order before `Pin`: `Pin` puts held rows and the pointed row
   back at their old indices, so the shown order is not sorted while a row is
   pinned, and a binary search over it would place rows wrongly (A4).
3. **Culture string keys sort by collation key (R9).** When the comparer is
   a culture-aware `StringComparer` (`StringComparer.IsWellKnownCultureAwareComparer`
   gives its `CompareInfo` and options), keep each row's
   `CompareInfo.GetSortKey` and sort by ordinal byte comparison. The order is
   the same as the comparer's. The keys cost one `GetSortKey` per row when a
   name first sorts or changes, and about 5–10 MB at 100,000 rows while that
   column is sorted. Opus proposed a public `SortKey` overload that takes a
   `CompareInfo` instead (N4); detecting the comparer gives the same result
   with no API change.
4. **The header click answers first (R9, K7).** The header gesture shows the
   new sort glyph, waits for `CompositionTarget.Rendering`, and enqueues the
   sort from there, as `Table.OnLaidOut` does for a reveal. A sort queued
   straight from the click can run before the next frame and block it, so the
   glyph would not paint first (the remarks on `Table._layoutPending` record
   why the wait goes through `Rendering`). The `Sort` setter itself stays
   synchronous, as section 5.3 requires; only the gesture waits for the frame.
   If the comparisons then throw, the setter commits nothing, and the header
   puts back the glyph of the criterion still in force.

   One field owns the pending header sort. A second header click replaces
   it. Anything else that sets the sort or the columns before it runs
   cancels it and puts the header back: a `Sort` assignment, a layout
   restore or `ResetLayout`, hiding the column, a new schema, and unloading.
   When it runs, it applies only if it is still the pending request, and it
   sorts the source as it stands then, not as it stood at the click.
6. **A first sort that would block too long runs on a worker (R2, R9, N4,
   L2).** At 100,000 rows the first sort of a name column still takes an
   **estimated** 100–250 ms after the glyph shows, and the UI does not respond
   during it. The keys are read on the UI thread, as section 5.3 requires;
   sorting collation keys or numeric keys runs no host code, so that part can
   run on a worker and the result is committed on the UI thread, superseded
   by any later request like the pending header sort. **Decided: build it
   when stage 2's measurement, extended to 100,000 rows, puts one sort's
   uninterrupted UI-thread work above 100 ms.** A host comparer that is not a
   culture `StringComparer` still sorts on the UI thread, because it is a
   host callback.
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
calls the sort for each group of siblings and changes with it.

### 3. A row that arrives or leaves does not re-sort the table

Builds on stage 2's kept keys and collation keys: the search for each
arrival's place reads them, so it never calls the culture comparer per row.

**Cost now.** Under a reordering sort, a publish that adds or removes even one
row re-sorts every row at once (`KeptOrder` returns null when the row count
changes). This costs a full sort, and it reshuffles the whole table
immediately: the reshuffle that the 3-second settle interval exists to stop.

**Change.**
- A row that leaves: drop it from the shown order. Removing a row cannot make
  the remaining order less sorted, so no sort is needed.
- Rows that arrive: sort only the arrivals (k log k), find each one's place by
  binary search over the kept keys of the last sorted order, before `Pin`
  (k·log n comparer calls), and build the new order in one pass that copies
  the runs between those places (n moves, no comparer calls). The other rows
  stay where they are until the next settle. A merge that compares at every
  step would call the Name column's culture comparer about 100,000 times for
  one arriving torrent at 100,000 rows: 30–100 ms (review).

**Decided: merge.** Inside the interval the shown order is the order of the
last settle, placed by the keys kept from that settle, and values change in
between. So an arriving row lands where those kept keys put it. That is its
sorted place when the values around it did not change, and it can be far from
it when they did: in an ascending order whose shown values are now
`[100, -1, -1, …]`, a new row with value 0 lands before 100, at the top. The
next settle, at most 3 seconds later, moves it to its place with the other
rows. Today the arriving row is exact and every other row jumps at once,
which is the reshuffle the settle interval exists to stop. Under the merge,
nothing else moves, and only the new row may need to move at the next settle.
That is the better trade for a person whose pointer and eyes are on the
rows.

This changes the positions at which `Membership` raises its inserts, not
their kind or number. By E1 and E3 the animation and the panel's work stay the
same. The no-flash checks apply: on screen, an arrival under a live sort
animates in at its merged place and nothing else jumps.

The `KeptOrder` remarks record why an earlier merge was removed: it scanned
the shown order once per arrival, about 1.4 million comparer calls when a
filter brought 900 rows back. The merge above compares each arrival once per
step of one pass, so that cost does not return.

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

**Open question.** The list control's own cost per notification is **not
measured** (L1). One notification per row that arrives or leaves stays,
because `IObservableVector` has no range event and a reset flashes (E4). The
work per event is constant (W4): an **estimated** few µs to 30 µs. At the top
of that range, a filter change of 50,000 rows costs 1.5 s in the list alone at
100,000 rows, and a change of 5,000 rows costs 150 ms at 10,000. Measure it
with this stage; the result decides whether stage 9 is built.

**Contract.** None.

### 5. Apply the change, not the whole source

F1, F6, K8, C3, C4, N8, N11. Subject to the no-flash checks.

**Cost now.** Every notification runs the full pipeline, whatever changed.
`Body.Source` does not read the event args.

**Change.** Carry the notification's action, indices and items into the
existing pipeline. Apply `Add`, `Remove`, `Replace` and `Move` from the args,
and keep full validation for `Reset` and a new source. A host `Move` reaches
the view as a removal and an insert, never as a `Move`, because the view never
raises one. Each step then costs
the size of the change:
- validate only the added rows;
- under a sort, place arrivals by stage 3's merge;
- update stage 1a's order maps for the change, not rebuild it;
- carry the selection over only for rows that left or were replaced;
- feed the same hold, selection and notification owners, with no second
  implementation.

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

**Depends on** stage 1b's "publish only what changed" gate (C3). Without it,
1,000 arrivals in one tick make every realized row update its states 1,000
times: about 120,000 `GoToState` calls.

**What it does not fix.** Both product hosts publish a new list: the torrent
list's `Finding.Project` and the Library's `Browser` (H1, H3). A replacement
list must still be read whole, so they gain nothing from this stage alone. To
gain, a host must raise per-row notifications, and the torrent host would
then need its own diff of its projection, which is O(n) unless it tracks
membership per torrent (C4). This stage is mainly for general use of the
library.

**Child collections too (N8, Q11).** In a hierarchy, every child-collection
notification runs a full capture of the supplied tree and a full rebuild, so
a host that fills a child `ObservableCollection` with k rows pays k full
captures: adding 1,000 children to a 10,000-node tree visits and allocates
over ten million nodes, even under a collapsed folder. Collecting the
notifications per dispatcher turn is rejected for the same reason as for the
root source. Apply child notifications the same way: insert or remove the
child's rows under its parent when the parent is expanded, and only record
the change when it is collapsed.

**The order map after a change (N11, review).** Every removal needs the row's
view index, because `Body.View` removes by index and the view index differs
from the source index under a sort, a held row or a hierarchy. After each
change, a stored index is off by at most the number of changes since the
last rebuild: a row stored at p now sits between p − removals and
p + inserts. Find it by searching the view by reference inside that window,
and rebuild the map once the change count reaches about √n (about 316 at
100,000 rows). That costs about 1 µs per change at 100,000 rows and needs no
new data structure. Rebuilding on every lookup below a stale mark instead
would cost O(n) per removal in random order: 1.5–2.5 s for 1,000 removals at
100,000 rows. The kept sort keys move with the view positions, in a second
gap buffer beside stage 4's list, so an arrival under a sort can still search
them. Stage 1b's selected view indices are not shifted on every change:
a change marks them stale, and the next read rebuilds them from the selected
set through the order map, once per commit, not once per notification. An
order-statistic tree is not needed; it would make every `View[i]`
read, the list's included, cost O(log n).

**The bound this stage gives.** One notification no longer runs the whole
pipeline. Its cost is the change's own comparisons (log n per arrival under a
sort), its one native event, and at most one move of the view's gap to the
change's position: a block copy of at most n references, about 40–80 µs at
100,000 rows (**estimate**). The order map's rebuild every √n changes adds
O(√n) per change when spread over them. So 1,000 single `Add` calls at
100,000 rows cost tens of milliseconds, where today each one runs the whole
pipeline.

**Contract.** Section 5.3 already allows an incremental update. Section 20
gains this bound: a source notification costs its own comparisons and event
plus at most one block copy of the view, never a rebuild of the view. The
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
  to it, and `RefreshView()` reuses captured membership. The file browser's
  `FileNode.Children` is a plain `List` that `FileSelection` changes in place
  when a folder loads or closes, so keeping its cache without that comparison
  would hide the loaded children. To remove the second rebuild per publish,
  `FileNode.Children` becomes a notifying collection, so that stage 5's child
  notifications carry its changes; then `FileSelection` stops assigning a new
  root array when only values changed, and one publish runs one rebuild. The hierarchy extension's claim that "the
  Files model already supplies" stable notifying collections is wrong today
  and is corrected with this stage.
- Project the visible tree once, with the sort, and answer membership from
  that list.
- Compare children with an indexed loop.
- Keep the node index and the child-source relations between captures. A
  child notification updates that collection and the descendants it adds or
  removes, and checks their identities against the kept global index. The
  first capture and a new root source still validate everything.
- Let `AcceptOrder` write depth and sibling position into stage 1a's new-order map
  instead of a second dictionary.

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
- `CellsPanel` sets its own opacity and cursor for the drag and held states
  and listens to `RowVisualsChanged` itself, so the row template becomes
  `CellsPanel` alone and `Body.Row` is removed. If that proves too large, at
  least make the presenter the `Row` template's root and remove its
  single-child `Grid` (K9).
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
  update at 100,000 torrents. Keep a map from item to captured position at
  `BeginDrag`, and check the current order against it in one pass, keeping the
  duplicate check. `IsDragging` searches the drag packet linearly for every
  realized row on every visuals change; give the packet an identity set.
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

P2, N10. Subject to the no-flash checks; built only if stage 4's
measurement calls for it.

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

**Decided: build it only when the measurement requires it.** Stage 4
measures the list's cost per notification at 1,000 and 2,000 rows. Build this
stage if that cost is above 2 µs, because 50,000 events, a filter change at
the product's 100,000-row target, would then exceed 100 ms in the list, the
limit section 1.1 sets for a visible answer. Rows already on screen
would then change through remove and insert animations inside the runs, where
today the panel shifts its containers. So the stage is accepted only after the
no-flash checks: a review of how the WinUI 3 list
(`3rdParty/microsoft-ui-xaml/dxaml`) handles a row placed without a
notification beside animated runs, and a visual test that shows the rows
animate and nothing flashes. If the visual test shows any flash, the stage is
dropped.

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

**Change.** Add `RefreshView(IEnumerable<object> changed)`. It reads keys
and interactivity again only for those rows, moves each by stage 2's binary
search, and skips validation and the index rebuild, because membership did
not change. `RefreshView()` stays for "anything may have changed".

**Gain. Estimate.** With about 100 changed torrents per update at 100,000
rows, from 1–10 ms to 0.1–0.5 ms per update.

**Decided: wait for stages 1 and 2.** The overload adds public API that every
host must use correctly: a host that leaves a changed row out of the list
shows that row in the wrong place until the next full refresh. After stages 1
and 2, measure the cost of one update where nothing moved at 1,000 and 2,000
rows and extend it to 100,000. Build this stage only if that cost exceeds one
frame at 60 Hz (16 ms); the estimate of 1–10 ms says it will not.

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
  movement. Fix: read the system drag distance once, converted to DIPs, for
  both.

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
  characters, so there is no cost today. A type-ahead added later searches
  the kept sort keys or the identity index, not the rows.

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
  rows, decides whether change 6 is built.
- Stage 3: per publish that adds one row under a sort, the comparer calls grow
  with log n and the time grows no faster than the rows (one copy of the
  order).
- Stage 4: the time to remove half the rows doubles, not quadruples, from
  1,000 to 2,000 rows. The time per notification, extended to 50,000
  notifications, decides whether stage 9 is built.
- Stage 5: per `Add` notification, the time grows no faster than the rows
  (at most one block copy), and no notification runs the whole pipeline.
- Stages 3, 4, 5 and 9: a visual test of arrivals, removals and a sort under
  a live source, showing the rows animate and nothing flashes, after the code
  review of the table and the WinUI 3 list source.
- Stage 1b: the selection check listed with its range call. With all rows
  selected, the model update for a Shift+arrow or Ctrl+click stays about the
  same from 1,000 to 2,000 rows; publishing the new packet still grows with
  the selection, and is measured apart.
- Stage 7: a visual check of rows, cells, drag and held states, and a
  hierarchy row.
- Stage 8: a visual check of the column-move slide.
- Each stage runs the existing tests that cover what it touches, not all
  four suites: `SortingTests` for stages 2 and 3, `SelectionTests` for 1b,
  `ReconcileTests` for 4 and 5, `HierarchyTests` for 6, and the test process
  exits when the tests end. Each stage adds a test only for the specific
  failure the reviews found in it:
  - stage 2: a key selector that returns the row itself, with a comparer by a
    property that changes, re-sorts after `RefreshView`;
  - stage 2: a header sort that is still pending is dropped by a `Sort`
    assignment, a hidden column or an unload, and a failing comparer puts the
    glyph back;
  - stage 1b: "select A, Ctrl+click D, Shift+click C" selects C–D only, and a
    Ctrl+Shift range moved inward keeps rows earlier gestures selected;
  - stage 6: a plain child list changed in place and published with a new
    root array shows the new child.

The 1,000- and 2,000-row sizes stay below the large object heap's 85,000-byte
threshold for some structures. Which structures cross it at 10,000 and
100,000 rows follows from their sizes, so it is computed from the measured
bytes per row, not run at full size.
