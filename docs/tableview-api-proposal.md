# TableView and application ownership — independent proposal

Implementation status, 2026-10-04: the generic keys/comparers, single schema row
type, initial state, selection notifications, synchronous event rules and live
text API have been adopted in code and the [authoritative contract](../lib/TableView/docs/tableview-contract.md).
Their implementation has not yet been built or run. This document retains the
independent design rationale; broader adoption and readiness criteria below are
not a claim that every proposed behavior is implemented.

Originally prepared for comparison on 2026-10-04, independently of the other
session's design. The contract linked above is the implementation authority.
Examples below retain the proposed design context; they have not been verified
by compilation.

## Recommendation

Keep TableView as a domain-neutral WinUI library. Its control is `Table`; its
public types share `Syno.TableView`. A torrent list, a peer list, a tracker list,
and a departures board are ordinary hosts of the same control. No torrent type,
engine connection, application preference, or torrent command enters the library.

Keep the existing arrangement: a non-generic XAML control, ordinary row objects,
typed cell templates, and a small `Schema<T>` for typed setup. Improve the few
places where the current interface makes callers compensate for the library:
string-only identity, restricted comparison, and incomplete live text support.
Make initialization and events behave as ordinary C# callers expect. Do not
introduce a table controller, row base class,
provider interface, or product-specific table subclass.

The acceptance standard is a reader who can configure an unfamiliar dataset
without studying table internals. The same interface must preserve selection
during updates, distinguish display sorting from domain ordering, and keep
localisation and accessibility inside their rightful owners.

This is a design to try in real hosts, not a claim that every future need is
known. Existing code can demonstrate a workaround; a concrete scenario can
demonstrate an ambiguity. Only integration can establish whether the complete
interface remains comfortable in the torrent, peers and trackers views. Lack of
a caller today does not prove that a capability is unnecessary. Preserve working
capabilities and distinguish corrective changes from additions still to evaluate.

## 1. The split

There are three owners, not a binary choice between “torrent code” and “table
code.” The application has presentation work that belongs to neither the engine
nor a generic control.

| Concern | Owner | Reason |
| --- | --- | --- |
| Torrent membership, transfer state, queue order, files, peer/tracker facts, commands | Native engine | These facts and decisions must survive the UI closing. |
| UI row objects, snapshot reconciliation, formatting, filters, command availability, inspector context | Application | These decisions interpret the product and its current screen. |
| Columns and cell templates for each use | Host view | Only the host knows what its rows mean. |
| Private display sort, virtualized containers, selection/current/anchor/focus, marquee | TableView | Every host needs the same coherent interaction mechanics. |
| Column resizing, hiding, moving, fitting and shared geometry | TableView | Header, cells, menus and input must use one resolved layout. |
| Row drag feedback and an insertion request | TableView | The control understands visual placement. |
| Whether that placement is meaningful and executing the move | Application and engine | Display order cannot decide a domain operation. |
| Header menu, generic placeholders and table accessibility text | TableView | A standalone table must work without product resources. |
| Row context menu, cell actions, product errors and busy feedback | Application | These are domain actions and their outcomes. |
| Layout snapshot shape and defensive restoration | TableView | Only the control understands its layout invariants. |
| Layout storage, settings location, debounce and disk I/O | Application | A library cannot choose the host's persistence policy. |
| Icon font and glyph definitions | Lucide library | Both product and table use this independent asset. |
| Branding, torrent status colors, pieces map and transfer history | Application | Being drawn inside a cell does not make something table behavior. |

Dependencies point from application to TableView and from TableView to its WinUI
and Lucide dependencies. TableView references no application or engine project.
The engine references no WinUI project. A sample references the library; the
library never references a sample.

“Generic” means independent of the domain. It does not require an open generic
XAML control, a uniform row interface, or support for every imaginable grid
feature. Grouping, remote paging, spreadsheet editing and dynamic schema mutation
need concrete requirements before they enlarge this interface.

### What to retain from the old torrent application

Paths below are relative to `../TinyTorrent/winui3/src/`; that repository stays
untouched. This is a responsibility map, not permission to copy whole files.

