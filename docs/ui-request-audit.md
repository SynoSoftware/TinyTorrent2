# UI request audit — 10 October 2026

Review of the current working tree against the visible conversation, including later corrections and approvals. Baseline: `ef37eb4`, with relevant uncommitted and untracked files included. Chat-history retrieval returned `Transport closed`, so an exact two-hour timestamp boundary was unavailable; every visible request was included instead. This report is a review checkpoint, not a new behavior contract.

The initial audit was read-only, with two independent reviews of requirements and code quality. The follow-up below records the subsequently authorized fixes and verification. “Present” in the checklist means supported by source inspection unless runtime evidence is stated.

## Follow-up implementation

The owner subsequently approved fixes and replaced the width-persistence rule:
column widths now live only in each Table instance. New tables fill their first
visible layout; hidden instances retain their widths; resizing and later data do
not repeat the fill. ColumnLayout now stores only order, visibility and sorting,
and legacy width fields are ignored. The old manual/automatic provenance logic
and tests requiring width persistence were removed.

Pane now owns the identity/navigation/actions arrangement. Inspector supplies the
content for Torrents and Library. Filters keeps its full-width header and existing
handle; the owner explicitly confirmed that it is correct. The side-gap finding
below is withdrawn, not an outstanding fix.

The prototype now includes all six torrent Inspector sections, shared table header
sizing, Files controls, Peers separation and equal right-aligned Tracker actions.
It keeps initialized widths in memory and demonstrates the Pieces loading transition.
Metadata edit/erase messages were corrected.

Pieces retains a dimmed, non-interactive bitmap while new data loads and fades in
the replacement only after rasterization completes. The loading state no longer
slides sideways; other torrent sections defer their arrival until loading ends.
Library details no longer repeats the same measure during a width probe or allocates
an array of preceding sections on every measurement.

Debug x64 compilation passed. The 29 focused TableView sizing and layout-state
checks passed, including ignoring legacy saved widths, retaining live widths,
and preserving saved order, visibility and sorting. Off-screen capture exposed
a UI startup failure: empty optional Pane presenters attempted to use its body
content. Collapsing absent presenters before measurement fixed the failure.
The engine logs showed normal requested shutdown, not an engine crash.

The final Library review passed all 40 scenes, including normal and rapid Pieces
switches. Rendering samples retained the old bitmap without hit testing during
loading, then recorded the replacement fading to full opacity and settling on
the latest target. The final detail review also passed, including native file
priority, tracker editing, live language changes and immediate settings changes.
Measured main, Files (with Select all) and Tracker headers are all 32px at text
scale 1. Tracker actions are both 125px wide with a 12px gap; navigation is centered
in the remaining header space.

Evidence remains in `artifacts/evidence/Capture-library-files-c76d6b1e-cb98-4874-8971-0af0cb5c11e8`
and `artifacts/evidence/Capture-details-0b748031-cfb2-4411-8eca-9058c0fbb2a5`.
These are native off-screen captures, not physical pointer or desktop acrylic
verification.

The ordinary Debug x64 executable was rebuilt successfully with zero warnings
and errors after clearing its stale generated XAML cache. Its build skipped
unchanged library compilation. No generated output directories were found
outside the permitted artifacts/dependency locations; `git diff --check` passed.

## Original review findings (superseded above)

1. **P2 — Old automatic column widths can still block initial fill.** [Table.cs:539](C:/SynoSoftware/TinyTorrent2/lib/TableView/src/Table.cs:539) treats every legacy saved width without `HasManualWidths` as manual. Earlier versions also saved automatic widths. Those layouts remain exempt from filling even after the visible-layout correction. New snapshots distinguish manual and automatic widths correctly. The conservative migration is documented but leaves “unless resized by hand, not by auto” incomplete for existing layouts. An old snapshot alone cannot tell us how its widths were chosen.

2. **P2 — Inspector tabs still use the old centering.** [InspectorPane.xaml:42](C:/SynoSoftware/TinyTorrent2/app/src/Shell/InspectorPane.xaml:42) retains balanced `*, Auto, *, Auto` columns. The selector is not centered in the actual space remaining between the identity and actions.

