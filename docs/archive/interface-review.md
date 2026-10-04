# Historical TinyTorrent interface review

Archived 2026-10-03. This is acceptance history for the earlier client. It does
not authorize implementation, impose Transmission capabilities, or certify the
new product. Current [interface guidance](../interface.md) preserves
the applicable user journeys. Read the current contracts before reusing a design.

**Scope update, 2026-10-03:** The acceptance history below concerns the earlier
client. The [architecture](../architecture.md) now owns
runtime and product scope; [localisation](../localisation.md) owns text
and live switching, and [testing](../testing.md) owns validation scope.
Retain applicable interaction and visual guidance. Earlier Transmission/remote
features, transport behavior, test gates, and implementation permissions do not
override those authorities or the current implementation pause.

Status: **REVISION 3.1 — DESIGN ACCEPTED for implementation, 2026-09-13.** Independent
user, UI/UX, Microsoft Fluent and keyboard reviewers passed the theoretical design.
The coordinator subsequently reopened **button width/padding**: the user said the
icon must not enlarge the button, while the proposal had narrowed that to height.
That interpretation is not approved. An explicit width clarification is pending;
unaffected implementation proceeds, but icon geometry cannot be claimed compliant.
This accepts the proposal below, not existing production edits. Implementation may
proceed when the coordinating agent releases the freeze. **Visual conformance and
usability remain UNVERIFIED**; desktop interaction remains stopped by the user.
Screenshots were not required for theoretical approval. Implementation, visual
conformance and usability verification remain separate subsequent gates.

| Independent lens | Accepted revision | Evidence |
|---|---|---|
| User / clutter and missing essentials | R3 | Eight task journeys passed; no unnecessary default fields or missing required paths |
| UI/UX design | R3.1 | Layout/behavior passed R2; final R3.1 literal labels and validation wording passed targeted verification |
| Microsoft Fluent / literal rules | R3 | Controls, hierarchy, themes, focus, help placement, native semantics and icon rules passed |
| Keyboard-only | R3 | Direct task paths, local field keys, editor protection, focus return and single-group Pieces inspection passed |

R3.1 changed only command wording and factual zero-selection validation; the other
three R3 passes carry forward. Acceptance covers Add, Preferences, fresh task-based
inspector content, shell adaptation and keyboard/icon contracts. It preserves the
user's table appearance and approved Pieces map reference. It does not authorize
new dependencies, styling systems, unrelated feature scope, or restarting desktop
control. The coordinating agent owns implementation release and subsequent verification.

[The architecture](../architecture.md) is the product authority.
The root Fluent review standard remains the styling authority. The user's table styling is preserved. No legacy
inspector tab is a content or layout reference except the **Pieces map appearance**.
The LabForms SettingsPage supplies the Preferences silhouette; the earlier Add flow
supplies evidence that recognizable sources, wanted files, and destination decisions
are useful. Neither supplies architecture to copy.

## Torrent-client assessment and shell correction

### Review repairs — implementation gate, 2026-10-02

The requested fix/polish iteration was challenged from user, keyboard/accessibility,
Fluent, performance and maintenance perspectives. These are simulated engineering
reviews, not approval by actual users. The bounded design is:

- A pending connection destination stays with the page's connection owner until
  draft resolution finishes. Queue advancement and closing wait for that decision.
  Dialog completion schedules queue advancement after its caller applies the
  decision, rather than starting another dialog inside the completion path.
  Cancel consumes the current queued addition for that destination, retaining the
  draft, current connection and later additions. Save/Discard completes the switch
  before additions resume. Unload invalidates the pending transition.
  A failed Save retains the queued source and waits for the draft to be resolved.
  The existing inspector reports successful Save/Discard to the page's queue owner,
  so a disconnected session cannot strand an addition by stopping poll callbacks.
  The resolver distinguishes accepted, canceled and failed outcomes in one place.
- Recognized touch taps and Space use the table's selection model; touch panning,
  hold gestures and embedded editors retain platform handling. Ctrl navigation
  moves current/focus without changing the selected packet or range anchor.
- Selection keeps its existing full-height bar. Its existing platform selected-text
  brush replaces the accent brush, giving a neutral cue in Light/Dark and the
  system highlight-text cue in High Contrast. No additional visual or theme token.
- Quiet sessions retain statistics-only ticks between full sweeps every 30
  completed ticks. The cadence derives from the daemon's 60-second recent-change
  window and the existing two-second tick. A full sweep, rather than reliance on
  delta retention, repairs external edits and equal-count replacements. Convergence
  takes 30 successful polls (nominally one minute plus accumulated request time);
  no new timer or setting.

The native tray also serializes startup at its existing lifecycle owner and reads
JSON error members through its existing scanner. Both changes keep the backend
independent of UI availability. No production dependency is added.

Build/headless verification and rendered interaction remain separate gates.
Desktop testing remains stopped; contrast, touch and keyboard usability are not
accepted by inference from the source review.

### Files sorting and Speed inspection — implementation gate, 2026-09-14

The user requested both capabilities. Independent user, Fluent and keyboard review
accepted this bounded design before implementation:

- Files retains its headers, hierarchy and shared column geometry. One native Sort
  command in its existing CommandBar offers Name, Size, Progress, Priority and
  Wanted, plus Ascending/Descending. Folders stay first in either direction;
  unknown values stay last. Numeric fields use actual values, with natural name
  and ordinal path ties. Each sibling group sorts independently; search retains
  its existing flat matching leaves. Apply on an explicit menu selection, including
  the current choice, and on a new or filtered listing. Progress refresh does not
  move rows underneath the user. Current policy and reapply guidance belong in the
  tooltip. Preserve native selection, expansion and focus through temporary
  snapshots, never a second persistent owner. Sorting remains available for loaded
  data independently of write permission; existing mutations keep their guards.
- Speed keeps its existing plot, two traces and 32-sample history. Hover selects the
  nearest actual sample; click also focuses the graph. Left/Right moves between
  samples, Home/End selects oldest/latest, through one focus region. Selection is
  one sample time, retained across refresh and clamped to the oldest retained sample
  when evicted. Release clears it. The native tooltip and read-only automation value
  show age relative to the latest recorded sample and both transfer rates. These
  are monotonic samples, not wall-clock timestamps. No interpolation across gaps,
  additional history or timers. One theme-aware cursor and distinct markers locate
  the inspected values. Empty, single-sample and zero-rate history remain truthful.
  Direct inspector navigation focuses the graph; empty history remains focusable.
  Help stays in tooltips; unmodified navigation keys and Ctrl+C preserve surrounding
  shortcuts and editor behavior. Only deliberate selection announces value changes.
  F6 pane entry uses the existing FocusView destination, as Alt+number and Ctrl+Tab
  already do, so it reaches Speed inspection directly. Clean General still enters
  navigation and an active editor retains its existing destination. Independent
  review accepted this single-owner focus amendment before implementation.

Implementation is followed by source review and the canonical analyzer-enabled
Release build. Live keyboard, focus, theme and pointer acceptance remain unvalidated
while desktop control is stopped.

### Property functionality — implementation gate, 2026-09-14

The user approved the functionality recommendations while questioning a menu bar.
No menu bar or new context menu is introduced. File Open, Folder and Pieces use
the existing compact CommandBar and its native overflow. Enter applies only to a
single local file; folder and text-editor keys retain native behavior. Local targets
must be contained in the captured torrent folder and actually exist. Remote paths
are never interpreted as local paths. One existing page owner launches targets.

Independent design review accepts replacing the tracker textarea with native staged
row editing: a ListView, one selected URL TextBox, Tier ComboBox, Add and Remove.
One canonical ordered tier/URL draft replaces raw-text state; existing Save/Cancel
and edit lifetime remain authoritative. Ambiguous runtime identity focuses the list
without pretending its first URL was selected. Delete removes only with list focus.

Peers gains a bounded Sources disclosure with connected-peer origin counts and
read-only webseed URLs. Text selection and Ctrl+C remain native inside the URL
editor. Empty webseed inventory collapses. Speed gains factual shared-rate and
elapsed-time bounds, including zero/single-sample states, using current 32 samples.

File-to-Pieces uses the existing 4.1+ RPC boundary plus validated nonempty file
ranges. Exact file annotation remains distinct from aggregate-block facts and the
single map caret. It survives same-torrent refresh and clears on navigation, leaving
Pieces or changing torrents. No extra polling, graph history or per-piece controls.
The existing overlay underlines intersecting aggregate blocks; the tooltip states
that these may include neighboring pieces. The read-only map remains focusable
while loading or unavailable, so Files → Pieces enters its keyboard region
immediately. Existing release clears prior torrent data before loading another;
completion updates content without moving focus. Independent review accepted this
loading-state amendment before implementation.
Native design gates are source-based; live input and visual acceptance remain pending.

### Properties comparison refinements — 2026-09-14

Independent user, Fluent, UI/UX and keyboard review accepts this bounded design
before implementation. General keeps its summary and Folder, followed by the
existing Transfer details, Torrent information, then Limits and behavior expanders.
The same wrapping, scrolling, native styles, access keys and fixed draft footer
remain. When metadata is complete and only some files are wanted, the summary
distinguishes selected size from total size. A known Last activity timestamp belongs
in Transfer details. Other speculative fields and new workflows are deferred.

Selected-peer facts retain the endpoint and two direction-specific traffic,
interest and choke lines. Row rates, progress, client and connection are not repeated.
Tracker status shows Announcing, Queued, Not contacted or the last result; sorting
uses that displayed status. Next announce shows a relative recorded schedule for
waiting trackers, Not scheduled for inactive trackers, and no future estimate during
an active/queued operation. Exact scheduled time and backend errors remain in selected
facts. Existing detail refresh supplies timing updates; no timer or new state owner.

Clean General entry focuses its selected navigation tab. An existing dirty General
draft returns to its editor. Trackers entry focuses its table unless its editor is
already open. Text editing, region keys, direct access keys and save behavior remain.
No controls, columns, resources, dependencies or backend capabilities are added.
This is a source design gate; live focus and visual conformance remain unverified
while desktop control is stopped.

### Consistency refinement — 2026-09-14

Independent user, UI/UX, Fluent and keyboard review accepts three local Add
corrections before implementation. Empty link errors and disk-space feedback
collapse; their existing Text remains the authority for visibility through one
compiled binding function. Immutable file rows use the same function to collapse
an absent parent-path caption. Nested files retain their filename/path hierarchy.
Existing measured layout, native typography, error brushes and live-region
attributes remain authoritative. Select all and Search expose their existing
shortcuts in tooltips, with Ctrl+A explicitly scoped to the file list. No new
focus stops, resources, state owners or actions. Native layout and screen-reader
announcements require the still-pending desktop verification.

### Impeccable follow-up — design before fixes

The 2026-09-14 critique identified shared button geometry, Files readability,
filter recognition and empty-result recovery. The correction preserves the main
torrent table and Pieces. No new token system, domain state or dependencies.

The filter/reset design passed independent user, Fluent and keyboard review.
Keep the native Filter dropdown. Its content becomes the selected status or label;
the default remains Filter. Only SetFilter updates this presentation, using the
existing filter owner. The accessible name and tooltip identify both the filter
and its full value, including whether it is a label. Constrain presentation to
the smaller of the existing search width cap and the measured command-content
slot, once that slot has positive width. Ellipsis must leave icon and native
chevron intact. Remove the duplicate filter value from the footer.

