# Status bar design proposal

Designed on 2026-10-13. The UI and shared-service integration are implemented
in the working tree; native validation remains outstanding. The [interface contract](interface.md)
owns the adopted behavior. The [interactive study](../app/prototypes/status-bar-prototype.html)
uses simulated data; it is not a native WinUI rendering.

## Purpose and hierarchy

The bar answers three questions in reading order: **what am I looking at,
what work is happening, and what is happening to transfers?** Torrents and
Library share the composition, but each supplies its own context.

| Position | Torrents | Library |
| --- | --- | --- |
| Left, anchored | Shown/total torrent count, active filter, selection | Applied configuration, file count, selection, active finding |
| Middle, flexible | Accepted background operations | The same operations, or ambient video-information state when space permits |
| Right, anchored | Session rates, applied caps, pause or alternative-limit state, torrent connection | Compact, labelled background torrent rates; explicit torrent hold |

Counts refer to the table. Rates always refer to **all torrents**, never the
selection or filtered rows. Library counts files, not distinct movies: two
files of the same movie count twice. Settings and About have no table context;
omit counts and selection there, retaining global transfers and accepted work.
Settings uses the left group for its current section, such as `Settings · Network`,
with a Settings icon, so page context remains in the same place on every section.

The left anchor deliberately replaces the current speeds-first arrangement.
The person should recognize the scope before interpreting a changing number.
The activity region consumes spare width without moving either anchor. A
selection count appears beside the collection count, not at the far end of
the window. Related facts form phrases rather than a row of independent badges.

## Content and states

### Torrents

| Condition | Visible text or behavior |
| --- | --- |
| All torrents | `84 torrents` |
| A state filter applies | `12 of 84 torrents · Downloading` |
| Selection exists | Append `3 selected`; zero selection adds nothing |
| Filter has no matches | `0 of 84 torrents · Downloading`; retain the table's empty-state action |
| No managed torrents, after first snapshot | `0 torrents`; no transfer numbers are needed |
| Before first coherent snapshot | `Connecting…`; do not present zero as a loaded collection |
| Ordinary session | `↓ 1.2 MB/s   ↑ 84 KB/s`; each accessible name says download/upload and all torrents |
| A directional cap applies | `↓ 1.2 MB/s of 5 MB/s`; show only that direction's cap |
| Alternative profile applies | `Alternative limits`; scheduled expiry belongs in the full text when known |
| Standard profile applies | Caps explain it; do not add a redundant `Standard limits` label |
| Manual session pause | `All paused`; omit caps, and yield rate digits before this explanation |
| Scheduled pause | `Paused until 18:00`, or `Paused by schedule` if no confirmed time is available |
| Missing adapter | `Paused · Adapter unavailable`; full detail names the adapter, and Settings > Network > network adapter is the recovery path |
| Connection setup holds transfers | `Paused for connection setup`; do not mislabel this as a manual pause |
| Connection to engine lost | `Last known · 84 torrents`; rates become `—`, and existing InfoBar explains reconnection |

Use the actual applied filter and displayed collection. The current Torrents
search offers navigation/commands; typing in it does not filter the table and
must not change the count to “search results.” A cap describes configured
policy, not proof of the cause of a slow transfer. No aggregate ETA, average
progress, total ratio, peer count, DHT count or lifetime bytes belongs here;
the existing inspector and Speed limits page give those facts their context.

Use the engine's confirmed pause reason. Do not build a second precedence
algorithm in the status view. When the engine connection is lost, cached
pause/cap/network facts are not presented as current. The bar says the data is
last known, and the existing message owner explains the problem.

### Library

