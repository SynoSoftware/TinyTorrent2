# Settings

The [shared UI policy](../ui.md), including [commit behavior](../ui.md#committing-edits),
applies to this contract. [Workspace navigation](workspace.md#native-controls-and-visual-authority)
owns history and Back; the engine owns [network and schedule semantics](../engine/settings.md#network-settings).

Group settings by user task and the actual libtorrent product, not the old
daemon's fields or fixed categories. Background choices have one engine owner;
Window-only settings have one WinUI operational owner; the existing settings
store persists their choices alongside theme. There are no remote profiles or
connected-server scopes in this local product.

**Owner ruling: TinyTorrent does not preallocate disk space.** The owner chose
not to offer this setting, so later work does not add it back. New downloads use
libtorrent's default sparse files.

**Owner ruling: the Settings page background is the window's acrylic with two
translucent gear marks.** The page sets no background of its own, so the
window's acrylic shows between and around the cards. Two large Lucide Cog glyphs
in the accent colour overhang the top-left and bottom-right corners behind the
content, at 4% opacity in the light theme and 3% in the dark theme, and hidden
in High Contrast. They take no input and screen readers skip them. This is the
owner's chosen look for the page, and nothing in the layout depends on it, so a
redesign that does not know about it deletes it. A change to the page keeps
both; only the owner removes them.

Use the [commit rules](../ui.md#committing-edits): ordinary settings apply individually,
with no page-wide Save step or confirmation on close. Keep dependent fields
visible and disable them while inapplicable, so changing a switch does not move
later rows. One Advanced mode switch reveals advanced rows and cards
across categories. Search includes those settings and reveals the chosen row.
Every search result, link or toast action that names a setting reveals it the
same way: the setting's card is outlined for three seconds and its row takes
focus, so the person sees where they arrived. One owner in the Settings page
draws that outline. A target that names only a page focuses the page heading.
**Owner ruling: Settings uses the title-bar search and has no search box of its
own.** The title-bar search already finds settings, including advanced settings;
a second box duplicates the task and takes a row away from the settings. Keep
the title-bar search's existing scope of torrents, commands and settings.
Settings uses the approved Windows Settings composition in
`app/prototypes/settings-windows-prototype.html`, variant C, and its accompanying notes.
Its sidebar contains Home, General, Transfers, Indexing, Speed limits, Schedule,
Appearance, Network, Plugins, and Advanced. Advanced appears while
Advanced mode is on. The sidebar's TinyTorrent logo and name form a
native button that returns directly to the workspace Settings was opened from.
The sidebar's footer items are Advanced mode and then the same return action,
last in the pane. Both are navigation items that act instead of opening a page,
so they render and respond like the categories. Advanced mode's icon is a
checked square for On and an empty square for Off, keeping the state visible
in the compact rail. The return label names Torrents, Library or Search
from the navigation owner's workspace, and both return actions use that workspace's existing command and departure
guards, so browsing Settings does not trap the person in its history.
The sidebar uses a compact icon rail at smaller widths
without a hamburger button; it does not set the window minimum
from translated category labels. Content is bounded at 1200 DIPs.
All categories
share one viewport-constrained, centred content column, so a change of
category cannot move the page or push its actions outside the viewport.

## Settings navigation

Indexing owns opt-in [torrent indexing](../indexing.md), after Transfers in the
sidebar. It uses the existing category header, artwork, facts, task sections and
settings rows for collection, storage, exclusions and maintenance. Search links
to this category. Only rule editing and explicit previews open task dialogs.
Collapsed sections retain their summaries through the shared Settings control:
indexed totals and collection state, storage and not-seen interval, active rules
and excluded count, and search-index maintenance state.
The original standalone settings dialog is retained in Git commit `763e82f`.
Clear acknowledges
collection Off before removal, so collection cannot immediately refill the
confirmed scope. Ordinary indexing fields retain the existing per-field commit
behavior; rule changes use their explicit preview and Apply.

**Owner ruling: every Settings navigation icon is 24 DIPs, including Advanced
mode and the return action.** The 16-DIP label's visible letters are vertically
centred against the icon; centring the font's line box leaves the text visibly
off-centre. The shared navigation text style owns tight line bounds and vertical
alignment. Labels are text elements in the native content slot and icons use
the native icon slot, so styling labels cannot hide the icons. Keep this
appearance in expanded and compact navigation and after live language or theme
changes.

Settings category icons use Microsoft's Fluent color SVGs at that
[Surface](../ui.md#icon-sizes) size, to make the categories easier to recognize. This is
the sidebar exception to Lucide iconography; High Contrast uses the existing
Lucide glyphs at the same size and native colors. Only the used assets and their
MIT license ship with the app.

**Owner ruling: both footer actions use neutral Lucide icons.** Advanced mode
uses checked and empty squares; the return action uses a plain left arrow.
These familiar symbols make the actions and their state immediately readable.
Both retain the shared 24-DIP size and inherit the native navigation foreground,
including in High Contrast. Their backgrounds are transparent.
Transfers uses paired up/down arrows on a transparent background, because its
settings cover both downloading and uploading.

## Category headers

**Owner ruling: Settings tab headers put the category title above a summary row
with artwork and two compact facts with Lucide icons at the end.** The artwork
stays visible on every tab. Summary text is optional: General, Appearance and
Advanced omit the generic product name and version, because they add no context
to those settings. Plugins summarises Library and its status.
The shared header component owns its artwork, title, subtitle, fact controls,
tooltips, accessibility and responsive layout. Pages supply text and values;
they do not assemble the header's visual tree. Removing summary text never
removes the artwork or selects a layout.
The header and fact visual trees, styles and layout states are declared in
XAML; their code-behind measures available space and selects the layout state.
Category states in page XAML own artwork, icons and the heading level. Titles,
captions and values bind to Editor's `Overview`, so controls do not have a
second copy of the model's display calculations, and a language change reaches
them while the tab stays shown.
Settings and Connection setup use this measured layout, so only available width
moves facts below the subject and then into separate rows by the same rules.
Facts describe the selected tab using its existing owners, so they
remain current without another query or stored summary. Captions and
values and the single-line page title keep their full text in tooltips. A fact
reads like a Windows Settings header fact: a Body Strong label above a Body
value, both using the 14-DIP `BodyTextBlockFontSize` token. Neutral values use
the secondary text brush rather than smaller type, so they stay readable.
The header's summary and fact pairs use `TinyTorrentTitleDetailSpacing` from
`App.xaml` between their lines, so their spacing changes in one place.
A Statistic icon sits at its native size in a shared square centred on both
lines. Tight text bounds align the text blocks by their visible capitals and
baselines, rather than the font's leading, so the Home title and its facts
share a visual centre. The fact's text column is constrained to its available
width, so long values trim after the facts have stacked. A fact that pairs
downloads with uploads, such as Home's session and all-time totals or the queue's
active counts, shows them with the download and upload arrows in their theme
colours; a fact with one
value needs no marker beyond its own icon. Transfers identifies its default
download folder above Download locations, and Queue limits names downloads,
seeds and the total beside their values so the limits are clear. Home uses one
Home title beside its logo and an activity subtitle.
Speed limits summarises Current limits; Schedule identifies whether it is active
and counts saved periods, so an inactive schedule does not imply those periods
are running. An inactive schedule has no subtitle, because its title and the
Current limits fact already say what applies. Advanced summarises torrent checking and speed history, whose
controls belong to that page. Network status names incoming connections rather
than implying the whole connection is unavailable.

Home provides curated shortcuts and the same theme, Windows integration and
automatic subtitle controls as their category pages. It shows facts supplied
by the native owners; prototype sample activity and totals are never production
data. The existing start-page preference controls whether Settings opens at
Home or General.

## Cards and categories

The dismissible Settings index card includes one concise benefit subtitle beneath
its heading, using the same secondary text style as the category cards. This
requested introductory text is an exception to the work-surface help-text rule.
Native add/remove transitions close the card and move the remaining categories
into place without a layout jump.

Use 24-DIP gaps between major tasks, 8-DIP gaps between sibling cards in either
direction, 8 DIPs from an external section heading to its card, and 16-DIP
horizontal card insets. Related rows share themed separators, no exterior row
gaps, a compact 52-DIP minimum or 64 DIPs with a description. Trailing controls
align to the actual right inset. Reserve an action column only where a group
has related actions. Numeric inputs use native TextBox, including its clear
button; optional limits use the Unlimited placeholder.

Cards and expanders retain their native card strokes, fills and control
rounding, so they remain quieter than the pane surfaces. Row dividers and
input borders also retain their native resources.

Use native WinUI Expanders with the shared [drawer animation](#category-motion).
Their headers contain the subject and a configured
summary from the existing settings owner; collapse that summary when expanded
so it does not leave an empty line above the controls. Notifications retains its consequence
description under the heading. Static group headings stay outside their cards,
except Home's overview groups, whose stronger headings sit inside their cards.
Standalone settings cards and expander headers carry one subject icon. Rows
inside a group keep text labels without repeated icons. Distinct manual tasks
inside an expander use the shared Section heading and its task icon, so icons
identify the hierarchy rather than competing with each preference.
Home link hover and focus cover their full row with the content inset inside the
button; the group heading is not part of the interactive row.
Settings labels, values and feedback remain single-line, with ellipsis and the
full text available through tooltips and accessibility.

General begins with Windows integration, then Startup and closing,
Notifications, Power, the Settings start-page preference, Dialogs and
confirmations, and automatic updates. The start-page preference is a separate
card, because it controls navigation rather than consent or a dialog.
Registration warnings use the full row width below the first row of actions.
The native proxy dialog keeps its existing transaction, validation, feedback
slot and button placement.

Transfers contains Download locations, Adding torrents, Queue, Seeding limits,
Watched folder, and Skip files. Skip files
hides its pattern editor while off, retaining the saved patterns. Network begins with adapter
and proxy cards; advanced groups contain encryption, peer connections,
discovery and incoming connections. Library settings use collapsed Video
information, Subtitles and Search protection expanders to keep their controls
out of the overview until needed. Video information includes the IMDb data row.
Subtitles uses native task tabs: Preferences holds supplier access, wanted
languages, automatic subtitles and additional matches. Wanted languages expands
the language-selection controls; additional matches is a direct setting row.
Find subtitles holds the existing Library batch search;
Existing files holds the local scan. The selector retains its native vertical
padding; coverage facts use the shared settings-row layout so they have the same
row insets as the other tasks. The shared Motion owner slides between tasks
in tab order and respects Windows' animation setting. Task controls stay
alive across switches, so language filters, selections, batch choices and scan
results survive. A Settings search destination selects its task before the
existing reveal and focus path runs. Batch status is observed only while its
task is shown; leaving that task does not stop the underlying search.
The task viewport has a 320-DIP minimum height to keep the following cards
steady for the ordinary controls, and grows with text scaling and expanded
options. The approved Task tabs design and its alternatives are preserved in
`app/prototypes/plugins-subtitles-prototype.html` on the throwaway branch
`codex/prototype-subtitle-task-tabs`; the implementation keeps only the chosen
composition. Action buttons keep their natural label width, including after an
expander opens or the language changes; a width measured while content is
collapsed must not clip a later label. Search protection owns the shared
provider search limit and skipped-search reset. Collapsed headers show the
video provider and automatic-download state, the subtitle supplier, languages
and automatic-download state, and the current search limit. City data is a
standalone icon-led card, matching Network's standalone cards; it needs neither
a separate Map heading nor an expander for one entry. The Database expander
keeps its existing presentation.
Video information separates Provider from the Download
automatically switch; configuring a provider, its credentials or its data does
not change automatic download intent. Provider configuration supports manual
requests independently of automatic work, so its controls stay available in
either switch state. The switch's neutral description states its background work.
The page header facts identify the video provider and subtitle supplier.
An expanded group leaves automatic-download state at its switch instead of
repeating its collapsed summary. The two data rows are one control with one pair of
dialogs, worded from each dataset's text section, so a download, update or
deletion of either data reads and behaves the same
([peer location](../peer-location.md)). Filtering languages never discards hidden selections.
Search and scan feedback occupy reserved single-line slots, so results do not
shift controls.

## Speed limits

Settings > Speed limits groups the current mode, standard and alternative caps,
Connection setup and bandwidth accounting. Queue belongs to Transfers and peer
connections to Network. The weekly editor
lives in Settings > Schedule, with both pairs of caps summarized above it so
periods have a visible meaning. The mode choice is
No limits, Standard limits, Alternative limits or Weekly schedule, in one native
ComboBox, so no separate schedule switch can disagree with it. No limits is a
choice of its own because people read "Normal limits" as no limits, and a capped
normal pair then looked like a stalled download; No limits also keeps the typed
caps for later. A fixed choice applies all week until Weekly schedule is chosen
again: it turns the schedule off rather than overriding it until the next
scheduled change, because a choice that expires by itself surprises the person.
The periods stay saved. Caps, origin and pause reason come together from the
engine snapshot. The picker shows a pending choice at once,
keeps that choice focusable and temporarily disables the other choices. The
rest of the page shows the engine's confirmed choice, so it changes once, when
the choice applies.
Failure restores the confirmed selection and appears beside it; recovery clears
an obsolete connection error. Native keyboard and accessibility behavior is
retained. Colour is not the indication of applied limits.

Speed limits puts Limits to use before equally sized Standard and Alternative
profiles, then Connection setup. Both profiles remain editable in every mode,
including No limits: editing saved caps does not choose which pair applies.
Speed limits and Schedule use one editor and the existing decimal MB/s
conversion; connection capacity stays in Mbps. Schedule wraps that shared
editor in an expander and retains an always accessible four-choice mode selector
beside the weekly map. Periods keep their existing save owner, with when and
behavior summaries, inline days, 24-hour Start/End controls and Remove beside
them when space permits.

## Appearance

Appearance uses one responsive three-column native radio group for System,
Light and Dark. All previews share a compact 21:9 crop with 8-DIP clipped
corners; System splits the same Light/Dark artwork. Display language remains
a separate row below. There is no redundant theme dropdown. Free space,
external IP, memory usage and title-bar speeds use four directly accessible
switch cards. The external-IP and memory-usage cards follow Advanced mode;
hiding one keeps its saved value and removes its whole card from the layout.

Appearance offers Show external IP in the status bar, off by default. The status
bar shows the IPv4 and IPv6 addresses libtorrent reports, or Not available until
one is known. A changed network clears the old addresses; no external lookup
service is needed. Appearance also offers the application language and Follow Windows, Light, and Dark
through the existing theme owner.

**Owner ruling: Reset all settings is the Advanced card after the last
Appearance card,** so the owner can see what a new installation shows. It flows
with the other cards rather than sitting at the bottom of the page. Its
confirmation defaults to Reset, the act the person chose. The reset returns
every engine setting, the Library and subtitle settings, saved sign-ins and API
keys, and the window layout to their defaults. The window then closes without
saving its layout, and the engine opens a new one, which starts as after
installation. Torrents, downloaded files, Library information, downloaded data,
search protection and the Windows file and sign-in registration stay, because
they are not settings: the installer sets the registration, and resetting it
would rewrite the person's default apps in Windows.

## Revealing settings

Home, search and related links use one reveal-expand-scroll-focus path.

**Owner ruling: the destination outline encloses the whole visible card,
including its icon, padding and contents, and excludes external headings.**
The card styles and each section template's `Card` part identify that surface; the reveal path
selects the nearest enclosing card, never an inner row, label or editor. This
keeps the outline off title text and prevents narrower highlights when a card
adds an icon beside its rows. The outline is neutral with normal card rounding,
flush against the block. It lasts three seconds or until focus enters a control.
Theme selection retains its
accent outline and checkmark. Sidebar category selection replaces the current
Settings visit, so Back does not retrace every peer category. Related links keep
their return destination and scroll position; breadcrumbs describe hierarchy.
Connection setup alone is a child page, with Speed limits > Connection setup.
It keeps the same Settings sidebar visible with Speed limits selected; the
retained caller category and scroll position still govern Back.
Related Schedule and Queue links select their real categories and reveal the
weekly map or Queue.

## Connection setup

Connection setup retains its existing proposal and Apply/Cancel workflow.
Its content and footer share the Settings width resource, so the proposal
does not spread across an arbitrarily wide workspace.
The approved constrained layout places inputs and proposed changes side by side
when they fit and stacks them otherwise; Apply and Cancel follow the content,
beside a status that says whether changes are ready to apply. Title-bar Back returns to the initiating setting. Ordinary settings
keep their individual commit behavior. The proposal shows current and proposed
values, emphasizes changes, and enables Apply only for a valid change.
One slider for each pair sets the standard and the alternative limits as a
share of the capacity, in 5% steps, so both pairs take one gesture each and the
person sees which pair they set. The top position removes a pair's limits. The
standard slider names Reduced at 50%, Balanced at 85% and Full speed at the top,
and each slider shows the resulting speeds below it. A pair follows its slider
only after the person moves it or changes the capacity, because the slider
shows the saved limits rounded to a step and must not propose a change nobody
made. Before the first setup the sliders start at Balanced and 25%. Queue
limits follow the standard slider: 2 downloads, 2 seeds and 3 torrents at 50%
or less, otherwise 3, 5 and 8. The preview groups the download and upload limits
under Standard limits and Alternative limits, marks the pair in use now, and
lists the maximum active counts after them. It leaves out peer limits, which the page never changes, and the capacity,
which its fields already show. Apply turns on standard limits only when Limits
to use is No limits and the standard limits change, because a new install
starts with No limits and limits that never apply look broken. A Limits to use
row appears only when Apply makes that change. Any other choice belongs to the person and stays.
Preset identity comes from confirmed settings, not a separate profile. Test measures
real download and upload capacity through the engine's
[temporary suspension](../engine/settings.md#connection-measurement). The full-width test
card uses the approved C original sweep, large segmented numbers and Lucide
download/upload captions. Both directions share one 1/2/5 scale, so equal
speeds fill equal arcs. It starts from the larger entered capacity, or the
first measured value when capacity is unknown, and expands without shrinking
during the test, keeping the reading within range without continuous scale
changes. When the test completes, it fits the larger result, so a stale
capacity does not keep the scale too large. Motion follows Windows animation preferences.
Quick, the default, measures five seconds each way; Longer measures fifteen
seconds each way. Test and Cancel use the same fixed-width
slot beside those choices. Phase, percentage, determinate progress and payload
usage share reserved space, so testing never shifts the inputs. These
reservations scale with system text size. Inline feedback includes the resumption
countdown and a Resume action, so the person need not wait for its expiry.
A Local connection row at the bottom of the speed test card shows the current
link speed and, for Ethernet, the speed the adapter supports, beside the result
the link can cap and before Apply/Cancel. Both of its states are two lines tall,
so a warning never moves the page. It becomes a warning when the
negotiated Ethernet link is below entered capacity or the adapter's reported
maximum, including a 100 Mbps link on a gigabit adapter, and suggests checking
the cable and ports without claiming which component caused the limit. It also
warns on a metered connection, because test traffic uses the data allowance.
The row is hidden while a proxy or selected adapter carries torrent traffic,
because the Internet route's link then says nothing about torrent speed.
Announce phase changes through the window's existing accessibility path, not
each countdown tick. Measurements populate the proposal; only Apply changes
settings. An information button beside the test heading opens a native flyout
with the provider, traffic cost, route details and privacy link, keeping these
details available without permanent extra lines in the form.

## Preferences and advanced mode

Download locations owns the default folder, incomplete-filename suffix and
optional separate incomplete folder. Folder and suffix defaults apply to new
torrents; help makes that scope explicit. General's Dialogs and confirmations
owns the independent Add dialog preferences for torrent files and magnets,
the related bring-to-front option, removal confirmation and Exit confirmation,
in workflow order. Transfers holds the defaults Add applies, with no second
control for showing its dialog. These ordinary preferences restore suppressed
dialogs without Advanced mode. The background-running toast is restored by its
Notifications switch, so notification preferences stay together.

Advanced mode reveals deliberate tuning and diagnostics, not merely
less-used features. Startup behavior, bringing Add to the front, queue position,
folder layout, incomplete filename suffixes, duplicate handling, seeding limits (including inactivity), watched
folders, and title-bar speeds are ordinary preferences. Download locations,
confirmation restores, notifications, sleep prevention, VPN adapter and proxy
setup, Library information, scheduling, and subtitle setup also remain visible.
Advanced reveals listening port and router mapping, peer protocols and discovery,
connection caps and attempt rate, slow-torrent thresholds, bandwidth accounting,
allocation, exclusion patterns, and
storage, checking, refresh, and history tuning. Use this copy is ordinary when
another TinyTorrent copy owns startup, because otherwise the visible switch
cannot select this copy.
Closing offers confirmation before Exit stops active transfers;
the engine's desktop host owns that decision so it also works without WinUI.
The open window shows the prompt as its own dialog; the host shows a native
prompt only when no window can. Closing only the window does not ask to stop transfers.

Updates follow the [update model](../architecture.md#installation-and-updates).
Reaching a seeding limit stops
the torrent; nothing is removed without a request. Starting that torrent by
hand lifts the limit for it, so it seeds on as asked instead of stopping again.
The sleep switch names its mains-power condition, so a laptop that sleeps on
battery does not surprise its owner. The seeding switch only extends it, so it stays
visible and is disabled while the sleep switch is off; hiding it would move the
page and hide the choice.

General's Windows integration group holds
Start when I sign in. Startup and closing holds Start in the notification area,
Start with transfers paused, the splash setting, and
[Unfinished tasks](torrents.md#unfinished-tasks). Exit confirmation belongs
to Dialogs and confirmations. Start in the
notification area, off by default, makes
starting TinyTorrent start only the engine in the tray; opening TinyTorrent
while it runs still shows the window. The splash switch, on by default, serves
a person who opens TinyTorrent often and finds the splash in the way.

## Proxy server

Settings > Network has a Proxy server section with one row: an icon for the
engine's check of the proxy in use, once it has ended, the proxy's type and
address or Off, and Edit. Edit opens the Edit proxy server dialog, because the
proxy's five values save together and a half-made proxy would stop every
connection. The dialog's Check connects with the typed values without saving
them; Check and its progress sit in the Footer at the left, and Save and
Cancel at the right. As in the supplier dialog, the result is a toast:
Connected closes by itself, and a failure stays with its reason. A failed
Save is the toast of every unsaved setting, and the dialog stays open. Save
does not require a check. Choosing None or SOCKS4 disables the fields that it does not
use instead of hiding them, so the dialog keeps its size. While a proxy is in
use, the port and port-mapping rows are disabled, because peers cannot connect
in through a proxy.

## Notifications

General has a Notifications section with four switches in order of
importance: Notify about problems, Notify when a download finishes, Notify
when a torrent is added, and Notify when the window closes and transfers
continue. The order shows which notifications matter most. Turning all four off
stops every engine notification except failures of something the person just
asked for. Problems are one switch, because no one needs to silence one kind of
problem and keep another.

## Weekly schedule

Scheduler presents one weekly overview with standard limits, alternative limits,
and paused periods, because separate schedules obscure their combined effect.
Time runs left to right beneath a 00–24 hour ruler; each day has one row, and
segments occupy widths proportional to their duration. The week shows saved
periods, and an accent line with a round head marks the current day and time.
The compact legend includes the same current day and time. Ordinary periods
use fills; an outline and handles distinguish the selected period. High Contrast
retains a thin outline around ordinary periods so their edges remain visible.
While a fixed choice
applies, the week shows that choice on every day, all day, and hides the legend
and the periods, because the map shows the limits that actually apply; the
periods return when Weekly schedule is chosen. Periods are coloured, and
the time between them is named Standard limits where the gap is wide enough to
read. A legend names
the three fills, and a Periods row with Add lists a fixed All other times row
with Standard limits, then every period as an expander. One period is open
at a time: opening a period closes the one open before, as one expander at a
time stays open, so the page never holds two unfinished periods. Each period's
header describes its days, times and mode. The native expander owns its arrow
and content visibility; opening a period does not replace the control. Its
content holds the mode ComboBox, days, start and end times, duration and Delete,
so editing controls do not compete with the header's expand gesture.
Every change applies at once, like every other setting, so the
editor has no Save or Cancel. Input the schedule cannot take, such as a period
with no day or a duplicate of another period, stays on screen beside its error.
Closing the period or leaving Settings drops that invalid input. Settings
navigation continues immediately while valid input saves in the background.
Closing or switching periods, Add and Delete apply valid input made during an
earlier save before replacing the editor, so save timing cannot discard a change.
Period changes
run in the order they are asked for, so a click made during one is never lost.
Add saves Monday to Friday, 09:00 to 17:00, with alternative limits, and opens
it; when the schedule already holds that period, Add opens it instead.
The scheduler is one control embedded in Settings, sharing its period and
save owner with the period editor rather than implementing scheduling rules twice.

Dragging at least an hour of empty time saves a period for that range and opens
it; a shorter drag counts as a click, so a slip of the pointer creates nothing.
Dragging up or down extends the new period to every day between the pressed row
and the pointer's row. A click on a period opens it; a click on empty time closes the open
period. Right-click, the menu key or Shift+F10 on a period offers Delete.
Dragging a
period horizontally preserves its duration; the open period's start/end edges
resize it. Thin inset grips appear on the hovered or dragged occurrence, while
every occurrence keeps its outline, so recurring periods do not fill the week
with handles. Gestures snap to 15 minutes and show a live time/duration preview.
Release saves the move or resize. Escape or lost pointer capture restores the
times before the gesture. The native time fields retain exact minute precision.
Blocks show their effective time ranges as well as their modes; hover and the
open period expose the complete source period, so an overlap does not obscure
its saved times. The ruler reduces its tick count at narrow widths, and calendar
geometry follows Windows text size so labels do not collide.
Keyboard arrows navigate days and times, Space or Enter opens the period under
the cursor, Enter on empty time adds a one-hour period, and Escape closes the
open period, so dragging is never required. Add and the period list remain
native keyboard and accessibility routes to every operation.
Keyboard navigation displays the current day and exact time. Escape in the
period editor closes it and returns focus to the period's header.

Opening a repeating period outlines every occurrence, including portions
covered by Pause, so moving one occurrence cannot silently change other days.
An overnight period shows its following-day portion and handles at its actual
endpoints. The fill still shows the effective mode, with Pause taking precedence.
Reject exact duplicate submissions; preserve distinct overlapping rules so a
later Edit or Delete retains its meaning. A fixed choice stops application of
the periods and keeps them for the next time Weekly schedule is chosen. A refused
save keeps the attempted values in the open period and reports the failure through
the application notification, so the person can correct them or close the period
without a navigation delay.
Full-day descriptions say All day rather than midnight to midnight.
Each period has days, start/end times, and a choice of alternative limits
or pause, edited with native checkboxes, TimePicker controls, and a ComboBox.
Standard limits apply outside periods; pause takes precedence on overlap. Overnight
periods end on the following day. Weekly schedule is not chosen by default; the
schedule repeats in local time. Its engine owner preserves stopped torrents and manual
Pause all, so a scheduled boundary cannot undo the person's explicit pause.

## Windows integration

Settings offers a Start when I sign in switch and an Open torrents with
TinyTorrent switch covering `.torrent` files and magnet links. Both call the
engine's [registration owner](../engine/lifetime.md#windows-registration) and show its
observed state. Each switch works at once and never opens Windows Settings
([owner ruling](../engine/lifetime.md#windows-registration)). The handler switch is on only
while Windows opens both kinds with TinyTorrent; turning it on registers the
current copy and makes it the default for both. Two read-only rows under the
switch, Torrent files and Magnet links, each name the app Windows opens that
kind with, or No default app set, so the person sees what a click starts without hovering
or opening Windows Settings. The sign-in switch
is off while Windows has disabled the entry, and turning it on enables it.
While any torrent or magnet handler starts a program that is missing,
whichever app registered it, the switch's caution flyout names that program and
Repair appears beside the switch, which removes the broken handlers, asking Windows for
an administrator when some were registered for all users, and registers
TinyTorrent. Only Repair asks for an administrator, and it then shows
Windows' shield beside its name; the switch never asks. A problem notification about such a handler opens this page.
Turning it off removes TinyTorrent as a handler. The action beside the switch
appears only when it is needed; the row's controls are right-aligned, so the
switch does not move.

**Owner ruling:** an entry that starts another TinyTorrent copy is still
TinyTorrent's registration, so its switch shows on. A warning icon opens a native
flyout with the complete, selectable message and executable path, so a long path
does not crowd the setting or hide its explanation. Both registration settings
use the same caution presentation; an entry never shows as unregistered merely
because it starts another copy.

When torrent handlers start another copy, Use this copy beside the switch writes
those entries for the running copy. Its tooltip names the scope: torrent files
and magnet links. When another copy owns startup, Use this copy appears beside
the sign-in switch in the same row. Both rows place the caution, action, switch and state
text in that order at the right. The two app names sit in compact, indented rows
under the handler setting, so they read as its details. Show failures beside the
affected action and preserve the user's work. Routine successful changes need
neither a confirmation dialog nor a technical explanation.

## Category motion

Settings expanders retain the native header and accessibility, with the owner's
LabForms drawer animation: the clipped body animates its height over 260 ms
opening and 220 ms closing, then returns to automatic sizing so following cards
move with the reveal.

**Owner ruling: Settings categories enter from outside the content viewport,
without fading.** A short nudge makes the replacement appear instant, and a
fade can superimpose pages. The title, summary and settings travel together by
the full viewport width, in navigation order. Entrances start off-screen before
layout can present the new content. Content creation and layout finish before
navigation animations start, so loading does not consume their visible duration.

## Committing settings

A setting saves too quickly for a pending display to help: its control stays
enabled, keeps focus and shows nothing until saving fails. Leaving a field,
category or Settings, including on window close, captures valid typed input and
restores the saved value of invalid input. Accepted saves finish in their
existing owners; failures notify even after navigation. A reply belongs to the
input submitted and cannot refuse or discard newer input. Shutdown drains
accepted work after hiding the window.

An unavailable engine differs from a refused value. Back preserves unfinished
ordinary Settings input in the window and leaves the page; window close may
discard it without asking. Neither waits for reconnection. A picker or an
operation already in progress finishes before close continues.

Schedule saves, Add and Delete failures use notifications and never hold
Settings navigation. Leaving drops an incomplete or duplicate period; valid
changes retain the schedule owner's ordered save path.
