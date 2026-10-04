# Historical TableView design

Archived 2026-10-03. This records construction alternatives and earlier evidence,
including proposals that differ from the current code. Follow the active
[contract](../tableview-contract.md) and
[implementation map](../tableview-implementation.md). Statements below about a
missing project or pending spike describe their original time, not current status.

- Original status: design for the version-one TableView specification
- Relationship to the specification: the specification defines observable
  behaviour and ownership. This document defines the WinUI 3 construction that
  produces it: control selection, visual tree, resolved geometry, selection
  ownership, theme resources, focus model, and automation structure.
- Authority: the specification wins. Where this document names a mechanism, the
  mechanism is replaceable; the observable result is not.
- Verification status: nothing here has been compiled or run. There is no C#
  project yet. Section 17 lists what must be proven before any of it is trusted,
  in the order it must be proven.

---

## 1. Control selection

WinUI 3 has no `DataGrid`. The table is a `ListView` for the body with a
separate header strip above it.

| Requirement | Chosen control | Rejected alternative and reason |
|---|---|---|
| Virtualized rows, container recycling, list automation | `ListView` + `ItemsStackPanel` | `ItemsRepeater`: no containers, no scrolling, no list automation. |
| Row container states — hover, selected, current, focus (§8) | default `ListViewItem` style and its `ListViewItemPresenter` chrome | A custom `ControlTemplate`: would have to reproduce every state and its High Contrast behaviour. |
| Selection semantics (§13) | **the table owns them** — see section 5 | `ListView.SelectionMode`: its anchor is unreachable and its reset reconciliation is positional. It cannot satisfy §5.3. |
| Header menu (§12) | `MenuFlyout`, `MenuFlyoutSubItem`, `ToggleMenuFlyoutItem` | A custom popup: loses native placement, focus restore, keyboard behaviour. |
| Column resize (§10) | own resize grip in the header cell | Toolkit `GridSplitter`: §20 forbids a runtime dependency beyond WinUI 3, and it is built for `Grid` star columns. |
| Sorted view (§9) | own resolved view over the source snapshot | Toolkit `AdvancedCollectionView`: same dependency rule, and it owns sorting policy §3 assigns to `TableView`. |

`ItemsView` is the newer collection control and does expose `CurrentItemIndex`,
which `ListView` lacks. It still has no anchor, so it does not remove the work in
section 5, and its automation surface is not the one §13 describes. `ListView`
stays.

---

## 2. Visual tree

```text
TableView : Control
+- Grid                                   PART_Root
   +- Row 0 (Auto)  header
   |  +- TableHeaderStrip : Control       PART_HeaderStrip
   |     TabNavigation = Once
   |     +- Border (Clip set from ActualSize)
   |        +- TableHeaderPanel : Panel   PART_HeaderPanel
   |           +- TableHeaderCell : Control   (one per visible column)
   |           |  +- ContentPresenter          header content
   |           |  +- FontIcon                  sort glyph
   |           |  +- ColumnResizeGrip : Control
   |           +- Path                    PART_ColumnInsertionMarker
   |
   +- Row 1 (*)  body
   |  +- Grid
   |     +- ListView                      PART_ItemsView
   |     |  SelectionMode      = see section 5.4
   |     |  ItemsPanel         = ItemsStackPanel
   |     |  ItemTemplate root  = TableRowPresenter : Panel
   |     |  ItemContainerStyle overrides Padding / HorizontalContentAlignment
   |     |  ScrollViewer.HorizontalScrollMode = Disabled
   |     +- Canvas                        PART_OverlayLayer
   |     |  IsHitTestVisible = False
   |     |  AutomationProperties.AccessibilityView = Raw
   |     |  +- Rectangle                   PART_MarqueeRect
   |     |  +- Path                        PART_RowInsertionMarker
   |     +- ContentPresenter              PART_StateLayer
   |
   +- Row 2 (Auto)
      +- ScrollBar                        PART_HorizontalScrollBar
         Orientation = Horizontal
```

Four rules this tree exists to satisfy:

1. **One vertical scroll owner (§8).** The `ListView`'s inner `ScrollViewer` is
   the only vertical scroller. Nothing wraps the `ListView`.
2. **The header does not scroll vertically (§8).** It is a sibling in a
   different `Grid` row.
3. **The table owns the horizontal axis (§8).** Section 3.
4. **Overlays work in viewport coordinates.** `PART_OverlayLayer` and
   `PART_StateLayer` sit in the body cell above the `ListView`, outside the
   scroller.

---

## 3. Horizontal offset and header alignment

**The table owns horizontal scrolling. The `ListView`'s inner `ScrollViewer`
does not.** `ScrollViewer.HorizontalScrollMode` is `Disabled` on the `ListView`.

A `HorizontalOffset` value lives on the resolved layout (section 4). A
`ScrollBar` in `PART_HorizontalScrollBar` writes it. `TableHeaderPanel` and every
realized `TableRowPresenter` subtract it in `ArrangeOverride`. Header and rows
therefore move together by construction — there is nothing to synchronise.

The table also handles Shift+wheel and horizontal-wheel input, and repositions
the marquee rectangle when the offset changes.

**Why not the obvious mechanism.** The earlier design put the horizontal axis on
the `ListView`'s inner `ScrollViewer` and drove the header from
`ElementCompositionPreview.GetScrollViewerManipulationPropertySet` with an
`ExpressionAnimation`. Two problems killed it:

