# Historical torrent table integration

Archived 2026-10-03 from Appendix A of the TableView contract. This preserves
the earlier torrent host configuration, including daemon/RPC, ghost-row,
and optimistic queue assumptions. It does not define TinyTorrent2 behavior.
The current [architecture](../architecture.md), [interface](../interface.md),
and [engine](../engine.md) contracts own the product; the
[TableView contract](../../lib/TableView/docs/tableview-contract.md) owns generic
control behavior. The original Appendix A numbering is retained below.

## Appendix A — Reference integration: torrent list

This appendix is informative. It describes one concrete host configuration and
the torrent-specific behavior it must supply. It does not add requirements to
the generic `TableView` contract.

### A.1 Reference profile

The torrent list uses the following columns. They are host configuration, not
built-in `TableView` behavior.

| ID | Declared initial width | Minimum | Target first-run visible | Cell content |
|---|---:|---:|:---:|---|
| `name` | 150 | 90 | yes | name and error indication/tooltip |
| `progress` | 220 | 110 | yes | progress bar, percentage, transferred amount |
| `status` | 110 | 95 | yes | localized status |
| `queue` | 80 | component default | yes | queue position |
| `eta` | 110 | component default | no | estimated time |
| `speed` | 180 | 160 | yes | download and upload speed |
| `peers` | 88 | component default | yes | connected/available peers |
| `size` | 100 | component default | yes | total size |
| `ratio` | 90 | component default | no | share ratio |
| `added` | 100 | component default | no | added date |
| `completedOn` | 110 | component default | no | completion date |

The target first-run visible set is:

`name`, `progress`, `status`, `queue`, `speed`, `peers`, and `size`.

This is deliberate torrent-host policy, not a generic default or a claim about
another table's startup state. Every listed initial width is explicit,
including `name = 150`; none relies on `TableView`'s generic 150-DIP fallback. A
`component default` minimum means the torrent profile supplies no additional
minimum beyond the table's 48-DIP default `MinWidth`. These values are not
generic defaults for another table.

The torrent host supplies:

- typed templates for all cells;
- header templates for the localized label, existing header icon, and any
  header tooltip or description;
- column comparers; its incoming natural order is queue-ascending;
- All/Downloading/Seeding domain filters and its own text filter;
- `Placeholder` and its loading/empty/no-results content;
- pause, resume, recheck, remove, queue, path, copy, and sequential-download
  commands;
- the row context menu;
- queue-reorder validation, mutation, optimistic display, and rollback;
- the layout-state storage key and persistence service.

Bulk commands operate on the current selected packet when the context row is
already selected. Single-row actions operate on the context row. These are
torrent-page rules, not table APIs.

### A.2 Reference data and view projection

The following reference configuration keeps daemon state, queue policy,
commands, and storage in the torrent host while `TableView` supplies table
mechanics.

#### A.2.1 Stable rows and source projection

The torrent host may keep one `TorrentRowViewModel` per torrent identity and
update its bindable properties from daemon snapshots. That is a host choice for
smooth progress, edit, and rich-cell animation continuity; it is not a
`TableView` requirement. The row exposes `INotifyPropertyChanged` so cells
redraw without rebuilding rows.

A host may instead publish rehydrated row instances with the same ID. The
schema's key selector still preserves table selection/current state, but it
cannot preserve template-local animation or edit state across the replacement.

Regardless of that choice, the torrent host owns a source collection or
projection of its current row instances. It derives semantic queue order first,
applies a pending optimistic queue order when one exists, then applies the
state filter and its own text filter before assigning `TableView.ItemsSource`.
Semantic ordering, domain-state filtering, and text filtering stay outside the
control:

```text
daemon snapshot -> current torrent row objects
               -> authoritative or pending semantic queue order
               -> torrent host's state- and text-filtered projection
               -> TableView.ItemsSource -> table sort
```

Set `Placeholder` to `Empty` when the unfiltered daemon torrent collection has
no items, `NoResults` when the state or text filter excludes all source rows,
and `Loading` while the first snapshot is in flight.

