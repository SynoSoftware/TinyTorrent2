# TableView implementation

Source map updated 2026-10-04. This describes the existing TableView library and
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
| Displayed sequence and collection reconciliation | [Body.View](../src/Body/View.cs). |
| Effective column geometry | [ResolvedLayout](../src/ResolvedLayout.cs). |
| Shared header and row arrangement | [CellsPanel](../src/CellsPanel.cs). |
| Hierarchy cell indentation and disclosure | [Body.Branch](../src/Body/Branch.cs), created only for the hierarchy column. |
| Selection, current item, anchor, and logical focus | [SelectionState](../src/SelectionState.cs). |
| Row pointer arbitration | [Table input](../src/Table/Input.cs). |
| Marquee geometry and row insertion feedback | [Body.Marquee](../src/Body/Marquee.cs) and [Body.Drag](../src/Body/Drag.cs), driven by the arbiter. |
| Header gestures and generated menu | [Header.Strip](../src/Header/Strip.cs) and [Header.Menu](../src/Header/Menu.cs). |
| Generic control text | [Strings](../src/Strings.cs), reading the embedded [en.json](../src/Resources/en.json). |

The source capture and displayed sequence serve different roles: one receives
host items, the other presents the private view. Neither owns domain records or
executes host commands. New behavior goes to its existing owner; the table above
does not set a quota of classes or files.

The [hierarchy extension](hierarchy.md) keeps host expansion state outside the
control. Captured child membership feeds the same private view, sorting cadence
and selection reconciliation as flat rows. `Table/Hierarchy.cs` integrates
navigation and collapse focus; `Body.Surface` adds hierarchy automation to its
existing item peers. Flat schemas do not allocate this source state or cell
chrome. The sample's Project plan page supplies a non-torrent hierarchy with a
large collapsed branch.

## Rendering and interaction

The body uses a virtualized `ListView`; the header is a sibling. Header and rows
use the same panel and resolved geometry. The body's native scroller owns the
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

The current arbiter has `None`, `Pressed`, `Marquee`, and `RowDrag` phases. Earlier
notes describing a `Committed` phase and calling the marquee during a row drag
are stale; inspect the current transitions before proposing a fix. Deferred click
decisions preserve the press/release contract, not an accidental processing delay.

Keep reconciliation bounded by the containers the list actually holds. The
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

Keep virtualized work on realized rows. Explicit fit owns measurement; routine
sorting, scrolling, and gesture feedback do not create a hidden measurement tree.
Rich cell templates receive row items directly. Live values use live bindings;
formatting can stay lazy so invisible cells do not allocate display strings.

The native container paints selection and keyboard focus. The row template
adds only drag opacity; it does not paint an additional selected-row stripe.
Actual contrast, Narrator,
and out-of-process automation still require relevant runtime evidence under the
[testing policy](../../../docs/testing.md).

## Localisation and source lifetime

`Strings.Load` prepares an immutable catalogue with parent/English fallback
and placeholder validation. `Table.Strings`, `Column.DisplayName` and `Column.Header`
refresh existing presentation through the table's text path. Generated menu items
subscribe while open. The standalone default remains English. This code has not
yet been built or exercised in a live language switch.

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
  keyboard and automation input and raise its invoked event. The hosted list
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
consequence also depends on documented platform mirroring. The narrow product
evidence is recorded in [implementation](../../../docs/implementation.md);
it does not establish Narrator, detached reload or unrealized-row behavior.
The later item-data and header-peer changes have source review only. The focused
`AutomationTests` cover item selection, keyed replacement and selection policy;
they have not run, and do not establish out-of-process provider routing.

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
makes its row ineligible without replacing it. Context invocation rechecks the
same interaction policy after the callback; otherwise the host receives a menu
request for a row it just made unavailable.

## Earlier evidence

The [archived design](archive/tableview-design.md) preserves platform investigations
and rejected alternatives. Its old type names, proposed templates, open spike
questions, and deleted-harness measurements are historical. Use current source
and reproducible evidence when revisiting those decisions.