- microsoft-ui-xaml **#9860**, open since 2024-07-30 with no root cause and no
  fix: `ItemsStackPanel` + `ListView` lays out items incorrectly with horizontal
  scroll mode enabled. Its stated trigger is a `TextBlock` whose desired width
  exceeds its set width — the normal state of a truncated name cell.
- The one shipping WinUI 3 table control sets `HorizontalScrollMode="Disabled"`
  in its own template and owns the offset itself. Its author filed #9860.

§8 requires the header to be *"horizontally synchronized with the body"* and
never names a scroll surface as the mechanism, so this costs the specification
nothing. It costs more code than the composition animation and removes the
dependency on an open platform bug.

**Extent.** `TableHeaderPanel` and `TableRowPresenter` both measure to the sum of
resolved visible column widths. Equal extents keep header and rows aligned
without a second layout pass. Space right of the last column stays table
surface (§10); no column absorbs it.

**Panel choice.** On #9860 the WinUI team proposed `VirtualizingStackPanel` in
place of `ItemsStackPanel`. It is UI-virtualizing, so it is not the
non-virtualizing trade-off it sounds like.

Read the thread carefully before relying on either panel:

- The reporter confirmed VSP resolves the layout defect, then added that it
  "causing problems with the horizontal scrolling of the header in my custom
  control" — **and immediately qualified that**: he had not tested it with
  `ListView`, and the header problem might be specific to his control. That is
  one untested 2024 observation, not a general finding. Do not treat VSP as
  known-bad.
- Later in the same thread he reported the defect "isn't just with horizontal
  layout, it is also unable to calculate vertical size." That widens the blast
  radius beyond the horizontal axis and is the comment most relevant here.

With the horizontal axis off the `ScrollViewer` the bug should not arise at all,
so `ItemsStackPanel` is the default and `VirtualizingStackPanel` is the fallback.
The spike settles it; neither panel is ruled out in advance.

---

## 4. Resolved layout — the one geometry source

One object holds the effective layout: ordered visible column IDs, resolved width
per column, cumulative x-offset per column, total width, the horizontal offset,
the sort column, and the sort direction.

`TableHeaderPanel` and every realized `TableRowPresenter` read it. Nothing else
computes column geometry.

A column move, resize, fit, visibility change, or horizontal scroll:

1. updates the resolved layout;
2. raises one internal `LayoutInvalidated`;
3. each subscriber calls `InvalidateArrange` (widths or offset changed) or
   `InvalidateMeasure` (total width changed).

Only the header and *realized* rows subscribe. That is how §20's "column layout
changes affect only headers and realized rows" is met structurally.

### 4.1 Row content realization

`TableRowPresenter` is a `Panel`, not a `Grid`.

- One `ContentPresenter` per visible column.
- Each gets `Content` = the **row item** and `ContentTemplate` = the column's
  `CellTemplate`. This is §3's "direct row binding": no per-cell wrapper exists.
- `MeasureOverride` measures each child at its resolved width and infinite
  height; `ArrangeOverride` places it at its cumulative x-offset minus the
  horizontal offset.
- A resize or a horizontal scroll is an arrange pass over realized rows only.

A `Grid` per row would re-run column resolution for every realized row on every
layout change, and need its `ColumnDefinitions` rebuilt on every visibility
change. The panel does neither. `CellHorizontalAlignment` (§6) applies to the
`ContentPresenter`.

### 4.2 Reconciling the private view

**The list is told about the rows it holds a container for. Every other
position of the private view changes without a notification.** §5.3 states
the contract; this is the mechanism and what settled it.

**Why it is safe.** In the framework's source, `dxaml/xcp/dxaml/lib` on the
`main` branch of microsoft-ui-xaml: `ItemCollection_Partial.cpp` forwards
`GetAt` and `IndexOf` to the source on every call and keeps no copy;
`ItemsControl_Partial.cpp` hands index lookups and every collection change to
the modern panel's own mapping; `ModernCollectionBasePanel_Partial.h` keeps a
run of valid containers, a registry of pinned containers and a recycle queue,
and nothing per position for a row without a container. The list holds a
count and reads the row when it realizes the position. Measured by diagnostics
section R: after a full reversal, after scrolls to rows 700, 1,400 and 1,990,
after `ScrollIntoView` of a far row, after two reconciles in one callback, and
under fifteen seconds of the 1 Hz publish sorted by speed, every container
showed the view's row at its index.

**The realized set.** Every child of the items panel that names an index and
is handed back for that index, read before the first notification and shifted
by the table as it raises. Not `FirstCacheIndex` to `LastCacheIndex`: a pinned
container, the focused row's above all, lives outside that range, and so does
a container the panel has not recycled yet. The test is generous on purpose.
A leftover costs a few notifications; a row changed quietly under a container
that still answers for its index is the one failure this design must not have.

**Four passes, in this order.** Membership, raised at true indices, because
the count is the one thing the list keeps for a row it has not realized and
raising it where it happens leaves the panel's anchoring alone. Then every row
leaving a realized run, highest index first. Then every position outside the
runs, quiet. Then what each run is missing, ascending. Removing before placing
and placing before inserting is what keeps a row from being in the view twice
at any moment. Survivors are found per run: a survivor set over the whole view
puts the one row a reversal keeps under a container the list has at another
index.

