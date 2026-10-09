# TableView contract

- Status: component design specification
- Scope: version 1
- Audience: engineers building dense, interactive WinUI 3 data lists
- Normative content: sections 1–20 and the appendix

This is the reusable control's behavior and public API authority. The
[implementation map](tableview-implementation.md) records the existing code and
known gaps; the contract does not claim that every requirement is implemented.
[Product architecture](../../../docs/architecture.md) owns application scope, and
[testing](../../../docs/testing.md) owns validation scope.

The [hierarchical rows extension](hierarchy.md) is the authority for optional
child rows. This contract continues to govern shared behavior and flat tables.

`Table` is a reusable WinUI 3 control for large, changing collections whose
cells need arbitrary XAML content. It combines native collection virtualization
and selection with shared column layout, sorting, layout persistence, and
row-reorder requests. It fills the gap between a basic list and a
spreadsheet-style grid without taking ownership of application data or actions.

## 1. Purpose and intended experience

For users, `Table` is a familiar desktop table: they can scan dense rows,
interact with rich controls inside cells, sort data, change column order and
visibility, resize columns, select ranges, and return to their saved layout.

For application developers, the control is declarative and bounded: supply
items, stable identity when needed, column definitions, and XAML templates;
handle a small set of domain-neutral events; persist the layout snapshot in the
host's chosen store.

The component provides:

- arbitrary rich controls and layouts inside cells;
- virtualized rows;
- sorting from column headers;
- column drag reordering;
- column resizing and explicit fit-to-current-content actions;
- column hide/show from a header context menu;
- persisted column order, visibility, widths, and sort;
- single, extended, range, keyboard, and optional marquee selection;
- row activation and row context menus;
- drag reordering of one or more selected rows when its host accepts reorder
  requests;
- loading, empty, and no-results presentations;
- aligned headers and rows.

The component deliberately keeps these behaviors cohesive rather than exposing
a collection of unrelated helpers. The rest of this document defines their
observable contract and ownership boundaries.

### 1.1 The table must never feel sluggish

The owner's ruling. Where the table would leave the user watching an unchanged
screen while it works, that is a defect, whatever else is correct.

**Do the work after the acknowledgement, and show whatever is ready.** The
answer and the work are different things. Acknowledging an action at once and
finishing it over the following frames, off the UI thread, or progressively, is
correct. A partial result that keeps up beats a complete one that arrives late.

**What sluggish means here, in the owner's own words.** Two behaviours, both
observed on the original host, and both about work the table does after an input
rather than about when an input is answered:

- a sort header that shows nothing for more than 100 ms after the click;
- a column resize that draws no guide line while the frame rate falls so far
  that the pointer itself stops tracking, and the width appears to move once a
  second or two. The reference for this one is a spreadsheet: it draws a cheap
  line where the edge would land and applies the width on release.

**What it does not mean.** It does not mean that a gesture must decide at the
press what it can only know at the release. Sections 14 and 16 make a press wait
in three cases, each so that a drag can keep something the press would have
destroyed. Those waits last the user's own button hold, they are what makes a
drag of an unselected row leave the selection standing, and removing them was
tried once and rejected. Do not trade them for latency again without a
measurement that names the code being paid for.

### 1.2 These rules state outcomes; the mechanism is the implementer's

Most of what follows describes what the user must be able to observe. Where a
rule instead prescribes *how* — a particular wait, a particular order of
operations, a particular structure — it is a defect in this document unless it
is one of two things: a contract a host binds to, which is section 5's public
surface and the event payloads; or a mechanism whose price is stated here beside
it.

An unpriced mechanism has already been implemented faithfully into a worse
product here: recorded in `AGENTS.md`, a token rule was applied to a literal
default, the header cell's padding was deleted, and the control rendered
misaligned until a host wrote a style. The rule read perfectly. It did not say
what it cost, so nobody weighed the cost, and it was obeyed into a defect.

The reason is what makes a rule arguable. A rule with a stated reason can be
checked against the case in front of you and raised as a finding when it does
not apply. A bare instruction cannot — the next reader assumes somebody had a
purpose, and implements it forever.

Raised as a finding, not removed. The opposite mistake has also been made here,
and it is the more expensive one. Section 14 and section 16 make a press wait
for the release in three cases. An implementer judged those waits unpriced,
priced them at the button hold, removed them, and rewrote this document and
`AGENTS.md` to match — attributing the change to the owner, who had not asked
for it and does not call the button hold sluggish. A rule you believe is wrong
is a question for the owner. It is never a licence to change behaviour and then
edit the specification into agreement.

So: a rule that says what must be true belongs here. A rule that says how to
make it true belongs here only with its price. A rule you think is mispriced is
a finding to raise, not a thing to delete.

## 2. Scope and intentional non-goals

`Table` MUST NOT know about:

- domain entities, domain state names, or a particular row view-model type;
- domain commands, domain context-menu content, navigation, or application
  workflows;
- RPC, polling, remote search, optimistic domain updates, or storage;
- page chrome, host-specific embedded modes, or outer page layout;
- a host's localization resources or theme-token system.

Those concerns belong to the host. `Table` owns presentation and interaction
mechanics only.

This is a dense item table, not a spreadsheet. Version 1 does not include:

- in-place spreadsheet-style cell navigation;
- column grouping, frozen columns, summaries, formulas, or pagination;
- multi-column sorting;
- arbitrary grouping (optional child rows follow the [hierarchy extension](hierarchy.md));
- local text search, text-match semantics, a built-in search box, or a
  domain-filter editor;
- data export;
- a separate visual theme, token system, styling framework, or plug-in
  framework.

These omissions keep the control focused, predictable, and inexpensive to
integrate.

## 3. Design decisions and rationale

1. **Specify observable behavior and ownership.** The contract defines what
   users and hosts observe, not a prescribed internal class structure.
2. **Use WinUI primitives where they fit.** Native collection controls, flyouts,
   theme resources, and UI Automation provide virtualization, input,
   appearance, and accessibility without a parallel control or visual framework.
3. **One owner per state.** The table owns transient view interaction; the host
   owns records, domain commands, saved settings, and domain mutations.
4. **Typed composition, not reflection.** Typed `DataTemplate`s, comparers,
   and callbacks keep row behavior explicit and compile-time discoverable.
5. **Direct row binding.** Each cell receives the row item directly, avoiding
   per-cell wrapper models and their update churn.
6. **No additional runtime dependency.** The control relies on WinUI 3 and
   does not require a data-grid, drag, or command-adapter package.
7. **Pay only for enabled behavior.** Marquee selection and row reordering do
   no work when disabled or idle. Fit measurement happens only for a fit
   command (section 10); the first fill measures nothing.
8. **One local view projection.** Header sort operates on one private view, so
   selection, layout, and visible order have a single authority.
9. **Be a Windows control, not a visual subsystem.** The control uses the
   application's normal WinUI control styles and platform theme resources; it
   introduces no TableView-specific palette, type scale, geometry, or animation
   vocabulary.
10. **Direct manipulation communicates intent; the host changes data.** The
    table supplies native-feeling drag feedback and an unambiguous insertion
    request. The host alone decides whether that intent is valid for its
    domain and publishes the resulting source order.

## 4. Mental model and ownership

### Terms

The [table glossary](../CONTEXT.md) defines the vocabulary used here.

### Data flow

```text
host source + columns
    -> TableView private view (stable header sort)
    -> virtualized rich rows
    -> gesture events and layout snapshot back to host
```

### `Table` owns

- the visible projection and stable sort of `ItemsSource`;
- realized row containers;
- selection mechanics, anchor, current item, logical row focus, and marquee
  gesture;
- reconciliation of its selected/current/anchor/focus items to a new source
  snapshot by stable key or reference identity;
- the effective column layout used by both header and rows;
- header sorting, resizing, reordering, and context-menu interaction;
- drag visuals, insertion feedback, and platform layout continuity for row
  reordering;
- generation and validation of a serializable layout snapshot;
- loading, empty, and no-results presentation selection.

### The host owns

- row objects, their lifetime, and domain-content reconciliation of external
  data updates;
- column definitions and cell templates;
- any optional observed selected-ID/current-ID projection of the table's
  selection;
- whether a row-reorder request is allowed and what it means;
- row and cell commands;
- row context-menu content;
- external, domain, and text filters, and the search-box/query state;
- storage and retrieval of the layout snapshot;
- batching view-affecting row changes and asking the table to refresh its
  private view;
- remote fetch timing, cancellation, version ordering, optimistic updates, and
  deferral of disruptive updates while a domain edit or animation is active;
- visual continuity outside table-owned selection/current/layout state,
  including template-local edit state and animations;
- all domain-specific policy.

The table emits requests and events. It MUST NOT execute domain actions or
mutate `ItemsSource` or the baseline column definitions. User layout changes
affect its private effective layout only.

## 5. Public control contract

The public surface is intentionally small. The following is the version-one
contract; an implementation may use equivalent language conventions without
changing these behaviors:

```csharp
public sealed class Table : Control
{
    public IEnumerable? ItemsSource { get; set; }
    public ObservableCollection<Column> Columns { get; }
    public Schema<TRow> Schema<TRow>() where TRow : class;
    public Strings Strings { get; set; }

    public ListViewSelectionMode SelectionMode { get; set; } // default Extended
    public Selection Selection { get; set; }
    public static void SetIsRowGestureEnabled(DependencyObject element, bool value); // default true
    public static bool GetIsRowGestureEnabled(DependencyObject element);

    public Placeholder Placeholder { get; set; } // Empty | Loading | NoResults
    public bool IsMarqueeEnabled { get; set; }   // default true
    public bool CanReorder { get; set; }         // default false
    public Thickness CellPadding { get; set; }   // default 12,6,12,6

    public object? LoadingContent { get; set; }
    public DataTemplate? LoadingContentTemplate { get; set; }
    public object? EmptyContent { get; set; }
    public DataTemplate? EmptyContentTemplate { get; set; }
    public object? NoResultsContent { get; set; }
    public DataTemplate? NoResultsContentTemplate { get; set; }

    public ColumnLayout Layout { get; set; }
    public Sort? Sort { get; set; }
    public TimeSpan SortInterval { get; set; }
    public bool ShowsHeaderButtons { get; set; }    // default false
    public void RefreshView();
    public void Release(IEnumerable<object> items);
    public void ScrollIntoView(object item);
    public double VerticalOffset { get; }
    public void ScrollTo(double verticalOffset);
    public void Fit(Column column);
    public void FitColumns();
    public void FillWidth();
    public void ResetLayout();

    public event EventHandler<Selection> SelectionChanged;
    public event EventHandler<ItemInvokedEventArgs> ItemInvoked;
    public event EventHandler<ItemContextRequestedEventArgs> ItemContextRequested;
    public event EventHandler<ReorderRequestedEventArgs> ReorderRequested;
    public event EventHandler<LayoutChange> LayoutChanged;
}

public sealed class Schema<TRow> where TRow : class
{
    public Schema<TRow> Key<TKey>(Func<TRow, TKey> key) where TKey : notnull;
    public Schema<TRow> CanInteract(Func<TRow, bool> predicate);
    public Schema<TRow> CanReorder(Func<TRow, bool> predicate);
    public Schema<TRow> SortKey<TKey>(Column column, Func<TRow, TKey> key,
        IComparer<TKey>? comparer = null);
}

public sealed class Strings
{
    public static Strings Load(string language);
}

public sealed class Selection
{
    public static readonly Selection Empty;
    public Selection(IEnumerable<object> items, object? current = null);
    public IReadOnlyList<object> Items { get; }
    public object? Current { get; }
}

public readonly record struct Sort(
    Column Column,
    SortDirection Direction = SortDirection.Ascending);
```