| Existing implementation | Proposed treatment |
| --- | --- |
| `TinyTorrent.Ui/Torrent/TorrentPage.xaml` and its code-behind | Reuse useful layouts and cell templates in the application. Keep filter and command wiring there. Replace connection/session orchestration with the new application owners. |
| `TinyTorrent.Ui/Torrent/InspectorView.xaml` and its code-behind | Reuse presentation selectively. Peers and trackers become separate instances of the same `Table`, each with its own source, schema, selection and layout. Inspector context remains application state. |
| `TinyTorrent.Ui/Torrent/TrackerEditor*` | Application editing surface. Validation and persistence reach the engine command owner; the table neither parses nor saves trackers. |
| `TinyTorrent.Ui/Torrent/PiecesView.cs`, `SpeedView.cs`, `SparklineView.cs` | Keep in the application initially. A chart needs an independently useful data contract and another real use before extraction into its own library. It does not belong in TableView merely because a cell hosts it. |
| `TinyTorrent.Ui/Torrent/DetailsSplitter.cs` | Application layout behavior; it divides product surfaces rather than table columns. |
| `TinyTorrent.Ui/InterfaceSettings.cs` | Application persistence, adapted to the current contract. TableView returns data; it never writes this file. |
| `TinyTorrent.Ui/Dialogs/AddView*`, `Dialogs.cs`, `Metainfo.cs` | Reuse suitable dialog presentation. Metadata parsing/preview is owned by the new engine and libtorrent; do not copy a second parser into the new UI. |
| `TinyTorrent.Ui/Preferences/*`, `Engine.cs` | Rebuild the product wiring around current preferences and activation contracts. Remote connection management and Transmission discovery are not table features. |
| `TinyTorrent.Core/Session.cs`, `TorrentCache.cs`, `Optimism.cs`, `Queue.cs` | Do not import as a second torrent authority. Inspect useful algorithms individually; the new engine and application projection own these decisions. |
| `TinyTorrent.Core/TorrentFormat.cs`, `SpeedHistoryBuffer.cs` | Potential application reuse after checking current units, localisation and data lifetime. Neither is table infrastructure. |
| Existing TableView source in this repository | Evolve this implementation. Do not import a second control from the old repository. |

Avoid replacing the old large page with dozens of one-method classes. Extract
an owner when it controls a distinct lifetime or decision: for example, one
application projection reconciles engine snapshots, while a view wires its
table and commands. File length alone is not a reason to create a new abstraction.

## 2. What using the table should look like

Start with an unrelated dataset so the design cannot depend on torrent concepts.
This proposed example assumes an application `Departure` type and a `Text` object
that raises change notifications for its translated properties. The XAML prefix
`table` means `using:Syno.TableView`; `local` names the host's namespace.

```xml
<table:Table x:Name="Departures" ItemsSource="{x:Bind Rows}">
    <table:Table.Columns>
        <table:Column x:Name="Destination"
                      DisplayName="{x:Bind Text.Destination, Mode=OneWay}"
                      Width="240">
            <table:Column.CellTemplate>
                <DataTemplate x:DataType="local:Departure">
                    <TextBlock Text="{x:Bind Destination, Mode=OneWay}"
                               TextTrimming="CharacterEllipsis" />
                </DataTemplate>
            </table:Column.CellTemplate>
        </table:Column>
    </table:Table.Columns>
</table:Table>
```

```csharp
InitializeComponent();

Departures.Schema<Departure>()
    .Sort(Destination, row => row.Destination);
```

That is enough for a sortable table. `Rows` is a stable notifying collection; a
host that replaces the collection uses a live binding or assigns `ItemsSource`
explicitly. Keys, saved layouts, command handlers and reorder configuration are
optional. A host supplies a localised automation name when the surrounding label
does not already identify the table.

Names express roles where needed: `Destination` is clear in this one-column
example; `DestinationColumn` is appropriate if the view also has a destination
editor. Avoid prefixes such as `tblDepartures`, `colDestination`, `strKey` and
`objItem`. A genuine distinction is useful; encoding a type in every variable is
not. Retain familiar framework vocabulary such as `ItemsSource`, `DataTemplate`,
`SelectionChanged` and `EventArgs`.

### The product is another host

The following are proposed setup examples. Row types and column fields belong
to the application; their property names illustrate roles rather than establish
an engine protocol.

```csharp
Torrents.Schema<Torrent>()
    .Key(row => row.TorrentId)
    .Sort(NameColumn, row => row.Name)
    .Sort(SizeColumn, row => row.Size)
    .Sort(SpeedColumn, row => row.DownloadSpeed)
    .Sort(QueueColumn, row => row.Position);

Peers.Schema<Peer>()
    .Sort(AddressColumn, row => row.Address, StringComparer.Ordinal)
    .Sort(ClientColumn, row => row.Client)
    .Sort(DownloadColumn, row => row.DownloadSpeed);

Trackers.Schema<Tracker>()
    .Key(row => (row.TorrentId, row.TrackerId))
    .Sort(UrlColumn, row => row.Url, StringComparer.Ordinal)
    .Sort(NextColumn, row => row.NextAnnounce);
```

`NextAnnounce` may be nullable; “unknown” remains absent data rather than a fake
date. The peer example uses reference identity: stable host row objects preserve
selection during telemetry updates. If the host has a real stable peer identity,
it can supply a key. An address alone is not necessarily a connection identity;
tracker URL text is not automatically a tracker identity either.

