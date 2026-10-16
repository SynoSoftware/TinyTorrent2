# Peer map implementation

This is the delivery record. [Peer location](peer-location.md#map) owns map
behavior and precision; [Interface](interface.md#inspector-and-edits) owns its placement.
The existing protocol and window-layout owners retain their responsibilities.

The [deferred MVVM refactor](#deferred-mvvm-refactor) is a proposed ownership
change to start after the current performance work has a measured, committed
baseline. It does not authorize implementation or change current map behavior.

The native baseline is recorded below. The [zoom and pan extension](#zoom-and-pan-extension)
is implemented in source; build and native interaction verification are pending.
[Arc dimming](peer-location.md#arc-dimming) now implements the
approved D treatment, Less glow; native visual verification remains pending.

The Peers inspector follows `app/peers-prototype.html`: one Lucide ellipsis menu in its
existing header selects Map, Table or both, and Countries or Cities. The inspector
header owns the separator before its common actions. Missing city data downloads
directly through the existing dataset owner and status bar. This revision is
implemented in source; build and native verification remain pending.
The combined arrangement automatically
fits the larger map and reuses Splitter; the existing inspector height adjustment
stays. Coordinates use shared-location rendering; country-only records use the
approved estimated distribution. Selection determines the torrent scope.

## Deferred MVVM refactor

Proposed 2026-10-15 at the owner's request. This section records a design for
later implementation, not another active work queue. The existing delivery
sections below describe earlier revisions; this proposal supersedes their
single-control ownership approach only when implemented. Product behavior
continues to belong to [Peer location](peer-location.md#map).

### Starting point and evidence

Finish the current performance change, its authorized build and capture, and
commit it before starting this refactor. Record that commit and its evidence
paths when implementation starts. Use the same map asset, peer fixtures,
viewport sizes, build configuration and journeys for before/after comparisons,
because otherwise a timing difference cannot be attributed to the refactor.
Coordinate ownership of the affected files before editing; another writer is
currently changing the renderer and navigation.

Source review found peer aggregation, destination placement, animation and
native resource reconciliation interleaved in `PeerMap.Arrange`. It also found
that initial tracing and dirty peer preparation run before the drawing rebuild
allowance begins. This establishes an ownership and scheduling concern, not a
measured cause of slow dragging. Pan alone does not dirty peer aggregation;
telemetry updates during a drag can still trigger it on the next draw.

The chosen-place path currently sends map events to `Inspector.Place` without
feeding that authoritative value back into the map. A visible disagreement has
not been reproduced. Investigate concrete transitions before describing this as
a confirmed defect or inventing reconciliation behavior.

### Concepts and owners

Use the [product vocabulary](../CONTEXT.md#peers): a peer place is a filter,
a map destination is a drawn position, and selected table peers are individual
records. Several estimated destinations can belong to one place. Picking one
does not reveal which actual peers occupy that estimated region.

| Owner | Responsibility and reason |
| --- | --- |
| Existing `Inspector` view model | Owns peer scope, geolocation results and the writable chosen place. The table and map therefore consume one filter decision. Owns the map view model within the existing presentation lifetime. |
| Map view model | Owns grouped peer counts, rates, precision and destination descriptions, including one implementation of estimated distribution. Contains plain values and existing peer identities, with no XAML or Win2D resources. Its parts may separate aggregation and distribution, but neither repeats the other's rules. |
| `PeerMap` control | Owns native input, focus, hover, tooltip and automation wiring. Turns gestures into selection requests and viewport operations. It does not acquire peers or independently decide the table filter. |
| Renderer | Owns geometry, brushes, animation values, clipping, detail selection, cache reconciliation and disposal. It consumes destination descriptions and retains GPU resources by stable identity across rate-only updates. |
| Viewport | Owns centre, zoom, ratio, forward/inverse transforms, constraints and anchor preservation. Drawing and hit testing consume the same transform, preventing separate projection rules. Native pointer capture remains in the control. |

These are concrete owners inside the existing app, not new projects or a plugin
framework. Use repository naming and placement conventions when extracting
types; these responsibility labels do not prescribe filenames. Partial files
are suitable for organizing one owner, not for pretending shared mutable state
has been separated. Keep the renderer and viewport interfaces small rather than
passing the entire control into them.

### Data flow and lifetime

1. Inspector supplies the current peers, origin and detail choice to the map
   view model. Capture the mutable peer facts needed for change detection once
   per input update; retain identity and avoid notifications for unchanged data.
2. Screen-dependent merging of populated places stays with map layout. Supply
   the resulting candidate regions to the one distribution implementation;
   changing pan or zoom does not rerun allocation. Preserve the existing
   resize behavior and population ordering.
3. The renderer reconciles destinations only when those inputs change. Separate
   destination facts from cached `Arc` and `Paint`; presentation objects neither
   dispose nor transfer native resources. Preserve the baseline's cache keys,
   shared borders, clipping, detail thresholds and rebuild allowance.
4. A map choice requests an update to `Inspector.Place`. Feed the resulting
   chosen place back to the map through the presentation path. Keep the exact
   inspected destination local where needed: a country filter cannot identify
   one of its several estimated markers. Table selection remains the existing
   table selection and is supplied for highlighting, not duplicated as another
   independently writable collection in the map view model.
5. Drawing reads retained presentation data and renderer state. It does not
   regroup peers, publish view-model notifications or allocate a new scene on
   every frame. Native geometry preparation remains under renderer ownership.
6. Preserve current hide/show and Map-off behavior. Unload releases native
   resources and bundled geography as today; a retained view model must not
   accidentally keep those resources alive or start hidden peer acquisition.
   Device recreation rebuilds rendering resources without changing peer facts
   or the chosen place.

### Implementation sequence

1. **Establish the baseline and selection behavior.** Read the committed source
   and existing capture consumers, including reflection calls. Exercise the
   scenarios below in the authorized capture and record any pre-existing
   discrepancy. Verification: baseline behavior and timing evidence are linked,
   and selection risks are distinguished from reproduced failures.
2. **Extract the map view model and make filter ownership explicit.** Move peer
   aggregation and distribution without changing their calculations. Keep
   destination facts separate from renderer resources and remove the replaced
   paths in the same change. Verification: rates, counts, destinations and
   table membership match the baseline, except for a documented selection fix.
3. **Extract rendering and viewport ownership.** Preserve cache lifetimes and
   current projection behavior, including latitude correction during pan.
   Update real consumers and the existing capture together. Verification: no
   projection, geometry or per-frame binding work is introduced by the split.
4. **Address measured preparation stalls separately.** First measure initial
   show and peer updates during dragging. If geometry work outside the allowance
   stalls frames, include it in renderer scheduling without rendering a partial
   logical peer update. Do not add background queues, cancellation infrastructure
   or chunked aggregation merely to complete the architecture. Moving CPU work
   out of `Draw` alone does not make it cheaper on the UI thread.
5. **Review and compare.** Review the final ownership and disposal paths, then
   perform only the owner-authorized build/capture needed for the comparison.
   Update the existing architecture and map records to describe the delivered
   owners; remove superseded paths rather than preserving compatibility wrappers.

### Acceptance evidence

- Select two estimated destinations belonging to the same country. The exact
  inspected marker may differ while the table's place filter stays the same.
  Select rows within that filtered table: highlighting must not replace its
  chosen place. Check Escape, empty-map click, detail changes and Table-only
  against the existing clearing rules.
- Change peer scope or remove the last peer at the chosen place; also hide/show
  the inspector. Record baseline behavior and preserve the product contract.
  Do not silently introduce an automatic filter-clearing policy for an empty
  place as part of an architectural move.
- Compare pan-only, zoom, and pan with live peer updates using the same journeys.
  Include high-latitude motion, seam crossing, drag cancellation, resize and
  device/resource recreation where the existing harness supports it. Verify
  pointer anchoring, fixed-width strokes, fades and selected-path appearance.
- Compare frame-time distributions and slow frames, managed allocations and
  retained memory with the measured baseline. Use existing instrumentation;
  distinguish CPU submission timing from GPU completion. Investigate differences
  beyond run-to-run variation instead of promising an unmeasured zero overhead.
- Rate-only updates retain route geometry when endpoints are unchanged; redraws
  do not publish presentation changes; unloading releases renderer resources.

Reuse existing meaningful capture checks. Add a focused regression check only
for a reproduced failure not already covered, following [Testing](testing.md).
This plan authorizes no builds, tests, captures or application changes now.

## Data and ownership

- Inspector owns selected/all-torrent scope and consumes the existing asynchronous
  torrent detail requests. All-torrent reads visit torrents sequentially, preserving
  torrent plus endpoint identity without cross-torrent deduplication. Hidden or
  superseded reads stop and discard their results.
- Geolocation exposes coordinates from the installed city MMDB. Country-only
  records retain that precision. The origin uses the engine-reported public
  address through the same local lookup; an unknown origin means no arcs.
- With no torrent selected, Peers shows all torrents and Speed reads the existing
  session history. Other sections retain their selection requirements.
- PeerMap owns aggregation, projection, hit testing and drawing. Win2D supplies
  antialiasing, blur and a mask on the composited arc layer; no browser or online
  map service is involved. Natural Earth geometry and populated-place weights
  from the approved prototype are bundled with attribution.

## Approved appearance

Countries aggregate measured rates, select at most as many distinguishable
populated regions as active connections, then distribute the country total by
those regions' population weights. These locations and allocated rates are
explicitly estimates. Cities group identical coordinates and retain idle markers.
Country fills are restrained activity shading. Arcs use the existing transfer
colours, fixed logarithmic scaling, gentle globe curvature and restrained glow.
The combined arc layer uses adaptive origin fading. Location marks remain
outside the mask.
Hover and keyboard inspection expose counts, precision and both measured rates;
the table remains available for individual endpoints.
The dimming implementation adds the combined opacity cap and destination fade;
the [rendering contract](peer-location.md#arc-dimming) specifies
their values, composition order, origin formula and theme exceptions in one place.

## Delivery and verification

1. Extend the lookup and Inspector data flow; inspect stale-context, reconnect,
   no-selection and hidden-view paths.
2. Add the map and compact header toggles; retain table selection, translated text,
   theme resources, saved layout and independent split proportions.
3. Review the final source and diff. Do not compile, launch or run tests without
   the owner's request. Record runtime and performance gaps explicitly.

## Zoom and pan extension

Updated 2026-10-14 from the supplied zoom plan and the subsequent prototype review.
[Peer location](peer-location.md#zoom-and-pan) owns the input,
appearance and view-lifetime behavior. The single maintained prototype is
[app/peers-prototype.html](../app/peers-prototype.html); retain its Map/Table
header toggles, automatic arrangement, splitter and inspector-height adjustment.

### Implementation approach

- Keep aggregation, fit-space projection, drawing, view state and hit testing
  in `PeerMap`, following the codebase-design principle of one deep module with
  a small interface. No new view service, persistence owner, browser host or map
  dependency is needed.
- Build country and arc geometry in fitted-map space. Zoom and pan update only
  the view transform and invalidate the canvas; they must not rebuild geometry,
  regroup peers, rerun populated-region allocation or recreate the drawing
  control. Peer updates rebuild destination counts and rates through `Arrange`,
  retaining active arc geometry when its origin and destination are unchanged.
  Removed or inactive routes release their geometry; resize, device recreation
  and unload release all geometry.
- Coalesce input redraws through the existing rendering mechanism, not the
  33-ms activity interpolation timer. The HTML prototype uses one scheduled
  animation-frame update; native code should use `CanvasControl.Invalidate`
  directly and let the rendering loop combine invalidations. Do not transplant
  the prototype's DOM machinery or add a separate gesture timer.
- Transform vector geometry with fixed-width strokes. Draw markers in screen
  space. Convert pointer positions through the inverse transform for geometry
  hit testing, keeping tolerances in screen DIPs. Keep brush coordinates aligned
  with the geometry; glow and origin/destination fades must not grow with zoom.
  Bound effect work to the visible canvas, including blur padding, so an 8× view
  does not allocate an eight-times-larger off-screen effect surface per axis.
- Apply each halo's destination fade inside the shared halo command list,
  before a single blur. Keep sharp cores above that result. This avoids one
  blur effect per active arc; native review must check the softened fade.
- Prefer this view transform over wrapping the canvas in a zooming
  `ScrollViewer`: ordinary whole-surface scaling would also enlarge strokes and
  markers, while hit testing and redraw coordination would still need custom
  handling. The custom interaction stays inside the existing map control.
- Route wheel, double-click, keyboard, menu and Fit through the same zoom, pan
  and fit operations. Use pointer capture after the drag threshold; releasing
  a drag must not activate the location beneath it. Escape and capture loss
  cancel the drag cleanly. Follow `Week`: use `InputSystemCursorShape.SizeAll`
  only during an active drag after the threshold, then restore the normal cursor.
- `PeerMap.OnKey` remains the keyboard owner. Mark handled map shortcuts so
  Ctrl+Plus/Minus cannot also reach the torrent queue handler. Menu shortcuts
  are display text, not a second set of registered accelerators. Format them
  from the existing shortcut strings. Follow `SpeedGraph`'s marker-anchored
  keyboard zoom: use the inspected destination when present, otherwise the
  view centre, so zooming does not lose the location being inspected.
- Add the compact Fit control inside `PeerMap.xaml`, using the normal button
  style, Lucide icon, tooltip and accessible name. It restores map focus when
  hidden. Update the existing keyboard help and add translated Zoom in, Zoom
  out and Fit labels in the English and Spanish catalogues; the menu and tooltip
  reuse those labels. Use a native `Button` and `MenuFlyout`, with theme resources
  and standard interaction states. Preserve system focus visuals and High
  Contrast behavior. Keep the existing inspector header and status bar layout.
- Keep lifetime in the existing `InspectorPane` owner: `ArrangePeers` discards
  `_peerMap` only for explicit Map-off; temporary hiding and `StopTracking` detach
  its content while retaining the control and view state. Unloading still
  stops animation and releases drawing resources; retaining view state must not
  keep hidden rendering or peer collection running. Do not add zoom to the
  saved window-layout snapshot.

### Dimming delivery and prototype comparisons

D is the approved native treatment, with values owned by the
[dimming contract](peer-location.md#arc-dimming).
`PeerMap.Draw` fades halos before one shared blur and masks each sharp core,
then applies the origin mask and opacity cap to the combined layer. Markers and
selection remain outside those layers; High Contrast bypasses dimming and glow.
Keep A–D in the prototype for comparison. The product gets one treatment, not a
rendering selector. This source change has not been built or visually reviewed;
verify it with live rates at fitted and zoomed views.

| Treatment | Difference from the existing rendering |
| --- | --- |
| A — Current | Existing activity styling and adaptive origin fade |
| B — Layer cap | Cap the combined arcs and glow after compositing |
| C — Soft landings | B plus destination fading for cores and halos |
| D — Less glow | C with reduced halo strength; selected for native delivery |

The contract defines fade multipliers and composition order. Markers remain
outside these fades; selected paths render in a separate highlight layer. Existing sparse-map origin
fading remains independent. No density detector was added. The comparison controls,
Cayenne/London illustrative origins, artificial peers and pause/time controls stay
in the prototype; they are not product settings. The native app retains its local
lookup origin and per-record coordinate/country precision without a mode selector.

### Evidence and native acceptance

The owner approved the updated prototype after the faster zoom/pan change. The
startup failure from a local `location` helper shadowing the browser URL was fixed
in both URL-handling paths. Execution checks exercised startup, map modes,
rendering treatments, zoom, Fit, wheel, drag and Escape. They confirmed that
gesture input no longer replaces the SVG and that marker/fade radii compensate
for zoom. These checks used a lightweight DOM harness: they do not establish
browser frame rate, raster appearance, WinUI behavior or live swarm performance.
The subsequent native implementation keeps zoom, geographic centre and drag state
inside `PeerMap`, in its Navigation partial. Wheel, double-click, keyboard and
menu commands share the same view operations. Navigation changes the drawing
transform without marking peer geometry dirty. Borders and strokes compensate
for zoom, markers and core masks draw in screen space, and the shared halo blur
is cropped to the viewport with padding. Inspector lifetime retains the view
while hidden and releases drawing resources on unload. English and Spanish
labels cover the native Fit button, menu and keyboard help.

Source review traced the input, transform, hit-test, resize, language and hide/show
paths. No native build, tests or capture review have been run for this extension;
the following acceptance checks remain unverified.

The rendering optimization reuses existing arc geometry across rate updates.
For an unchanged set of active destinations and origin, an update now creates
no arc paths instead of recreating every path. Counts, allocation and activity
interpolation still update normally. Origin changes replace the routes, and
inactive or removed destinations release them. This reduction is established
from source; frame time, allocations and native appearance remain unmeasured.

The demand-driven refresh pass skips unchanged clamped view transforms and
starts activity ticks only for visible interpolation. Keyboard inspection requests
its own redraw even when revealing a location does not move the viewport. Identical
peer samples no longer refresh the table; an origin change still updates the map.
Map-only presentation defers table sorting until the table is shown again.
These paths were source-reviewed; build, interaction and timing checks remain pending.

Pane arrangement no longer resnapshots peers during splitter or inspector resizing.
`ArrangePeers` handles layout and attachment; its data-changing callers invoke
`RefreshMap` explicitly. Row updates and language changes retain their existing
refresh paths, and a detached map receives no snapshots. Resize still invalidates
the canvas and rebuilds size-dependent geometry as required. This removes a full
peer snapshot/comparison from each layout-only update without adding cached state.

The subsequent plan and ownership review found and corrected shortcut routing
from the Fit button, viewport movement when opening the keyboard context menu,
tooltips reappearing during a drag after live updates, and stale hover state
after panning or wheel zoom. Inspection lookup now
has one implementation shared by navigation and descriptions. The Fit button
returns focus to the map before zooming out hides it, including wheel zoom.

For the authorized native implementation review:

- Capture fitted, zoomed and edge-panned views, resize while zoomed, and Fit in
  Light, Dark and High Contrast. Check the Fit control, focus restoration,
  fixed screen-space sizes, effect clipping and country detail at 8×.
- Review dimming with a crowded European destination scene and sparse scenes
  with 1, 8, 9 and 20 active destination arcs. Check that the origin adjustment
  uses aggregated arcs, its transition is continuous, and destination fading
  does not hide markers or inspection highlights. Verify the combined layer cap
  includes glow, leaves country shading intact and survives overlapping arcs.
  Repeat while zooming and panning to check fixed fade distances and stable
  brightness when arcs leave the viewport. Check High Contrast without dimming.
- Exercise a real mouse wheel, precision touchpad, drag threshold, Escape,
  capture loss, double-click, context menu and main/numpad shortcuts. Confirm
  map shortcuts do not operate the torrent queue with either map or Fit-button
  focus, and a drag does not select. Open the keyboard menu with an off-screen
  inspected location and confirm the viewport stays still. Keep live rates
  updating during a drag and confirm location tooltips remain suppressed.
  After releasing a drag or using the wheel, check that hover describes the
  location now under the pointer without changing the selected peer.
- Verify plain-arrow inspection reveals off-screen locations and preserves
  table selection ownership. Zoom by keyboard while inspecting a location near
  the viewport edge and check that it remains visible. Check unknown origins
  and mixed-precision peers.
- Check resize, live rate updates, section changes, minimising, Map-off/on and
  restart against the view-lifetime contract. Fit-space destination allocation
  must remain unchanged by zoom alone.
- Measure interaction with sparse and crowded scenes while rates update. Check
  frame responsiveness and effect allocations at high zoom, not just handler
  duration. Do not claim parity with Google Maps from the script-only check.

## Implementation status

### Refresh audit — 2026-10-14

The source audit found two corrections: **P2**, view changes left transient hover
inspection attached to a location after Fit or keyboard pan moved it away from
the pointer; **P3**, choosing the already-selected peer view repeated arrangement
and refresh work. `SetView` now clears transient inspection while preserving
selection; `SetPeerView` skips equal choices. Wheel and drag release re-hit-test
the new view, and keyboard inspection retains its selected destination through
reveal. Neither correction changes the approved appearance.

Provisional source assessment: **14/20**. These scores describe implementation
coverage, not measured performance or an accessibility certification.

| Dimension | Score | Evidence and remaining limit |
| --- | --- | --- |
| Accessibility | 2/4 | Named controls, keyboard inspection, system focus, reduced motion and High Contrast paths exist. Narrator announcements of changing canvas inspection and larger-text usability remain unverified. |
| Performance | 3/4 | Arc reuse, unchanged-sample suppression, conditional animation and direct navigation invalidation exist. Native frame cost remains unmeasured. |
| Appearance and theming | 3/4 | Theme brushes, opaque alpha masks and High Contrast exceptions preserve the intended hierarchy. Rendered contrast and theme transitions need native review. |
| Platform conformance | 3/4 | Native menus, buttons, splitter and TableView retain their owners. Mouse, touchpad and assistive-technology behavior need execution checks. |
| Adaptivity | 3/4 | Automatic split orientation, retained proportions and fixed screen-space drawing exist. Extreme sizes, DPI changes and larger text need capture review. |

No additional P0/P1 defect was established in the inspected paths. Geometry
release, conditional activity ticks and the shared viewport-bounded blur remain
intact. TableView owns a sort-settle timer, so skipping identical peer samples
does not strand deferred sorting. Map-only presentation reconciles sorting when
the table returns.

Profiling with the map visible later showed that creating brushes and layers
for every arc in every `Draw` forced full garbage collections, and that
stroking the country borders took half of `Draw`. At 4–8× zoom, Direct2D's
per-draw rasterization of the arcs took a median of 124 ms per frame and up to
1.1 s. Each arc now keeps one radial brush that carries both its route
brightness and its landing fade. Countries and arcs are drawn from geometry
realizations made for the current zoom, so a pan rebuilds nothing. Rebuilding
all of them took about 170 ms, so a zoom step spreads the rebuild over the
following frames, a few milliseconds each. The
per-frame `ComputeArea` call for idle countries is not a demonstrated
bottleneck. Verify Narrator's response to changed
inspection: updating HelpText alone does not establish what it announces.

Validation was source tracing and `git diff --check`. No build, tests, launch,
profiling or new captures were run. A subsequent authorized native optimization
review, followed by visual polish of its findings, supplies the missing evidence;
repeated source audits cannot establish frame responsiveness or contrast.

Implemented in `Inspector`, its Peers partial, `InspectorPane`, `PeerMap`, and
the existing local `Geolocation` owner. The world geometry and populated-place
weights are embedded, with Win2D 1.4.0 providing native drawing. The existing
window-layout snapshot retains visible views and both split proportions. Previously
saved Stacked and Side by side values restore as the automatic combined view.
No engine command, simulation, browser host or online map dependency was added.

Before the adaptive header revision, Debug x64 engine, product and capture builds
passed with zero warnings and errors. That native capture journey exercised the
layout menu, splitter proportions,
global peer reads and empty-selection Speed, and renders 25 scenes in Light and
Dark. The populated map scenes use synthetic coordinates and rates; a separate
lookup decodes the bundled country MMDB. City MMDB decoding and public-swarm
transfer timing remain unverified.

The adaptive revision keeps one arrangement owner in InspectorPane, one fitting
calculation in PeerMap, the existing Inspector scope and read path, and the existing
status-bar context. Its capture journey now uses the header toggles, different
window proportions, and country-only, coordinate and mixed records. The feature
review and screenshots below describe the preceding revision; they are historical
evidence, not verification of these changes.

### Adaptive inspector verification — 2026-10-14

Debug x64 engine, product and capture builds passed with zero warnings and errors
in `artifacts/lanes/2`. The initial default-output build could not copy its
executable over the running development window. The isolated engine build exposed
an unrelated duplicate local name in startup; renaming its two-character buffer
unblocked compilation without changing behavior. The stale lane rebuilt its
dependencies; the retry recompiled the corrected startup and concurrently updated
engine files. All generated output remained under `artifacts/`.

`Capture.ps1 -Review peers` passed 49 scenes, covering both header toggles, map-only,
table-only and automatic combined views, both splitter orientations, country-only,
coordinate and mixed records, and an unknown origin. The same journey verified
global peer reads and empty-selection Speed against its disposable engine. The
launcher now recognizes the repository's redirected default output when a review
runs from a lane. No review processes remained afterward.

Representative Light and Dark frames were inspected: header icons and counts
are visible, the panes rearrange at the tested sizes, resizing grows the map,
and active arcs retain their glow. Evidence is in
`artifacts/lanes/2/evidence/Capture-peers-83b5a344-fd3a-4de1-8692-0518a76c5e8b/`.
The populated scenes use synthetic peers. This review does not establish city-MMDB
decoding, public-swarm timing, OS High Contrast or persistence across a restart.

### Path review — 2026-10-14

Traced scope changes, asynchronous replies and cancellation, saved layout
restoration, toggles and both splitters, coordinate decoding, aggregation,
projection, animation, selection, status updates and canvas disposal.

Selection now uses the same destination identity as animation: country,
coordinate and estimated precision. A database coordinate coinciding with an
estimated anchor no longer selects both groups or describes the wrong rates.
Arrow navigation resumes from the selection after the pointer leaves. A timed-out
peer read releases the existing Inspector demand so background file reads can
continue. These corrections remain inside PeerMap and Inspector.

The mixed capture fixture now includes active country-only peers; previously
its country-only rows were all idle. A coincident-location scene selects the
coordinate-backed table row and records its map description for review.

Debug x64 engine, product and capture builds passed with zero warnings and errors
in the default output. Only the engine startup source needed recompilation there;
both app builds reused Lucide and TableView. The 50-scene peer review passed,
including the selected coordinate's own connection count and rates at the
coincident anchor. Mixed-data Light/Dark and landscape/portrait screenshots were
inspected. Evidence is in
`artifacts/evidence/Capture-peers-275914c9-984e-4577-985e-a8bb799383ab/`.
No review processes remained, and generated output stayed under `artifacts/`.
Timeout cleanup and keyboard continuation were source-traced, not fault-injected
or driven with native keyboard input; the earlier live-data validation gaps remain.

## Prototype comparison

Source comparison against `peer-map-layouts.html` confirms the two approved map
modes, country bandwidth allocation, coordinate grouping, fixed activity scale,
glow with a sharp core, adaptive origin fade and independently visible endpoints.
All four layouts retain both resize directions and the existing inspector height
control. Selected-torrent and global peers share one read path; empty-selection
Speed uses session history.

The product keeps the choices in the existing header instead of the prototype's
toolbar. It fits all valid coordinates rather than copying the prototype's
latitude crop. Simulator controls, alternate shading/rendering experiments and
the illustrative origin are omitted. High Contrast uses solid lines without glow.
Map inspection does not filter or replace the table's selection; selecting a table
peer highlights its map location. Visual equivalence and live timing still need
a native app review.

## Feature-by-feature source review

Reviewed on 2026-10-14. **Present** below means the behavior is implemented and
its source path was inspected, not that it has passed a build or native review.
The references identify implementation evidence, not additional authorities.

| Feature | Result | Implementation evidence and limits |
| --- | --- | --- |
| Peers inspector placement | Present | `InspectorPane.xaml` places View in the existing pane actions; the map is inside the Peers section. No additional toolbar row. |
| Table only | Present | `InspectorPane.ArrangePeers` retains the existing TableView and removes the map control. |
| Map only | Present | The same layout owner collapses the table and gives the map the available content area. |
| Stacked | Present | Map and table occupy separate star-sized rows with the existing Splitter between them. |
| Side by side | Present | The same layout owner uses columns and changes the Splitter orientation. |
| Map/table resizing | Present | `SetupPeers` converts the Splitter's trailing-pane size into a proportion; each orientation keeps its own proportion. Pointer and keyboard resizing need native verification. |
| Inspector height | Present | `MainWindow.UpdateInspectorSize` continues to own the outer splitter and inspector row height. The map fits the resulting size. |
| Saved view | Present | `InspectorLayout.View` saves layout, map mode and both proportions through the existing window placement snapshot. Old or invalid values have defaults at restoration. |
| Cities coordinates | Present | `Geolocation.Coordinates` reads MMDB latitude/longitude; `PeerMap.Locate` prefers those coordinates. Neither a city name nor a matching country polygon is required. Actual MMDB decoding is unverified. |
| Country-only fallback | Present | Missing coordinates use a bundled country anchor and are described as country-only. An unlocatable connection remains in the table. |
| Shared city locations | Present | `PeerMap.Arrange` groups connected peers by country and identical coordinates, then sums both rates and counts. It does not jitter locations. |
| Countries estimated distribution | Present | `Arrange` merges populated regions within 25 DIPs, uses the largest place for one active peer and at most one destination per active peer thereafter. More peers than distinguishable regions use all regions. |
| Country bandwidth allocation | Present | Chosen regions divide the measured country totals by population weight. Individual peers are not assigned invented city rates. Inspection separates country totals from estimated allocated rates. |
| Responsive destination spacing | Present | Projection and candidate merging are recalculated when the map size changes. Anchors may merge or separate on resize; changes in speed alone do not move them. Changes in active count can change the chosen set. |
| Country activity shading | Present | `Draw` uses a subtle idle tint and capped activity tint from aggregate throughput on a fixed logarithmic scale, independent of the fastest country. |
| Idle and active presence | Present | Cities retain idle outlines and active filled markers; shared-location counts increase marker opacity. Countries use fills, active arc endpoints and small-country fallback outlines. These marks are outside the arc mask. |
| Activity-driven arcs | Present | Only active destinations have arcs. Width and opacity use allocated/group throughput with the fixed scale; the dominant transfer direction chooses the existing download or upload brush. The two numeric rates remain available in inspection. |
| Antialiased glow and transparency | Present | Win2D draws sharp cores over a blurred halo; a four-stop gradient varies core opacity. This establishes the intended rendering mechanism, not visual parity with the artistic reference. |
| Earth-following curves | Present | `Arc` samples a great circle and splits at the date line; `Project` fits the full longitude/latitude range. These are geographic guides, not measured network routes or a perspective globe. |
| Sparse-map adjustment | Present | `Draw` counts rendered active arcs, applies no origin attenuation through eight and ramps to the crowded adjustment by twenty. A 40-DIP radial mask affects the composited arc layer; there is no separate density detector. |
| Origin | Present | `Inspector.Origin` looks up the engine-reported public address locally. An unknown origin suppresses arcs while retaining destinations. No invented origin or endpoint label. |
| Live rates and motion | Present, timing unverified | Engine payload rates reach both table and map through `ApplyPeers`. Existing destinations interpolate prominence over 1.6 seconds; unchanged snapshots do not restart animation. Appearance/disappearance and direction-colour changes are immediate, not cross-faded. There are no moving particles. |
| Selected and all-torrent peers | Present; scope toggle corrected | `ReadPeers` is the single sequential collection path. Row identity includes torrent and endpoint. The View menu now allows an explicit All torrents choice to be cleared after a multiple selection. |
| Empty-selection Speed | Present | `IsGlobal` selects all peers or session Speed with no selected torrent; `ReadHistory` omits `torrent_id` for session history. The global header suppresses the selected torrent's size. |
| Stale and hidden reads | Present | Inspector context/session/torrent guards reject stale pushes; invalidation cancels the pending reply and withdraws demand. Removed torrents are excluded when the complete batch is applied. Minimize, hide and section changes release the map. |
| Hover and selection | Present | `OnPointer` checks generous marker targets and arcs; `Describe` shows counts, both rates and precision. Table selection highlights matching coordinates without selecting a country. Map inspection does not filter or replace table selection. Individual IP diagnostics stay in the table. |
| Keyboard, themes and language | Present, native review pending | Arrow keys inspect destinations; Escape clears highlighting. The control has an accessible name/help text, uses theme brushes and translated strings, and bypasses glow/fading and interpolation in High Contrast. Reduced motion bypasses interpolation. |
| Resource lifetime and offline assets | Present, runtime review pending | `Stop` stops animation and releases canvas geometry and the CanvasControl. World geometry is embedded and loaded once; Win2D and Natural Earth attribution are included. No browser, runtime map API or population-ranking dependency. |

The feature pass found no further missing approved mode or layout. It does not
establish live timing: all-torrent reads are sequential and replace the displayed
collection after the batch completes, so they are not a synchronized one-second
sample across torrents. The subsequent native review below adds build and visual
evidence; OS accessibility, persistence across app restarts and live refresh cost
remain open validation work.

## Native verification

On 2026-10-14, `Capture.ps1 -Review peers` completed all 25 scenes without a
failure. Both map modes, all four layouts, changed split proportions, inspector
height and an unknown origin were captured. The flag cells render without the
previous deferred-font exception. Screenshot review found unreadably narrow peer
columns; the host now declares minimum widths, retaining the table's established
right-edge clipping policy. Synthetic visual fixtures are confined to capture
builds and do not change the production geolocation or collection contracts.

Evidence: `artifacts/evidence/Capture-peers-c8a47ba9-e3fa-449a-a051-1201211c9622/`.
The report distinguishes engine-backed scope checks from synthetic rendering.