`ItemsSource`, `Strings`, `Placeholder`, `CellPadding`, and the runtime interaction flags
`IsMarqueeEnabled` and `CanReorder` are bindable
dependency properties. Marquee selection defaults to enabled and row
reordering to disabled, and the asymmetry is deliberate: a marquee is a
selection gesture and is meaningful in every table, while a reorder is a
domain request and is meaningful only where the host owns an order. A press in
the table body that nothing else competes for must do something, so the marquee
is on; most tables have no domain order, so the reorder is off.

`CanReorder` is the only owner of that capability. A gesture is
offered when the flag is true, the view shows the row order, and the row is
eligible for dragging; having a `ReorderRequested` handler is not part of the test,
because two owners of one capability eventually disagree. The view shows
the row order when it is unsorted, which is the source order, or sorted either
way by the column whose `DefinesRowOrder` is true (section 6); under any other
sort the table withholds the drag itself, and a drag from a row is section 14's
marquee. A host binds or sets the flag to false whenever its current external
filter/order, pending domain operation, or ordering model cannot map a visual
placement to a domain insertion.

`ItemsSource` may be any `IEnumerable`. It is the host's already filtered
projection. When that projection is empty, the host sets `Placeholder` as
section 17 defines.

`Columns`, `SelectionMode`, and everything `Schema<TRow>()` carries are
setup-only schema configuration. Schema has two halves: `Columns` is the
declarative half a host writes in XAML, and `Schema<TRow>()` is the typed half
that states the row type once and hands over the identity selector, the
interaction predicate, and every column's sort key with it. The control stays
non-generic because WinUI 3 XAML cannot instantiate an open generic; a
non-generic class can still have a generic method, and XAML never sees one.

Each table has one row type. Repeated `Schema<T>()` calls return the same setup;
a different row type is rejected. Retaining the schema does not bypass its setup
lifetime. Rows are reference types, so one row instance stays one object for
selection, focus and cell bindings.

The table captures the structural schema exactly once, at its first `Loaded` event. A host
may populate it in XAML or code before then; calling `Schema<TRow>()`
afterwards, changing a setup-only value, or structurally adding, removing, or
replacing a column afterwards, is a configuration error. This fixed schema
keeps cell templates, persisted layout, identity semantics, and selection rules
stable. Runtime changes belong in bindable state or the effective layout, not in
the schema. Localized presentation text, including `DisplayName`, remains live
under section 6.1; changing language does not change structural schema.

The structural values of `Columns` form the immutable baseline. The table keeps separate effective
order, visibility, width overrides, and sort state. A drag, resize, visibility
change, or an assignment to `Layout` MUST NOT mutate the definitions.
`ResetLayout()` restores the captured baseline. Because that baseline has
no sort criterion, reset also clears local sort and returns to natural order.

`Selection`, `Sort`, and `Layout` are settable properties rather than method
pairs. Each reads back what stands and each setter is an idempotent request:
assigning the state that is already in force changes nothing and raises no
event. A host can therefore project any of the three out to another surface and
push it back without a suppression flag or a second owner. `Selection` is
deliberately *not* a dependency property, so section 5.2's rule against a
two-way selected-items binding is structural rather than prose.

`ItemsSource`, `Selection`, `Sort` and `Layout` may be assigned before first
`Loaded`, in either source/schema order. Pending getters expose the requested
state. At first load the table resolves them together against the completed
schema and current source. A later `Layout` replaces the pending sort; a later
`Sort` overrides the layout's sort. Initial layout is silent; resolved nonempty
selection/current raises one `SelectionChanged`. After load, unavailable selection
items are discarded immediately rather than retained as future requests.

Assigning `Selection` atomically replaces the table-owned selection and current
item after resolving both to interactive instances in the current private view. An
omitted or `null` `Current` uses the first selected item in current visual
order, or `null` when nothing is selected. A supplied `Current` may be
unselected. An unavailable supplied `Current` is treated as `null` and uses that
same fallback.

`Sort` names a column and a direction as one value, so no state exists in which
the two disagree, and `null` is natural order. The column must be visible in the
effective layout, declared by this table, and carry a sort key from
`Schema<TRow>()`; a request that cannot be met is the host asking for something
impossible, so it throws. A saved sort arriving from storage is the other case
entirely and belongs in `Layout`, which recovers defensively (section 18).

`Schema<TRow>().CanReorder` defaults to every interactive row being draggable.
Its setup-only predicate restricts dragging without restricting selection,
invocation, or context commands. A selected packet containing a refused row
cannot be dragged; an unselected permitted row still moves alone. After a
predicate input changes, the host calls `RefreshView`, as for `CanInteract`.
This allows display rows outside a domain order to retain their ordinary actions.

`Schema<TRow>().CanInteract` defaults to every row being interactive. When the
predicate returns false, the item still renders but cannot be selected, invoked,
context-clicked, or included in a row drag packet. Keyboard navigation skips it.
Interactivity is evaluated on each view rebuild and immediately before an item
interaction.

`Schema<TRow>().Key` is optional. Without it, identity is object reference.
Configured keys use `EqualityComparer<TKey>.Default` (ordinal equality for
strings). Keys must be stable, non-null and unique; empty strings and zero are
valid keys. Every snapshot has unique effective identities, so repeated references
without a key are rejected too. Distinct value-equal references remain distinct.
Null rows, wrong row types and duplicate identities fail before replacing the
accepted view. The selector MUST be pure and inexpensive.

The API is intentionally non-generic at the XAML boundary, matching WinUI item
controls and keeping the control directly usable from XAML. Type erasure is
confined to the item boundary (`ItemsSource` and the event payloads); typed
`DataTemplate`s with `x:DataType` retain the host's row type for rendering, and
`Schema<TRow>()` retains it for identity, interactivity, and sorting. There is no
generic control hierarchy, reflection, property-path API, or untyped row
wrapper. The row-type cast happens once, inside the schema, rather than once per
selector and comparer at the host; a wrong row type is a configuration error and
throws rather than silently comparing equal.

### 5.1 Callback and event boundary

The control deliberately has two extension mechanisms, with no overlap:

| Surface | Kind | Used for | Must not do |
|---|---|---|---|
| `Schema<TRow>().Key` | synchronous policy callback | stable item identity | allocate, fetch, mutate, or depend on visual state |
| `Schema<TRow>().CanInteract` | synchronous policy callback | display-only versus interactive rows | execute a command or change selection |
| `Schema<TRow>().SortKey` | synchronous column callback | the value a column orders a row by | format UI, mutate items, or call RPC |
| `SelectionChanged` | host event | publish an optional external selection/current projection | continuously feed its own output back |
| `ItemInvoked` | host event | primary domain action | assume an action was completed |
| `ItemContextRequested` | host event | construct/show a domain menu | put domain menu logic in the table |
| `ReorderRequested` | host event | request a domain reorder | mutate `ItemsSource` through `Table`; the host may update its own source after the event |
| `LayoutChanged` | host event | debounce a persisted layout snapshot | write settings on every pointer movement |

Policy callbacks are called on the UI thread and MUST be pure, synchronous, and
cheap. The table never calls them per render frame. A sort key is read
`O(n log n)` times during an explicit sort and therefore must be especially
cheap. `Comparer<TKey>.Default` supplies normal .NET ordering, including enums
and nullable keys; default nulls come first ascending. An optional comparer
expresses domain ordering, such as unknown values last or ordinal text. Preserve
the host's intended ordering when removing sentinel values. Unsupported key types
fail when comparison is needed; comparisons finish before the new sort is committed.
Equal values retain source order, including descending sorts.

Events are the component's callback API for completed gestures or table-state
changes. They fire only after the table has completed its own mechanics.
`SelectionChanged` can also result from assigning `Selection` or from
source/interactivity reconciliation. Its immutable payload is the updated
`Selection`, including when only the current row changes.
Every selected-item packet below is in current visual row order. Event payloads
are immutable snapshots:

| Event | Event args contract |
|---|---|
| `SelectionChanged` | the `Selection` itself: current visual-order `Items` and `Current` |
| `ItemInvoked` | `Item`, ordered `SelectedItems` after normal input selection processing |
| `ItemContextRequested` | `Item`, ordered `SelectedItems`, realized row `FrameworkElement Target`, nullable `Point Position` relative to it (`null` for a keyboard invocation) |
| `ReorderRequested` | row-order `Items`, nullable `Before` item, never one of `Items` |
| `LayoutChanged` | one `LayoutChange`: `Sort`, `Move`, `Resize`, `Fit`, `Visibility`, or `Reset` |

`LayoutChanged` carries the change kind and nothing else. A host that wants the
snapshot reads `Layout`, which is the same independent snapshot the event used
to carry, so there is one way to obtain it rather than two.

Assigning `Layout` is silent. `Before` describes a position in the
row order *after* `Items` have been removed: insert the complete packet
immediately before that remaining item; `null` means append at the end. The
row order is the current visual sequence, read from the bottom up when the
`DefinesRowOrder` column is sorted descending, and `Items` is in that
order too, so the request names the placement the user pointed at whichever
way the view runs. The first remaining item therefore expresses the start of
the order, and `null` expresses its end without a synthetic target or a
two-direction ambiguity. A request is emitted only for a legal placement that
would change visual order. A drop outside a legal boundary, onto the dragged
packet, or back to the same resulting order is a no-op and does not raise a
reorder event. When the packet is the complete view, there is no distinct
remaining insertion position and no reorder request.

Events are raised synchronously on the UI thread. A host may start or forward
async work from an event handler, but the table does not await it and never
infers success from it. In particular, a reorder event is a request, not a
transaction.