Keys are scoped to the table's entire source lifetime, including inspector
switches. A tracker identifier local to one torrent must include that torrent's
identity. A mutable closure over “currently inspected torrent” is unsuitable:
it would change the old rows' keys when the inspector changes. Store identity on
the row. Where the source cannot reliably identify a replacement, accept that
selection clears instead of manufacturing continuity.

Peers and trackers require no torrent-specific table mode. They default to no
row reordering. If tracker tier editing is later shown as an ordered list, its
host must define that operation; enabling drag cannot invent tier semantics.

## 3. The public interface

Keep the existing control, columns, templates, selection value, sort value and
layout snapshot. Keep events as the one delivery mechanism for interactions.
The following is the core surface, not a second declaration of every inherited
WinUI property or every existing content/template property.

```csharp
public sealed class Table : Control
{
    public IEnumerable? ItemsSource { get; set; }
    public ObservableCollection<Column> Columns { get; }
    public Schema<T> Schema<T>();

    public Selection Selection { get; set; }
    public Sort? Sort { get; set; }
    public ColumnLayout Layout { get; set; }

    public void RefreshView();
    public void Fit(Column column);
    public void FitColumns();
    public void ResetLayout();

    public event EventHandler<Selection>? SelectionChanged;
    public event EventHandler<ItemInvokedEventArgs>? ItemInvoked;
    public event EventHandler<ItemContextRequestedEventArgs>? ItemContextRequested;
    public event EventHandler<ReorderRequestedEventArgs>? ReorderRequested;
    public event EventHandler<LayoutChange>? LayoutChanged;
}

public sealed class Schema<T>
{
    public Schema<T> Key<TKey>(Func<T, TKey> key) where TKey : notnull;
    public Schema<T> CanInteract(Func<T, bool> predicate);
    public Schema<T> Sort<TKey>(Column column, Func<T, TKey> key,
                              IComparer<TKey>? comparer = null);
}
```

This is an API sketch: method bodies, dependency-property declarations and
framework-generated details are intentionally omitted. The changes are explicit:

| Reviewed baseline | Proposal | Concrete reason |
| --- | --- | --- |
| `Key(Func<T, string>)` | `Key<TKey>(Func<T, TKey>)` | Stable numeric, GUID and composite identities should not need string conversion or concatenation. |
| `Sort` requires `IComparable<TKey>` | Default comparer with one optional `IComparer<TKey>` | Nullable values, enums and explicit ordinal comparison should use ordinary .NET comparison rather than sentinel values or formatted sort text. |
| `Schema<T>` does not fix one type per table | Capture one row type and reject a conflicting schema | Mixing row policies for different types fails at setup rather than in a later gesture. |
| Plain `Column.DisplayName` | Live dependency property | Host localisation must update existing headers, generated menus and automation names. |
| English loaded once internally | Prepared strings, described below | A language change must work without reconstructing the control. |
| Selection notifications track logical identity only | Notify when the exposed selection value changes, including replacement instances | An inspector observing selection must receive the current row without a second update path. |
| Contract defers handler-requested changes | Commit ordinary runtime setters synchronously | Reading a property after setting it must not require knowledge of an internal command queue. |
| Constructor-time state is incompletely specified | Resolve initial input against the completed schema before first display | XAML source binding and constructor setup must work without a host `Loaded` workaround. |

These are intentional proposed contract changes, particularly the selection and
event rules. They must be adopted explicitly with the implementation; earlier
logical-only notification and deferred-handler rules cannot coexist with them.

There is concrete evidence for the key/comparison changes: the old inspector
converts a tracker identifier to a string, while the current
[jobs host](../lib/TableView/sample/Jobs/JobsPage.cs) casts an enum and substitutes
values for absent measurements. Those are caller workarounds the proposed
selectors can remove. Their intended ordering still belongs to the host.

Most existing names and behavior stay. `Schema<T>` is a setup object owned by
the table, not an independent model of its state. Repeated access with the same
type returns the same setup; another row type is rejected. All setters on a
retained schema object enforce the same setup lifetime, so retaining it cannot
bypass the freeze.

Reference identity is the default. Value rows need stable keys when continuity
across snapshots or independently boxed selection requests matters. The library
does not require reference-row wrappers, a base class, or a row interface. Null
rows are rejected rather than silently disappearing.

### Setup, state and defaults

Configure columns, selection mode and schema after `InitializeComponent()` and
before the first `Loaded`. Structural setup is then fixed, including on a later
unload/reload. Validate it before installing an interactive view. A table can
omit `Schema<T>` entirely when it needs neither typed policy nor sorting.

Source input may arrive from XAML before code-behind configures the schema. Capture
that input without invoking incomplete policies. Constructor-time `Selection`,
`Sort` and `Layout` assignments are initial settings: resolve them together against
the completed schema and current source before displaying the first view. No
public initialization method or host `Loaded` handler is required.

