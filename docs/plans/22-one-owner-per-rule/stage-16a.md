# Stage 16a gate record: the table tests use their harness

Gate done on 12 November 2026, on commit a4312a7. Paths are under
`lib/TableView/tests`; line numbers are those of a4312a7. Only test code
changes, and every test keeps what it asserts.

## History

`git log v0.9.1.2..HEAD -- lib/TableView/tests` shows no earlier attempt to
move these helpers into `ProofHarness.cs`, and none to split them out of it.

## Findings

Every finding is kept. Each states its rule in one sentence.

1. **`Descendants`** in `ColumnFitTests.cs` 913, `ColumnMenuTests.cs` 430,
   `ColumnMoveTests.cs` 450, `RealColumnDragTests.cs` 259 and
   `RealHeaderMenuTests.cs` 87: the elements of one type below a root, in
   depth-first order. `Proof.Descendants` walks in the same order. The gate
   found one more copy of the same tree walk: `SelectionHarness.Descendant`
   (176) is `Proof.Descendant` line for line, with 21 callers in 9 files.
2. **`Shot`** in `ColumnMoveTests.cs` 517 and `SortingTests.cs` 859: one
   rendered frame of the control, read as pixels and measured as a WCAG
   contrast ratio. The capture, the pixel read and the ratio are the same
   code. `SortingTests` also computes its own copy of `Shot.X` from the
   table's scale (`PixelX`, 772).
3. **The key-injection setup** in `RealHeaderMenuTests.cs` 110 and
   `RealKeyboardTests.cs` 145: injected input goes to the foreground
   window, so a test injects only after its own window holds the
   foreground, and is inconclusive otherwise. The gate found the same
   setup inline in `RealColumnDragTests.cs` 146-167 (`DragHarness`) and
   `RealPointerTests.cs` 257-277 (`Mouse`). The block below the setup in
   both cited files is a copy too: a key press with an optional modifier
   (`PressAsync`, `Info`). `DragHarness.KeyAsync` and `Mouse.Key` build the
   same key events by hand.

Seen and not taken, because the stage lists three helpers and these are
other rules or differ in behavior:

- **Reflection helpers.** `Invoke` and `Field` in `ColumnMoveTests.cs`
  504-514, `ColumnMenuTests.cs` 513-523, `ColumnResizeTests.cs` 307,
  `ColumnResetTests.cs` 209 and `RealColumnDragTests.cs` 278,
  `SelectionHarness.Invoke` (168), and inline `GetField("_panel")` in
  `RealHeaderMenuTests.cs` 81, `RealSortTests.cs` 150 and
  `SortingTests.cs` 1018 do what `Proof.Call` and `Proof.Field` do. They
  differ in reach (declared members only) and in failure (`!` instead of
  `MissingMethodException`), and they span ten files. They are their own
  item.
- **Pointer placement.** `DragHarness.InjectAsync` and `ScreenPoint` and
  `Mouse.MoveTo` and `ScreenPoint` both map a window point to an absolute
  desktop coordinate, but one rounds and the other truncates, and only
  `Mouse` asks for `MoveNoCoalesce`. Merging them changes where an
  interactive test's pointer lands. Not a helper the harness has.

## Questions answered from the code

1. **The order of the walk.** The lazy copies yield a match and then its
   subtree, child by child; `Proof.Descendants` adds a match and then walks
   it (`ProofHarness.cs` 130-143). The order is the same. A copy's
   `.First()` therefore returns what `Proof.Descendant` returns
   (`ProofHarness.cs` 102), which stops at that match. **Decision:** a call
   that takes the first match calls `Proof.Descendant`; a call that filters,
   counts or needs exactly one calls `Proof.Descendants`.
