# Documentation guide

Read in this order when joining the project. Afterwards, use the document that
owns the decision being changed. Filenames describe subjects; the order lives
here so moving a topic does not require renaming its links.

| Order | Document | Authority |
| --- | --- | --- |
| 1 | [Current architecture](architecture-current.md) | Source-backed map of the existing projects, runtime, and control data flow. |
| 2 | [Target architecture](architecture.md) | Product scope, responsibilities, dependencies, reuse, and implementation direction. |
| 3 | [Engine](engine.md) | Torrent state, durable changes, activation, and process lifetime. |
| 4 | [Protocol](protocol.md) | Local communication, snapshots, and connection outcomes. |
| 5 | [Localisation](localisation.md) | Text sources, language selection, live updates, and formatting. |
| 6 | [Interface](interface.md) | Product journeys and native Windows interaction and presentation. |
| 7 | [Testing](testing.md) | Which evidence earns its cost and what it establishes. |

The architecture owns the [live decision list](architecture.md#decisions-still-open).
Update it as choices are made; historical reviews do not supply active requirements.

For Synapse work, continue with the [table glossary](../winui3/CONTEXT.md),
[TableView contract](../winui3/docs/tableview-contract.md), and
[TableView implementation](../winui3/docs/tableview-implementation.md).
The [product glossary](../CONTEXT.md) defines terms, not additional requirements.
[Root instructions](../AGENTS.md) and [WinUI instructions](../winui3/AGENTS.md)
route contributors to these documents.

## Status and authority

The engine, its pipe, the new product UI, and shared live localisation are target
contracts. Their documentation is not evidence that they are implemented.
Synapse, its sample, and its tests are existing code; the implementation document
identifies known differences from the target contract.

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

[Archived WinUI documents](../winui3/docs/archive/README.md) preserve earlier
construction reasoning and interface reviews. They are reference material, not
current plans, implementation permissions, or proof of current behavior. Useful
ideas must satisfy the current owner's contract before reuse.

Build configuration and dependencies live in project/build files. Do not copy a
toolchain inventory or measured performance result into a plan as though it were
a permanent property of the product.
