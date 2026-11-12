# TableView implementation

Source map reviewed as a whole on 2026-10-04; later commits edited individual
sections. This describes the existing TableView library and
the reasons for its construction. The [contract](tableview-contract.md) owns
observable behavior and the public API; this map is not a competing specification.
The sample and tests exist. No new rendering or runtime verification was performed
for this documentation review.

## Owners in the current code

| Responsibility | Implementation |
| --- | --- |
| Public control, template lifetime, composition | [Table](../src/Table.cs) and its parts in [Table/](../src/Table/). |
| Source subscription and coherent source capture | [Body.Source](../src/Body/Source.cs). |
| Optional child membership and subscriptions | [Hierarchy](../src/Hierarchy.cs), configured by `Schema<TRow>.Hierarchy`. |
| Worker ordering, holds, sorted merge and hierarchy projection for one update | [Body.Projection](../src/Body/Projection.cs), from the source snapshot and UI state captured by [Table/View](../src/Table/View.cs). |
| Each row's slot and what updates keep by slot: view index, sibling place, source numbering, stale sort keys; committed on the UI thread, or, for a list that shares no row with the view, swapped for a key map the worker built | [Body.Slots](../src/Body/Slots.cs); the column's kept sort keys are [Body.SortKeys](../src/Body/SortKeys.cs). |
| Worker collection-edit planning, from what the list holds (`Changes.Screen`) | [Body.Changes](../src/Body/Changes.cs). |
| Displayed sequence, applying prepared edits and the selected state the list reads | [Body.View](../src/Body/View.cs). It has two faces: `Rows`, every row the table shows, and the collection itself, the positions the list has been told of. |
| Telling the list a change in count in slices, and showing a different list's rows in the containers below the screen | `CatchUp` and `QueueRebind` in [Table/View](../src/Table/View.cs). |
| Effective layout | [EffectiveLayout](../src/EffectiveLayout.cs). |
| Realized items reported to the host | `RealizedItems` in [Table/View](../src/Table/View.cs), read from the row panel's cache range; the panel's `CacheLength` in [Generic.xaml](../src/Themes/Generic.xaml) sets the margin. |
| Shared header and row arrangement | [CellsPanel](../src/CellsPanel.cs). |
| Hierarchy cell indentation and disclosure | [Body.HierarchyCell](../src/Body/HierarchyCell.cs), created only for the hierarchy column, with a `SubtleButton` as its expander. |
| Selection, current item, anchor, and logical focus | [SelectionState](../src/SelectionState.cs). |
| Row pointer arbitration | [Table input](../src/Table/Input.cs). |
| Pointer and release input | [Table holding](../src/Table/Holding.cs); [Body.Projection](../src/Body/Projection.cs) prepares holds and the pointed row's frozen place. |
| Marquee geometry | [Body.Marquee](../src/Body/Marquee.cs), driven by the arbiter. |
| Numbered order and drag wheel | [Order](../src/Order.cs) captures host positions; [Table/Reordering](../src/Table/Reordering.cs) owns the gesture snapshot and request; [Body.Wheel](../src/Body/Wheel.xaml.cs) owns popup rendering and gap geometry. |
| Header gestures and generated menu | [Header.Strip](../src/Header/Strip.cs) and [Header.Menu](../src/Header/Menu.cs). |
| Generic control text | [Strings](../src/Strings.cs), reading the embedded [en.json](../src/Resources/en.json). |

The source capture and displayed sequence serve different roles: one receives
host items, the other presents the private view. Neither owns domain records or
executes host commands. New behavior goes to its existing owner; the table above
does not set a quota of classes or files.

`Table/Sorting.cs` owns setup-only `SortMode`. Its default `Local` projects sort
keys; `External` retains source order while exposing header criteria through
the existing `Sort`, `Layout` and `LayoutChanged` interfaces. The same owner
bypasses settling and local pointer-order pinning for external sorts. The worker
receives those decisions and the effective interval rather than deriving them
again. Hierarchy projection, membership holds and selection reconciliation remain
shared. A hierarchy snapshot travels with its projection request and is accepted
only with that request's current result; no separate pending snapshot owns it.

The [hierarchy extension](hierarchy.md) keeps host expansion state outside the
control. Captured child membership feeds the same private view, sorting cadence
and selection reconciliation as flat rows. `Table/Hierarchy.cs` integrates
navigation and collapse focus; `Body.Surface` adds hierarchy automation to its
existing item peers. Flat schemas do not allocate this source state or cell
chrome. The sample's Project plan page supplies a non-torrent hierarchy with a
large collapsed branch.

## Rendering and interaction