**A membership-only update is unaffected by construction.** The host filters
an already-ordered source, so after the first pass the view is already the
snapshot, every held row is a survivor, and the later passes raise nothing.

**No forced layout anywhere.** The panel updates its container map inside the
notification: a container at index 697 answered 696 immediately after the row
at index 0 was removed, before any layout ran (section R, case 0). So two
reconciles in one callback read a current realized set, and the race a
deferred map would have opened does not exist.

**What it bought.** Measured once on the torrent host's own diagnostics
harness, section K, in a Release build, before that harness was deleted: a
full reversal of 2,002 rows raised 34 notifications where it had raised about
4,000. Specification 20 records the same notification pair and says not to
quote it as current, and neither number can be reproduced now that the
harness is gone. The section K citation was never a claim about anything
built here either — this document's own header already says nothing in it
has been compiled or run. What remains of a sort's cost is preparing the
visible containers, which every design pays; that is a cell template cost,
measured by section L.

---

## 5. Selection ownership

**`TableView` owns selection. `ListView` supplies containers and chrome.** This
is the largest structural decision in the design and it is forced, not chosen.

### 5.1 Why `ListView` cannot do it

§5.3 requires that on every source snapshot the table reconcile four things by
stable string key: selected items, current item, selection **anchor**, and
logical **focus**. `ListView` defeats all four.

- **The anchor is unreachable.** `ListViewBase_Partial.h` declares
  `UINT* m_pAnchorIndex` in a `private:` section with `SetAnchorIndex` and
  `ReleaseAnchorIndex` and no getter. None of it is WinRT-projected — the
  projected members are the `*Impl` ones and there is no anchor `*Impl`. A C#
  subclass cannot read, set, or observe it.
- **Reset reconciles positionally.** In `Selector::NotifyOfSourceChanged`, the
  `CollectionChange_Reset` branch reads the new item at each selected *index*,
  compares with `PropertyValue::AreEqual`, and unselects when they differ. A
  rehydrated instance at the same key but a different index loses selection.
  That is precisely what §5.3 forbids.
- **Focus is dropped on reset.** The same branch calls `SetFocusedIndex(-1)`.
- **There is no current item.** `ListView` exposes no current-item concept
  distinct from selection. (`ItemsView` has `CurrentItemIndex`; `ListView` does
  not.)

The one public anchor API — `SelectionModel` with `AnchorIndex`,
`SetAnchorIndex`, `SelectRangeFromAnchor` — is marked `[Experimental]` in the
Windows App SDK, and `ListViewBase` neither consumes nor exposes it.

Every shipping table control reaches the same conclusion. WinUI.TableView
derives from `ListView` and still keeps its own `SelectionStartRowIndex`; the
Toolkit DataGrid keeps its own `AnchorSlot`.

### 5.2 What the table owns

A selection model holding, by key where a key selector is configured and by
object reference otherwise:

- the selected set, in current visual order;
- the current item, which may be unselected (§13);
- the range anchor;
- the logical focus item.

All four reconcile against each new source snapshot in one atomic step, pruning
removed and non-interactive items, and raising **at most one**
`SelectionStateChanged` (§5.3).

### 5.3 Input semantics

§13 asks for *native extended-list behaviour*. That is an observable contract —
plain click selects one and sets the anchor, Ctrl-click toggles, Shift-click
takes the inclusive range from the anchor, Ctrl+Shift-click adds it — not a
requirement to use the platform's implementation, which is positional and
therefore wrong here. The table implements those semantics against its own model
and applies the four `SelectionMode` limits from §13 in `SetSelection`.

Keyboard: Up/Down, Shift+Up/Down, Home/End, Shift+Home/End, Page Up/Page Down,
Ctrl+A and Enter are handled against the same model. Scroll-into-view uses
`ListViewBase.ScrollIntoView`.

### 5.4 How the container reflects it

Open question, and the first thing the spike must settle: whether
`ListViewItem` still renders its selected visual state when the host `ListView`
has `SelectionMode="None"`.

- If it does: `SelectionMode="None"` and the table sets
  `ListViewItem.IsSelected` on realized containers from its own model. Cleanest —
  `ListView` runs no selection logic of its own, so no second anchor exists.
- If it does not: `SelectionMode="Multiple"`, whose click behaviour is a plain
  toggle and which therefore uses no anchor, with the table intercepting input
  and driving `SelectRange`/`DeselectRange`. Native visuals and native
  `IsSelected` automation are retained and there is still only one anchor.

Do not use `Extended`. It is the one mode that maintains the unreachable anchor,
which would put a second, invisible anchor underneath the table's own.

---

## 6. Header strip

### 6.1 Header cell

`TableHeaderCell` is a `Control` with a lightweight style, not a retemplated
`Button`:

- a `ContentPresenter` for `Header`/`HeaderTemplate`, or a single-line
  `TextBlock` with `TextTrimming="CharacterEllipsis"` bound to `DisplayName`
  when the host supplied neither (§8);
- a sort `FontIcon`, collapsed unless this column is the sort column;
- a `ColumnResizeGrip` on the trailing edge when `CanResize` is true.

