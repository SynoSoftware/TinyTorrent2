# Torrent indexing UI prototype

Question: how should catalogue search and file choices fit the existing
torrent page? Open [the HTML](torrent-indexing-prototype.html) directly. From
the repository root, one command opens it in the default browser:

```powershell
Start-Process .\app\prototypes\torrent-indexing-prototype.html
```

This is throwaway presentation code with invented, memory-only sample data.
It makes no network requests, reads no catalogue and starts no downloads.
The [torrent catalogue contract](../../docs/contracts/indexing.md) owns production
behavior. Native Search implements the approved UI; this folder contains only
the presentation prototype.

The owner's later decision replaces its standalone settings dialog with the
normal Settings > Indexing category after Transfers. Existing category headers,
icons, cards and rows own that presentation; rule editing and explicit previews
remain task dialogs. Inspector General uses the existing torrent-properties
sections, fields, statistics and icons. The original native settings dialog is
preserved in Git commit `763e82f`.

The [reference inventory](../../docs/plans/14-torrent-references.md) records the
subsequent Rats/Bitmagnet research. Bans are automatic rule-based rejections:
rejected metadata is discarded and compact rule-linked evidence suppresses later
retrieval until lifted. The prototype now shows rule editing and count previews,
with no manual hash-ban dialog or excluded-identity list.

The owner's direction fixes the composition to the torrent page: results table,
left Filters, bottom Inspector. General retains Summary and Properties; Files
uses the full-width file browser. Alternative Inspector layouts have been
removed because the catalogue does not justify a different visual structure.
This direction takes precedence over the prototype skill's usual variants.

The chosen direction is file-first and collapsed by default. Equal known v2 file
roots and lengths share one result despite different names. Files without a root
remain distinct; filename equality alone does not merge them. Expansion lists
torrent sources on one-line rows, with original paths in tooltips. A matching alias
finds the group and exposes its other known sources. The sample has a shared root
under Chapter 01 and Chapter One, and a second Chapter 01 with a different root.

File and Torrent actions occupy separate columns. Both use the one-word Download
label, the repository's Lucide icon and a tooltip with scope and source. Empty
File cells on source rows preserve Torrent alignment. File download records only
the chosen original index, using the highest known seed estimate, peers to break
ties and stable source order, with the configured folder and no dialog. Torrent
download opens Add for that source. Both simulated acceptances use one owner.

The file Inspector has General and Aliases sections. General shows size, source
and alias counts and a source table with torrent name, size, file count, seeds,
peers, Observed, Last seen and Inspect. Observation times have their own column; torrent
entries stay on one line. No hashes appear in the file Inspector or these tables.
Source rows collapse equal known torrent hashes; their
display-name aliases remain with the canonical torrent. Automatic strongest-source
choice is used by Download file, not as a replacement for that table.
Aliases provides Search and Copy for each name. Selecting
a source restores the existing torrent General/Files Inspector. Utilities use
available metadata rather than invented transfer history.

Results, expanded sources and Inspector files scroll continuously, without
pagination controls. Production uses the existing TableView virtualization;
engine query batches and work budgets remain internal.
The finite sample's exact totals and global sorting do not establish production
search guarantees. The [search contract](../../docs/contracts/indexing.md#search-and-add)
allows bounded scans and limited results with explicit coverage and truthful
counts/order; the HTML has not been updated to demonstrate those outcomes.

The differences are semantic: catalogue entries have metadata and optional dated
swarm estimates, but no transfer progress, rates, ratio or local save folder.
File choices prepare an addition rather than edit an accepted torrent. The
torrent's General and Files remain its useful sections; live-transfer sections
are not populated with invented data. Production reuses established owners.

There is no Search-specific toolbar. File > Settings opens indexing settings;
the table starts with its column headers, which own sorting. Result and selection
counts appear in the status bar, without a separate title/count/sort row.
Indexing and its options live there; the first-use empty view also offers the
same indexing switch. View owns Filters and Inspector
visibility, as it does elsewhere in the application. Search holds commands for
the selected content and links to exclusion rules; Download remains available in
the Inspector. The normal application's optional toolbar is not redesigned by
this prototype. The status bar reports indexing state without another switch.

The state drawer shows original file indices and current sample state. Reset
clears the in-memory interaction state. The bottom bar contains prototype
scenario controls only and is not application chrome.

Try searching, extension and size filters, Files/Torrents scope, sorting,
Ctrl/Shift result selection, Inspector file choices and Add. Download selected
files appears in File, Search and the context menu. It picks one source per
unique file and groups original indices by source torrent into one acceptance.
Inspector > Files keeps one Download button at the bottom right for its choices.
Add previews the wanted files, destination
and ordinary options, then reports the simulated outcome. The scenario menu
exposes saved search with indexing off, above-budget storage, partial rebuild, first
use and storage failure. Settings demonstrates the shared indexing switch,
rules, rebuild, clear and reviewed removal of retained metadata.

Rules combine torrent title, total size and file count with any/every/no matching
file. File conditions cover name, path, extensions and size. Numeric comparisons
include lower/upper limits, exact values and ranges, with explicit byte units.
Conditions within a file rule apply to the same file. Edits and
removals preview excluded counts before Apply or Cancel. Two initial rules have
overlapping rejection cohorts: removing the MP3 rule shows 300,000 excluded now,
200,000 after and 100,000 eligible again. The cohorts model compact remembered
decisions without allocating 300,000 fixtures or retaining their metadata.
Matched-rule identities and revisions stay internal. Cancel changes nothing;
Apply changes the policy, with retained catalogue removal reviewed separately.

The search matcher is a case-insensitive substring demonstration; it does not
implement the production search syntax. Retention patterns support literals
and wildcards, with combined per-file conditions. Storage allowance, statistics, destination
free space and progress are illustrative. No performance, durability, native
WinUI semantics or translation behavior is established by this HTML.

Browser checks covered initial collapse, equal-root/different-name grouping,
automatic strongest-source file download without a dialog, whole-torrent Add,
aliases and alias searches, rule-preview Cancel/Apply, and same-file conditions.
Parent and source torrent-download buttons measured at the same horizontal
coordinate and width. Existing View/Settings paths and file choices were also
inspected. JavaScript parsing was checked without compiling TinyTorrent. Native
accessibility, High Contrast, text scaling, production query paging and engine
integration remain implementation work.

Settings holds the storage budget and not-seen interval. Actual DHT sightings
refresh Last seen. At budget, stale entries can be removed oldest first;
collection continues above budget when no stale entry is eligible.

Both indexing switches show On/Off and explain that discovery builds a searchable
catalogue of torrent files. Enabling first use leaves the empty view in place
until results arrive. Recovery names the cause: a damaged search index offers
Open folder, Repair from retained metadata and confirmed Delete through the
existing clear-catalogue flow. Inaccessible storage offers Open parent folder;
Repair/Delete are absent while access is denied. The prototype records folder
opening as a simulated action and starts Repair in the status-bar work slot.

Rebuild work uses the existing status-bar pattern: icon, short label, percentage,
progress below and Cancel alongside. No appearing banner shifts the workspace.
Browser checks confirmed identical results/Inspector bounds before and after
cancellation, the first-use switch, and two selected files becoming one torrent
acceptance. The HTML simulates these interactions without production downloads.

The owner approved this interface on 2026-10-25. The prototype and plan are
captured on `codex/torrent-indexing-design`; no production implementation is
included. The HTML remains the primary visual reference for native UI work.