| Condition | Visible text or behavior |
| --- | --- |
| Videos | `Videos · 240 files` |
| Music | `Music · 86 files` |
| Files | `412 files`; do not repeat the configuration's noun |
| Selection exists | Append `2 selected`; file sizes remain in the table and inspector |
| A query or facets apply | Append `Filtered`; show one short criterion when it fits, e.g. `Genre: Drama` |
| No matches | `Videos · 0 files · Filtered`; distinguish this from an empty Library |
| A search is pending | Keep count and configuration tied to the last published rows; publish new count, configuration and applied-finding indication together |
| Before any usable result | Configuration only until a count is known; `Library unavailable` on a real failure, never an invented zero or a loading message |
| Videos, automatic video information downloads are off | No notice; this preference belongs to Settings · Plugins, like automatic subtitles. Saved information and manual fetching remain available. |
| Videos, default provider has failed and prevents requests | `Waiting for TMDB`, naming the actual provider; detailed reason and recovery stay with the existing feature surface |
| Music or Files | No ambient video-provider state; an explicit metadata batch still appears |
| Managed torrents exist | Right group: `Torrents  ↓ 1.2 MB/s  ↑ 84 KB/s`; keep it present across zero-rate samples |
| Background torrent hold | `Torrents paused` / `Torrents paused until 18:00` / `Torrents paused · Adapter unavailable` |
| Engine connection lost | Mark displayed rows `Last known` if retained; no live rate or “Library up to date” claim |

`RowsConfiguration` already distinguishes applied rows from the pending view
choice. Use it; no second configuration state is needed. A finding indicator
must likewise describe the query that produced the displayed rows. Preserve
the Library owner ruling that the page is ready on opening and never shows a
loading/preparing state; no new loading label, spinner or delay threshold.

Count only current Library entries. Do not invent an unfiltered denominator
from a facet count or the Torrents collection. Do not add total bytes,
metadata-coverage percentages or subtitle-coverage counts without a concrete
task that needs them. They compete with current work and selection.

Background transfer presence follows the existence of managed torrents, not
whether the latest rate sample is nonzero. This avoids blinking at every gap
between network bursts. It is secondary to Library work and the first core
group to yield under pressure. If caps are omitted from that compact summary,
append `Limits on` while an effective nonzero cap applies and transfers are
not paused. This describes a policy, not a fault.

### Metadata work across both modes

An explicitly accepted batch remains visible on every page with its Stop X,
as the existing owner ruling requires. In Library: `Metadata · 2 of 7 processed`.
On Torrents, Settings and About: `Library metadata · 2 of 7 processed`.
“Processed” means completed attempts, including no match or local protection;
it does not mean successful matches. Do not display `Done + 1` as completion.

If all remaining work is waiting, use `Metadata waiting · 2 of 7 processed`.
Name a provider or retry time only when the batch owner establishes that it
explains the whole wait. Batches can contain several providers and rotate
delayed files; the queue head and the default provider's warning cannot
establish this. Until the owner exposes a reliable work/wait distinction,
`Metadata · 2 of 7 processed` is the truthful neutral form in both states.

The batch takes the activity slot before ambient provider state. If a Videos
provider warning is displaced, it remains available in the inspector and
Settings; do not claim that provider is blocking a different batch. Do not
alternate messages on a timer or build a queue of status messages.

A single Lucide Download icon anchors metadata work; a Clock replaces it for
a confirmed wait. The icon distinguishes the activity group while its text
explains the exact work. No perpetual spinner or invented percentage is needed.
The X means **Stop fetching metadata**, not dismiss. Keep its native button
focus/hover/pressed states, a full accessible name and a matching tooltip.
Recover keyboard focus to the next usable task control, work-list button or
active page when that button becomes unavailable. Completion and failure use existing outcomes;
the footer returns to its resting context without a lingering “Ready.”

