# Hierarchical rows

Status: accepted extension to TableView, written 2026-10-06. This document
specifies the library behavior and its first host, the Files browser. It extends
the version 1 [contract](tableview-contract.md) with optional child rows; all
other contract behavior remains applicable. No change to an owner ruling is
implied. Acceptance of the design is not evidence of runtime validation.

## Purpose and scope

Files needs independent row selection for commands on several files, alongside
the checkboxes that decide what to download. It also needs the same column
geometry, resizing, fitting and sorting as the other tables. Extending TableView
gives those mechanics one owner instead of teaching a separate file tree to
imitate them.

A hierarchical table displays roots and expanded descendants as ordinary rows
in its existing virtualized body. The name cell adds indentation and an expand
arrow; other cells remain aligned with their headers regardless of depth.
There is one header, one column layout, one selection model and one scroller.
Flat hosts, including the torrent queue, keep their existing behavior.

The extension covers finite, already available hierarchies, sibling sorting,
expansion, selection, keyboard navigation and accessible expansion. Lazy loading,
group summaries, arbitrary grouping, tree editing and hierarchical row dragging
are outside this scope because Files requires none of them. No separate tree
control, rendering system or general hierarchy framework is introduced.

## Ordinary tables stay ordinary

Hierarchy is explicitly opt-in through `Schema<TRow>.Hierarchy`. An existing
flat host compiles and runs unchanged: same row types, XAML, schema calls,
source updates, selection events and layout snapshots. It needs no mode flag,
empty child callback, expansion property, interface, adapter or new dependency.
The library does not inspect rows to guess whether they contain children.

Without that call, every column keeps its existing hide/reorder behavior,
`CanReorder` still enables queue dragging, and keyboard and automation behavior
remain those of the flat table. Hierarchy-only restrictions and validation
apply only to a schema that requests hierarchy. This prevents an optional Files
feature from changing the other hosts' contracts.

A flat table allocates no hierarchy projection, parent/depth index, child
subscriptions or expansion state. Its rows acquire no expander elements or
extra first-cell layout containers, even collapsed ones. Flat source capture,
sorting, refresh, selection and scrolling perform no child traversal, hierarchy
validation or per-item hierarchy callback. A cheap feature check at the relevant
entry point is acceptable; passing every flat row through a tree representation
is not, because it adds work proportional to a feature the host did not request.

Keep the existing control and shared mechanics. Isolate the optional hierarchy
work at those owners' entry points rather than duplicating the flat table or
introducing a strategy framework. This preserves both ordinary call sites and
one implementation of layout, selection, reconciliation and input arbitration.

## Ownership and host API