Add one native Reset button, with a Lucide icon and Show all torrents tooltip,
only in NoResultsContent. Reuse one ResetFilters method for this command and
revealing an added torrent. Clearing restrictions cannot exclude the bound
torrent and must preserve its draft without prompting. Existing selection
transition guards remain authoritative. FocusRows goes directly to Reset only
when the projected count is zero, the placeholder is NoResults and button focus
succeeds; the placeholder enum alone does not establish that no rows are shown.
After reset, normal row focus resumes. No new accelerator or permanent toolbar
button is needed.

Files retains single-line density. A proposed two-line default was rejected
because it repeats labels per row and reduces the number of visible files.
The accepted replacement uses Name, Wanted, Progress, Size and Priority headers
above the native tree. One view-owned pass measures header and realized metric
text, projects widths to the header, and binds row columns directly to it. Native
indentation consumes Name space only. Fit uses the smaller of header space and
the narrowest realized row, so deep indentation participates in the same uniform
decision. Priority, then Size, collapse uniformly
when the view becomes narrow; the existing selected-file facts retain them.
Use per-cell leading margins rather than spacing beside zero-width columns.
One unattached TextBlock probe measures even hidden fields with their actual
typography. Widths are recalculated, not retained as increasing maxima, so text
scale reductions and shorter values can reclaim space. Existing data updates,
row loads and view/header size changes queue one pass after bindings settle.
No all-file scan, row registry, new table implementation or per-row hide decision.
Independent review accepted this design before implementation; live geometry is
still an unverified acceptance step.

Native button padding is 11,5,11,6: 22 DIP horizontally. The current 16-DIP icon
plus 8-DIP gap exceeds it even before text enlargement. Normal-size accommodation
must fit the existing allowance. At large text scales an icon can exceed all
available padding; the owner has been asked whether to permit only necessary
growth or instead relax the icon/text size relationship. That decision remains
pending; no exception to the stated rule has been silently accepted.

The implemented filter/reset and Files corrections passed source review and an
analyzer-enabled Release build with zero warnings/errors. The native Grid label
template supplies bounded text ellipsis. This is not acceptance of live geometry,
keyboard usability or theme behavior. Shared button-padding work and the critique
backlog remain open until the pending text-scale choice and validation are resolved.

2026-09-14 owner direction: the torrent table is the central workspace, with no
left search/filter/navigation sidebar. This supersedes earlier acceptance of the
outer NavigationView. The native top navigation inside Preferences and Details
still serves their actual categories; it is not a list-filter sidebar.

| Assessment | Decision |
|---|---|
| KEEP | The eleven intentional torrent columns, composite cells, sorting, resizing, reordering and selection contract. The Pieces map's ordered raster, availability/completion distinction, range inspection, keyboard traversal and honest Unknown state. |
| REFINE | Native commands, table-adjacent search/filtering, lower resizable Details, compact property facts, supported Add and Preferences flows. Retain the recent reviewed typography/alignment changes. |
| REDESIGN | Replace the outer left navigation with a native Filter menu beside search. Evaluate General/Files/Peers/Trackers for diagnostic usefulness against supported engine fields, independently of legacy layout. |
| REMOVE | The left rail, hamburger/filter navigation route and its persistent per-label UI collection. Exclude legacy token registries, browser/HUD/polling/style controls and decorative dashboards from parity goals. |
| MISSING | Live visual/keyboard/theme acceptance remains open. Supported pre-add priorities/sequential overrides are candidates, not a mandate to expose every RPC option; post-add file priority and per-torrent sequential controls already exist. Unsupported proxy/geolocation and unverified version-dependent peer byte totals must not become invented data. |

Read-only reference inspection includes frontend TorrentTable_ColumnDefs,
TorrentTable keyboard interactions, TorrentDetails_Pieces and its view model,
Add panels/controller, and Settings. Its column vocabulary matches the native
table. Only useful behavior and the intentional Pieces appearance are retained;
no frontend styling, resource registry, component hierarchy or state architecture
is authoritative.

### Proposed shell, before implementation

Keep the existing native title bar, compact connection header, CommandBar and
full-width table with the existing lower Details split. Remove only the outer
NavigationView and its pane width from initial window sizing. The table columns
and templates are unchanged. Use the native LayerFillColorDefaultBrush directly
for the content layer over the existing Mica window. No new page, secondary shell
or dashboard.

Place a native DropDownButton labelled **Filter**, with the existing Lucide
list-filter glyph, immediately beside search inside a left-aligned content group
in CommandBar.Content. Search
uses a flexible star column, maximum width 220 and the native TextControlThemeMinWidth
(64 in the installed SDK). The command bar's native star Content column supplies
the finite available width; primary commands retain native overflow. At widths
below search minimum plus the measured Filter width and the existing 8-DIP gap,
the page's SizeChanged layout handler puts Filter on the next row. This is a
geometry-derived reflow, with no breakpoint resource, cached mode or second view.
Filter remains measured at its natural width in both arrangements. Search and
Filter SizeChanged both re-evaluate the same rule for text scaling. Custom Content
is not assumed to participate in native command overflow.
Source review caught a feedback risk in measuring the left-aligned group itself.
The accepted correction uses a stretch measuring root and a left-aligned inner
grid whose width is the lesser of that slot and the controls' desired row width.
The stack decision uses the independent root width, with no retained mode.
The menu offers one mutually exclusive choice: All torrents, Downloading, Seeding,
Paused, Errors, or one label in a Labels submenu. Use RadioMenuFlyoutItem and its
native check state; the installed WinUI SDK confirms the control and GroupName
contract. Labels are derived on menu opening, not maintained as an always-present
second navigation tree; their menu controls are released on closure. Empty Labels
is disabled. Search still combines with the
selected filter using the existing predicates and session update projection.

An active filter's factual name precedes the existing status line; the full status
is available in its tooltip. The default All choice adds no redundant status text.
Choosing a filter closes the native menu and returns focus to the resulting table
through the actual Closed event. Before applying a filter that excludes the bound
torrent, resolve its existing Details draft; Cancel retains the old filter and
draft focus, while a filter that retains the torrent leaves its draft alone.
Normalize a label against current present rows before the draft decision and again
at commitment, so a removal while the menu or confirmation is open falls back to
All. Only a missing label falls back; an empty status filter remains selected.
Escape dismisses without change and retains the
native invoker focus. If an active label disappears, filtering returns to All;
revealing a newly added torrent still clears search and filters.

Ctrl+F focuses search; Enter applies and enters the results; Escape clears the
query and enters the results. Ctrl+Shift+F opens Filter directly, and the existing
Alt+N filter shortcut remains as an alias outside Details. Menu arrows/Enter/Escape
remain native. F6/Shift+F6 cycle only command/search, table and visible Details;
the removed rail costs no focus stop. Ctrl+O, Ctrl+V outside editors, Ctrl+comma,
selection commands, inspector keys and splitter keys retain their implementations.
Standard editor chords, AltGr and open-popup priority remain protected.

No new style/resource keys, palette, dependency, view model or filter service.
Existing page state remains the filter owner. The only new UI is the native
dropdown/menu replacing substantially more persistent navigation UI. Normal,
narrow and enlarged-text fit; active/empty/error/offline/selected states; Light,
Dark and High Contrast; mouse, menu keyboard and focus return require live checks.
Desktop resumption is still pending, so source/build checks cannot pass that gate.

### Supported settings and diagnostic corrections

Keep five Preferences categories. Move the two peer-limit controls from Bandwidth
to Connection, using a section created from that destination group so validation
reveals the right category. Keep global Alt+G and assign per-torrent Alt+B to avoid
the incoming port's Alt+P. Move the capability-gated sequential default to General's
Adding torrents section with Alt+Q, preserving Start's Alt+S. Controls, draft/read/
save behavior and supported capabilities stay unchanged.

Keep all six Details tabs: Speed has an operational transfer-history purpose;
Pieces answers availability questions that the other tabs cannot. Refine selected
peer facts with directional choke/interest and actual transfer state using fetched
fields; show active webseeds separately only when nonzero. Retain the last completed
tracker announce outcome/result while displaying current Announcing/Queued status
separately. Add unchecked and discarded-corrupt byte facts to General's existing
transfer disclosure only when nonzero. No new permanently visible columns, RPC
fields, timers, data owners, or unsupported country/hostname/byte-count claims.

Long peer/tracker details must not consume the whole table. Use a native vertical
ScrollViewer around each existing selectable facts block, with a maximum height
of one third of its available tab body after the command/summary strip and gaps.
Auto height still uses only the space short facts need. This leaves at least two
thirds of that body for the existing table; outer pane minima continue to reserve
the native header and a realized row. MinimumHeight reads the bounded facts viewport,
not the potentially long text. Preserve native text selection and context Copy;
the existing table Ctrl+C command still copies the peer address or tracker URL.
Keep native scrolling without a new Tab stop. This allocation is an explicit local
composition decision, not a new resource or generic table metric.

### Review outcome

Both independent reviewers challenged the shell, settings and diagnostic design
before implementation. Their first review rejected fixed-width command content
and conflicting settings access keys; the revised design passed. Source review
then caught the width-measurement feedback and disappearing-label race described
above. Both corrected designs and their implementations passed targeted independent
source review. No runtime interaction or visual acceptance is implied.

The final Release UI publish and native tray build passed with zero warnings and
errors, with the installed WinUI analyzer enabled. The torrent columns and page
resources remain identical to the pre-change XML. The staged candidate's 50
manifest entries all match their hashes and sizes; detailed evidence and remaining
live gates are recorded in `delivery-validation.md`. No application was launched.

## Visual refinement — source audit and proposed correction

Earlier pass, 2026-09-14. That task preserved functionality, navigation, bindings and
ViewModels. Every screen's XAML and associated owner was inspected before this
proposal: MainWindow, torrent shell, all six inspector tabs, Add, Preferences,
Connections and shared dialogs. This is a source audit; the stopped desktop has
not yet been inspected again. Runtime visual acceptance remains outstanding.

The frontend token architecture is explicitly rejected. Use WinUI resources
directly, without application aliases, imported token names, shade scales or a
comprehensive spacing dictionary. New application resources require demonstrated
reuse and a semantic distinction the platform does not already supply. Existing
torrent visualization roles are the narrow exception. This refinement adds no
resource keys, brushes, dependencies or runtime services.

The correction below passed independent user, UI/UX, Fluent and keyboard design
challenge by both the Preferences and Inspector reviewers before production edits.
The coordinator released this bounded implementation; runtime acceptance is open.