Accepted unfinished subtitle work appears with Lucide Captions on every page,
including alongside metadata and IMDb operations. Use `Subtitle work`
when only accepted unfinished work is known; `Subtitles waiting` for a confirmed
blocker. `Finding subtitles` is valid only when its owner establishes active
searching. Acquisition.Finding includes resumed persisted jobs and is not proof
of live network work. Reuse the distinction already expressed by Subtitles.Batch
rather than deriving a second one. Found is successful outcomes, not processed
attempts: do not turn Found/FindTotal into a progress fraction. The footer and
existing subtitle surface use the same operation cancellation command; the
background-work plan separates manual and automatic cancellation scopes.
All explicit operations outrank ambient video-information settings. Do not
cycle competing work on a timer.

### Long-running work: IMDb data

The owner extended the footer to long-running operations, including IMDb
download/import with progress and cancellation. This supersedes the earlier
metadata-only Cancel exception. Ordinary settings stay outside the footer.

Each operation has its own icon, stage text, compact native ProgressBar and
adjacent Cancel X when cancellable. The 24-DIP icon spans the label and progress
rows; the bar sits below the label and count, with Cancel centred beside both.
It remains visible on every page. Stages
change in place; download completion must not make the ongoing import vanish.

Use Microsoft.UI.Xaml.Controls.ProgressBar with its native template, theme
brushes and animation. IMDb uses IsIndeterminate=True for unmeasured phases and
its reported percentage for measured phases; metadata uses its known Value and
Maximum. The HTML study's browser progress control and the static
illustration are layout placeholders, not references for the native appearance.
Do not replace the bar with a ProgressRing because the placeholder looks like
an empty line: [Windows progress guidance](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/progress-controls)
recommends the indeterminate bar for work that lets the user continue using
the app while completion time is unknown.

| Owner state | Presentation |
| --- | --- |
| Download | `Downloading IMDb data · 284 MB`, determinate only with a trustworthy byte total, otherwise indeterminate; Cancel download |
| Parse source | Name episodes, titles or ratings and its approximate percentage, determinate bar, Cancel preparation |
| Build or optimize search index | Name the current IMDb phase, indeterminate bar, Cancel preparation |
| Install | `Finishing IMDb preparation`, indeterminate bar; cancellation unavailable after publication begins |
| Cancellation requested | `Cancelling IMDb import`, disabled Cancel; retain the activity until cleanup finishes |
| Success | Remove the activity; existing completion feedback owns the outcome |
| Requested cancellation | Remove after cleanup; no failure warning for the requested cancellation |
| Failure | Existing actionable failure feedback, never an endlessly busy bar |

The shared observation normalizes the existing IMDb phase and numeric facts.
Parsing episodes, titles and ratings reports approximate compressed-source
progress; indexing and optimization have no denominator. The background-work
plan owns phase and cancellation details. Show each phase honestly, without
inventing ETA or an overall percentage across stages. Metadata's processed/total supports a
determinate processed-attempt bar. A known wait freezes that fraction and uses
its waiting label; no busy animation implies active requests during a wait.

Cancellation uses the shared operation command routed to its accepted run.
Keep `Cancelling…` until the executing owner confirms cleanup; it retains old data
before install and decides when installation has committed. Do not promise
rollback after installation. The prototype's Cancel immediately simulates a
completed cancellation; its separate cancelling scenario depicts the pending
state.

Concurrent operations form one content-sized activity group in stable
registration order, with subtle vertical separators between processes. Each
process keeps its icon, label, native progress bar and Cancel close together.
Only the gaps between the top-level context, activity and transfer groups take
the remaining width. The explicit metadata batch remains inline under the
narrow-width rule.
Each cancel affects only its own operation. Do not rotate messages. When narrow,
use `Library metadata · 2/7` and `IMDb import`. Remove routine diagnostics,
normal rates and collection count first. The metadata progress bar can yield
before its exact processed fraction; decorative icons can yield before the
operation identities and Cancel controls. Keep IMDb's indeterminate indicator.

Subtitle work remains a separate compact labelled state while unfinished; do
not silently hide it behind other active work. The current specimen demonstrates
metadata plus IMDb concurrency, not all three jobs simultaneously. That layout
needs review before implementation can claim complete concurrency coverage.
Compose existing owners; no new generic scheduler or polling is needed.

