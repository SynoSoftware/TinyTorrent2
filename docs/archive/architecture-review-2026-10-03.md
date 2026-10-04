# Architecture plan review — historical

> Historical assessment, preserved before the subsequent usability revisions.
> Its draft-conflict and disk-backend recommendations have been superseded.
> Use the [active architecture plan](../architecture.md), its
> [live decision list](../architecture.md#decisions-still-open), and the subject
> contracts for current requirements. This file is not an implementation backlog.

Reviewed 2026-10-03 against this checkout's source and the active architecture,
engine, protocol, localisation, interface, and testing contracts. This is a dated
assessment, not an additional contract. [Current architecture](../architecture-current.md)
records what exists; [proposed architecture](../architecture.md) owns the direction.

## Assessment

The plans are consistent about their central decision: the engine owns torrent
state and application lifetime, WinUI owns presentation and drafts, and Synapse
owns table mechanics. Retain that split. Most planned product modules are absent,
so the immediate work is to establish one real engine-to-window path, rather than
refactor a product that already exists.

The strongest existing module is TableView. Its interface hides source
reconciliation, geometry, selection, and gestures from two different sample
hosts. The deletion test supports keeping it: removing those owners would move
their complexity into each host. No inspected forwarding module justifies an
additional extraction for testability alone.

The selected shape is not an end-to-end implementation specification. Review
advice exposed missing behavior rules as well as choices intentionally left for
implementation. The former are now recorded at their contract owners below;
the latter remain open. No performance conclusion follows from this review.

## Findings and proposed work

### 1. Establish the engine command owner with real persistence — Strong

**Current:** the [engine contract](../engine.md) defines state transitions and
recovery, but no native implementation exists. The sample's mutations affect
only demonstration rows.

**Proposed:** build the narrow path in
[First implementation](../architecture.md#first-implementation). Put application
state changes and operation outcomes behind one engine module interface; tray
actions and the pipe adapter use it. Keep libtorrent handles, late alerts, storage
commit, and path claims within that module's implementation as those operations
are added. Do not prescribe a separate project or interface per responsibility.

This gives locality to remove/re-add and failed-write handling and gives both
callers leverage from the same operation. Engine behavior can be checked through
the production command interface without a window. The specific risks that earn
checks are a confirmed removal returning after restart, an old completion
affecting a new addition, and an uncertain delete being replayed.

### 2. Complete the existing localisation seam — Strong

**At review:** `TableResources` used a static resource loader, and `TableColumn`
did not notify changes to `DisplayName`. Their current counterparts are
[Strings](../../lib/TableView/src/Strings.cs) and
[Column](../../lib/TableView/src/Columns/Column.cs). Resource generation alone
cannot update already-built headers, menus, or accessibility text. This is a confirmed implementation gap,
already recorded in the [control map](../../lib/TableView/docs/tableview-implementation.md#known-localisation-gap).

**Proposed:** implement the existing [localisation contract](../localisation.md)
with the first product surfaces: one editable catalogue, generated native and
WinUI outputs, and one process-local lookup/refresh policy. Deepen Synapse's
internal text module so both standalone and product hosts use the same refresh
path. Preserve its complete English fallback and domain independence.

Locality means a control-text correction reaches every host. Depth means callers
do not rebuild schemas or controls to change language. Verify the actual failure:
an open control retains old text or loses selection/focus/drafts after a switch.
Use the targeted live-switch exercise in the contract, not assertions on wording
or a separate localisation framework.

### 3. Keep product projection at the host seam — Strong

**Current:** the [sample hosts](../architecture-current.md#sample-and-host-responsibilities)
already supply data and policy to the same TableView. There is no product
snapshot-to-row implementation to reuse.

**Proposed:** give the product host one projection from confirmed engine
snapshots to rows and one command path for its gestures and menus. The pipe
adapter owns communication failures; the engine owns operation legality;
TableView continues to own generic interaction. Choosing stable row instances
or replacement rows is a concrete host decision, not a reason for another
torrent database or table implementation.

This preserves leverage from Synapse and locality for reconnection and draft
reconciliation. Verify stale-state handling and durable identity where the real
host consumes snapshots. Do not add a substitute engine or duplicate the control
suite merely to exercise that seam.

The [table contract's Appendix A](../../lib/TableView/docs/tableview-contract.md#appendix-a--reference-integration-torrent-list)
is explicitly informative reference. Its daemon/RPC and optimistic queue examples
are not competing product requirements. No conflict needs reopening; their
authority has already been limited by the active documents.

## Decisions open at review

These are implementation questions within the selected design, not invitations
to revisit the process split or add frameworks. Resolve each before the code
that depends on it and record the answer at the linked owner.

| Decision | Why it matters / when to settle it | Owner |
| --- | --- | --- |
| Store, commit ordering, and checkpoint retry | Before claiming additions, removals, or settings are saved. Choose a representation that can enforce durable membership and reject late writes. | [Engine persistence](../engine.md#persistence-and-file-safety) |
| First message layouts and bounds | With the first C++/C# round trip. Specify byte order, operation codes, units, maximum lengths, identities, and version refusal in one implementation contract. | [Protocol encoding](../protocol.md#encoding-and-validation) |
| Operation correlation and retention | Before reconnecting around an accepted command. Choose correlation encoding and retention bounds for the now-explicit unavailable-outcome behavior; keep unfinished file recovery independent of result eviction. | [Engine work](../engine.md#state-and-work) and [protocol outcomes](../protocol.md#outcomes-and-reconnection) |
| Concrete activation and close handshake | With the first engine/UI launch. Reserve one UI launch, keep Save possible during draft resolution, and preserve the different meanings of window close and application Exit. | [Engine lifetime](../engine.md#startup-and-activation) and [control notifications](../protocol.md#one-local-connection) |
| Catalogue generation and live refresh | With the first localised control and product surface. Resolve generated build inputs, standalone fallback, and notification of existing text without schema reconstruction. | [Localisation](../localisation.md) |
| Product row lifetime and display refresh | With the first snapshot-fed table. Decide which bindable objects persist and where batches reach the UI dispatcher, preserving identity and draft ownership. | [Presentation flow](../architecture.md#command-and-presentation-flow) and [snapshots](../protocol.md#snapshots-and-detail) |
| Edit preconditions | With the first draft save. Choose the baseline representation and edit scope that detect stale changes without invalidating drafts on telemetry updates. | [Engine edits (subsequently revised)](../engine.md#committed-edits) |
| File-operation mechanism and recovery records | Before enabling deletion or relocation. Establish safe source/destination handling, collision refusal, and recovery under interruption; libtorrent's move flags alone do not prove safety. | [File safety](../engine.md#persistence-and-file-safety) |
| Native build and CPU targets | With the first native build. Select toolchain, dependency pinning, crypto support, and supported targets in build files; Synapse's platform list does not settle engine support. | [Dependencies](../architecture.md#dependencies-and-cost) |
| Installation and registration | Before implementing installed activation and sign-in behavior. Decide distribution form, runtime delivery, state location, handler registration, and update approach. | [Architecture](../architecture.md) and [activation](../engine.md#startup-and-activation) |
| Preferences, screen layouts, and tray contents | Before implementing each affected journey. Specify the actual settings and interactions; proxy, port mapping, encryption controls, and completion notifications raised by reviews remain scope questions, not approved additions. | [Product scope](../architecture.md#product-and-scope), [engine](../engine.md), and [interface](../interface.md) |
| Background diagnostics | With the first engine failure path. Decide what diagnostic evidence remains available with the window closed, including destination and retention; no logging framework has been selected. | [Engine](../engine.md) |

Native dependency pinning and the selected disk behavior still need a real build
and focused evidence. The [engine's selected baseline](../engine.md#disk-write-caching)
is a plan, not a measured property of this checkout. Product packaging likewise
has to ship the two compatible executables together; the sample's deployment
settings do not choose its final form.

## Disposition of external review advice

The supplied model reviews were treated as findings to verify, not owner
instructions. The pinned source was read directly where upstream behavior
affected a recommendation.

| Advice | Disposition |
| --- | --- |
| Protect normal downloads as well as destructive work. | Accepted as a missing rule; [payload ownership](../engine.md#payload-ownership) now covers accepted torrents, path resolution, and restart. |
| Commit removal before deletion; protect both ends of relocation and recover partial moves. | Accepted; [removal and relocation](../engine.md#removal-and-relocation) now states ordering and recovery. The pinned move contract confirms the overwrite race and unrelated-source-file risk. |
| Prevent stale drafts from overwriting newer choices. | Accepted at this review; [engine edits](../engine.md#committed-edits) has subsequently been revised. |
| Define expired results and delivery of asynchronous outcomes. | Accepted; [protocol outcomes](../protocol.md#outcomes-and-reconnection) uses summary snapshots and explicit unavailable outcomes, separately from unfinished-file recovery. |
| Windows defaults to pread in 2.1.2, not mmap. | Verified in the pinned `session.cpp`. [Disk settings](../engine.md#disk-write-caching) now explicitly selects mmap for the existing baseline and requires comparison with the default; a lower-memory claim for either backend remains unmeasured. |
| Name the magnet-preview guard. | Accepted; [addition](../engine.md#addition-and-identity) names `default_dont_download` and handles explicit priorities separately. The no-payload guarantee still needs a real check. |
| Replace live language switching with reopening WinUI. | Not adopted: it changes the explicit [product requirement](../localisation.md#ownership-and-live-behavior). The implementation cost is acknowledged, not grounds to silently remove it. |
| Remove paging based on estimated frame size and speed. | Not adopted as a blanket change. [Paging](../protocol.md#snapshots-and-detail) remains conditional on real data exceeding bounds; the first small round trip needs none. Estimates do not establish latency or safe size limits. |
| Choose plain files or SQLite now. | Deferred to the first durable path. The reviews recommend different stores; neither proves the required crash and write-order behavior by its name alone. |
| Delete diagnostic probe pages. | Outside this contract update. Existing source and sample cleanup need their own reason; this review does not remove them. |
| Require a fixed sequence of skills before coding. | Not adopted as project policy. Use skills for the actual task; repository contracts still own scope and validation. Clarification is useful when a decision is material, not a mandatory interview before every change. |

## First priority and review limits

Start with the engine command/persistence path and its first WinUI caller. Include
the shared text path as those surfaces appear. This resolves the largest unknowns
while preserving the existing deep control module. Broader screens, a generic
scheduler, schema compiler, alternate backend, or extra resident process would
increase work before that path has evidence.

This review inspected source, project references, active contracts, archive
status, and the relevant pinned upstream source for the supplied advice. The
repository has no commit history yet, so it cannot support a history
of frequently changing areas. The earlier repository was not audited. No build,
desktop test, rendering check, or libtorrent experiment was performed; none is
needed to validate documentation edits. Product behavior and resource costs
remain unverified until implementation and the evidence required by
[testing](../testing.md).