The body uses a virtualized `ListView`; the header is a sibling. Header and rows
use the same panel and effective layout. The body's native scroller owns the
vertical axis, and nothing scrolls sideways. Wrapping that list in another
vertical scroller or calculating column positions in a second place breaks those
responsibilities.

The selection model reconciles identity across changing source instances.
Containers reflect that model rather than supplying a second domain selection.
Table publishes one immutable selection after the complete update. Comparing it
with the previous publication decides whether to notify, including replacement
instances and changes in visual order.
Pointer, keyboard, menu, and automation paths must reach the same operations.
Marquee and drag visuals do not decide which gesture is active.

The private view sends native bindable-vector insert, remove and replacement
events. The nongeneric sender uses a fixed interface ID instead of generating a
generic vector ID for every callback. It also avoids projecting .NET
collection-event arguments and their item lists for every membership change,
a measured cost on large Library searches. It raises every event with one
reused arguments object, as WinUI's own vectors do, and takes a new one only
when a listener still holds the last.

The private view also implements `ISelectionInfo`, answering from the table's
selection model. The list reads a row's selected state from it when it builds
the row's container and after every range call, and keeps no selection of its
own, so a reconcile leaves nothing to re-apply and the list never scrolls to a
selected row on the table's behalf.

The current arbiter has `None`, `Pressed`, `Marquee`, and `RowDrag` phases. Earlier
notes describing a `Committed` phase and calling the marquee during a row drag
are stale; inspect the current transitions before proposing a fix. Deferred click
decisions preserve the press/release contract, not an accidental processing delay.

Keep reconciliation bounded by the containers the list actually holds, and
membership by the positions its cache reaches; past them the view tells the list
only the change in count, after the update (contract section 5.3). The positions
the list holds are read once and kept until `ContainerContentChanging` or a view
change can have moved them, because each read walks the panel with two native
calls per container. The
implementation uses remove/insert notifications rather than `Move`, and avoids
clearing the displayed collection. Earlier work observed whole-list flashing on
`ObservableCollection.Move`; preserve this constraint. If new evidence differs,
report it before changing the mechanism. Collection count and container identity
must remain coherent through intermediate updates.

## Defaults and platform behavior

The default template must give an ordinary result with columns and a source;
hosts should not have to repair missing defaults. Header and cell insets share
one value. The contract's restriction on new named style resources does not
prohibit a local spacing default inside the template. Theme-sensitive colors
still use the platform resources required by the contract.

`Table.QueueFill` coalesces load, width and ancestor-visibility notifications into
one fill after visible layout, then detaches its layout listener. It uses the
same fit and scale as `FitWidth`, without raising a persistence notification.
`PrepareView` calls `FillViewport` after it applies a sort command's first
order (`Ordering.Changed`) or the first rows after a fill that found none
(`_filledEmpty`). During a preparation that call only sets
`_fillPending`; the preparation's end queues the fill, which runs from
`LayoutUpdated` after the layout that realizes the new rows. WinUI raises
`LayoutUpdated` inside that layout and lays out again what the handler changes
before the frame renders (`CLayoutManager::UpdateLayout`), so the new rows and
the new widths appear in one frame. After a different list, `PrepareView` calls
`QueueListFill` instead, which restarts the one-shot 300-ms `_listFillDue`
timer; its tick calls `FillViewport`. A tick during a preparation takes the
`_fillPending` path above, and every `FillViewport` call stops the timer,
because any fill serves the list on screen. A fit
leaves out a row whose cells do not show the view's row at its index, because
the rows below the screen show a different list's rows only after
`QueueRebind`. It reads the cells' row (`CellsPanel.DataContext`), not the
container's `Content`: the container can hold the new row while its cells still
show the old one. `FillViewport` owns the automatic fill switch: it returns at once
while `IsAutomaticFillEnabled` is off.
`Table/Columns.cs` handles successive size changes with a one-shot 150-ms timer.
The first change discards the fitted widths; following changes reuse them and
the empty columns that measurement found, restarting the timer each time.
The scale uses one fill minimum for both empty-column slack and width clamping:
the declared minimum, or the unscaled fitted width when `CanShrink` is false.
Width clamping also uses a fill maximum: the unscaled fitted width when `CanGrow`
is false, and none otherwise.
When the size settles, the timer performs a fresh fit at the final bounds. Explicit
fit commands still measure immediately. This applies to every source of width
changes without window or host gesture tracking. It avoids repeated unbounded
content fits, leaving WinUI's normal measure and arrange intact. Unloading stops
the timer.
Collapsed ancestors defer the fill until the table is shown. Visibility callbacks
are registered while loaded and removed on unload; ordinary row updates, the
re-sorts of a sort already in force, and scrolling do not request a fill.
Widths stay in the table's effective layout for its lifetime. `ColumnLayout`
contains order, visibility, sorting and the automatic fill switch, never widths,
so neither disk persistence nor a host's in-memory layout cache carries widths
into another table instance.