The torrent host applies these display rules:

- removed IDs are omitted;
- ghost/pending rows bypass the All/Downloading/Seeding state filter, but the
  host's text filter matches both name and ghost label;
- checking rows appear in both Downloading and Seeding filters;
- ghosts are display-only: they cannot be selected, invoked, context-clicked,
  included in bulk commands, or included in a queue-reorder packet.

When a changed value affects active table order or ghost eligibility (for
example queue position, name, size, or ratio), the torrent host batches updates
and calls `RefreshView()` once. Fast-changing speed/progress updates normally
redraw only; they do not force a view refresh.

#### A.2.2 Columns

```xml
<controls:TableView
    x:Name="TorrentTable"
    ItemsSource="{x:Bind ViewModel.FilteredTorrents, Mode=OneWay}" />
```

During host setup, declare the eleven `TableColumn` definitions in the
declared order and with the explicit initial widths in Appendix A.1, each with
`x:Name` so the schema can name it. Put the typed XAML templates in the torrent
host or its resource dictionary, then assign each template directly to the
matching column. There is no torrent-specific column class or renderer registry.
Give every definition its localized `DisplayName` for the generated header menu
and UI Automation.

Each sortable column gets its order from `Schema<TRow>().Sort`, which joins
the column to its key through the field the XAML compiler already generated for
`x:Name`: renaming a column is then a build break rather than a lookup that
silently stops matching. The natural order is the torrent host's
queue-ascending semantic source order, not an implicit torrent feature of
`TableView`. Start with the seven intended visible columns listed in Appendix
A.1, then assign `Layout` from the stored snapshot, if present.

The torrent host therefore has exactly one mapping layer:

```text
TorrentRowViewModel + DataTemplate + a sort key -> TableColumn
```

A user resize or fit produces a width override that supersedes these torrent
widths until reset; `ResetColumnLayout()` returns to the Appendix A.1 values.
The inset inside every cell is the control's own `CellPadding`, applied to the
header cell and the row cell alike, so the host repeats no cell margin and the
header cannot drift out of line with its column.

Status display calculation, formatted speeds, ETA strings, and progress labels
belong on the torrent row view model (or its display-state owner),
because the templates use them. `TableView` receives only the finished row
object and template.

#### A.2.3 Events and policy callbacks

The torrent host wires its generic policy and event boundary in one place:

```csharp
private void ConfigureTorrentTable()
{
    TorrentTable.Schema<TorrentRowViewModel>()
        .Key(row => row.Id)
        .CanInteract(row => !row.IsGhost)
        .Sort(QueueColumn, row => row.QueuePosition)
        .Sort(SpeedColumn, row => row.ActiveSpeed)
        .Sort(CompletedOnColumn, row => row.CompletedOn ?? DateTimeOffset.MaxValue);

    TorrentTable.SelectionStateChanged += OnTorrentSelectionStateChanged;
    TorrentTable.ItemInvoked += OnTorrentItemInvoked;
    TorrentTable.RowContextRequested += OnTorrentRowContextRequested;
    TorrentTable.RowsReorderRequested += OnTorrentRowsReorderRequested;
    TorrentTable.LayoutChanged += OnTorrentLayoutChanged;
}
```

The row type is stated once, so there is no cast per selector and no comparer
adapter in the host. The marquee needs no line here: it is on by default.

The handlers have exactly these responsibilities:

- `OnTorrentSelectionStateChanged`: publish selected/current torrent IDs to the
  shell. `Selection` is assigned only when an independent shell action changes
  selection; matching IDs are an idempotent no-op.
- `OnTorrentItemInvoked`: open the permitted docked detail/inspector action.
- `OnTorrentRowContextRequested`: build and show the torrent `MenuFlyout`.
- `OnTorrentRowsReorderRequested`: pass the immutable request to the queue
  coordinator, which owns optimistic update, RPC, reconciliation, and failure.