```csharp
Torrents.Schema<Torrent>()
    .Key(row => row.TorrentId)
    .Sort(NameColumn, row => row.Name);

Torrents.ItemsSource = rows;
Torrents.Layout = preferences.Torrents;
Torrents.Sort = new Sort(NameColumn);
Torrents.Selection = new Selection(new[] { previous });
```

Here `previous` may be an older row instance with the same key as one in `rows`.
The initial selection resolves to the current instance. Assignments follow source
order: a later `Sort` overrides the sort in a restored `Layout`; a later `Layout`
replaces the sort as well as the column layout. Before first load, getters report
pending configuration, not a reconciled interactive view. No events fire for
those pending assignments. First load installs coherent state, then raises
`SelectionChanged` once if an initial selected/current item resolved; initial
layout produces no `LayoutChanged` notification.

This initial staging ends at first load. Subsequently setters take effect before
returning, including in event handlers. Selecting an absent row is resolved
against the current source, not retained as an indefinite future request. A host
loading rows asynchronously restores selection after publishing those rows.

Keep these existing capabilities, with their current small names:

| Setting | Default or meaning |
| --- | --- |
| `SelectionMode` | `ListViewSelectionMode.Extended`; retain the current setup-only setting. |
| `IsMarqueeEnabled` | True; generic selection behavior. |
| `CanReorder` | False; the host must explicitly authorize a meaningful row move. |
| `Sort` | Null means source order. One column and direction form one value. |
| `SortInterval` | Existing settling policy for changing values; explicit user sorts remain immediate. |
| `Placeholder` | Empty by default; the host distinguishes Loading and NoResults. Existing rows remain visible during loading. |
| Loading/empty/no-results content and templates | Optional host presentation; complete generic defaults work without them. |
| `ShowsFitButton`, `CellPadding` | Optional presentation settings, not required setup. |

Runtime selection-mode changes remain an integration question, not a declared
non-goal. The reviewed hosts do not establish a need for them, and that is not
evidence that future hosts will not. Keep the current behavior for this proposal's
baseline. If a real interaction switches mode, extend the existing selection
owner; do not require a host to replace the table or build a second selection
mechanism merely to preserve this setup restriction.

`Column` retains its width bounds, baseline visibility, hide/resize capabilities,
cell alignment, cell template and optional header content/template. `DisplayName`
is always a meaningful plain-text label for menus and accessibility, even when
the visual header is an icon. Header presentation can update with language; its
structural column identity and cell-template contract do not change.

`Column.Id` remains an optional stable layout-storage key. It is unrelated to
row identity or the localised header. An unpersisted table needs no IDs. `Fit`
accepts a column reference so runtime callers do not look columns up by strings.
User resizing/hiding/reordering changes effective layout, not column defaults.

### Identity and sorting

`Key` uses `EqualityComparer<TKey>.Default`, with ordinal equality for string
keys. Every snapshot must have unique effective identities: configured keys when
present, otherwise object references. A source containing the same object twice
is rejected because selection and current item cannot distinguish its occurrences.
Distinct objects that compare equal, and separately boxed value rows, remain
distinct under reference identity.

Configured keys must be stable and non-null. An empty string, zero or an empty
GUID is valid if unique; no domain-specific validity rule belongs here. Mutable
key values are invalid. Duplicate identities or wrong-type rows reject the
incoming snapshot before changing the effective view. Keys are only for control
continuity, never interpreted as hashes, endpoints or URLs.

`Sort` uses the supplied comparer or `Comparer<TKey>.Default`. A missing nullable
value sorts before a present value in ascending order; descending reverses that
comparison. A host wanting another order supplies its comparer. Unsupported key
types fail when comparison is required, like ordinary .NET sorting; this is a
deliberate trade from the old compile-time restriction. Failure leaves the last
coherent view in place and is reported as a configuration defect, not silently
treated as equality. [.NET comparer behavior](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.comparer-1.default).

The default is not a migration instruction. For example, a host that deliberately
places unknown completion times last must retain that policy through its comparer.
Removing a sentinel must not silently change the meaning of its ordering.

Stage validation and comparison before committing the accepted snapshot,
effective sort, selection and displayed sequence. A failed sort does not leave
the header claiming the new sort over an old sequence, and emits no successful
layout or selection event. Host changes to mutable row properties are outside
this transaction; the table cannot roll them back.

Sorting compares underlying values, never display strings. Equal values preserve
the latest source order in either direction. Clearing sort returns to that
latest order. A custom comparer is a fixed policy but may read the host's current
formatting culture during a coherent UI-thread refresh; do not capture a stale
culture and expect a language switch to replace a setup-only comparer.

Selectors and comparers are synchronous, cheap and side-effect-free. They perform
no I/O, command execution or mutation. The table does not add a property-name
query language, reflection-based column discovery, or a comparer framework.

## 4. Updates and interaction

