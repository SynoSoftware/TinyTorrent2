# Peer location

The Peers table shows where each peer is in separate Country and City columns:
its country as a flag and name, and its city when the downloaded data provides
a name. This document owns the data, the lookup and the build step; the
[torrent interaction contract](ui/torrents.md#peers-and-connections-map) owns the
columns, [Settings](ui/settings.md#cards-and-categories) owns the dataset row,
and [licences](licences.md#db-ip) owns the attribution.

## Data

Both files come from [DB-IP](https://db-ip.com/db/lite.php), CC BY 4.0,
without an account or key, in the MaxMind DB format: a memory-mapped binary
search tree that answers one address in a few microseconds without an import.
The app reads the files in place with the MaxMind.Db package; nothing is
copied into SQLite, because the format is already the lookup structure.

| Data | File | Size | Source |
| --- | --- | --- | --- |
| Country | `Assets/dbip-country-lite.mmdb` beside the app | 8 MB compressed | Installed with the app |
| City | `dbip-city-lite.mmdb` beside `library.db` | 121 MB compressed | Downloaded in Settings |

DB-IP publishes both files once a month under a fixed name,
`dbip-<kind>-lite-YYYY-MM.mmdb.gz`, during the first hours of the month.

## Country data in the build

`app/CountryData.ps1`, run by the `CountryData` target of the app project,
keeps this month's country file in `artifacts/data/` and copies it to the
output's `Assets` folder when its hash differs. It downloads at most once a
month; the previous month stands in while DB-IP has not published yet; without
network it uses the newest cached month and warns; it fails only when no copy
exists, so a release never ships without flags. The publish step and the
installer's presence check carry the file into the installation.

## City data in Settings

The City data row sits under IMDb data in Plugins and is the same control,
`DatasetRow`, fed by the same contract, `IDataset`, that Library fulfils for
IMDb data and `Geolocation` fulfils for city data. One pair of dialogs,
worded from the dataset's text section, asks before an update or a deletion.
The download goes through the shared HTTP owner, so the saved proxy and
adapter apply, and through search protection under the `dbip` catalogue. It
fetches this month's archive, or the previous month's when this month's does
not exist yet, unpacks it beside the installed file, checks that the result
opens as a database, flushes it to disk and moves it over the installed copy
under the lookup lock. Failure or cancellation before that keeps the previous
copy. A reply that asks the app to wait, such as HTTP 429, records the hold:
the failure names the time to try again, and Download sends nothing until
then. A route change or a protection reset interrupts the download and
reports it. Both behave as they do for the IMDb download. The status bar
shows the phases: downloading, unpacking, installing.

A copy from the current month counts as current, so Update warns that the
download would most likely transfer the same file.

## Lookup

`Geolocation` answers one address from the country file and, when installed,
the city file. The Inspector looks a peer's address up when the peer appears
and again only when the city data changes, because the address never changes
and the names come in every language DB-IP ships, so a language switch needs
no new lookup. The worker that reads a torrent's reply looks up the peers that
joined, so rows appear with their locations, and a newer click cancels the
lookups with the read. A torrent's peers looked up in older data are looked up
again by its next reply, and a change of data subscribes again, so the shown
torrents reply at once. Only the shown torrents' peers are looked up. The map
origin, the public address, has its own lookup. An address the
data lacks, a web seed's host name or an I2P destination has no location.

## Flags

The flags are glyphs of `lib/Glyphs/src/Fonts/twemoji.ttf`, a flags-only
subset of Mozilla's Twemoji colour font: 184 KB for the flag of every
two-letter region code, vendored with its
licence. It is a COLR version 0 colour font, which Windows draws in colour
the way it draws its own emoji. `Flags.Glyph` turns a country code into the
pair of regional indicator characters that the font joins into one flag, and
the Country cell shows that text in `Flags.Font` before the name. Only that
text block uses the font; the name beside it stays in the app's font.

## Map

`Inspector.Places` is the chosen filter for both views. Inspector captures the
immutable peer-list reference and filter intent on the UI thread, and finds the
chosen places' rows on a worker only when a join, a leave or a location can
change them. That search runs one at a time. A call during a search leaves the
work to a follower that reads the latest peers and place, so a burst of replies
searches the population once more, not once per reply. For the map it gathers each reply that joined or removed a peer or
changed a peer's role, state, rates or location, independently of
place-filtered rows. A new
list that no reply describes, such as a new scope, makes the map group every
peer again. The table takes each torrent's reply, but once the map has shown a
whole poll round of a subject, later rounds give the map their gathered changes
once, when the round ends, because each publication draws the whole picture
again. Until then each reply publishes, so a new subject's map fills while its
first round loads. While a new subject's rows are unread, the map keeps its last
picture rather than an empty one, which would release every arc only to build
them again. Map settings reuse the accepted population, and an update that
changes none of the map's facts does not prepare the map again. `MapModel` takes the
population and its changes without enumerating peers or relabelling an old
subject. One serialized `MapWork` worker owns grouping and estimated
distribution. It keeps each peer's contribution to its groups, so a change takes
exactly that off and adds the peer's published record and location; a new
subject, detail, language or population groups every peer again, and so does a
round of changes that names more peers than the population holds. The worker
keeps its own copy of each contribution rather than the update, because the map
skips and merges updates: a superseded preparation, a hidden map and a cancelled
worker each drop some. Taking off what it added and adding the current values
gives the right totals however many updates it missed, and subtraction is exact
because the engine sends rates as whole bytes per second. A group's estimated
mark counts its members that have no point, so taking such a peer off can clear
it. It publishes
detached group copies that nobody subsequently mutates, copying a group again
only after it changed. The UI
captures size, appearance and selection intent; `MapMotion`'s dedicated render worker
owns native geometry, route resources and cached pictures. Its Win2D game loop
presents completed pictures, fixed-DIP markers and dots. Aggregation and native
rendering never run on the UI thread. Aggregation reads only each peer's
published immutable record and its location; rendering reads no peers.
Table selection passes its immutable packet without a UI copy. PeerMap counts
selected places on the thread pool, independently of aggregation, with one active
task and one latest request. It accepts only the current peer generation,
selection, subject, detail and map lifetime; clearing is immediate and unload
does not join the task. Accepted immutable place counts reach the render worker
through the existing handoff, where estimated destinations are resolved. Neither
UI nor presentation scans the selected peers, and blocked grouping cannot block
their highlights.
Hit testing reads detached line segments from the same sampled arcs that draw;
it neither creates nor releases native geometry on the UI thread.
`MapViewport` supplies detached transforms for preparation and inspection.
After drawing succeeds, the game loop publishes one detached inspection, view
and surface identity, including frames without moving dots. Each interaction
captures that value once and resolves current intent within its destinations;
subject, detail, surface and lifetime guards exclude stale content. UI
notifications refresh descriptions from the current display without installing
captured targets. New grouping never replaces the person's interaction intent.
Drawing and pan alone do not regroup peers. Changes that leave every peer's map
facts equal retain the presentation and picture.
A different subject or map detail cancels pending aggregation; compatible
telemetry coalesces its next pass. Navigation, hover and selection publish
independently of aggregation.
Unload cancels without waiting and retires the worker when its current
preparation ends. The render worker releases geometry when its native work ends
and retires returned pictures after the game loop stops. Each picture owns its
prepared marker, halo and moving-dot textures for its device and DPI; the render
worker creates and destroys them through the same cached, pending and shown
reference accounting. The stopped callback returns ownership and signals
completion without native destruction. Inspector retains plain map state.

The render worker prepares route membership, rates, trips and dot-size inputs
once per traffic revision. The game loop first advances the old rates to the
adoption time, then updates surviving lanes in place, preserving carried bytes
and flight state. It owns simulation, departure timing and size hysteresis.
Visible marker drawing still traverses the destinations; no marker
layer cache is retained.

Rate-only observations retain peer membership, groups and unchanged destinations.
The model still rebuilds counts and rates through one aggregation rule; changed
membership, locations, detail, language or geography replaces the grouping, while
fitted scale and meridian refresh estimated placement. The renderer retains
ordered routes when the destination keys remain the same.

The Peers inspector's ellipsis menu selects Map only, Table only or Map and table.
Right-click anywhere in Peers opens the same menu, on the map, its Speed readout,
the table's rows and its empty space; the table's column headers keep their own
menu. When it opens with Table only, it shows only these three choices, because
every other choice changes only the map. Its choices keep it open under the
[choices ruling](ui.md#native-controls-and-visual-authority), and so does Get
city data, so no entry closes it; a click outside closes it. It keeps the
entries it opened with until it closes, so choosing another layout in it does
not move it.
Its Map detail group selects Countries or Cities. Countries aggregates every
peer by country using estimated distribution; Cities uses supplied coordinates
and falls back to estimated distribution for records without coordinates.
Only the visible Peers inspector requests libtorrent's full retained IP peer
list with its live connections in one asynchronous snapshot. Ordinary torrent
updates carry counts, never peer lists. A selected torrent limits the request
to that torrent; the existing global view aggregates only while it is open.
Hiding the inspector or leaving Peers withdraws its demand. Disconnected
addresses appear as Known in the table, with no connection statistics. Connecting and handshaking peers
also keep their locations. Transfer rates alone determine activity.
Cities is disabled without installed city data. Get city data starts the existing
dataset download directly, with progress in the application status bar, so the
person need not leave the inspector. It is disabled during a download and omitted
once the data is installed. The chosen map detail is saved with the peer view;
Countries is the default and the fallback when city data is removed.
When both are visible, the inspector compares the fitted map scale in each
arrangement and uses the larger one. `MapViewport` owns that scale calculation,
so layout and projection agree. Combined views retain separate split proportions,
using the same accessible Splitter as the inspector frame. Coordinates
from the installed city database are grouped at identical positions.
Destinations closer than the place grouping distance at the shown zoom show as
one: one arc, one light and one set of dots, landing at the place with the most
peers, as map applications group nearby markers at each zoom level. The map
menu's Place grouping submenu offers Less, Normal and More: 25 DIPs at Normal,
half at Less and double at More, saved with the peer view and starting at
Normal. Zooming in separates them and zooming out joins them. The distance is
measured on the map as the default view draws it, with longitude relative to
the origin's meridian and no wrap across the seam, because places on either
side of the seam are drawn a map width apart. Each place joins the nearest
landing closer than that distance or becomes a landing, so landings lie that
far apart and a line of close places does not chain into one destination. The
merges change when the view rests, also after a vertical pan, which changes the
horizontal scale, after a held drag only once it is released, and at once on a
resize or a new place grouping. They are made again whole for a new scale,
meridian, subject, detail or place grouping. Between those, each place keeps its landing while that shows, so peer
counts that change on every poll never move an arc; a place that joins merges
with the nearest landing or shows on its own. A merged destination's arc,
colour, dots and rank under the arc limit come from the sum of its places'
drawn rates, each estimated region counting its share. Its description names
its landing and counts its other places, with the totals of each place's
peers once, and says country-only when any place is estimated. A city name or
a bundled country polygon is not required to plot a supplied coordinate. The
projection fits the full latitude/longitude range with the origin's longitude
in the centre, or 0° while the origin is unknown, as map applications centre
the person's own location. A shortest route from the centre spans less than
180° of longitude, so it stays between the map's edges. Country-only records use estimated distribution and remain labelled
country-only. In Cities mode both precisions can coexist, according to each record's
available coordinates.
The engine's public address uses the same local lookup for the origin; no
external lookup request is made, and an unknown origin produces no arcs.

Estimated distribution aggregates every record in Countries mode, or each country's
country-only records in Cities mode, and their
measured rates, and merges bundled populated places within 25 DIPs into
regions. It shows the most probable placement of the group's active
connections, or of one connection when every peer is idle, taking each peer to
be in a region with probability proportional
to its population: each next connection goes to the region with the highest
population per connection already there, counting the new one. For regions
with 90% and 10% of the population, two connections both go to the first,
because that is 81% likely against 18% for one in each; the second region first
appears with the tenth connection. The regions used divide that group's
bandwidth by their population weights. Their locations and allocated speeds are explicitly estimates.
Every country code the bundled country lookup returns has a map entry, keyed by
that code. Each entry holds the populated places that the most probable
placement of 200 connections, the engine's default connection limit, would use;
places beyond that never receive a connection. A territory that lies inside
another country's outline, such as Réunion, has places but no outline. A
country without populated places shows one point at its anchor. Records
with coordinates in Cities mode keep their measured rates at those coordinates; they do not
also contribute to the estimated arcs.

The map reads the bundled `world.bin` when it shows and releases it when it
hides. Every outline point carries the largest error, in degrees, that leaving
it out causes, so the map draws only points that would move a border by half a
DIP or more at the current zoom. A border two countries share is stored once
and drawn once, by one of them, so it is a single line at every zoom; the 180°
cut in the data is never drawn.

Country fills add their antialiased coverage on the transparent geography
surface, so adjacent shaded countries meet without a source-over blending seam.
Borders and routes retain their normal blending.
**Owner ruling: preserve speed over the country artifact.** Broad gaps and
overlaps between shaded countries still show in places. They stay wherever a
correction would slow navigation or complicate the renderer, so they get no
further probe, geometry change or redesign of how a picture is handed over.

Outline tracing yields at bounded source-point, ring and border-piece
checkpoints. A map traces every country that has no outline yet before it draws
its land, without the renderer's rebuild budget, so the map's first picture
shows all of its land before any arc. The border pieces that this first
picture shows take their first stroke without the budget, so it shows the
borders with the land. Tracing the world takes about 0.1 s on
the worker, once per map. Retracing at another detail checks the budget at each
checkpoint before continuing. A complete
old outline stays visible until its replacement is complete; nearby strokes are
prepared within the same budget. A native tessellation call runs to completion.
Superseded work releases its native resources on detail, meridian/device change
or unload.
Fills keep their complete Alternate polygons and holes, clipped to a window
three views wide and high. The window moves after half a view of panning, so
dragging reuses the cropped coastline geometry. A crop that no longer covers
the view is replaced before drawing.

Win2D draws bundled Natural Earth geometry and great-circle guides, not measured
network paths. Guides adaptively subdivide curved segments to a quarter-DIP
tolerance at their retained scale. Zooming in refines only visible guides in
powers of two within the rebuild budget; panning and zooming out reuse that
detail. The existing guide stays visible until refinement finishes. Guides,
country outlines and the glow are rebuilt for a new view only when it rests, a
quarter second after its last change; while it moves they keep what they have,
and the glow moves with the map. A zoom reaches a new detail every few frames,
so work done while the view moves is soon replaced, and doing it made each
drawing several times longer. Their
overview length stays fixed for dot timing, and both drawing and
motion use conservative analytical bounds at polar crests. Active arcs use a
fixed logarithmic speed scale and the app's
transfer colours; idle markers remain visible in both detail modes. The map menu's
Maximum arcs choice limits active destination arcs, starting at 200. When more
destinations transfer, retain the fastest by combined download and upload rate,
using each estimated destination's allocated share. Equal speeds keep destination
order. Only retained arcs carry moving dots. All peers, location markers, table
rows, country shading and aggregate speed values remain available. This choice
is saved with the peer view and never changes the engine's network connections.
Every shown destination has one small light, combining the peers of all its
places regardless of role or connection state. Markers sit above geography and below
the arcs; moving transfer dots remain above the arcs. A 3-DIP solid core has a
subtle soft halo, white in Dark and black in Light, using the theme's foreground
ink. Chosen, selected and pointed locations enlarge by half without outlined
rings. High Contrast uses solid system foreground ink with no halo. Hit targets
retain their existing size.
The place description supplies seed, leecher and connection counts. The arc
limit never removes location markers, including destinations with only known peers.
The map opens no tooltip, because one over the map hides the routes around
the pointer. The line under the
[Speed readout](ui/torrents.md#peers-and-connections-map)'s totals describes
the place under the pointer, or the one inspected with the arrow keys, and
otherwise the chosen places: a map pin, the place, its connections (peers not
merely known), then its download and upload rates in the totals' colours.
The chosen places are named like a merged destination, by the chosen
destination's landing while it shows and otherwise the first by key, so the
name never changes between polls on its own.
Connections and each rate show only when above zero, because "0 connections"
or a dash tells the person nothing. It describes no place while the map is
dragged. It and the map's accessible description change with the displayed
places and their highlight, not with each new picture, so panning and zooming
add no UI-thread work for them. An estimated
destination, a merged destination and the chosen places show the totals of the
peers that choosing them shows, not an estimated share.
Land and boundaries use the platform's secondary control surface and strong
control stroke. Arcs and moving transfer dots use the same accent text and
upload brushes as the table and speed graph. Location lights retain their
theme's foreground ink, except when a table selection mutes other connections.
Country fills stay subdued.
Countries have no hover or selection state; inspection targets location markers
and arcs. Every selected table peer highlights where the map shows it. In
Cities detail a peer with coordinates marks its location and route. A row
without coordinates, and every row in Countries detail, marks its country's
estimated destinations by the same placement, with the selected rows as the
connections, up to the destinations shown. This is a best guess, like the
distribution itself.
While the peers table has selected rows, their mapped connections keep their
download/upload colours. Other arcs, glow, moving dots and location markers
draw in grayscale at one quarter of their normal opacity. The existing shared
location and estimated-placement rules decide which destinations represent the
selected rows. Clearing the table selection restores normal colours and opacity,
including cached glow. This changes appearance without restarting traffic or
activity fades. High Contrast retains its solid, undimmed system ink.
Choosing a location on the map, by clicking its marker or route or with the
arrow keys, limits the peers table to the peers of its places in every
connection state: the whole country in Countries detail, one coordinate in
Cities detail, or the country's records without coordinates for an estimated
destination, and each place a merged destination shows. The chosen places stay
while the map zooms and pans, whichever destinations then show them, so the
table keeps its rows. Every destination that holds a chosen place shows as
chosen, so after a zoom-out a chosen mark means some of the chosen peers are
there, and choosing one estimated region marks every region of its place. The
arrow keys continue from the chosen destination while it shows, and otherwise
from the first marked one.
Any other table filter applies only while nothing is chosen. Clicking empty
map, pressing Escape, changing the map detail or choosing Table only shows
every peer again. Selecting rows in the limited table keeps the choice.
A choice changes only the table's rows, so the inspector announces only them
and the table takes them in a later dispatcher turn, never inside the click
and without refreshing the rest of the inspector.
Active country arcs keep visible endpoints; idle countries keep their markers.
The composited arc layer uses the [dimming treatment](#arc-dimming);
selected routes use the same transparency and fades with a two-DIP stroke.
They retain transfer colours while table rows are selected; every route that
holds a chosen place uses highlight ink when no table rows are selected. Location marks remain
outside it. Drawing stops when the map leaves the visible inspector.
Redraws follow changed observations, view transforms, inspection or appearance.
An unchanged, clamped pan or zoom does not request another frame. Navigation
requests redraw directly through Win2D so dragging does not wait for the activity
timer; that timer runs only while visible activity styling is interpolating.
The Peers table refreshes changed rows while visible and reconciles deferred
sorting when shown again, avoiding repeated work for identical samples or a
hidden table.
No simulation controls ship in the product.

### Moving dots

Dots travel along each active arc: download dots from the peer's location
to the origin in the download colour, upload dots from the origin outward in
the upload colour. The arcs show absolute speed; the dots show that data
moves, in which direction, and how much each route carries compared with the
others.

- **Meaning.** One dot stands for the same number of bytes on every route.
  Dots leave evenly at each route's measured rate, with no random gaps,
  because the rates are averages. A route shows no dot before its bytes are
  complete; bytes short of a dot carry over. A dot whose trip would already be
  over is not shown late.
- **Motion.** A route's trip takes its length on the fitted overview ÷ 160
  DIPs per second, within 0.5–3 seconds, so zoom changes how fast dots cross
  the screen but never how many are in flight. Fast moves at this speed,
  Normal at half of it and Slow at a quarter. A route shorter than 20 DIPs on
  the overview carries no dots, because a dot would never leave the fades at
  its ends; its arc and marker still show it is active.
- **Number.** The map holds at most one dot per 10,000 DIP² of canvas, and at
  least one; Total dots Low halves this maximum and High multiplies it by four,
  because twice as many dots on a sparse map do not look like more. The total rate of the routes that carry dots sets the target:
  that maximum × the cube root of total ÷ the ceiling, at least one dot.
  The cube root spreads the count evenly from a trickle to the ceiling.
  The dots of one direction on one route stay at least 15 DIPs apart.
- **Size.** The smallest value in the series 1, 1.5, 2, 3, 4, 6 … KiB that
  keeps the mean dots in flight at 80% of the target and every route within
  its spacing. It rises at once and falls only after ten seconds of lower
  traffic, so the legend does not flicker; a resize, a new animation
  choice and a new subject's first traffic apply at once, so another
  torrent's peers do not wait ten seconds under the last one's size. A new
  route starts its count somewhere within its first dot, spread evenly over
  the routes that start together, as a route in steady traffic is; from zero,
  a new subject's first dot left only when its busiest route alone had
  carried a whole dot, about 8 s. Dots
  launched before a change keep their size until they land.
- **Ceiling.** The connection capacity that Connection setup saved, when
  both directions are above zero; otherwise the highest total of this engine
  session, at least 1 MiB/s. A total above a stale saved capacity replaces it.
  Speed limits do not lower it, because they switch with the schedule and can
  be one-sided. The arcs keep their own fixed scale.
- **Mark.** A 4.5-DIP round head with a soft glow out to 7 DIPs across, and
  a tail that tapers and fades out 8 DIPs behind it, turned along the motion
  and tinted with the transfer colour. A longer tail would join the dots of
  a busy route, which sit 15 DIPs apart, into one line. A dot is hidden under the markers at its own two ends, fading in
  from 3.5 to 10 DIPs, and it takes the arcs' destination fade and origin
  mask, so converging dots cannot form a bright knot. It does not take the
  arc layer's 80% cap.

The map's [Speed readout](ui/torrents.md#peers-and-connections-map) shows
the dot size on the line under its totals, such as "•• 48.0 KiB per dot",
with a tooltip that explains the colours and directions. A described place
takes that line in its place, so the Speed readout keeps one height. This legend
shows also while no data moves and before the map has loaded, with the
smallest size before the first and the last size after traffic stops, so the
Speed readout never changes size on its own.

The peer-view menu's Animation submenu offers Off, Slow, Normal and Fast, and
its Total dots submenu offers Low, Normal and High. Both are saved with the
peer layout and start at Normal. Off draws the map still, without dots,
legend or arc fades, and disables Total dots. Windows animations off or High
Contrast on has the same effect and disables both submenus, because no choice
in them would change the map.
Windows preference changes apply while the map is open even when peer data
is unchanged. Returning to animation uses current rates; time spent with
animation off contributes no dots.

The dots keep moving at the display's frame rate while the app works, such as
while the Peers table fills. During a pan or zoom the dots are optional: they
keep moving while that costs the gesture nothing, they may pause or hide when
it does, and they resume without another input when the gesture ends, because
the gesture's own response comes first. The map shows in a transparent Win2D
`CanvasAnimatedControl`, whose own thread draws the latest picture of the map
and the dots over it, because the map and every table share the UI thread and
any long work there would freeze dots drawn on it. The map itself cannot lie
under that control: a WinUI 3 swap chain shows only the window's backdrop
through its transparent pixels, so the backdrop's acrylic stays but anything
drawn under it does not show. Each frame therefore starts with the colour
of every background between the backdrop and the map, composed as XAML draws
them, so the map matches the peers table beside it in every theme.

One dedicated render worker clears each leased picture's geography and arc bitmaps
to transparent before drawing the current map, so old coastlines and opacity never
accumulate. The presentation frame applies the composed background tint once,
separately from the map image, preserving the acrylic through transparent areas.
Navigation publishes an immutable view immediately. The presenter transforms
the latest completed image into that view while the worker draws its exact
replacement from vector geometry, independently of peer aggregation. Pictures
include one eighth of a viewport of extra geography at each edge; wrapped
copies meet at the world boundary. A pan that keeps the zoom draws no
replacement while the latest picture shows the whole view with half that
margin to spare and its horizontal scale, which follows the latitude, is
within 5% of the view's. The presenter's move of that picture already shows
such a view, and drawing one for every view repeated that work about 60 times
a second. The worker draws the replacement when the margin runs short, at an
activity-fade step, and when the view has rested for a quarter second. A zoom
draws at every step. Geography draws first, then location lights,
then arcs, then moving packets. Markers and packets draw in the current view at
their normal DIP sizes. The render worker prepares marker map
coordinates, theme tints and emphasis sizes with
each completion, reusing unchanged values. Each frame wraps and transforms these
coordinates in the current view, culls them and submits their native draws.
Lines, glow and their masks temporarily scale with the retained image until its replacement
arrives. Backdrop tint never takes that transform. Hit testing uses the displayed
view and its detached targets. View-only input neither prepares routes again
nor restarts activity fades.

The worker keeps geometry, brushes, tracing and rasterization off both WinUI
and the presentation loop. It draws on the game loop's `CanvasDevice`, and
Win2D's `CanvasSwapChain` takes that device's Direct2D factory lock
(`ID2D1Multithread`) to start each frame (`CreateDrawingSession`) and to
present it (`PresentWithSyncInterval`). One long Direct2D call on the worker
therefore holds the game loop's next frame, and the map freezes although the
UI thread is free: in 83 of 99 such freezes of one run the game loop drew
nothing. Reading the map's input off the UI thread cannot help such a freeze. It retains complete older outlines while finer
detail is prepared, and replaces stale borders without recording every
intermediate map picture. It sleeps between inputs and activity-fade steps.
When no visible dot flies and the latest picture has been presented, the
control's thread pauses until the next dot, activity-fade step or input, so slow
traffic wakes it once per dot.
When every dot-carrying route lies outside the shown view, it also pauses with
dots in flight. Conservative analytical bounds include polar crests, wrapped
copies and the sprite margin. Hidden departures need no wakes; a pending
ten-second dot-size decrease still wakes for the legend. Traffic and pictures
wake the loop, and a newer wake invalidates an older queued sleep decision.
Resuming integrates elapsed traffic without replaying expired dots and respects
the global dot ceiling. Animation Off ends accounting instead.
The UI thread wakes it, so the first dot after a pause can wait for a busy UI
thread; dots already in flight never wait. All dots are one sprite batch. A
control with a new device stays hidden until it has shown its first frame,
because until then it can show black.

The map's picture is drawn again only when the view needs a new one, as
described above, or the appearance, inspection, a pending
rebuild or what it draws of the places changes: their arcs, role kinds,
activity, colour and strengths. A strength change below one colour level is
kept for later, so a steady transfer redraws nothing. Border strokes that a
picture drew from another scale are replaced on the render worker.
Each drawing draws the whole map, so refinement that needs its own drawing
costs a whole map for the few strokes it changed. Each fade step therefore
draws once, and an arc width that a fade step changed waits for the next step,
which draws anyway. The glow is recorded once per fade step. An arc that a
fade's last drawing leaves at an earlier width, or that any drawing leaves
with a stroke or core brush made for another scale, waits in a queue; passes
that draw nothing rebuild the queued arcs, as they do stale border strokes, and
the map draws once when the queue is empty. An arc made for another scale does
not ask for another drawing, because the arcs draw last: with more arcs than
the 6 ms allowance reaches, every drawing refused the later arcs, so the map
drew again without end and never rebuilt them. Without this rule a fade drew at every frame, 37
to 59 times in an idle second.
Geometry preparation uses bounded passes, reading the latest inputs between
them; no preparation is spread across UI frames. The glow is recorded again
only when what it shows can change.
The UI replaces one latest request without waiting. Navigation, appearance and
same-subject observations coalesce while a useful pass finishes, so continuous
updates cannot starve completed content. A different Inspector subject,
origin, detail mode, surface or device cancels incompatible work. The worker
checks cancellation between routes, countries and border pieces and immediately
before each publication. A publication is one immutable result containing its
bitmap, scene, inspection and source view. The presenter adopts that whole
result, so a later cancellation cannot separate a published bitmap from its
packet scene.

The first picture of a new subject is drawn once and published three times, so
its land does not wait for its arcs. The first publication holds the land,
country shading and borders, with no scene, markers or places. The second adds
the arcs, the scene and the places that have a connection. The third adds every
other place. The presenter does not draw the arcs layer of the first
publication, because the worker is still drawing it; the second publication
completes it. A cancellation between publications leaves
the last one on screen, and the next pass prepares and draws the picture again,
because the map counts its input as applied only after the last publication.
Each publication has an inspection with its own list of places, so hit testing
and the accessible description follow what is shown, and the display changes
once per publication. A later picture of the same subject publishes whole,
because three publications on every poll would make its arcs disappear and come
back.

The land publication holds all of the land and nothing is drawn twice. The first
picture of a map traces every country that has no outline yet before it draws
the land, outside the 6 ms rebuild allowance: tracing in slices took 14 to 24
drawings, each showing arcs over missing countries and drawing them again. The
retrace after a zoom waits for the view's rest and keeps the allowance, because a complete coarser outline
stays on screen meanwhile. The land publication also holds the activity shading
of estimated countries, because drawing it later would draw the fills twice.
The map takes a new subject when the next torrent's rows arrive and keeps its
last picture until then. When they arrive before the old subject's connections
are drawn, the old land stays on screen until the new subject's land replaces
it; when they arrive later, the old connections are published first.

Grouping returns only geographic facts; it cannot restore captured selection
or hover. The Inspector owns a subject identity that survives ordinary refreshes.
Table selection and map inspection remain immediate UI intent and are reconciled
against accepted destinations. An image retained from an earlier subject cannot
select peers in the replacement subject.

Only the worker creates, reuses or disposes map bitmaps. The presenter returns
its previous bitmap after presentation, and a replaced pending result returns
its bitmap at once. There are at most three full-size targets; incompatible
returned targets are disposed before another is created. Unload cancels work
without joining the worker on WinUI. The worker retires geometry after its
native call finishes and retires returned targets after the game loop stops.
Worker device-loss errors use CanvasDevice.RaiseDeviceLost to initiate recovery.
Country tracing prepares border strokes inside the overscan window before
publishing the complete outline. A visible arc creates its first cached stroke
once on the worker, avoiding repeated uncached tessellation when the refinement
allowance is exhausted. A border piece's first stroke takes the allowance like a
replacement, except in the drawing that first traces a map's land, and a piece
without one is drawn in a later pass: a country traced
at a new detail needs a stroke for every piece, and making them all in one
drawing held the device the game loop presents with for up to 2.8 s. Retained
strokes remain visible while the allowance replaces them for the current width
and scale. Refining an arc replaces its cached strokes, so they follow the new
guide, and keeps its core brush, which depends only on the end points, colour
and scale. The halo's brush depends only on its colour, so the map keeps one per
colour and sets the centre, radius, opacity and scale before each arc: a Win2D
command list keeps a radial brush by value and references only its gradient
stops. A route that leaves the shown routes keeps its arc and core brush for
its place's return and releases its strokes, which hold most of an arc's native
memory. `CanvasDevice.Trim()` does not release the committed pages of zeros
that appear when a map's first arcs show: 178 to 225 MiB within a second on
10 November 2026, not measured since the arcs became cheaper to draw. Only a
native heap trace can name their owner. The map reports ready only after the work left for the view's rest is
drawn, so a capture sees the finished map.
Rate-strength transitions keep the current smooth fade. A new observation
preserves the pending visible step's deadline, so frequent updates cannot keep
postponing all visible progress.

### Arc dimming

The map uses soft landings and restrained glow so destination activity remains
readable in crowded regions. Rendering comparisons remain prototype controls,
not product settings. Adaptive origin fading is a separate effect.

Render in this order, so overlapping glow cannot defeat the opacity limit:

1. Draw land, boundaries and restrained activity shading normally.
2. Draw active arc cores and glow with their existing fixed logarithmic activity
   scale and cyan/download or purple/upload colour. Halo opacity is
   `0.5 × 0.35 × strength²`, where strength is the interpolated activity level.
   Dim both the core and halo near their destination: strength rises from 30%
   at the endpoint to 100% at
   30 DIPs away, measured radially on screen, not as a fraction of route length.
   This multiplies the existing activity strength; it does not replace it.
   Apply the halo fade before one shared blur, keeping cores sharp above it.
3. Composite the arcs and glow into one isolated layer. Apply the adaptive
   origin mask to that combined result and cap the layer at 80% opacity.
   Setting each individual line to 80% would still let overlaps approach full
   opacity. Neither operation dims the land or country activity shading.
4. Draw location lights and the origin separately.
   They retain their visibility and hit targets. Map-chosen route cores use
   highlight ink when no table rows are selected; table-selected routes keep
   their transfer colours. Both remain inside the shared arc layer, retaining
   activity strength, destination fade and origin dimming without an additional
   opaque stroke. Selection and inspection never brighten the entire network.

Opacity means visible strength, not transparency: 80% opacity is 20%
transparency. The 30% destination multiplier is applied before the layer cap;
an isolated fully opaque core therefore contributes at most 24% opacity at
its endpoint before other existing attenuation. The shared blur softens the
halo's fade profile, so that endpoint value is not an exact halo bound.
Overlapping endpoints can still exceed that value, but their combined arc layer
cannot exceed the 80% cap.

The origin fade depends on the number `N` of rendered active destination arcs
after shared-location aggregation and estimated distribution, not raw peer
count. Count the complete set, not just the portion currently visible after
panning, so exploring the map does not change its brightness. For `N > 0`:

```text
target = clamp(0.8 * sqrt(5 / N), 0.10, 0.80)
t = clamp((N - 8) / 12, 0, 1)
originStrength = 1 - (1 - target) * t * t * (3 - 2 * t)
```

There is no origin attenuation through eight arcs; the crowded adjustment is
fully applied at twenty. From the origin, smoothly restore the mask from
`originStrength` to 1 over 40 DIPs. With no active arcs there is no arc layer to
dim. Keep a nonzero centre so connections remain visible at their origin.
Arc count controls the adjustment without a separate density detector. The
origin mask acts after compositing because many individually faint overlapping
arcs can otherwise form an opaque knot.

Zoom must not enlarge the 30-DIP destination fade, 40-DIP origin fade, halo,
line widths or markers. Activity changes retain the existing 1.6-second smoothing
when animations are enabled; navigation remains direct, and the only moving
marks are the [moving dots](#moving-dots). Dark and Light themes retain the same activity hierarchy. High
Contrast keeps the existing solid, visible lines without glow or dimming;
reduced-motion settings and the map's Animation Off bypass activity
interpolation.

### Zoom and pan

| Action | Mouse | Keyboard with map focus |
| --- | --- | --- |
| Zoom | Wheel around the pointer, with or without Ctrl; double-click zooms one step | Plus or Minus, with or without Ctrl, around the inspected location when present, otherwise the view centre; main keyboard and numpad |
| Pan | Left-drag beyond the drag threshold | Ctrl+Arrow, one eighth of the view per press |
| Fit to window | Corner button | Ctrl+0, main keyboard or numpad |
| View and map detail menu | Right-click or inspector ellipsis | Menu key or Shift+F10 |
| Inspect locations | Click or hover as before | Plain arrows continue location inspection and reveal an off-screen destination |
| Cancel drag | — | Escape restores the view from before the drag; outside a drag, existing selection clearing remains available |

Right-click shows the inspector's existing flyout instance, so right-click and the
ellipsis share commands, live state and the same icon-led native menu headings.
It shows the menu from the Peers area, not from the map, because choosing Table
only removes the map, and the menu stays open.

Zoom ranges from 1× to 128×, with 1.25× per wheel notch and proportional small
wheel deltas. A drag does not select a location. Show the SizeAll cursor only
after the drag threshold is crossed, restoring the normal cursor when the drag
ends or is cancelled. Input moves the view directly, without added easing,
inertia or a zoom animation. While a mouse drag is active, the map's game loop
reads the cursor at each frame and moves the view itself, so the map follows
the hand while the UI thread is busy; the control takes that view back when the
drag ends. The press, the drag threshold, the release, pen and touch drags, the
wheel and the keyboard use the UI thread's events, so a zoom still stops while
the UI thread is busy
([issue 585](https://github.com/SynoSoftware/TinyTorrent2/issues/585)). One table update is one such block: about 130 to 165 ms
at the stress load, in which the list lays out its rows and starts their
animations and no code reads input
([TableView implementation](../../lib/TableView/docs/tableview-implementation.md#winui-list-behavior-the-design-depends-on)).
The tables are not held for a map gesture: the owner approved holding the
peers table's new order while the map moves and then withdrew it. WinUI's own
scrolling reads touch, pen, touchpad and wheel input off the UI thread
(`ScrollPresenter.cpp`), but not a mouse drag, and for swap chain content the
platform's way is an input source made off the UI thread. `InputPointerSource`
is closed source, so what that route does with a drag that leaves the map,
with the controls above the map and with click, menu, hover, cursor and focus
is not known. A compact control at the bottom centre
shows a Segoe Fit button after each zoom, while the view is not fitted. It
hides after three seconds, but not while the pointer is over it, it has
focus or the map has keyboard focus, so Tab reaches it. It does not return
when the pointer moves or the view pans, because the owner wants it only
after a zoom. Beside it, the zoom
factor, such as 2×, answers how far that zoom went. It shows and hides with the
Fit button by the same rule, because the two read as one control.
Fit shows the whole world with the origin in the centre and returns
focus to the map when its button disappears. The menu exposes Zoom in, Zoom out
and Fit, including shortcut text. No permanent toolbar or on-map hint is added.

Left and right panning continues without end at every zoom, as in established
map applications; the view always shows one turn of longitude. Vertically, the
world stays centred where it fits; otherwise panning stops at its top and
bottom edges. Resize retains the zoom factor and geographic centre, subject to the
new bounds. Peer updates do not move the view. Estimated regions are merged
in fitted-map space, so zoom does not change which regions an estimate uses.
The shown merge changes the number of destinations when the view rests and
when it resizes.

The map uses equirectangular coordinates. Zooming calculates the horizontal
ratio as the cosine of the latitude at the centre of the constrained view.
The full-world overview is centred at 0° and has a 2:1 width-to-height ratio.
Correction follows the centre latitude as the view moves toward either pole;
it does not preserve every local shape across a broad view. The vertical bounds
keep a finite polar view centred below 90°, so the horizontal scale stays
positive. An undrawable view is constrained to 0° and uses the overview ratio.
Zoom and drag preserve the geographic point under the pointer except where
vertical world bounds constrain it. Panning recalculates the ratio as the
centre latitude changes, so the map stays in proportion without zooming; Fit
restores the overview.
Estimated-place spacing and inspector arrangement use the same fixed overview
ratio, so navigation does not change which regions an estimate uses or
rearrange the inspector; only the shown merge regroups destinations at the
view's rest.

Geometry remains vector-based. Land fills are clipped to an overscan window
and stay cached until the view nears its edge or outline detail changes.
Strokes are prepared after both axis scales and retain that scale with
their GPU geometry, so the existing rebuild budget can replace them as the view
changes. Country borders, arc widths, marker and hit-target
sizes, glow width and fade distances retain their screen-space sizes while zooming.
The view survives minimising and temporary section changes. Explicitly turning
Map off discards that view; turning it on starts fitted. Zoom and pan are not
saved across app restarts. Touchscreen gestures remain out of scope; precision
touchpad behavior must be checked on the native control rather than inferred
from browser wheel events.

### Torrent scope

Single-torrent, selected-group and all-torrent peers use one Inspector read path:
one [detail subscription](pipe-api.md#snapshots-and-detail) for the whole scope,
which the engine collects up to eight torrents at a time. Without a
torrent selection, the scope is all torrents; otherwise it is the selected group.
Torrent identity remains
part of every row key, so shared endpoints across torrents are not deduplicated.
The all-torrent scope includes torrents with retained addresses even when they
have no connections. Collection stops outside the Peers inspector. Only new
addresses need a background location lookup; unchanged snapshots do not redraw.
Non-IP addresses cannot be located, and tracker swarm totals are not address lists.
Each torrent's available peers appear immediately, and its fresh result replaces
only that torrent's rows in the same visible inspector context. Other torrents'
rows remain visible while their reads continue. The Inspector retains the last
peer rows for eight recently read torrents, so returning to one displays them
before the pipe reply. A new engine session or disconnect clears those rows.
Changing selection abandons the old read without waiting for its completion.
The Inspector owns that cancellation; token callbacks run asynchronously, so a
torrent-table gesture never waits for inspector cleanup. Peer parsing, record
comparison, merged source construction, place filtering, summary counting and
location membership reconciliation run on workers over captured immutable
facts. The UI applies prepared property notifications and publishes row sources.
Retained peers keep their row identity. Coalesced place changes publish only the
latest filter and release its prepared membership changes through TableView's
generic hold policy. Native collection notifications, input, focus, cell
measurement and layout remain UI work; generic projection, sorting and edit
planning belong to TableView.
Speed reads the same scope from
the engine's retained histories and displays its day range before fetching finer recent samples.

## Native map validation

The `peers` capture review renders the map with synthetic peers and a real
country-MMDB lookup. Live public-swarm timing, city-MMDB decoding and OS
accessibility settings are outside that review.
