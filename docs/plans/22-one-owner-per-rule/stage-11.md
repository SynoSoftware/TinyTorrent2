# Stage 11 gate record: rules inside the table

Gate done on 12 November 2026, on commit a4312a7. Paths are under
`lib/TableView/src`. Line numbers are those of a4312a7. The plan's numbers
are those of 7e0ee84: since then `CellsPanel.cs` moved down 3 lines
(`FindOwner` 97 is now 100) and `Table/View.cs` moved up 4 lines after line
555 (`KeyOf` 653 is now 649). The other files did not change.

## History

The [TableView ruling](../../../lib/TableView/AGENTS.md#control-work) asks
for earlier attempts at the same change or its opposite. Searched with
`git log -S` over `v0.9.1.2..HEAD` for each copy.

- **`KeyOf`.** 884167c (preparation off the UI thread) wrote the copies in
  `Body/Projection.cs` and `Table/View.cs`. 2dec906 gave each projection a
  second `ItemIdentity` whose selector read a precomputed key dictionary;
  bfc54dc removed it when `Body/Slots.cs` took over keys, and wrote the
  third copy there. No commit tried one owner, and none tried the opposite.
- **`FindOwner`.** Both copies exist at v0.9.1.2 and no commit since
  changed them.
- **`ShowItem`.** bfc54dc ("A row rebind raises one managed event") copied
  `RefreshChild`'s two assignments. The same commit replaced an attached
  `Column` property with the `_cells` list, so layout reads its cells
  without asking WinUI. Keying that list by `EffectiveColumn` keeps this.
- **The other pairs** date from cdd6a03 or earlier. No commit since
  v0.9.1.2 changed or merged them. The 3 November revert (25da2fe) was for
  memory and touched none of them.

## Findings

Every finding is kept. Each states its rule in one sentence.

1. `Body/Row.FindOwner` (128) and `CellsPanel.FindOwner` (100): the table
   an element belongs to is the first `Table` among its visual ancestors.
   The two loops are identical.
2. `KeyOf` in `Table/View.cs` 649, `Body/Slots.cs` 492 and
   `Body/Projection.cs` 194: an item's key is the schema's key, or the item
   itself when the schema has none. The gate found two more copies of the
   same identity rule. `Hierarchy.cs` 72 computes the key by hand.
   `Body/Slots.cs` 94 and `Body/Projection.cs` 593 each write "keys compare
   by reference without a schema key, else by the schema's comparer". The
   owner absorbs both.
3. `Header/Strip.cs` 759 and `Table/Input.cs` 261: resize, column drag,
   row drag and the marquee are mouse and pen gestures, and touch keeps the
   platform's paths (contract section 19, "Direct resize and row/column
   reorder are mouse/pen gestures in version one").
4. `Table/Input.cs` 521 and 560: a click or a tap selects by section 13,
   with the row's rules over a row and a clear beside the rows.
   `ApplyPressSelection`'s own summary already claims to be the one
   implementation of what a click means.
5. `Header/Strip.cs` 205 and `Header/Menu.cs` 111: the screen's left is one
   step back in visible order, and one step forward in a right-to-left
   table, because XAML mirrors it.
6. `CellsPanel.cs` 217 (plan: 209) and `EffectiveLayout.cs` 110: a
   column's place among the published visible columns, or -1 when it is
   hidden. Both scan the same `_visible` list.
7. `CellsPanel.cs` 139 and 342 (plan: 136 and 289): a row cell shows its
   row's item, a plain cell as its content and a hierarchy cell as its item.

Seen and left as they are:

- **The header menu's check items.** Inside the table one mechanism
  remains: every item, check items included, goes through `Item` in
  `Header/Menu.cs` 45, with one `holdOpen` refusal of the close (27). The
  app's `Controls/MenuItems` is the app's owner, and the table uses no app
  code, as "What stays as it is" says.
- **The entry checks in `Body/Slots.cs` 306-309 and `Hierarchy.cs`
  68-73.** Both refuse a row outside the row type and a null key, with the
  same text. They guard two entry paths: `Hierarchy.Capture` must refuse a
  row before its identity dictionary hashes the key, and `Slots.Number` is
  the only check of a flat source. Not a finding of this stage.

## Questions answered from the code

1. **A null key.** `Table/View.cs` 649 and `Body/Projection.cs` 194 used
   the item when the key selector returned null; `Body/Slots.cs` 492
   returned null. Contract section 5 says "Null configured keys or
   duplicate effective identities fail fast", and `Body/Slots.cs` 308 and
   `Hierarchy.cs` 72 refuse such a row where rows enter. **Decision:**
   `KeyOf` returns what the schema's key returns, typed `object` as the
   selector is (`Table.cs` 327), with no fallback. Every row the table
   accepts has a non-null key, so the callers that used the fallback get
   the same key as before: `Table/Holding.cs` 53 reads rows the view shows,
   and `Body/Projection.cs` 595 reads numbered slots. Only a host whose key
   for a row turns null after the table accepted it sees a difference, and
   `Schema.Key` (`Schema.cs` 65) requires a stable, non-null key.
2. **`ItemKey` or the identity's selector.** `Table/View.cs` 649 read
   `Table.ItemKey`, the other copies the identity's selector. `Table.cs` 358
   copies `ItemKey` into the identity at schema capture, and `Schema.Key`
   refuses after capture (`Schema.cs` 75, `Table.cs` 320), so the two are
   equal from capture on. Before capture the held set is empty
   (`Table.cs` 45), so `IsHeld` answers false either way.
3. **The strip's flow direction or the table's.** `Header/Strip.cs` 205 read
   the strip's own `FlowDirection`, `Header/Menu.cs` 111 the table's. The
   property inherits through the visual tree and `Themes/Generic.xaml` sets
   it nowhere, so they are equal.
4. **Cells keyed by `Column` or by `EffectiveColumn`.** Schema capture makes
   one `EffectiveColumn` per declared column (`Table.cs` 349-352), and the
   schema is fixed after the first `Loaded` (`Table.cs` 320), so the two
   keys are one to one for a table's lifetime. **Decision:** the cells
   panel keys its cells by `EffectiveColumn`, the type the layout publishes,
   and asks the layout for the visible place.

## Owners and interfaces

- **`ItemIdentity`** (`SelectionState.cs`) owns section 5's identity rule.
  - `Install(key, comparer)` sets the schema's key once, at capture. The key
    selector becomes private.
  - `KeyOf(item)` returns the item's key.
  - `KeyComparer` compares keys. Without a schema key it is reference
    equality, so a reader never has to choose the comparer.
  - `Equals` and `GetHashCode` compare items through `KeyOf` and
    `KeyComparer`, so the rule has one statement. Their results do not
    change.
  - Callers: `Table.CaptureSchema` calls `Install`. The `Slots` constructor
    and `Projection.AcceptHold` read `KeyComparer`. `Slots.IndexOf`,
    `SiblingOf`, `IsWaiting`, `SlotOf` and `Number`, `Projection.AcceptHold`,
    `Table.IsHeld` and `Hierarchy.Capture` call `KeyOf`. The three private
    `KeyOf` copies go.
- **`Body.Row.FindOwner(node)`**, unchanged. `CellsPanel.OnLoaded` calls it,
  and `CellsPanel.FindOwner` goes.
- **`Table.IsMouseOrPen(type)`**, internal and static in `Table/Input.cs`,
  beside `OwnsInput`, which the strip already calls the same way. Callers:
  `Strip.OnPointerPressed`, `OnTapped`, `OnDoubleTapped` and
  `Table.OnRowsPointerPressed`. `Strip.IsMouseOrPen` goes.
- **`Table.LeftStep`**, in `Table/Columns.cs` beside `MoveColumnBy`.
  Callers: `Strip.OnKeyDown` (Left and Right) and the menu's Move left and
  Move right. The menu's local function goes.
- **`Table.ApplyPressSelection(item, ctrl, shift)`**, in `Table/Input.cs`.
  `ApplyPress` and `DispatchClick` pass the gesture's fields;
  `SelectFromTap` passes its hit. `SelectFromTap`'s copy goes.
- **`EffectiveLayout.IndexOfVisible(column)`**, unchanged.
  `CellsPanel.SyncChildren` calls it. `CellsPanel.VisibleIndexOf` goes.
- **`CellsPanel.ShowItem(cell, item)`**, a private static overload beside
  `ShowItem(item)`. `ShowItem(item)` and `RefreshChild` call it.

## Consumers of the changed names

Searched `app`, `lib`, `engine` and `docs` (not `artifacts` or
`3rdParty`) for `ApplyPressSelection`, `IsMouseOrPen`, `LeftStep`, `KeyOf`,
`FindOwner`, `VisibleIndexOf`, `CellIndexOf`, `KeyComparer`, `KeySelector`,
`ItemIdentity`, `ShowItem` and `RefreshChild`. Every hit is in
`lib/TableView/src`. The table tests reach `Table` members by name only for
`ApplyPress`, `DispatchClick`, `SelectItem`, `SelectFromTap` and
`MoveColumnBy` (`FeatureProofTests.cs` 213, 395 and 1012,
`SelectionHarness.cs` 130 and 142, `SortingTests.cs` 66,
`ColumnMoveTests.cs` 487). None of their signatures changes. No app file,
capture journey or app check script names a table internal.

## Files the change may edit

`lib/TableView/src`: `SelectionState.cs`, `Table.cs`, `Hierarchy.cs`,
`Body/Slots.cs`, `Body/Projection.cs`, `Table/View.cs`, `Table/Holding.cs`,
`CellsPanel.cs`, `Header/Strip.cs`, `Header/Menu.cs`, `Table/Input.cs`,
`Table/Columns.cs`. `lib/TableView/docs/tableview-implementation.md` for
the owner table and the right-to-left note.

## Cost

Batch D: one build of the table tests and one run, shared with stage 16a.
Every test class loads a live table, so the identity and cells-panel
changes run in all of them, and the run is the whole suite without the
interactive category. About 2 hours of agent work, this gate included.