### One explicit update rule

All table input and callbacks run on its UI dispatcher. The application applies
a completed engine update there; TableView does not become another dispatcher,
transport scheduler or version arbiter.

| Change | Host action | Table action |
| --- | --- | --- |
| A displayed property changes | Notify through ordinary binding | Update the cell; no view rebuild is required. |
| Sort values or eligibility change in one batch | Call `RefreshView()` once after that batch | Re-evaluate the captured source and reconcile interaction state. |
| Membership, filtering or natural order changes | Notify through the source collection, or assign a fresh finite snapshot | Capture the new source once, then update its private view. |
| Source objects are replaced under the same keys | Publish the replacement source | Resolve selection/current/anchor/focus to the new instances. |
| A non-notifying collection changes | Assign a new snapshot | Do not expect `RefreshView()` to discover it. |

Use one publication per logical batch where the host already receives batches.
Do not add a second observable collection solely to feed the control, and do not
add a public Begin/End update protocol until a measured caller requires it.
Repeated `Clear()`/`Add()` calls are not an atomic replacement: their intermediate
states can legitimately remove selection.

The table owns sort settling because it knows both the old display order and the
new source. It shows additions/removals promptly while avoiding continual
movement of surviving rows under the pointer. Ordinary telemetry does not replace
the source on every tick. A disruptive source/sort change cancels a drag rather
than applying an insertion against a stale arrangement.

Unload detaches source notifications, timers and template subscriptions. Reload
recaptures a notifying source to incorporate missed changes. A plain enumerable
retains its captured snapshot until the host assigns a replacement: re-enumerating
a one-shot iterator could otherwise erase the rows. Assigning a new source while
detached still replaces the captured input. Both paths retain valid table-owned
state and the original schema. The host must not dispose a table to avoid a
subscription leak. A new source detaches the old source at the same owner.

### Selection and actions

`Selection` is one immutable value containing `Items` and `Current`. Current can
be unselected. Setting it makes an atomic change; a request that resolves to the
same value is a no-op. The table resolves requests to eligible current instances and
owns the selection anchor and focus mechanics. The application observes selection
to drive commands and the inspector, rather than maintaining a second editable
selected-items collection.

```csharp
private void OnSelectionChanged(object? sender, Selection selection)
{
    Inspector.Show((Torrent?)Torrents.Selection.Current);
}

private void OnItemInvoked(object? sender, ItemInvokedEventArgs e)
{
    OpenDetails((Torrent)e.Item);
}
```

The inspector reads current state because another event subscriber may already
have made a newer selection. Use the immutable payload when the specific change
itself matters, such as recording an interaction. The cast at the WinUI event
boundary is intentional. Typed templates and typed
schema already cover the repeated operations. A retained typed controller just
to remove these two casts would duplicate the control's state surface. Use a
checked cast when a row type is required; `OfType<T>()` must not silently hide
misconfigured mixed sources.

Events are synchronous and follow completed control mechanics. Event collections
are immutable snapshots of membership/order, not deep copies of mutable rows.
Convert rows to domain identifiers before starting asynchronous work. Context
events carry the row, selected items, placement target and optional pointer
position; the host shows its menu while that target is valid.

`SelectionChanged` means the publicly exposed value changed: membership, item
order, current item, or the row instances representing them. Replacing a selected
row under the same key preserves selection and notifies its observers with the
replacement. The inspector handler above therefore stays correct for stable
objects and replacement snapshots alike. Ordinary property updates on the same
objects use their bindings and produce no selection event.

First compare selected/current identities and their order: a change always
notifies. Then check whether unchanged identities expose different row values.
For reference rows, compare instances by reference, regardless of an overridden
value equality. For value rows, use the default equality comparer. A struct that
defines equality without its key therefore cannot hide a move to another
selected identity. Snapshot collections copy their input so a caller cannot
later mutate the event packet.

Callback failures are programming errors. The control must not publish a partial
sort or snapshot. Event-handler exceptions occur after the control operation;
the control cannot roll back a host action. Finish the operation before notifying
the host. An event handler may then make another ordinary synchronous change:

```csharp
Torrents.Selection = Selection.Empty;
UpdateCommands(Torrents.Selection);
```

`UpdateCommands` sees the new selection. That setter may synchronously raise a
nested notification if the value changed; assigning the same resolved value
raises none. Event payloads describe the operation that raised them, while getters
always describe the latest committed state. A later subscriber can therefore
receive an older payload after another subscriber has changed current state;
it reads the table if it needs the current value. This is normal synchronous
event delivery, without deferred setters, frozen getters or a command queue.
After invoking host code the control must not resume work using stale gesture
state. There are no parallel `ICommand` properties or async acceptance callbacks.