The generated `TextBlock` carries `ToolTipService.ToolTip` = the full
`DisplayName`, so a trimmed label stays readable (§8).

### 6.2 Sort indicator

`FontIcon` with `FontFamily="{ThemeResource SymbolThemeFontFamily}"`:

| State | Glyph | Meaning |
|---|---|---|
| Ascending | `&#xE70E;` | chevron up |
| Descending | `&#xE70D;` | chevron down |
| Natural order | — | collapsed |

Direction is never colour alone (§19): the glyph is the visual cue, section 13.3
adds the automation string.

### 6.3 Resize grip

A `Control` subclass is required, because `UIElement.ProtectedCursor` is
protected and cannot be set from outside the element:

```csharp
internal sealed partial class ColumnResizeGrip : Control
{
    public ColumnResizeGrip() =>
        ProtectedCursor = InputSystemCursor.Create(
            InputSystemCursorShape.SizeWestEast);
}
```

- Hit width about 8 epx centred on the boundary; the visible line is narrower.
- `PointerPressed` captures; `PointerMoved` updates the resolved width, clamped
  to `[MinWidth, MaxWidth]` only (§10).
- `Escape` restores the width captured at press.
- `DoubleTapped` fits that column.
- One coalesced `LayoutChanged` on release, never per pointer move (§10, §20).

Touch gets no direct resize recogniser (§10); the header menu's fit, narrow and
widen commands are the touch and keyboard path.

### 6.4 Column drag

Threshold, feedback and drop follow §11. `PART_ColumnInsertionMarker` is a `Path`
inside `TableHeaderPanel`, so it moves with the header content for free.

---

## 7. Row container

No `ListViewItem` subclass and no retemplating. The default container chrome
already expresses hover, selected, current, focus and disabled states in every
theme including High Contrast, and §8 says to let it.

`ItemContainerStyle` sets only what shared column geometry needs:

```xml
<Style x:Key="TableRowContainerStyle" TargetType="ListViewItem"
       BasedOn="{StaticResource DefaultListViewItemStyle}">
    <Setter Property="HorizontalAlignment" Value="Left" />
    <Setter Property="HorizontalContentAlignment" Value="Stretch" />
    <Setter Property="Padding" Value="0" />
</Style>
```

`Padding="0"` moves cell padding into cell templates where the host owns it.
`HorizontalContentAlignment="Stretch"` lets the row presenter reach the resolved
total width. `HorizontalAlignment="Left"` stops the container reaching further
than that: a row is as wide as its columns, so the space to the right of the
last column belongs to no row. §14 starts its marquee from that space and §19
requires the row's fill to stop there, so a stretched container removes the
gesture rather than merely widening the paint — with rows filling the viewport
there is then no empty surface anywhere on screen and every press becomes a row
drag.

The table sets no row height. Uniform height is a consequence of uniform cell
templates; imposing one would be TableView-specific spacing, which §19 forbids.

### 7.1 Non-interactive rows

`CanInteractWithItem` returning false sets `IsEnabled = false` on the container.
The row still renders, the automation peer reports `IsEnabled = false`, and the
table's own selection and keyboard logic skips it.

The consequence, stated because it is a decision and not a caveat: disabling the
container also disables interactive controls inside its cells. That matches the
reference profile, where ghost rows are display-only (§A.2.1).

---

## 8. Cells

Cell templates are host content and receive the row item as `Content` (§7).
Three rules for hosts:

- do not paint a row backdrop in a cell — it competes with the container's
  selection and hover states;
- declare a `MinWidth` on any column whose cell holds a control or a value that
  must stay usable; the table will not widen a column to keep it reachable (§8);
- set `TableView.SuppressRowGestures="True"` on a custom interactive control the
  table cannot recognise (§7).

`x:Bind` in a cell template defaults to `OneTime`. Every live value needs
`Mode=OneWay`.

---

## 9. Overlays and state presentation

### 9.1 Contrast is a measured result, and the platform accent brushes fail it

In WinUI's HighContrast dictionary, `AccentFillColorDefaultBrush` and
`AccentFillColorSelectedTextBackgroundBrush` both resolve to
`SystemColorWindowColor` — which is also the unselected `ListViewItem`
background. Any marker drawn in them measures **1.00:1** in every contrast theme.
An earlier version of this document used them and claimed they were correct in
Contrast themes. They are not.

The platform ships the role under a different name. `FocusStrokeColorOuterBrush`
and `FocusStrokeColorInnerBrush` are global keys whose HighContrast entries are
`SystemColorWindowTextColor` and `SystemColorWindowColor`. Drawn as a two-tone
marker — an outer core with an inner casing, the platform's own focus-rectangle
idiom — the core reads over an unselected row and the casing reads over a hovered
or selected row. Both clear 3:1 in all four shipped contrast themes. No new
resource, restriction intact.

### 9.2 Insertion markers

`PART_ColumnInsertionMarker` and `PART_RowInsertionMarker` are two-tone: a line
in `FocusStrokeColorOuterBrush` with a casing in `FocusStrokeColorInnerBrush`,
plus a cap at the leading end. The cap is the positional cue §19 requires so the
destination is not conveyed by colour alone.

### 9.3 Marquee

`PART_MarqueeRect` in `PART_OverlayLayer`, in viewport coordinates, because the
gesture and its auto-scroll are viewport gestures.