3. **Withdrawn — Side handle gap.** The owner confirmed Filters is correct and must remain unchanged. The earlier interpretation of the two-pixel gap was not a justified defect.

## Incomplete work and verification

- **Prototype browser review remains pending.** Torrent sections, shared headers, spacing, Tracker actions and metadata messages are synchronized in source; JavaScript syntax validation passed.
- **Width resizing remains slow in the native diagnostic.** The measurement below reproduces the cost with the Inspector closed as well as with Info/File open. It does not isolate the exact cause in the shared resize path. Inspector height changes, including Cast column/wrap transitions, were much cheaper.
- **Remaining native checks:** physical drag/release/cancel, High Contrast, desktop translucency and enlarged text scaling. Header checkbox sizing, common header height, Pane layout and Tracker initial fill were observed in the final native captures. Remaining gaps are not additional established defects.

## Full request checklist

| Request, including subsequent corrections | Result | Evidence/owner |
|---|---|---|
| Align table header with native selection at both edges | Present | TableView header Strip uses four-pixel side insets |
| Keep native row rendering; defer replacement row template | Preserved | Native row selection remains in use |
| Match header rounding to selection | Present | Shared `ListViewItemCornerRadius` |
| Same table header design throughout the app | Native captures reviewed | One shared Cell/Strip template; main, Files and Tracker headers measured32px |
| Files header must not be taller because of Select all | Verified at text scale1 | Native checkbox header measured32px; shared template owns compact sizing and a non-measuring divider |
| Filters and its compact handle | Accepted; unchanged in follow-up | Owner withdrew the side-gap concern |
| Bottom compact handle needs gap above | Present | InspectorToggle `Margin="0,2,0,0"` |
| Double-click unhides and expands Inspector | Present for Torrents and Library | ItemInvoked reaches Properties owners, which clear compact state |
| Improve slow card/window resizing | Measured; window-width cost remains | Native measurement below; pane height work is inexpensive, width resizing remains slow with the pane closed |
| Separate Cast column only if it fits vertically; otherwise wrap below Synopsis | Present | Details.MeasureOverride compares measured column height with viewport space |
| Clear identification → Erase metadata; Edit identification → Edit metadata | Present in production | English/Spanish resources, Browser commands, MetadataDialog |
| Metadata terminology in code/design vocabulary | Present in product vocabulary and UI-facing names | CONTEXT.md records Library metadata; automatic identification remains a distinct internal operation. The generic codebase-design skill file itself was not changed |
| Menu order Open folder → Edit metadata → Erase metadata | Present | InspectorPane/Library.cs, RefreshLibraryActions |
| Vertical divider between item actions and Pin/Close | Present | Inspector library actions end with a theme-aware divider |
| Center selector in remaining space as a shared pane rule | Native captures reviewed | Pane owns Header/Navigation/Actions composition; measured navigation placement matches remaining space |
| Remove blue and restore neutral grey | Present | Dark Chrome brush uses platform neutral base color |
| Increase translucency | Source change present; visual acceptance pending | DesktopAcrylicBackdrop retained; dark root brush opacity0.1 |
| Pane uses Settings card color and thin border | Present | One Pane style uses LayerOnMicaBaseAltFillColorDefaultBrush and CardStrokeColorDefaultBrush, thickness1 |
| Filters and Inspector use one card/header owner | Present | Controls/Pane.cs and its implicit style own material, outline, corners, header padding and divider |
| Windows Settings visual direction | Shared neutral layers/borders present; comparison pending | Pane corners remain8px; the earlier “do not change, just tell me” did not authorize a4px change |
| New tables fill once; widths remain only for the instance lifetime | Focused tests passed; native Tracker fill observed | TableView FillOnce; ColumnLayout excludes widths; hidden instances retain their widths; resizing does not refill |
| Connection setup uses the same width token as Settings | Present | Both consume TinyTorrentSettingsWidth=1000 |
| Explore layouts and apply approved A — Constrained | Present | connection-prototype.html has A–D; ConnectionPage implements bounded two-column/stacked layout |
| Connection setup as a dialog | Superseded | Later request and approval selected the constrained page first |
| Move provider/data-use/privacy lines to a click surface | Present | Native info button and Flyout |
| Test/Retest beside the capacity label | Present | TestActions is the rightmost header column |
| Status/progress must not shift the layout | Present | Reserved44×text-scale height; progress placed at bottom; fixed TestActions width |
| Inspector right gap, keeping the already-correct main table | Present | InspectorSurface right margin4 |
| Remove duplicate padding around all tab tables | Present horizontally | SectionContent has no outer padding; TableView retains its internal geometry |
| Add separation between Peers header and pane divider | Present | Peers top margin8; other tables follow separated toolbars |
| Remove duplicate Filters content padding | Present | Parent horizontal padding removed; native list inset retained; Library filters/Clear retain content margins |
| Edit and Reannounce equal width | Present | Existing ActionButton.Align measures both and applies the larger width |
| Tracker buttons equal spacing and sensible placement | Present | Right-aligned above table;12px between buttons and above/below command row |
| Shrink Inspector below useful height, collapse on release | Present | ValueCommitted uses twice measured HeaderHeight; same collapse owner as Pin |
| Reopen at previous useful height; cancellation restores without collapsing | Present for ordinary gestures | Previous gesture size retained; cancelled drag restores start value; smaller usable sizes persist |
| Dim grip until hover/drag | Present | Opacity0.4 at rest,1 active; High Contrast remains1 |
| Keep prototype synchronized | Expanded; browser review pending | All six torrent tabs now have sample content; shared header, spacing, memory-only widths and Pieces transition reflected |

