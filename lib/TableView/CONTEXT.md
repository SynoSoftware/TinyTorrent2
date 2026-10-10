# Table vocabulary

Domain-neutral terms for TableView. Observable requirements belong to the
[TableView contract](docs/tableview-contract.md) and its
[hierarchy extension](docs/hierarchy.md); implementation choices belong to the
[implementation map](docs/tableview-implementation.md).

## Language

### Control and host

**TableView**: a reusable library for large, changing collections with rich cells.

**Host**: the page or application that supplies rows and configures a table.
_Avoid_: consumer when naming the same role.

**Schema**: the structural definition of row identity, columns, and interaction
and sort policy, with optional hierarchy relationships. Localised presentation
text is distinct from structural schema.

**Item**: one row object supplied by the host. A cell displays content from that
same item.

**Row**: the table's presentation of one item across its columns.
_Avoid_: item, when referring to the presentation rather than the host's object.

**Identity**: the basis for recognising an item across a source change, either
its object reference or a host-supplied stable key.

### View

**Source snapshot**: a coherent capture of the host-supplied items and their
source order, including child membership in a hierarchical table.

**Base sequence**: the items in source order before header sorting; in a
hierarchical table, the visible roots and descendants in that order.

**Private view**: the table's display projection over the base sequence.

**Natural order**: the host's order before header sorting, applied within each
sibling group in a hierarchical table.

**Row surface**: the scrollable area of the table that presents its rows.

**Held row**: a row the private view keeps showing, dimmed and non-interactive,
after it left the source, while the pointer is over the rows or a row's context
menu is open. A change the person caused is never held.

### Hierarchy

**Root**: an item with no parent in the supplied hierarchy.

**Children**: the immediate items beneath a parent in the supplied hierarchy.
_Avoid_: descendants, when referring only to immediate children.

**Descendants**: all items beneath a parent, including children at every depth.

**Expansion**: the host-owned choice to show an item's children. A descendant
appears in the private view only while all its ancestors are expanded.

**Hierarchy column**: the column that presents indentation and expansion controls.

### Layout

**Baseline layout**: the column defaults declared by the host, including the
control defaults used where none were supplied.

**Baseline width**: a column's declared width, or the control default, raised to
its minimum width.

**Width override**: a width that takes precedence over the baseline after user
resizing, explicit fit, or restored layout.

**Fit**: a width override taken from the widest header or cell the table has
realized for a column.
_Avoid_: autofit, auto-size.

**Fill**: a fit of the visible columns followed by one proportional scale that
makes them end at the table's right edge, wider or narrower.
_Avoid_: stretch.

**First fill**: the one proportional scale of the baseline widths to the
table's width that the table applies when it first has a width and no width
override exists. It measures nothing.

**Trailing space**: the part of the table to the right of the last visible
column.

**Visibility override**: a shown or hidden state that takes precedence over the
baseline after the user shows or hides a column, or after a restored layout.

**Effective layout**: column order, visibility, widths, and sort resolved against
the baseline and current overrides.

**Layout snapshot**: a record of column and sort choices that a
host can retain and restore, separate from row content and selection.

### Interaction

**Selection**: the items chosen for table actions, distinct from any checkboxes
or other choices displayed inside their cells.

**Current item**: the logical current row, which can be selected or unselected.
It is distinct from physical keyboard focus.

**Anchor**: the row from which a range selection extends.

**Packet**: an ordered set of items acted on together in current visual order.

**Non-interactive item**: a host-designated row that is displayed without joining
selection, invocation, context actions, or reorder packets.

**Reorder request**: the table's report that a packet was placed at a legal
position. The host decides whether to perform the corresponding domain change.