**A faint neutral surface, and no line.** §14 mandates an overlay rectangle.
Four versions were seen and ruled on by the owner: a stroke-only rectangle read
as a bare outline over a dense table; the two-tone stroke read as a white box in
Dark, where the pair's core is white; a one-pixel accent line around a faint
fill — the platform's own selection rectangle, which Explorer and the desktop
draw — still read as a border he did not want; and an accent surface alone left
him unsure the colour was right. It was not, for this table. WinUI gives a
selected row the neutral subtle fill and puts the accent only in the selection
pill, which this table does not draw, so nothing in its selection is
accent-coloured and the accent rectangle was the one piece of the selection
language in a different colour. Explorer's rubber band is accent because
Explorer's selected items are. The rectangle is now `TextFillColorPrimaryBrush`
at element `Opacity` 0.14. A neutral highlight is a light wash of the foreground
colour, and the primary text brush is the one platform brush that carries the
foreground colour in every theme: 89% black in Light, white in Dark and the
window text colour in a contrast theme, so the wash is 12% to 14% in all three
and the rectangle is the same kind of wash a selected row is, a little stronger
than one.

No platform brush is translucent in a contrast theme, where every fill resolves
to an opaque system colour, so the opacity is on the element: legal, since §19
bans *resources*, not element properties, and WinUI's own HighContrast
dictionary retains `ListViewItemDragThemeOpacity`. That is also why the
platform's list-highlight brush was tried and rejected: it carries its own 20%
in Light and Dark but resolves to the opaque `SystemColorHighlightColor` in a
contrast theme, so the 0.7 that gave 14% in Light and Dark gave a 70% wash
there, and the specs review computed the covered rows' text at 2.1:1 to 2.5:1.
The other neutral fill brushes have opaque face and window colours as their
contrast-theme entries, which hide the rectangle or wash the text under it
(section 9.1). What remains in a contrast theme is that over the rows it has
selected the rectangle is hard to tell from them, the highlight being what a
selected row is there; it shows on its own where it overhangs them, beside and
below. That is kept as a property rather than a defect: a stroke's job was to
mark the boundary and it vanished exactly there, while the fill's job is to
show the area, and in a contrast theme the area then is the selection it is
making.

Computed rather than rendered: the wash over a row is about `#3F3F3F` in Dark,
on which white text measures 10.5:1, and about `#D5D5D5` in Light, on which the
primary text brush measures 12.3:1. In the two shipped contrast themes the specs
review measured, with the wash over the text as well as the background because
the overlay sits above the rows, the covered rows' text keeps 15.6:1 unselected
and 11.0:1 selected in HC Black, and 15.2:1 and 11.2:1 in HC White. Name the
brush, never its colour key: the contrast dictionary's `TextFillColorPrimary`
colour is a red placeholder, and only the brush points at the window text
colour. The rectangle is not the cue that
carries §19's 3:1: the rows it covers are selected as it covers them, and that
state is the feedback. The insertion markers keep the two-tone pair: a marker is
a short thick line, not a box, and the white core is what makes it read over
any cell.

The layer is `IsHitTestVisible="False"` with
`AutomationProperties.AccessibilityView="Raw"`, so it neither steals cell input
nor enters the automation tree (§14, §19).

### 9.4 Loading, empty, no results

`PART_StateLayer` is a `ContentPresenter` in the body cell. One of four
presentations is visible, in §17's precedence:

| Condition | Content |
|---|---|
| resolved view has items | collapsed — rows show |
| view empty and `IsLoading` | `LoadingContent` |
| view empty, `EmptyState == Empty` | `EmptyContent` |
| view empty, `EmptyState == NoResults` | `NoResultsContent` |

Rows stay visible during a refresh because the layer binds to the *resolved view*
being empty, not to `IsLoading`. The host is the only owner of which empty state
applies — the component no longer decides it, because it no longer filters.

---

## 10. Header context menu

A `MenuFlyout` built on demand (§12):

```text
Hide this column                 MenuFlyoutItem        (hideable, >1 visible)
Columns                        > MenuFlyoutSubItem
    <DisplayName>                ToggleMenuFlyoutItem  (one per column)
----------------------------
Fit this column                  MenuFlyoutItem        (resizable active column)
Fit visible columns              MenuFlyoutItem
----------------------------
Move left                        MenuFlyoutItem
Move right                       MenuFlyoutItem
```

- Column names come from the host's localized `DisplayName`.
- Action labels come from **the control's own** resources (§12), read through
  `ResourceLoader.GetForViewIndependentUse("<LibraryName>/Resources")`.
- A command that cannot change layout is disabled, not hidden.
- The last visible column's toggle is disabled, so zero visible columns is
  unreachable.
- The menu offers no per-step width command. An earlier version carried Narrow
  this column and Widen this column, each stepping 8 DIPs. A menu flyout closes
  on every invocation and WinUI offers no way to hold one open for a command, so
  a step cost a full reopen: widening the torrent host's 150 DIP name column to
  something readable took thirteen right-clicks and thirteen clicks. That count
  came from the torrent host's own fitted width, and the harness that produced
  it has been deleted, so the thirteen cannot be reproduced as written; what
  survives it is the arithmetic, one invocation per 8 DIPs however far a column
  has to go. Fit this column reaches the outcome from the keyboard in one
  invocation. Continuous keyboard resizing, if it is ever wanted, belongs on the
  focused header as a held key where auto-repeat does the work.