This explicit IMDb import differs from opening Library. Opening the page does
not acquire a loading/preparing state; its readiness ruling remains.

## What yields and what belongs elsewhere

- Retain the connection item at the end of the Torrents session group. Network
  + `Others can connect` uses the existing success icon; `Connection unconfirmed` uses caution;
  Route + `Using proxy` stays neutral. Text is neutral in each case. This is torrent
  connectivity, not general internet health or proof of privacy. It yields as a
  complete icon-and-text item before core facts at narrow widths. Routine port
  status is omitted on Library; an unavailable adapter remains an explicit
  torrent hold there, with the hold's higher priority.
- Retain existing external-IP and free-space opt-ins in either mode when room
  permits. Show `Torrent IP: …` and a drive icon with `120 GB free`; the free-space
  tooltip identifies the default download folder and its drive, so the folder
  name cannot be mistaken for a transfer total. Use the confirmed destination's
  actual volume; never imply this is Library space or the selected file's disk.
  Unknown values say unavailable
  rather than showing stale healthy values. These are the first items omitted
  at narrow widths. Their settings descriptions must explain that behavior.
- Retain `Update available` only when a newer release is known, after the core
  groups and at the lowest layout priority. Help > Update remains the action.
  The existing implementation has no other passive update notification, so
  removing this notice would make discovery depend on opening Help. It yields
  first at narrow widths, and adds no new notification mechanism.
- Long errors and recovery actions remain in the existing InfoBar above the
  footer. Its position is an owner ruling, an explicit product departure from
  Fluent's usual below-command-bar placement. Do not relocate it in this task.
- Eliminate hidden double-click navigation from passive footer text. Settings,
  filters and update commands already have visible keyboard-accessible homes.
  Footer commands cancel tasks or open their list when concurrent work cannot
  fit. No overflow menu, toggle or settings flyout retains marginal diagnostics.

## Visual composition

