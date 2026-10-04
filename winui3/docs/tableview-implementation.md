# TableView implementation

Source map reviewed 2026-10-03. This describes the existing Synapse library and
the reasons for its construction. The [contract](tableview-contract.md) owns
observable behavior and the public API; this map is not a competing specification.
The sample and tests exist. No new rendering or runtime verification was performed
for this documentation review.

## Owners in the current code

| Responsibility | Implementation |
| --- | --- |
| Public control, template lifetime, composition | [TableView](../src/Synapse/TableView.cs) and its partial files. |
| Source subscription and coherent source capture | [TableSourceView](../src/Synapse/TableSourceView.cs). |
| Displayed sequence and collection reconciliation | [TableItemsView](../src/Synapse/TableItemsView.cs). |
| Effective column geometry and horizontal offset | [ResolvedLayout](../src/Synapse/ResolvedLayout.cs). |
| Shared header and row arrangement | [TableCellsPanel](../src/Synapse/TableCellsPanel.cs). |
| Selection, current item, anchor, and logical focus | [TableSelectionModel](../src/Synapse/TableSelectionModel.cs). |
| Row pointer arbitration | [TableView.Input](../src/Synapse/TableView.Input.cs). |
| Marquee geometry and row insertion feedback | [TableMarquee](../src/Synapse/TableMarquee.cs) and [TableRowDrag](../src/Synapse/TableRowDrag.cs), driven by the arbiter. |
| Header gestures and generated menu | [TableHeaderStrip](../src/Synapse/TableHeaderStrip.cs) and [TableHeaderMenu](../src/Synapse/TableHeaderMenu.cs). |
| Generic control text | [TableResources](../src/Synapse/TableResources.cs). |

The source capture and displayed sequence serve different roles: one receives
host items, the other presents the private view. Neither owns domain records or
executes host commands. New behavior goes to its existing owner; the table above
does not set a quota of classes or files.

## Rendering and interaction

The body uses a virtualized `ListView`; the header is a sibling. Header and rows
use the same panel and resolved geometry. The control owns the horizontal offset;
the body's native scroller owns the vertical axis. Wrapping that list in another
vertical scroller or calculating column positions in a second place breaks those
responsibilities.

The selection model reconciles identity across changing source instances.
Containers reflect that model rather than supplying a second domain selection.
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

The control adds its own selection cues where the standard container does not
meet the contract. It currently draws no current-row or focus cue, as recorded
below. Earlier selected-row contrast of 1.08:1 is the reason the
regression check exists, not a current rendering claim. Actual contrast, Narrator,
and out-of-process automation still require relevant runtime evidence under the
[testing policy](../../docs/testing.md).

## Known localisation gap

The contract requires live `DisplayName`, generated header/menu text, and
accessibility updates without rebuilding the schema. Currently `DisplayName` is
an ordinary CLR property without change notification; generated headers and
automation names receive its value when built. `TableResources` uses a static
resource loader over the library's English `.resw`. Neither provides the required
live invalidation or shared catalogue implementation.

Implement this once in Synapse, following [localisation](../../docs/localisation.md#synapse),
before the new product relies on live switching. Retain standalone fallback and
domain independence. Do not fork TableView, recreate the control on a language
change, or quietly mark its existing contract complete.

## Other known integration gaps

Source review found these gaps on 2026-10-03. They are unresolved implementation
work, not completed fixes or grounds for replacing the control.

- **Source lifetime:** [TableSourceView](../src/Synapse/TableSourceView.cs) detaches
  collection notifications only when its source changes. TableView's unload
  path stops its timer but leaves that subscription and `SnapshotChanged` rebuilds
  active. A longer-lived collection can retain a detached table and keep updating
  its old view. Suspend subscriptions while detached and recapture on reload at
  the existing source owner, preserving selection and control state.
- **Automation selection:** [TableView.Selection](../src/Synapse/TableView.Selection.cs)
  restores its own selection after every unsolicited native `SelectionChanged`.
  Native UI Automation selection therefore gets undone too. Route automation
  requests to the existing selection operations; accepting arbitrary native
  selection would break the deliberate pointer arbitration.
- **Automation exposure:** [TableHeaderCell](../src/Synapse/TableHeaderCell.cs)
  and TableView derive from `Control` without creating automation peers. Header
  name/sort attached properties and the table's drag announcement therefore lack
  those peers. Supply the needed header actions/status and table notification
  support using [platform peers](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/custom-automation-peers),
  preserving the hosted list's native automation instead of duplicating it.
- **Keyboard location:** [Generic.xaml](../src/Synapse/Themes/Generic.xaml)
  deliberately suppresses native row focus, while `TableRowVisual` draws neither
  focus nor current state. Ctrl+Arrow can move the keyboard location invisibly,
  conflicting with contract section 19. The suppression is a retained workaround
  for incorrect focus restoration during recycling. Resolve it in the
  [hands-on row review](tableview-contract.md#8-rendering-layout-and-visual-language),
  preserving visible keyboard location without reintroducing focus movement to
  unrelated rows or controls. The implementation remains incomplete against the
  keyboard contract until that correction has relevant runtime evidence.
- **RTL header navigation:** [TableHeaderStrip](../src/Synapse/TableHeaderStrip.cs)
  maps Left/Right to index minus/plus one without considering `FlowDirection`.
  Those keys must follow the mirrored visual order. Include them in the existing
  live-language exercise. [XAML already mirrors the coordinate frame](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.frameworkelement.flowdirection);
  do not add a second panel-mirroring path based only on this input defect.

The lifetime and automation findings follow concrete source paths. The RTL
consequence also depends on documented platform mirroring. None has been checked
in a running product, Narrator, or an external automation client in this review.

## Earlier evidence

The [archived design](archive/tableview-design.md) preserves platform investigations
and rejected alternatives. Its old type names, proposed templates, open spike
questions, and deleted-harness measurements are historical. Use current source
and reproducible evidence when revisiting those decisions.