A gesture can produce more than one notification: right-clicking a different row
changes selection before requesting its context menu. Complete the selection
mechanics and capture the intended action packet before the first notification.
After selection handlers return, check that the packet, target row and any
placement element are still valid for that action. If a handler changed the
selection, removed/replaced its rows, recycled the target or unloaded the table,
cancel the pending context/invocation notification. Do not target a replacement
row or reuse a stale element. A handler that only reads state or updates the
inspector does not cancel the action. This check belongs to the existing input
owner; it adds no public callback or deferred queue.

Interactive cell controls retain their normal input. Use the existing attached
`SuppressRowGestures` only for custom interactive content the control cannot
recognize; ordinary buttons and editors should not require host pointer plumbing.

### Reordering is an insertion request

Keep `ReorderRequested` with `Items` and `Before` in its event arguments.
The packet is in the supplied source order; the anchor is a remaining row after
removing that packet, or null to append within that supplied order. Indices are
not a stable command boundary. An invalid, internal or unchanged placement emits
no request.

The host enables reorder only when its current source is a meaningful domain
order. The table additionally permits it only in natural order or under the one
column marked `DefinesRowOrder`. Under descending order, it converts both packet
and insertion boundary back to ascending domain order. Under a rate/name sort it
withholds reorder. A search-filtered torrent list disables reorder unless the
application explicitly defines placement among hidden rows. Given full order
`[A, hidden H, B, hidden J]` and visible `[A, B]`, an append to the visible list
does not say whether the domain insertion belongs before or after `J`. The host
must define that mapping or withhold the gesture.

`DefinesRowOrder` promises a strict order consistent with the base sequence.
Validate that promise before enabling sorted reorder: ascending must reproduce
the base sequence and descending its exact reverse, with distinct order keys.
If it does not, withhold reorder under that sort. Ordinary sort columns still
allow stable ties. This distinction matters: `[A(1), B(1), C(2)]` sorts descending
to `[C, A, B]`; reversing it would not recover the source order. A tracker tier
shared by several rows therefore cannot itself define row order.

```csharp
private void OnReorder(object? sender, ReorderRequestedEventArgs e)
{
    var torrentIds = e.Items.Cast<Torrent>().Select(row => row.TorrentId).ToArray();
    TorrentId? before = e.Before is null
        ? null
        : ((Torrent)e.Before).TorrentId;
    Commands.Move(torrentIds, before);
}
```

`Commands.Move` is an illustrative application entry point, not a new proposed
engine signature. That owner revalidates the identities/order and reports busy
or failure. TableView changes no source and awaits no result. The host publishes
confirmed order and provides equivalent keyboard/touch commands through that
same application operation. Peers need none of this code.

## 5. Localisation, layout and platform behavior

The table owns the words for its generic operations, the host owns column names
and cell text, and Lucide owns its font assets. Follow the repository's
[per-project catalogues](localisation.md), with English usable in a standalone
table. Do not inject torrent text keys or expose the table's catalogue dictionary.

Prepared strings are proposed as one immutable library value, extending the
existing internal text owner rather than creating another lookup implementation:

```csharp
public sealed class Strings
{
    public static Task<Strings> LoadAsync(string language,
                                        CancellationToken cancellation = default);
}

// On Table, alongside its ordinary state properties:
public Strings Strings { get; set; }
```

An ordinary host needs no translation setup; English is the default. A host with
live language switching prepares the library strings asynchronously alongside
its own text. If preparation fails, it retains the previous language. Its existing
language owner checks the latest request and publishes all prepared values in
one UI-dispatcher callback, with no awaits between assignments: host text, column
labels, table strings, root `Language`, and `FlowDirection`. One prepared value
can serve many tables. This is a resource value, not another language preference
or a provider framework.

`Table.Strings` is a dependency property. Each localised host binds it to the
language owner's current prepared value, so existing and subsequently created
tables follow the same path:

```xml
<table:Table x:Name="Peers"
             ItemsSource="{x:Bind Rows}"
             Strings="{x:Bind Text.TableStrings, Mode=OneWay}" />
```

Columns are omitted here to show only the language binding. `Text.TableStrings`
is a notifying host property holding a `Syno.TableView.Strings` value, not another
catalogue. Opening the peers view after switching language therefore uses the
current value immediately. There is no table registry or mutable global default.
For the first localised view, the existing language owner prepares its initial
text and table strings before creating that bound view. An English-only host
that does not bind `Strings` needs no such application setup.

`LoadAsync` completes loading, validation and fallback before returning a value;
callers cannot construct an incomplete one. Assigning `Table.Strings` performs
no I/O and starts no background load. Preparation failure and cancellation are
handled once by the existing host language owner. English readiness is a library
responsibility, not another initialization call required of ordinary hosts.

