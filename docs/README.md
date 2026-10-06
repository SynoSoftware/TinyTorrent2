# Documentation guide

Read in this order when joining the project. Afterwards, use the document that
owns the decision being changed. Filenames describe subjects; the order lives
here so moving a topic does not require renaming its links.

| Order | Document | Authority |
| --- | --- | --- |
| 1 | [Current architecture](architecture-current.md) | Source-backed map of the existing projects, runtime, and control data flow. |
| 2 | [Target architecture](architecture.md) | Product scope, responsibilities, dependencies, reuse, and the [implementation plan](architecture.md#first-implementation). |
| 3 | [Engine](engine.md) | Torrent state, durable changes, activation, and process lifetime. |
| 4 | [Protocol](protocol.md) | Local communication, snapshots, and connection outcomes. |
| 5 | [Localisation](localisation.md) | Text sources, language selection, live updates, and formatting. |
| 6 | [Interface](interface.md) | Product journeys and native Windows interaction and presentation. |
| 7 | [Testing](testing.md) | Which evidence earns its cost and what it establishes. |
| 8 | [Naming and structure](naming.md) | Names and placement of types, files, folders, namespaces, and resource keys. |

The [selected refactoring plan](architecture/proposed/README.md) consolidates
the architecture reviews and delivery work. The active contracts above remain
the authority for product behavior.

The architecture owns the [live decision list](architecture.md#decisions-still-open).
Update it as choices are made; historical reviews do not supply active requirements.

For TableView work, continue with the [table glossary](../lib/TableView/CONTEXT.md),
[TableView contract](../lib/TableView/docs/tableview-contract.md), and
[TableView implementation](../lib/TableView/docs/tableview-implementation.md).
The [product glossary](../CONTEXT.md) defines terms, not additional requirements.
[Root instructions](../AGENTS.md), [WinUI instructions](../app/AGENTS.md), and
[TableView instructions](../lib/TableView/AGENTS.md) route contributors to these documents.

## Where documents belong

Keep repository instructions and the product glossary at the root. Product
contracts and plans live in `docs/`; TableView's contract, source map, glossary,
and control history stay beside the library in `lib/TableView/`. Folder-specific
`AGENTS.md` files contain local guidance and inherit the root rules. Keep licence
notices with their assets or dependencies. Link between these owners rather
than keeping copies in both locations.

## Status and authority

The native engine and first WinUI download path exist alongside TableView,
Lucide, the sample and control tests; see [current build integration](architecture-current.md#build-integration)
and [implementation evidence](implementation.md). Later milestones and the
installer remain targets; a contract alone is not implementation evidence.
The control's implementation document identifies known gaps against its contract.

Each rule has one home. Link to it from another subject instead of copying it.
Product scope belongs to the architecture; a control specification does not add
product features, and a UI design does not choose storage or command semantics.
Testing owns validation scope across the repository.

Change the relevant contract alongside an intentional design change. A mismatch
between code and contract is a finding to resolve, not permission to rewrite
the contract merely to describe whatever the code currently does.

## Historical material

The [architecture review of 2026-10-03](archive/architecture-review-2026-10-03.md)
preserves the assessment before the usability revisions, including superseded
choices and review dispositions. Consult the active contracts for current behavior.

The later [skill audit](archive/skill-audit-2026-10-03.md) records additional
source-backed findings, their disposition, and the limits of the review.

[Archived TableView documents](../lib/TableView/docs/archive/README.md) preserve
earlier control construction reasoning. The [archived interface review](archive/interface-review.md)
preserves earlier product presentation work. These are reference material, not
current plans, implementation permissions, or proof of current behavior. Useful
ideas must satisfy the current owner's contract before reuse.

The [historical torrent table reference](archive/torrent-table-reference.md)
preserves the old host integration formerly embedded in the table contract.
The [archived TableView API proposal](archive/tableview-api-proposal.md) preserves
the design rationale and review record behind the current table API, and a map of
old torrent application files to their new owners.

Build configuration and dependencies live in project/build files. Do not copy a
toolchain inventory or measured performance result into a plan as though it were
a permanent property of the product.