Use the existing [owners](tableview-implementation.md#owners-in-the-current-code).
Source capture owns root and child membership; the private view owns visible
order and derived depth; selection owns current, anchor and selected items;
the existing layout and row input owners handle geometry and gestures.
These are responsibilities, not requirements to introduce new classes.

The host owns row objects, their child collections, expansion state and domain
commands. Expansion belongs there because Files retains it when filtering
temporarily removes rows from the supplied source. TableView neither knows file
types nor keeps a second expansion store to synchronize with the host. Its
captured relationships supply parent and depth information; callers do not
maintain matching parent pointers or depth values for the control.

Add one setup-only operation to the existing typed schema:

```csharp
public Schema<TRow> Hierarchy(
    Column column,
    Func<TRow, IEnumerable<TRow>> children,
    Func<TRow, bool> isExpanded,
    Action<TRow, bool> setExpanded);
```

For the existing Files model, hierarchy setup follows the same schema chain as
a flat table. `NameColumn` is its declared XAML column:

```csharp
table.Schema<FileNode>()
    .Key(node => node.Path)
    .Hierarchy(NameColumn,
        children: node => node.Children,
        isExpanded: node => node.IsExpanded,
        setExpanded: (node, value) => node.IsExpanded = value)
    .SortKey(NameColumn, node => node.Name);

table.ItemsSource = model.Roots;
```

`column` identifies the cell containing hierarchy navigation. `children` returns
immediate children in natural order, with an empty enumerable for a leaf.
`isExpanded` reads host state; `setExpanded` writes the requested value for both
expansion and collapse. No new row interface, node wrapper, options object or
binding-expression compiler is needed to describe these four facts.

`Hierarchy` follows the existing fluent schema vocabulary of `Key` and `SortKey`:
it declares a relationship, not a runtime command. The boolean reader and state
writer have distinct names at the call site. `children` means immediate children;
descendants is reserved for the whole subtree. These names follow the existing
[naming policy](../../../docs/naming.md) without introducing synonyms for item,
identity, source snapshot or private view.

Declare hierarchy at most once before schema capture, with a column belonging
to this table and non-null callbacks. The declaration itself makes that column
visible, non-hideable and first in the effective layout. The caller does not set
matching flags or arrange declarations to make hierarchy valid. Its width,
templates and sort key remain ordinary column settings. Initial source and
schema setup retain the existing control lifecycle; no extra load handler is
required. Row templates, sort keys and selection packets receive the original
`TRow`, never projection wrappers.

The read callbacks are pure. `setExpanded` is a synchronous state write, not a
policy callback or a request event: when it returns, `isExpanded` returns the
requested value. It may raise the row's normal property notification, but does
not call the table, change source membership or start asynchronous work. A
request for the current state does nothing. TableView then commits the view
and selection through their existing owners; the host need not call
`RefreshView()` in this callback. Callback defects follow the existing failure
boundary, without a new rollback or accept/reject protocol.

Hosts can change expansion directly, including Expand all and Collapse all,
then call `RefreshView()` once after the batch. TableView re-reads expansion in
that operation. Property notifications alone still update bound content, not
the projection, so telemetry cannot accidentally rebuild the hierarchy.
The initial expansion state is the host's supplied value. No expansion event,
default-state setting or saved-expansion collection duplicates that authority.

For example, the existing Files command expands its folders and publishes the
new view once:

```csharp
model.Expand(true);
table.RefreshView();
```

## Source, identity and projection

`ItemsSource` supplies roots. Each item occurs exactly once in the complete
supplied hierarchy, including collapsed descendants. Effective identities use
the existing key selector, or object identity when none is configured, and are
unique across the whole hierarchy rather than just among siblings. Reject null
rows, null child enumerables and repeated identities before publishing the
candidate snapshot. The same uniqueness rule rejects cycles and shared children;
it needs no separate graph policy. This prevents a row from appearing under two
parents or expansion from recursing forever.

Capture a coherent hierarchy on the UI dispatcher using the source contract's
finite, stable enumeration rule. Observe notifying child collections as well
as the root collection, including those below collapsed rows, so membership
does not become stale while a folder is closed. Release subscriptions when
their collections leave the captured hierarchy or the control unloads, using
the existing source lifetime rules.

A root or observed child collection notification recaptures that collection and
captures newly reachable child sources before publishing a coherent hierarchy.
Other captured collections retain their snapshots, so a child notification does
not consume an unrelated one-shot enumerable again. On reload, recapture the
notifying collections at every depth and retain non-notifying snapshots.
For a plain collection, or replacement of a child collection without a
collection notification, the host publishes a replacement root enumerable
through `ItemsSource`, causing a fresh hierarchy capture. Assigning the same
dependency-property value is not a refresh mechanism. Prefer stable notifying
collections for a live hierarchy, as the Files model already supplies.
`RefreshView()` reuses captured membership and re-evaluates expansion,
interactivity and sort values. These two update paths retain the existing
distinction between a source snapshot and its private view.

The private view is a depth-first sequence: each row precedes its visible
descendants. Roots have depth zero. A child is visible only when all its
ancestors are expanded. Having children is determined from the supplied
hierarchy, not from the expanded flag. A row with no supplied children has no
expand arrow and is a leaf for navigation and automation.

Expansion inserts the visible descendant sequence; collapse removes it.
Expansion changes neither source order nor descendant expansion flags, so
reopening a parent restores the descendant branches the person left open.
Neither operation changes column widths or fits columns. Use the existing
collection reconciliation and native scroll anchoring; expansion must not recreate unaffected rows or jump to the top of the table.

Filtering remains a host projection. TableView receives the surviving hierarchy
and does not invent search, ancestor retention or forced expansion. A filtered
row is unavailable for selection under the existing source rules. Replacing
objects under the same keys preserves table-owned state for surviving visible
rows; the host carries expansion to replacement objects because it owns it.

`ScrollIntoView` continues to address the current private view. A hidden child
does not implicitly expand its ancestors; a host that intends to reveal it
expands the path, refreshes once, then scrolls to it. This keeps scrolling from
silently changing host expansion state.

## Cost of enabled hierarchy

The existing [performance contract](tableview-contract.md#20-performance-requirements)
still applies. UI virtualization limits realized controls; it does not by itself
bound source enumeration, sorting or allocations. Distinguish these costs:

- Initial capture and a published structural change may inspect the supplied
  hierarchy to establish membership, identity and subscriptions. This is source
  work, not something repeated on scroll or a progress repaint.
- Expansion, collapse and value-only `RefreshView()` use captured relationships.
  They do not re-enumerate child sources, rebuild subscriptions or repeat
  identity validation when membership has not changed. Recomputing the
  visible sequence through the existing projection is allowed; a separate
  incremental tree engine is not required to avoid that traversal.
- Projection visits visible branches. Collapsed descendants need no expansion,
  interactivity or sort callbacks until they can enter the view. Their membership
  remains captured and observed, so deferring presentation work loses no source
  changes and opening them uses current values.
- Scrolling, pointer movement and column layout do no hierarchy-wide work.
  Realized rows use captured depth and parent information; measuring or arranging
  a cell does not walk its ancestors or instantiate descendant controls.
- Display-only property changes update bound cells without reprojecting or
  resorting. The extension adds no per-row timers, polling or forced layout to
  keep expansion synchronized; the existing explicit update model supplies it.

These limits permit the straightforward visible-list projection while keeping
hidden files out of recurring presentation work. Optimize further only for a
measured failure, including the host's aggregate sort keys in that measurement;
moving an expensive traversal into a host callback does not make it free.

## Columns and first-cell geometry

The effective layout has one additional constraint: the hierarchy column is
visible and first in reading order. Resolve it in the existing layout owner
for initial layout, header actions, programmatic layout, restore and reset,
so every entry point preserves access to hierarchy navigation. The declaration
overrides that column's visibility, hideability and order; other columns retain
their relative order and ordinary rules. No new per-column locking setting is
needed. Resizing, fitting and sorting remain available for the hierarchy column.

Indentation and the expander occupy space inside that column's effective width.
They never shift subsequent columns. Each depth adds one consistent indent;
siblings reserve the same expander slot whether or not they have children.
A hierarchy containing only root leaves reserves no expander slot, so an
ordinary flat file list does not start with a blank navigation gutter.

Use the targeted WinUI TreeView's indentation and expander presentation as the
geometry reference, resolved in the library's existing layout path. There is
no host copy of template offsets, no independent column grid and no new public
spacing framework. Respect the existing rich-cell padding and overflow policy;
deep names can truncate within a narrow column without covering other cells.

Fit includes indentation and expander space for cells available to normal
visual layout. It retains the current bounded measurement contract: it does
not expand folders, create off-screen templates or inspect hidden descendants
solely to find the widest name. Column persistence stores column layout only;
it does not acquire selection or expansion state.

## Sorting

Apply the existing column comparer independently to the sibling collections
that enter the view, then flatten the result. Hidden child collections acquire
their current sorted order when opened. A parent's visible descendants travel
with it as a contiguous subtree. Descending order reverses the sibling
comparison, never the flattened sequence, so a child cannot precede its parent.
Equal keys retain the latest sibling source order; clearing sort restores it.

The same active sort applies when a folder is opened. Header sort changes take
effect through the existing sort operation. Value-only refreshes retain the
existing `SortInterval` policy; expansion, collapse and structural changes take
effect immediately because delaying membership would make an arrow lie about
what it displays. Settling can move sibling subtrees, never separate a parent
from its descendants. A changed parent relationship is structural even when
the complete set of identities is unchanged.

The host supplies natural order and typed sort values for every row. The library
knows no folders-first rule or aggregate calculation; those belong to the host's
domain. One existing comparer contract serves flat rows and sibling rows.

## Selection, current item and collapse

Row selection is independent of expansion and of controls inside cells.
Selecting a parent selects that row only. Commands can interpret a folder as
its descendants; the library never silently adds those descendants to selection.
Extended selection, Ctrl-click, Shift ranges, marquee and Ctrl+A act on interactive
rows in the current visible sequence, including rows outside the viewport.
Collapsed descendants are excluded, so a range cannot select invisible files.

Expansion preserves selection, current and anchor. Activating an expand arrow
does not select its row, invoke it or start marquee/drag. It is not an extra Tab
stop; keyboard expansion is available from the passive row. A pointer expansion
leaves existing keyboard focus alone unless collapse would hide that focus.

Collapse follows one atomic transition:

1. Remove descendants that became hidden from selection.
2. If current or row focus became hidden, move it to the nearest remaining
   visible, interactive ancestor. For Collapse all this is the surviving root,
   not an intermediate ancestor that also disappeared. Other visible selection
   stays selected; the ancestor is not automatically selected.
3. Clear a hidden range anchor. The next range gesture follows the table's
   existing no-anchor behavior rather than retaining an invisible endpoint.
4. Publish at most one `SelectionChanged` for the final exposed packet, and
   reveal the new current row when keyboard focus needed to move.

If no interactive ancestor survives, use the existing removal fallback for current
and focus. A collapse must not steal focus from an unrelated control. Reopening
does not restore hidden selections, so later commands cannot unexpectedly regain
targets. Ordinary source removal and filtering retain their existing pruning
rules rather than treating every removal as a collapse.

Explicit `Selection` assignments also resolve only visible interactive rows.
Selection by a key under a collapsed ancestor does not expand it. Non-interactive
rows retain the contract's exclusion from input, including expansion, while the
host can still set their expansion state before refreshing.

## Keyboard, invocation and gestures

For the passive row surface, use tree navigation in the reading direction:

| Key in left-to-right layout | Behavior |
| --- | --- |
| Right on a collapsed parent | Expand it; keep current and selection. |
| Right on an expanded parent | Move to its first interactive visible immediate child; if none exists, stay. |
| Right on a leaf | Stay. |
| Left on an expanded parent | Collapse it using the transition above. |
| Left on a collapsed parent or leaf | Move to the nearest interactive visible ancestor; at a root, stay. |

Swap Left and Right in right-to-left layout, matching the mirrored arrow.
Navigation to another row uses the existing selection-mode and modifier rules:
Ctrl preserves selection and anchor, Shift extends through visible rows, and
unmodified navigation selects according to the current mode. Expansion-only
actions do not extend a range. Alt-modified keys remain available to the shell.
Up/Down, Home/End, Page keys, Space and Ctrl+A keep the table's visible-list rules.

Rich controls retain their input: an open priority ComboBox handles its arrows,
and a checkbox handles Space. Headers retain their separate Left/Right column
navigation. Enter and passive-row double-click keep the existing invocation
contract; they do not also toggle expansion. This prevents one input from both
opening a file and changing the hierarchy.

Marquee uses the visible flattened rows and the existing gesture arbiter. A
host expansion, collapse or refresh during marquee preserves the rectangle and
recomputes coverage as an ordinary view update; a hidden anchor is still cleared.
An expander press never enters that arbiter's marquee or reorder path. Header
resize and column drag remain independent of hierarchy membership changes.

Hierarchy makes row reordering ineligible through the existing drag-eligibility
decision, just as a sort can make it ineligible. `CanReorder` remains the host's
permission, not a guarantee that the current view admits a reorder. A hierarchy
never starts a row drag or raises `ReorderRequested`, even when that permission
is true; there is no meaningful flat insertion boundary across parents. This
needs neither an extra caller flag nor a new setter exception. Ordinary marquee
behavior remains available, and flat queue dragging keeps its current behavior.

## Accessibility and virtualized rows

Retain the current list/list-item automation structure and named headers. Add
hierarchy information to the existing item peer: one-based level, one-based
position within its displayed sibling order and sibling count after host
filtering. Counts describe siblings, not every flattened row or just realized
containers, so scrolling does not change a row's announced position.

Rows expose `ExpandCollapse` with Expanded, Collapsed or LeafNode state as
appropriate. Expand and Collapse use the same host write and reconciliation
path as pointer and keyboard input. Repeated requests for the current state
are no-ops; leaf and unavailable operations follow the platform pattern rules.
Selection remains the existing `SelectionItem` behavior; the file checkbox
retains its own Toggle behavior and accessible name.

Recycled and keyed item peers must resolve the current row's depth, parent,
expansion and interactivity before acting, so an old peer cannot expand a different
file. Collapsed descendants are absent from the navigable visible collection;
a cached peer cannot select, invoke or expand them. Off-screen visible rows
retain the existing virtualization and realization behavior without
materializing an entire subtree of controls.

Publish the corresponding expansion, structure and selection changes after a
coherent update. The expander glyph is decorative in automation when its row
provides the action, avoiding duplicate controls. Generic control text follows
the library catalogue and live-language path; file names and file commands stay
with the host. Preserve the existing theme, focus and input requirements.

Microsoft permits conditional ExpandCollapse support on ListItem controls;
see [control patterns](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-controlpatternmapping)
and [hierarchy properties](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-implementingcustomnavigation).
This extension does not claim full Grid/Table patterns or promise a particular
Narrator phrase such as "tree grid". Acceptance requires observing that Narrator
communicates hierarchy, selection and expanded state and can operate expansion;
properties in an in-process cache alone do not establish that experience.

## Files browser integration

The product's [Files policy](../../../docs/interface.md#add) owns its columns,
priority actions, command targets and wanted choices. The shared `FileBrowser`
uses a hierarchy column whose host content is the checkbox, file-type icon and
name; TableView supplies the indent and arrow. Column widths, first fill,
overflow, selection and sibling sorting use the existing control mechanics.
The host supplies domain sort keys and folder aggregates. TableView emits rows,
never torrent file indices or engine commands, so the library remains usable
by hosts without torrent concepts.

## Contract amendments for implementation

The proposal deliberately changes these version 1 boundaries; this list keeps
the extension from being mistaken for behavior already guaranteed today.

| Existing owner | Amendment when implemented |
| --- | --- |
| TableView contract section 2 | Admit this optional hierarchy; retain the other grouping non-goals. |
| Sections 5 and 5.3 | Add typed hierarchy setup, child source capture and expansion writes/refresh semantics. |
| Sections 6, 11, 12 and 18 | Keep the hierarchy column visible and first through every layout entry point. |
| Sections 8 and 10 | Include hierarchy chrome in shared first-cell geometry and bounded fit. |
| Section 9 | Sort siblings and settle whole subtrees. |
| Sections 13 and 14 | Define visible-only ranges, collapse reconciliation and tree navigation. |
| Section 16 | Include hierarchy in the existing row-reorder eligibility decision. |
| Section 19 | Add hierarchy properties and ExpandCollapse to existing item peers. |
| Section 20 | Preserve the flat-table cost and API boundaries; bound optional hierarchy work as specified here. |
| Product interface, file choices | Specify independent row selection, bulk Priority and passive-row Space selecting rows; retain wanted-checkbox Space and F2 priority access. |

## Delivery and evidence

Implement in coherent slices, following [testing](../../../docs/testing.md).
This specification does not authorize desktop launches or require a new test
framework. Inspect existing coverage before adding checks; each new test must
watch a failure not already guarded at a cheaper layer.

1. Extend source/projection and the existing selection owner together. Complete
   when nested expansion, sibling sorting and collapse yield coherent visible
   rows and one final selection packet, while a flat schema follows its existing
   path. A focused behavioral check earns its place if it catches a child
   separated from its parent or a hidden selected file remaining a command target.
2. Add hierarchy chrome, keyboard and automation to existing rows. Complete when
   the existing non-torrent sample can exercise a nested hierarchy using the
   same header, layout and virtualized body. Reuse that host; no second sample
   application or hidden measurement tree is needed.
3. Migrate Files and consolidate priority commands. Complete when an arbitrary
   multi-selection changes the intended files once, overlapping folder/file
   targets are deduplicated, and the copied header/geometry is gone. A focused
   check should pin the command's exact target set because ordinary control
   selection tests cannot catch a host sending priority to the wrong files.
4. Review the integrated journey with pointer, keyboard and Narrator when a
   desktop run is explicitly authorized. Check expansion near the viewport edge,
   collapse containing focus, Shift ranges across folders, Ctrl+A versus wanted
   checkboxes, filtered folder edits, column fit/reorder, RTL and a deep long
   name. Observe themes and focus in the actual control rather than asserting
   XAML, pixels or wording in unit tests.

Before completion, exercise the affected flat-table selection, sorting,
marquee and queue-drag paths through relevant existing checks. Existing flat
hosts must require no source edits to accommodate the extension; compilation
and review establish that API boundary without a new compatibility framework.

Before implementation, capture a flat-table baseline with the existing sample
and diagnostics. After the change, compare the same build configuration, rows,
columns, viewport and update sequence on the same machine. Cover initial load,
sorting, live updates, scrolling and selection/drag input. Record operation time,
UI-thread work, allocations, retained memory, realized containers and collection
notifications where they explain a difference. Separate startup from steady
state and repeat a comparison only enough to distinguish a regression from
measurement noise; no new standing benchmark suite is required.

A repeatable flat-table slowdown or additional per-row hierarchy cost blocks
acceptance. Find and remove its cause before shipping the extension; performance
claims require the before/after evidence rather than an arbitrary percentage
allowance or a claim that the difference is probably imperceptible. If the
authorized measurement is unavailable, mark this acceptance check unverified.

For hierarchy itself, use a focused large source with most branches collapsed,
then expand a representative large branch. Establish that collapsed data adds
no recurring layout/scroll work, opening a branch does not enumerate its source
again, and live progress updates do not recreate the tree. Include the actual
Files aggregate sort keys, since sample-only comparers can hide host costs.
Report source review, compilation, behavioral checks and desktop observations
separately. An unperformed Narrator or interaction review remains an explicit
acceptance gap, not a claim inferred from a successful build.

### Implementation evidence, 2026-10-06

The library, Files host and Project plan sample implement this extension. The
Files host retains its existing wanted and priority model; toolbar, context,
F2 and inline priority actions converge on `FileSelection.Change`. That method
unions descendant files by file index and rejects targets from a replaced source.

Independent standards and specification reviews found and corrected duplicate
expansion refreshes, obsolete host properties, F2 target selection, collapse
focus reveal, hidden-peer lookup cost and a captured name-sort culture.

The existing desktop test host adds four checks in
[HierarchyTests](../tests/HierarchyTests.cs):

| Check | Failure it protects against |
| --- | --- |
| Sibling sorting and collapse | Children separating from their parents, or hidden selected files remaining command targets. Existing flat tests have no parent relationships. |
| Captured children and invalid hidden membership | Re-enumeration during expansion or accepting duplicate identities in a collapsed branch. Flat source tests do not traverse children. |
| Restored hierarchy layout | A saved layout hiding or moving the only hierarchy navigation column; flat layouts permit both actions. |
| Row-reorder rejection | Hierarchical rows entering flat queue dragging when a host enables reordering; moving a child would break its parent relationship. |

Visual Studio MSBuild Debug/x64 builds of the app, library, sample and existing
test host pass with zero warnings and zero errors. The final app build reuses
the library outputs verified by the sample and test-host builds. The prescribed
Everything check was unavailable because its service was not running; a direct
directory scan found no generated output outside the permitted artifact roots.

These four checks compile but their desktop test host has not run. The later
authorized product self-capture, `LibraryCapture-5cc7b53c-434a-4cf1-9f43-d46f9adf9213`,
used native automation providers to select a child and an outside file, collapse
their folder, and expand it. Hidden selection was pruned, outside selection was
retained, current moved to the folder, and expansion left priorities unchanged.
All 300 torrents and 307 payload hashes were retained. Twelve Files captures
across EN/ES, Light/Dark and the three supported sizes passed an independent
image review. The app build and Everything output-path check passed; exact logs
and evidence are in the [morning report](../../../docs/morning-report.md).

The later `LibraryCapture-88080534-8c7b-4f9e-843d-cc7bf429e73e` selected a folder,
its child and an outside file through native selection providers, then invoked
High through the real priority menu. Persisted priorities were exactly
`[7,7,7,4,4,4,4,4]`; the same menu restored the original Normal values. All 300
torrents and 307 payload hashes were retained. This closes the host's exact bulk
target check without deriving the expected indexes from its selection code.

Physical pointer/keyboard input, Narrator, actual High Contrast and before/after
flat-table performance remain unverified
acceptance checks. Native programmatic header focus passed in LTR and RTL, but
does not establish physical tree-key navigation. No comparative responsiveness
claim follows from source review, compilation or these captures.