The table's single strings setter refreshes generated headers, open generic
menus, placeholders and automation text without replacing rows, schema, focus,
selection, widths or sort. New containers use the current value. Catalogue loading
and fallback remain library internals. `DisplayName`, header content and header
presentation update through live properties; changing a label never changes its
layout key. Custom header/cell text follows host bindings. Invalidation completes
before presenting the next frame; this is presentation coherence, not a
transaction over arbitrary host event handlers. `Strings` exposes no public
dictionary, lookup interface, or mutable catalogue contents.

Do not call the property `Translation`: WinUI already uses
[`UIElement.Translation`](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.uielement.translation)
for a three-dimensional rendering offset. A short name that hides a platform
property is less readable, not more elegant.

The extra value is justified by preparation and atomic publication, not by a
desire for dependency injection. Merely assigning WinUI `Language` cannot load
our JSON catalogues or report preparation failure. The platform property remains
the font/text-language signal; it is inherited through the element tree.
[Microsoft's Language contract](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.frameworkelement.language).

UI language and regional number/date formatting remain distinct host decisions.
The language owner refreshes affected display bindings and requests `RefreshView`
if comparisons depend on the changed culture. Native IME input and active editor
text remain untouched until their ordinary commit/cancel boundary.

Layout storage stays equally direct:

```csharp
Torrents.Layout = preferences.Torrents;
Torrents.LayoutChanged += (_, _) => preferences.QueueSave(Torrents.Layout);
```

`QueueSave` illustrates an application storage owner that coalesces writes and
does I/O asynchronously. Each view has its own saved layout, so peer column
choices cannot overwrite torrent column choices. Restoring a snapshot emits no
`LayoutChanged` event and tolerates removed columns, invalid widths and obsolete
sorts. If restoration changes the exposed selection order, ordinary
`SelectionChanged` still applies. Runtime misuse
such as `Fit` with another table's column is a configuration error. Snapshot
collections are independent immutable data, with no row objects, templates,
controls, delegates or references to storage.

The library owns complete keyboard, focus, high-contrast and automation behavior
for its mechanics. Hosts supply useful domain labels and accessible cell content.
No host should patch table internals to make Ctrl+Arrow location visible or to
make selection work through UI Automation. Existing interaction and Fluent rules
remain governed by the [control contract](../lib/TableView/docs/tableview-contract.md)
and [interface guidance](interface.md); this API proposal does not approve the
current pixels or remove recorded accessibility work.

## 6. Alternatives considered

| Design | Benefit | Cost and decision |
| --- | --- | --- |
| Existing XAML `Table` plus small `Schema<T>` | Ordinary WinUI surface; typed setup without wrapping rows; one state owner | A few event-boundary casts. Recommended, with the scoped changes above. |
| Generic `Table<T>` as the consumer's primary control | Fully typed properties and events | Requires a different XAML construction strategy or per-type concrete controls; weakens ordinary XAML consumption. Rejected here. |
| Direct `Column.SortBy((Peer row) => ...)` and control-level typed selectors | Removes the schema type | Repeats type declarations and permits incompatible row policies across columns. No reduction in caller obligations. |
| A retained `Rows<T>` or table controller | Can make all runtime outputs typed | Adds a second concept/lifetime beside the XAML control and moves ownership for selection/projection. Not justified by the small casts in these hosts. |
| Property-name strings, inferred columns or reflection | Short demonstrations | Moves errors to runtime, loses rich-cell intent, introduces naming conventions that callers must memorize. Rejected. |

## 7. Adoption and verification

If chosen, update the active table contract and callers in the same implementation
change. This proposal remains comparison material; it is not a competing authority.
There is no need for a compatibility facade for repository-internal callers.

1. Reconcile the public contract with current source names before changing it.
   Current source uses `Table`, `SelectionChanged`, `ItemContextRequested`,
   `ReorderRequested`, `CanReorder`, `RefreshView`, `Fit`, `FitColumns`,
   `ShowsFitButton`, `ResetLayout` and `SortInterval`; portions
   of the contract still show predecessor names. Source-map paths have also moved.
2. Extend the existing identity and comparison owners. Preserve one path for
   reference identity, configured keys, stable sort and interaction reconciliation.
3. Implement live column presentation and prepared strings at existing
   text/header/menu owners. Resolve source-subscription lifetime and the known
   automation/focus/RTL gaps before claiming product readiness.
4. Adapt the existing departures/jobs samples, then implement product table
   callers with real engine projections. Do not copy the old torrent host into
   the library's sample or test dependency graph.
5. Reuse focused checks under the [testing policy](testing.md). Add a check only
   for an otherwise unprotected failure; a rename does not earn a new test suite.

Use the first real torrent, peers and trackers callers to reassess open design
judgments: whether selection mode needs to change at runtime, whether the update
cadence needs adjustment, and whether language preparation is comfortable at
startup and when opening new views. These are integration questions, not failures
of genericness or reasons to add an extension framework in advance. A review can
approve the design's consistency without resolving every future product need.

Acceptance evidence should cover the following user-visible failures, rather
than mirror every method: replacement rows lose or misdirect selection; a nullable
sort corrupts ordering; a reversed drag commands the wrong insertion; a detached
view keeps receiving updates; a language switch loses focus or leaves open menus
in the old language; automation can see selection but cannot change it. Use
existing coverage where it already protects these outcomes.

Compilation and runtime checks belong to the later implementation. This document
was prepared without building or launching applications. A design review can
establish a coherent interface; it cannot establish rendering quality, Narrator
behavior, performance or successful compilation of proposed signatures.

## 8. Review record

Three independent reviewers first examined alternatives for minimal surface area,
extensibility and ordinary C# ergonomics. A second, fresh set then reviewed the
proposal for first-use clarity, simplicity and contract consistency, without
treating the earlier pass as proof. This record describes the final decisions,
including corrections to that earlier review.

| Challenge | Final resolution |
| --- | --- |
| Does typed access justify another controller? | No. Keep typed setup/templates and explicit casts at the native event boundary. |
| Must rows be classes? | No. Permit value rows; stable keys give continuity across boxing/replacement. |
| Can a reload enumerate any source again? | No. Recapture notifying sources; retain captured one-shot snapshots. |
| Does reversing a descending view recover domain order? | Only with a validated strict order. Tied ordinary sorts remain supported, but do not authorize that reorder mapping. |
| Does filtered append mean the full queue's end? | No. It means the supplied order's end; the host defines the domain mapping. |
| Can comparison fail after the new sort has become visible state? | No. Stage comparison and commit coherent control state together; do not promise rollback of host object mutations. |
| Can constructor setup restore selection and sort before `Loaded`? | Yes. Resolve initial settings with completed schema before first display; define assignment precedence without another initialization API. |
| Does a same-key replacement keep an inspector current? | Yes. Preserve selection and notify when its exposed row instances change. |
| Do property setters work normally inside handlers? | Yes. Commit synchronously; payloads describe their event and getters describe current state. The earlier deferred-setter rule was removed. |
| Is a runtime selection-mode change proven necessary or unnecessary? | Neither. Retain the existing setup behavior for the baseline and evaluate mode switching through actual host interactions. |
| Must an empty string key be prohibited? | No. Stability, non-nullness and uniqueness are sufficient for any key type. |
| Can a language property alone ensure prepared, coordinated resources? | No. An immutable ready `Strings` value supports one preparation path and publication without I/O in setters. |
| Is `Translation` a suitable name on a WinUI control? | No. It already names a platform rendering property; use `Strings`. |
| Will a peers view opened after a language switch start in English? | No. Bind its `Strings` property to the same current prepared value as existing views. |
| Can one source contain the same reference twice without a key? | No. Effective identities must be unique even when identity is by reference. |
| Can value equality hide a different selected key? | No. Compare identity and order before comparing exposed row representations. |
| Can a selection handler invalidate a pending context action? | Yes. Recheck its captured packet and placement after handlers return; cancel the invalid action. |
| Does removing nullable sentinels authorize changing null ordering? | No. Preserve the host's intended ordering with a comparer. |
| Did concurrent source renames invalidate the draft's “current” names? | Yes. Reconciled to the reviewed baseline's `ReorderRequested`, `Before`, `CanReorder`, `FitColumns` and `ShowsFitButton`; no compatibility aliases. |

Earlier review rounds passed their revised drafts. Three independent reviewers
also passed this revision for caller clarity, edge-case correctness and evidence
for its scope, with no remaining concrete design blockers. They checked the
corrected identity and compound-event rules and the distinction between
demonstrated requirements and integration judgments. Absence of a current caller
does not establish that a capability is unnecessary.

The document's local links, code fences and proposed names were checked as well.
This is a reviewed proposal, not proof of a completed implementation; section 7
defines the evidence limits.

## Source basis

- [Current schema](../lib/TableView/src/Schema.cs), [columns](../lib/TableView/src/Column.cs),
  [events](../lib/TableView/src/Events.cs) and [selection](../lib/TableView/src/Selection.cs).
- [Control properties](../lib/TableView/src/Table/Properties.cs),
  [sorting](../lib/TableView/src/Table/Sorting.cs),
  [input/selection setup](../lib/TableView/src/Table/Selection.cs) and
  [source capture](../lib/TableView/src/Body/Source.cs).
- [Departures host](../lib/TableView/sample/Board/DeparturesPage.cs) and
  [its XAML](../lib/TableView/sample/Board/DeparturesPage.xaml).
- [Control contract](../lib/TableView/docs/tableview-contract.md),
  [implementation findings](../lib/TableView/docs/tableview-implementation.md),
  [target architecture](architecture.md) and [naming policy](naming.md).

The implementation map and source links identify the current owners. Remaining
gap reports are review leads, not fresh runtime verification.
