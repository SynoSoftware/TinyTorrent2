# Documentation guide

Read in this order when joining the project. Afterwards, use the document that
owns the decision being changed. Filenames describe subjects; the order lives
here so moving a topic does not require renaming its links.

| Order | Document | Authority |
| --- | --- | --- |
| 1 | [Architecture](contracts/architecture.md) | Product scope, process responsibilities, projects and source layout, dependencies, and installation. |
| 2 | [Engine](contracts/engine.md) | Shared state and persistence rules, with conditional references for addition, files and recovery, lifetime, settings and notifications. |
| 3 | [Pipe API](contracts/pipe-api.md) | Local communication, snapshots, and connection outcomes. |
| 4 | [Localisation](contracts/localisation.md) | Text sources, language selection, live updates, and formatting. |
| 5 | [UI](contracts/ui.md) | Shared UI policy and conditional references for workspace, Settings, torrent interaction and feedback. |
| 6 | [Testing](agents/testing.md) | Which evidence earns its cost and what it establishes. |
| 7 | [Naming and structure](agents/naming.md) | Names and placement of types, files, folders, namespaces, and resource keys. |

Before compiling, launching a built executable or resolving build failures,
read [compilation](agents/compilation.md). It owns when a task compiles, the shared
build script and failure handling. Before planning work that other agents
will do, read [coordination](agents/coordination.md). It owns how a lead plans,
splits, briefs and checks work across agents.

The architecture's [ownership decisions](contracts/architecture.md#ownership-decisions)
record settled ownership and deferred design choices. GitHub issues own actionable work,
implementation status and remaining acceptance criteria; local review artifacts
are not a second backlog.

The architecture owns the [live decision list](contracts/architecture.md#decisions-still-open).
Update it as choices are made; historical reviews do not supply active requirements.

For subtitle work, read the [subtitle contract](contracts/subtitles.md) for shared
outcome, ownership and recovery rules, then its change index for configuration,
matching, file handling or supplier details.

For Library work, read the [Library contract](contracts/library.md) for shared
ownership, membership and SQLite rules, then follow its change index for file
search, providers, presentation or lifecycle. Its
[prototype implementation review](contracts/library/presentation.md#prototype-implementation-review)
tracks the pending Inspector and navigation choices. Library and subtitles
share one window-lifetime SQLite owner; the [database contract](contracts/database.md)
owns schema initialization, versioning, connection lifetime and the rationale
for durable and reconstructed state.
[Search protection](contracts/search-protection.md) owns shared suppression, pacing
and storage rules for Library and subtitle requests. Its change index selects
the retry/reset and IMDb local-data references.

The [disk discovery plan](plans/13-disk-discovery-plan.md) defines the optional NTFS
C++ helper for video, music and subtitles, plus targeted lookup of missing torrent
files: its purpose, shared pipe interface, DTOs, memory budget, current-file
visibility and the engine's recovery hold. Two independent opt-ins start it when
Library needs discovery or with the engine for movement tracking. Tracking searches
only after files are noticed missing; neither option builds an all-files index.
It releases query memory while unused and exits with the engine. Its
implementation is in progress.

[IMDb datasets](contracts/imdb-datasets.md) owns the Library provider that reads IMDb's
downloadable datasets from a local SQLite file and shows their ratings for
every provider's movies.

[Background work](contracts/background-work.md) owns the shared reporting of app-wide
long operations and their progress and cancellation in the status bar.

[Peer location](contracts/peer-location.md) owns the peers' Location column: the DB-IP
country data installed with the app, the city data the person downloads
through the same dataset path as IMDb data, and the flags.
It also owns the native peer map and its estimated country distribution.

[Licences](contracts/licences.md) holds release rights, provider attribution and the
unresolved distribution questions.

The [ring copy research](plans/ring-copy-plan.md) keeps the evidence and the
design of a copy engine of the engine's own, for moves between hard disks. It
will not be implemented.

The [torrent control plan](plans/torrent-control-plan.md) keeps what remains
of the storage controller work, whose steps 0 to 5 are implemented: the
owner's dependency rebuild, the checks that wait for it, and step 6, disk work
per drive inside libtorrent, which needs separate authorization.

The [communication plan](plans/communication-plan.md) is paused. It holds the
scoped-subscription design for torrent detail, which the owner has not decided
to implement; its implemented first stage lives in the protocol and engine
contracts.

For TableView work, continue with the [table glossary](../lib/TableView/CONTEXT.md),
[TableView contract](../lib/TableView/docs/tableview-contract.md), and
[TableView implementation](../lib/TableView/docs/tableview-implementation.md).
The [product glossary](../CONTEXT.md) defines terms, not additional requirements.
[Root instructions](../AGENTS.md), [WinUI instructions](../app/AGENTS.md), and
[TableView instructions](../lib/TableView/AGENTS.md) route contributors to these documents.

## Where documents belong

Keep repository instructions and the product glossary at the root. `docs/`
has three folders:

- `contracts/` describes how the product works now. It is the authority for
  product behavior and changes alongside it.
- `agents/` holds the working rules for building, testing, naming, comments
  and coordinating agents.
- `plans/` holds only work that is open or in progress. A plan's filename
  starts with a two-digit number, given when its work starts, so the folder
  lists plans in order of implementation. When the work is done, its live
  rules move into the contract that owns them and the plan is deleted, because
  a finished plan read as current misleads; git history keeps it.

TableView's contract, source map, glossary,
and control history stay beside the library in `lib/TableView/`. Folder-specific
`AGENTS.md` files contain local guidance and inherit the root rules. Keep licence
notices with their assets or dependencies. Link between these owners rather
than keeping copies in both locations.

## Status and authority

The architecture lists the [existing projects](contracts/architecture.md#projects-and-source-layout).
The installer's installed journeys remain targets; the [release command](../installer/README.md)
generates candidates, and a contract alone is not implementation evidence.
The control's implementation document identifies known gaps against its contract.

Each rule has one home. Link to it from another subject instead of copying it.
Product scope belongs to the architecture; a control specification does not add
product features, and a UI design does not choose storage or command semantics.
Testing owns validation scope across the repository.

Change the relevant contract alongside an intentional design change. A mismatch
between code and contract is a finding to resolve, not permission to rewrite
the contract merely to describe whatever the code currently does.

## Historical material

Prototype files describe their reviewed versions; current interface rulings
decide which parts remain applicable.

[Archived TableView documents](../lib/TableView/docs/archive/README.md) preserve
earlier control construction reasoning. These are reference material, not
current plans, implementation permissions, or proof of current behavior. Useful
ideas must satisfy the current owner's contract before reuse.

Build configuration and dependencies live in project/build files. Do not copy a
toolchain inventory or measured performance result into a plan as though it were
a permanent property of the product.
