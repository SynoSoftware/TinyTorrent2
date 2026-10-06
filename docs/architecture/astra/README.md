# Architecture review — Astra

Review dated 2026-10-06, based on commit `8e92ce9` and the working tree inspected
during the review. These are source-backed proposals, not implemented changes
or replacements for the [active contracts](../../README.md).

The [complete report](review.html) contains two independent architect reviews,
a third review applying the owner's priorities, and the consolidated judgment.
It includes before/after diagrams, deferred proposals and their disposition,
implementation order, verification expectations, and GitHub tracking.

## Read the report

- [Consolidated recommendation](review.html#decision)
- [Proposed ownership and implementation](review.html#design)
- [Deferred issues revisited](review.html#deferred)
- [Delivery order](review.html#delivery)
- [Architect 1](review.html#architect-one)
- [Architect 2](review.html#architect-two)
- [Review against the owner's priorities](review.html#owner-review)
- [GitHub follow-ups](review.html#tracking)

## Selected proposals

| Proposal | Tracking |
| --- | --- |
| One active dialog lifetime, restoring unfinished editors when closing fails | [#115](https://github.com/SynoSoftware/TinyTorrent2/issues/115) |
| One presentation owner for accepted selection and inspector transitions | [#116](https://github.com/SynoSoftware/TinyTorrent2/issues/116) |
| Shared committed-edit behavior for tracker merge and explicit edits | [#117](https://github.com/SynoSoftware/TinyTorrent2/issues/117) |
| A concrete owner for speed-history state and aggregation, independently and at lower priority | [#118](https://github.com/SynoSoftware/TinyTorrent2/issues/118) |

Related details were added to the existing
[TableView batching issue #113](https://github.com/SynoSoftware/TinyTorrent2/issues/113)
and [scheduler redraw issue #114](https://github.com/SynoSoftware/TinyTorrent2/issues/114)
rather than creating duplicates. The parallel review's
[preference input-loss issue #112](https://github.com/SynoSoftware/TinyTorrent2/issues/112)
takes priority over discretionary structural cleanup. GitHub holds subsequent
issue status; this report preserves the review at the time it was written.

The selection criterion is easy-to-read, low-bloat code: refactor when it removes
duplicate decisions or clarifies ownership, and reject abstractions that merely
add indirection. Existing structure is not a reason to preserve difficult code.

## Evidence limits

The review did not change product code or run product builds, tests, desktop
applications or benchmarks. The working tree was changing concurrently, so
source line references may move. HTML structure and internal links were checked;
browser policy blocked local-file preview, so rendered layout remains unverified.
Primary diagrams use HTML/CSS; optional Mermaid and Tailwind use CDNs.
