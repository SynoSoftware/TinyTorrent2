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
| Optional child membership, subscriptions and sibling projection | [Hierarchy](../src/Hierarchy.cs), configured by `Schema<TRow>.Hierarchy`. |
| Displayed sequence, collection reconciliation and the selected state the list reads | [Body.View](../src/Body/View.cs). |
| Effective layout | [EffectiveLayout](../src/EffectiveLayout.cs). |
| Realized items reported to the host | `RealizedItems` in [Table/View](../src/Table/View.cs), read from the row panel's cache range; the panel's `CacheLength` in [Generic.xaml](../src/Themes/Generic.xaml) sets the margin. |
| Shared header and row arrangement | [CellsPanel](../src/CellsPanel.cs). |
| Row drag/held visuals and cursor | [Body.Row](../src/Body/Row.cs) keeps its state owner and uses a presenter as its template root. |
| Hierarchy cell indentation and disclosure | [Body.HierarchyCell](../src/Body/HierarchyCell.cs) creates its expander on the first expandable row and retains leaf indentation and slot geometry. |
| Selection, current item, anchor, and logical focus | [SelectionState](../src/SelectionState.cs). |
| Row pointer arbitration | [Table input](../src/Table/Input.cs). |
| Held rows and the pointed row's frozen place | [Table holding](../src/Table/Holding.cs), applied by the view order in [Table sorting](../src/Table/Sorting.cs). |
| Marquee geometry | [Body.Marquee](../src/Body/Marquee.cs), driven by the arbiter. |
| Numbered order and drag wheel | [Order](../src/Order.cs) captures host positions; [Table/Reordering](../src/Table/Reordering.cs) owns the gesture snapshot and request; [Body.Wheel](../src/Body/Wheel.xaml.cs) owns popup rendering and gap geometry. |
| Header gestures and generated menu | [Header.Strip](../src/Header/Strip.cs) and [Header.Menu](../src/Header/Menu.cs). |
| Configured row/header drag distances | [DragDistance](../src/DragDistance.cs) reads the Windows axis metrics at 96 DPI for shared DIP distances. |
| Column-move motion | [Motion](../src/Motion.cs) reuses one compositor/easing and one animation per distance within each move. |
| Generic control text | [Strings](../src/Strings.cs), reading the embedded [en.json](../src/Resources/en.json). |

The source capture and displayed sequence serve different roles: one receives
host items, the other presents the private view. Neither owns domain records or
executes host commands. New behavior goes to its existing owner; the table above
does not set a quota of classes or files.

`Table/Sorting.cs` owns setup-only `SortMode`. Its default `Local` projects sort
keys; `External` retains source order while exposing header criteria through
the existing `Sort`, `Layout` and `LayoutChanged` interfaces. The same owner
bypasses settling and local pointer-order pinning for external sorts. Hierarchy
projection, membership holds and selection reconciliation remain shared.

`Column` holds the typed sort definition installed by `Schema.SortKey`.
`SortedOrder` in the sorting owner keeps the active flat order: each shown row
with the key it took its place by, its collation key and its source index, and
a position index over the order. A candidate is accepted only after comparisons
succeed. Its stable index sort also sorts hierarchy siblings. One placement
routine serves every change: each kept row stays, changes in place, leaves or
moves; the rows that move and the rows that arrive are sorted and binary-placed
among the rows that stay, which are copied between those places in native
blocks. A whole change reads every key; a change that names its rows reads only
theirs, unless a withheld key is due. While moves are not allowed a row keeps
its kept key, and the order records that a settle is needed; that record clears
only in a pass that read every kept key. A key without equality (a mutable key
object or a comparer that follows the current culture) always counts as
changed, and before such a change places rows, the kept rows that stay must
prove their order with the actual comparer; if they cannot, every row is
placed again by its current key, which counts as a settle. Entry revisions defer source-index
shifts for stable ties. When that list reaches roughly the square root of the
source length, preparation rebases every entry through the candidate position
index and one source-order walk. The shown order after pointer pinning stays
outside this workspace. The sorting owner makes the one settle decision, for
flat tables and hierarchies, and is the only code that records when rows last
took their sorted places. Header choices carry committed-sort and
detach revisions and run after the shared rendering frame wait. The `Sort`
setter, a queued header sort and layout restoration commit through one routine,
`CommitSort`, the same synchronous commit boundary.
Eligible header sorts capture detached immutable key groups on UI; a worker
takes the text keys' collation keys, orders them with the same stable routine,
and returns through that one frame owner. The queued request holds the generic
job and an optional hierarchy-owned captured parent/group association. Completion checks the exact
request and current revisions, then rebases current instances, keys and base
ties before the ordinary publish accepts it. Capture and commit remain UI work;
no host callback or identity map enters the worker payload. The captured
dispatcher posts completion; refusal releases only that exact queued request.
The pipeline prepares the current source capture before an explicit sort and
passes that preparation into the same publish for acceptance. Failed
comparisons leave the source, criterion and active workspace unaccepted.