Do not add parallel `ICommand` properties or an async completion protocol for
these events. They would duplicate delivery and make gesture ordering unclear.
A host may use ordinary XAML event handlers or adapt events to its own command
model outside the control.

### 5.2 MVVM consumption

`Table` follows normal WinUI control semantics: bind values into dependency
properties and receive interaction requests or state changes as events. A page
may forward an immutable event packet to its view-model command or application
service using the MVVM mechanism it already uses. That forwarding is
deliberately outside the control; it requires no behavior library, command
adapter, or framework-specific dependency.

The table is the single interactive selection/current owner. A page or shell
only needs an ID projection when another surface—such as a command bar or
detail pane—uses it. It observes `SelectionChanged` and assigns
`Selection` only when another surface changes that projection. There is
intentionally no two-way selected-items binding: that would create a competing
selection owner, which is why `Selection` is a plain property rather than a
dependency property. The idempotence rule above prevents a feedback loop. Cell commands belong in the typed cell template; a row-context
request necessarily remains at the view boundary because it carries placement
and pointer information.

### 5.3 Source, identity, and update contract

The control is UI-thread-affine. `ItemsSource` assignment, source enumeration,
`INotifyCollectionChanged` notifications, public method calls, and callbacks
MUST occur on its XAML `DispatcherQueue`. A host receiving daemon, RPC, or
background data marshals a completed update to that queue before changing its
source. The table does not dispatch, serialize, cancel, or order external
updates.

On `ItemsSource` assignment, and after every observed collection notification,
the table captures one ordered source snapshot. `Add`, `Remove`, `Move`,
`Replace`, and `Reset` are all visible as a new snapshot; `Reset` is equivalent
to replacement of the complete source input. The implementation may update a
private view incrementally or rebuild it, but the observable result MUST be the
same. The enumerable must be finite and stable for each enumeration. A one-shot
iterator is treated as a snapshot and the host supplies a new iterator for a
later source update.

The private view is handed to the native row surface in full. That surface
reads a row only when it realizes the row's position, and for every other
position it keeps nothing but the count. A source update or sort therefore
raises a collection notification for two things and nothing else: every
membership change, at its true position, and every position the surface
currently holds a container for, pinned and not-yet-recycled containers
included. Every other position takes its new row without a notification. The
observable result is the one this section already requires: the count is
always right, a row that leaves or arrives at a held position animates, and a
container never shows a row other than the view's row at its index. A
membership-only update, such as a host filter over an already-ordered source,
raises exactly the notifications it raised before this rule and no others.
What falsifies the rule is a container whose content differs from the view's
row at its index after a sort, a scroll, or `ScrollIntoView`; the reference
host's diagnostics section R checks exactly that.

A source that implements `INotifyCollectionChanged` is live for membership and
source-order changes. A plain `IEnumerable` is immutable from the table's
perspective after assignment: the host assigns it again after changing its
membership or order. Each accepted source update retains the current layout
and sort criterion, then applies stable header sort to the new base sequence.

A source update establishes the latest base sequence. In an unsorted view, the
visible natural order is that sequence. In a sorted view, the same latest
sequence breaks equal comparer values. Clearing sort always returns to this
latest natural order; it never restores an earlier visual order.

**Owner ruling: rows that leave are held while the pointer is over the rows.**
While the pointer is over the row surface, or a row's context menu is open, a
row that leaves the source stays where it is, dimmed and non-interactive, and
rows that arrive wait. When the pointer leaves the rows, or the menu closes,
the held changes apply at once. Without the hold, a filter change or a removal
moves every row below it up under the pointer and an arrival pushes rows down,
so the person acts on a row they did not aim at; people do not forgive things
moving while they work.

- The hold is a presentation of the source. The host's collection is never
  modified, and the latest snapshot is still the base sequence the next
  update and `RefreshView()` start from.
- A held row cannot be selected, invoked, context-clicked, or dragged, and
  keyboard navigation skips it, as for a non-interactive item. Selection
  pruning follows the rule below, so the selected packet never names a held
  row, and a reorder request never names one as `Before`.
- A change the person caused applies at once, even with the pointer over the
  rows, because the rule is that nothing moves unless the person caused it. The
  table cannot tell who caused an update, so the host names the rows whose
  presence the person's command changes with `Release(items)`, before the
  change reaches the source or while the table holds it. The update that
  carries the change applies whole, held rows and waiting rows included. Every
  other update stays held. A named change that never comes, such as a refused
  removal, leaves its rows named until their presence next changes.
- A held row that comes back into the source stops being held.
- A table that shows no rows takes its arrivals at once: nothing on screen can
  move.
- A sort change moves every row anyway, so it takes the source whole.
- A hierarchical table holds nothing. Expansion and collapse are the person's
  own commands and arrive through the same refresh as any other change.

`INotifyPropertyChanged` on a row redraws ordinary bound cell content only.
It does not automatically re-sort or re-evaluate interactivity.
After a batch changes any value used by the active sort or the schema's
interaction predicate, the host calls `RefreshView()` once.
`RefreshView()` re-evaluates the current source snapshot, applies the current
sort, and does not re-enumerate or fetch a non-notifying source. When rows are
then allowed to trade places is section 9's settling question, not this one.
Display-only updates need no call.

`ScrollIntoView(item)` reveals an item without changing selection or keyboard
focus. If the source has the item but the pointer hold delays its arrival, the
held update applies before scrolling: explicit navigation must show its target.
Hosts use it after a confirmed operation whose result should be visible, before
selecting a waiting arrival and without accessing the control's template parts.

`VerticalOffset` reports how far the rows are scrolled, in DIPs. `ScrollTo`
moves the rows, clamped to the scrollable range, without changing selection or
keyboard focus; a non-finite offset makes the call do nothing. It lays out the
current rows first, so a host can restore a saved position immediately after
supplying the rows. Called before `Loaded`, the offset is held and applied when
the table loads. A host restoring a saved position calls `ScrollTo` after it
restores the source, because the row count decides the scrollable range.

When the schema supplies a key selector, every new source snapshot—including
assignment, `Add`, `Remove`, `Move`, `Replace`, and `Reset`—reconciles selected
items, current item, selection anchor, and focus to the current row instances
by key. `Selection.Items` then exposes those new instances.
`SelectionChanged` reports a changed exposed packet: identities, item order,
current item, or replacement row instances. Ordinary property changes on the same reference do not
raise this event. An
anchor or focus item that no longer survives clears. Without a selector, object
reference is identity, so a replacement object is a removal and an addition.
Null configured keys or duplicate effective identities fail fast.

If an update removes an item or makes it non-interactive, the table prunes the
effective selection/current item atomically. If that removes the current item
while selected items remain, the first retained selected item in current visual
order becomes current; otherwise current becomes `null`. The table raises at
most one `SelectionChanged` event. Source reordering and header sorting do
not raise that event when the exposed selected/current packet is unchanged.

A source update, `RefreshView()`, or sort change during a
row drag cancels the drag without raising
`ReorderRequested`. A marquee gesture survives all three: the rectangle
stays where it is on screen, the rows move through it, and its coverage is
re-asked of the new view (section 14).
Header resize and
column-drag gestures are data-independent and remain active.

An explicit `Selection` assignment, or turning off
`IsMarqueeEnabled`/`CanReorder` during its corresponding
gesture, cancels that gesture before applying the new state. It never produces a
reorder request.

Policy callbacks MUST NOT mutate the source or call back into the table. Events
are post-mechanics: a handler may publish an optimistic source update or start
async domain work. Runtime setters commit synchronously, including inside an
event handler. Event payloads describe that event; getters expose current state,
which an earlier subscriber may already have changed. No hidden deferred setter
queue exists. A compound gesture captures its intended packet, then rechecks
state and target after selection handlers return. An invalidated context request
is cancelled; keyboard navigation does not resume focus work for an obsolete row.

Unloading suspends source subscriptions and timers. Explicit setters still
reconcile logical state while detached. Reload recaptures a notifying source;
a non-notifying one-shot source retains its captured snapshot.

The table guarantees continuity only for its own key-addressable state. It does
not merge domain snapshots, retain row view models, preserve arbitrary cell
animation/edit state, or promise scroll anchoring across disruptive replacement.
A host that needs that continuity keeps stable row objects or defers/reconciles
its external updates. Remote fetch coalescing, cancellation, version checks,
optimistic rollback, and update deferral remain host policy.

### 5.4 Invalid conditions and failure boundaries

The control distinguishes four cases so that a host does not have to infer
meaning from a missing update:

- **Configuration error.** A malformed captured schema or policy contract—for
  example duplicate column IDs, an invalid width range, duplicate item keys, a
  setup-only mutation after `Loaded`, or a `Sort` request naming a column this
  table did not declare or did not give a sort key—is a developer error. The table does not
  construct a partly valid interactive schema or substitute a guessed meaning.
- **Ineligible runtime interaction.** A well-formed interaction can be
  unavailable because the host disables reordering, an item is non-interactive,
  a resize target is non-resizable, or a drop has no legal insertion boundary.
  The table cancels or ignores that interaction, changes no
  layout/source/selection state, and emits no domain request.
- **Host callback or handler failure.** A policy callback that throws or
  returns a contract-invalid value is a host defect; the originating view
  or interaction does not apply a partial table state. Event delivery is
  post-mechanics: the table neither interprets a handler's outcome nor rolls
  back host work. In particular, a reorder request is never accepted merely
  because the event was raised.
- **Obsolete persisted state.** Unknown or stale layout data is ordinary
  compatibility input and is recovered defensively as defined in section 18;
  it is not treated as a configuration error.

These are observable categories, not requirements for a particular exception,
assertion, logging, or recovery mechanism.

## 6. Column contract

```csharp
public sealed class Column : DependencyObject
{
    public string? Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;

    public object? Header { get; set; }
    public DataTemplate? HeaderTemplate { get; set; }
    public DataTemplate? CellTemplate { get; set; }

    public double Width { get; set; } = 150; // DIPs, the baseline
    public double MinWidth { get; set; } = 48;

    public bool IsVisible { get; set; } = true;
    public bool CanHide { get; set; } = true;
    public bool CanResize { get; set; } = true;
    public bool DefinesRowOrder { get; set; }

    public HorizontalAlignment CellAlignment { get; set; } =
        HorizontalAlignment.Left;
}
```

The type carries the context, so the names do not repeat it: inside a
`Column`, `Width` and `IsVisible` are the column's baseline width and
baseline visibility, and `CellAlignment` is how its cells align.

Sortability is not on this type at all. A column sorts because
`Schema<TRow>().SortKey(column, row => key)` gave it a key, and does not otherwise;
the flag and the comparer that previously had to agree are one call, so the
disagreement is no longer representable.

