# Close destinations merge at the shown zoom

Status, 11 November 2026: implemented, built, and the peers and map capture
journeys pass; [Work left](#work-left) lists what remains open. The rules are in
the [peer location contract](../contracts/peer-location.md). The owner decided it:
"yes, plan the merge at the shown zoom. merge the paths if the cities are close
together!", and "close together is determined based on map rendering scale".

## What the person sees

Map destinations whose points lie closer than 25 DIPs on the screen show as
one map destination: one arc, one location light and one set of dots. Zooming
in separates them, and zooming out joins them, as established map applications
group nearby markers at each zoom level. Without this, a whole-world view of a
large swarm draws hundreds of arcs into one band. The band shows nothing that
one arc would not, and each arc costs drawing time and native memory. Opening
the all-peers map with 1,000 arcs, which needs the Maximum arcs choice raised
from its default of 200, added about 600 MiB of private memory; how much of it
the arcs hold is not measured. Each further 50 MiB of growth makes WinUI stop
the app for about 150 ms.

- **The merges change when the view rests**, a quarter second after its last
  change, as the glow and the guide refinement already do. A zoom changes them,
  and so does a vertical pan, because the map's horizontal scale follows the
  cosine of the latitude at the view's centre. A resize changes them at once,
  as it already re-places estimated regions. During a zoom or pan the arcs keep
  their last merges and are drawn coarser or finer, as now. The owner called
  this expected: arcs already look coarser or finer during a zoom until there
  is time to draw them again.
- **Between rests the merges hold still.** A place that joins merges at once
  with the nearest shown destination closer than 25 DIPs, or shows on its own.
  A place that leaves drops out; when the landing leaves, its other members
  merge again by the rule below. Peer counts that change never move an arc,
  because in a large swarm they change on almost every poll.
- **The merged arc lands at the member place with the most peers** when the
  view rests. It is a real place.
- **Its colour, brightness, dots and rank under the arc limit** come from the
  sum of its places' drawn rates, as if one place held all their peers; an
  estimated region counts its share, as it does today. The owner: "expected.
  normal".
- **Its detail on hover and the line under the speed totals** name its landing
  and count the others, for example "Brussels, Belgium and 4 more places".
  They show the totals of the peers that choosing it shows: each member place's
  peers once, with their whole rates. It is labelled country-only when any
  member is estimated, so country-only records keep their label. The owner:
  "expected. normal". A list of every name would repeat each city's country and
  grow without bound; choosing the destination shows every peer's place in the
  table.
- **One location light per shown destination.** Hover, the arrow keys and
  choosing address it. A light kept for each member could not be inspected on
  its own, and established map applications show one marker for a group. The
  owner judges this on the running map.
- **Choosing it limits the peer table to the peers of all its places.** The
  choice stays the places the person chose, until they choose again, clear the
  choice or change the map detail. Zooming, panning and peer updates never
  change the table's rows. Every destination that holds a chosen place shows as
  chosen. After a zoom-out, a chosen destination can also hold places that are
  not chosen, so a chosen mark means "some of the chosen peers are here".
  Choosing one estimated region marks every region of its place, because they
  all stand for the peers the table shows; today only the clicked one is. The
  speed line and the map's accessible description describe the chosen places
  as one destination, named and counted as above, with the totals the table
  shows. The arrow keys continue from the chosen destination while it shows,
  and otherwise from the first marked destination, as they do today.
- **A peer selected in the table marks the destination that holds its place**,
  by the same estimated placement as now: an estimated member is marked when
  the selected rows reach its region.
- **At a rest that merges routes**, the merged-away arcs and their flying dots
  leave together, and the landing's arc takes their rate.
- **The rule holds for every map destination:** cities with coordinates,
  estimated regions, and countries in Countries detail. At the fitted view the
  regions of one country never merge, because they already lie 25 DIPs apart.
  Regions of neighbouring countries can merge; each country keeps its own fill.

For the 1,000 cities of the benchmark, a 25-DIP merge leaves about 164 arcs at
the whole-world view, 333 at 2×, 566 at 4×, 769 at 8× and 918 at 16×. These
are grid estimates from the run's saved locations; real swarms crowd into fewer
places, which is not measured.

## The merge rule

- **Distance:** each point as the map draws it at the default view: longitude
  relative to the origin's meridian (`MapViewport.Coordinates`), times the
  shown scale, measured in a straight line with no wrap across the seam.
  `Regions` measures the same way. At the default view the seam is the map's
  left and right edges, so places on either side of it are drawn a map width
  apart, and their arcs leave the origin in opposite directions. After the
  person pans the seam into view, places on its two sides stay apart: a missed
  merge, never a wrong one.
- **Shown scale:** the view's horizontal and vertical DIPs per degree at its
  last rest: (ratio, 1) × zoom of the rested view × the current fitted scale.
- **Leader rule:** places are taken in priority order. Each joins the nearest
  landing closer than 25 DIPs, and otherwise becomes a landing. So landings lie
  at least 25 DIPs apart and every member lies within 25 DIPs of its landing; a
  line of places 20 DIPs apart does not chain into one destination across a
  continent. A grid of 25-DIP cells finds the near landings, because the places
  can number in the thousands.
- **When it runs whole:** when the shown scale or the origin's meridian
  changed since the last arrangement, and for a new subject or map detail,
  every place is placed most peers first. A new meridian moves the seam, so
  members kept from before could lie a map width from their landing. One
  worker serves every subject until the map unloads, so another torrent's
  landings never count. Equal peers are ordered by destination key, because
  the order of the groups' dictionary changes when it reuses a freed slot.
- **Between rests:** every placed destination that showed in the last
  arrangement keeps its landing, by destination key, while that landing shows.
  A key, not a peer place, because each region of an estimated place is placed
  on its own. The leader rule places only the places that joined and the
  members of a landing that left, most peers first, against the landings that
  remain. Placing every place again would not hold still: a member 20 DIPs
  from its landing and 18 DIPs from a later landing would move to the nearer
  one at the next poll.

## Owners

1. **The merge: `MapModel.Arrangement.Arrange`**, on the `MapWork` worker. It
   places the groups' destinations as now, estimated regions included, and then
   merges them by the rule above. The estimated distribution keeps its own rule
   and the fitted scale: which regions a country-only group's connections use
   is a probability statement, and the zoom must not change it.
2. **What a shown destination is: `MapModel.Destination`.**
   - It always lists its members, landing first; a single place is a list of
     one. An empty list never means "itself", because each state gets its own
     value (root `AGENTS.md`, code style).
   - Its key and point are its landing's, so a route keeps its arc across a
     regroup while its landing stays.
   - Its drawn download and upload rates are the sums of its members' drawn
     rates. One group times one share cannot express this, because the ratio
     differs for download and upload. Its colour, download or upload, comes
     from these drawn rates.
   - Its totals (name, peers, seeds, leechers, connections, active peers, rates
     and the country-only label) count each distinct member group once. They
     carry no place and no direction, so no reader can take the landing's place
     for the destination's, or the totals' direction for the drawn one.
   - The placed destination (group, point, share, area, rank) becomes the
     member type. The compiler then lists every reader of the old single place
     (`PeerMap.xaml.cs:203`, `:211`, `:491-492`, `MapMotion.cs:721-723`,
     `MapRenderer.cs:211` and `CapturePeers.cs:1819-1824`) and of the old
     direction (`MapRenderer.cs:1056`, `:1232`, `:1297`, and the glow's
     `Arranged.Of` at `:1609`).
   - `Presentation.Matches` compares each member by the facts it compares for
     a destination today: point, share, area, rank and group facts. Otherwise a
     regroup that moves a peer between two equal members keeps stale members.
   - The names follow the `/codebase-design` skill and
     [naming](../agents/naming.md).
3. **The shown scale: `PeerMap.Capture` and `MapWork.Prepare`.** Today the
   grouping receives only the fitted scale (`PeerMap.xaml.cs:338`). `PeerMap`
   keeps the rested view's zoom and ratio. They start at the viewport's view
   when the map loads, because the view survives an unload. `Capture`
   multiplies them by the current `_viewport.Scale`, so a resize follows at
   once: `Resize` raises no `Changed` when zoom, centre and ratio stay
   (`MapViewport.cs:93-94`). `MapWork.Prepare` arranges again when the shown
   scale differs from the last one.
4. **When the view rests.** The quarter second is today `MapRenderer.Rest`; it
   moves to `MapViewport` as the one constant. Each clock stays with its reader.
   The renderer measures from `_viewMoved` on the render worker, because it
   works on its own copy of the view (`MapMotion.cs:701-706`). `PeerMap` gets a
   one-shot `DispatcherQueueTimer`, like `_navigationTimer`. The timer restarts
   on `_viewport.Changed` and stops in `Stop()`. Its tick stores the rested
   zoom and ratio and queues a preparation when they changed. A tick during an
   active drag does nothing, and `EndDrag` restarts the timer, because a
   release raises no `Changed`: established map applications regroup after the
   gesture ends, not under a held button. Today nothing
   prepares the map after a zoom of an idle subject: preparations start only
   from a size change, a model change or a new surface (`PeerMap.xaml.cs:92`,
   `:228`, `:258`).
5. **Selection marks: the shown destination.** It is marked when any member's
   reach is greater than that member's rank. `Presentation.Marks` computes each
   member place's reach from that member group's regions. `Highlight.IsSelected`
   (`MapRenderer.cs:210-211`) calls it, so its readers follow:
   `MapMotion.cs:716-717` and `:1055`, `MapRenderer.cs:1047` and `:1288`, and
   `PeerMap/Navigation.cs:40` and `:237`.
6. **The choice: `Inspector.Peers.Place`.** It becomes the set of chosen
   places: compared by its members, null for no choice, never empty. `Filter`
   keeps the peers of any of them, and `PlaceChanged` carries the set.
   `Highlight` holds the chosen places instead of one key, and a destination is
   chosen when it holds one. That replaces the one-destination lookups in
   `MapMotion.cs:721-723` and `PeerMap.Chosen`; `PeerMap` keeps the chosen key
   only as the keyboard position. `PeerMap.Described`, which the speed line
   reads (`InspectorPane/Peers.cs:490`), and the accessible description, which
   reads `Inspected` (`PeerMap/Navigation.cs:39-40`), describe the choice as one
   destination made of every placed member whose place is chosen. Its first
   member, which gives the name, is the one with the chosen key while it
   shows, and otherwise the first by key, so the name never changes between
   polls without the person. The keyboard
   zoom anchor in `ZoomInspection` stays a shown destination, because it needs
   a point on the map. `RefreshMap` still clears the choice when the detail
   changes.
7. **The detail text: `MapModel.Describe`.** It names the landing and counts
   the others with `peer_map.places_more_one` and `_other`, already in all six
   catalogues. A merged destination has no "Estimated near" part, because no
   single region holds it.

The hit test, the arcs, the glow and the dots draw shown destinations by key
and drawn rate, and need no rule of their own. A route whose key leaves parks
in `_departed` as now, and its flying dots leave with it (`MapDots.cs:72`).

## Defaults

- **The distance is 25 DIPs**, the existing `Spacing`, so one distance means
  "too close to tell apart" on the whole map. It is one constant. The owner
  judges it on the running map first: `app/tests/Performance.md` records
  roughly 500–700 arcs from the benchmark's 1,000 locations as the owner's
  expected collapse at its whole-world view, and 25 DIPs leaves about 164.

## Contract changes when it lands

Before editing [peer location](../contracts/peer-location.md), tell the session
that coordinates edits to it; the merge's text goes after its latest text.

- [Peer location](../contracts/peer-location.md):
  - "Coordinates from the installed city database are grouped at identical
    positions" gains the merge at the shown zoom.
  - "Estimated destinations are merged in fitted-map space, so zoom does not
    change their number or locations. Resize may still merge or separate
    destinations" becomes: the estimated regions stay in fitted-map space, and
    the shown merge changes the number of destinations when the view rests and
    when it resizes.
  - "Estimated-place spacing and inspector arrangement use the same fixed
    overview ratio, so navigation does not redistribute peers" is narrowed to
    the estimated distribution; navigation regroups shown destinations at its
    rest.
  - "Every location has one small light" and "The arc limit never removes
    location markers": a light stands for every place of its shown destination.
  - Choosing "limits the peers table to that place's peers" becomes the places
    of the chosen destination, and the choice stays across zoom and pan.
  - "A map-chosen route uses highlight ink" becomes every route that holds a
    chosen place, every region of a chosen estimated place included.
  - "An estimated destination shows the totals of the peers that choosing it
    shows" covers merged destinations and the chosen places.
- [Product vocabulary](../../CONTEXT.md): a map destination can stand for
  several peer places, and the chosen place becomes the chosen places.
- `app/tests/Performance.md`: "Current city grouping merges exact coordinates
  only; nearby-location merging remains a product finding" describes the merge.

## Work left

Every owner above is written, with the contract and vocabulary changes. The
capture journey "merged choice across zooms" passes: the merged choice splits
on zoom-in, joins again on zoom-out, keeps the table's rows, and a rate-only
update changes no merge. A five-pair round at an arc limit of 1,000, in Cities
detail, measured peak private memory 903.5 to 714.9 MiB and the all-peers map's
first picture 3,361 to 2,692 ms, both better in 5 of 5 pairs, and pans
smoother. Open:

- The owner looks at the whole-world and zoomed maps on the running app, with
  the distance and the merged lights first, and chooses a merged destination
  and zooms.
- The round measured some measures worse, and their cause is not known yet:
  torrent clicks' worst UI response 167 to 197 ms and the selected-peers map's
  first picture 269 to 279 ms, both worse in 4 of 5 pairs, and zoom-in and
  zoom-out navigation updates per second 46.8 to 45.7 and 47.1 to 44.2.