`Table/View.cs` also owns the disjoint visible-window transition exception.
It compares final rows in the current panel's visible index interval with old
positions through the view's position index. With no overlap and a nonempty final source,
it saves the surface's local transition value once and assigns a separate empty
collection through that layout episode. The shared Rendering callback
completes that layout and restores the original value source, so a later
publish gets the list's own transitions; detach and retemplate restore the
captured surface. The list itself animates no change that comes within one
second of its previous change (`fastmutationThreshold` in WinUI's
`ModernCollectionBasePanel`), so rows shifted by such a change move at once
with or without this exception. A shrinking source clips
the old interval conservatively; this does not predict a later native viewport
clamp. The reconciliation script and notifications are unchanged.

When no row of a nonempty order is in the view by identity, `RebuildView` sets a
scrolled list's offset to 0 with `ChangeView` after the reconcile, in the same
publish, so the layout that shows the new rows lays them out from the first.
The difference decides from its counts: every view row leaves, every order row
arrives, and it names no moved row. A publish that empties a scrolled view keeps
the rows it showed until the next frame, and a publish that fills the view
before then looks those rows up in the reconciled view instead. `ScrollTo`
records the source assignment it followed. Publishes of that assignment keep
the host's offset, including notifications while a hold delays the list, until
the first publish that shows the source's rows.

`Table/Sliding.cs` slides the rows a reorder moves. When the difference moves
rows, named or not, and animations are on, it records, before the reconcile, the
shown y of each realized row and an edge for each row the cache range will
bring on screen, keyed by instance. A later publish before that frame keeps
these positions and maps any new instance to its row's entry. A layout episode
that a disjoint replacement started records nothing, so a reorder later in it
slides no row of the new list. The publish suppresses transitions as above. The Rendering callback lays the frame out,
then puts each realized container that is on screen before or after back at
its recorded y as a composition translation and animates it to zero with
`Motion`'s duration and curve. The compositor reports no animated value, so a
container's current offset comes from `Motion.Remaining` with the slide's
start time. That value runs one frame ahead of the frame on screen, which is
where the next slide takes over. A container the list prepares for another row
stops its slide. A row that the list's own transition is moving when a reorder
comes starts from its layout slot, because no API reports where the list's
transition shows it.

The [hierarchy extension](hierarchy.md) keeps host expansion state outside the
control. Captured child membership feeds the same private view, sorting cadence
and selection reconciliation as flat rows. `Table/Hierarchy.cs` integrates
navigation and collapse focus; `Body.Surface` adds hierarchy automation to its
existing item peers. Flat schemas do not allocate this source state or cell
chrome. The sample's Project plan page supplies a non-torrent hierarchy with a
large collapsed branch.

`Hierarchy.ChildSource` keeps each child enumerable's items, referring parents
and subscription identity. `Hierarchy.Prepare` reads again only the lists a
change can have altered: every list for a new root assignment, the roots for a
root change, the notified list for a child notification, and any list it cannot
trust. It compares each with its accepted items by identity. Rows that leave
take their subtrees, rows that arrive bring theirs, and a new instance of a row
replaces its node in place; a same-identity instance changes no sibling group.
The result stays in the preparation until `Accept`, so a failed publish leaves
the accepted hierarchy as it was, with nothing to undo. Every child
notification enters the one pipeline as a change, collapsed or not. A change
whose lists all belong to collapsed or hidden parents keeps the view, states no
difference, and updates only the expanders of those parents that are shown. The
projection keeps each expanded parent's sibling order from the previous
publish, as stored derived state, and sorts again only a group that is newly
shown or whose list changed which rows it holds; every group when moves are
allowed for a whole change or a pending settle; and the named rows' groups when
moves are allowed. The same groups supply worker capture.

## Rendering and interaction

The body uses a virtualized `ListView`; the header is a sibling. Header and rows
use the same panel and effective layout. The body's native scroller owns the
vertical axis, and nothing scrolls sideways. Wrapping that list in another
vertical scroller or calculating column positions in a second place breaks those
responsibilities.