- `OnTorrentLayoutChanged`: read `Layout` and give that snapshot to the torrent
  settings debouncer.

Cell buttons and menu items call torrent commands directly through the row view
model or host command service. Do not add `OnPause`, `OnResume`,
`OnRemove`, or other torrent callbacks to `TableView`.

#### A.2.4 Selection, activation, and the row menu

Observe `SelectionStateChanged` and project its selected rows to the shell's
selected torrent IDs and current/active ID for global hotkeys, bulk commands,
and inspector/detail loading. When an independent application surface changes
that state, resolve its IDs to current torrent rows and assign `Selection`.
Matching logical state is a no-op, so the table remains the one interactive
selection/current owner without a suppression guard.

Handle `ItemInvoked` by opening the docked torrent detail/inspector, unless
detail opening is disabled. Ghost rows are never invoked.
Handle `RowContextRequested` to create the torrent `MenuFlyout` at the supplied
placement target and its relative point when present. Ghost rows do not request
a menu. Use the supplied selection unchanged:

- Pause, Resume, Resume now, Recheck, Remove, Remove data, and queue commands
  apply to the selected packet.
- Open folder, Set/Locate path, Copy hash, Copy magnet, and sequential-download
  toggle apply to the context row.

The host decides command enablement from current torrent state. The generic
table has no knowledge of any of these commands. When queue dragging is
enabled, keep equivalent queue move commands in this menu (or another keyboard-
accessible torrent command surface) for users who do not use pointer drag.

#### A.2.5 Queue drag reordering

Mark the queue column `DefinesRowOrder`, and bind `IsRowReorderingEnabled` to
true only when the state filter is All and no text filter is active. The flag is
off by default and is the only owner of the capability, so the torrent host
turns it on exactly for the views it can map to queue ordering, and subscribing
to `RowsReorderRequested` neither enables nor implies the gesture. The sort
needs no binding: the table
offers the drag unsorted and under a queue-column sort either way, withholds it
under any other sort, and reports a descending view's request already in
ascending queue order. The torrent host handles `RowsReorderRequested` as
follows:

1. Convert `MovingItems` and `InsertBeforeItem` (defined after removal of the
   moving packet) into the daemon's queue operation(s).
2. Immediately publish the optimistically reordered semantic source projection.
3. Send the command through the torrent command/RPC path.
4. Reconcile with the next daemon snapshot; roll back or show an
   error state if the command fails.

If queue moves must be serialized, set `IsRowReorderingEnabled` false while a
request is pending and restore it after reconciliation when the view remains
eligible.

The table provides the packet, insertion anchor, feedback, and selection
preservation. It never calculates queue priorities or talks to the daemon; the
one order it reverses is its own view's, so that a request under a descending
queue sort arrives in queue order.

#### A.2.6 Layout persistence

Use a torrent-specific settings key to load and save `TableLayout`. During
setup:

1. create the default torrent columns;
2. load the saved data-only snapshot;
3. assign it to `Layout`, before or after the first `Loaded`;
4. subscribe to `LayoutChanged` and, from that handler, read `Layout` and
   debounce persistence of the snapshot it returns.

Do not save selections, filters, live status values, progress, or queue drag
state as part of the table layout. Application preferences remain the storage
owner; `TableView` only validates and produces the DTO.

#### A.2.7 Torrent integration checklist

- The Appendix A.1 columns have typed templates, localized headers, explicit
  comparers, the listed first-run visibility, and a persisted layout key.
- The host supplies state- and text-filtered, queue-ascending rows; it applies
  pending queue order before those filters, and calls `RefreshView()` once
  after a view-affecting batch.
- Ghost/pending and checking rows follow A.2.1's display and eligibility rules;
  the host may keep stable row objects or rehydrate by key.
- It enables marquee selection for the torrent page, projects table
  selection/current state to the shell, supplies its domain menus and commands,
  and handles queue requests, persistence, loading, no-results, error, offline,
  and permission presentation as described above.
