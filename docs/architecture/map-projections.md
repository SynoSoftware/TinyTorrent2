# Map projection research and proposed design

**Disposition: Won't do.** The current map works well: its equirectangular
ratio adapts to the view as it moves and zooms. No projection-family change is
planned; the research below remains for reference.

Research checked 2026-10-15. The owner subsequently chose adaptive
equirectangular proportions, implemented as described in the
[peer-location contract](../peer-location.md#zoom-and-pan). The renderer still
uses Win2D cached geometry. No build, test or performance measurement has
validated this change; the projection-family interfaces below remain proposals.

## Question and recommendation

Allow the peer map to select a projection without paying for a general plugin
framework on every rendered frame. Treat the interchangeable part as coordinate
mathematics with declared world bounds and edge behaviour. Keep geometry,
navigation, selection, animation and Win2D resources with their existing owner.

Preserving the current performance gains takes priority over projection changes.
Keep one projection family with adaptive geometry detail. The implementation
now derives its horizontal factor from the cosine of the view's centre latitude
on zoom and pan, starting with a 2:1 overview.
Its land fills reuse an affine transform;
strokes are prepared after both axis scales, and endpoint fades and hit testing
account for those scales. The shared outline traversal and cached GPU drawing
remain in place. Their update and rendering cost must be measured before
describing this as a free change. Adoption depends on preserving the current
renderer's measured performance and visible detail.

If switching projection families is eventually justified, consider only
Miller for broad views and bounded spherical Mercator for regional detail,
retaining Miller for polar views. Prepare a replacement after interaction settles;
do not continuously deform the cached geometry. The earlier manual-selector
interface below is a design study, not the implementation recommendation for
this adaptive mode. Its full-world domain and lack of an inverse are insufficient
for that mode.

A dynamically loaded assembly is a separate distribution requirement; switching
implementations does not require a loader, reflection, dependency injection
container or process boundary.

## Optimization audit and revised recommendation

The audited baseline is commit `3adf807b` plus the working changes to
`PeerMap.xaml.cs`. The higher-resolution asset and outline hierarchy are already
in that commit; the remaining diff adds further caching and rendering changes.
Compare a future implementation against this combined baseline, not its slower
predecessor. These are source and asset inspections, not new timing measurements.
The counts below predate the subsequent shared-section asset change in the
working tree; they describe the audited baseline, not that replacement asset.

| Outline asset | Countries | Rings | Stored vertices | Bytes |
| --- | ---: | ---: | ---: | ---: |
| Previous JSON, `3adf807b^` | 206 | 280 | 9,986 | 479,761 |
| Audited binary, `3adf807b` | 254 | 1,520 | 73,073 | 959,486 |

The audited source has 7.3 times as many outline vertices, but the shared
simplification hierarchy retains only 6,114 at the 0.5-degree detail level,
9,311 at 0.25 degrees and 23,863 at 0.0625 degrees. These are source-filter
counts before visibility, seam handling and realization, not GPU vertex counts.
All 73,073 are available at the finest level, 1/128 degree. Thus an overview
can use fewer points than the old asset while zoomed views reveal much more.

The owner's pasted development chat describes a different, intermediate
baseline: 27,069 vertices in an earlier 50m binary after a 0.05-degree cutoff,
versus 73,073 after lowering that cutoff to 1/128 degree. The 9,986 count above
belongs to the older JSON asset, not that intermediate binary. These comparisons
must not be presented as the same before/after measurement.

The important mechanisms to preserve are:

- Binary loading and shared-border error levels, with power-of-two detail
  selection. The half-DIP target is nominal: rounding the level can increase
  the threshold by up to the square root of two.
- Geometry in map coordinates, retained across resize and transformed for
  navigation. Visible countries are retraced at the new detail as budget permits.
- Cached fills, strokes and route brushes, reused routes for unchanged
  destinations, and one shared glow pass.
- The working diff's selected-route preparation priority and paced brush-colour
  changes. Rounded joins and
  pixel-wide borders with compensating alpha improve outline rendering.
- The existing marker sizes, route widths, origin/landing fades, hit testing
  and composition order. A faster result that loses these is not equivalent.

Selection-ring caching was an unsuccessful experiment, not a demonstrated gain:
the pasted chat reports that redraw time did not improve. One investigated
version instead drew all markers before drawing selection rings; the working
tree now contains cached rings again. The chat's batching explanation remains
a hypothesis until a comparable result confirms the improvement. Neither the
presence of that code nor an instrumented capture proves it. Shared-border,
projection and batching changes need comparable measurements before assigning
a performance gain to any one of them.

Continuous CPU reprojection would invalidate the geometry that makes this fast.
Microsoft specifically recommends geometry realizations for maps whose stable
geometry is transformed, and explains why continuously changing geometry defeats
that reuse. A crossfade also draws two scenes during the transition. Neither is
the first choice for the owner's speed constraint.
[Direct2D geometry realizations](https://learn.microsoft.com/en-us/windows/win32/direct2d/geometry-realizations-overview)

For a fixed built-in Miller map, the most direct way to preserve the optimization
is to generate its outline coordinates and shared-border simplification levels
with the asset. The existing asset preparation already owns that shared-border
calculation. Longitude recentering remains a horizontal translation, while
projected latitude and its detail hierarchy are fixed. Static land then needs no
runtime projection or conservative global stretch multiplier. Keep geographic
peer/place coordinates as the source of truth and project their changing display
positions during preparation. If the two-projection mode is chosen, the same
preparation can produce a separate bounded Mercator outline asset; load the
required one rather than keeping every realization resident. This trades asset
size and preparation work for less runtime work, and still requires measuring
the resulting geometry's rendering cost. It is appropriate for a small built-in
set, not an answer to arbitrary runtime projection plugins.

### Projection choice by geographic view

Pixel resolution determines required geometry detail. Geographic coverage and
latitude determine suitable projections; a larger monitor or a particular zoom
multiplier alone is not a reason to change projection.

| View | Candidates considered | Choice for this renderer |
| --- | --- | --- |
| Whole world | Miller; Robinson or Natural Earth for a balanced world outline; Equal Earth for area comparison | Miller is the least disruptive change because it retains rectangular wrapping and simple longitude recentering. Curved outlines remain optional visual alternatives, not a prerequisite for detail. |
| Broad continental view | Miller; region-specific Lambert conformal conic | Keep Miller. Selecting regional parameters adds transitions and ownership for little benefit to locating peers. |
| Small regional view, away from poles | Spherical Mercator | Useful if automatic projection changes are justified: preserves local shapes on the sphere. Keep its projection fixed within this mode and adapt only detail. |
| Polar view | Polar stereographic | Retain Miller in the small implementation so every location remains representable. A true polar map would be a separate product requirement. |

Mercator distorts area at world scale and is singular at the poles. The useful
property here is local shape preservation, not web-tile compatibility.
[PROJ Mercator](https://proj.org/en/stable/operations/projections/merc.html)
World compromise/equal-area and regional conic projections serve different
purposes; a globe hides half the world.
[Mapbox projection guidance](https://docs.mapbox.com/mapbox-gl-js/guides/projections/)
Polar stereographic supplies a dedicated polar alternative.
[PROJ stereographic](https://proj.org/en/stable/operations/projections/stere.html)

### Smallest defensible adaptive implementation

1. Choose the mode from visible geographic coverage and latitude. Use separated
   entry/exit thresholds to prevent repeated switching near a boundary; select
   those thresholds from the actual viewport and visual comparison, not an
   arbitrary universal zoom value.
2. Finish the gesture using the current scene. Prepare the candidate with the
   same geometry owner, retaining the geographic anchor and a defined local
   scale. A projection change still changes surrounding shapes; anchoring cannot
   make it visually identical. Inverse conversion and explicit valid-domain
   clipping become real requirements. Never clamp polar peers onto a false
   Mercator boundary.
3. Publish land, routes, markers, gradients, bounds and hit geometry together.
   Retain only the active caches and temporary replacement work. Rate changes
   and frames with ready geometry perform no projection calls.
4. Prefer the precomputed projected hierarchy for these fixed built-in choices.
   If retaining the generic geographic hierarchy instead, keep detail selection
   projection-aware without breaking common borders.
   The earlier global `MaxStretch` is conservative, but applying Mercator's
   polar worst case everywhere would waste the existing detail savings.
   The binary has matching vertex levels, not shared-arc identities: independently
   simplifying each country can create cracks. For regional preparation, use
   one common, conservatively bounded geographic window, clip all geometry to it
   consistently and apply one bound to that window. Its preparation margin must
   cover navigation until replacement geometry is ready. Projection-specific
   shared-border hierarchies are another option, but require additional asset
   preparation/topology and are not a free extension of the current format.

Even fixed Miller needs projected-error accounting; its global derivative bound
can retain unnecessary detail. The four-member interface below therefore describes
a conservative boundary, not an efficient final LOD policy. Do not adopt a
conservative formula that passes visual checks by drawing substantially more
geometry everywhere.

One existing opportunity worth measuring before adding adaptive projection work
is separating fill and border realization lifetimes. `Realized` recreates both
after a scale change above five percent. Country fills are already polylines and
their shape changes with detail; border stroke widths genuinely depend on scale.
Reusing the fill within the same detail may avoid work. No gain is claimed before
measurement, and this audit makes no implementation change.

The speed target is **no regression against the current optimized baseline** in
ready-scene rendering or interaction, at equivalent visible quality. Zero
projection calls on a ready frame is achievable by design; zero added GPU cost
or switch latency is not established by source inspection. Existing captures
exclude GPU execution and do not establish a before/after result for unimplemented
projections. The six-millisecond rebuild deadline is checked between operations
and starts after `Arrange`, so it is not a hard frame-time guarantee. Compare
preparation latency separately from steady rendering before adopting a change.

## Primary-source findings

### Projection choice changes the map's edges

Miller is a spherical cylindrical projection with a simple forward formula:
`x = longitude`, `y = 1.25 * log(tan(pi / 4 + 0.4 * latitude))`, using radians and
unit radius. Its latitude endpoints are finite. It supports central longitude;
the documentation identifies world maps as a use. Inference: a rectangular
world and a constant horizontal repeat distance suit the existing map's
navigation. [PROJ: Miller](https://proj.org/en/stable/operations/projections/mill.html)

Robinson is a global spherical pseudocylindrical projection and also accepts a
central longitude. [PROJ: Robinson](https://proj.org/en/stable/operations/projections/robin.html)
Its implementation interpolates tabulated polynomial coefficients for the
forward transform and uses iteration for the general inverse. Horizontal scale
varies with latitude. Inference: translating a second copy by one constant
width cannot join every latitude along the curved boundary; the renderer must
handle a bounded world instead of assuming seamless rectangular repetition.
[PROJ Robinson source](https://github.com/OSGeo/PROJ/blob/master/src/projections/robin.cpp)

Roussilhe should not be included as an interchangeable world overview in this
proposal. The current documentation calls it pseudocylindrical and global,
while its implementation identifies it as azimuthal/ellipsoidal and evaluates
polynomial expansions about an origin. The implementation author's manual
places it under oblique stereographic projections. These sources disagree;
catalogue metadata alone does not establish suitability for our complete-world
view. No precise valid geographic radius is claimed here.
[PROJ Roussilhe page](https://proj.org/en/stable/operations/projections/rouss.html),
[PROJ Roussilhe source](https://github.com/OSGeo/PROJ/blob/master/src/projections/rouss.cpp),
[Evenden's libproj4 manual, pp. 109–110](https://kartenprojektionen.de/download/evenden-libproj4_manual.pdf#page=109)

### Point conversion is only part of rendering

D3's geographical renderer separates spherical clipping at the antimeridian
or a small circle, projection, adaptive resampling and planar clipping.
Its resampling tolerance is expressed in pixels. This is evidence for the
mathematical responsibilities, not a proposal to adopt browser code or D3's
stream architecture in this Windows application.
[D3 projection operations](https://d3js.org/d3-geo/projection)

Its resampler checks projected midpoint error, angular separation and midpoint
position, with a recursion bound. Inference: transforming only existing
vertices can make a long edge visibly straight where the projected path should
curve; culling and simplification must use a screen-space error budget. The
amount of extra geometry depends on the projection, zoom and source edges.
[D3 resampling implementation](https://github.com/d3/d3-geo/blob/v3.1.1/src/projection/resample.js)

Win2D recommends retaining `CanvasCachedGeometry` for repeated drawing instead
of recreating it each frame. These resources store flattened geometry in a
GPU-oriented format; tighter flattening tolerance increases accuracy, memory
and preparation cost. This supports retaining the current cached rendering
approach while changing the coordinates used to construct it. Projected source
coordinates also need a compact unit scale: Win2D can clip cached geometry
outside roughly plus or minus 524,287 DIPs even when the eventual drawing
transform scales it down. A backend returning world coordinates in metres
therefore needs normalization before geometry creation.
[Win2D cached geometry guidance](https://microsoft.github.io/Win2D/WinUI3/html/T_Microsoft_Graphics_Canvas_Geometry_CanvasCachedGeometry.htm)

### What PROJ would supply

PROJ supplies coordinate transformations and more than a hundred projections
under the MIT licence. It is a candidate mathematical backend, not a replacement
for PeerMap's path construction, interaction or Win2D rendering.
[PROJ purpose and licence](https://proj.org/en/stable/about.html)

Its C API can construct a projection directly with `proj_create`; a CRS-to-CRS
operation is a different setup path. CRS axis order and units can differ, and
`proj_normalize_for_visualization` provides traditional longitude/latitude
ordering for such operations. Keep that distinction inside the integration
boundary. [PROJ C API quick start](https://proj.org/en/stable/development/quickstart.html)

The API offers both `proj_trans_array` and `proj_trans_generic`. The latter
transforms strided coordinate arrays in place. The array API attempts all points
and marks failed coordinates with `HUGE_VAL`; a conversion failure does not
split or repair a line. `proj_trans_bounds` densifies a bounding rectangle's
edges; its result is not a projected polygon or an arbitrary projection's
clipping boundary. Angular-input/output queries distinguish radians from other
units. Resolve conventions when creating the operation, not for each vertex.
[PROJ transformation API](https://proj.org/en/stable/development/reference/functions.html#coordinate-transformation)

`PJ_COORD` is a four-double union even for two-dimensional transformations.
A `PJ_CONTEXT` and its operations belong to one thread. Consequently a native
backend would retain its operation for its lifetime and use batches to amortize
interop; it must not create a context per point or share one concurrently across
workers. [PROJ types and threading](https://proj.org/en/stable/development/reference/datatypes.html)

Modern PROJ's build requires SQLite; TIFF grid support and curl network support
are optional. Actual deployed size depends on packaging and selected features,
and was not measured. This is more dependency work than copying the simple
published Miller equation into the app. Do not assume that a complete geodetic
database, remote grids or datum conversion are needed for a spherical world
display. [PROJ build requirements](https://proj.org/en/stable/install.html#requirements)

### Dependency decision

Recommendation: start with the small managed projection set and retain one
replaceable mathematical boundary. Adopt PROJ when the supported projection
catalogue or coordinate-system requirements justify its native deployment and
interop ownership. For Robinson, use a reviewed established algorithm and
coefficient set with its licence notice, rather than inventing interpolation.
If PROJ is chosen, use one implementation for each projection; do not keep a
second managed fallback for the same mathematics.

## Earlier manual-selector interface study

The research agent investigated established techniques. A separate design agent
independently inspected the renderer and proposed an interface before comparison
with the main agent's design. Both designs converged on batched projection
mathematics, shared geometry preparation and cached projected endpoints.

### Scope and interface

The proposed first scope is finite, uninterrupted, full-world projections with
one antimeridian seam: Plate Carree, Miller and Robinson. The seam can move with
the central longitude. This supports selecting implementations in a combo box;
it does not promise that every entry in PROJ's catalogue fits the same geometry
rules. Regional maps, interrupted maps and a globe's hidden hemisphere need
different domain/clipping support when requested.

Keep one internal interface in the existing app, near PeerMap. `GeoPoint` is
the existing degree-based value; `Vector2` is the existing projected point;
`Rect` is `Windows.Foundation.Rect`.

```csharp
internal interface IMapProjection
{
    Rect Bounds { get; }
    double? Period { get; }
    double MaxStretch { get; }

    void Project(ReadOnlySpan<GeoPoint> source, Span<Vector2> target);
}
```

The interface includes these obligations, not only its C# declaration:

| Member | Meaning and reason |
| --- | --- |
| `Bounds` | Finite bounds of the whole projected world, in compact map units, before pan, zoom or viewport fitting. Both fitting and navigation need the actual extent rather than 360 by 180. |
| `Period` | A positive horizontal translation that joins adjacent worlds at every latitude, or `null` for a bounded world. Plate Carree and Miller repeat; Robinson does not. Zero is not a sentinel. |
| `MaxStretch` | A conservative upper bound on map-space displacement per degree of source-coordinate displacement within a seam-free region. This lets the shared geometry owner use the existing geographic simplification levels without hiding larger projected errors. It is a mathematical bound for the chosen formula/normalization, not an estimated performance number. |
| `Project` | Write one projected point for each input into caller-owned storage of the same length. No allocation per point, callbacks, drawing, wrapping or clipping. One implementation dispatch for a batch, with the numerical loop inside that implementation. |

The shared geometry owner shifts longitude and splits at the seam **before**
calling `Project`. Input longitude is relative to the selected central meridian,
in the closed range -180 to +180 degrees; latitude stays in degrees. The two
longitude endpoints are deliberately distinct: they denote the two sides of
the cut. The projection must not wrap +180 back onto -180. These temporary
coordinates do not replace the original locations in the model.

Output uses unit-sphere map coordinates, with positive Y downward for Win2D.
There are no viewport pixels or Earth-radius metre values in the geometry.
For example, Plate Carree maps degrees to radians and reverses Y. Every
implementation returns finite coordinates throughout the agreed input domain,
including poles and both seam edges. The interior is continuous and does not
fold the world over itself; finite output alone is not enough. A projection
unable to satisfy that contract is outside this first scope; silently dropping failed polygon points
would corrupt fills. A later native adapter handles native failures before
publishing replacement geometry.

Implementations are immutable during use. Read their three properties when
selecting one, and retain that metadata with the map's active view. Keep the
choice's saved identity and localized label with the existing view options;
the numerical interface does not own text, persistence or menus. There is one
implementation of each formula. No generic plugin host or separate assembly
is required for these built-in choices.

`MaxStretch` deserves its place because the source already stores geographic
error bounds. For Plate Carree it is pi/180 with these units. For Miller, the
maximum latitude derivative is sec(72 degrees), so
`(pi/180) * sec(72 degrees)` bounds the full-world transform. Robinson's bound
must be derived conservatively from the selected coefficient implementation,
including its numerical tolerance, before adoption. Do not invent a constant
or compute a derivative per rendered vertex.

### One geometry owner

The shared implementation in PeerMap prepares geometry in this order:

1. Shift the geographic coordinates around the chosen central longitude.
2. Split lines and clip filled rings at that longitude's antimeridian. Preserve
   ring parity/holes, close fill fragments along the cut, and omit artificial
   cuts from stroked borders. The existing data also has artificial cuts at
   the original 180-degree seam; those remain distinguishable from coastlines.
3. Project batches, then refine edges where projected curvature is visible.
   Country edges follow the source polyline; connection guides retain their
   great-circle interpolation. Shared border segments must produce identical
   points in reverse order on both countries.
4. Construct the existing Win2D geometry and realizations, projected marker
   positions and projected bounds. Retain those results for subsequent frames.

`Distribution.Regions` also projects populated places to merge nearby ones in
the fitted view. It must use this same projection and fit transform. Retain
those projected places/merged regions for the current projection, meridian and
fitted size; changed traffic does not require projecting them again. Zoom alone
still does not redistribute estimated peers. The population allocation rule
itself is independent of projection.

Raw point conversion alone cannot replace steps 2 and 3. In particular,
Robinson's cut follows a curved projected meridian: joining a fragment with a
straight chord or wrapping its X by a fixed width produces the wrong map.
This geometry logic belongs to the shared implementation, never independently
inside every projection adapter.

The existing `Geography.Vertex.Level` records error in degrees, and `Arrange`
currently compares that to `.5 / ViewScale`. A nonlinear projection invalidates
that conversion. Retain the shared levels, using `MaxStretch` to choose a
conservative geographic threshold. Reserve part of the screen-space tolerance
for subsequent curve approximation; do not spend the whole tolerance twice.
Subdivision must account for long segments and curvature, not just one midpoint
that can happen to fall on a chord. Keep finite subdivision and detail limits;
at the asset's finest available detail, do not promise accuracy the bundled
source cannot supply. This avoids a new independent simplifier per country.

All projection-dependent geometry changes together: land, route paths, marker
positions, hit geometry, gradient endpoints, bounds and realizations. Reuse the
existing invalidation/disposal owner for projection or central-longitude changes.
Rate-only updates keep projected locations and paths. Adding a cached `Vector2`
to each displayed destination avoids the existing repeated coordinate calls
from markers, glow, culling and pointer hit testing. Its raw field payload is
8 bytes per destination: 80,000 bytes for 10,000 destinations, before any object
layout effects. Cache reuse must follow projection, meridian and location, not
just the peer's identity.

Culling uses the prepared path's projected bounds, expanded by its screen-space
stroke/glow margin. The current `Reaches` infers an arc's extent from its
endpoints' longitude; that is not a valid bound for every projected curve.

### Navigation and lifecycle

Ordinary dragging remains affine panning of prepared geometry. It does not
change the central meridian on every pointer move. Changing central longitude
rebuilds projected geometry; these are different operations and have different
costs. Cylindrical maps keep horizontal repetition. Robinson pans within a
bounded world, with the world centred on an axis where it fits the viewport.

Selecting a different projection returns to Fit, retaining the person's origin
as central longitude. This makes the new world shape visible and avoids treating
the previous projection's centre as coordinates in the new one. A later request
to preserve an arbitrary geographic point across projection switches could
justify an inverse operation; the current interaction does not.

There is **no inverse method** in the proposed interface. `Navigation.ToMap`
only reverses the affine screen transform. `OnPointer` tests projected marker
distances and `CanvasGeometry.StrokeContainsPoint`, so both continue using the
same geometry that was drawn. `FitScale` uses `Bounds`, and the inspector's
side-by-side/stacked comparison asks that same owner for fitting. There is no
second fitting formula in the view.

The current geometry preparation needs care before making it more expensive:
`Draw` calls `Arrange` before establishing its six-millisecond rebuild deadline.
Initial land tracing and newly created arcs therefore fall outside that budget.
Even the later budget is checked between whole operations, so one slow operation
can exceed it. Extend the existing staged rebuild mechanism to preparation as
needed; do not describe six milliseconds as a proven hard bound. If preparation
spans frames, publish a coherent projected scene, never new markers over old
land. Retain only the active projection's caches, apart from temporary replacement
work, rather than caching every combo-box choice.

### Alternatives considered

| Alternative | Decision |
| --- | --- |
| `Project(point)` alone | Attractive declaration, but leaves bounds, seams and error tolerance implicit. Calling it during drawing adds work with every marker and frame. The selected batch interface makes the intended cost and ownership explicit. |
| A projection implements its own complete renderer | Reject: repeats selection, animation, theme, caching and clipping rules, and makes visual fixes diverge between projections. |
| A chain of per-point stream callbacks | Useful evidence from mature mapping code, but unnecessary indirection for this native renderer. Keep one shared preparation implementation with ordinary loops and batches. |
| Static generic specialization for every projection | Reject without measured need: multiplies renderer instantiations to remove dispatch that the selected design already keeps out of ordinary drawing. |
| PROJ behind the batch interface | Viable when broader mathematics justify deployment. Handle radians, axes, native storage, lifetime and failures inside that one adapter; keep the shared geometry implementation. |
| Discoverable third-party DLLs | Separate scope: versioning, dependency resolution, loading and failure policy add complexity independently of projection maths. Discovery need not occur per frame, but it is unnecessary for the present combo-box choices. |

### Complexity increase

This is a moderate change to this renderer, not a new rendering architecture.
The numerical interface has four members and three small concrete implementations
(Robinson additionally carries coefficients). The substantial addition is one
shared implementation of seam-safe polygon preparation and projected error
control. It should replace the current degree-specific paths, not sit beside
them as a second renderer.

| Existing owner | Required change | Relative complexity |
| --- | --- | --- |
| `Coordinates`, `Trace`, `Arc` in PeerMap | One mathematical interface; shared seam handling, resampling and consistent projected output | Moderate; filled ring closure is the largest correctness risk |
| Destination, Paint, Outline and realization lifecycle | Retain projected endpoints/bounds; invalidate together on projection changes | Small to moderate |
| `Distribution.Regions` | Project and merge places in the selected projection's fitted view; reuse for traffic changes | Small |
| `Navigation.cs` and draw visibility | Replace hardcoded 180/360/90 assumptions with bounds and optional period; bounded Robinson navigation | Moderate |
| `InspectorPane/Peers.cs` and existing view options | Select/store a projection and use its fit dimensions | Small |
| Engine, protocol, peer-location lookup and population allocation | No new ownership or work | None |

After those shared changes, another projection satisfying the same domain
contract adds its formula, metadata and one choice; it does not require edits
to the drawing loop. A projection with different cuts or a restricted domain
is a larger feature, and must not be advertised as a formula-only addition.

Local evidence: [PeerMap](../../app/src/Controls/PeerMap.xaml.cs),
[navigation](../../app/src/Controls/PeerMap/Navigation.cs),
[estimated-place spacing](../../app/src/Controls/PeerMap/Distribution.cs),
[geographic outline format](../../app/src/Models/Geography.cs),
[inspector arrangement](../../app/src/Views/InspectorPane/Peers.cs).

## Performance evidence and limits

No source establishes a reliable percentage slowdown for this app, its map
asset, GPU, peer count and zoom level. A number such as “1%” or “5%” here would
be invented. Separate three costs when evaluating the proposal:

| Cost | What can be established before measurement |
| --- | --- |
| Architecture | One implementation selection per batch avoids one virtual or native call per vertex. Cached projected geometry can keep the plugin out of repeated drawing. This is a design property, not an FPS claim. |
| Projection mathematics | Miller adds transcendental functions; Robinson's forward path is coefficient evaluation. Neither their relative timing nor total app slowdown follows from operation count alone. |
| Geometry and pixels | Curvature may require more vertices, different world bounds alter visible geometry, and clipping can change GPU work. These costs exist with or without a plugin boundary. |

The expected architecture penalty on a settled animation frame is negligible:
the proposed path adds **zero projection calls and zero projection allocations**
there. This is not a measured zero-percent slowdown. The useful distinctions are:

| Operation | Expected work under this design |
| --- | --- |
| Settled activity animation, hover or selection | Cached projected positions and paths; no projection calls |
| Pan with ready geometry | Affine transform and periodic-copy selection only; no reprojection |
| Pan revealing geometry not yet prepared at the current detail | Existing cache preparation may run, including projection if that outline needs retracing |
| Zoom or resize | Reuse projected geometry where its detail remains sufficient; resample/reproject only when detail changes, and update scale-dependent realizations as today |
| New locations, changed projection or central longitude | Batch projection, clipping, sampling and realization preparation; this is where additional latency occurs |

For `N` converted coordinates in `B` batches, dispatch scales with `B`, while
mathematical work scales with `N`. With `F` subsequent settled frames, conversion
is paid for the geometry change, not repeated `F` times. The extra GPU time
depends on the resulting geometry and visible pixels; the interface cannot
promise an identical triangle count across projections.

For later measurement, compare the same projection and same output geometry
through a direct call and the proposed boundary first, to isolate architecture.
Compare different projections separately. Report projection-switch/recentre
latency, warm-frame CPU and GPU time, allocation, and output-vertex count; a
single average FPS mixes initialization with rendering. This is a proposed
measurement scope only; execution remains subject to the owner's build/test
ruling.

Use the same geometry, DPI, viewport, theme, peer locations and activity when
isolating interface overhead. Compare the existing Plate Carree renderer with
Plate Carree through the proposed interface before comparing Miller or Robinson.
Report median and tail draw CPU times separately from preparation latency. The
existing [peer capture](../../app/src/MainWindow/CapturePeers.cs) has useful
fixtures, but its known-peer timing has no active arcs and explicitly excludes
GPU execution. It cannot establish complete frame rate or the performance of a
projection implementation that does not exist yet. A later focused comparison
needs active routes as well; no new general test framework is warranted.

## Source limitations

The online PROJ stable documentation identified itself as 9.9.0 when read.
The referenced PROJ source URLs track `master`; a later implementation must pin
the reviewed source/version and preserve its applicable notices. D3 source is
pinned to v3.1.1. No installed PROJ package, C# binding or shipped binary was
selected or evaluated. The Roussilhe documentation inconsistency is recorded
rather than silently resolved by assuming either source is sufficient.
