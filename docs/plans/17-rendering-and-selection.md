# Rendering and selection

Status, 7 November 2026. The map handoff from `e723ae6` is implemented and
functionally validated; the owner reports much better pan/zoom. Preserve it. At
`4f069bb` every movable preparation already runs off the UI thread and the game
loop: Inspector's peer capture runs inside its worker, `MapModel.Update` stores a
population reference, picture resources are created and retired on the render
worker, traffic membership and dot input are built in `MapRenderer`, marker
preparation runs in the render pass, `MapWork.Prepare` runs on the pool and
`OnDraw` publishes one displayed value. No further "move it to a worker" task
exists; what remains on those threads is presentation or WinUI-owned work.

Responsiveness is not accepted. At `4f069bb` the worst navigation, marquee and
stationary-dot draw gaps are 322, 266 and 225 ms against a 16.67 ms frame period,
and the largest overlap inside each round's worst gap is a blocking full garbage
collection on the UI thread. Relocating work did not move those maxima. The open
work is the [pending selection correction](#pending-correction-map-selection-preparation),
then the [one next round](#next-round-read-row-keys-and-sort-keys-on-the-worker).

Purpose: faster input response and smooth animation, shown by before/after
evidence on the same workload. Scope: map rendering, interaction and
preparation; Inspector changes only supply the map's detached facts.
Communication is out of scope. Current behavior is owned by
[architecture](../contracts/architecture.md),
[peer location](../contracts/peer-location.md),
[torrent Inspector](../contracts/ui/torrents.md) and
[TableView](../../lib/TableView/docs/tableview-contract.md). The
[harness guide](../../app/tests/Performance.md) is the measurement entry point.

## Outcome

A torrent or peer selection acknowledges input without awaiting grouping,
projection preparation, geometry, rasterization or a replacement picture. The
map keeps presenting completed content while replacement content is prepared;
new content appears when available, and a completed handoff may contain partial
detail. Completing visual detail cannot delay input or presentation. Selection
semantics and displayed interaction targets stay correct while refinement is
incomplete.

Owner acceptance: a torrent click is visibly reflected in the next displayed
frame. Map navigation, stationary dot animation and marquee movement miss no
presentation deadlines and show no uneven jumps. Measure each separately; smooth
dots cannot compensate for stalled geography. Use the actual display refresh
rate; at 60 Hz the frame period is 16.67 ms.

Native selection, accessibility, drag gestures, unfinished-edit protection and
the retained Inspector remain intact. Native control updates still run on the UI
thread; detached calculation and native map rendering run elsewhere.

## Preserved handoff and settled decisions

- **Owner ruling: a frame never waits for complete visual refinement.** The game
  loop presents the available compatible picture and advances dots without
  joining grouping or rendering. The render worker publishes useful partial
  detail through the existing completion handoff, then refines it. Blocked
  grouping leaves navigation over the last completed facts; blocked rendering
  leaves input and presentation over the last completed picture.
- Keep the implemented owners and seams: Inspector and PeerMap accept intent on
  the UI thread; `MapWork` groups detached observations on a serialized worker;
  the `MapMotion` render worker owns one mutable renderer, native geometry and
  offscreen rasterization; the `MapMotion` game loop owns adopted completions,
  per-frame viewport, dots and presentation and publishes the one displayed
  inspection/view. One latest request, bounded completions, worker-only native
  lifetime; the UI never joins the worker. A generic scheduler, second device,
  tile cache or alternate representation needs a demonstrated failure first.
- Retain the raster transform, longitude wrapping and one-eighth viewport margin
  at every edge, and the three-target bitmap reuse. Static strokes and sampling
  may scale temporarily; markers, rings and dots keep their DIP sizes.
- **Owner ruling: preserve speed over the country artifact.** The broad gaps and
  overlaps between shaded countries remain visible and are accepted where a
  correction would slow navigation or complicate the renderer. The antialiasing
  coverage correction at `4f069bb` is retained; the gaps are not claimed fixed,
  and no further probe, geometry or handoff redesign is released for them.
- Themed location lights without outlined rings are done at `caad1b0`.
  Marker-layer caching stays deferred.
- Dots during navigation are optional: keep them moving when that preserves
  responsiveness, pause or hide them when it does not, and resume without
  another input when the gesture ends. Stationary dot animation keeps its own
  requirement.
- **Owner ruling, 8 November: work leaves the UI and drawing threads.** The
  goal is an app that answers input at once, so work that is not UI or drawing
  moves off those threads, and optimizing a block that can move is not
  accepted instead of moving it. The question for each piece of work is which
  owner it moves to, not whether it can move: the owner whose responsibility it
  already is, such as an app worker, a library policy or the engine. A move
  never turns the architecture upside down, for example by making a library
  depend on the app or the engine depend on the UI.
- TableView work is eligible only for an operation that can and should move off
  the UI thread: detached inputs, a concrete blocking cost removed, and simpler
  or equally clear ownership. Native WinUI updates stay on their owner. No
  publication redesign or broad table investigation, because that redesign was
  tried, reverted and never attributed. The one exception is the table's hit
  test for lost presses ([plan 18](18-keep-up-with-the-person.md)), because the
  owner ruled on 9 November that a lost click is a bug.
- Memory is a regression guard, not the goal. Dedicated memory tuning stays
  deferred; flag concrete, blatant waste when attribution shows it.
- Build, test and capture rules are owned by [testing](../agents/testing.md)
  and [compilation](../agents/compilation.md); this plan adds none.

## Workload

Keep the established stress workload: the frozen 50-torrent fixture with
159,810 known peer rows (`fixture.json` owns the exact counts), 1,000 distinct
places, descending speed sorting, city detail with the 1,000-arc limit, docked
layout, Replay/InputOnly. Do not reduce it to obtain a pass; it is where the
stalls reproduce. Compare the same fixture, profile, configuration, refresh
rate and DPI, against the preserved reference binary.

Distinguish two populations when attributing a cost. Peers represented on the
map are grouped places and arcs, built from detached facts off the UI thread.
Rows exposed to XAML are the peers table's rows, published through TableView on
the UI thread. A cost belongs to one of them; name which.

The fixture sets Inspector's `_locating` flag by reflection, so `LocatePeers`
never runs in the benchmark. Its two whole-population sets per pass are
ordinary-use waste ([issue 578](https://github.com/SynoSoftware/TinyTorrent2/issues/578)),
checked by source review and the existing focused location check, not by a run.

Ordinary use is a separate expectation: hundreds of rows, sometimes thousands,
rarely ten thousand. A correction that helps only at stress size and adds code,
state or memory at ordinary size is not retained.

## Acceptance

Acceptance uses the harness's existing response and draw endpoints on the stress
workload. GC duration explains a stall; it is not an acceptance endpoint,
because a GC rule alone would again leave responsiveness unverified.

| Endpoint (existing) | Target |
| --- | --- |
| Click-to-XAML completion, per click | p95 within one frame period; worst within three. The interval covers the whole UI turn after the click, including the selected torrent's peer-table publication; the click follow-up attributes what is inside it. |
| Navigation draw gap | p95 within one frame period; worst within three |
| Marquee sample gap | p95 within one frame period; worst within three |
| Stationary dot draw gap | p95 within one frame period; worst within three |
| Command-delivery worst | within three frame periods |
| First selected connections, first aggregate connections, post-click map settlement | not worse than the reference |
| Peak and end private commitment, peak working set | not above the reference |

The three-frame worst allowance is the default, not the outcome. The owner's
outcome says no missed deadline, which is one frame; three tolerates the
isolated misses that a full collection currently makes unavoidable, until a
round shows what one frame would cost.

These endpoints cannot certify the physical display. Displayed frame rate and
next-displayed-frame selection stay recorded as unverified until a
presented-frame endpoint exists; they are not a gate, because an unmeasurable
gate kept every round "not accepted" regardless of what changed.

### Deciding a correction

- Before building, the correction names the endpoint it predicts to change and
  the direction. A change without a predicted endpoint is not a performance
  correction and is judged as code only.
- One matched comparison against the preserved reference binary. Predicted
  endpoint improved and no other endpoint worse than the reference: retained,
  and the reference moves to it. Predicted endpoint not improved: not retained,
  because it did not deliver its intended result.
- Predicted endpoint improved but another endpoint worse: one more matched
  pair, and that pair decides. Worse again on that endpoint: not retained.
  Otherwise retained. No third run, no retry for a favorable maximum.
- Corrections already retained stay unless a matched comparison attributes a
  regression to them. A worse single run is not a confirmed regression; it
  prevents an unjustified revert and nothing more.
- Record every adverse observation. Keep UI elapsed time distinct from worker
  time and queueing; never add overlapping intervals. New in-process probes
  need matched diagnostic builds of both revisions; external recording may use
  the preserved binary directly.

## Pending correction: map selection preparation

Source review at `4f069bb` found one population-sized UI path left: both map
callers copied the table's selection on the UI thread, `PeerMap` compared every
selected peer and location on every map update, and selecting all 159,810 peers
made those UI operations population-sized. Ordinary torrent clicks do not take
this path, so the standard click comparison cannot validate it.

The correction is committed at `6f5f091`, unbuilt and unrun. Done when all of
the following hold:

1. Source. Both map callers pass the table's immutable selection without a UI
   copy. `PeerMap/Selection.cs` alone counts selected places on the thread pool
   with one active task and one latest pending request, independent of
   grouping. No `Peer` reference enters `MapMotion.Input`;
   `Presentation.Marks` only resolves destinations and estimated regions. The
   UI accepts a result only while the peer generation, selection reference,
   subject, detail and map lifetime still match; clearing publishes empty at
   once; unload never joins the task. TableView, geometry, the renderer and the
   picture handoff are unchanged. The
   [peer-location contract](../contracts/peer-location.md) carries the new
   selection text in the same change.
2. Review. No map-specific selected-peer enumeration remains in UI or
   presentation callbacks, pending work is bounded, and rapid select/clear,
   subject or detail replacement, location mutation during capture, blocked
   grouping and unload are traced.
3. Validation, one collected build. The existing map lifecycle capture passes
   with its stale-selection tripwire: block the worker, replace and clear the
   selection, release, and obsolete marks are rejected. A select-all in the
   peers table on the stress fixture highlights the selected places with no
   peer scan sampled on the UI or game-loop thread.
4. The owner's review of the recorded results.

The guaranteed result is the removal of these synchronous scans. It does not
claim shorter GC pauses or a large-selection latency figure.

## Next round: read row keys and sort keys on the worker

What is established. A blocking full collection on the UI thread overlaps most
of the worst stationary gap in every round:

| Round | GC suspension inside the worst stationary gap |
| --- | ---: |
| `555bd62` | 539 of 611 ms |
| `75af850` | 374 of 381 ms |
| `4f069bb` | 210 of 225 ms |

The trace identifies the collection's managed caller as the .NET
reference-tracker host (`GC/Triggered` under
`ReferenceTrackerHost.IReferenceTrackerHost_DisconnectUnusedReferenceSources`
on the UI thread). Its native trigger is not confirmed; WinUI's working-set
growth heuristic (`CCoreServices::TrackMemory`, 50 MiB) is a source-backed
candidate. The 1,105,232 handles and 200,468 finalizable objects come from GC
163 in the `555bd62` trace, before the runtime bookkeeping correction at
`75af850`; they are a historical sample, not a current inventory, and do not
show that every object is a COM wrapper.

Allocation owners, from the `78fb43f` allocation trace (sampled tick amounts,
not a census), read by owning frame from its raw ticks and by phase in its
preserved [phase grouping](../../artifacts/evidence/ui-profile-alloc-current-20261106-78fb43f/phase-grouping.md).
The journey is 59.3 s; its aggregate phase is 13.6 s with the whole population
loaded.

| Owner | Thread | Sampled allocation over the journey | Standing |
| --- | --- | ---: | --- |
| `Table.CaptureProjection`: the memoized row-key map, built by calling the host's key selector once per row | UI | 184 MiB of entries, 33 MiB of buckets | Moved to the worker by this round. The peers table sets a key, so every capture read 159,810 keys on the UI thread into a map grown from zero capacity. |
| `Schema.SortKey` capture: the sort-key map, built by calling the host's sort selector once per row | UI | 19.5 MiB of entries, 3.8 MiB of buckets | Moved to the worker by this round. |
| `Body.Projection.Prepare`: uniqueness and source-key sets, position map | worker | 198 MiB of set entries, 139 MiB of position entries, 78 MiB of buckets | Sized from their counts by this round, which removes the doubling chains; they never blocked the UI. |
| `Body.View.Apply`: COM-wrapper holder arrays | UI | 51 MiB | The .NET 10 duplicate-bookkeeping defect, removed by the runtime option at `75af850`. |
| `Peer[]` replacement lists in `ApplyPeers` reconciliation | worker | 119 MiB in the aggregate phase | No correction selected. |
| Inspector `Filter` capture | worker | about 24 captures in the aggregate phase, 1.7 per second | One 4.9 MiB value array per capture. The fixture delivers about one reply burst per second, so coalescing captures per burst cannot move an endpoint here. |
| `MapModel.Update` per-peer objects | worker | 209 MiB | Removed at `fc0916d4` and `6528d57`. |
| `Dictionary<DestinationKey, Destination>` rebuilt by `MapRenderer.Inspect` every render pass | render worker | 137 MiB | About 1,000 entries, generation 0, at pass rate during navigation. |

Issues [573](https://github.com/SynoSoftware/TinyTorrent2/issues/573) and
[578](https://github.com/SynoSoftware/TinyTorrent2/issues/578) are therefore
ordinary-use code fixes, judged as code: the per-reply recapture is real when
the engine answers one torrent at a time, and the geolocation sets are real
when geolocation runs, but this fixture shows neither. They ride in the next
build without a predicted endpoint.

The change, implemented in the working tree after the selection correction,
[issue 579](https://github.com/SynoSoftware/TinyTorrent2/issues/579): the
projection worker reads the host's row key and sort keys itself, once per row,
from the source snapshot the UI captured. `Table.CaptureProjection` captures
only UI-affine state (the source snapshot, displayed rows, realized indices,
hierarchy state and holds) and passes the raw selectors; `Body.Projection`
memoizes the keys and builds the sort comparer, and sizes its collections from
the counts they fill. One UI-thread read stays: a sort or layout request reads
its keys once when it is made, so a request whose selector fails is rejected
before the table changes, as the contract and its tests require; every later
projection of the accepted sort, which is the per-update path, reads on the
worker. Hosts are unchanged. This meets the TableView fence as written: detached inputs, a
concrete blocking cost removed, and clearer ownership, since the worker now
prepares everything from one captured snapshot.

The contract changes with it: key and sort-key selectors run on the worker and
read only state the host publishes safely, never UI objects. Every host
selector was audited: the peers table's key and sort keys read readonly fields
and the volatile record snapshot the map already reads off-thread; torrent and
tracker selectors read plain properties written on the UI thread, where a
stale read sorts a row by its previous value until the next update; file nodes,
Library entries, the sample and the tests read plain or immutable data.

Predicted endpoints, one build and one matched city/1,000 docked run against
the step-1 build as reference: click-to-XAML p95 and command-delivery worst
down, because the per-row capture no longer runs inside the UI turn that
follows a click or a peer update; induced full-collection count, total GC
suspension and peak private commitment not up, since the same bytes move to the
worker minus the doubling chains. Issues 573 and 578 ride in the same build
because the fixture cannot see either, so they cannot confound the comparison.
Issue 575 and the `Inspect` dictionary rebuild wait, because they touch the
measured draw path. Pass or fail is recorded once under
[deciding a correction](#deciding-a-correction); a fail ends the round.

Conditional diagnostics, not stages:

- GC ownership. Only if induced collections still dominate the worst
  stationary gap after this round, one heap snapshot during the aggregate
  phase attributes the handles and finalizable objects; the existing traces
  carry counts but not owners.
- Click feedback. Use the preserved `75af850` delay attribution first
  (delivery, selection and XAML completion separated; the delivery interval
  includes row-binding notifications). Add a probe only if those records
  cannot separate dispatcher delay from handler work. The 9 November native
  trace attributed the click delay to WinUI realizing, laying out and drawing
  changed table rows. Filling those rows in phases
  ([plan 18](18-keep-up-with-the-person.md)) made the typical click slower in
  every measured pair and was removed. The 249 ms endpoint is click-to-XAML completion, not visible selection.

Why this round: it is the one population-sized piece of work the trace still
shows on the UI thread, it leaves under the owner's ruling above, and the map's
thread-work objective is already complete. Sizing the worker's collections rides
along because the same change already owns them.

## Follow-ups, not steps

Each becomes a task only by the owner's scoped decision, with its own predicted
endpoint and completion condition.

- Presented-frame endpoint (DXGI or DWM present events) in the harness. It is
  the precondition for claiming displayed frame rate or next-frame selection,
  not a work stage.
- Shared-device contention between offscreen rasterization and the game loop's
  presents is a possibility, not a stage; any second device or smaller render
  unit needs it measured first.
- Sure source redundancies without measured savings:
  [575](https://github.com/SynoSoftware/TinyTorrent2/issues/575) (bitmap wraps
  drawn outside the view; it touches the measured draw path, so after the next
  round) and [576](https://github.com/SynoSoftware/TinyTorrent2/issues/576)
  (small per-frame redundancies, including the destination dictionary that
  `MapRenderer.Inspect` rebuilds every pass). Each names its predicted endpoint
  when it joins a build. Issue 574 was closed as invalid: `SequenceEqual` stops at the
  first difference and the unchanged path allocates nothing.
- Code shape: [577](https://github.com/SynoSoftware/TinyTorrent2/issues/577),
  splitting the picture pool, sprites and markers out of `MapMotion`.
- Perceived halo softness of the location lights is visually unverified.

## Deferred TableView findings

Correctness findings from the selection review, outside this round and owned by
TableView (`Table/Selection.cs`, `Body/Surface.cs`, `Table/Reordering.cs`,
`Order.cs`):

- Internal `SelectedItems` reads the public getter, which can return a pending
  future-view request, so accessibility, context menu, drag and invocation can
  consume pending intent as the current selection.
- UI Automation actions use the public setter, which defers during view
  preparation, while pointer selection commits at once.
- A selection callback during projection can queue a correction while
  `_preparingView` is true, and same-version completion need not drain it.
- Repeated `CanStartDrag` queries enumerate the host's order until a second
  queue participant is found; the proposed repair is one cached capability in
  `Order` with a narrow `Table.RefreshOrder()` refresh on every unpublished
  row-change path.

None of these is established as a cause of the measured stalls.

## Historical results

| Record | Content |
| --- | --- |
| [Map handoff functional run](../../artifacts/evidence/Capture-map-handoff-d220f8ff-add3-44be-adfb-cc33004a45fd/captures/review.json) | Eight journeys passed at `e723ae6`. |
| [Replay baseline `b20b923`](../../artifacts/evidence/ui-benchmark-replay-baseline-20261106-b20b923/README.md), [corrected observer `ba64259`](../../artifacts/evidence/ui-benchmark-draw-observer-baseline-20261106-ba64259/README.md) | Frozen references. |
| [Peer snapshot](../../artifacts/evidence/ui-benchmark-peer-snapshot-20261106-2beb6e7/README.md), [scope retention](../../artifacts/evidence/ui-benchmark-scope-retention-20261106-3d2970a/README.md), [notification attribution](../../artifacts/evidence/ui-notification-attribution-20261106-deb3e1e/README.md), [bindable notifications](../../artifacts/evidence/ui-profile-bindable-20261106-222e88d/README.md), [sort capacity](../../artifacts/evidence/ui-profile-sort-capacity-20261106-78fb43f/README.md) | Table-side attribution rounds. |
| [Allocation trace `78fb43f`](../../artifacts/evidence/ui-profile-alloc-current-20261106-78fb43f/README.md) | Allocation owners behind the next round. |
| [Thread work `fc0916d4`](../../artifacts/evidence/ui-profile-map-thread-work-20261106-fc0916d4/README.md) | Relocation demonstrated; response mixed. |
| [Marker preparation `555bd62`](../../artifacts/evidence/ui-profile-marker-preparation-20261106-555bd62/README.md) | Acceptance failed; GC first identified. |
| [Runtime bookkeeping `75af850`](../../artifacts/evidence/ui-profile-runtime-bookkeeping-20261106-75af850/README.md), [delay attribution](../../artifacts/evidence/ui-profile-runtime-bookkeeping-20261106-75af850/plan17-reviewed-delay-findings.md) | Retained; mixed. |
| [Defined changes `4f069bb`](../../artifacts/evidence/ui-profile-map-defined-changes-20261107-4f069bb/README.md), [build](../../artifacts/evidence/ui-build-map-defined-changes-20261107-4f069bb/README.md) | Current reference; mixed. |
| [Country full-screen run](../../artifacts/evidence/ui-country-fullscreen-20261107-4f069bb/comparison.json) | Visual review of the accepted artifact. |
| [Plan before this rewrite](../../artifacts/evidence/plan17-rewrite-20261107/plan-before-rewrite.md) | The full investigation record through 7 November, with the former HTML review beside it. |
