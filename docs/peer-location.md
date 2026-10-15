# Peer location

The Peers table shows where each peer is in separate Country and City columns:
its country as a flag and name, and its city when the downloaded data provides
a name. This document owns the data, the lookup and the build step; the
[interface contract](interface.md) owns the columns and the Settings row,
and [licences](licences.md#db-ip) owns the
attribution.

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
adapter apply, and through request protection under the `dbip` catalogue. It
fetches this month's archive, or the previous month's when this month's does
not exist yet, unpacks it beside the installed file, checks that the result
opens as a database, flushes it to disk and moves it over the installed copy
under the lookup lock. Failure or cancellation before that keeps the previous
copy. A route change or a protection reset interrupts the download and
reports it, as it does the IMDb download. The status bar shows the phases:
downloading, unpacking, installing.

A copy from the current month counts as current, so Update warns that the
download would most likely transfer the same file.

## Lookup

`Geolocation` answers one address from the country file and, when installed,
the city file. The Inspector looks a peer's address up when the peer appears
and again only when the city data changes, because the address never changes
and the names come in every language DB-IP ships, so a language switch needs
no new lookup. Rows appear before those lookups: one background worker resolves
peer and map-origin locations, then updates only the peers still displayed and
only from the current dataset version. An address the
data lacks, a web seed's host name or an I2P destination has no location.

## Flags

The flags are glyphs of `lib/Lucide/src/Fonts/twemoji.ttf`, a flags-only
subset of Mozilla's Twemoji colour font: 184 KB for the flag of every
two-letter region code, vendored beside the Lucide icon font with its
licence. It is a COLR version 0 colour font, which Windows draws in colour
the way it draws its own emoji. `Flags.Glyph` turns a country code into the
pair of regional indicator characters that the font joins into one flag, and
the Country cell shows that text in `Flags.Font` before the name. Only that
text block uses the font; the name beside it stays in the app's font.

## Map

The Peers inspector's ellipsis menu selects Map only, Table only or Map and table.
Its Map detail group selects Countries or Cities. Countries aggregates every
connection by country using estimated distribution; Cities uses supplied coordinates
and falls back to estimated distribution for records without coordinates.
Cities is disabled without installed city data. Get city data starts the existing
dataset download directly, with progress in the application status bar, so the
person need not leave the inspector. It is disabled during a download and omitted
once the data is installed. The chosen map detail is saved with the peer view;
Countries is the default and the fallback when city data is removed.
When both are visible, the inspector compares the fitted map scale in each
arrangement and uses the larger one. The renderer owns that scale calculation,
so layout and projection agree. Combined views retain separate split proportions,
using the same accessible Splitter as the inspector frame. Coordinates
from the installed city database are grouped at identical positions. A city name or
a bundled country polygon is not required to plot a supplied coordinate. The
projection fits the full latitude/longitude range and wraps arcs at the date
line. Country-only records use estimated distribution and remain labelled
country-only. In Cities mode both precisions can coexist, according to each record's
available coordinates.
The engine's public address uses the same local lookup for the origin; no
external lookup request is made, and an unknown origin produces no arcs.

Estimated distribution aggregates every record in Countries mode, or each country's
country-only records in Cities mode, and their
measured rates, merges bundled populated-place candidates within 25 DIPs, and
uses no more destinations than active connections. One active connection uses
the largest populated place; larger counts use the most populous distinguishable
regions. The selected regions divide that group's bandwidth by their population
weights. Their locations and allocated speeds are explicitly estimates. Records
with coordinates in Cities mode keep their measured rates at those coordinates; they do not
also contribute to the estimated arcs.

Win2D draws bundled Natural Earth geometry and great-circle guides, not measured
network paths. Active arcs use a fixed logarithmic speed scale and the app's
transfer colours; idle city markers remain visible. Country fills stay subdued.
Countries have no hover or selection state; inspection targets location markers
and arcs. A table peer with coordinates highlights its matching location;
country-only rows do not select the country's estimated locations.
Active country arcs keep visible endpoints; idle countries use shading, with
an outlined marker only where the country would otherwise be too small to see.
The composited arc layer uses the [dimming treatment](#arc-dimming);
marks remain outside it. Drawing stops when the map leaves the visible inspector.
Redraws follow changed observations, view transforms, inspection or appearance.
An unchanged, clamped pan or zoom does not request another frame. Navigation
requests redraw directly through Win2D so dragging does not wait for the activity
timer; that timer runs only while visible activity styling is interpolating.
The Peers table refreshes changed rows while visible and reconciles deferred
sorting when shown again, avoiding repeated work for identical samples or a
hidden table.
No simulation controls ship in the product.

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
4. Draw location markers, the origin and the inspection highlight separately.
   They retain their visibility and hit targets. Highlight only the inspected
   connection/location; do not brighten the entire network during inspection.

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
when animations are enabled; navigation remains direct and there are no moving
particles. Dark and Light themes retain the same activity hierarchy. High
Contrast keeps the existing solid, visible lines without glow or dimming;
reduced-motion settings bypass activity interpolation.

### Zoom and pan

| Action | Mouse | Keyboard with map focus |
| --- | --- | --- |
| Zoom | Wheel around the pointer, with or without Ctrl; double-click zooms one step | Plus or Minus, with or without Ctrl, around the inspected location when present, otherwise the view centre; main keyboard and numpad |
| Pan | Left-drag beyond the drag threshold | Ctrl+Arrow, one eighth of the view per press |
| Fit to window | Corner button | Ctrl+0, main keyboard or numpad |
| View and map detail menu | Right-click or inspector ellipsis | Menu key or Shift+F10 |
| Inspect locations | Click or hover as before | Plain arrows continue location inspection and reveal an off-screen destination |
| Cancel drag | — | Escape restores the view from before the drag; outside a drag, existing selection clearing remains available |

The map uses the inspector's existing flyout instance, so right-click and the
ellipsis share commands, live state and the same icon-led native menu headings.

Zoom ranges from 1× to 8×, with 1.25× per wheel notch and proportional small
wheel deltas. A drag does not select a location. Show the SizeAll cursor only
after the drag threshold is crossed, restoring the normal cursor when the drag
ends or is cancelled. Input moves the view directly, without added easing,
inertia or a zoom animation. A compact top-right control
shows the zoom percentage and a Lucide Fit button only above 1×. Fit returns
focus to the map when its button disappears. The menu exposes Zoom in, Zoom out
and Fit, including shortcut text. No permanent toolbar or on-map hint is added.

The world stays centred on an axis where it fits; otherwise panning stops at
its edges. The view does not wrap, although geographic arcs still split at the
date line. Resize retains the zoom factor and geographic centre, subject to the
new bounds. Peer updates do not move the view. Estimated destinations are merged
in fitted-map space, so zoom does not change their number or locations. Resize
may still merge or separate destinations under the existing spacing rule.

Geometry remains vector-based. Country borders, arc widths, marker and hit-target
sizes, glow width and fade distances retain their screen-space sizes while zooming.
The view survives minimising and temporary section changes. Explicitly turning
Map off discards that view; turning it on starts fitted. Zoom and pan are not
saved across app restarts. Touchscreen gestures remain out of scope; precision
touchpad behavior must be checked on the native control rather than inferred
from browser wheel events.

### Torrent scope

Single-torrent, selected-group and all-torrent peers use one Inspector read path
and the existing asynchronous detail collector, one torrent at a time. Without a
torrent selection, the scope is all torrents; otherwise it is the selected group.
Torrent identity remains
part of every row key, so shared endpoints across torrents are not deduplicated.
Each torrent's available peers appear immediately, and its fresh result replaces
only that torrent's rows in the same visible inspector context. Other torrents'
rows remain visible while their reads continue. The Inspector retains the last
peer rows for eight recently read torrents, so returning to one displays them
before the pipe reply. A new engine session or disconnect clears those rows.
Changing selection abandons the old read without waiting for its completion.
Speed reads the same scope from
the engine's retained histories and displays its day range before fetching finer recent samples.

## Native map validation

The initial native implementation built successfully and passed a 25-scene
capture review with synthetic peers and a real country-MMDB lookup. The approved
adaptive header revision supersedes that review's menu and fixed-layout checks.
The [delivery record](peer-map-plan.md) records verification for each revision.
Live public-swarm timing, city-MMDB decoding and OS accessibility settings remain
separate from synthetic rendering evidence.