`HeaderTemplate` is the composition point for an icon, visual label, tooltip,
or embedded header control. `Table` deliberately has no separate header
icon, description, or renderer-metadata API; those are ordinary host content.
Sortability and structural column definitions are setup-only. `DisplayName` is
live presentation state: its update must reach generated headers, menus, and
automation names in place. A one-time `{StaticResource}` lookup is insufficient
for that contract. Use ordinary observable presentation/binding behavior for
this value; it does not make the structural schema mutable.

### 6.1 Column invariants

Required invariants:

- `Id` is the persistence key. It is optional: a table whose layout is never
  saved has no keys to invent, and a column without one appears in no layout
  snapshot and is left in declared order after every column a restore did name.
  When supplied it is stable, unique, and non-empty.
- `DisplayName` is a non-empty localized plain-text name used by generated menus
  and UI Automation; it need not match the visual header exactly. It may change
  after `Loaded` when presentation language changes.
- `CellTemplate` receives the row item as its `DataContext`/content.
- `Width`, `MinWidth`, and persisted widths are finite
  device-independent pixels (DIPs). `Width` is greater than zero;
  `MinWidth` is non-negative;
- the effective width is never below `MinWidth`. There is no maximum, because
  how wide a person makes a column is their choice;
- at least one column remains visible, which a table declaring no columns at
  all does not satisfy;
- `CanHide == false` prevents hiding that column;
- a column is sortable exactly when the schema gave it a sort key, and that key
  is pure and defines a consistent total ordering for the host's rows;
- at most one column has `DefinesRowOrder`; its ascending values are the host's
  row order, the order the unsorted view shows and a row drag changes (section
  16);
- hidden columns retain their effective position and most recent width;
- the declaration order, `Width`, `IsVisible`, and the control's documented
  width defaults form the reset baseline; runtime layout lives only in
  `ColumnLayout`.

The table validates every column definition when it captures the schema at
`Loaded`. Missing or duplicate values are configuration errors rather than an
unusable header later. Changing a captured structural column definition or sort
key after that point is unsupported and is a configuration error. Values bound
inside a cell or header template remain live, as does localized presentation
text. Column identity, templates, comparers, and layout defaults remain fixed.

`Column.DisplayName` and `Column.Header` are live dependency properties; templates
and structural column settings remain setup-only. Bind changing presentation with
`Mode=OneWay`. Updating text preserves existing header elements, including custom
content whose instance has not changed.

Prepare control text with `Strings.Load(language)`, then
publish `Table.Strings` on the UI thread with the host's prepared text, language
and flow direction. The immutable value exposes no catalogue dictionary. The
loader reads this library's embedded catalogues on the calling thread, so the
host chooses that thread, and falls back through
parent languages to English. An invalid catalogue throws and leaves the current
value intact. English is the standalone default; no host setup is required.

A language change updates generated headers, open menu labels, placeholders,
sort status and drag status without recreating the table or changing selection,
layout or source. New views bind to the host's current prepared `Strings` value.
Prepare that initial value before creating the first localised bound view.
Regional formatting remains separate from UI language. The desktop
[localisation contract](../../../docs/localisation.md) owns catalogue policy and
composition protection; the library does not persist a language preference.

The declared defaults are: visible, hideable, resizable, non-sortable,
left-aligned, a `Width` of 150 DIPs, and a `MinWidth` of 48 DIPs. A host SHOULD explicitly declare width and minimum policy
for rich cells whose footprint is meaningful—such as progress, button clusters,
sparklines, or status pills—rather than treating the generic default as domain
policy.

All visible columns are reorderable in version one. Version 1 does not include
per-column subclasses, render delegates, property paths, table-owned value
converters, or a separate column registry.

## 7. Rich cells

A cell template can contain any normal WinUI content, including:

- formatted text, icons, badges, and multiple aligned values;
- `ProgressBar` with labels;
- `Button`, `ToggleButton`, or command surfaces;
- `CheckBox`, `ToggleSwitch`, `ComboBox`, or `TextBox`;
- a host-defined `UserControl`;
- tooltips and accessibility descriptions.

Example:

```xml
<DataTemplate x:Key="ProgressCellTemplate"
              x:DataType="viewModels:ProgressRowViewModel">
    <Grid ColumnDefinitions="*,Auto">
        <ProgressBar Value="{x:Bind Progress, Mode=OneWay}" />
        <TextBlock Grid.Column="1"
                   Text="{x:Bind ProgressText, Mode=OneWay}" />
    </Grid>
</DataTemplate>
```

The example deliberately does not prescribe a table-local spacing, font, or
color treatment. A host uses the application's existing layout resources
and standard-control styling for its own cell composition.

The table MUST NOT convert rich templates into text values or take over their
domain visual styling. It owns the outer row container and its selected,
current, hover, focus, unavailable, and drag states; a cell template supplies
content inside that state rather than duplicating or masking it. This keeps the
table's native interaction feedback coherent while letting a host use
normal app controls and templates in a cell.

Input rules:

- interactive descendants receive pointer, keyboard, and focus input normally;
- clicking a `Button`, editor, selector, or toggle MUST NOT also invoke the row;
- an interactive descendant's own `ContextFlyout`, manipulation, text editing,
  and automation behavior take precedence over table gestures;
- table keyboard shortcuts apply only from the passive row/header surface and
  MUST ignore text-editing controls;
- clicking passive cell content follows normal row-selection behavior;
- a host can update cell values through normal binding and
  `INotifyPropertyChanged`.

For a custom interactive control the table cannot recognize automatically, set
`Table.IsRowGestureEnabled="False"` on its root or an ancestor. It prevents
row selection, invocation, row-context requests, marquee initiation, and row
dragging from that subtree without adding another policy callback. It does not
suppress the descendant's own normal focus, keyboard, context-menu, or
automation behavior.

## 8. Rendering, layout, and visual language

The table MUST preserve vertical virtualization and one effective column layout
shared by headers and rows:

- use one vertical scrolling owner rather than nesting vertical scroll surfaces;
- render each realized row from its row item and the visible column templates;
- derive header and row widths from the same effective column values;
- keep the header visible during vertical scrolling;
- retain columns at narrow widths rather than silently hiding or reflowing
  data.

**Owner ruling: the table never scrolls sideways.** Columns that do not fit run
past the right edge and are cut off. The person decides what the table shows:
to see a cut-off column, they hide or narrow other columns.

The table is one dense data surface, not a stack of cards or a second command
bar. Its header provides column actions; its body provides rows. It has no
window-width breakpoint that silently hides columns or table mechanics. The
host chooses the supported allocation and any page-level minimum size.

Text overflow is intentional. A generated textual header keeps a stable
one-line header treatment, trims its visible label when necessary, and exposes
the full localized DisplayName through its native accessible name and tooltip.
A host header or cell template deliberately chooses wrapping, trimming, or
clipping for its content and declares a sufficient MinWidth when it contains a
control or essential value that must remain directly usable. The table does not
make a rich cell reachable merely by shrinking it below its usable width.

The visual baseline is WinUI. This section owns TableView's appearance across
hosts; the host owns page composition and its cell content. This applies
Fluent's platform-native, focused, and inclusive principles by giving data and
interaction states priority over decorative table chrome:

- use built-in WinUI collection, flyout, text, icon, focus, and control states
  before adding any custom appearance;
- use platform `ThemeResource`s and normal application resources when an
  application already defines them for those standard controls;
- use the Windows type ramp and default system font behavior for ordinary
  labels and values; distinguish headers through the normal control/type
  hierarchy, not a table-specific font recipe;
- retain platform control geometry and flyout geometry instead of defining
  TableView corner-radius, border, elevation, color, or spacing tokens;
- change a standard control template only when shared-column geometry requires
  it, and preserve the control's native rest, pointer-over, pressed, focused,
  selected, checked, disabled, input, and UI Automation behavior;
- let the native row container express hover, selection, current, and focus.
  Cell templates must not replace these with a competing row backdrop;
- adapt when Light, Dark, or High Contrast changes at runtime. A custom
  host cell remains responsible for using equivalent accessible resources.

The column header uses a `ControlAltFillColorTertiaryBrush` band, the platform
fill that differs from both the window and a card by a similar visible step in
Light and Dark. A table hosted inside another surface retains this header,
so its column headings have the same appearance wherever the table is used.