Keep virtualized work on realized rows. Fit and fill measure the header, visible
rows, and one already-realized row above and below the viewport; column motion
uses the whole cache. Fit and fill own measurement; routine
sorting, scrolling, and gesture feedback do not create a hidden measurement tree.
Rich cell templates receive row items directly. Live values use live bindings;
formatting can stay lazy so invisible cells do not allocate display strings.

`Header.Cell` owns generated full/short label selection. Its fit uses the short
label when provided; normal layout uses the full label whenever it fits.
`Header.Content` measures the label and the sorted column's glyph together and
always arranges the glyph, so a fit includes it and the label takes the rest.
The glyph is collapsed on every other column and takes no room.

The native container paints selection and keyboard focus. The row template
adds only drag opacity; it does not paint an additional selected-row stripe.
Actual contrast, Narrator,
and out-of-process automation still require relevant runtime evidence under the
[testing policy](../../../docs/agents/testing.md).

## WinUI list behavior the design depends on

These facts come from WinUI's source in `3rdParty/microsoft-ui-xaml` and from
traces of the product's stress replay in November 2026. A design that works
against one of them was built and removed at least once, so a new design names
the fact it relies on or the measurement that shows the fact has changed.

- **One notice per row.** `IBindableObservableVector` has no range notice, so
  a list that changes by 1,000 rows takes 1,000 notices, and contract 5.3
  forbids a Reset. Each notice reaches .NET through `Surface`, a managed
  `ListView` subclass that exists for its automation peer, and makes four
  `ComWrappers` calls. A list that is not a managed subclass would save about
  a tenth of a notice's cost and lose the automation peer, so `Surface` stays.
- **One update is one UI block.** The list lays out the changed rows and
  starts their animations in one pass on the UI thread: about 130 to 165 ms
  for a new sort order of a screen of rows at the stress load. No other UI-thread code
  runs in that block, input included. In the native trace of 8 November, WinUI
  spent 31.9% of a new order drawing each row in a transition again from
  nothing, 19.8% drawing the other rows and 31.5% in layout; reconciling the
  view took under 2 ms. The list has no move, so a moved row is built a second
  time.
- **Row animations run on the compositor.** The UI thread starts a row's
  transition once and the compositor plays it (`DoubleAnimation.cpp:121`,
  `timemgr.cpp:346`), so rows keep animating smoothly while the UI thread is
  busy.
- **A removed container is not reused until its animation ends.** A change
  that removes every held row therefore needs as many other containers at
  once. Going from all peers to one torrent's peers removes the 16 rows the
  list holds; where 10 of the 16 other containers were not spare, building
  them took about 85 ms of cell measure.
- **The list recycles containers it no longer needs.** In a 60-row test the list
  recycled its containers below the screen in the layout after a sort and filled
  them again, with the right rows and selection, within 1.5 s. Below the
  screen, `RebindRows` therefore matters only for containers the list keeps,
  such as a focused row's. The list keeps its recycled containers in a queue
  until its items source changes (`ModernCollectionBasePanel_IICG2_Partial.cpp`,
  `RefreshImpl`).
- **A moved row does not keep its container while the list animates.** The list
  has no move, so a moved row is one remove and one insert. The insert takes the
  last container in the recycle queue with no look at the row it showed
  (`GenerateContainerAtIndexImpl`), and binds and measures the row again: about
  13 ms for a torrent row at the stress load, for each of the 10 to 30 rows a
  new sort order moves. While the list plays its delete animation, which it
  skips when the previous change came less than a second earlier, the removed
  container reaches the recycle queue only when its leave transition ends
  (`OnItemRemoved`), after the insert of the same update has taken another one.
  Choosing the container in `ChoosingItemContainer` cannot change that. A store
  of recycled containers that kept their rows was designed on 11 November 2026
  and dropped before commit: it kept rows the list no longer shows bound and
  measured at every update, and on the animated path it helped only rows that
  came back later.
- **A hit test runs any waiting layout first** (`CUIElement::HitTestEntry`
  calls `UpdateLayout`), and so does `TransformToVisual`. A click or a marquee
  step pays for a layout that is waiting, whatever dispatcher priority queued
  its cause. Only a smaller pending layout makes the click faster.
- **The list builds rows inside the UI tick,** until the time since the
  previous frame passes its budget, 40 ms by default
  (`BUDGET_MANAGER_DEFAULT_LIMIT`). `ListViewBase::SetupContainerContentChangingAfterPrepare`
  sets a prepared row's content and measures the row with all of its cells at
  once, unless a handler marks the event handled. A press that arrives during
  that tick waits for all of it.
