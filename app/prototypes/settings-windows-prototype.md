# TinyTorrent as Windows 11 Settings

Open [the prototype](http://127.0.0.1:8768/app/prototypes/settings-windows-prototype.html?variant=B&page=schedule).
This is a throwaway HTML exploration with sample data held in memory. Reloading
resets it. It does not change the engine or production WinUI screens.

The owner requested the full Windows 11 Settings composition, with TinyTorrent's
own SVG branding and no second search box. The application's title-bar search
remains the search entry point. The bottom prototype toolbar compares A (category
pages), B (the same groups expanded for review), and C (Home as the entry page).
All variants share one category hierarchy, with Schedule immediately after
Speed limits.

## Design authority and native composition

Microsoft's [app settings guidance](https://learn.microsoft.com/en-us/windows/apps/design/app-settings/guidelines-for-app-settings)
and [Fluent content guidance](https://fluent2.microsoft.design/content-design)
inform the hierarchy, immediate ordinary changes, concise labels and contextual
help. The supplied Windows screenshots inform the dark surfaces and grouping.
The grouping of TinyTorrent's settings is a design judgment, not a published
Microsoft algorithm.

The intended native implementation uses the stock WinUI 3
[Expander](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/expander)
without replacing its control template. Its Header contains ordinary icon and
text layouts; its Content contains ordinary settings controls. No independent
action is placed inside the expander header. The HTML details element is a browser
approximation, not an actual WinUI component.

Opening an expander preserves its header height, padding and title position.
The collapsed summary hides without removing its layout space. In native WinUI,
reserve that space within the Header layout, not by fixing the whole Expander's
height: its Content still needs to expand naturally.

The Notifications header keeps its explanation visible in both states:
“Alerts can appear while TinyTorrent runs in the background.” It does not repeat
that sentence inside the expanded content or imply that alerts continue after
the application exits.

The Speed limits expander uses the same content surface and separator tokens as
other expanders, including Adding torrents. Subtle lines separate profile rows
and Connection setup; nested card outlines remain suppressed.

Windows integration keeps its controls on the first row. Associations, warnings
and paths span the available width beneath them. Each text line trims with an
ellipsis instead of wrapping; tooltips expose the full warnings and paths, and
the existing accessible descriptions still associate them with their controls.

NavigationView, ToggleSwitch, ComboBox, TextBox, CheckBox, radio buttons and
ContentDialog supply their standard roles. Period editors map to TimePicker with
a 24-hour clock. Start, End and Remove occupy the same content row, with matching
control heights and bottom edges. Start and End labels sit beside their controls,
with each label/control pair wrapping as a unit if space is constrained.
Commands throughout the pages and dialogs have leading Lucide icons. Remove
keeps its one-word label and explanatory tooltip. Toggle switches, theme choices
and the breadcrumb retain their standard control presentation.

Toolkit examples are references only; this design requires no Toolkit package
or custom expander implementation. Header icons mark subjects; sibling settings
inside a group share the same text edge. Values and actions use consistent
columns within each group. The subtitle group reserves one action column for
Account, Edit, Check and batch operations. Expanded content ends at the ordinary
16-pixel control inset; it does not reserve the header chevron's space.

Network right-aligns its control groups to the ordinary card inset, with no
empty action column beside standalone selectors or numeric fields. Selectors
and numeric control groups share the editor width; numeric units occupy a
consistent trailing slot. Proxy status and Edit stay together at the same right
edge. Showing Advanced adds settings without changing these alignment rules,
so the basic page remains coherent on its own.

Transfers follows the same right inset for Queue, Seeding limits, and Deleting
files. An action column exists only where a row contains an action such as
Browse; standalone selectors and numeric fields do not reserve it. Transfers
and Network share the trailing slot for numeric unit text. Duration selectors
remain part of their numeric control group.

Setting labels, descriptions and summaries remain on one line and trim with an
ellipsis at compact widths. Their full text remains available in tooltips and
the existing accessible descriptions. Keyboard focus survives shared rendering;
opening a schedule period focuses its header, and removing it returns focus to
Add period. Light-theme sidebar icon colours retain readable contrast. Theme
tiles allow system contrast colours, and expander chevrons respect reduced motion.

## Settings spacing

Settings uses one shared spacing scale so equivalent groups keep the same
rhythm across pages. These are composition values; native controls retain their
own internal metrics.

| Relationship | Spacing |
| --- | --- |
| Major sections or distinct tasks | 24 px |
| Independent cards, horizontally or vertically | 8 px |
| Section heading to its content | 8 px |
| Horizontal card content padding | 16 px |
| Rows within a card | No external gap; shared padding and separators |

Single-line setting rows remain 52 px high with 10 px vertical padding. Rows
with secondary information use 12 px vertical padding and a 64 px minimum.
Expander headers retain their 68 px minimum and reserve their summary space
when open. Icon columns and dependent-field insets remain alignment offsets,
not additional card padding. Home cards, theme tiles, paired speed profiles and
Connection setup use the same card gap. The weekly scheduler is a distinct task
and follows the major-section gap.

## Category mapping

| Category | Subjects |
| --- | --- |
| General | Windows integration, startup and closing, notifications, power, updates |
| Transfers | Folders, adding torrents, queue, seeding, watched folder, file selection, deletion |
| Speed limits | Current mode, standard and alternative caps, bandwidth accounting |
| Schedule | Current behavior, weekly map, shared cap presets, editable periods |
| Appearance | Theme, language, window and status bar |
| Network | Adapter, proxy, encryption, peer counts and transport, discovery, incoming connections |
| Library & subtitles | Providers, credentials, languages, automatic and batch acquisition, file scan, search limits, statistics |
| Advanced | Resource limits, checking, refresh and history |
| Home | Common choices and links into the same categories |

Small related groups disclose in place. Peer connections belong to Network;
protocol overhead and local-peer bandwidth accounting belong to Speed limits.
No first field doubles as a heading for its peers. Connection setup is the only
separate settings workflow: it presents capacity, a proposal, Apply and Cancel.
Proxy and supplier credentials use transactional dialogs; supplier selection
itself is immediate.

The native Proxy dialog and shared Dialog/CheckStatus controls remain the
authority for the editor. The prototype mirrors their footer: Check and its
one-line, trimmed result at the left, equally sized Save and Cancel at the
right. Status changes reserve their space; validation appears beside the field
label, without inserting rows. None and SOCKS4 disable unused fields in place.
Proxy-type and sign-in explanations belong to tooltips. The prototype's check
is simulated and makes no connection. Do not replace the native implementation
with the browser editor or duplicate its validation and draft logic.

Hierarchy review covered every category with advanced settings shown as well as
hidden. Independent rows share alignment and icon treatment under neutral group
headings. No expander header contains a master switch. Actual dependent rows
receive a 24-pixel text inset from the existing dependency metadata; their
controls retain the shared right alignment and disabled-state explanations.
This follows current [app settings guidance](https://learn.microsoft.com/en-us/windows/apps/design/app-settings/guidelines-for-app-settings)
and the peer/subordinate distinction in Microsoft's older
[desktop layout guidance](https://learn.microsoft.com/en-us/windows/win32/uxguide/vis-layout).
Browser checks confirmed that turning off torrent integration or Settings Home
leaves unrelated switches usable, while the incomplete-folder switch controls
only its dependent folder editor.

Links to a group, such as Queue, expand it, align its header near the top of the
scroll area, focus its settings block and outline the destination for
three seconds. All group links use this one navigation path. The outline is a
prototype design choice for orientation, not a claimed Windows requirement or
an Expander template replacement. It does not animate. Microsoft's
[initial-focus guidance](https://learn.microsoft.com/en-us/windows/apps/develop/input/keyboard-interactions#initial-focus)
supports placing focus at the logical destination.

Implementer note: follow the [navigation contract](../../docs/contracts/ui/workspace.md#native-controls-and-visual-authority)
for hierarchy and history. Cross-links select the destination category; only
Connection setup has a parent breadcrumb. The prototype's Back button currently
returns to a fixed parent or Home and does not demonstrate the required history
or scroll restoration. The native implementation must use the existing window
navigation owner for Back and restore the caller's scroll position.

## Reading Schedule from top to bottom

1. The Speed limits expander summarizes the active mode and speeds. It contains
   Limits to use, the shared profiles, and Connection setup.
2. The map header has the same four-option Limits to use selector, replacing the
   one-way Use weekly schedule action. It remains accessible with the expander
   closed and selects an explicit mode when leaving Weekly schedule. The map
   shows the resulting week, including standard gaps and pause precedence.
3. Periods specify when to use a profile or pause transfers.

Speed limits and Schedule use the same mode/status renderer and shared block of paired profiles
and the full-width Connection setup card. One 8 px spacing value controls both
the gap between profiles and the gap above Connection setup. Schedule wraps the mode, profiles and setup card in its existing expander pattern above
the timeline. They edit one settings
object through the same validation and commit path. Neither page keeps its own
speed or mode state. Schedule retains its selector, timeline and speed editors
in place.

Numeric fields use text boxes with a clear button, numeric keyboard hints and
the existing validation. Empty optional limits show Unlimited in the field;
neither page adds an instruction above the profiles.

Speed summaries share one renderer across both pages, Home and Connection setup,
including its Current/Proposed tables and slider captions. They use decimal
MB/s with up to two fractional digits and omit trailing zeros. The shared
editors convert the same stored values to MB/s; display rounding does not change
a stored limit until it is edited. Very small positive summaries say <0.01 MB/s
rather than displaying zero. Connection capacity stays in Mbps.

“Alternative limits” is the same concept in the selector, map, preset card and
period editor. Each period header leads with days, time range and duration,
which distinguish it from other periods. Its second line states the behavior
and, for alternative limits, the caps as “Alternative limits · ↓ 5 MB/s · ↑ 1 MB/s”. The caps are
derived from the shared preset, not copied into each period. The inline fields
reuse the Speed limits page's renderer, validation and state. Editing a preset
keeps the map and periods in place and updates their summaries immediately.
Two lines separate
when from what; they are not an arbitrary division by fact count.

The two period behaviors use radio buttons, so both choices stay visible.
Their group label sits beside the choices; the row wraps when space is limited.
A muted Days label introduces the weekday checkboxes on the same row.
Time is consistently displayed in 24-hour format. Overnight periods say
“next day.” Remove sits at the right of the Start/End row without an extra row.

## Behavioral coverage

The inventory and rules were checked against the current native Settings,
SettingsPage, Schedule, Week, PeriodSpan, MainViewModel limits, ConnectionSetup,
Proxy and subtitle settings/batch sources. Existing older prototypes were not
treated as authorities. The unbound Preallocate property is not presented as a
current user-facing setting.

The weekly model handles minute boundaries, overnight and Sunday wrap, full-day
periods, standard gaps, pause priority, duplicate/invalid periods and the next
effective change. Fixed modes show a uniform week while preserving saved periods.
Map and editor use the same period data. Pointer movement/resizing uses the
native quarter-hour adjustment rules; keyboard and typed minute edits are also
supported. Preview state exposes manual pause, a missing adapter, temporary
resume and a controlled clock for checking the displayed result.

Connection setup has one shared full-width navigation card immediately below
the limit editors on both pages. Both open the
same dedicated workflow. Related settings retains Schedule and Queue; setup
does not appear in the sidebar. Browser review verified both entry points.

Connection setup preserves independent standard/alternative proposals, unit
conversion, queue recommendations, cancellation and atomic Apply. Opening it
does not change settings. The additional pass restores live capacity validation,
optional empty capacities, native rounding, unchanged preset preservation and
queue changes only when the standard share moves. The simulated test follows
stopping, download, upload, a three-minute review hold and restoration. Cancel,
retest, expiry and departure release the hold. Pending Apply locks edits; a
failed save preserves the proposal for retry. Preview state exposes network,
timeout, restoration and save failures, plus engine disconnection. Test details
explain the native Cloudflare transfer and temporary pause. These are simulated
states, not evidence that native service behavior has been exercised.

Dependent fields follow their enabling conditions,
including proxy/incoming connections, folder options, seeding criteria and
limit-mode availability.

Supplier credentials follow provider-specific requirements. Language selection,
batch scope/matches, start/stop and subtitle file scanning have interactive sample
states. Ordinary settings apply in memory; compound drafts save or cancel together.

## Verification and limits

Focused JavaScript checks passed for weekly boundaries, overlap precedence,
fixed modes, pause/resume, period adjustment and connection proposals. Browser
checks covered map/editor synchronization, overnight minute edits, fixed/weekly
switching, moving a period by dragging, connection Apply, proxy dependencies,
subtitle credentials/languages/batch controls, and the category pages.
The later pass verified speed and mode edits across both pages in both
directions, including the resulting map and period summaries. It also checked
test progress, the review hold, failed Apply retaining its proposal and keyboard
Apply recovery in the browser simulation.
All nine pages passed the visible command-icon and horizontal-overflow audit.
MB/s edits round-tripped between Speed limits and Schedule without unit drift.
Queue navigation expanded and focused the group, positioned its header 17 pixels
below the scrolling viewport top, and cleared the outline after three seconds.

The final period editor's Start, End and Remove controls measure 32 pixels high
and share the same bottom edge. The browser shows the revised when/behavior
header, consistent preset terminology and scalable TinyTorrent branding.
No native build or native test suite ran.

External operations are simulated: capacity measurement, supplier requests,
registration, updates, folders and database/file operations do not touch the
system. The sample language catalogue is limited and the displayed copy remains
English. Native service errors, disconnections, accessibility and theme behavior
still require verification in the actual WinUI application. Browser rendering
cannot establish native text, picker, animation or high-contrast fidelity.

## Artwork

The owner requested an illustration for each settings page. Nine original
transparent blue glass illustrations now identify General, Transfers, Speed
limits, Schedule, Appearance, Network, Library & subtitles, Advanced and
Connection setup. One page-heading renderer owns placement. Home's illustration
was removed at the owner's request; the existing TinyTorrent SVG remains.

Home and Appearance share the generated light/dark theme images and the same
theme-choice renderer. System, Light and Dark occupy three equal responsive
columns with compact 21:9 previews and 8 px clipping and selection corners.
System combines the two existing images in a split preview and retains the
existing Use Windows setting value. The tiles replace the theme dropdown;
App theme labels the group, and Display language stays in its own row below.
One radio group supports arrow keys, Home and End, with one Tab stop and focus
retained through theme changes. Images use normal browser interpolation without
pixelated rendering. The previews change the app theme, not desktop wallpaper.
The live weekly map remains interactive code. Folder layout uses a text path
example derived from its selected option.

The shipped artwork lives in `../src/Assets/`. The generated masters and the exact
built-in imagegen prompts stay outside the repository, in
`C:\SynoSoftware\TinyTorrent2-artwork\settings-artwork\` (`README.md` and
`PROMPTS.md` there).

## Cumulative steer audit

- Windows 11 settings structure, spacing, sibling alignment, default-control
  intent and semantic grouping remain the design basis. Native adoption uses
  stock WinUI 3 Expander; browser details elements are only a prototype.
- No second search field. Existing TinyTorrent vector branding is preserved.
- Schedule follows Speed limits in navigation. Small groups expand in place;
  peer connections belong to Network, bandwidth accounting to Speed limits.
- Both speed pages share values, operating mode, formatting, validation and the
  effective-limits card. Schedule includes both inline speed editors.
- Rates use MB/s with at most two decimal places through the shared formatter.
  Capacity remains Mbps; very small positive rates are distinguished from zero.
- Weekly map/editor synchronization includes overnight and Sunday boundaries,
  overlap precedence, manual pauses, missing adapters and fixed modes.
- Period summaries separate when from behavior. Alternative summaries include
  shared caps. Two radios, inline Days and time labels, a 24-hour clock, duration
  in the header and same-row Remove remain in place. Commands use Lucide icons.
- Group navigation expands, scrolls, focuses and briefly outlines its target.
  This outline is a prototype design choice, not a Microsoft requirement.
- Connection setup preserves proposal review, capacity edits, test/cancel/retry,
  review hold, Apply failure recovery and restoration in its simulation.
- The copy pass adds scope, consequences and recovery where useful, removes
  repeated-label descriptions and shares count/seeding guidance across fields.
  Proxy restrictions, queue grace time and memory targets now read accurately.
  The watched-folder destination dialog now accepts an empty path, matching
  its normal-download-location fallback.
- Show advanced settings is restored as one shared visibility preference at
  the bottom of the left sidebar, below a divider. It follows native advanced
  classifications, retains values and returns from a hidden Advanced page.
  The earlier header position was explicitly replaced by this owner steer.
- Preallocate disk space is deliberately absent. The owner will prune its
  native implementation separately.
- Home's existing 76-pixel identity header now contains This session and All time
  transferred-byte totals. These are sample counters independent of torrent
  sizes. Activity counts and available storage use the existing Downloads card.
  Problems appear only when the sample state requires attention; the normal
  state has no problem banner or extra chart.

## Final review evidence

All category pages and Connection setup loaded their intended artwork. Home
and Appearance loaded both theme images, and Light/Dark changes applied through
the shared theme state. All nine categories passed the browser overflow and
description-reference checks at the review viewport.

The advanced switch hid and restored the intended groups across pages and
returned safely from Advanced. Preallocation was absent. Speed limits edited to
25.34 MB/s appeared on Schedule; an alternative upload edit of 2.56 MB/s on
Schedule appeared back on Speed limits. Connection setup used the same precision.
The focused JavaScript schedule and connection-proposal checks passed after
the source edits. The final Home header measured 76 pixels at desktop width.
The adapter-unavailable preview displayed the conditional Home problem message;
the normal state displayed none. The final sidebar switch was unobstructed by
the prototype toolbar. All retained theme images loaded without window overlays.
The artwork and copy detector reported only its existing dialog-padding and
prototype-toolbar-shadow findings; actual dialog bodies retain 24-pixel insets.

These are browser-prototype checks. No native compilation or native tests ran.
No production adoption is implied.

## Header composition review

Content is left-anchored with the existing 56-pixel desktop inset and a
1200-pixel maximum width. This prevents the sidebar gap from growing on wide
windows. Every page title uses the same top alignment, independently of its
illustration. Existing page artwork stays in place.

Home retains its 76-pixel desktop summary height and 76-pixel logo. Branding
anchors the left edge; session and lifetime totals form one group anchored to
the right edge of the cards, with 12-pixel labels and 20-pixel semibold values.
There is no divider or identity subtitle. Narrow windows move the statistics
group below the identity while retaining its right alignment.
The values still represent transferred bytes from the same sample activity data.
The sidebar advanced switch and single application search remain settled choices.

Browser review confirmed the 56-pixel inset and 1200-pixel content width at
1920 pixels, and inspected the reflow at 760 pixels. The seven ordinary category
pages shared the same title position, loaded their artwork, and had no horizontal
overflow at the default viewport. No native build or test run was needed.

## Subtitle card review

Preferences, Library subtitle search, and existing-file scanning are visible
sections on the same page. Labels, value controls, and command buttons keep
consistent columns; Find shares the manual match selector's row. Coverage is
status text above manual search. The additional-match selector shows None or
one to three extras, while retaining the underlying values zero through three.

Supplier remains the canonical subtitle term; provider names video-information
sources. Not searched remains scoped to recorded search history for each
language, rather than promising that a movie has never been searched. Manual
matching includes an exact-only policy as well as quantity choices, and its
quantity applies per movie and language. Scan describes torrent-folder scanning
and explicitly preserves files.

Browser checks verified that Find and Scan work with automatic downloads off,
automatic extras do not alter the manual match preference, and missing supplier
credentials disable downloads but leave scanning available. Find/Stop keeps
keyboard focus across refreshes, with scope and match selectors disabled during
the sample batch. These checks simulate operations; they do not access a supplier
or scan the user's files.

## Restored Windows integration and dialogs

General contains one collapsible Dialogs and confirmations group, with the native
screen's six controls: Settings Home, torrent Add dialog, magnet Add dialog,
bringing Add forward, delete confirmation, and exit confirmation. These rows
moved from their scattered locations; they still use the existing values and
dependency rule. None requires Show advanced settings.

Combo boxes use a shared editor width, including between expanders. Pages with
Browse, Edit or Account actions reserve a separate action column, so fields
above and below them keep the same edges. Number fields and their units occupy
one editor column. Narrower windows reduce the shared width and wrap controls
below labels when needed; Schedule's paired speed editors keep their compact
width. Browser review covered all categories at desktop and 760-pixel widths.
Expander state survives immediate setting changes, including in layout B.

Windows integration uses one sample registration state and one renderer on
General and Home. It shows separate torrent and magnet defaults. Another
TinyTorrent copy may count as On; the warning names that copy and Use this copy
moves the registration. Missing-program repair takes precedence, and a shield
identifies administrator approval. The switch never requests elevation.
Preview state on General or Home exposes mixed defaults, another copy, missing
programs, disabled startup, disconnected, success, failure, and approval-declined
cases. This is simulated UI, not Windows registry access or a real UAC prompt.

Subtitle languages uses an inline expander: follow the display language or
select languages from a searchable checklist. Each choice applies immediately;
there is no language dialog or Save/Cancel step. Entering custom mode retains
the effective display language. Removing the final explicit selection returns
to the display language. Native implementation should use the stock Expander,
RadioButtons, AutoSuggestBox and a multiple-selection ListView populated from
the supplier's actual language catalogue. The browser uses a small sample
catalogue. The remaining compound editors retain Save and Cancel.

Browser checks exercised other-copy replacement, mixed defaults, startup
disabled by Windows, disconnected controls, and declined repair approval.
Language checks covered immediate selection, filtering without losing selected
languages, display-language fallback, and narrow and desktop layouts. The Add
dialog switches correctly disable Bring to front only when both are off.
No native build, Windows settings change, or supplier request was performed.

## Settings copy and General order

General follows Windows integration with Startup and closing, Notifications,
Power, Dialogs and confirmations, then automatic updates. Startup stays beside
sign-in; daily behavior precedes less frequent prompt preferences. This is a
TinyTorrent ordering decision, not a sequence prescribed by Microsoft.

Collapsed groups show configured values from the existing settings owner:
queue counts, seeding limits, watched folder, peer discovery, listening port,
database size, and checking/history limits. Their summaries disappear when the
same values are visible in expanded controls. Home and related navigation reuse
the same summaries. No copy of settings or status is stored for presentation.

Descriptions remain for consequences, constraints, disabled reasons and
non-obvious behavior. Obvious control restatements and repeated selected values
are removed; single-line rows use compact, vertically centered spacing.
Connection setup retains its preview, capacity units, test status and recovery
messages; its introduction now states the test's temporary pause of torrents.
The preview uses sample activity and registration state, not live disk or
Windows association checks.

Browser review covered every category, Connection setup, expanded and collapsed
groups, and all categories at 760 pixels. Editing a seeding duration and unit
updated the group and Home together; clearing a queue count showed Unlimited.
Hidden Add dialogs produced accurate summaries, and
Bring to front remained disabled only when both Add dialogs were off. Sample
values were reset by reloading the prototype.

## Shared speed profiles

Speed limits and Schedule render Standard and Alternative limits through the
same paired editor. The cards sit side by side on wide pages and stack at
narrow widths. Empty optional values show Unlimited inside their text boxes;
profile headings have no subtitles. Both profiles remain editable in every operating
mode, including No limits. The mode determines which caps apply, independently
of editing saved values. Neither card has a description below it.

On both pages, Connection setup follows the profiles. Schedule retains its
timeline and wraps the shared speed controls in an expander. Both pages use the existing
settings state and change handlers.

Browser review verified edits in both directions, the shared operating mode,
one instruction per page, resolved input descriptions, and stacked alignment
without horizontal overflow at 760 pixels. No native build or tests were run.
Both profiles were also checked in all four modes on both pages. Editing an
inactive profile preserved the active caps; selecting it applied its saved values.

## Settings link destinations

Home shortcuts and related-settings links name a group, individual setting or
the weekly map rather than only a category. The shared navigation handler opens
the destination's group, reveals advanced settings when required, scrolls only
as far as needed and focuses the destination surface. One neutral outline uses
the normal 4 px card rounding and sits flush with the block edge. Section titles
keep their normal position and spacing above the cards, outside the outline.
Theme selection retains its accent border and checkmark, distinct from this
temporary navigation cue. The outline
lasts three seconds or until focus moves into a control, which then shows its
normal focus indicator. Sidebar category links
and the separate Connection setup workflow retain page-heading focus.

Browser review covered all ten Home shortcuts, both related-settings links,
Connection setup entry and return, and Display language at 760 pixels.

## Subtitle action feedback

The native `Subtitles/Batch.cs` keeps a status line present, and
`Subtitles/Settings.cs` pairs scan feedback with its action button. The
prototype follows those layouts: search progress replaces the ready count
under Movies to search; scan results occupy the value column beside Scan files.
Neither action inserts rows or grows its card. Status text stays on one line,
trims with an ellipsis when necessary, and exposes its full text through a
tooltip and an accessible status element. Static explanations can wrap normally.

Search scope and match changes clear stale completion text. Manual search and
scan remain independent of automatic downloads. Scan uses the native action's
FolderSearch icon and describes existing files, not subtitle downloads.
Counts and results remain sample data; no supplier request or disk scan occurs.

Browser review measured identical card bounds before and after scan and across
ready, searching, stopped and finished search states. It checked retained button
focus, locked selectors during search, stale-result clearing, and the 760-pixel
layout. No native build or tests were run.
