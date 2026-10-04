# Table vocabulary

Domain-neutral terms for TableView. Observable requirements belong to the
[TableView contract](docs/tableview-contract.md); implementation choices belong
to the [implementation map](docs/tableview-implementation.md).

## Control and host

**TableView**: a reusable library for large, changing collections with rich cells.
Its control is the `Table` type in the `Syno.TableView` namespace.

**Host**: the page or application that supplies rows and configures a table.
_Avoid_: consumer when naming the same role.

**Schema**: the structural definition of row identity, columns, and interaction
and sort policy. Localised presentation text is distinct from structural schema.

**Item**: one row object supplied by the host. A cell displays content from that
same item.

**Identity**: the basis for recognising an item across a source change, either
its object reference or a host-supplied stable key.

## View

**Source snapshot**: one coherent enumeration of the host's filtered and ordered
items.

**Base sequence**: the source snapshot in its enumeration order.

**Private view**: the table's display projection over the base sequence.

**Natural order**: the order of the base sequence before header sorting.

## Layout

**Baseline layout**: the column defaults declared by the host, including the
control defaults used where none were supplied.

**Baseline width**: a column's default width after its bounds are applied.

**Width override**: a width that takes precedence over the baseline after user
resizing, explicit fit, or restored layout.

**Effective layout**: column order, visibility, widths, and sort resolved against
the baseline and current overrides.

**Layout snapshot**: a data-only record of layout choices that a host can store.

## Interaction

**Current item**: the logical current row, which can be selected or unselected.
It is distinct from physical keyboard focus.

**Anchor**: the row from which a range selection extends.

**Packet**: an ordered set of items acted on together in current visual order.

**Non-interactive item**: a host-designated row that is displayed without joining
selection, invocation, context actions, or reorder packets.

**Reorder request**: the table's report that a packet was placed at a legal
position. The host decides whether to perform the corresponding domain change.