Right-click on empty header space opens the same flyout without the
active-column items. `Menu` and `Shift+F10` on a focused header open it there and
leave row selection unchanged.

---

## 11. Focus and keyboard model

Three regions in page tab order:

```text
[ header strip ]  ->  [ row surface ]  ->  next page element
```

**Header strip is one tab stop.** `TabNavigation="Once"` on `PART_HeaderStrip` is
the platform mechanism for §13's composite control region. Header cells set
`IsTabStop="False"`; the strip tracks an active header index and moves focus on
Left/Right/Home/End. Enter and Space follow §9's sort cycle when the column is
sortable; Menu and Shift+F10 open the header menu.

**Row surface.** The `ListView` supplies the focusable container sequence and
scroll-into-view. Arrow-key and Ctrl+A behaviour are handled by the table
against its own selection model (section 5.3), not by `ListView`.

Interactive descendants of a header or cell keep their own tab and input
behaviour and do not join the composite strip (§13). Decorative elements and the
overlay layer are not tab stops.

---

## 12. Theme resources

Every brush is a platform brush. The control defines no brush, type, geometry,
spacing or token resource (§8, §19).

| Surface | Resource |
|---|---|
| Header strip background | `LayerFillColorDefaultBrush` |
| Header/body divider | `DividerStrokeColorDefaultBrush` |
| Header label | `TextFillColorSecondaryBrush` |
| Active sort label and glyph | `TextFillColorPrimaryBrush` |
| Resize grip line | `DividerStrokeColorDefaultBrush` |
| Insertion marker core | `FocusStrokeColorOuterBrush` |
| Insertion marker casing | `FocusStrokeColorInnerBrush` |
| Marquee | `TextFillColorPrimaryBrush` at element opacity 0.14, no line |
| Row hover / selected / current / focus | container default — set nothing |
| Non-interactive row text | container disabled state — set nothing |
| Sort glyph font | `SymbolThemeFontFamily` |

No `ThemeDictionaries` block. Nothing is defined per theme, so nothing can be
wrong in High Contrast.

### 12.1 Token rules

The table above is a map of *usage* — which platform token each surface reads.
It is not a set of tokens the control owns. The control owns none.

These rules are what stop a small set becoming a large one. The application this
replaces reached over a thousand tokens, and every visual change then had to be
made in many places at once.

**1. Never name a token after an element.** `TableHeaderBackgroundBrush`,
`TableRowSelectedBrush` and `GripStrokeBrush` are all forbidden. An
element-named token has exactly one caller by construction, so it can only ever
be tuned alone — which is how a set grows without limit and drifts out of
agreement with itself.

**2. Name by semantic role, never by appearance.** The role is what the value
*means* — a divider, a raised layer, secondary text, a selection accent. Not its
hue, and not where it happens to be used. The three platform tokens in use
already follow this: `LayerFillColorDefaultBrush` is a layer, not a header;
`DividerStrokeColorDefaultBrush` is a divider, not a grip.

**3. A role that already exists is reused, not restated.** The resize grip and
the header/body divider are both dividers and both read the same token. Two
tokens holding the same value is two things to keep in agreement.

**4. If the control must ever introduce a key, it inherits rather than
redefines.** Alias it to the platform token carrying the nearest role:

```xml
<StaticResource x:Key="TableSelectionAccentBrush"
                ResourceKey="AccentFillColorDefaultBrush" />
```

This allocates nothing, follows the platform if the platform retunes, and keeps
Light, Dark and High Contrast correct with no per-theme entry. A literal
`<SolidColorBrush Color="#..."/>` is a defect: it is a value that must now be
maintained in three themes by hand.

**5. No platform role for what you need is a finding, not a licence.** Stop and
say so. Two cues are already in that position — see below.

### 12.2 The two cues the container cannot supply

Measured: the container's own selected-row cue is **1.08:1** in Light and
**1.18:1** in Dark against §19's 3:1 requirement, and none of its nine visual
states expresses a current row that is not selected. So the table draws both
itself, and these are the only two places it may.

Both are drawn by aliasing existing platform roles, under rule 4 — a selection
accent and the two-tone focus-stroke pair the insertion markers already use. No
new colour is defined, and the count of table-owned keys stays at two. If it
grows past that, treat it as a defect and escalate rather than adding a third.

---

## 13. UI Automation

### 13.1 Structure

`ListView` supplies `List` and `ListItem` automation natively. Keep it.

Do **not** implement `ITableProvider` or `IGridProvider`. §19 forbids claiming
the Table and Grid patterns without the complete header/cell relationships they
require, and version 1 does not supply them.

### 13.2 Names

- Control region: the **host** supplies `AutomationProperties.Name`. A nearby
  caption is not that relationship (§19).
- Header strip: a localized name from the control's own resources.
- Header cell: `AutomationProperties.Name` = the column's `DisplayName`.
- Overlay layer and decorative parts: `AccessibilityView="Raw"`.

### 13.3 Sort state

`AutomationProperties.ItemStatus` on the header cell, set to a localized string,
with a property-changed event raised on the peer when it changes. `ItemStatus` is
the property for transient element state and needs no Grid pattern.

### 13.4 Selection state

