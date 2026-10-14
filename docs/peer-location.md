# Peer location

The Peers table shows where each peer is: its country as a flag and name, and
its city once the person downloads the city data. This document owns the data,
the lookup and the build step; the [interface contract](interface.md) owns
the column and the Settings row, and [licences](licences.md#db-ip) owns the
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
the city file. A peer looks its address up once and again only when the city
data changes, because the address never changes and the names come in every
language DB-IP ships, so a language switch needs no new lookup. An address the
data lacks, a web seed's host name or an I2P destination has no location.

## Flags

The flags are glyphs of `lib/Lucide/src/Fonts/twemoji.ttf`, a flags-only
subset of Mozilla's Twemoji colour font: 184 KB for the flag of every
two-letter region code, vendored beside the Lucide icon font with its
licence. It is a COLR version 0 colour font, which Windows draws in colour
the way it draws its own emoji. `Flags.Glyph` turns a country code into the
pair of regional indicator characters that the font joins into one flag, and
the Location cell shows that text in `Flags.Font` before the name. Only that
text block uses the font; the name beside it stays in the app's font.

## Map

The Peers inspector's Map and Table header toggles select either view or both.
When both are visible, the inspector compares the fitted map scale in each
arrangement and uses the larger one. The renderer owns that scale calculation,
so layout and projection agree. Combined views retain separate split proportions,
using the same accessible Splitter as the inspector frame. Coordinates
from the installed city database are grouped at identical positions. A city name or
a bundled country polygon is not required to plot a supplied coordinate. The
projection fits the full latitude/longitude range and wraps arcs at the date
line. Country-only records use estimated distribution and remain labelled
country-only. Both precisions can coexist, according to each record's available
coordinates, without a user setting.
The engine's public address uses the same local lookup for the origin; no
external lookup request is made, and an unknown origin produces no arcs.

Estimated distribution aggregates each country's country-only records and their
measured rates, merges bundled populated-place candidates within 25 DIPs, and
uses no more destinations than active connections. One active connection uses
the largest populated place; larger counts use the most populous distinguishable
regions. The selected regions divide that group's bandwidth by their population
weights. Their locations and allocated speeds are explicitly estimates. Records
with coordinates keep their measured rates at those coordinates; they do not
also contribute to the estimated arcs.

Win2D draws bundled Natural Earth geometry and great-circle guides, not measured
network paths. Active arcs use a fixed logarithmic speed scale and the app's
transfer colours; idle city markers remain visible. Country fills stay subdued.
Active country arcs keep visible endpoints; idle countries use shading, with
an outlined marker only where the country would otherwise be too small to see.
The composited arc layer has no origin fade at eight or fewer arcs, and reaches
the crowded-map fade at twenty; marks remain outside it. Drawing stops when the
map leaves the visible inspector. No simulation controls ship in the product.

Selected-torrent and all-torrent peers use one Inspector read path and the existing
asynchronous detail collector, one torrent at a time. Without a torrent selection,
the scope is all torrents. Torrent identity remains
part of every row key, so shared endpoints across torrents are not deduplicated.
The complete collection replaces the previous one only in the same visible
inspector context. Speed uses the engine's existing session history.

## Native map validation

The initial native implementation built successfully and passed a 25-scene
capture review with synthetic peers and a real country-MMDB lookup. The approved
adaptive header revision supersedes that review's menu and fixed-layout checks.
The [delivery record](peer-map-plan.md) records verification for each revision.
Live public-swarm timing, city-MMDB decoding and OS accessibility settings remain
separate from synthetic rendering evidence.