- **Splitting row work does not make it smaller.** Rows filled in phases, the
  first cell at once and the other cells from the list's phase callback or one
  row per Low-priority dispatcher item, were built and removed on 9 November:
  the median click was slower in all 15 pairs. During fast clicking the UI
  thread is busy most of the time, so each piece ran where the next press
  landed and added its own layout pass. Only a cheaper cell makes a row
  cheaper: the cost is WinUI inflating each cell's `DataTemplate`, binding it
  and measuring its text.

## Localisation and source lifetime

`Strings.Load` prepares an immutable catalogue with parent/English fallback
and placeholder validation. `Table.Strings`, `Column.DisplayName` and `Column.Header`
refresh existing presentation through the table's text path. Generated menu items
subscribe while open. The standalone default remains English. The product's
captures switch language live with the table present, but they check retained
input and focus, not that headers and menus show the new text.

`Body.Source` suspends collection subscriptions on unload, recaptures notifying
sources on reload and retains plain snapshots. Explicit detached setters reconcile
logical selection and ordering without touching containers. A snapshot is
validated before it is accepted; the table does not undo an assignment when
validation or a host handler throws. Source lifetime still needs a focused runtime check.

## Keyboard and automation integration

Source review found these gaps on 2026-10-03. Their implementation now follows
the existing owners. The first product journey verifies realized-row selection,
visible row focus, pointer selection, keyboard Pause/Resume and live text.
Broader virtualization, Narrator and RTL scenarios remain unverified.

- **Automation selection:** [Table.Selection](../src/Table/Selection.cs)
  restores its own selection after every unsolicited native `SelectionChanged`.
  [Body.Surface](../src/Body/Surface.cs) supplies list and item data peers whose
  selection actions request `Table.Selection`, including unrealized rows. Cached
  item peers resolve the current keyed row and enforce selection mode and row
  interactivity. Unsolicited native selection remains rejected, preserving
  deliberate pointer arbitration.
- **Automation exposure:** [Header.Cell](../src/Header/Cell.cs)
  and [Header.Strip](../src/Header/Strip.cs) expose HeaderItem and Header peers
  outside the content view. Sortable headers share one operation across pointer,
  keyboard and automation input and raise its invoked event. The row surface
  extends native list automation and the table peer supports drag notifications.
- **Keyboard location:** [Generic.xaml](../src/Themes/Generic.xaml)
  enables native row focus. Before reconciliation removes containers, physical
  focus moves to the list in pointer state; the existing logical focus owner
  restores row focus afterward. Verify this in the
  [hands-on row review](tableview-contract.md#8-rendering-layout-and-visual-language),
  preserving visible keyboard location without reintroducing focus movement to
  unrelated rows or controls. The implementation remains incomplete against the
  keyboard contract until that correction has relevant runtime evidence.
- **RTL header navigation:** [Header.Strip](../src/Header/Strip.cs)
  maps Left/Right according to `FlowDirection`, following mirrored visual order.
  Include them in the existing
  live-language exercise. [XAML already mirrors the coordinate frame](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.frameworkelement.flowdirection);
  do not add a second panel-mirroring path based only on this input defect.

The lifetime and automation findings follow concrete source paths. The RTL
consequence also depends on documented platform mirroring. The product
evidence does not establish Narrator, detached reload or unrealized-row behavior.
The later item-data and header-peer changes have source review only. The focused
`AutomationTests` cover item selection, keyed replacement and selection policy;
their four tests passed on 2026-10-06, and they do not establish out-of-process
provider routing.

## API verification

The API upgrade has two independent source reviews. The library, sample and tests
build. The first milestone's noninteractive run passed 207 checks.

Existing selection checks now cover replacement-instance notifications and packet
order. Duplicate-source rejection also checks that the selection remains intact.
One regression covers a previously unguarded failure:

- `InitialStateResolvesTogetherWithTheLatestSort`: initial selection and the first
  displayed order must use the completed schema and final sort, without evaluating
  a superseded saved comparer.

The recorded run includes these checks. Live localisation and the first keyboard
journey also ran in the product; detached reload, wider automation behavior and
full caller ergonomics still need their relevant runtime evidence.

The existing non-interactive context check also covers a selection callback that
makes its row non-interactive without replacing it. Context invocation rechecks the
same interaction policy after the callback; otherwise the host receives a menu
request for a row it just made unavailable.

## Earlier evidence

The [archived design](archive/tableview-design.md) preserves platform investigations
and rejected alternatives. Its old type names, proposed templates, open spike
questions, and deleted-harness measurements are historical. Use current source
and reproducible evidence when revisiting those decisions.