The retained row appearance has specific exceptions awaiting the
[hands-on Fluent review](../../../docs/architecture.md#decisions-still-open): no hover
fill and square corners for edge-to-edge rows. Native row focus is enabled after
the first product journey verified visible keyboard location, selection,
Pause/Resume and focus recovery during refresh.
The [integration record](tableview-implementation.md#keyboard-and-automation-integration)
keeps broader automation and RTL verification separate from that narrow path.

`Table` exposes no palette, visual-style object, font setting, token map,
or menu-style API. It must not create component-specific colors, bespoke
flyout surfaces, or visual recipes in XAML.

## 9. Sorting

Header passive-surface primary activation behavior:

1. an unsorted sortable column becomes ascending;
2. ascending becomes descending;
3. descending returns to natural/source order.

Mouse/pen click or touch tap on a passive header that does not become another
gesture uses this cycle. A non-sortable header has no sort action.

Only one column is sorted at a time. The active header shows direction using a
native, theme-aware glyph and exposes the state through UI Automation. Each
passive header participates in the header strip's composite focus model;
primary keyboard activation (Enter or Space) sorts only when that column is
sortable.

Sorting requirements:

- sorting creates a private view; it does not reorder `ItemsSource`;
- sorting is stable;
- equal values retain the exact current base-sequence order;
- null placement is defined by the host comparer;
- natural order means the current base-sequence order;
- identity is the string the schema's key selector returns when one is
  supplied, otherwise object reference; invalid keys are source-contract errors and unavailable
  items are pruned from selection;
- source updates and rehydration follow section 5.3;
- hiding the sorted column clears the sort and returns the view to natural
  order, reported in the same `LayoutChanged` as the visibility change. The
  header is the only place the sort shows and the only place it is changed, so
  a sort by a hidden column would be one the user can neither see nor undo;
- normal property notification redraws cells but does not continuously resort;
- the host calls `RefreshView()` after a batch changes any active
  view-affecting value;
- while a sort is applied, the view converges on the sorted order within a
  bounded interval. Membership is deferred only by section 5.3's hold while
  the rows are pointed at; otherwise a row that arrives appears at once and a
  row that leaves goes at once, while existing rows trade places on the
  settling interval. `SortInterval` sets that interval, and `TimeSpan.Zero`
  restores immediate re-sorting. A sort by the column that `DefinesRowOrder`
  never settles: that order changes only when the host reorders, usually on
  the person's own command, so holding it would delay that command.

**Owner ruling: a live sort freezes only the row under the pointer.** Whenever
the rows take their sorted order, the row under the pointer keeps its
position and the other rows take their sorted order around it. The same holds
for the row whose context menu is open, and during a pointer gesture over the
rows the frozen row stays the one the gesture began on. When the pointer moves
to another row, that row is
the frozen one from then on; nothing re-sorts merely because the pointer moved.
Rows section 5.3 holds keep their positions the same way. The owner chose this
over freezing the whole table, so the table stays useful while the pointed
target stays still. It applies under a sort that reorders: natural order and
the column that `DefinesRowOrder` show the host's own order, and a hierarchical
table freezes nothing, because a frozen row could be separated from its parent.

Avoiding automatic re-sorts on every property notification is important for
rapidly changing data such as speed and progress.

The settling interval exists because a host has only one lever and the table has
two. Publishing a snapshot carries membership and position together, so a host
that throttles its updates to stop a reshuffle also delays the completion that
made the update necessary. The table holds both the previous order and the new
one, so it is the only layer that can take the membership immediately and let
the position wait. It does so by never merging the two. A snapshot that adds or
removes a row is not a settling case at all: the table takes the sorted order
for it whole, which is also where an arriving row belongs, and only a snapshot
of exactly the same rows can hold its position. The rule is therefore structural
rather than a promise — the settling path cannot defer a membership change,
rather than choosing not to. Only section 5.3's hold defers one, for its own
reason, and while it does the rows are the same rows, so settling sees no
membership change either.

Measured on the original host's 2,002-row list, per host publish, because with settling off
every publish reorders and the two units are then the same one: sorted by
download speed, every publish reordered the whole view. What the host had
published was a single row finishing. The reshuffle is the reason the
interval exists, and the reason it is a table concern rather than a host one is
that no host can separate the two halves of its own snapshot.

Two notification counts stood in this section and have been removed: an average
of 2,158 per publish, and 2,660 for a settled reorder against that 2,158. They
were not counts of what this table raises. They were taken against a reconcile
that announced every row it moved, and that implementation is gone, so neither
can be re-measured. Read as this table's own output they also contradicted
section 20, which requires a reorder's notifications to be bounded by realized
containers and therefore never to exceed the row count — which 2,158 did, on a
list of 2,002 rows. The claims they were quoted for are stated here without
them.

Count per publish for the defect and per reorder for the benefit; per second for
neither. A count over a window measures how often the host happened to publish in
it. Per publish then measures the defect honestly, for the reason just given, and
measures the benefit dishonestly: settling works by making some publishes free,
so averaging those back in divides out the effect. Measured that way one pair of
runs put the saving at a half and the next at nothing, both arithmetically
correct and neither measuring anything.

What the interval buys, over fifteen seconds. Sorted by speed with a
three-second settle, eleven publishes produced five reorders — exactly one per
interval, so the cap holds at the value it claims. The same sort with settling
off reordered on nine publishes out of nine. Sorted by a stable key, sixteen real
publishes produced no reorder at all. That is the claim, and it holds whatever
the host's publish rate: one reorder per interval, against one per publish.
These three counts came from the original host's own publish stream, and that
harness has been deleted, so the eleven, the nine, and the sixteen cannot be
reproduced as written. What survives them is the claim itself, which follows
from how the interval is defined and not from any particular publish rate.

Note what it is not. Each settled reorder is a bigger one, because a longer
interval lets more drift accumulate before the rows are allowed to move. Cutting
the number of reorders therefore does not cut the work by the same factor, and a
host raising the interval to thirty seconds should expect a calmer table rather
than a proportionately cheaper one. That is the shape of it; the two counts that
used to give it a size are the removed ones above. The usability gain is the
real one — at most one reshuffle per interval instead of one per publish — and no
notification count is needed to say so.

There is no `SortRequested` callback or remote-sort mode in version one.
Sorting is a local table projection; add an explicit external-sort mode only if
a real host requires it.

## 10. Column widths, resizing, and fit commands

`Table` uses fixed device-independent-pixel (DIP) column widths. Version 1
has no star, fill, percentage, or viewport-responsive width mode. Extra space at
the right remains table surface, and columns that do not fit are cut off
(section 8). Resizing the host window never redistributes or re-measures column
widths. There are two exceptions, each applied once and leaving fixed widths
behind: the first fill below, and the explicit command `FillWidth()`, which
fits the columns to the table's width when the person asks.

Every column has a deterministic baseline width. A declared `Width` is
used exactly after it is raised to its `MinWidth`. When a host
does not declare one, the control's 150-DIP default is used. The baseline is
the width a column has before the first fill.

**Owner ruling: a table fills its width once, when it first has one.** When
the table first has a usable width and no column has a width override, it
scales the visible resizable columns from their baseline widths by the scaling
step of `FillWidth()`, so they end where the header buttons begin, or at the
table's right edge. A width override is a width the person chose: restored
from a saved layout, set by a resize, or set by a fit. The person's choice
wins, so a table restored with saved widths keeps them. The declared widths
alone would cut off or waste space on first use. Filling depends only on the
width, so it runs before or regardless of rows, and data that arrives later
never resizes a column; a fit to the first rows resized the columns in front
of the person whenever data arrived after the table was on screen. The first
fill does not run `FillWidth()`'s fit, because that fit would measure whichever
rows happen to be realized at that moment and replace the host's declared
proportions with them. It is part of the table's initial layout, so like the
rest of that layout it raises no `LayoutChanged`; a host that saves the layout
on `LayoutChanged` saves the filled widths with the person's first change.
After that one fill, later data, property updates, sorting, filtering,
scrolling, window resizing, and visibility changes MUST NOT silently widen or
narrow a column.

An applied valid `ColumnLayout.WidthOverrides` entry, a completed direct
resize, or an explicit fit creates a width override.
It wins over the baseline until another override or `ResetLayout()`
replaces it. A user may resize down to the declared `MinWidth` even when cell
content clips or truncates; observed content never becomes a new hard minimum.
Cell templates own their overflow policy.

Every resizable visible column has a mouse/pen resize separator. Touch and
keyboard use the generated header-menu fit commands, so version one does not add
a competing direct-touch resize recognizer to a dense header.

- mouse/pen dragging captures the pointer and updates the shared effective width;
- the width stops only at the column's `MinWidth`;
- Escape cancels the active drag and restores the starting width;
- double-clicking the mouse/pen separator fits that column;
- the header menu includes **Fit this column**, **Fit columns**, and
  **Fill width** when applicable, and offers no
  per-step width command;
- `ShowsHeaderButtons` offers the same two visible-column commands as buttons in
  the header's trailing space. It is off by default, because a table must not
  add a visible control to a host's header uninvited and a host with its own
  buttons for these commands would then show two of each; turning it on is not a guarantee that
  they appear, since the strip withholds both whenever the columns reach far
  enough right to want that space. The person can hide either button from the
  header menu; that choice persists in `Layout`, and `ResetLayout()` shows both
  again;
- the table raises one coalesced `LayoutChanged` notification when a gesture,
  fit, or menu width command changes the effective layout, not one persistence
  write per pointer movement.

An earlier version of this clause required **Narrow this column** and **Widen
this column**, each changing the width by 8 DIPs. They were removed, and the
reason is recorded because the requirement looked like accessibility and was
not. A menu flyout closes on every invocation and WinUI offers no way to keep
one open for a command, so each 8 DIP step cost a full reopen: widening the
original host's 150 DIP name column to something readable was thirteen
right-clicks and thirteen clicks. That count came from the original host's own
fitted width, and the harness that produced it has been deleted, so the thirteen
cannot be reproduced as written. What survives it is the arithmetic: a column at
the 150 DIP default needs one invocation per 8 DIP, however far it has to go.
Nobody walks that path twice, so it was the
appearance of a keyboard route to resizing rather than one. **Fit this column**
already gives the keyboard the outcome the user is actually after, in a single
invocation, and it is two lines above in the same menu. Should continuous
keyboard resizing be wanted, it belongs on the focused header as a held key,
where auto-repeat does the work, and not as a menu item invoked once per step.

`Fit(Column)` and `FitColumns()` are explicit fit commands, not an
automatic sizing mode. A fit considers only the header and cells available to
the current normal visual layout, includes normal padding and the sort glyph,
and raises the result to that column's `MinWidth`. It MUST NOT enumerate source data
solely to size columns, instantiate off-screen row templates, or maintain a
hidden measurement table. A per-column fit changes only that column; it MUST
NOT fall back to a fit of other columns when its result is unchanged.

`FillWidth()` runs the same fit as `FitColumns()` and then scales the
visible resizable columns by one factor so the visible columns end where the
header buttons the table offers begin, or at the table's right edge when it
offers none: wider when space is left, narrower when they run past it. The
buttons therefore keep their place, and the Fill button a person just clicked
stays under the pointer instead of disappearing. One factor keeps the fitted
proportions, so the widest fitted columns, which a fit that sees only realized
rows marks as the most likely to hold longer values, keep the most room. A column the factor would take below its `MinWidth` stays
at `MinWidth` and the others share what is left; when even the minimums do not
fit, the columns run past the edge as section 8 describes. It produces at most
one `LayoutChanged` event, of kind `Fit`, and like any fit creates width
overrides.

`FitColumns` fits each currently visible, resizable column independently
and produces at most one `LayoutChanged` event. Hidden columns retain their
effective width and are not fitted; a direct fit request for a hidden or
non-resizable column is a no-op, while a column this table does not hold, or
any column before the first `Loaded` captures the schema, is an argument error. A value that has not entered the current visual layout may require a
later fit after scrolling. This is an intentional limitation of a virtualized
convenience action, not a promise to discover the widest value in the source.

Hiding or showing a column does not discard, recompute, or fit its width.
`ResetLayout()` discards width overrides and restores the captured
baseline widths; it neither fits the current data nor fills the width again.
The first fill creates width overrides as a fill does, so a reset discards
them too.

## 11. Column drag reordering

Mouse/pen dragging of a header reorders visible columns.

- movement begins only after the normal drag threshold;
- the dragged header remains identifiable;
- a theme-aware insertion marker shows each legal destination, including before
  the first and after the last visible column;
- dropping at a different legal destination updates the effective layout order;
- Escape cancels without changing the order;
- a click that never crosses the drag threshold still sorts;
- a visible move removes the dragged ID and reinserts it at the chosen visible
  boundary in the full logical order. All non-dragged IDs, including hidden
  IDs, retain their relative order;
- **Move left** and **Move right** use the same neighboring-visible-column
  semantics as drag;
- no-op and invalid placements leave the layout unchanged and raise no
  `LayoutChanged` event;
- selection and row scroll position do not change, and focus remains on the
  moved header after a keyboard action or returns to it after mouse/pen drag.

Only a passive header surface starts sorting or column drag. Embedded header
controls retain their normal input behavior.

Touch does not start a direct header-drag gesture in version one. Standard
press-and-hold opens the same header menu described in section 12, which gives
touch and keyboard users equivalent move commands without competing with native
touch scrolling or control input.

Column drag remains local to the control and requires no additional runtime
drag-and-drop dependency. During a valid drag it uses platform drag feedback;
on completion, cancellation, or an applied layout, the header and realized
cells use normal WinUI reposition/layout continuity as defined in section 19.

## 12. Header context menu

Right-clicking or standard touch press-and-hold on a header opens a native
`MenuFlyout`. Right-clicking unused header space opens the same menu without an
active-column action. The Menu key or Shift+F10 on a focused header opens the
same menu and leaves row selection unchanged.

The generated menu is deliberately limited to table mechanics. It provides both
pointer actions and keyboard-accessible alternatives:

- **Hide column “Name”** for the column the menu was opened on, while it is
  hideable and another column can remain, which becomes **Show column “Name”**
  once that column is hidden;
- **Fit column “Name”** for a resizable active column, and **Fit columns**
  and **Fill width** when at least one
  visible column is resizable;
- **Move left** and **Move right** for the active column;
- one item per declared column, hidden ones included, carrying a check when the
  column is visible;
- when `ShowsHeaderButtons` is on, a separator and then one item per header
  button, **Fit button** and **Fill button**, carrying a check when that button
  is not hidden. They are shown and hidden the way the columns above them are,
  and the separator keeps them from reading as columns.

Labels stay short because the longest one sets the width of the whole menu.

The three commands that act on a single column name it, in typographic quotation
marks: the label is a sentence a translation owns, and a test asserts the
literal, so a straight quote written here would fail it on a character nobody can
see in a diff. "This column" was
unambiguous only while the column list lived behind a submenu: with the list in
the same menu, "this" meant the column the menu was opened on while the names
directly below it meant themselves, and nothing on screen said which was which.

The column list is in this menu and not a submenu of it. A submenu is a second
popup with a dismissal of its own that no API can refuse, so the root menu could
be held open across a change while the list collapsed underneath it, and turning
three columns on cost three trips back through the submenu. It is also what the
reference does.

No item closes the menu. Every one of them is repeated by nature — showing and
hiding columns, nudging a column left until it sits where it belongs, fitting one
and then another — or is immediately worth undoing, which amounts to the same
thing. Because the menu stays open, every item MUST re-ask its own label, icon
and enabled state after each invocation rather than merely be correct when the
menu was built: hiding a column can leave another as the last visible one and
disable its entry, moving a column to an edge disables the command that moved it
there, and hiding the column the menu was opened on turns that item into the one
that shows it back. No item may be left saying something that has stopped being
true.

A column entry carries its state as an icon rather than as a toggle item's own
check. A checkable menu item keeps its check in a column of its own that holds
its width even while the check is invisible, so one standing beside items that
carry icons gives the menu two glyph columns and indents every label past both.
The cost is that the state is no longer reported through the toggle pattern, so
it MUST be published another way; `AutomationProperties.ItemStatus` carries it.

The fit commands differ by glyph and MUST NOT differ by colour alone. A menu
icon inherits the text foreground and is monochrome by design, so a coloured one
reads as status rather than as category; High Contrast overrides icon colour
outright, which would take the distinction from the readers who most need it;
and section 19 does not allow colour to be the only carrier. They differ in
scope rather than in instrument — a measurement of one width against arrows
spreading outward — because they do the same thing to a different number of
columns.

Double-clicking a mouse/pen resizer fits one column. The fit commands provide the
keyboard and touch path for column sizing; move commands provide the equivalent
path for column order. Sort is available from the active passive header: Enter or
Space follows section 9's sort cycle. The column entries use each column's
localized `DisplayName`, and the commands that name a column place that name into
a sentence their translation owns; every other label is localized by
`Table`'s own `en.json`. They are not host-overridable configuration. Catalogue
authoring follows [localisation](../../../docs/localisation.md#tableview).
The control owns these generic messages without depending on product settings. This
keeps generated UI self-contained while the host owns its column names and
header content. Focus returns to the invoking header when the menu closes, in
the state the request arrived in: a flyout restores focus itself but not with
that state, so a menu opened by pointer and dismissed by invoking a command left
a keyboard focus ring on the header.

The menu MUST prevent a state with zero visible columns, respect `CanHide`, and
enable only commands that can currently change layout. It has no extension or
domain-command injection surface. A host that needs a domain command puts
it in normal page UI or an interactive control supplied by its header template;
that control owns its own menu.

## 13. Selection and keyboard behavior

Default selection mode is `Extended`. Its default pointer behavior is the
native extended-list behavior:

Pointer selection:

- plain click selects one row and sets the anchor;
- Ctrl-click toggles one row;
- Shift-click selects the inclusive range from the anchor;
- Ctrl+Shift-click adds the inclusive range;
- clicking empty space clears selection unless a marquee gesture begins;
- selection is based on row identity, not visual index.

Other `ListViewSelectionMode` values retain their normal WinUI semantics rather
than receiving a second TableView-specific interpretation. Non-interactive
display rows are skipped by pointer and keyboard selection.

`None` permits no selected items, `Single` permits at most one, and `Multiple`
and `Extended` permit many. Assigning `Selection` applies those limits after
resolving interactive current-view items: it clears selection in `None`, retains
the first resolved item in current visual order in `Single`, and retains all
resolved items in `Multiple` and `Extended`. In `None`, `Selection.Items`
remains empty, but a passive row may still become current for invocation or
context requests.

`Selection.Current` is the table's logical current row. It may be selected or
unselected, and is not synonymous with physical keyboard focus. A passive row
selection/navigation action makes its row current. When focus enters a rich
interactive descendant, that descendant owns its normal Tab, keyboard,
text-editing, menu, and automation behavior without clearing the current row or
redirecting keys back to the table.

The generated passive header strip is one composite control region in page tab
order. Tab enters or leaves the strip instead of visiting every passive header.
Left/Right moves its active visible header, Home/End moves to the first/last,
and Enter, Space, Menu, and Shift+F10 use the active header's defined
sort/menu behavior. The row surface is the following normal reachable
region; within it, the platform's arrow-key navigation is retained. Decorative
elements and the marquee overlay are not tab stops. Interactive header
descendants and interactive cell controls retain their own normal tab, focus,
and input behavior rather than joining the passive-header composite.

`Table` is one content region. It does not reserve a page-level F6
shortcut; a shell that offers F6/Shift+F6 navigation between prominent regions
owns that policy and gives this table region a localized accessible name.

Keyboard selection:

- Up/Down moves the current item;
- Shift+Up/Down extends from the anchor;
- Home/End moves to the first/last row;
- Shift+Home/End extends to the first/last row;
- Page Up/Page Down retain normal list-page navigation;
- Ctrl navigation moves current/focus without replacing selection or its anchor;
- in `Multiple`, unmodified navigation likewise preserves selection;
- Ctrl+A selects all rows when multiple selection is enabled;
- Enter invokes the current row;
- Space selects the focused passive row using the mode's normal selection
  semantics and Ctrl/Shift modifiers; interactive cell controls retain Space.

Touch selection occurs on a recognized passive-row tap through the same selection
model as click and Space. A pan or press-and-hold does not begin that selection
path; scrolling and context recognition remain native. Tapping empty row surface
clears selection, and interactive descendants keep their own input.

The control MUST scroll the current item into view when keyboard navigation
moves beyond the viewport.

Selection MUST survive sorting, column changes, row recycling, and same-key
source rehydration. Removed or unavailable items are pruned as defined in
section 5.3.

Assigning `Selection` resolves supplied identities by key when configured,
drops duplicate, unavailable, and non-interactive items, and applies these
selection rules atomically. A host treats `SelectionChanged` as an output
and assigns `Selection` only for an independent external selection/current
action; an equal logical request is a no-op.

## 14. Marquee selection

When `IsMarqueeEnabled` is true and `SelectionMode` is `Multiple` or
`Extended`, dragging from empty row-surface space with a mouse or pen creates a
selection rectangle, and so does dragging from a row the table would not drag:
one it withholds section 16's drag from because reordering is off, the host has
no handler, the view is sorted by a column that is not the row order, or the
row is not interactive. Dragging from a row the table would drag is section
16's row drag. Which of the two a press becomes depends only on what it landed
on, never on the direction of the first movement. The reference implementation
draws the same line between its rows and the canvas beside them; a row it would
not drag starts the rectangle here as well, because nothing competes for the
gesture there and the pointer has already said so with the arrow, where a
draggable row shows the move cursor. In `None` and `Single` modes, marquee
selection is inactive. Touch remains native scrolling/selection/context-menu
input; it does not begin a marquee gesture.

- the rectangle is drawn in an overlay above rows and below menus;
- a plain marquee replaces selection with its intersected interactive rows;
- Ctrl adds/toggles against the selection captured at gesture start;
- Shift extends from the current anchor;
- intersection with a realized row's band selects that row;
- auto-scroll occurs near the top or bottom edge;
- Escape cancels and restores the selection as it stood before the press, so a
  rectangle begun on a row takes that press's own selection change back with
  it;
- the gesture never begins from an interactive cell descendant, header, resize
  separator, or active row-reorder handle/gesture;
- the table delays an empty-surface clear until pointer release or the drag
  threshold, so starting a marquee does not briefly clear selection first;
- a press on an unselected row the table would not drag selects that row as a
  click would, and the rectangle covers that row from its first movement, so
  nothing the press did is taken back. A press on a selected such row defers,
  as every press on a selected row does, and the plain rectangle then replaces
  the packet with what it covers, exactly as the click it would otherwise have
  been would have replaced the packet with that one row;
- whether the table would drag the pressed row is answered at the press and
  again at the threshold, and the drag needs both. A row that became draggable
  in between, after a press that had already applied its click, starts the
  rectangle rather than moving a packet that click just made;
- the rectangle's fixed corner is a point in the scrolled content, never a
  row: a source update, `RefreshView()`, or sort change during the gesture
  leaves the rectangle where it is on screen, moves the rows through it, and
  re-asks which rows stand in its band. The viewport itself MUST hold still
  under such an update, so the list panel keeps its scroll offset rather than
  following a row; a rectangle whose corner jumped with the row the panel was
  following is what the owner saw under a sort by speed;
- the overlay disappears on completion, Escape cancellation, unload, or the
  host withdrawing `IsMarqueeEnabled` mid-gesture; the last two
  restore the selection as it stood before the press.

Empty row-surface space is below the last row and beside the last column. A row
is only as wide as its columns, so the space to their right belongs to no row.
That is a requirement and not an appearance: with full-width rows and enough of
them to fill the viewport there is no empty surface anywhere on screen, every
press lands on a row, and while those rows can be dragged the gesture cannot be
started at all. The reference
implementation is built the same way: its rows sit on a canvas the width of the
columns inside a scroll container the width of the viewport, and a press in the
space between the two starts its rectangle.

The line between a row and that space MUST be visible, or the space reads as a
drag that stopped working. Measured once on the original host at 2,538 wide with
1,120-wide rows: 1,418 pixels of every row band, 56%, started a rectangle
where the user expected a drag, with nothing on screen to mark the line, and
the line moved with every fit, resize and hidden column. That host's diagnostics
harness has been deleted and the one that replaced it runs a different feed with
different columns, so the 56% cannot be reproduced as written. It is recorded
because it is why this clause exists, and the clause does not turn on the exact
fraction — only on the space being wide enough to be pressed by mistake.

The row's own fill
marks it only while the row is selected, and hover is off by the owner's
ruling, so the pointer marks it: the table shows the move cursor over a row
that can be dragged and the arrow over the space beside it, as the reference
shows its grab cursor. Section 19 requires the selected fill to stop at the
same line for the same reason.

While the table offers the drag, the gesture's availability therefore depends
on the column widths. While the columns are narrower than the viewport, the
space beside them starts a marquee on any row line; once they fill it there is
none, and the only empty row surface left is below the last row, which a full
table does not have. That is a boundary of this rule rather than a defect in
it, and the reference has the same one for the same reason. While the table
withholds the drag, every row starts a rectangle and the widths do not matter.

A row's band is its vertical extent across the whole row surface. The rectangle
is tested against the band, not against the row's own box, so how far it reaches
across a row says nothing about whether that row is in it: a rectangle drawn
entirely in the space beside the columns still selects every row it spans.
Testing the box instead would make the one place the gesture can start the one
place it selects nothing.

Only visible/realized geometry is measured. As auto-scroll realizes additional
rows, they participate normally. The overlay uses platform-aware feedback,
does not become an automation element, and never conveys resulting selection by
color alone.

## 15. Row activation and context requests

A row is invoked by double-click, double-tap, or Enter when the original input
target is not an interactive cell descendant. `ItemInvoked` supplies the row
item and the current selection. It does not execute a command itself.

Context invocation includes right-click, touch press-and-hold, and the Menu key
or Shift+F10 on the current/focused interactive row. The platform's normal
press-and-hold recognition resolves touch context invocation; row drag begins
only from a mouse/pen gesture after its normal drag threshold. The table adds no
competing long-press timer.

Row-context behavior:

- if the row is already selected, preserve the existing multi-selection;
- otherwise select only that row when selection is enabled;
- in both cases make the target the current row, logical row focus, and the next
  range-selection anchor. A current-item change raises `SelectionChanged`
  even when the selected packet itself is unchanged;
- raise `ItemContextRequested` with the target item, an immutable selected
  packet, a row `FrameworkElement` placement target, and a point relative to
  that target when pointer-originated (otherwise a null relative point);
- let the host synchronously construct and show a native `MenuFlyout` or
  `CommandBarFlyout` with domain-specific commands at that placement target.

The control draws its own icons from the Lucide library it references. A host
building this row menu draws from the same set through `Lucide.Font` and the
glyph names on the `Lucide` class, rather than through a path into the library's
package or a theme key, which `Application.Current.Resources` cannot reach.

The placement target is presentation context, not durable view-model state. A
host forwards command intent to its view model but creates/shows the flyout
at the view boundary while that target is valid. Closing a flyout returns focus
through normal native flyout behavior. The host owns its menu's labels,
enablement, keyboard behavior, and accessibility; `Table` owns only the
selected/current mechanics and transient placement context. `IsRowGestureEnabled="False"`
and interactive cell descendants suppress the row request and retain their own
context menus. This makes multi-selection context menus predictable for any
domain without giving the generic table a domain menu model.

## 16. Row drag reordering

Row reordering is disabled by default as described in section 5. When enabled,
it supports multi-row movement without embedding domain ordering policy.

The host MUST set `CanReorder` to false whenever its external
filter/order cannot map a visual placement to domain ordering. The active table
sort is the table's own to judge: it offers the drag only while the view shows
the row order — unsorted, or sorted either way by the column whose
`DefinesRowOrder` is true — and withholds it under any other sort, where a drag
from a row is section 14's marquee instead. If `CanReorder` becomes
false during a drag, or the sort stops showing the row order during one, the
table cancels the drag, whether or not the rows moved. For a race or command
failure, the host simply does not change (or reconciles) its projection;
there is no post-drop accept/reject protocol.

- a mouse/pen passive row press that ends before the normal drag threshold
  follows normal selection behavior; a press that crosses it becomes a row drag;
- dragging an unselected row creates a one-item packet without disturbing the
  existing selected packet;
- dragging a selected row moves the complete selected packet;
- non-interactive display rows and rows refused by the reorder predicate cannot
  start or join a drag packet;
- packet order follows the current visual order;
- an immediate, theme-aware insertion indicator identifies the legal boundary;
- the event supplies `Items` and `Before` as defined in section
  5.1, both in row order: the visual order, or its reverse when the
  `DefinesRowOrder` column is sorted descending; it never uses a target inside
  the moving packet;
- top, between-row, and append-after-last placements are valid. Invalid,
  packet-internal, and no-change placements are cancelled without an event;
- `Table` does not mutate the collection; after the event, the host may
  update its own source;
- the table returns to idle immediately after the event; the host may begin
  asynchronous domain work and reconcile its own projection;
- a selected moving packet remains selected. An unselected-row drag preserves
  the prior selection and current item;
- Escape cancels the drag;
- cell controls that handle manipulation do not start row dragging.

During a valid drag, the pointer feedback, insertion cue, and realized-neighbor
movement use normal WinUI drag/list layout behavior. Escape, source/view
invalidation, and invalid drops remove that feedback and leave data/layout
unchanged. When the host later publishes a changed source, its normal
repositioning supplies the resulting visual continuity; when it publishes no
change, rows settle in their original order. The table does not create a
speculative second order, await a remote result, or require host animation
coordination.

Direct row reordering is a mouse/pen gesture in version one. On touch,
native panning, selection, and press-and-hold row context input retain
precedence; the table installs no competing touch-reorder recognizer. The drag
affordance and destination status must be exposed accessibly while a drag is
active, but the control MUST NOT claim UI Automation Drag/Drop patterns unless
it genuinely implements and verifies them. Because domain ordering remains host
policy, a host that enables row dragging MUST also offer equivalent domain move
commands in its row context menu or another keyboard-accessible command
surface; those commands are also the touch path and may operate directly on the
domain rather than fake a pointer drop.

The host decides whether reordering is valid under its current filter and how
a place in the row order maps to domain ordering; the table decides it under
the sort. Optimistic updates, remote calls, failure handling, and
reconciliation remain outside `Table`.

## 17. Loading and empty states

`Placeholder` has exactly three values: `Empty`, `Loading`, and
`NoResults`. The host sets it, because only the host can tell an empty
domain source from a filter that excluded everything, or from a fetch in
flight. There is no separate loading flag beside it: a boolean qualifying an
enum is the enum missing a member, and keeping the two apart let a host say
“loading, and also no results,” which is not a state.

The row surface shows exactly one of:

1. rows when the current view has items;
2. the content `Placeholder` selects when the view is empty—`LoadingContent`
   for `Loading`, `EmptyContent` for `Empty`, `NoResultsContent` for
   `NoResults`.

`Placeholder` says only which presentation an *empty* view gets; it never hides
rows. During refresh existing rows remain visible, so a host may set `Loading`
for a refresh without blanking the table.

The table ships default content for all three, so a minimal host writes none:
the platform's progress ring for `Loading`, and one line of text from the
control's own resources for the other two. That default is deliberately the
least the platform can say—no spacing, colour, or font size is chosen there,
because none of them is derivable—and a host that wants more supplies its own
content or template. What it replaces is the blank rectangle that a table with
no rows and nothing configured used to render.

Content and templates otherwise come from the host, and the table provides
layout only. Application-level error, offline, and permission states remain
outside the table unless the host deliberately supplies them as content.

## 18. Layout persistence

The table exposes, but does not store, a data-only snapshot:

```csharp
public sealed record ColumnLayout(
    IReadOnlyList<string> Order,
    IReadOnlyDictionary<string, bool> VisibilityOverrides,
    IReadOnlyDictionary<string, double> WidthOverrides,
    string? SortColumnId,
    SortDirection SortDirection,
    bool FitButtonHidden = false,
    bool FillButtonHidden = false);
```

The snapshot is read and written through one property, `Table.Layout`.
Reading gives an independent snapshot of the overrides; assigning restores one.

The sort is persisted as a column ID and a direction rather than as the
`Sort` value section 5 uses at runtime, because this record has to survive
being written to a settings store and `Sort` names a live `Column`.

`ColumnLayout` deliberately has no version field. Stable column IDs and
defensive restoration are sufficient for version one; if the host later needs
to version its stored envelope, that envelope is the version boundary.
The only valid persisted directions for an active sort are ascending and
descending; `SortDirection` is ignored when `SortColumnId` is `null`.

`VisibilityOverrides` and `WidthOverrides` are intentionally sparse: they contain
only values that override declared visibility and width baselines. A missing
column ID means “use that column's baseline.” Reading `Layout` follows the
same rule, so untouched defaults do not become duplicate persisted
configuration. A column with no `Id` is not persisted at all: it appears in
neither `Order` nor either override map.

`FitButtonHidden` and `FillButtonHidden` record that the person hid that header
button from the menu. False is the baseline, so a snapshot stored before these
fields existed restores both buttons shown.

Persist:

- full column order, including hidden columns;
- visibility overrides;
- explicit width overrides in DIPs, including overrides for hidden columns;
- active sort column and direction.

Do not persist in the layout snapshot:

- selected/current items (a host stores their keys itself);
- the scroll offset (a host stores `VerticalOffset` itself);
- loading state;
- hover, drag, resize, marquee, or context-menu state;
- row data.

Applying saved state MUST be defensive:

- ignore unknown IDs;
- ignore duplicate IDs after their first valid occurrence;
- append newly introduced columns in definition order;
- treat `VisibilityOverrides` and `WidthOverrides` as complete override maps:
  omitted values use their column baseline and clear any earlier override;
- ignore non-finite, non-positive, and non-resizable-column widths; raise valid
  finite widths to the column's `MinWidth`;
- restore required columns if saved as hidden;
- restore the saved sort when its column is currently sortable, visible once the
  saved visibility has been applied, and its direction is valid; otherwise use
  natural order, including when the sort column is missing, non-sortable,
  hidden, or absent;
- guarantee at least one visible column.

Each read of `Layout` is an independent snapshot that the table does not mutate
afterwards. `LayoutChanged` is raised once after each completed effective sort,
column move, resize, fit, visibility, or reset operation, including public
fit/reset calls, and carries only which of those it was. It is not raised by
initial setup or by assigning `Layout`; this prevents restore-and-persist loops.
Initial baseline widths never force persistence. A host that persists layout
reads `Layout` from its `LayoutChanged` handler, debounces, and writes that
snapshot to its settings store.

## 19. Accessibility, input, theming, and motion

`Table` is a dense native list/table surface, not a new visual or
accessibility framework. The host supplies its localized control-level
accessible name through `AutomationProperties.Name` or an explicit automation
label relationship; a nearby visible caption alone is not that relationship.
When the host shows a caption, it uses the same localized text. The control
itself MUST:

- preserve native collection-control selection, scrolling, virtualization,
  focus, and UI Automation behavior;
- expose named, localized column headers with sort direction/state and
  discoverable header actions; generated menu commands must be reachable,
  enabled, and checked correctly through keyboard and UI Automation;
- expose selected/current and unavailable row state;
- retain visible native focus indicators and a logical Tab/arrow-key order;
- keep rich-cell controls as independently named, focusable automation peers.
  Decorative layout elements and marquee overlays must not pollute the
  automation tree;
- use a list/list-item automation structure with separately named headers. It
  MUST NOT claim a full UI Automation `Table`/`Grid` pattern unless the
  implementation genuinely supplies and verifies the complete patterns and
  header/cell relationships those patterns require;
- make sort, selection, drag destination, and current state understandable
  without color alone. A drag destination includes a positional cue; a
  focus/selection state retains its normal visual and programmatic state;
- work with mouse, keyboard, pen, and touch according to sections 11–16.
  Direct resize and row/column reorder are mouse/pen gestures in version one;
  standard touch scrolling, selection, and press-and-hold menus remain native
  paths to the corresponding table or domain commands. Where the table or a
  host exposes a custom touch target, its effective target is approximately
  40 × 40 effective pixels (the visible glyph may be smaller). A dense
  mouse/keyboard-only host need not enlarge every row mechanically;
  mouse/pen marquee selection is intentionally not a touch gesture;
- use native `MenuFlyout`/`CommandBarFlyout` behavior and restore focus through
  the platform when those transient surfaces close;
- use WinUI `ThemeResource`s and built-in controls so Light, Dark, user accent,
  and High Contrast update at runtime. It must not hard-code colors or define
  TableView-specific brush, type, geometry, spacing, or token resources;
- meet at least 4.5:1 contrast for table-owned normal text and 3:1 for
  table-owned large text and required non-text information, including
  drag-destination, and availability cues, in every applicable state and
  supported theme. A resource name or use of a standard palette is not proof of
  that result. The table draws no current-row cue: WinUI draws none either, and
  its row container has no visual state for a current row that is not selected,
  because Fluent gives position to the focus visual and choice to selection.
  Current remains a model concept that section 13 needs for the anchor and for
  range selection, and nothing measures it because nothing paints it;
- **Owner ruling: use the native row container's selection fill and focus
  visual, without an additional selected-row stripe.** Selection identifies
  the whole row; an extra vertical mark adds unwanted chrome beside its data.
  Keep native High Contrast selection behavior and UI Automation state;
- end a row's own fill at its last column rather than at the edge of the list,
  and show the move cursor over a row that can be dragged. The space to the
  right of the last column belongs to no row, and section 14's marquee is
  started from it; the fill and the cursor are what make that line visible,
  and on a row that is not selected the cursor is the whole of it. A fill
  spanning the list would claim that space for a row, and a pointer that
  never changed would leave the line where it was measured: invisible, and
  read as a drag that stopped working;
- preserve normal effective-pixel text scaling and platform type behavior.
  Text and controls must remain readable and operable at supported display/text
  scaling without a table-specific font-size override.

Motion is functional feedback, not decoration. Direct resize and drag feedback
tracks the input immediately. Column moves and the host-published outcome of a
row reorder use the standard WinUI list/reposition/drag transitions when they
are available; cancellation returns to the unchanged layout without a second
animation system. The transitions clarify movement and destination but never
delay input, create a fake successful reorder, or require a host to coordinate
internal animation state. When the system disables UI animations, the same
state changes and insertion cues remain clear without custom substitute motion.

The control's accessibility contract is verified with keyboard-only use,
Narrator, UI Automation inspection, Light/Dark/High Contrast, animation
enabled/disabled, the supported allocation and display/text scaling range, and
long localized content. A host template remains responsible for the
accessible name, contrast, state, and input behavior of any custom visual or
interactive content it introduces.

## 20. Performance requirements

The component supports dense, frequently updating lists. Its performance
contract is expressed as invariants rather than an unproven source-count or
throughput target.

- Vertical rows MUST be virtualized and recycled by the native row surface.
- No work may scale with all rows during normal scrolling, column drag, resize,
  or visibility changes.
- A display-only row update MUST NOT rebuild the whole table. A source, sort,
  or `RefreshView()` change may recompute the private view as specified.
- A sort or source update raises collection notifications for membership
  changes and for the positions the row surface holds a container for, and
  for nothing else (section 5.3). The number of notifications a reorder
  raises is bounded by realized containers, never by the row count. Measured
  once, on the original host's 2,002 rows in Release, with no collection inside the
  span: a full reversal fell from 3,998 notifications to 34, and the collection
  change from 223 ms to 13 ms. Neither half can be reproduced now, and both are
  recorded rather than deleted only so the requirement's origin is readable. The
  "before" half was taken against a reconcile that announced every row it moved,
  and that implementation is gone. The "after" half was taken on that host's own
  diagnostics harness, which has been deleted; the harness that replaced it
  (`lib/TableView/sample/Jobs/JobsPage/Diagnostics.cs`) runs a different
  feed with different columns, so it tests the same claim on a different list and
  reports its own numbers. Do not quote these four as current.
  The claim is falsifiable and MUST be tested as such —
  it fails the moment a container's content differs from the view's row at that
  container's index, so the test is a sort, a far scroll and a `ScrollIntoView`,
  each followed by comparing every realized container against the view. Rows
  placed without a notification give the panel no reason to re-examine what it
  holds, so the view MUST invalidate measure when its order moved; without that
  the foot of the viewport stays blank until something else forces a pass.
- A reorder MUST need no forced layout. Two reconciles arriving in one dispatcher
  callback — a source publish and the settle timer landing together with a sort
  applied — resolve without one, because the row surface updates its own map of
  container to index inside the notification rather than at the next layout pass.
  This is stated because the defensive fix is expensive and invisible: a reader
  worrying about a stale realized set adds an `UpdateLayout` to the reconcile
  path, which is a full layout on every publish, and nothing else in the tree
  would tell them it was measured and found unnecessary. The diagnostics carry
  that measurement.
- Column layout changes affect only headers and realized rows.
- Source updates, property updates, sorting, scrolling, visibility changes, and
  host-window resizing MUST NOT measure content or change widths.
- Active sorting is `O(n log n)` and occurs only on source, sort, or explicit
  `RefreshView()` changes.
- Explicit fit commands measure only the header and currently realized cells;
  they never create a second measurement surface or force off-screen
  realization.
- Pointer movement does not produce layout-persistence events.
- Reposition and drag feedback use platform-appropriate transitions and require
  no host-visible animation or suppression protocol.

The component introduces no runtime dependency beyond WinUI 3. This keeps
package size, memory use, and versioning surface small.

## Appendix — Core verification checklist

The component is ready when these generic scenarios pass in a WinUI 3 sample or
host integration, with validation scope set by [testing](../../../docs/testing.md).

1. Typed templates render text, progress, buttons, toggles, and custom controls
   without stealing their normal input, focus, or automation behavior.
2. A representative large source keeps realized rows driven by the viewport,
   aligns headers and rows, and does not traverse the full source during normal
   scrolling or direct column-layout gestures.
3. A view-affecting batch followed by one `RefreshView()` applies interactivity
   and sort without re-enumerating a plain source.
4. Header mouse/pen click, touch tap, and keyboard activation cycle ascending,
   descending, and natural stable order without interfering with embedded
   controls. Equal values use current source order, and clearing or resetting
   sort returns to natural order.
5. Mouse/pen column drag and touch/keyboard header commands produce the same
   order; the header menu respects hide, visibility, fit, resize, and move
   eligibility and never leaves zero visible columns.
6. Restored widths, and declared widths after the first fill, remain stable
   across data and layout changes.
   Direct resize, bounded fit, hide/show, reset, and save/reload preserve their
   specified baseline/override behavior without off-screen measurement.
7. `None`, `Single`, `Multiple`, and `Extended` selection limits work with
   pointer and keyboard input. `Selection.Current` can be unselected; assigning
   `Selection` produces the specified state without feedback loops, and
   same-key rehydration retains that state.
8. When enabled, mouse/pen marquee selection, modifiers, edge auto-scroll, and
   Escape work without stealing cell input or touch scrolling.
9. Invocation and row-context requests provide the expected selected/current
   state and a valid transient placement context; host menus retain their own
   commands and accessibility.
10. In an eligible view, mouse/pen row drag sends the current-visual-order
    packet and a valid post-removal `Before` item without mutating
    the source. Invalid/no-change drops emit nothing, and the host exposes an
    equivalent keyboard/touch domain move command.
11. Loading, empty, no-results, and refresh-with-existing-rows follow
    the stated precedence.
12. Saved layout restores valid order, visibility, widths, and sort; obsolete
    values recover defensively to a usable natural/sorted layout. Applying it
    before or after `Loaded` is silent and never mutates source or baseline
    definitions.
13. Collection updates establish the latest source order; display-only property
    changes do not rebuild or sort. View-changing updates reconcile state,
    cancel incompatible marquee or row-drag gestures, and retain live cell
    bindings. While the pointer is over the rows, a leaving row stays dimmed in
    place, arrivals wait, and a re-sort leaves the pointed row where it is.
14. Policy callbacks are pure; `SelectionChanged` and the other four
    events carry their documented immutable post-mechanics state without
    invoking domain work. The component contains no domain types, commands, or
    service references.
15. Keyboard-only use, Narrator, UI Automation, Light/Dark/High Contrast,
    supported scaling, mouse, pen, touch, and animation-enabled/disabled modes
    preserve the stated interaction, focus, contrast, overflow, and motion
    behavior without a parallel visual or automation system.
16. Invalid schema/callback contracts, ineligible interactions, callback
    failures, and obsolete layout data follow section 5.4 without applying a
    partial table state.
