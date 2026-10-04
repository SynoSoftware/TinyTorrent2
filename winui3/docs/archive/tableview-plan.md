# Historical TableView implementation plan

Archived 2026-10-03. The library, sample, and tests now exist. This is the original
build sequence, not work to repeat. Use the active
[contract](../tableview-contract.md) and
[implementation map](../tableview-implementation.md). Paths, toolchain evidence,
and open questions below belong to the original plan.

- Current specification: [TableView contract](../tableview-contract.md)
- Historical design: [TableView design](tableview-design.md)
- Current glossary: [Table vocabulary](../../CONTEXT.md)

Paths in this document are relative to the repository root.

This plan says what gets built, in what order, and what proves each piece. It
contains no estimates. Where something is unknown, it says so.

---

## 1. Toolchain — done

Verified on this machine on 2026-09-02:

```
.NET SDK             10.0.400
Visual Studio 2026   present
WinApp CLI           0.6.1
WinUI 3 templates    winui, winui-lib, winui-mvvm, winui-navview,
                     winui-tabview, winui-unittest
Developer Mode       enabled
```

Nothing else is installed. The Windows App SDK arrives as a package reference
when the first project is created.

---

## 2. Solution shape

```
TableView.slnx
CONTEXT.md
docs/
  torrent-table-specs.md
  tableview-winui3-design.md
  tableview-implementation-plan.md
src/
  SynoSoftware.TableView/            class library      (winui-lib)
    Themes/Generic.xaml
    Strings/en-US/Resources.resw
samples/
  TableView.Sample/                  desktop app        (winui)
tests/
  TableView.Model.Tests/             unit tests         (winui-unittest)
```

Three projects, and each earns its place.

**The class library** is the boundary. The specification spends §2 defining what
the component must not know, and Appendix B item 14 requires that it contain no
domain type, command, or service reference. In a separate assembly that is
compiler-enforced rather than review-enforced. §12 also requires generated menu
labels to come from the control's own resources, which needs its own `.resw`.

**The sample** is the only place most of Appendix B can be verified. Sixteen of
its sixteen items are generic, and several cannot be checked against a torrent
table at all — the four selection modes, a non-torrent row type, a second column
profile. One page, one table, heavily instrumented. Section 5.

**The test project** covers the selection model and the resolved layout only —
the two pieces that are pure logic and carry the most risk. Everything else is
visual and belongs in the sample. This is not a general unit-testing programme.

No other projects. No abstractions shared between them beyond the library's
public surface.

---

## 3. Step zero — the spike

**Throwaway code, thrown away.** Not the sample, not the library. A blank app
that answers five questions from design §17 before anything is designed around
the answers.

The whole design rests on hosting a `ListView` in a way nobody has confirmed
works. If it does not, that is much cheaper to learn now.

| # | Question | Why it decides something |
|---|---|---|
| 1 | Does `ListViewItem` still draw selected chrome, and does its peer still report `IsSelected`, when the host `ListView` has `SelectionMode="None"`? | Decides design §5.4. If no, the fallback is `SelectionMode="Multiple"` with intercepted input. It is also an accessibility contract, not a cosmetic one. |
| 2 | Does a row presenter measuring wider than the viewport arrange correctly with the inner `ScrollViewer`'s horizontal axis disabled and the table applying the offset? | The horizontal model in design §3. |
| 3 | Does `ItemsStackPanel` recycle normally under that arrangement? | If not, `VirtualizingStackPanel` is the documented fallback. |
| 4 | Does the default `ListViewItem` style still draw selection, hover and focus chrome at `Padding="0"`? | Design §7. If no, cell padding has to move back into the container. |
| 5 | Do Shift+wheel and horizontal-wheel reach the table's own offset rather than being swallowed by the `ListView`? | Design §3 input. |

Question 1 is the gate. Answer it first.

**Output:** a short written answer to each, recorded in the design document's
§17, and the spike deleted. Nothing from it is kept.

---

## 4. Build order

Each phase is complete and demonstrable in the sample before the next begins.
Nothing is stubbed forward.

### Phase A — geometry and hosting

The resolved layout, the header strip, the row presenter, the horizontal offset,
the `ListView` hosting, and the four presentation states.

- `TableColumn`, `TableSearchField`'s absence, `TableLayoutState`, the public
  surface from §5 with selection members present but inert
- schema capture at first `Loaded`, with the §6.1 validation rules
- resolved layout as the single geometry source (design §4)
- `TableHeaderPanel`, `TableHeaderCell`, `TableRowPresenter`
- table-owned horizontal offset driving header and rows at arrange time
- loading / empty / no-results presentation (§17)

At the end of Phase A the table renders columns and rows, scrolls both ways, and
shows its empty states. It selects nothing and sorts nothing.

*Proves Appendix B items 1, 2, 11.*

### Phase B — selection

The largest phase, and the one the design was wrong about until late.

- the selection model: selected set, current item, anchor, logical focus
- reconciliation of all four by key on every source snapshot, atomic, at most one
  `SelectionStateChanged` (§5.3)
- the four `SelectionMode` limits and `SetSelection`'s idempotence (§13)
- pointer semantics: plain, Ctrl, Shift, Ctrl+Shift
- keyboard: Up/Down, Shift+Up/Down, Home/End, Shift+Home/End, Page Up/Down,
  Ctrl+A, scroll-into-view