2. **The two `Shot` queries.** `ColumnMoveTests` measures the strongest
   contrast along one row between two x positions in DIPs; `SortingTests`
   measures it over a pixel rectangle that it converts from DIPs with the
   same rounding (`SortingTests.cs` 772-777, against `Shot.X` and `Shot.Y`).
   **Decision:** one `Contrast(reference, area)` over an area in DIPs; a row
   is an area of height 0. Four parameters were the limit, so the area is a
   `Rect`, as [naming](../../agents/naming.md#inputs-and-results) shows.
   `DrawnWidth` keeps its one-row form, because only `ColumnMoveTests` uses
   it and a count over an area is not a width.
3. **The empty-bitmap message.** "The header rendered" in `ColumnMoveTests`,
   "The control rendered" in `SortingTests`. Both render the whole table
   (`ColumnMoveTests.cs` 237), so the shared message says "control".
4. **The wait after a key press.** `RealHeaderMenuTests` waits 250 ms for
   the menu to open, `RealKeyboardTests` and `DragHarness` 120 ms, and
   `Mouse` waits in its own steps. **Decision:** the harness injects the
   press and each test keeps its own wait, so no test runs slower or with a
   shorter wait than today.
5. **The inconclusive message.** It said "real key messages", "real mouse
   messages" or "mouse messages". The shared one says "real input", because
   the setup serves keys and mouse alike.

## Owner and interface

`ProofHarness.cs` is the owner. It gains:

- `Proof.WindowHandle(element)`: the handle of the window that shows the
  element. Callers: `Proof.TakeForegroundAsync`, `DragHarness.LoadAsync`
  and `Mouse.CreateAsync`, which also need it for screen coordinates.
- `Proof.TakeForegroundAsync(element)`: brings the element's window to the
  foreground, is inconclusive when it cannot, and returns the injector. The
  name says it takes the foreground, because that changes what the person
  at the machine sees. Callers: `RealHeaderMenuTests`, `RealKeyboardTests`,
  `DragHarness.LoadAsync`, `Mouse.CreateAsync`. Their four copies and their
  `SetForegroundWindow` and `GetForegroundWindow` imports go.
- `Proof.Press(injector, key, modifier)`: one press of a key, inside a hold
  of the modifier when there is one. Callers: the `PressAsync` of
  `RealHeaderMenuTests` and `RealKeyboardTests`, which keep their waits, and
  `DragHarness.KeyAsync`. Both `Info` copies go.
- `Proof.Key(key, up)`: one injected key transition. Callers: `Proof.Press`
  and `Mouse.Key`, which holds a modifier across a mouse press.
- `Shot`, a type beside `Proof`: `TakeAsync(element)`, `X(dips)`,
  `Y(dips)`, `At(x, y)`, `Contrast(reference, area)` and
  `DrawnWidth(background, from, to, at)`. Callers: the marker test in
  `ColumnMoveTests` and the glyph test in `SortingTests`. Both nested copies
  and `SortingTests`' `PixelX` go.

`Proof.Descendant` and `Proof.Descendants` are unchanged. The five
`Descendants` copies and `SelectionHarness.Descendant` go, and their
callers call `Proof`.

## Consumers of the changed names

Searched `lib/TableView` and `app` for `Descendants<`, `Descendant<`,
`Shot`, `FocusWindowAndCreateInjectorAsync`, `PressAsync`, `Info(`,
`SetForegroundWindow` and `GetForegroundWindow`. Every consumer is in
`lib/TableView/tests`. The app and the sample have their own helpers with
some of these names, in their own assemblies; the test helpers are internal
to the test project, so nothing outside it calls them, and the probe pages
stay as the owner decided.
`SelectionHarness.Descendant` has 21 callers in `ColumnMoveTests`,
`ColumnResizeTests`, `HierarchyTests`, `KeyboardAndGestureTests`,
`RealKeyboardTests`, `RealSortTests`, `SortingTests` and `SelectionHarness`
itself.

## Files the change may edit

`ProofHarness.cs`, `SelectionHarness.cs`, `ColumnFitTests.cs`,
`ColumnMenuTests.cs`, `ColumnMoveTests.cs`, `ColumnResizeTests.cs`,
`HierarchyTests.cs`, `KeyboardAndGestureTests.cs`, `RealColumnDragTests.cs`,
`RealHeaderMenuTests.cs`, `RealKeyboardTests.cs`, `RealPointerTests.cs`,
`RealSortTests.cs`, `SortingTests.cs`.

## Cost

Batch D, shared with stage 11: one build of the table tests and one run.
The interactive classes this stage edits (`RealColumnDragTests`,
`RealColumnResizeTests` and `RealSortTests` through `DragHarness`,
`RealHeaderMenuTests`, `RealKeyboardTests`, `RealPointerTests`) compile in
that build but run only when the interactive category is asked for. About
1.5 hours of agent work, this gate included.
