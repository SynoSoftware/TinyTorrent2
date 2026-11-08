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
then the [one next round](#next-round-size-the-projection-collections-from-known-counts).

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
- TableView work is eligible only for an operation that can and should move off
  the UI thread: detached inputs, a concrete blocking cost removed, and simpler
  or equally clear ownership. Native WinUI updates and UI-affine selectors stay
  on their owner. No publication redesign or broad table investigation, because
  that redesign was tried, reverted and never attributed.
- Memory is a regression guard, not the goal. Dedicated memory tuning stays
  deferred; flag concrete, blatant waste when attribution shows it.
- The owner reviews each recorded result before any further performance change,
  build or run. Silence is not approval, because an unreviewed loop produced
  four rounds of mixed results and no decision.
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

## Next round: size the projection collections from known counts

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
not a census; its README holds the per-owner figures):

| Owner | Sampled allocation | Standing |
| --- | ---: | --- |
| TableView projection collections at `Table/View.cs`: UI `CaptureProjection`'s object-to-object entry arrays and integer arrays, worker `Prepare`'s object-to-position and object-set entry arrays | 184 + 33 MiB and 139 + 198 MiB over the journey | Current code. Grown from zero capacity although the counts are known; at stress size every entry array is on the large object heap, and growth allocates a doubling chain of about twice the final size with rehash copies, the UI ones inside `CaptureProjection`. The README names sizing them as the next candidate and says it changes no identity, selection, release, sort or native reconciliation rule. |
| Inspector `Filter` capture | 92 sampled capture arrays over the journey; 1.25 MiB in the marquee interval | Now one 4.9 MiB value array per capture. The fixture delivers about one reply burst per second, so coalescing captures per burst cannot move an endpoint here. |
| `MapModel.Update` per-peer objects | the largest owner then | Removed at `fc0916d4` and `6528d57`. |
| `Dictionary<DestinationKey, Destination>` rebuilt by `MapRenderer.Inspect` every render pass | 6.06 MiB in the marquee interval | About 1,000 entries, generation 0, at pass rate during navigation. |

Issues [573](https://github.com/SynoSoftware/TinyTorrent2/issues/573) and
[578](https://github.com/SynoSoftware/TinyTorrent2/issues/578) are therefore
ordinary-use code fixes, judged as code: the per-reply recapture is real when
the engine answers one torrent at a time, and the geolocation sets are real
when geolocation runs, but this fixture shows neither. They ride in the next
build without a predicted endpoint.

The measured change, after the selection correction lands: give the UI identity
map, the worker uniqueness set and the position map at `Table/View.cs` their
capacity from the counts already available. Capacity only: no publication
redesign, no relocation, no behavior change. This is outside the letter of the
TableView fence above, because it moves nothing off the UI thread, and inside
its reason, because it reopens no design and the trace attributes it. The fence
is the owner's, so this one-change exception needs the owner's yes. Without it,
this round is the conditional heap snapshot below, and the capacity change is
recorded here as the largest measured churn owner awaiting that decision.

Predicted endpoints, one build and one matched city/1,000 docked run against
the step-1 build as reference: induced full-collection count and total GC
suspension down; UI `CaptureProjection` elapsed (an existing in-app marker)
down; click-to-XAML p95 down; peak private commitment not up. Issues 573 and
578 ride in the same build because the fixture cannot see either, so they
cannot confound the comparison. Issue 575 and the `Inspect` dictionary rebuild
wait, because they touch the measured draw path. Pass or fail is recorded once
under [deciding a correction](#deciding-a-correction); a fail ends the round.

Conditional diagnostics, not stages:

- GC ownership. Only if induced collections still dominate the worst
  stationary gap after this round, one heap snapshot during the aggregate
  phase attributes the handles and finalizable objects; the existing traces
  carry counts but not owners.
- Click feedback. Use the preserved `75af850` delay attribution first
  (delivery, selection and XAML completion separated; the delivery interval
  includes row-binding notifications). Add a probe only if those records
  cannot separate dispatcher delay from handler work. If native publication
  of the selected torrent's peer table is the cost, the options are deferring
  it until after the selection frame presents, or chunked, natively consistent
  publication, which lifts the TableView fence and needs that attribution
  first. The 249 ms endpoint is click-to-XAML completion, not visible selection.

Why this round and not another relocation: the thread-work objective is
complete, four rounds that changed code without a predicted endpoint ended
mixed, and this is the largest allocation owner the existing trace measures.
It is a capacity argument that names what it should move.

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
