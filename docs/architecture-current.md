# Current architecture

Source review, 2026-10-06; source placement updated 2026-10-16. This describes
what exists in **TinyTorrent2**, not the earlier application in `../TinyTorrent`.
The [target architecture](architecture.md)
owns the intended product design and its [open decisions](architecture.md#decisions-still-open).
The first download path has Release builds, production checks and a hands-on
review recorded in [implementation evidence](01-implementation.md).

## What exists

The reusable WinUI table library lives in `lib/TableView/`, with its two
development hosts in `lib/TableView/sample/` and `lib/TableView/tests/`.
[TinyTorrent.slnx](../TinyTorrent.slnx) includes these projects, Lucide, and a
native engine and product WinUI host. Its [entry point](../engine/src/Main.cpp)
claims one logon-scoped engine and starts the native desktop owner. The first
usable `.torrent` download path and everyday torrent actions are implemented; the later implementation
milestones remain work in progress.

| Existing project | Role | Project dependency |
| --- | --- | --- |
| [Engine](../engine/src/Engine.vcxproj) | Native transfer state, persistence, pipe, tray, splash and lifetime. | libtorrent and native dependencies from `3rdParty/`; no WinUI or .NET. |
| [TinyTorrent](../app/src/TinyTorrent.csproj) | On-demand product window and display/draft state. | TableView, WinUI and .NET; one pipe to Engine. |
| [TableView](../lib/TableView/src/TableView.csproj) | Reusable `Table` control, templates, and English text. | WinUI and Lucide; no torrent application dependency. |
| [Lucide](../lib/Lucide/src/Lucide.csproj) | Lucide icon font and glyph names. | WinUI. |
| [TableViewSample](../lib/TableView/sample/TableViewSample.csproj) | Desktop demonstrations and diagnostic probes. | TableView. |
| [TableViewTests](../lib/TableView/tests/TableViewTests.csproj) | Desktop MSTest host for control behavior. | TableView. |

```mermaid
flowchart TB
    subgraph Engine["Native engine process · while transfers may run"]
        Entry["Native desktop owner"] --> Session["libtorrent session"]
        Entry --> Pipe["Local pipe"]
        Session --> Store["Ordered persistence writer"]
    end
    subgraph Product["Product process · while its window is open"]
        Window["WinUI views"] --> ProductTable["TableView"]
        Window <-->|"bindings and native UI requests"| Presentation["MainViewModel and AddDraft"]
        Presentation <-->|"commands and confirmed snapshots"| Pipe
    end
    subgraph Sample["Sample process · when launched"]
        Pages["Render jobs / departures pages"] --> Table["Table"]
        Data["Synthetic in-memory rows"] --> Pages
        Table --> Platform["WinUI / .NET / Windows"]
        Table --> Icons["Lucide icon font"]
    end
    subgraph Tests["Separate test process · when explicitly run"]
        Harness["MSTest + control hosts"] --> TestedTable["Same TableView library"]
    end
```

The sample and tests are development hosts; TableView and Lucide load inside
their host process. The product uses the same TableView public API. The engine
restores durable membership and intent, safely commits additions before allowing
payload transfer, and checkpoints through one storage writer. Closing the product
exits its process while the engine and tray remain. Appearance choices use the
saved settings owner and embedded catalogues in each process.

The product follows MVVM. `MainViewModel` owns membership, accepted selection,
commands and connection facts. It publishes a completed visible list only when
membership or order changes; routine snapshots refresh window state, while
connection and language transitions propagate to the affected child owners.
`Settings.Editor` owns confirmed settings, ordinary field drafts, adapter choices,
conversion and submission. Its concrete `Schedule` owns period editing and whole-list saves.
`AddDraft`, `Inspector` and `FileDraft` own their unfinished operations.
Inspector retains peer/tracker row identity within the current session and target.

`MainWindow` retains native pickers, chrome, focus and TableView gestures. Menu
shortcut hints use the registered native accelerators. One
private dialog interaction covers native display, submission and cleanup, and
remembers the actual suspended editor for unsuccessful close recovery. The
model accepts or refuses selection before the window reflects it. Connection
conditions and command failures have separate app-level messages; field and
editor failures stay at their owners. Text bindings retain explicit language
refresh where the catalogue changes.

In the engine, one private `CommitEdit` derives, persists and applies intended
tracker/priority changes for explicit edits and merges; each entry point keeps
its own legality checks. `SpeedHistory` owns its buffers and aggregation, while
the engine owns sampling cadence and the session envelope.

The project files own framework, platform, and package choices. The sample and
test hosts declare unpackaged deployment. TableView carries its own English
text in an embedded `en.json`. The target product's
[installation plan](architecture.md#installation-and-updates) selects unpackaged
deployment. [Release.cmd](../Release.cmd) generates an Inno Setup installer with
prerequisite checks and delivery; installed upgrade and uninstall journeys still
need verification on a disposable Windows machine.

## App source layout

The app remains one WinUI project. `Shell/` holds the window, main view model
and InspectorPane composition. `Torrents/`, `Settings/`, `Library/` and
`Subtitles/` keep their related state, operations and presentation together.
`Shared/` holds common facilities, with engine communication, storage, network,
desktop and diagnostics in their supporting folders; `Controls/` holds reusable
UI. `App` and `Enums.cs` stay in the project root. Partial-type folders retain
their owner's namespace. [Naming and structure](naming.md#app-folders) owns the
placement rules. Prototype documents live in `app/prototypes/`.

## Build integration

The root solution builds the native and managed projects together through Visual
Studio MSBuild. [TableView.slnx](../lib/TableView/TableView.slnx) contains only
the control, Lucide, and the control's development hosts. Both solutions select
x64. Broader platform lists in individual C# projects do not establish additional
product release targets.

[Directory.Build.props](../Directory.Build.props) owns the `artifacts/` root and
the managed output layout. It excludes generated folders from source inputs so
repeated XAML builds cannot consume their own output. The
[native project](../engine/src/Engine.vcxproj) uses that same root for its output
and intermediates.

The window build copies the engine through [CopyEngine.ps1](../app/CopyEngine.ps1).
An identical binary stays untouched, so UI-only restarts leave transfers running.
When the binary changes, the script forcibly stops the engine running from that
destination in the current Windows session and waits before replacing it. It
resolves junctions before comparing executable paths, because the process can
report the physical output path rather than the repository's artifacts path. This
development restart bypasses Exit confirmation and the final checkpoint so the
build can replace the executable without waiting for a dialog.

Every TinyTorrent in a logon session shares one window lock and one engine
pipe, so a launch hands off to whichever window holds the lock and connects to
whichever engine owns the pipe, from any folder. A Debug window therefore acts
on those two conflicts only. When the lock is held by a window started from
another path, it stops that window and takes the lock; a window from its own
path is shown as usual. On its first connection, it stops the engine that owns
the pipe unless that engine runs from beside it, then starts its own. Path
identifies the build because a running executable is locked and the build
stops that engine before replacing it; identical bytes do not, because the
capture output holds a copy of the same engine that a review runs on a
disposable store. Release windows never stop anything.

The native project selects the toolchain and static linking, and includes and
links the libraries in `3rdParty/` that [Dependencies.ps1](../engine/src/Dependencies.ps1)
builds, as [third-party dependencies](architecture.md#third-party-dependencies)
describes. It never starts that script; without a complete installation in
`3rdParty/` the build fails. It reads libtorrent's compile definitions from the libtorrent build,
because they affect libtorrent's ABI. The build files own these choices; a
separate build system is not needed for engine work.

## Sample and host responsibilities

[App](../lib/TableView/sample/App.cs) opens
[MainWindow](../lib/TableView/sample/MainWindow.cs), which navigates
between a render-job table and a departures board. The
[render-job page](../lib/TableView/sample/Jobs/JobsPage.cs)
uses [Jobs.Feed](../lib/TableView/sample/Jobs/Feed.cs);
the [departures page](../lib/TableView/sample/Board/DeparturesPage.cs)
supplies a different row type and presentation. Neither manages torrents.

The host supplies row objects, filtered source order, column definitions, cell
templates, identity and sort selectors, and handlers for domain actions. A table
reorder reports a request to that host; it does not change the host's domain
records. `ColumnLayout` is a snapshot the host can store, not storage owned by the
control. Probe pages are diagnostic material, not a second product interface.

## Inside TableView

The [implementation map](../lib/TableView/docs/tableview-implementation.md) is the
detailed source authority. At the module interface, the host deals with one
`Table`; its partial files share that same control and state.

```mermaid
flowchart TB
    Host["Host: items, schema, templates"] --> Source["Body.Source: source snapshot"]
    Source --> Control["Table: private order and reconciliation"]
    Control --> View["Body.View: displayed collection"]
    View --> Rows["Virtualized ListView rows"]
    Control --> Selection["SelectionState: selection, current item, anchor"]
    Control --> Geometry["EffectiveLayout: shared column geometry"]
    Geometry --> Header["Header CellsPanel"]
    Geometry --> Cells["Row CellsPanel"]
    Rows --> Cells
    Control --> Events["Invocation, context, reorder, layout events"]
    Events --> Host
```

[Body.Source](../lib/TableView/src/Body/Source.cs) captures the host's
enumeration on the UI thread and subscribes to collection changes.
[Table sorting](../lib/TableView/src/Table/Sorting.cs) derives a private
order; [Body.View](../lib/TableView/src/Body/View.cs) reconciles that
order for the native list. These are presentation projections of the same host
items, not independent domain authorities.

The selection model reconciles object references or supplied stable keys.
The effective layout is shared by header and row panels; the table owns horizontal
offset and the body's native scroller owns vertical scrolling. The input arbiter
chooses between a press, marquee selection, and row drag. Its visual helpers do
not choose the gesture. These owners hide real mechanics from both sample hosts;
splitting or merging them merely to change file count would not improve depth.

## Existing gaps and evidence limits

The [implementation map](../lib/TableView/docs/tableview-implementation.md) records
remaining keyboard, automation and RTL work. [Strings](../lib/TableView/src/Strings.cs)
now prepares immutable embedded catalogues with fallback; live column text and
`Table.Strings` refresh existing presentation. Source subscriptions suspend on
unload and resume on reload. The first product builds and live language switches
have runtime evidence in the [implementation record](01-implementation.md).
Broader source-lifetime and unrealized-row automation scenarios remain separate
from that narrow product journey.

The test project contains control and input checks, not engine, persistence, or
pipe verification. The first milestone's noninteractive run passed 207 checks;
focused engine checks cover framing, failed storage and restart intent.
Source inspection establishes structure and code paths; it does not establish
rendered appearance, accessibility behavior, responsiveness, transfer throughput,
or background memory use. Evidence selection belongs to [testing](testing.md).