- `CanInteractWithItem` eligibility, evaluated on rebuild and before interaction
- container reflection per the spike's answer to question 1

Build the model first and unit-test it before any of it is wired to input. It is
pure logic, it is where the identity contract lives, and it is the piece most
likely to grow long-lived bugs.

*Proves Appendix B items 7, 13, 14.*

### Phase C — sorting

- the sort cycle: unsorted → ascending → descending → natural (§9)
- stable sort; equal values retain current base-sequence order
- the sort glyph and its `ItemStatus` automation string
- header composite focus region, `TabNavigation="Once"` (§13)
- `RefreshView()`

Sorting comes after selection because the thing worth proving is that selection
survives a view rebuild. That cannot be shown without both.

*Proves Appendix B items 3, 4.*

### Phase D — column layout

- resize grip, clamping, Escape, double-click fit
- `AutoFitColumn`, `AutoFitVisibleColumns`, `ResetColumnLayout`
- hide/show, the header context menu, move left/right (§12)
- column drag with insertion marker (§11)
- `GetLayoutState`, `ApplyLayoutState`, `LayoutChanged` coalescing (§18)

*Proves Appendix B items 5, 6, 12.*

### Phase E — activation and context

- `ItemInvoked` on double-click, double-tap, Enter
- `RowContextRequested` with placement target and relative point (§15)
- `SuppressRowGestures` and interactive-descendant suppression (§7)

*Proves Appendix B items 1, 9.*

### Phase F — marquee selection

Pure addition. Touches nothing built before it, and §3 says it costs nothing
when disabled.

*Proves Appendix B item 8.*

### Phase G — row drag reordering

Also pure addition, and last because it is the only feature whose value depends
entirely on a host that can accept the request.

- packet from current visual order, `InsertBeforeItem` after packet removal
- insertion marker with its positional cue
- cancellation on Escape, source change, sort change, flag change

*Proves Appendix B item 10.*

### Throughout

Appendix B items 15 (accessibility, input modes, themes) and 16 (invalid
conditions) are not a phase. They are checked at the end of every phase, because
retrofitting either is how both get skipped.

---

## 5. The sample application

One page, one table, one synthetic row type that is deliberately not a torrent —
a name, a number, a date, a percentage, a status string, a bool.

**Six cell templates**, covering the classes §7 names: text, progress bar with
label, button, toggle, combo box, plain number. These prove a rich cell keeps its
own input, focus and automation behaviour instead of being swallowed by the row.

**A control strip** wired to the public surface: selection mode, `IsLoading`,
`EmptyState`, marquee on/off, reordering on/off, a row-count generator that
reaches 10,000, and buttons for `AutoFitColumn`, `AutoFitVisibleColumns`,
`ResetColumnLayout`, `GetLayoutState`, `ApplyLayoutState`.

**An event log pane** showing the five events as they fire, with payloads. This
is how the event contracts in §5.1 get checked at all.

No second table. The torrent host is the second column profile, and it arrives
for free when that work starts.

---

## 6. Verification

Appendix B is the acceptance list. Each phase above names the items it proves.

Three things are measured, not reasoned about:

- **Contrast** (§19): the sort glyph on the header background; header label
  active and inactive; the two-tone insertion marker over selected and unselected
  rows; the marquee stroke over both; disabled row text. In Light, Dark, and a
  contrast theme. A resource name is not proof, and neither is arithmetic.
- **Narrator**: header strip as one region, each header's name and sort status,
  selection announced correctly, overlay layer absent from the tree.
- **Marquee legibility**: whether stroke-only reads over a dense table. Product
  judgement, taken after seeing it.

There is no frame-rate target and no benchmark gate. The performance rules in
§20 are structural invariants — virtualization intact, no work scaling with all
rows during scrolling or layout gestures, no measurement outside explicit fit —
and they are checked by reading the code, not by timing it.

---

## 7. Not in version one

§2 of the specification lists the non-goals and they are not restated here. The
two worth repeating because they were decided during design, not in the original
text:

- **Local text search.** Removed from the component. The host filters before
  assigning `ItemsSource`.
- **Runtime schema changes.** Structural column definitions, search fields, selection mode, the
  identity selector, the interaction predicate and every comparer are captured
  once at first `Loaded`. No column can be added at runtime, ever. Lifting this
  later is a breaking change to the contract, not an addition. Localized
  presentation text is the live exception defined by the current table
  specification §6.1; changing it does not recapture the schema.

---

## 8. Open

- **Design §17 items 6, 7, 8** — contrast measurement, marquee legibility, and
  Narrator — cannot be settled before there is something to look at.
- **The dependency rule** (§20). Left as written. Revisit at the first spike,
  when a project exists that can produce the numbers a package must justify
  before it is accepted: executable size impact, memory footprint, runtime cost.
  Absent those numbers, a package is rejected by default.
- **`microsoft/microsoft-ui-xaml` carries branches named
  `user/aahmedov/tableview-api-spec` and `user/dipesh/tableview/data-shaping-grouping`.**
  A TableView is being specified inside WinUI itself. Observed, not opened, status
  unknown. It is worth knowing what they contain before Phase D.