Because the table owns selection, it must ensure the container's automation
`IsSelected` still reflects the model. Section 5.4's open question decides how:
under `SelectionMode="None"` the peer may report nothing selected even when the
table considers the row selected, which would be an accessibility defect, not a
cosmetic one. This is part of the same spike.

---

## 14. Motion

All platform motion.

| Movement | Source |
|---|---|
| Row reposition after the host publishes a new order | `ListView` default reposition transitions |
| Column move settle | header panel arrange; no custom animation |
| Resize, drag and horizontal scroll feedback | tracks input directly, no easing |
| Cancellation | returns to the unchanged layout, no second animation |

No custom animation system and no host coordination protocol (§16, §19). When
the system disables animations, states and cues stay correct because none of
them depends on motion to be understood.

---

## 15. Reference host page — torrent list

Informative, matching Appendix A.

### 15.1 Allocation

First-run visible columns and declared widths (§A.1):

```text
name 150 + progress 220 + status 110 + queue 80
         + speed 180 + peers 88 + size 100      = 928 DIPs
```

- **928 DIPs** shows the first-run set fully.
- **About 980 DIPs** of table allocation adds gutters and the vertical scrollbar.
- All eleven columns at declared widths total **1338 DIPs**.
- Resized to declared minimums, the first-run set compresses to **599 DIPs**.

For a shell with a left navigation pane this puts the window in the multi-pane
range, roughly 1100–1300 DIPs wide by 720–840 tall, with the table getting at
least 980. Below that, horizontal scrolling keeps the columns rather than hiding
them (§8). The page owns the minimum window size; the table has no breakpoint
that drops columns.

Note `MaxWidth` defaults to `double.PositiveInfinity` (§6), so any user resize
can exceed any viewport at any window size. Horizontal scrolling is not only for
optional columns.

### 15.2 Page composition

A dense data surface, not a card stack:

```text
+---------------------------------------------------------+
| SelectorBar: All | Downloading | Seeding      [search]   |
+---------------------------------------------------------+
| TableView                                                |
+---------------------------------------------------------+
| docked detail / inspector (opened by ItemInvoked)        |
+---------------------------------------------------------+
```

- Three domain filters → `SelectorBar`, not hand-built pills.
- Search is a `TextBox` owned entirely by the page. **The component does no text
  matching** — the host filters before assigning `ItemsSource`.
- The table fills the remaining space. Do not put it in a card.

### 15.3 Binding

```xml
<controls:TableView
    x:Name="TorrentTable"
    AutomationProperties.Name="{x:Bind ViewModel.LocalizedTorrentTableLabel}"
    ItemsSource="{x:Bind ViewModel.FilteredTorrents, Mode=OneWay}"
    IsRowReorderingEnabled="{x:Bind ViewModel.CanReorderQueue, Mode=OneWay}"
    IsLoading="{x:Bind ViewModel.IsLoading, Mode=OneWay}"
    EmptyState="{x:Bind ViewModel.EmptyState, Mode=OneWay}" />
```

`FilteredTorrents` is state-filtered **and** text-filtered by the host, in queue
order. `CanReorderQueue` is false whenever a text filter is active, per §A.2.5.

Every binding is `Mode=OneWay`. `x:Bind` defaults to `OneTime`; without the mode,
filters and loading state work once and then freeze.

### 15.4 Cell templates

Eleven typed `DataTemplate`s with `x:DataType="vm:TorrentRowViewModel"`.
Formatting — status text, speed strings, ETA, progress labels — lives on the row
view model (§A.2.2).

Format **lazily**, in property getters, raising `PropertyChanged` when the
underlying value changes. Only realized cells then read them. Formatting eagerly
on every snapshot allocates a string per field per row per tick for rows nobody
is looking at.

Rich cells declare their minimum: `progress` 110, `speed` 160, `status` 95,
`name` 90 (§A.1).

### 15.5 What the page keeps

The page owns truth: daemon snapshot, queue order, optimistic reorder, rollback,
command enablement, persistence, and all filtering. It renders only facts it can
prove, and renders *unknown* rather than an inference when it cannot. The table
renders and reports gestures. It calls nothing.

---

## 16. Design decisions