The identity module creates typed maps and sets, so value-type keys remain
unboxed. Every lookup reads the row's key through the schema's selector, which
the contract keeps pure, stable and inexpensive; no map keeps keys of its own.
`Table.InstallIdentity` is the one place an identity is installed.
The source classifies each change once. A whole change is an assignment,
`RefreshView()`, a Reset or Move, or any change without a validated
predecessor. Any other change names its rows: those that leave, those that
arrive with their source index, and those to read again, which a same-identity
Replace and `RefreshView(changed)` are. It accepts the change only after
ordering succeeds. Its subscription identity rejects stale callbacks even when
their nullable sender carries no usable identity. Each later step applies one
rule to the rows a change affects and adds work only for its own state: the
hold reads every row for a whole change, rows named by `Release`, or a view that
did not show the whole source; a held row that returns while the rows are
pointed at keeps the held row's index until they are released; the sorted
order reads every key when a withheld change is due. The only list of edits
one step hands another is the order's stated difference, below.
Shared PositionIndex bounds stale positions by each sequence's insertion/removal
counts, rebuilding after about the square root of its length; an index that
still names its row's exact place answers at once. Sibling positions belong to
the hierarchy's nodes.
While the run script's native vector callbacks observe an intermediate view,
its lookup checks the old and new positions and scans by identity only when
neither is current.
The hierarchy owns visible membership for sorting; source capture versions decide
when child snapshots need replacing, without a dependency from source to hierarchy.

The selection model reconciles identity across changing source instances.
Containers reflect that model rather than supplying a second domain selection.
Table publishes one immutable selection after the complete update. Comparing it
with the previous publication decides whether to notify, including replacement
instances and changes in visual order. The model keeps selected view indices in
ascending order, recording the view version they describe. Toggle and continued
range gestures update their changed edges; reconciliation resolves selected and
tracked identities through the view index. An unchanged packet is retained, and
a current-only change reuses its immutable items.
The table commits selection and refreshes hierarchy and row presentation before
delivering automation and host selection events.
Pointer, keyboard, menu, and automation paths must reach the same operations.
Marquee and drag visuals do not decide which gesture is active.
The marquee reports coverage additions/removals and bounds; the selection model
captures its Ctrl baseline once and updates changed coverage or Shift-range
edges. A source publish rebases coverage against the new view.
Order captures provider entries and their identity map once per row drag. Its
one validation pass checks subsequent provider entries without sorting another
snapshot; packet membership uses the gesture's captured identity set. Provider
references remain in the packet while current rows supply interaction policy.

The private view sends native observable-vector insert, remove and replacement
events. It avoids projecting .NET collection-event arguments and their item
lists for every membership change, a measured cost on large Library searches.
The view stores rows in a gap buffer. The order's producer names each version
of its own order and, when it knows, states how it differs from the version
before: the natural order from the notification's indices, the kept sorted
order from its removed, placed and replaced rows, so a row that moved leaves
its old place and arrives at its new one. A difference says whether it names
moved rows this way, so no reader looks them up again. A `RefreshView` of the
accepted capture states no difference. While rows are held without a local
sort, membership waits, so the natural order is the view itself with its new
instances, stated against the view. The view takes a stated difference only
when it shows the version the difference starts from, or when the difference
starts from the view itself. A pointed row that already stands at its place
under a sort keeps the stated difference, unless the difference names moves.
Otherwise, and for an order whose pinned rows had to be put in place, held rows
under a sort, a resort that placed every row again, a hierarchy or a new sort,
it compares itself with the order in one walk of references, looking up a view
index only where the two differ. When the difference names every changed row,
by no more rows than the list has realized containers, it removes the leaving
rows highest first and inserts the arriving rows lowest first, updating its
position index with each; a named moved row is removed and inserted again. A
walk that finds shared rows in another order names no row, and that change, or
any larger one, runs the
realized-run remove/quiet-place/insert script, with count events after the last
surviving run. In both, a new instance of a row that stays replaces the old one
without an event, and the table sets the realized container's content to it,
so the row element rebinds in place. The list fills a container it prepared
from the instance it recorded then, after the preparation event or, for a
scrolling placeholder, in a later pass; each container's update callback puts
a newer instance back after that fill. The list's own container record keeps
the instance it generated the container for, so the table finds a container's
row, and a row's container, by view position (`RowItem`, `RowContainer`), and
an item automation peer names the generated instance (`ContainerItem`) so the
list finds its container; the peer reads the current row by identity. Native callbacks see true
intermediate rows and counts. A failure part-way indexes the view again as it
stands, so the next publish compares with the view as it really is.

