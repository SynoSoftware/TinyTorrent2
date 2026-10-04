# Target architecture

Selected design, reviewed 2026-10-03. TinyTorrent will be a Windows desktop
torrent client with one native libtorrent engine, an on-demand WinUI 3 interface,
one local pipe, and the shared Synapse control library. These are implementation
targets; the [current architecture](architecture-current.md) records what exists.
[Decisions still open](#decisions-still-open) lists the remaining choices, and
the [documentation guide](README.md) identifies each contract's authority.

## Usability comes first

Common sense, ease of use, and intuitive behavior take precedence over other
design preferences in these plans. The contracts and implementation exist to
serve the person's task. Prefer familiar actions, sensible defaults, and
automatic handling of routine work; change a mechanism that adds avoidable steps
or exposes internal bookkeeping. Ask the user to decide only when their intent
is ambiguous or proceeding would risk their files or discard unfinished work.

Follow Microsoft's Fluent 2 principles through native Windows and WinUI patterns.
When inherited visual rules conflict with clarity, accessibility, or familiar
platform behavior, resolve the conflict at the rule's owner. The
[interface contract](interface.md#native-controls-and-visual-authority) owns the
concrete presentation guidance.

## Product and scope

The product manages local downloads and seeds. It supports magnet and torrent-file
addition, metadata preview, destination and file choices, paused addition,
duplicate detection, pause/resume, queue order, transfer and peer limits, seeding
policies, verification, relocation, and distinct remove versus delete-data
actions. It exposes useful errors and on-demand files, peers, trackers, pieces,
and speed information, including tracker editing and reannounce. Keyboard use,
accessibility, shell activation, and recovery after restart are part of the product.

Design the interface around these tasks and libtorrent's capabilities. Remote
servers, browser access, interchangeable engines, other platforms, and a search
panel are outside scope. A local torrent filter remains a normal interface
operation. History, automation, and blocklists need an identified user requirement
before they become implementation work. The initial release includes automatic
port mapping (UPnP/NAT-PMP), an editable listen port, completion notifications,
and preventing idle sleep during active downloads on mains power. Port mapping,
notifications, and idle-sleep prevention start enabled and can be turned off.
Encryption follows libtorrent's defaults without a separate setting. Proxy
configuration is outside the initial scope.

The [payload-ownership policy](engine.md#payload-ownership) gives each file one
torrent owner. Simultaneous shared-file seeding and automatic replacement of an
existing torrent are outside the initial scope. Multiple trackers on one torrent
remain supported. Existing files can be reused after their previous torrent is
removed without deleting data and the engine has released its storage claims.

## Two processes, one download authority

The native engine remains running while downloads may run. It contains libtorrent,
persistence, the tray, the splash window, and the local pipe endpoint. Opening the
interface starts or activates a separate WinUI process; closing its window lets
that process exit while transfers continue.

```mermaid
flowchart LR
    subgraph UI["WinUI process · while its window is open"]
        Views["Views, display projections, drafts"] --> Client["Pipe adapter"]
        Views --> Table["Synapse TableView"]
    end
    Client <-->|"commands, replies, snapshots, control notifications"| Pipe
    subgraph Engine["Native engine · while downloads may run"]
        Pipe["Pipe adapter"] --> Commands["Application commands"]
        Tray["Tray and activation"] --> Commands
        Commands --> Torrents["libtorrent"]
        Commands --> Saved["Persistence"]
        Tray --> Launch["UI launch and splash"]
    end
    Launch -.->|"starts or activates"| Views
```

Two processes allow the UI runtime to disappear and a UI crash to leave transfers
running. A separate tray process would add another resident executable and
supervision path without earning its cost. The engine loads neither .NET nor
WinUI. It builds as one executable; focused native checks can link the same
implementation units. Its startup, commands, and shutdown also work without tray
windows or UI files, so headless checks use the production implementation.

| Responsibility | Sole owner |
| --- | --- |
| Swarm and transfer execution, torrent metadata | libtorrent inside the engine |
| Application commands, torrent membership, queue and transfer policy | Engine |
| Durable identities, saved settings, resume checkpoints | Engine persistence |
| Payload ownership during transfers, file operations, and recovery | Engine |
| Tray, splash, activation routing, UI launch, application lifetime | Engine |
| File/link handler and start-at-sign-in registration | Engine registration owner |
| Installation files, prerequisites, shortcuts, and uninstall entry | Installer |
| Background preferences and selected application language | Engine |
| Framing, decoding, request correlation, connection failures | Pipe adapter at each endpoint |
| Display copies, filters, drafts, dialogs, window placement, UI-only preferences | WinUI |
| Table geometry, sorting, selection, and gestures | Synapse TableView |
| Resource lookup and live text refresh | One localisation component per process, following the [localisation contract](localisation.md#ownership-and-live-behavior) |

Keep work together when it changes together. Use a small interface that hides
a real decision; remove forwarding layers that add no policy or useful isolation. Add an
abstraction when a concrete caller needs it, not to permit hypothetical engines,
platforms, or test substitutes.

Immutable snapshots, saved checkpoints, and display projections serve different
lifetimes under the same state authority. Two language codecs implement the same
protocol.

## Command and presentation flow

Tray actions and pipe requests call the same engine operations. Transport checks
belong to the adapter; torrent-state validation and download policy belong to the
engine. The tray does not call its own process through IPC.

WinUI presents confirmed state and pending work. File pickers, clipboard actions,
and opening Explorer use Windows directly; changes to engine-owned files go
through the engine.

The product host maps a completed engine snapshot into display rows keyed by
durable torrent identity, then supplies its filtered projection to TableView.
Keep row instances stable and apply live property updates on the UI dispatcher,
following the [TableView update contract](../winui3/docs/tableview-contract.md#53-source-identity-and-update-contract).
Replacing the source on every transfer tick would cancel queue dragging;
ordinary telemetry updates the row values. Table sorting changes presentation
order; a queue reorder event becomes a
request to the engine's command owner. Selection and drafts remain presentation
state, reconciled against confirmed membership.

The close/reopen path makes the intended lifetime visible:

```mermaid
sequenceDiagram
    actor User
    participant Engine as Native engine
    participant UI as WinUI process
    User->>Engine: Open application
    Engine->>UI: Start or activate window
    UI->>Engine: Connect and request snapshot
    Engine-->>UI: Confirmed state and session identity
    UI->>Engine: Torrent command
    Engine-->>UI: Acceptance or rejection
    UI->>Engine: Request outcome / refreshed state
    Engine-->>UI: Pending, completed, or failed
    User->>UI: Close window
    Note over UI: Close normally; protect unfinished input if present
    Note over Engine: Transfers and persistence continue
    User->>Engine: Open again
    Engine->>UI: Start window
    UI->>Engine: Reconnect and reconcile snapshot
```

Application Exit follows the coordinated [shutdown](engine.md#closing-and-shutdown)
path.

## Reuse and source layout

| Location | Purpose |
| --- | --- |
| `engine/` | Planned native engine, created in the Native engine core milestone. |
| `winui3/src/Synapse/` | Existing reusable control library and the only TableView. |
| `winui3/samples/` and `winui3/tests/` | Control consumers and focused verification. |
| `winui3/src/TinyTorrent/` | Planned WinUI product host, created in the First product connection milestone. |
| `resources/locales/` | Planned canonical translation sources for the first product surfaces; see [localisation](localisation.md). |

Keep the original `../TinyTorrent` repository untouched. Reuse its native
controls, layouts, or algorithms only after identifying their current job and
removing old client dependencies. Keep one product host and reference Synapse.
Transmission, its daemon supervisor and RPC client, TypeScript concepts, browser
architecture, and web state models do not define this product.

## Dependencies and cost

Runtime memory while downloading or seeding with WinUI closed is the primary
resource measure. Correctness, durable downloads, required features, and useful
throughput remain constraints. Package size is secondary. Closing WinUI releases
its process and engine work retained solely for that UI.

Retain libtorrent and the networking/crypto dependencies needed for torrent
interoperability. Removing application HTTP/RPC does not remove HTTPS trackers,
web seeds, incoming peer connections, TCP/uTP, or useful discovery.

Use the latest stable upstream libtorrent release when the native engine build
is established, independently of the version in `../TinyTorrent`. Resolve the
release from [upstream](https://github.com/arvidn/libtorrent/releases/latest),
then pin its exact version/commit and feature set for reproducible builds. Check
for newer stable releases when updating dependencies; do not silently float the
build on a moving branch. Recheck version-sensitive engine assumptions on upgrade.
The automatic disk policy is recorded in [engine settings](engine.md#disk-write-caching).
Runtime packages need a concrete job; test frameworks and build tools stay out
of the shipped dependency graph.

Remove unnecessary retained work and duplicate data before constraining useful
transfer buffers or peer activity. Bound queues and retained data at their existing
owners. Each worker has identifiable blocking work and an idle wait. DLLs, shared
runtime installations, and single-file bundles do not by themselves prove lower
running memory.

## Installation and updates

Ship an unpackaged application with a small per-user Inno Setup installer from
the project's release page. The Microsoft Store is not required. Use
[non-administrative install mode](https://jrsoftware.org/ishelp/topic_setup_privilegesrequired.htm)
for the application under `%LOCALAPPDATA%\Programs\TinyTorrent`; engine data
lives separately under `%LOCALAPPDATA%\TinyTorrent`. Resolve these locations
through Windows known folders.

Publish WinUI as framework-dependent. Ship the engine, product host, Synapse,
resources, and required application dependencies, without app-local copies of
the shared WinUI or .NET runtimes. Reuse compatible installed runtimes; fetch
missing prerequisites directly from Microsoft's official distribution. Check
the supported architecture and version before launch. The release's actual
dependency metadata determines which [.NET runtime](https://learn.microsoft.com/en-us/dotnet/core/install/windows)
is needed; WinUI alone does not imply the WPF/Windows Forms Desktop Runtime.
Include required Visual C++ runtime checks from the
[Windows App SDK deployment guide](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/deploy-unpackaged-apps).
Prerequisite installation can need elevation or be blocked by machine policy;
report that outcome instead of promising every machine a no-admin installation.
Cancelling a prerequisite must leave a retryable installation, not launch a
broken UI. Updating TinyTorrent does not redownload an already compatible runtime.

First installation offers opening torrents with TinyTorrent and starting at
sign-in, both selected by default. Setup performs the registration and, when
Windows requires the person's default-app choice, takes them to that choice.
Declining it leaves the application installed and usable. The installer calls
the engine's [registration operations](engine.md#windows-registration);
Preferences calls the same owner. Upgrades preserve disabled registrations and
existing default-app choices without repeating setup prompts. The installer owns
the engine-targeting shortcut and Installed apps entry, not a second handler writer.
Uninstall requests coordinated Exit, unregisters TinyTorrent, and removes its
program files. It leaves downloads, saved engine data, and shared runtimes intact.

The installer does not silently add firewall rules or change network profiles.
[Windows Firewall](https://learn.microsoft.com/en-us/windows/security/operating-system-security/network-security/windows-firewall/rules)
may prompt for inbound access; a prompt or approval is not guaranteed by policy
or account permissions. Continue using available outgoing connections when
inbound access is blocked. Port mapping does not bypass the host firewall.

Updates use the same release installer, after the coordinated
[Exit](engine.md#closing-and-shutdown). Stage and validate a complete release
before replacing it; never launch an engine/UI pair from different releases.
Keep an interrupted update recoverable without modifying downloads. A Downloads
and updates action opens the release page on request. There is no periodic
release polling, silent update, or resident updater in the initial release.
Sign public release artifacts; validate installation, upgrade, and uninstall
with the chosen prerequisites before publication. A future winget listing can
reference this same installer without becoming a Store dependency.

## First implementation

Build one narrow path: start the engine, connect WinUI, show real torrent state,
issue a command, close WinUI, and reopen it while the transfer continues. Include
disconnect and orderly exit behavior. Deliver the path in dependency order;
[testing](testing.md) governs the evidence required and desktop execution.

| Milestone | What it establishes | Completion evidence |
| --- | --- | --- |
| Native engine core | Real libtorrent state, durable identity, one command and persistence owner, [bounded diagnostics](engine.md#diagnostics), and [storage claims before payload writes](engine.md#payload-ownership). | A narrow real transfer path; saved membership and intent survive restart; failed storage is not reported as saved; the first [resource check](testing.md#resource-checks). |
| First product connection | One concrete pipe contract and WinUI host referencing Synapse, with the [shared text path](localisation.md) on the first screen and its controls. Resolve the [source-lifetime, keyboard-location, and UI Automation gaps](../winui3/docs/tableview-implementation.md#other-known-integration-gaps). | Real snapshots and a command round trip; reconnect reconciles pending work without replaying uncertain destructive commands; focused evidence for the affected table interactions. |
| Independent lifetime and live language switching | Engine activation/tray, UI close/reopen, startup failure feedback, coordinated Exit, [Windows registration](engine.md#windows-registration), live text refresh, and RTL header navigation. | Closing WinUI leaves a transfer running; reopening restores confirmed state; engine restart reattaches a surviving UI; registration changes work through the same owner; switching language updates existing views, controls, and tray without losing input or breaking keyboard navigation. |
| Broader product journeys | Addition preview, files and inspector edits, settings, relocation, and removal. Establish the [preview guard](engine.md#addition-and-identity) before magnet preview and [file recovery](engine.md#removal-and-relocation) before deletion or relocation. | Focused evidence for each new data-integrity or interaction risk, followed by the whole-application milestone review. |
| Distribution | The selected [installer and runtime delivery](#installation-and-updates), including notification and power behavior with WinUI closed. | Fresh installation with missing prerequisites, preserved preferences on upgrade, taskbar/handler/sign-in activation, and safe uninstall; no downloads or shared runtimes removed. |

Choose concrete storage, message layouts, and project boundaries as that path
requires them. Add complexity only after naming the behavior that the simpler
arrangement cannot provide.

Revisit a decision when evidence contradicts its reason. A single process would
keep WinUI resident; shared memory would add synchronisation and lifetime costs.
Neither is needed now. If custom serialization or lifecycle code becomes harder
to maintain than a platform facility, reconsider that choice at its owner rather
than wrapping it in more layers.

## Decisions still open

Resolve each decision before its dependent work, then record the choice at its
owner and update this live list. Continue independent implementation work while
unrelated questions remain open.

| Decision | When it matters | Owner |
| --- | --- | --- |
| Store, commit ordering, and checkpoint retry | Before confirming additions, removals, or settings as saved. | [Engine persistence](engine.md#persistence-and-file-safety) |
| Storage claims and path identity | In the first engine milestone, before enabling payload writes. Choose the claim representation and filesystem checks, including aliases and metadata arriving after acceptance. | [Payload ownership](engine.md#payload-ownership) |
| File-operation mechanism and recovery records | Before deletion or relocation. Establish safe move scope, collision handling, and recovery from partial work. | [Removal and relocation](engine.md#removal-and-relocation) |
| First messages and bounds | With the first C++/C# round trip. Define the byte layouts, units, version checks, and limits once. | [Protocol encoding](protocol.md#encoding-and-validation) |
| Command correlation and outcome retention | Before reconnecting around accepted work. Choose identifiers and retention bounds; unfinished file recovery outlives outcome-record eviction. | [Protocol outcomes](protocol.md#outcomes-and-reconnection) |
| Activation and close handshake | With the first engine/UI launch. Define concrete messages and ordering for direct launch, adoption of a surviving UI, readiness/failure feedback, foreground permission, and edit settlement before Exit. | [Engine lifetime](engine.md#startup-and-activation) |
| Catalogue generation and live refresh | With the first localised control and product surface. Define build outputs, standalone fallback, and refresh of existing text. | [Localisation](localisation.md) |
| Refresh cadence and dispatcher batching | With the first snapshot-fed table. Choose the update frequency and batch limits for the stable row mapping. | [Presentation flow](#command-and-presentation-flow) and [snapshots](protocol.md#snapshots-and-detail) |
| Native build and CPU targets | With the first native build. Pin the toolchain, dependencies, crypto support, and supported targets in build files. | [Dependencies](#dependencies-and-cost) |
| Release prerequisites and installation mechanics | With the first release build. Pin supported runtime versions/architectures and official downloads, signing configuration, and recoverable upgrade ordering for the selected installer. | [Installation and updates](#installation-and-updates) |
| Preferences, screen layouts, and tray contents | Before each affected journey. Choose the controls needed for the agreed scope. | [Product scope](#product-and-scope), [engine](engine.md), and [interface](interface.md) |
| Table row appearance | With hands-on testing before product integration. Review row hover, selection accent, corner shape, and focus treatment. Retain the existing appearance until that review; invisible keyboard location remains an accessibility gap. | [TableView visual contract](../winui3/docs/tableview-contract.md#8-rendering-layout-and-visual-language) |