| # | Decision | Driven by |
|---|---|---|
| 1 | The table owns the horizontal offset; the inner `ScrollViewer`'s horizontal axis is disabled | §8 synced header, and open bug #9860 under the alternative |
| 2 | `TableRowPresenter` custom panel instead of a per-row `Grid` | §3 direct row binding, §20 layout changes touch realized rows only |
| 3 | **The table owns selection, current item, anchor and focus** | §5.3 keyed reconciliation, which `ListView` cannot satisfy |
| 4 | Do not use `SelectionMode="Extended"` | it is the one mode maintaining an unreachable second anchor |
| 5 | `IsEnabled = false` for non-interactive rows | §5 render-but-not-interact, delivered natively including automation |
| 6 | `ColumnResizeGrip : Control` for the resize cursor | `ProtectedCursor` is protected; it cannot be set externally |
| 7 | `TabNavigation="Once"` for the composite header region | §13 one tab stop for the header strip |
| 8 | `AutomationProperties.ItemStatus` for sort state | §19 forbids claiming the Grid and Table patterns |
| 9 | Two-tone `FocusStrokeColorOuter`/`Inner` for the insertion markers | the accent fill brushes measure 1.00:1 in contrast themes |
| 10 | Marquee: a faint neutral surface, a 14% wash of the primary text brush at element opacity, and no line | the owner ruled out a stroke-only rectangle, the two-tone stroke, the platform's one-pixel accent line and an accent surface in turn; this table's selection is neutral, the accent pill being gone, so the rectangle is neutral to look like the selection it makes; no platform brush is translucent in a contrast theme, §19 bans resources, not element properties, and the text brush is the one brush that is the foreground colour in every theme, where the list-highlight brush became an opaque 70% wash (section 9.3) |
| 11 | Generated menu labels from the control's own `.resw` | §12 action labels are not host-overridable |
| 12 | Fit measures realized containers only | §10 no off-screen realization, §20 no hidden measurement surface |
| 13 | No `ThemeDictionaries` at all | §19 no control-specific resources |
| 14 | No dependency beyond the Windows App SDK | §20, and revisit only when a project exists to produce the numbers a package must justify: executable size, memory footprint, runtime cost |
| 15 | The list is told only about the rows it holds a container for; every other position of the private view changes quietly | §5.3 and §20: notifications bounded by realized containers, because the list keeps nothing per unrealized position (section 4.2); chosen over a windowed collection, a custom panel, ItemsRepeater and a forked runtime |
| 16 | The table withholds the drag under any sort but the `DefinesRowOrder` column's, offers it under that column either way, and reports a descending view's request in row order; a row it withholds the drag from starts the marquee | §14 and §16: only the table sees both the sort and the boundary, so the host keeps `IsRowReorderingEnabled` for what only it can see, its filter and its pending operations; and a press nothing competes for must not be a dead press |
| 17 | Hiding the sorted column clears the sort, and a restored layout ignores a sort on a hidden column | §9 and §18: the header is the only surface that shows or changes the sort, and under decision 16 a sort nobody can see would withhold the drag with nothing on screen to explain it |
| 18 | The item panel keeps its scroll offset across updates (`ItemsUpdatingScrollMode="KeepScrollOffset"`) | §14: the marquee's corner is a point in the scrolled content, and the default mode moved that content to follow the first visible row on every settle of a live sort, so the corner jumped with the row; under a live sort the viewport holds still and rows move through it |

---

## 17. Unverified — spike before trusting, in this order

Nothing here compiles. These need a running WinUI 3 project.

1. **Selected visual state under `SelectionMode="None"`.** Does `ListViewItem`
   still draw its selected chrome, and does its automation peer still report
   `IsSelected`, when the host `ListView` has selection off? This decides
   section 5.4 and it decides an accessibility contract, so it is first.
2. **Full-width rows.** Does a row presenter measuring wider than the viewport
   arrange correctly with the inner `ScrollViewer`'s horizontal axis disabled,
   with the table applying the offset itself?
3. **Virtualization.** Does `ItemsStackPanel` recycle normally under that
   arrangement? If not, try `VirtualizingStackPanel` (section 3).
4. **`Padding="0"` on the container.** Does the default `ListViewItem` style
   still draw selection, hover and focus chrome at zero padding?
5. **Horizontal input.** Shift+wheel and horizontal-wheel reaching the table's
   own offset rather than being swallowed by the `ListView`.
6. **Contrast measurement (§19).** Measured, not computed, in Light, Dark and a
   contrast theme: the sort glyph on the header background; header label active
   and inactive; the two-tone insertion marker over a row, selected and
   unselected; the marquee stroke over both; disabled row text on the container's
   disabled background.
7. **Marquee legibility.** Taken: stroke-only did not read well enough over a
   dense table, and the rectangle now carries a fill (section 9.3).
8. **Narrator.** Header strip reads as one region; each header reads its name and
   sort status; selection state is announced correctly given section 5; the
   overlay layer is absent from the tree.

---

## 18. Design review checklist

- [ ] Header and rows derive geometry from one resolved layout, including the horizontal offset
- [ ] The `ListView`'s inner `ScrollViewer` has horizontal scrolling disabled
- [ ] Nothing wraps the `ListView` in a `ScrollViewer`
- [ ] Header is a sibling of the `ListView`, not `ListView.Header`
- [ ] `SelectionMode` is never `Extended`
- [ ] Selected set, current item, anchor and focus reconcile by key, in one atomic step, raising at most one `SelectionStateChanged`
- [ ] Every `x:Bind` for a live value carries `Mode=OneWay`
- [ ] No hard-coded colour anywhere; no `ThemeDictionaries`
- [ ] No accent brush used for a marker or the marquee; the marquee is a wash of the primary text brush at element opacity, with no line
- [ ] Row container chrome is default; no `ControlTemplate` for `ListViewItem`
- [ ] Cell templates paint no row backdrop, and format lazily
- [ ] Overlay layer is `IsHitTestVisible="False"` and `AccessibilityView="Raw"`
- [ ] Sort, drag destination and selection are legible without colour
- [ ] Header strip is one tab stop; header cells are not tab stops
- [ ] No `ITableProvider` / `IGridProvider` implementation
- [ ] No package reference beyond the Windows App SDK
- [ ] Rich columns declare a real `MinWidth`
- [ ] `LayoutChanged` is coalesced per gesture, not per pointer move
- [ ] The component contains no domain type, command, or service reference