| Surface | Evidence and correction | Preserved behavior and states |
|---|---|---|
| Preferences | Card padding 24 and title gap 16 add unnecessary inset inside an already padded dialog. Use padding 16 and title gap 12; remove the second, width-dependent padding assignment. This saves 20 DIP vertically per wide card. | Native controls, row spacing, label/editor reflow, hit targets, category navigation, scroll owner and commit footer. |
| Preferences context | Engine scope has the same prominence as field values. Apply the native secondary text brush without shrinking its Body text. | Full wrapping, engine identity and primary disconnected/error text. |
| Dialog identities | Add already emphasizes the source name; shared Location/Labels and Connections decisions do not distinguish identity from consequence. Use native BodyStrong for those identities. | Same text, wrapping, selection behavior, controls, focus order and Cancel defaults. |
| General | Folder label has no section emphasis; an empty stopping-facts block still participates in spacing. Use BodyStrong for Folder and collapse stopping facts only while their text is empty. | All seeding/stalled facts and the existing availability projection; no extra state or new fields. |
| Peers / Trackers | Numeric rates, percentages and counts do not match the main table's alignment. Set the existing columns' CellAlignment to Right. | Same table owner, column widths/order, templates, source bindings and selection. |
| Speed / Pieces | Current rates, history context and legend have equal emphasis. Use BodyStrong for live rates and native secondary foreground for history context and legend. | Body size and wrapping, all factual values, both charts and the approved Pieces rendering. |
| Inspector edits | Apply/Discard and Save/Cancel currently have equal emphasis. Use native AccentButtonStyle for ApplyOptions and ApplyTrackers only, retaining neutral secondary buttons. | Existing draft visibility, enabled/saving states, Lucide content, commands and keyboard behavior; no default-button or Enter change. |
| Shared themes | The custom focus dictionary names the dark theme Default. Give it the explicit Dark key required by winui-design. | Existing Light/Dark focus colors and native High Contrast pair; no new brush or palette. |