## Code-quality result

No additional concrete ownership defect was established in the reviewed changes. Pane owns pane chrome and selector placement; TableView owns header appearance and initial fill; Splitter owns gesture completion; MainWindow owns the Inspector collapse decision. Equal-width tracker actions reuse the established helper.

Unrelated database/provider/subtitle edits in the dirty working tree were excluded: they were not requests in the visible UI conversation.

## Library resize measurement

The owner authorized the pending resize check after commit `60df67e`. A native
Debug capture measured 545 samples: 60 idle frames, three 101-step window-width
sweeps (1600–800–1600px), and two 91-step Inspector-height sweeps (480–120–480px).
The fixture has 16 cast members. Observed dimensions reached the requested ranges;
Cast used both columns and wrapping in both Info sweeps. No sample needed more
than two observed layout notifications. The complete capture review passed.

| Surface / change | Synchronous work median / p95 | Next rendering callback median / p95 |
|---|---:|---:|
| Idle | 0.00 / 0.00ms | 31.15 / 32.31ms |
| Info / window width | 43.19 / 57.51ms | 142.34 / 194.17ms |
| File / window width | 43.82 / 59.48ms | 134.79 / 184.05ms |
| Inspector closed / window width | 43.05 / 58.24ms | 121.48 / 176.68ms |
| Info / pane height | 3.27 / 5.28ms | 30.93 / 50.00ms |
| File / pane height | 2.04 / 3.34ms | 30.64 / 48.99ms |

Synchronous work includes the resize API and currently pending forced XAML layout;
native resize processing can follow the API call. Rendering latency includes
scheduling, as the 31ms idle baseline demonstrates. This is off-screen Debug
evidence, not a Release benchmark, physical drag, GPU presentation or desktop
acrylic measurement. There is no pre-change timing baseline, so it does not
quantify improvement from the earlier layout changes. The result points beyond
the Library metadata arrangement: closing the Inspector preserves most of the
window-width cost.

Run `app/tests/Capture.ps1 -Review library-files` with an up-to-date capture build
to repeat the measurement. Raw samples and screenshots are preserved under
`artifacts/evidence/Capture-library-files-fcdebd4e-1284-48db-97f2-4042fc300980/captures/`.