| Element | Decision and reason |
| --- | --- |
| Surface | Continue the existing window surface; no raised card, acrylic island, rounded container or accent-filled strip |
| Separation | A subtle top hairline only if adjacent content needs a boundary; no cell borders or separators after every value |
| Geometry | About 40 DIP high at normal text scale, 16 DIP horizontal inset, vertically centered; accommodate scaled text and the native Stop button rather than clipping |
| Typography | Existing shared Body status style, regular weight; no per-footer font system and no smaller font to solve crowding |
| Emphasis | Primary text for count, selection, activity and holds; secondary text for optional scope details and background transfer telemetry |
| Grouping | Small middle dots inside a context phrase; approximately 16 DIP between distinct groups and 6–8 DIP between an icon and its label |
| Counts | One leading Lucide List for Torrents; Film, Music or Files for Library's configuration; keep count, filter and selection text together without extra icons per number |
| Transfer directions | Lucide down/up arrows, 16 DIP, beside numeric rates; accessible names expand their meaning |
| Holds | Pause for pause, Clock for confirmed wait, caution-colored Turtle for alternative limits; the existing port icon expresses torrent connectivity rather than general health |
| Filtering | `Filtered` or the actual short criterion in text; a funnel icon alone cannot communicate which scope the count describes |
| Activity | Download for metadata work, Captions for accepted subtitle work, Clock for a confirmed wait; one meaningful icon per group |
| Color | Preserve the current owner ruling: download rate and arrow use AccentTextFillColorPrimaryBrush; upload uses TinyTorrentUploadBrush (#7756A3 Light, #C5A3E6 Dark); alternative icon uses SystemFillColorCautionBrush; port icon uses success/caution and proxy stays neutral. Use system colors in High Contrast. Arrows, icons and text keep every meaning without color |
| Motion | No animated rearrangement, pulsing dot or rotating messages; native feedback only where actual work warrants it |

Do not reserve two fixed 216-DIP fields for speeds. Align each rate's digits
and keep a compact measured width for the active format, units and cap. Reuse
the slot through numeric updates; recompute on language, scale, unit range or
cap changes. Right anchoring and stable digit widths contain normal changes.
Do not size the entire right group for every hypothetical longest status.

## Narrow widths and accessibility

The groups remain in **one horizontal row**, with each process's progress bar
below its text. Measure content in the active language and text
scale; do not hide labels to icons at guessed window breakpoints.

1. Remove the update notice, routine port/proxy, optional IP/free space and expanded filter criteria;
   retain `Filtered` where applicable.
2. Shorten explanatory wording. Move numeric caps to the rate's accessible
   description and keep visible `Limits on`; shorten batch to
   `Metadata · 2/7` with “processed” in its accessible name. Outside Library
   keep `Library metadata` so progress cannot be mistaken for torrent metadata.
3. Remove routine background transfer numbers in Library. If still needed,
   remove ordinary rate numbers in Torrents while a batch or hold occupies
   the space. Caps and `Limits on` disappear with those omitted readings;
   the pause reason and batch X remain.
4. Compact the collection phrase (`12/84 torrents`, `Videos · 24 files`) and
   retain selection. Long provider/filter/adapter names ellipsize; the full
   state is in UI Automation and the established Settings/filter surface.
5. At extreme text scale, omit the collection count before losing an active
   batch and its X or an active hold. Retain the page/configuration and any
   selection count if they fit. The table remains the collection itself.
   Never convert the batch, selection or pause to an unexplained glyph.
   If task groups still cannot fit readably, use the native work-list fallback
   in [Interface](interface.md): preserve the explicit metadata batch and X
   inline, and expose all operations through one labelled work-count button.
   Never treat the last reduction step as proof that content fits: verify it.

Keep the batch's Stop target stable while its processed count changes, using
the actual total's digit width. An activity group follows context rather than
being geometrically centered. Whitespace between activity and session absorbs
available width. A missing-adapter hold does not acquire a new InfoBar in this
proposal; its existing Resume notice may explain a refused resume, and Network
settings remains the discoverable repair surface.

There is no guarantee that all facts fit simultaneously at arbitrary widths.
The desktop's existing minimum width remains; do not raise it to accommodate
optional status. Enlarged text can increase the single row's height. It must
not produce a second line, horizontal scrolling or overlap.

Decorative icons are excluded from the accessibility tree. Each semantic group
has a complete name, e.g. “12 of 84 torrents shown; Downloading filter;
3 selected,” or “All torrents, download 1.2 megabytes per second, limit
5 megabytes per second.” Static text is not exposed as a button.
Do not announce rate samples or every processed file. Announce significant
automatic holds and batch completion through the existing notification owner,
once; do not duplicate an InfoBar announcement. X participates in normal Tab
order and activates with Enter/Space. Essential information and recovery are
available without hovering; tooltips supplement visible text and UI Automation.

## Implementation ownership and acceptance

### Background-operation integration

The owner assigned implementation of the shared service and status-bar UI to
this chat after accepting the architecture review. The
[background-work contract](background-work.md) owns the reviewed service
contract and inventory, replacing the preliminary handoff here. The UI owns
localized full/compact text, Lucide icons, stock WinUI 3 ProgressBar, accessible
names, native Cancel buttons, focus recovery and responsive presentation.
Feature owners retain execution and lifecycle decisions; the shared module
publishes coherent observations and one cancellation command per operation.
Direct chat delivery failed with Transport closed; review was exchanged through
the shared document. The service author accepted the revised interface with
the cancellation-lifetime clarifications recorded in that contract.

The working-tree implementation replaces direct task presentation properties
with coherent `BackgroundWork` observations. `WorkStatus` renders each operation
in the footer and native overflow flyout; Settings consumes the same IMDb
operation and cancellation command. Detail surfaces wrap the full description,
while the footer remains one compact row. Builds and native validation remain
outstanding.

Keep one composition in MainWindow. The existing MainViewModel supplies
torrent/session facts; Library.Browser supplies its applied rows, selection and
ambient state; BackgroundWork supplies operation observations. The bar derives
display phrases without caching a second copy of engine or Library state.
No new engine messages, polling, provider requests, settings, generic status
framework or activity scheduler is warranted.

Adoption updates the existing status-bar section of `interface.md`, relevant
Library status wording and appearance-setting descriptions, replacing the old
rules rather than maintaining two contracts. English and Spanish go through
the existing catalogue and formatting owner. Reuse existing native text and
button styles, Lucide tokens and notification ownership.

Source review found these concrete gaps: Library.Status joins row count,
filters and provider state in one string and omits selection; the current
responsive code preserves 432 DIP of rate fields while hiding meaning;
Settings/About inherit torrent list facts; batch progress displays `Done + 1`;
network labels are not a reliable freshness or Library-health signal.

Before shipping, inspect the native bar at normal and minimum supported widths,
English and Spanish, Light/Dark/High Contrast, and enlarged Windows text.
Review selection in both modes; no results versus not loaded; page/configuration
switch during a query; zero rates; caps; every pause reason; reconnect;
mixed-provider waits; and a simultaneous batch, pause and selection. Verify
Stop with keyboard and Narrator, and that changing digits do not shift targets.
These are acceptance checks for later authorized native validation, not claims
that this design-only task has compiled or tested the app.

## Fluent and Windows evidence

- [Fluent 2 principles](https://fluent2.microsoft.design/design-principles)
  support platform familiarity, focus and inclusion. They do not prescribe a
  universal status-bar layout or a mandatory icon for every value.
- [Windows status bars](https://learn.microsoft.com/en-us/windows/win32/uxguide/ctrl-status-bars)
  describes contextual status and warns against crucial information living
  only there. It discourages double-click interaction. This is explicitly
  legacy Windows 7 guidance, used for enduring interaction rationale, not
  presented as a current Fluent 2 component specification.
- [Windows typography](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/typography)
  supports native text roles, regular body text, sentence case and legibility.
  This proposal keeps the app's shared Body role.
- [Fluent progress](https://fluent2.microsoft.design/components/web/react/core/progressbar/usage)
  supports truthful progress and clear cancellation. Its web implementation
  does not dictate WinUI controls or justify a synthetic progress estimate.
- [Fluent message bars](https://fluent2.microsoft.design/components/web/react/core/messagebar/usage)
  and [WinUI InfoBar](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/infobar)
  guide persistent, actionable problems. The app's explicit placement ruling
  remains the exception described above.

## Adversarial review record

The owner's subsequent screenshot exposed a newer color ruling that this
proposal's neutral-only palette had missed. The revision preserves those
established rate and state brushes, restores one Lucide anchor per context
group, and includes owner-backed subtitle activity on Library. It supersedes
the initial review's recommendation to remove routine port status and metadata
icons. The same reviewer confirmed these revisions against current source.

The independent reviewer challenged the first proposal's wrapping fallback,
12-DIP typography, batch success ambiguity, provider attribution, global
appearance opt-ins, off-page counts and rate-driven visibility. The revision
keeps one line and shared Body text, counts processed attempts, requires the
actual batch owner's wait state, retains labelled opt-ins, removes off-page
counts and uses torrent membership rather than nonzero samples for visibility.
The final pass also removed selected-size clutter and a redundant metadata
icon, retained passive update discovery, corrected the adapter recovery path,
and removed an initial-read label that conflicted with the Library owner ruling.
The prototype source review also corrected its final width constraint and
accessible group semantics. Browser rendering could not be inspected in this
session: localhost navigation timed out and the browser rejects file URLs.
The prototype therefore illustrates the proposal without claiming rendered
layout or assistive-technology verification.