The private view also implements `ISelectionInfo`, answering from the table's
selection model. The list reads a row's selected state from it when it builds
the row's container and after every range call, and keeps no selection of its
own, so a reconcile leaves nothing to re-apply. A container re-reads its selected
state only when its visual state re-runs: when its stored selected value changes,
on pointer and focus changes, and when it loads. Container preparation, range
calls and the list's own gestures write that stored value, so a commit that adds
or removes selected rows refreshes the containers once. A range call also moves
the list's `SelectedIndex` to the first selected row, and a move from -1 makes the
list focus and scroll to that row while the rows hold keyboard focus. The refresh therefore sets `SelectedIndex`
itself when it differs, which refreshes the containers without that move, and
makes a range call otherwise, so the list never scrolls to a selected row on the
table's behalf.

The current arbiter has `None`, `Pressed`, `Marquee`, and `RowDrag` phases. Earlier
notes describing a `Committed` phase and calling the marquee during a row drag
are stale; inspect the current transitions before proposing a fix. Deferred click
decisions preserve the press/release contract, not an accidental processing delay.
Page navigation uses realized row bounds. A deferred request listens for the
scroller's final ViewChanged and runs after the next Rendering boundary, guarded
by both request identity and stage. Public ScrollIntoView can produce no scroll
event: its native panel command invalidates measure, so a reveal also arms that
same frame once and continues only if its row then intersects the viewport.
Otherwise it ends. Changed view/current, new input, unload and retemplate detach
the request's handler.

Keep reconciliation bounded by the containers the list actually holds. The
implementation uses remove/insert notifications rather than `Move`, and never
clears the displayed collection, because both make the whole list flash; the
contract's owner ruling in section 5.3 says what a change to this mechanism
must prove. Collection count and container identity must remain coherent
through intermediate updates.

## Defaults and platform behavior

The default template must give an ordinary result with columns and a source;
hosts should not have to repair missing defaults. Header and cell insets share
one value. The contract's restriction on new named style resources does not
prohibit a local spacing default inside the template. Theme-sensitive colors
still use the platform resources required by the contract.

`Table.QueueFill` coalesces load, width and ancestor-visibility notifications into
one fill after visible layout, then detaches its layout listener. It uses the
same fit and scale as `FillWidth`, without raising a persistence notification.
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
are registered while loaded and removed on unload; ordinary row updates and
scrolling do not request a fill.
Widths stay in the table's effective layout for its lifetime. `ColumnLayout`
contains only order, visibility and sorting, so neither disk persistence nor a
host's in-memory layout cache carries widths into another table instance.

Keep virtualized work on realized rows. Fit and fill measure the header, visible
rows, and one already-realized row above and below the viewport; column motion
uses the whole cache. Fit and fill own measurement; routine
sorting, scrolling, and gesture feedback do not create a hidden measurement tree.
Rich cell templates receive row items directly. Live values use live bindings;
formatting can stay lazy so invisible cells do not allocate display strings.

`Header.Cell` owns generated full/short label selection. Its fit uses the short
label when provided; normal layout uses the full label whenever it fits.
`Header.Content` measures the label and glyph together and allocates a trailing
glyph slot only when the chosen label leaves room. This keeps fitting independent of sorting while retaining an
indicator wherever the actual column width permits one.

The native container paints selection and keyboard focus. The row template
adds only drag opacity; it does not paint an additional selected-row stripe.
Actual contrast, Narrator,
and out-of-process automation still require relevant runtime evidence under the
[testing policy](../../../docs/agents/testing.md).

## Localisation and source lifetime

`Strings.Load` prepares an immutable catalogue with parent/English fallback
and placeholder validation. `Table.Strings`, `Column.DisplayName` and `Column.Header`
refresh existing presentation through the table's text path. Generated menu items
subscribe while open. The standalone default remains English. The product's
captures switch language live with the table present, but they check retained
input and focus, not that headers and menus show the new text.

`Body.Source` suspends collection subscriptions on unload, reads notifying
sources again on reload, and reads an indexable source in place instead of
copying it. A hierarchy keeps its own copy of the roots, because each capture
compares its structure with the previous one. Explicit detached setters reconcile
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