Shell, Files, Add flow and native controls retain their existing composition where
the source audit found no concrete defect. No card-per-field treatment, extra
borders, shadows or material layers. The existing Preferences CardStyle remains
the single owner of that repeated composition; native BodyStrong and secondary
brush references need no wrapper style. Font selection remains native:
ContentControlThemeFontFamily resolves to XamlAutoFontFamily in the installed SDK,
and the Gallery's type-ramp sample uses the built-in TextBlock styles. Microsoft
describes [Segoe UI Variable and Windows text roles](https://learn.microsoft.com/en-us/windows/apps/design/signature-experiences/typography)
and [native content grouping](https://learn.microsoft.com/en-us/windows/apps/design/basics/content-basics).

Acceptance must check normal and narrow layouts, long identities/paths, large
numeric values and enlarged text; Light, Dark and High Contrast; hover, pressed,
selected, disabled, focused, loading, empty, offline, dirty and saving states.
Every existing keyboard route and focus return must remain intact. Source review
and a clean build cannot certify these visual and interaction checks. The earlier
button width/padding question remains unresolved; no clipping or negative-margin
workaround is authorized by this proposal.

Implementation verification: both independent source cross-reviews passed. The
later Apply/Save accent addition also passed both design reviewers before its XAML
was changed. The analyzer-enabled Release build passed with zero warnings/errors;
the 2026-09-14 05:42:52 UTC stage has 51 files and 50 matching manifest hashes.
Bindings and XAML element counts are unchanged. Desktop resumption remains pending,
so no visual, theme, enlarged-text, Narrator or keyboard acceptance is claimed.

## 1. Product direction and shared composition

TinyTorrent is a focused Windows transfer utility: a dense torrent list with an
optional inspector, a short Add task, and occasional configuration. The table stays
the main workspace. Dialogs should look composed and finished through readable
hierarchy and aligned controls, without becoming showcases for decoration.

The shared language is native WinUI: standard type roles, controls, focus states,
neutral surfaces, the user's Lucide vectors with labels, and restrained accent emphasis. Each
surface has a clear title, one primary commit, and secondary actions beside the
content they affect. No decorative illustrations, imported fonts, card-per-field
layout, custom pill navigation, new token system, dependency, or motion framework.
The latest AGENTS.md interface rules prevail: help exists only in tooltips; surface
text is limited to labels, factual state, errors and necessary decision information.
Every app-authored button, including dialog footers, has its needed Lucide vector.
Native Windows-owned pickers retain their own controls. Buttons use one-word Windows
commands, or two only where meaning requires it. Icons fit existing padding and are
slightly larger than adjacent text. The owner's full button-size requirement
remains in force; width/padding acceptance is unresolved as recorded above.

Dimensions below are effective pixels, not physical display pixels. Standard control
metrics and text scaling remain intact. Existing layout spacing wins; where absent,
use the Windows composition relationships: 8 for close relationships, 12 between
label/content regions, and 16 for ordinary gutters. These are local composition
choices, not new resource keys. Window size and actual text pressure determine
adaptation, never monitor resolution or a blanket phone breakpoint.

Platform materials retain their normal roles: the persistent window uses its existing
backdrop, content surfaces remain neutral, and native dialogs/flyouts supply their own
layering. No acrylic cards, custom shadows, or arbitrary surface stacks. Native
corner resources and control templates retain their geometry.

## 2. Add torrent

**User question:** What am I adding, which files do I want, and where will they go?
The source is something chosen or pasted; a filesystem path is not the primary form.

Normal composition, one column:

```text
Add torrent                                      native dialog title

[Browse]  [Paste]                                 ordinary icon+text buttons

Ubuntu desktop                                   resolved name, body strong

Files                       [Select all] [None]
1 of 1 selected · 5.8 GB
┌──────────────────────────────────────────────┐
│ ☑ ubuntu-desktop.iso                  5.8 GB │  native selectable rows
│   optional relative parent path              │  list owns its scrolling
└──────────────────────────────────────────────┘

Save to
C:\Users\…\Downloads
[Change] [Copy]
☑ Start downloading

                                     [Add] [Cancel]  native footer
```

Empty/link mode uses the default native dialog width. A resolved file list uses the
800-content-width ceiling shared with Preferences to make real filenames readable;
its actual width never exceeds the native available dialog viewport.
There is no extra column merely because space is available. The filename receives
flexible width, size receives its measured text width, and deep paths never push the
leaf filename out of view. This rule, rather than a chosen path character count,
defines useful width. Summary names wrap; list filenames trim with full text available
to keyboard users and automation as well as hover users.

### Journey and state decisions

- **Empty:** the labeled link editor, two source actions and destination/start choices
  are visible. No empty file table or fake torrent summary. Add is unavailable until
  there is a valid source and destination. The link editor receives focus after
  initialization; Browse's tooltip is `Choose a .torrent file`. Paste's tooltip names
  magnets and URLs.
- **Choose file:** native picker filters for `.torrent`. The dialog acknowledges reading
  immediately. Parsing is off the input path; Cancel stays available. Successful parsing
  replaces the source and initially selects all files. The resolved torrent name is
  primary; the input filename is in its tooltip. Selected count and bytes appear
  once above Files, not repeated as totals beneath the source title.
- **Paste:** read the clipboard only after this action. Show a labeled `Magnet link or
  URL` editor with the pasted text, so keyboard entry remains possible if the clipboard
  is empty. Keep the caret in that editor; do not auto-submit or silently read again.
  Invalid input gets a specific inline message. A friendly magnet name or URL filename
  and host identifies a valid source; do not use a long raw URL as the heading.
- **Link without metadata:** replace the file region with the factual status
  `Files unknown`. A tooltip explains that file choices become available after adding
  and receiving metadata. This is a normal factual state, without warning severity.
  Pre-add metadata acquisition must not silently add a torrent to the engine.
- **Replacement:** cancelling either native picker preserves all previous choices.
  Disable Add while reading a candidate. Keep the accepted source identity and its
  choices visible; publish the replacement only after successful parsing. On failure,
  identify the failed candidate in the error while preserving the accepted source,
  search, wanted choices and destination. A successful replacement starts with all
  its files wanted. The accepted refinement below governs this transition.
- **Wanted files:** native multiple selection with visible checkboxes. Search files
  filters displayed rows without changing wanted choices; its accessible name is
  `Search files`. Select all/None apply to all files, regardless of filter. Space toggles
  the focused file; count and total size update together. Select all/None are explicit
  bulk actions. If a known file list has no selected files, disable Add and show
  `At least one file required`. The purpose is adding usable content, not creating a
  silently inert torrent. The unknown-file link state is exempt.
- **Local destination:** preselect the engine default. Show one path line retaining
  the folder leaf, trim the parent portion, and expose the full path through its
  tooltip and Copy. Change opens the native folder picker. If the default is absent, show
  `No folder selected` with Browse. No editable local source or destination
  path is presented as the normal workflow.
- **Remote destination:** show the active connection name and a full-width labeled
  `Folder on <connection>` text field, prefilled from that engine. Explain once that
  this folder is on the remote computer in a tooltip. No local picker or local filesystem validation.
  Require a nonempty server-absolute path without assuming Windows path syntax.
- **Free space:** an explicit `Check space` secondary action beside destination
  queries the connected engine for that path. Show checking/result/unavailable inline;
  never block typing or fire a network request on every keystroke. A known shortage
  is a warning naming selected size and available size; an unavailable check does not
  claim the folder is invalid or disable an otherwise valid Add.
- **Commit:** the primary button is always `Add`; the adjacent Start downloading
  checkbox communicates whether it starts or remains paused without duplicating words.
  Capture one source, destination, and file selection before sending. Freeze edits and
  show `Adding…`; do not label an in-flight committed write as cancellable. On success,
  close and reveal/select the added torrent in the existing list. On duplicate, report
  `This torrent is already in your list`, keep the existing torrent unchanged, and offer
  `Show` plus Close. On rejection, restore edits and focus the relevant field
  where known. An uncertain transport outcome says it could not confirm addition and
  reconciles with the list before permitting an automatic retry.

### Short-height composition

The native footer remains reachable. One stock ScrollViewer directly in the dialog
content slot scrolls the form body. The same wanted-files ListView keeps a finite
height derived from the actual viewport and sibling content, with one native-row
floor; it owns collection scrolling. Keep one form, one file list and stable
Add/Cancel commands at every supported height. No Files drill-down or extra mode.
Native focus visibility and scroll chaining require live verification.

## 3. Preferences

**User question:** Where is the setting that changes this behavior, and will saving
change this computer or the connected engine?

Keep the five approved categories: General, Connection, Bandwidth, Queueing, Advanced.
The name Advanced is existing scope, not permission to add an engine field dump.

```text
Preferences                                      native title
Connected engine: Home server                    visible scope

General  Connection  Bandwidth  Queueing  Advanced  native top navigation

┌ Download folders ─────────────────────────────┐
│ Download folder                              │
│ [full-width path/value appropriate to scope]  │
│ ☐ Keep incomplete downloads in another folder│
│ [dependent folder, enabled only when checked]│
└───────────────────────────────────────────────┘

┌ Adding torrents ──────────────────────────────┐
│ ☑ Start newly added torrents                  │
│ …                                            │
└───────────────────────────────────────────────┘
                                      [Save] [Cancel]
```

The content maximum is 800, observed in the LabForms reference. Native dialog padding
and viewport determine actual size; there is no fixed 520-height body and no guessed
subtraction for dialog chrome. One native dialog content scroller owns overflow.
The native Save/Cancel footer stays fixed. Category navigation may scroll with content
in a short window; that explicit compromise is preferable to a second nested scroller.

Use one text-only top NavigationView with native overflow and selection following
focus. F6 reaches its visible selected entry or native overflow; Alt+1–5 moves
directly to each category's first useful field. Native overflow preserves category
access without a second picker, duplicated selection or discarded draft.

Cards group related decisions, not individual fields. Use one 16-DIP card padding
and a 12-DIP title gap. Separate cards by 16. Card titles
use a standard strong text role, optionally a small Lucide icon where it improves
recognition. No repeated 40-pixel illustrations. Help about scope or consequences
belongs in tooltips; necessary units remain in field labels. No card descriptions.
Ordinary labels are readable body text, not tiny muted
captions. A repeated numeric row has a 160 label column (reference), 16 gap, and an
editor at least wide enough for its meaningful value and native affordances. Stack the
label above the editor when those measured parts do not fit. Folder/URL values always
get a full-width row below the label; no squeezed long path beside a fixed label.

### Groups and reading order

| Category | Groups in reading order | Content and decisions |
|---|---|---|
| General | Download folders; Adding torrents; This computer | Default folder, optional incomplete folder, part-file naming; existing add defaults including supported sequential download; local tray/startup/exit behavior clearly separated from remote scope |
| Connection | Incoming connections; Peer discovery; Peer limits | Listening port and related startup/forwarding choices, encryption; supported discovery options; global then per-torrent peer limits |
| Bandwidth | Speed limits; Alternative limits; Schedule | Download then upload with adjacent units; alternative enable/rates; enabled schedule, start/end time and wrapping weekday checkboxes |
| Queueing | Active torrents; Seeding limits | Download and seed queue limits, stalled handling; ratio and idle stop conditions |
| Advanced | Blocklist | Enable, URL, current count, explicit Update. No deprecated cache control or speculative engine tuning |

Booleans use CheckBoxes because **Save commits edits**. Dependent numbers/paths remain
visible but disabled when their checkbox is clear, retaining their draft value. Time
range explicitly uses the connected engine's local time. Units sit with their numeric
label. Unsupported engine fields are omitted as capabilities, not shown as broken
controls. Do not invent replacements for cut features.

Loading preserves the title and Cancel and says `Reading preferences…`. Failure offers
Retry and Close without constructing a fake editable form. Saving captures both engine
and local edits once and freezes editing. During the committed request, Save, Cancel,
Close and Escape dismissal are disabled until its result; success closes, and failure
restores editing and Cancel. Cancellation before Save discards the draft. A failed
save retains the draft, names failure scope, and does not claim a transaction across
engine and tray. If one scope saved and another failed, say which saved and retry only
the remaining scope. Validation reveals the category and focuses the invalid field.

Port test and blocklist update are explicit commands on the last saved configuration.
When the associated field is dirty, disable that command with `Save changes before
testing/updating` in its tooltip. Free-space check uses the shown destination and names it.
Command progress/results are inline and prevent duplicate invocation while retaining
unrelated draft edits. After all edits save successfully, close the dialog; reopening
reads the actual state. These rules avoid misleading immediate-toggle behavior inside
a staged form. [Microsoft's toggle guidance](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/toggle-switch)
supports CheckBoxes for changes requiring a subsequent commit.

## 4. Torrent inspector and shell

**User question first; engine field second.** These sections are newly organized from
transfer tasks and supported engine capabilities. Existing legacy or current text dumps
do not earn a place merely because a property exists.

```text
[existing toolbar, filters, torrent table — styling preserved]
──────────────── resize affordance ────────────────────────
Ubuntu desktop                                      [Close]
General   Files   Peers   Trackers   Speed   Pieces
<one bounded selected view>
```

The selected torrent name is persistent. Each static view is a local SelectorBar item,
not a closable document tab. At insufficient width a native ComboBox exposes the same
six choices. Keep text labels. All views remain reachable without horizontal page
scrolling. Existing table controls inside Peers/Trackers retain their own horizontal
column scrolling, which is meaningful for tabular data.

### General: understand progress, then change behavior

The first region answers `What is happening?`: status, one relevant progress measure,
downloaded wanted bytes/selected size, remaining time when meaningful, current download
and upload rates. Metadata acquisition and verification show their own progress instead
of an apparently stalled download percentage. Unknown ETA reads `Unknown`; inapplicable
ETA is omitted. An actionable engine error appears
directly below status, not in a metadata expander.

When seeding, prioritize uploaded total, ratio and the effective stopping rule in the
same region. When excluded files make wanted completion differ from whole-torrent
completion, label the primary value `Selected files` and expose the full-torrent
percentage in Transfer details. Wanted 100% never implies every file or piece is present.
When stalled, show known facts such as no sending peers or unavailable wanted bytes
with links to Peers/Pieces; do not invent a cause from a zero speed sample.

The second region answers `Where is it?`: destination with full-value access, Open folder
for a local engine, and Change location using the existing relocation command. A remote
destination is labeled with its connection and never opened as a local path.

Next is a collapsed `Limits and behavior` disclosure: honor session limits; download and
upload enable/value pairs; seeding mode and dependent ratio/idle values; bandwidth
priority; peer limit; supported sequential choice. Expanding reveals one coherent editor
and a General footer with `Apply` and `Discard`, outside the General scroller.
This footer appears only while the torrent has a dirty draft; the disclosure introduces
no nested scroller.
CheckBoxes stage choices, and Apply commits. Unrelated live updates must not overwrite
dirty values. General and tracker editing share the draft lifetime defined below.

Last is collapsed `Transfer details`: uploaded total, ratio, downloaded total, verified
and remaining bytes, then added/completed dates. A separate `Torrent information`
disclosure holds hash (copyable), creator, creation date, comment and private/public
status when available. Copy magnet is an action, not a multi-line link taking over
the page. MIME guesses, internal torrent-file path, sequential start-piece index,
every timestamp, and duplicated completion percentages are excluded from the default
product presentation. They are not needed to answer these questions.

General's content has one scroll owner. Its torrent identity/navigation stays outside
that scroll. Most users see status/location without opening an editor. No stack of
always-visible controls pushes basic torrent facts below the usable pane.

### Files: choose content and understand its progress

Use the folder hierarchy. Reading order is Name, Wanted, Progress, Size, Priority.
Wanted is factual text (`Yes`, `No`, `Mixed`), never an inert checkbox that looks
interactive. Compact rows always retain Name, Wanted and Progress; Size/Priority remain
available on selection. Commands are `Download`, `Skip`, `Priority` and `Rename`.
Download/Skip act on the selected descendants immediately; Priority opens native
Low/Normal/High menu choices that act immediately. The selection count makes scope
clear. Rename opens the native-style name dialog for one valid selection.

There is **no Files draft or Apply button**. Each command captures the actual file
indices once; disable mutation commands through the response, and retain confirmed
row facts until success. Failure preserves those facts and shows a concise inline
error. Changing selection during a request cannot retarget it. Ordinary browsing
never prompts to save. This removes draft state and keystrokes for a frequent task.
Folder aggregate state can be Mixed; totals include every descendant. Skip neither
deletes existing data nor changes its downloaded-byte total. Native selection semantics
remain selection semantics; Space is not silently repurposed to start a transfer.

Search files locates content without changing wanted choices. The virtualized collection
owns scrolling; no per-file card. Missing metadata reads `Metadata unavailable`; no
selection disables selection commands. Full paths and all hidden values are accessible.

### Peers: diagnose whether useful connections exist

Summary: connected peers, peers supplying data, and peers receiving data where supplied
by the engine. Default columns: Address, Download, Upload, Peer has, Client, Connection.
`Peer has` is the remote peer's whole-torrent percentage, never our wanted progress.
Use clear units and formatted state labels rather than unexplained bit flags. No Add
peer/Ban peer controls: the engine does not support them. Empty states distinguish no
connections from still loading. Optional origin statistics are secondary; no giant
always-visible breakdown before the peer table.

### Trackers: diagnose discovery and repair the list

Default columns: Tracker/host, Announce status, Next announce, Seeds, Leechers.
Preserve tier order and primary/backup relationships. Full URL, peers returned by the
last announce, last announce time and scrape time/result are available on selection.
Unknown counts read `Unknown`, never zero; never-contacted reads `Not contacted`.
Scrape counts describe a dated tracker response, not a live connected-peer census.
A concise error appears
as text, not color alone. `Reannounce` applies to this torrent. `Edit` reveals
the existing tier-preserving URL editor and Save/Cancel; the editor is not permanently
occupying half the pane. Explain blank lines between tiers in the editor tooltip. Preserve
engine tiers and validate the editable value without silently flattening them.

### Speed: understand recent transfer behavior

Show current download and upload rates above a compact time series, with a visible time
window and rate units. Download/upload must be distinguishable by labels and line
treatment, not only color. Show both using bounded timestamped direction samples for
the selected visible detail; the existing active-direction history must change.
Do not switch one unlabeled
line from download to upload according to torrent status. History starts when observed;
no invented history, interpolation through unknown gaps, or unbounded retention. Provide
a textual current/peak summary for users who cannot read the chart. An empty history
says `Collecting speed history…`.

### Pieces: preserve the liked map

The legacy map's geometry, visual density, completion overlay and rarity appearance
are the explicit visual reference. Its accompanying summary states piece count/size,
completion and availability meaning; a compact legend names each map state. Retain
non-color differentiation for rare/unavailable states and a textual summary accessible
without inspecting individual pixels. The bitmap is one focusable group. Alt+6 focuses
it; arrows navigate drawn blocks, Home/End the first/last block. One selected-block index
drives its focus outline and accessible range/composition value, with Ctrl+C to copy
those facts. No visual or automation element is created per piece. Keyboard focus
reveals the same block tooltip as pointer hover; updates do not announce every poll.
High Contrast uses system colors and shape/pattern
distinctions. Implementation is one lean raster/bitmap path, not a visual element per
piece. The expensive availability payload is requested only while Pieces is visible.
Use `Have` for the local state unless the pinned daemon's have-bit invariant is proved
to mean verified. This wording preserves the approved appearance. Availability means
current connected peers; unavailable does not mean unobtainable from the swarm. Absent
availability is Unknown, and missing magnet metadata is a metadata state. Aggregated
blocks expose their range and mixed composition, without inventing per-piece
download-in-flight, partial progress or global rarity. Explanation belongs in tooltips.

### Inspector editing lifetime

General and Trackers have one active edit session, bound to the torrent identity and
editor. Same-torrent view changes retain that draft; entering a different editor first
asks `Save`, `Discard`, `Cancel`. Changing torrent, clearing/multiplying selection,
closing Details or closing the window uses the same guard. Cancel preserves the prior
selection and returns to the editor. A disconnected session retains the draft and
disables Save; Discard/Cancel remain available. Reconnection refreshes confirmed facts
without overwriting draft values; a removed target cannot receive a write.

Save captures identity and draft once. Commit disables editing/dismissal until the
result, then success clears the draft, or failure keeps it and restores Cancel. No
silent autosave, hidden-target Ctrl+S, or parallel drafts per view. Files commands have
no draft and are unaffected. This deliberate boundary supports frequent file decisions
without confirmation prompts and protects infrequent multi-field configuration.

### Resizing and compact mode

One page owner allocates table and inspector height. First opening uses the requested
40% inspector share; an explicit user adjustment is remembered and clamped to the
current available space. The resize affordance sits in the existing gutter, displays
the vertical resize cursor and native hover/pressed/focus feedback, and is keyboard
adjustable. Its accessible name is `Resize torrent details`; expose the current value
and permitted range to automation. Up/Down steps correspond to a native row height,
Home/End to the usable bounds. Hit testing belongs to the gutter and must not cover
table rows or headers. No table geometry owner or token is added.

The split is usable only if the table can show its header and a complete data row and
the inspector can show title, navigation, and a complete primary content row. These are
measured semantic minima, not a fixed arbitrary pane height. If both cannot fit,
**switch to a single-pane details presentation** in the existing content area with a
visible `Back` action (tooltip `Back to torrents`). Selection and requested split height survive. Going
back restores the table; selecting Details returns to that torrent. When room returns,
restore split presentation if details are still requested. Never silently hide the
requested inspector and keep polling it. Only the actually visible selected inspector
view receives its detail payload.

## 5. Cross-surface interaction and inclusion

Text-entry and shortcut handling follow the active-composition rule in
[the localisation contract](../localisation.md#ownership-and-live-behavior).
Apply it before the application keyboard map below, including Enter, Escape,
navigation keys, and explicit draft-saving commands.

Torrent rows announce the torrent name, Files the relative file/folder path, Peers
the endpoint, and Trackers the host/full URL. Never announce a CLR type such as
`TinyTorrent.Torrent`. Named cells expose values without putting every changing number
in the row name. This is host semantics, not a visual table redesign. Native Title is
used for dialog titles, BodyStrong for source/section identity, Body for labels/values,
and Caption only for secondary facts. Decorative icons are excluded from automation;
button content yields one accessible command name.

Every field has a persistent visible label and matching accessible name. Summary
headings have heading semantics. Native control roles/states remain intact. Icon-only
Close and resize affordances have names; unfamiliar commands retain text. Read-only
hashes, paths and errors are selectable/copyable where useful. Tooltips hold help and
supplement truncation, available on keyboard focus and through automation descriptions.
Labels and context identify the action without a help paragraph. Important factual
full values remain accessible through selection, copy or named controls without hovering.

Keyboard order follows the diagrams. Dialogs contain focus and return it to their
invoker. Source replacement does not unpredictably jump to the primary action.
Picker cancellation returns focus to its action. Validation focuses the affected
field after revealing it. Async results are politely announced once, without stealing
focus or announcing every live speed tick. Escape cancels drafts through the active
dialog's rules; it never claims to undo a submitted engine command.

Light, Dark, High Contrast, runtime theme changes, large text, long paths, no torrent,
multiple torrent selection, loading, empty, error and disconnected states are designed
states. No-selection inspector says `No selection`; multiple selection says
`Multiple torrents` and shows no stale editable torrent. The requested inspector
remains open; any transition first observes the shared dirty-draft guard.
Disconnect retains recognizable identity/read-only last data with a visible stale
state and disables writes until reconnected; it does not present stale values as live.

Use native brushes/styles so system theme and accent changes propagate. Text and
meaningful controls must meet the project's contrast requirements. Chart/map meaning
has a text/shape equivalent. Do not use thin low-contrast labels for the sake of density.
No new accessibility framework or elaborate announcements pipeline is needed.

Feedback begins with the action; expensive parsing/drawing/network work follows without
blocking pointer or keyboard response. Native transitions may express navigation,
disclosure and state changes; no decorative motion or animation of a live pane size.
Honor Windows animation preferences. Haptics, sound, widgets and notification systems
are not relevant to these three workflows and are intentionally absent.

### Keyboard map and focus paths

Scope priority is editor/popup, active dialog, active view, then shell. Editors retain
Ctrl+C/X/V/A/Z/Y and normal cursor/selection keys. They consume Delete without bubbling
into torrent removal. Enter first accepts an editor or popup; it never submits a
multiline tracker editor or converts file selection into Add. Ctrl+S explicitly commits
an active draft after flushing/validating pending NumberBox/text input. Escape first
closes a popup, then returns from an in-dialog subview, then cancels the dialog or
invokes its dirty-draft guard. Committed writes disable dismissal through their result.

Lists are one focus group with native arrows/Home/End and Shift/Ctrl selection;
rows are not separate Tab stops. Add's wanted list uses Space to toggle the focused
wanted file. Files' selection retains native selection keys, with Download/Skip as
explicit commands. Hidden/disabled controls, decorative content and read-only summaries
add no Tab stops. F6/Shift+F6 cycles navigation, toolbar, table, inspector; in dialogs it cycles
navigation, active content and footer. Native NavigationView owns section arrows and
overflow, including its normal Enter/Escape behavior. Direct category shortcuts select
the category and focus its first useful control, bypassing the navigation control.

| Scope | Shortcut | Exact action |
|---|---|---|
| Shell | Ctrl+O | Native torrent picker directly, then populated Add |
| Shell | Ctrl+F | Focus torrent filter; Enter focuses resulting current row; Escape clears filter and returns to rows |
| Shell | Ctrl+Shift+F / Alt+N; Alt+Z | Open Filter (native arrows/Enter); focus visible inspector splitter |
| Shell, outside editors | Ctrl+V | Paste link into Add; invalid/empty text leaves focus in its editor |
| Shell | Ctrl+,; Alt+D | Preferences; show/focus current torrent Details |
| Add | Alt+B / Alt+P / Alt+D / Alt+F | Browse source / Paste link / destination / focus Files |
| Add | Alt+S / Alt+A / Alt+N | Start checkbox / Add / None |
| Add file view | Ctrl+F; Ctrl+A; Space | Search; all files (search editor selects text); toggle wanted file |
| File search | Enter / Escape | Focus matching list / clear filter and focus list; first Escape does not close Add |
| Preferences | Alt+1…5; Ctrl+S | Category directly; Save |
| Inspector | Alt+1…6; Ctrl+Tab / Ctrl+Shift+Tab | View directly; next/previous view inside inspector |
| General | Alt+L; Ctrl+S | Open limits editor and focus first field; Apply active draft |
| Passive torrent row | Shift+F10 / Menu | Context menu on current row, preserving documented selection scope |
| Passive torrent row | Alt+S / Alt+P; Delete | Start / Pause selection; Remove confirmation (keeps data by default) |
| Torrent context menu | Q then T/U/D/B | Queue submenu, then Top/Up/Down/Bottom; same command owner as pointer actions |
| Files | Ctrl+F; Alt+W / Alt+K / Alt+P; F2 | Search; Download / Skip / Priority menu; Rename one selection |
| Trackers | Alt+E / Alt+R; Ctrl+S | Edit with URL caret / Reannounce torrent; Save active draft |
| Selected peer/tracker/path, outside editor | Ctrl+C | Copy endpoint/full URL/full path for that focus context |
| Splitter | Up/Down; Home/End | Native-row step; usable minimum/maximum |
| Compact inspector | Alt+Left | Back, through dirty-draft guard when needed |
| Confirmation | Enter; Escape | Native default action; Cancel (default for dirty/destructive decisions) |
| Dirty confirmation | Alt+S / Alt+D / Alt+C | Save / Discard / Cancel; Cancel remains default |
| Pieces bitmap | Arrows; Home/End; Ctrl+C | Navigate blocks; first/last block; copy selected range/composition |

Preferences field access keys are local to the selected category. Category numbers,
Ctrl+S, Escape and F6 are reserved. Keys focus the stated control, not its label:

| Category | Alt+key → field/action |
|---|---|
| General | D download folder, I incomplete enable, F incomplete folder, P part naming, S start-on-add, T original-torrent disposal, Q sequential default, A existing tray add behavior, E exit behavior, O Open log, C Check space |
| Connection | P port, R random port, F forwarding, E encryption, X PEX, D DHT, L local discovery, T port test, G global peers, B per-torrent peers |
| Bandwidth | D download enable, L download value, U upload enable, V upload value, A alternative enable, B alternative download, C alternative upload, S schedule enable, F start time, T end time, W weekdays |
| Queueing | D download queue enable, L download count, S seed queue enable, N seed count, E stalled exclusion, T stalled minutes, R ratio enable, V ratio value, I idle enable, M idle minutes |
| Advanced | B blocklist enable, U URL, P Update |

General inspector has an equally direct map, scoped so shell commands never consume
its keys: Alt+O Open folder, Alt+M Change location, Alt+C Copy path, Alt+T Transfer
details, Alt+I Torrent information, Alt+L Limits. Inside Limits: Alt+H honors session
limits, Alt+D download enable, Alt+N download value, Alt+U upload enable, Alt+V upload
value, Alt+R ratio mode, Alt+A ratio value, Alt+S idle mode, Alt+B idle minutes, Alt+P
priority, Alt+G peers, Alt+Q sequential. Ctrl+S applies and Alt+Z discards. Escape from
the editor closes a popup first, otherwise resolves its dirty draft and returns focus
to Limits; with no draft it simply returns to Limits. This does not close Details.

Weekdays are one focus group: Alt+W enters it, Left/Right traverses days, Space toggles,
Tab exits. Time/number controls retain native internal keyboard behavior. Unsupported
capabilities remove their key without reassigning others. Save/Cancel use Ctrl+S/Escape
so they do not collide with fields. Buttons/menus display accelerators; fields use
native key tips. Retry uses Alt+R in the loading/error surface, which has no field keys.

Shortest task traces to challenge:

- Add file defaults: Ctrl+O → picker choice → Alt+A. Wanted changes: Alt+F → arrows/Space;
  destination: Alt+D → picker/remote field → Alt+A. No header/footer tour.
- Add link: Ctrl+V outside editors → Alt+A; correction stays in the editor.
- Preferences: Ctrl+, → Alt+category → Alt+field → edit → Ctrl+S. A paired enable/value
  uses two direct keys, not traversal through unrelated settings.
- File decision: select torrent → Alt+D → Alt+2 → Ctrl+F → query → Enter → selection
  → Alt+W/K/P or F2. No Apply and no prompt on ordinary selection changes.
- Rejected value: field revealed/focused → edit → Ctrl+S. Cancel restores the invoker;
  if resizing removes it, focus its semantic replacement (category picker, Files action,
  or selected torrent row), never a disappeared control or arbitrary page start.
- Queue decision: current row → Menu → Q → U/D/T/B. Start/Pause uses Alt+S/P with
  passive row focus; text-editor keys and table arrows/selection remain untouched.
- General peer limit: Alt+D → Alt+1 → Alt+L → Alt+G → edit → Ctrl+S; it never requires
  tabbing through rate and seeding controls. Discard is Alt+Z or Escape then Alt+D.
- Pieces facts: Alt+D → Alt+6 → arrows → Ctrl+C. A sighted or screen-reader keyboard
  user can inspect the same block facts as a pointer user without thousands of stops.

### Buttons and icon authority

Canonical visible labels are `Open`, `Change`, `Copy`, `Edit` and `Show`; domain prose
such as opening a folder or copying a magnet describes the operation, not extra button
words. The current surface and Lucide icon provide the object context.

One app authority supplies only needed Lucide native vectors, with no icon runtime.
The icon is slightly larger than adjacent text and fits existing padding without
increasing button height. All app-authored footer buttons retain native ordering,
focus, hit area and primary/close semantics. OS-owned pickers retain their own buttons.

| Commands | Lucide vector |
|---|---|
| Browse, Change, Open | folder-open |
| Paste | clipboard-paste |
| Add | plus |
| Save, Apply | save |
| OK | check |
| Cancel, Close | x |
| Back | arrow-left |
| Files | files |
| Select all, None | list-checks, square |
| Check space | hard-drive |
| Retry, Update, Reannounce | refresh-cw |
| Edit, Rename | pencil |
| Copy | copy |
| Discard | undo-2 |
| Test | network |
| Show | arrow-up-right |
| Download, Skip, Priority | download, circle-slash, arrow-up-down |
| Start, Pause, Force start | play, pause, fast-forward |
| Remove, Remove data | trash-2 |
| Preferences, Connections | settings, plug |
| Details, Filter, Clear | panel-bottom, list-filter, x |
| Verify | check-check |
| Queue, Top, Up, Down, Bottom | list-ordered, arrow-up-to-line, chevron-up, chevron-down, arrow-down-to-line |
| Labels, Speed mode | tags, gauge |

The icon rule applies to every existing shell/inspector/menu action, not just this
inventory's newly discussed buttons. Reuse the existing matching Lucide vector first;
an unlisted action must receive its semantic vector in the same icon authority, with
no missing-icon exception and no new icon package. Contextual one-word Queue submenu
labels replace verbose repeated `Move to…` wording.

## 6. Adversarial design gate

Challenge BOTH unnecessary characters and missing essentials: every initially visible
label/value/action must support a decision, and every required task must have a
discoverable path. Neither a field dump nor hidden essentials passes.

Reviewers must decide the proposal, not infer acceptance from existing code. Each
finding names the user goal, a concrete failing scenario, and the smallest correction.

1. **User lens:** Walk through adding a chosen file, pasting a link, changing a remote
   destination, selecting no files, cancelling a picker, correcting an error, receiving
   a duplicate result, changing a preference, and inspecting a downloading/stalled
   torrent. Is the next action apparent and its effect truthful? Is any work discarded
   or committed unexpectedly?
2. **Product designer lens:** Read each diagram and field ordering without implementation.
   Does hierarchy prioritize the user's question? Do the default surface and each
   compact alternative retain essential content and clear navigation? Does visual
   density come from alignment and disclosure rather than reduced readability?
3. **Microsoft/Fluent lens:** Challenge semantics, command placement, state feedback,
   navigation, sizing, text scaling, keyboard/automation, themes, typography, materials,
   color, geometry, iconography, motion and writing against the cited platform guidance.
   Do not prescribe every available Fluent effect simply because it exists.
4. **Architecture lens:** Can each behavior be implemented through its existing owner
   with one source of state? Is every new state demanded by a stated user interaction?
   Is hidden work released? Does any proposal require another styling/configuration
   system, uncontrolled history, or per-piece/per-file visual bloat?

**Theoretical pass:** no unresolved scenario contradicts the layout/interaction rules,
engine capabilities or authority documents. Concrete amendments are incorporated here
before implementation resumes. The author does not independently declare its own
proposal passed.

**Later conformance:** implementation is compared with this accepted design at relevant
sizes/states/themes; then usability is exercised. Neither a successful build nor a
working RPC is visual or usability acceptance. Desktop interaction is currently stopped
by the user. Nothing in this proposal authorizes restarting it.

## Sources and evidence

### Accepted extension: Location and Labels

The independent user/Fluent/keyboard review accepted this bounded extension after
checking Transmission 4.1.1's request semantics. These existing dialogs follow the
same composition, native footer, icon authority and sole shell modal gate as Add.

- **Location:** selected name or torrent count, then a local middle-trimmed path with
  Change and Copy, or an editable `Folder on {connection}` for a remote engine.
  Change opens the native folder picker; cancellation preserves the draft. `Move files`
  retains its existing checked default. Its find-versus-move explanation belongs in
  tooltip/accessible help. Check space shows factual free space; a shortage comparison
  includes known required bytes and never invents an unknown size. Save/Cancel footer.
  Initial focus is Change locally or the remote path; Alt+D reaches it, Alt+M reaches
  Move files, Ctrl+S saves outside native popups, and Escape cancels.
- **Labels:** selected name or torrent count, a multiline Labels editor initialized
  with the selection's common labels, Save/Cancel. One-per-line, replacement and empty
  clearing semantics belong in tooltip/accessible help. Initial focus is the editor;
  Enter inserts a line, Ctrl+S saves, Escape cancels.
- Both dialogs capture hashes, session and draft once before dispatch. Editing, Save,
  Cancel, title close and Escape are unavailable while committing. A normal rejection
  retains the draft and restores editing/cancellation with factual inline feedback.
  A disconnected session retains the draft, shows Disconnected and disables Save.
- A successful Location RPC acknowledges the request, not a completed physical move;
  closing the dialog must not claim files moved. The existing Core refresh shows actual
  subsequent state or torrent errors. A transport failure after dispatch shows
  `Could not confirm the update`, retains a read-only copyable draft, disables Save,
  and offers Close after the request ends. Retry guidance belongs in its tooltip.
  This reuses Add's uncertainty behavior, without a per-target receipt system.
- Icons use the established pairs: Change/folder-open, Copy/copy,
  Check space/hard-drive, Save/save, Cancel/x. The separate button width clarification
  remains pending; this extension does not settle it.

This is theoretical acceptance only. Build, source conformance and actual usability
remain separate gates.

### Accepted correction: Preferences local folders

Local Download folder and Incomplete folder retain their full-width native TextBox,
header and staged value, but the field is read-only, selectable and copyable. Each
has an adjacent Change button opening the owner-window native folder picker. Full
paths remain accessible values and tooltips; remote fields remain editable and have
no local picker. A canceled picker preserves the draft; acceptance stages the chosen
path and returns focus to Change. The incomplete-folder checkbox enables its field
and button together. An active picker prevents a second invocation and Save.

Alt+D and Alt+F reach Change locally or the corresponding editor remotely. Visible
buttons say Change with the existing folder-open icon; accessible names distinguish
Change download folder from Change incomplete folder. The independent Fluent, user
and keyboard review accepted this correction before implementation. No new layout,
settings owner, or dependency is required.

[qBittorrent PropertiesWidget source](https://github.com/qbittorrent/qBittorrent/blob/master/src/gui/properties/propertieswidget.cpp)
was checked for useful data coverage: progress, rates, location, transfer totals,
availability, peer/seed facts and provenance. Its layout and unsupported features are
not copied; Transmission capabilities constrain the inventory.

- Local product plan and root `Fluent 2 design and review standard for WinUI 3.md` were
  read. LabForms `Pages/SettingsPage.xaml` was read for hierarchy, aligned rows and
  category/card organization. Legacy Add source was read for task comparison only.
- Available engine-shaped inspector contracts were inspected in
  `src/TinyTorrent.Core/Inspector.cs`; they demonstrate data availability, not the right
  product order. No legacy inspector content ordering is adopted as authority.
- [Windows design principles](https://learn.microsoft.com/en-us/windows/apps/design/design-principles)
  establish effortlessness, calm, adaptation, familiarity and coherence; this proposal
  applies them through task hierarchy, native controls and explicit compact behavior.
- [Fluent principles](https://fluent2.microsoft.design/design-principles) establish
  platform familiarity, focus and inclusion; the user's product identity takes
  precedence over imitation of Microsoft branding.
- [SelectorBar guidance](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/selector-bar)
  supports a limited set of local views and explicitly does not promise adaptive item
  rearrangement; this proposal supplies a native compact alternative.
- [Dialog guidance](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/dialogs-and-flyouts/dialogs)
  supports concise task titles, explicit action labels, inline validation and native
  footer buttons with safe dismissal.
- [Window breakpoints](https://learn.microsoft.com/en-us/windows/apps/design/layout/screen-sizes-and-breakpoints-for-responsive-design)
  and [accessibility overview](https://learn.microsoft.com/en-us/windows/apps/design/accessibility/accessibility-overview)
  ground adaptation in available window space and inclusion across inputs/preferences.
- The requested WinUI design, WinUI app and UI/UX Pro Max skills were read. The optional
  UI/UX Pro Max database command could not spawn its Python runtime; no database result
  is claimed. Native Microsoft guidance and the existing product authorities govern.

## Accepted amendment — native navigation and stable Add form

Accepted by the coordinator after independent design cross-review, 2026-09-13.
This amendment supersedes the paired SelectorBar/ComboBox navigation for Preferences
and the compact Files subview for Add. Implementation, pixels and live usability
remain unverified; desktop interaction remains stopped.

Preferences uses one stock top NavigationView with the same five text categories,
no settings item, back button, Frame or navigation stack. Native overflow owns
adaptation instead of hiding and replacing the focused navigation control. Set
SelectionFollowsFocus to Enabled so Left/Right continues selecting categories.
Alt+1–5 selects the same category and focuses its first useful field. F6/Shift+F6
cycles navigation, active content and native footer; navigation focus goes to the
selected visible native item, or the native overflow host when that item is in
overflow, never a collapsed or closed-flyout item. Selection and drafts retain one
owner. Grouped cards use CardBackgroundFillColorDefaultBrush and
CardStrokeColorDefaultBrush rather than a Mica Alt window-layer brush.
The accepted groups, form rows, staged CheckBoxes, local pickers, Copy shortcuts,
scope and disconnected facts, validation, partial-save recovery, measured native
dialog width, sole native body overflow and fixed Save/Cancel footer remain unchanged.
This follows [NavigationView guidance](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/navigationview);
five SelectorBar items are supported, so the reason is native adaptive ownership,
not an unsupported item count.

Add retains one canonical form and wanted-files ListView. Known files always appear
in that list; there is no Files drilldown, content reparenting or Add-to-OK footer
change. The native footer remains Add/Cancel; Escape cancels, Alt+F focuses the same
list and Ctrl+F its search. The list always has a finite height derived from the
actual native dialog viewport after visible source, destination and tools. Its
minimum accommodates one complete realized row plus actual list viewport chrome;
an existing verified native item-height resource is only the initial floor before
realization. Recompute after realization, source replacement and text-size pressure.
At short heights, the native dialog body scroll exposes the form controls while the
bounded native list scrolls its collection. Preserve native scroll chaining and
focus visibility; add no app ScrollViewer, new window, template or second draft owner.
Metadata, work, selection, destination and uncertainty behavior remain unchanged.

The accepted Add scrolling behavior is a design requirement, not a claim inferred
from a template ScrollViewer. Before relying on it, establish the native constrained
ContentDialog behavior; a stock viewport whose scrolling remains Disabled does not
satisfy the short-height requirement.

The inspector uses the same stock top NavigationView for its six existing views,
SelectionFollowsFocus Enabled, native overflow and visible-item/overflow F6 focus.
Alt+1–6 and Ctrl+Tab use its existing SelectView and draft owner; add no Frame,
back stack or second selection state. Files commands become an inline native
CommandBar with labels on the right, existing Lucide icons in native icon slots,
and native dynamic overflow. Wrap General rates and provide the file search's
visible Header. Returning to a General draft expands Limits before focusing an
enabled field; offline return focuses the enabled native Limits Expander header.
The inspector minimum includes actual visible fixed chrome/footer, generic table
header and one realized visible collection row where present. Reuse the shell's
existing VisualHeight traversal, excluding collapsed subtrees; empty views use
their actual factual content, without invented row metrics or new metric caches.
The table contract, Pieces appearance, engine capabilities and work lifetimes remain
unchanged. This bounded inspector amendment passed independent cross-review;
live focus, scrolling, visual conformance and usability remain unverified.

## Accepted amendment — explicit dialog body scrolling

The coordinator verified that the installed SDK's ContentDialog template declares
ContentScrollViewer with VerticalScrollBarVisibility Disabled, with no setter or
ContentDialog implementation code enabling it. This supersedes the earlier
assumption that the native dialog automatically scrolls a constrained form body.

Preferences and the shared Add form use one stock ScrollViewer directly as
ContentDialog.Content, wrapping the existing form root. Set vertical scrolling to
Auto, horizontal scrolling to Disabled and IsTabStop to false. The native dialog
content slot constrains that direct root; do not put it inside an unconstrained
StackPanel, add guessed heights, change the template or manipulate native parts.
The installed disabled native viewport supplies the layout constraint rather than
a second scrolling owner. Native footer commands remain pinned and unchanged.
Add's finite wanted-files ListView continues to scroll its separate collection;
body scrolling exposes source, destination and tools at short heights. Preserve
native focus visibility and scroll chaining. Actual finite bounds, keyboard focus,
scroll chaining and pixels remain unverified while desktop interaction is stopped.

## Accepted amendment — Connections cancellation recovery

The coordinator accepted this bounded recovery amendment after independent review.
Retain the existing Connections controls, profile store and Connect transaction.
Await the management dialog before showing delete confirmation. Canceling that
confirmation reopens the same management dialog with its selected profile and
unsaved name, RPC address, username and password intact; do not reset selection,
rebuild handlers, write the store or touch credentials on that continuation.
Restore focus to the originating native Delete footer button from Opened, after
native initial focus, using one local return-focus value. Normal opening retains
native initial focus. Keep the shell's existing modal gate throughout the loop.
Confirmed deletion retains its existing explicit confirmation and error handling.

Management content uses a direct stock ScrollViewer with vertical Auto, horizontal
Disabled and IsTabStop false, constrained by the native ContentDialog content slot.
Connect remains the single action for both the native primary button and Ctrl+S.
One shared Dialogs.HasPopup guard checks popup priority before handling shortcuts;
ignore closed popups and ToolTips, exclude the popup containing this dialog's
content, and let child ComboBoxes and flyouts retain priority. Replace equivalent
Form and Preferences guards through that same owner rather than duplicating scans.
Validation states factual requirements: name is required, RPC address must be a
complete HTTP or HTTPS address, credentials are not allowed in the RPC address,
and password is required for the specified username. Existing tooltips retain
guidance. No new model, window, persistence authority or TLS scope is introduced.
Repeat-show focus, scrolling and keyboard behavior remain runtime-unverified.

## Accepted amendment — inspector recovery and Pieces theme invalidation

Session's existing inspector selection owns detail failure. A read-only failure
projection updates before the existing detail notification, including failure while
the detail is already null. A contextual unavailable summary and native Retry button
remain independent of command feedback. Retry and F5 clear only the live selected
view's failure, respect native popup priority and committed writes, and let the
existing tick read again; no immediate RPC, separate poller or focus jump is added.
General, Files, Peers, Trackers and Pieces show factual unavailable content rather
than indefinite loading or false zero results. Existing unsaved General/Trackers
drafts retain their identity. The current-life non-Live transition clears expensive
detail and file metadata while retaining the selected hash and view for reconnect.

Pieces keeps its approved map appearance and native ThemeResource brushes. During
its existing active-detail lifetime, brush replacement and actual brush Color
changes invalidate the raster; contrast mode changes invalidate its hatch/outline
geometry. One native contrast event marshals to the UI thread before changing the
existing revision. Revision-guarded queued redraws coalesce updates without a
pending flag, timer, palette cache or new rendering owner. Release revokes brush
callbacks and the contrast event, clears the event source and invalidates queued
and in-flight work. Hidden or disconnected views retain no map or theme observer.
The inspector/recovery amendment passed independent user, UX, Fluent and keyboard
review. Headless regression coverage is required; live theme, focus and recovery
behavior remain unverified until coordinator validation.

## Accepted correction — factual validation wording

Independent literal review accepted four remaining procedural-error corrections.
Add requires a magnet link, HTTP URL, or HTTPS URL. Preferences numeric errors
state the field's required number kind and actual minimum/maximum; inspector
numeric errors state the number kind and minimum. Tracker validation states the
accepted HTTP, HTTPS or UDP schemes. Preserve the existing validation predicates,
focus recovery and tooltip guidance. This changes only error wording and adds no
controls, state or workflow.

## Screen review — 2026-09-13, design accepted for implementation

The owner requested another complete Fluent design review. This review carries
forward the accepted native-control and recovery amendments. Independent user /
keyboard and Fluent / UI/UX reviewers passed the revised seven corrections after
requiring exact shortcut modifiers and bounded confirmation titles. The coordinator
released the implementation freeze for these corrections only.
Actual interface acceptance is still stopped, and no source review replaces it.

Both design search executables were executed successfully: the bundled
`winui-my-design/scripts/winui-search.exe` returned three control searches and
their samples; WinApp CLI 0.6.1 returned Gallery search/scenario results. `winapp`
now resolves after refreshing the user PATH. Sample snippets inform control
selection; installed WinUI behavior and the project's table contract still govern.

| Screen | Controls, composition and navigation to retain | States and keyboard review |
|---|---|---|
| Window / torrent list | Native TitleBar, Mica, adaptive NavigationView, CommandBar and existing Synapse table; selected torrent remains the context for Details | Preserve search, selection, empty/filter/no-connection states and Windows editor keys. Repair navigation focus when the native pane is closed; short-width header and status fit require live checks. |
| Add | One ContentDialog form, source Browse/Paste, labeled link, bounded wanted-files ListView, local picker or remote folder, native Add/Cancel | Preserve reading/empty/invalid/duplicate/uncertain states. F6 must reach an enabled footer and child popups keep keyboard priority. |
| Preferences — General | Download folders, adding behavior, this-computer settings; staged native CheckBoxes, local pickers/remote editors | Alt+1 and direct field keys; preserve native CheckBox invocation and all staged drafts. |
| Preferences — Connection | Port and encryption, discovery; native number and choice inputs | Alt+2; port test remains tied to saved configuration. Test running/error/offline states remain in the existing owner. |
| Preferences — Bandwidth | Main/alternative limits and schedule; NumberBoxes, TimePickers, native weekday choices | Alt+3; disabled dependencies and arrow navigation within weekdays. Scaled field widths need live verification. |
| Preferences — Queueing | Active downloads/seeds, stall threshold and seeding limits | Alt+4; numeric constraints, enabled dependencies and save rejection preserve edits. |
| Preferences — Advanced | Supported blocklist and behavioral settings, explicit Update | Alt+5; preserve running/error/draft states without adding protocol fields. |
| Connections | Saved-profile ComboBox, labeled identity/address/credentials, native Connect/Delete/Cancel | Delete cancellation already preserves drafts. Switching a dirty profile needs explicit discard confirmation and predictable focus return. |
| General details | Progress/status first; location; disclosed limits, transfer and metadata; native scroller and pinned edit commands | Keep known hash available. Path/magnet copy must depend on matching current data. Retain dirty edits during offline/read failures. |
| Files | TreeView plus native CommandBar; selection facts, search, Download/Skip/Priority/Rename | Preserve original file identity and selection. Rename rejection must keep the name editor and draft; menus keep keyboard priority. |
| Peers | Shared Synapse table and selected-peer facts | Retain endpoint identity/copy and loading/empty/unavailable states. Keyboard table and long-address fit need live testing. |
| Trackers | Shared Synapse table, tier/backup facts, explicit multiline editor | Current queued/active announce state takes precedence over contact history. Reannounce owns one pending command; draft and popup routes remain native. |
| Speed | Two distinguishable traces with current/peak rates and actual time range | Preserve time gaps, missing-history state and release when hidden. Live contrast/overlap/minimum-height checks remain required. |
| Pieces | Approved bitmap map, one keyboard focus group, exact piece-range/composition facts, native theme brushes | Keep known count and nominal piece size when the map cannot fit. Do not report an invented final-piece byte total. Clear obsolete drawing and focus facts on size loss. |
| Location | Identity, local picker or remote editor, Move files and explicit space check | Different source folders must be shown as mixed, with no implicit destination. Save requires an explicit absolute destination. |
| Labels | Identity and multiline editor; Enter remains a newline | Preserve the accepted shared-label replacement policy. Mixed sets are factual state and the commit says Replace, so replacing individual sets is explicit. |
| Rename | Native name editor and Rename/Cancel, through the existing Form transaction | No-op/invalid names cannot commit. Reading, rejection, disconnect and uncertain completion retain the name and use the existing session/form ownership. |
| Removal / unsaved confirmations | Native ContentDialog, identity/consequence, Cancel default for destructive decisions | Long identity/error content must scroll inside the native content slot; no extra modal, custom template or guessed fixed height. |

### Proposed corrections and exact behavior

1. **Native navigation and popup priority.** Keep one FocusNavigation implementation.
   For the shell's minimal closed pane, Alt+N/F6 opens the native pane and focuses
   its selected entry after native layout. Top navigation retains its visible-entry
   or native-overflow behavior. Use the existing Dialogs.HasPopup at Add and
   Inspector shortcut entry, before handling keys. Existing text editors and native
   ComboBoxes/flyouts keep their expected keys. Form footer focus selects the
   enabled native primary command, otherwise native Cancel/Close; it never consumes
   F6 while repeatedly attempting to focus a disabled primary. Preferences staged
   CheckBoxes use native access-key invocation; custom focus remains for editors
   and the existing weekday group entry. Existing shortcut handlers require the
   exact intended modifiers: Ctrl commands reject Alt/AltGr and unintended Shift;
   Alt commands reject Ctrl and unintended Shift. Ctrl+Tab and F6 deliberately
   support Shift for reverse navigation. Plain F2/Enter/Escape and arrow commands
   do not consume modified editor combinations. Preserve explicit existing
   multi-modifier commands, using their exact chord rather than individual bits.

2. **Rename retention.** Reuse Dialogs.Form for the rename transaction, including
   its native footer deferral, working/read-only/uncertain behavior and session
   observation. Capture the existing torrent hash and original file path. The
   existing name editor remains on a normal RPC rejection, with factual error and
   selected text/focus available for correction. A successful commit closes once;
   a no-op or invalid leaf name cannot submit. On an uncertain rename, the Session
   Request boundary also invalidates its existing file catalog and requests its
   existing full refresh, just as confirmed rename invalidates that catalog.
   Never resend the rename automatically. No second commit/retry owner.

3. **Mixed values.** Location loads all selected folder values. Equal values
   prefill the destination; differing values show `Multiple folders`, leave the
   destination unset, and disable Save until the user chooses/types a destination.
   Canceling a picker changes nothing. Labels retain common-label initialization
   and whole-set replacement; differing sets show `Different labels`, with native
   primary `Replace` and the existing tooltip describing scope. Ctrl+S uses that
   same commit. No hidden write or extra mode/selection state.

4. **Connections draft switching.** Compare the visible fields to the selected
   profile, with a nonempty password treated as an edit. A switch with no edits
   changes normally. A dirty switch restores the previous ComboBox selection and
   fields, then closes the management dialog and uses its existing sequential loop
   for `Discard changes?` with Discard/Cancel, default Cancel. Discard loads the
   requested profile; Cancel reopens the unchanged draft with focus at the profile
   selector. Neither continuation writes storage or credentials. No overlapping
   ContentDialogs, autosave, second store or accumulated event handlers.

5. **Inspector availability and tracker state.** Derive Copy path/magnet from
   matching available facts; known hash remains copyable. Reannounce uses the
   existing immediate-command commit lifetime so ticks cannot re-enable it before
   completion. Current Active/Queued announce states display `Announcing`/`Queued`
   even before the first contact; historical last-contact facts may still say
   `Not contacted`. Add no timer, request loop or second pending flag.

6. **Pieces at limited size.** Separate missing detail from insufficient drawing
   dimensions. At insufficient size, release the old raster/overlay/range facts,
   stop flashes and retain only known count and nominal piece size in the summary.
   Use the approved block geometry to decide if drawing fits. Keyboard map facts
   are unavailable until a real layout exists. Omit block byte count because the
   projection lacks total size and the final piece can be shorter; retain exact
   range and state counts. Keep appearance and active theme-observer lifetime.

7. **Native confirmation scrolling.** Wrap potentially long confirmation/error
   content in a direct stock ScrollViewer with vertical Auto, horizontal Disabled
   and no extra Tab stop, leaving native footer and modal ownership intact. Local
   Location's path and Change/Copy commands occupy separate rows so the controls
   cannot consume the path's entire width. Dialog titles stay short and fixed:
   `Delete connection?` replaces a title containing an unrestricted profile name.
   Full identity and the necessary consequence move into the scrolling body.
   Other confirmations likewise keep unrestricted identities out of title chrome.
   Add no shared layout framework.

The following remain observation gates, not reasons to invent a redesign: full
shell/header/status fit, native dialog layout, Preferences editor sizing after
text-scale changes, all button icon dimensions, and keyboard/contrast behavior.
Preserve the owner-defined torrent table. Replacing it with a sample ListView,
forcing CheckBoxes into immediate ToggleSwitch semantics, adding a settings
dependency, or moving help onto the surface would violate the product contract.

## Consistency, hierarchy, density and task flow — accepted refinement

The owner requested `ui-ux-pro-max` with native Windows conventions taking
precedence. Its installed search ran successfully against `ux-guidelines.csv`
and `stacks/winui.csv`; the relevant quick-reference sections were also read.
This is a refinement of the existing product, not a second design system.

| Quality | Rule for this app | Consequence |
|---|---|---|
| Consistency | Use the existing native controls, platform type roles, semantic brushes and Lucide command owner. The same fact and action retain the same vocabulary and placement. | Keep the native navigation, staged settings, shared form footer and table contract. Give local folder identity the same full-width treatment in Add and Location. |
| Hierarchy | Present task identity and current state before choices, then supporting diagnostics. Use existing disclosure for less frequent settings; keep the commit in the native footer. | Preserve the Preferences groups and inspector sections. Keep source, wanted files and destination in Add's current order, with a direct labeled source editor. |
| Density | Remove repeated facts and stale feedback, not needed information or native hit targets. Unknown is different from zero, empty and successful. | Say Unknown for an unidentified peer; report an uncontacted scrape once. Preserve the full endpoint, timestamps and actual results when available. |
| Task flow | Reach the next useful input directly; preserve an accepted draft until its replacement is valid. Errors belong to the draft that produced them. | Empty Add opens at link entry. Failed Browse keeps the previous source and file choices. Changing a connection clears only the previous profile's feedback. |

The skill's generic web/mobile suggestions do not override Windows or the owner:
no mobile size/font/breakpoint rules, new palette, breadcrumb or page back stack
for flat inspector sections, surface helper text, autosave, or added dependency.
No new timer, persistence owner, rendering framework or retained data cache is
needed. Reading a replacement torrent temporarily holds its bytes alongside the
current draft, only until success or failure; preserving the draft requires that
bounded overlap. No persistent second source is introduced.

Independent user/keyboard and Fluent/UIUX/necessary-data reviews passed all five
changes before implementation. The coordinator released the implementation freeze.
Initial focus must occur after Form.Work restores enabled fields; replacement
acceptance must check cancellation before publishing. Accepted changes:

1. **Direct source entry.** Show the existing labeled link editor when Add has no
   source. Focus it after initialization; native typing and Ctrl+V require no
   preliminary Paste action. Keep Browse/Paste and their current shortcuts.
   A successfully read file hides the link editor. F6's source group targets the
   visible link editor, otherwise Browse. The existing form scroller and footer
   remain, with no extra step or control. Tradeoff: the empty form gains the
   existing input row, which earns its space by accepting the source directly.
2. **Safe source replacement.** Read and parse a chosen file into local values
   under the existing Form.Work feedback. Only after success replace the source,
   wanted set and file display. Cancellation or read/parse failure leaves the
   previous source, search, wanted choices and destination intact; the existing
   error identifies the failed replacement. The old identity stays visible while
   native working feedback disables editing. Do not show a candidate filename as
   though it were the accepted source.
3. **Readable destination.** Put Add's existing local path display on its own
   full-width row, followed by the existing Browse/Change and Copy commands, as in
   Location. Preserve the full path tooltip, actual folder picker and remote
   editor. No new tab stop or help text; the native body scrolls at short heights.
4. **Feedback ownership.** Clear the existing Connections status when LoadProfile
   actually loads another profile. A canceled dirty switch keeps both the draft
   and its feedback. No clearing during the temporary ComboBox restoration, and
   no storage or credential writes are added.
5. **Necessary inspector facts.** Project an empty/whitespace peer client as
   Unknown. Last scrape shows Not contacted once before any contact; after contact
   it retains its timestamp and appends a result only when nonempty. Preserve
   announce/returned-peer facts and every underlying protocol value. Leave the
   existing unavailable rows unchanged until a live review can establish whether
   removing them would leave an ambiguous empty body.

Review must challenge keyboard entry/return, source failure/cancellation, saved
profile versus draft identity, missing diagnostic facts, and the short-height
cost of the two layout changes. Desktop visual and keyboard acceptance is still
stopped; a design or source pass cannot close those observation gates.

The scoped implementation passed independent source review and the coordinator's
Release build with zero warnings/errors. No additional control, dependency or
persisted state was introduced. Concrete live acceptance cases and the verified
candidate manifest are recorded in `delivery-validation.md`; none was executed
through the stopped desktop interface.

## WinUI code review — design of corrections

The requested `winui-code-review` design challenge passed independently for the
shell/inspector and Add/Preferences/dialogs. The main proposal's obsolete Add and
navigation wording was brought into agreement with the accepted refinements above.
The subsequent implementation review found six concrete mismatches. Independent
user/keyboard/ownership and Fluent/binding reviews passed the six corrections below;
the coordinator released their implementation freeze. A dependent read skips only
an explicitly false local value; unset local values remain subject to validation.
Credential restoration runs only after an actual credential mutation.

1. **Inactive Preferences drafts.** A disabled dependent editor must neither block
   Save nor submit an inactive draft. The existing Link operation owns its local
   IsEnabled value. Read that local value when collecting Text/Number/Time drafts;
   effective IsEnabled is unsuitable because Save disables the entire view. Use
   the existing schedule checkbox for its weekday group. Preserve draft text when
   toggling off/on, keep unrelated fields validated, and retain existing confirmation
   callbacks only for values actually sent. No second dependency registry.
2. **Preferences read failure.** Put the existing initial-error body in a direct
   stock ScrollViewer with vertical Auto, horizontal Disabled and no Tab stop,
   using the existing retry and modal lifetime. The native footer stays reachable.
3. **Connection save failure.** Keep one provisional new-profile ID for the dialog.
   Before changing a credential, read the old secret if the existing profile owns
   one. Apply the credential change and existing atomic JSON write in the current
   Connect owner. If JSON saving fails, restore the prior credential or delete the
   candidate credential; do not report success. If restoration also fails, report
   that partial failure explicitly. Repeated attempts use the same provisional ID,
   so failed cleanup cannot create a new orphan on every retry. Never persist a
   backup password or replace the profile/store/credential owners. This compensates
   synchronous save failure; two different stores cannot promise crash atomicity.
4. **Live selected facts.** Update peer/tracker footer facts after replacing their
   source as well as after a logical selection change, using the table's reconciled
   current item. Share each footer projection between those paths. Do not force a
   selection event or duplicate table state just to update text.
5. **Stalled seeders.** Append current stalled diagnostics to the uploaded total,
   ratio and effective stopping rule when both states apply. Keep both sets of
   necessary facts and retain the existing disclosed transfer details.
6. **Files automation identity.** Name the native TreeViewItem with its relative
   path. Remove the changing full-report name from its inner Grid; retain the
   individual visible values and selected-file report. No per-tick row rename or
   new automation peer.

The installed Microsoft analyzer also reported an implicit OneTime binding for
the splitter's fixed Shell.RowSpacing and two migration hints for Pieces'
HighContrastChanged subscription. Make the fixed binding mode explicit. The
subscription has a verified source-level active/release lifetime; a migration hint
alone does not establish a defect. The newer ThemeSettings API exists in the
installed SDK, but no new theme owner is justified solely to silence an advisory.
Native peer/tracker row-name fallback, application accent contrast, requested-theme
resource lookup, Narrator, dynamic text scaling and actual UI latency remain
observation gates. They cannot be declared passed through this source review.

All six implementations passed independent source cross-review after this design
gate. The final Release stage built successfully with the installed analyzer:
zero errors and the two recorded WUI1001 migration advisories. No automated desktop
interaction or storage fault injection was performed. See `delivery-validation.md`
for the findings, exact evidence and remaining acceptance cases.

## Remaining analyzer fixes — accepted lifecycle

The owner's request to apply fixes includes the two remaining Pieces migration
advisories. Replace the one AccessibilitySettings observer with ThemeSettings;
the installed SDK already supplies it. Microsoft documents creation from
[XamlRoot's window ID](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.system.themesettings.createforwindowid)
on the owning window's thread, and the Changed event follows that window's lifetime.

Create the observer in the existing Draw path only with active detail, IsLoaded,
a ContentIslandEnvironment and a nonzero AppWindowId. Loaded calls that same Draw
path so a pre-load Update can retain its sole detail payload and wait for a valid
window. Until the observer exists, the existing unavailable-map branch clears any
old raster and exposes only known metadata; never invent a non-High-Contrast state.
Brush callbacks attach when the observer is created. A brush replacement before
creation needs no subscription because the first draw reads the current brushes.
Changed keeps the existing UI-queued sender-identity/revision guards. Release
unsubscribes and drops the observer, brush callbacks and rendering state. Preserve
UISettings for the separate animation preference. No extra owner, fallback event,
window-ID field, dependency, timer, or change to the approved map appearance.
Independent user/keyboard and Fluent/lifecycle reviewers passed this design before
the coordinator released its implementation freeze. Live theme validation remains
pending desktop control.
The implementation passed independent source review and the analyzer-enabled
Release build with zero warnings/errors. The same candidate includes the six prior
review corrections. No desktop theme transition or lifecycle test was performed;
the corrected behavior remains unvalidated through the interface.
