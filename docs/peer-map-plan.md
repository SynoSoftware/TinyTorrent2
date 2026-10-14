# Peer map implementation

This is the delivery record. [Peer location](peer-location.md#map) owns map
behavior and precision; [Interface](interface.md#inspector-and-edits) owns its placement.
The existing protocol and window-layout owners retain their responsibilities.

The Peers inspector follows `app/peers-prototype.html`: two Lucide toggles in its
existing header show Map, Table or both. The combined arrangement automatically
fits the larger map and reuses Splitter; the existing inspector height adjustment
stays. Coordinates use shared-location rendering; country-only records use the
approved estimated distribution. Selection determines the torrent scope.

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
The combined arc layer fades near the origin only above eight rendered arcs,
reaching its full adjustment at twenty. Location marks remain outside the mask.
Hover and keyboard inspection expose counts, precision and both measured rates;
the table remains available for individual endpoints.

## Delivery and verification

1. Extend the lookup and Inspector data flow; inspect stale-context, reconnect,
   no-selection and hidden-view paths.
2. Add the map and compact header toggles; retain table selection, translated text,
   theme resources, saved layout and independent split proportions.
3. Review the final source and diff. Do not compile, launch or run tests without
   the owner's request. Record runtime and performance gaps explicitly.

## Implementation status

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
| Hover and selection | Present | `OnPointer` checks generous marker targets, arcs and populated country shapes; `Describe` shows counts, both rates and precision. Table selection highlights the map. Map inspection does not filter or replace table selection. Individual IP diagnostics stay in the table. |
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
