# Torrent interaction

The [shared UI policy](../ui.md), including [commit behavior](../ui.md#committing-edits),
applies to this contract. [Workspace](workspace.md) owns shell commands, panes
and saved presentation; [status and feedback](feedback.md) owns notifications.
The [engine](../engine.md) owns operation semantics and durable state.

## Torrent table and commands

The table starts with Name, Size, Progress, Status, Down speed, Up speed, Time
left, Ratio, Seeds, Peers, Added, and Completed; the person can hide, show, and reorder them.
Queue, Size, Progress, Status, Down speed, Up speed, Time left, Ratio, Seeds,
Added and Completed keep at least their fitted widths during fills, because
cutting these short values hides their meaning. Progress percentages stay on
one line even when a person narrows the column directly.
Time left reads in its two largest units, such as 3 d 4 h or 45 min, and 100
days or more reads as ∞, because a raw count of minutes cannot
be read at a glance. Seeds and Peers each show the connected count with the
swarm total in parentheses: the tracker's count when it
reports one, otherwise the peers this session has heard of. Peers excludes
seeds. Both sort by the swarm total, because it shows how widely a torrent is
shared, while the connected count follows this session's own connections.
Added and Completed read as elapsed time, such as 3 hours ago, because
how long ago a torrent arrived or finished is what the person compares; the
exact date and time is the tooltip. Completed shows a dash until the torrent
is complete.

A check or a move that waits for its [drive](../engine/files.md#drive-turns) shows
Waiting to check or Waiting to move, with the Checking or Moving files icon. A
state is named by what ends it: this one ends by itself, so the person has
nothing to do, while Checking and Moving files would claim that the work runs
and Queued already means the download queue. The Status tooltip names what the
job waits for, such as "Waiting for C:, used by Ubuntu 24.04": the drive by the
letter or server of the torrent's own folder, because the person does not know
disk numbers. A move that waits only for another torrent's file work names
that torrent instead, such as "Waiting for the files of Ubuntu 24.04". No
queue position shows, because each drive has its own order.

### Queue ordering

Natural order follows the engine's download queue. The Queue column shows
one-based queue positions and a dash for torrents outside the queue. The engine
owns order and renumbering; the window saves only its table layout.

Dragging a selection opens the numbered queue wheel under any table sort.
The wheel starts at the average of the selected queue positions. While holding
the button, scroll to a destination and drop between numbers. Selected torrents
without a queue position remain untouched; a selection containing only those
torrents does not open the wheel, because there is nothing it can move.
Destinations include the complete queue even when the table is filtered.
Queue menu and keyboard actions use the same engine operation as dragging.

### Commands

Torrent commands are Stop, Start, Force start, Open, Open folder, Copy magnet
link, Copy info hash, Move files, Verify, and Remove. Download in
sequential order and Download first and last pieces first are menu choices
beside them. An item is checked when every selected torrent
has that choice and mixed when only some do. Choosing a checked item turns the
choice off for all of them, and otherwise turns it on for all of them, so one
click makes a mixed selection consistent. The inspector does not repeat them:
the item already shows the choice where it is changed. Open hands the file
of a single-file torrent, or the folder of a multi-file torrent, to Windows as
Explorer does, only on the person's request. Double-click and Enter on a row
invoke Properties in both Torrents and Library, showing and expanding the
inspector even when it was hidden or compact. Properties in the context menu
does the same. Close and View > Inspector hide the pane through the same draft
guards. **Owner ruling:** Inspector visibility, compact state and split size
belong to the shared workspace, as filter-pane visibility does. Switching
between Torrents and Library changes the pane's content and preserves those
choices, because navigation must not move the workspace or undo an explicit
Close. Selecting a Library row updates its content without opening the pane.
When no torrent or several are selected, or the inspected torrent is
removed, the pane stays open and shows how many are selected, because a pane that
closes and reopens as rows are selected moves the table under the person.
Add torrent file opens
the native picker, and Add magnet
link opens a field for the link even when the Add dialog is turned off; the
source then follows the magnet dialog preference. Pause all and Resume all
are in the window and the tray, and keep each torrent's own
[stopped or running state](../engine.md#state-and-work). Exit is in the window as
well as the tray.

### Torrent speed limits

Limit torrent speed… sits beside the piece-order choices in Torrent, the row
context menu and command search. Its name says torrent because Speed limits…
already opens the global limits. It opens a dialog that names the torrent, or
the count for several, with a download limit and an upload limit in the rate
units Settings uses; an empty field or 0 means no limit. When the selected
torrents differ, a field starts empty and reads Mixed, and leaving it so keeps
each torrent's own limit. Save applies both fields; a refusal keeps the dialog
open with its error. Each limit is one fixed cap. The lower of the torrent's
limit and the current global limit applies, so the schedule, alternative limits
and Pause all keep working through the global limits, and no torrent needs its
own alternative limits or schedule. A limit above the global limit is accepted,
and the limit fields' tooltip shows the current global limit so the person sees
which one applies. The menu item is checked when every selected torrent has a limit and
mixed when only some do, as the piece-order choices are. The inspector's General
view states the torrent's limits, because a person who wonders why one torrent
is slow opens its properties. An optional Speed limit column, hidden by default
because most torrents never have a limit, lets the person find every limited
torrent: it shows ↓ 500 KB/s, ↑ 1 MB/s or both, and stays empty without a limit.
Its accessible text names download and upload instead of the arrows. It sorts
by download limit and then upload limit, with no limit as the highest value, so
an ascending sort lists the limited torrents first.

## Remove and unfinished tasks

Remove keeps data unless the person ticks a check box. One Remove torrents
dialog confirms every removal, by default, with the affected torrents, a Remove
button and a safe Cancel action. Remove confirms because a removed torrent
cannot be restored without its torrent file or magnet link. Two check boxes on
one line say what else goes: the downloaded files, with their total size, and
the .torrent files the torrents were added from, with their size, shown only
when a selected torrent's .torrent file still exists, with n of m when only some
do. As in File Explorer, the key chooses the
deletion mode and the dialog never asks it: Delete, or a click on Remove, sends
ticked files to the Recycle Bin, and Shift with either deletes them permanently.
A line under the check boxes states the result, so the button stays Remove:
with nothing ticked, that no files are deleted and the torrents leave only
TinyTorrent, because people read Remove as deleting files; otherwise what goes
to the Recycle Bin, or what is deleted permanently. When other torrents use some
of the files, the line says those are kept and gives no size, because the engine
keeps them and the total would overstate what goes. Only a permanent deletion
takes the caution icon, because the Recycle Bin can restore the rest. Only Remove
saves the check boxes for the next removal. Add, Remove torrents, the
window's Exit confirmation and the [unfinished tasks](#unfinished-tasks) question
use the shared dialog footer's Don't show this
dialog again checkbox; only submitting saves that preference. Delete and
Shift+Delete then remove directly with the saved check boxes, and a refused
operation opens the dialog with its error.
Settings can restore confirmation. The dialog's default,
focus and buttons follow the ruling in [Buttons](../ui.md#buttons).
Routine stop, start, and applied settings need no confirmation.

<a id="unfinished-tasks"></a>**Unfinished tasks.** Exit, its 10-second end or a
crash can leave disk work unfinished: moves, checks and file deletions. The
Unfinished tasks setting decides what the engine does with all of it at start:
Ask (the default), Continue or Abandon. It is a ComboBox in General's Startup
and closing group, because it decides what happens at start, and choosing Ask
restores the question after Don't show this dialog again. The
[pipe contract](../pipe-api.md) owns its words and the snapshot field that reports
the waiting work.

With Ask, the window opens one Continue unfinished tasks? dialog for all the
waiting work, never one per torrent. Its body lists what waits, one line for
each kind that has work, with its count, such as 3 moves, 1 check, 1 deletion.
The title is short because a dialog title never wraps, and the list says what
the title does not. Its buttons are Continue, the default, and Abandon. It
is Abandon and not Cancel because in Windows Cancel means change nothing, while
this button deletes the files that moves copied; the tooltips say what each
button does to each kind of work. The dialog has no Close button, because
nothing is not an answer here: the engine keeps the work waiting and the torrents
stopped until the person answers. Escape therefore leaves the dialog open. Only
an unavailable engine closes it, because the dialog could not answer anything;
it opens again when the engine reports the same waiting work.

The dialog opens once for each time the engine starts waiting. It waits until
the window is on screen and no other dialog or file picker is open, and then it
opens before a waiting Add draft, because the engine holds drive work until it is
answered. A snapshot that was sent before the answer does not open it again. A
failed answer is reported and leaves the dialog open. The Don't show this dialog
again check box saves the pressed button as the setting, Continue or Abandon,
after the engine accepted the answer; a failed save is reported like any other
setting.

## Disconnection and progress

Retain recognizable identity and last-known read-only values on disconnect, mark
them stale, and disable writes until the engine is available. Current speed is
unknown while disconnected and displays an em dash; old rates must not appear
current. Show loading separately from an empty list. Progress fills its column
through the shared active and muted styles in `App.xaml`. Torrent rows, the
inspector summary and file rows consume those styles rather than choosing their
own progress colours. File and folder bars are muted at 100%; incomplete bars
retain the platform accent. Stopped, paused and finished torrent bars are muted,
verification remains prominent, and native progress error states retain their
severity resources. Written status preserves the meaning without colour.
Routine outcomes are announced to assistive technology without a visible toast;
actionable failures follow the
[feedback placement policy](feedback.md#feedback-placement). Pending operations
and failures remain visible without exposing internal protocol machinery.
Reconnection preserves presentation state according to the pipe API contract.
After reconnecting, show the confirmed list; do not leave a permanent busy state.
A missing row means the torrent was removed, not that its files were deleted; a
deletion failure arrives as a notification with Open folder.

## Unfinished input

Leaving a tracker list applies it, as leaving a setting does, and failed file
choices are sent again. Incomplete, invalid or refused edits retain their
editor and error instead of changing context; Settings and its schedule follow
their [notification and navigation policy](settings.md#committing-settings).
A torrent that leaves a filtered table leaves its selection, but the Inspector
keeps that target and any unfinished edit. If the engine or target is unavailable,
leaving drops file choices because Retry is unavailable and those choices have
no Cancel. Window close drops edits that cannot be saved.

Add and Move ask before departure when the person has entered a source or a
destination. An untouched editor or options alone do not justify a question.
Save runs Add or Move; a failure retains the editor and its error. Discard drops
the draft and continues; Cancel retains the editor and focus. The question
names the unfinished work and its action. Save and Discard tooltips explain their
effects. Cancel is the default, so closing cannot unexpectedly start work or
discard input. Deleting files has its own confirmation unless suppressed.

Restart stays reachable inside an open Add draft while the engine is unavailable,
as in the main window, so a modal editor cannot block recovery.

## Add

The task answers: what is being added, which files are wanted, and where they go.
Add to top of queue is an always-visible option, initialized from Settings (off
by default). For a batch,
it places the new torrents first in their displayed source order, so choosing
the option does not reverse the list the person reviewed.
Use native source/destination pickers and an editable magnet input with an
explicit Paste action. Read the clipboard only after the relevant user action.
Preserve accepted input and choices when a picker is cancelled or a replacement
source fails; successful replacement deliberately starts a new preview.
Getting metadata transfers pending magnet text into the staged source once.

Show preview progress immediately, keep cancellation available while acquiring
metadata, and expose file choices when metadata is ready. The engine supplies
that metadata; the window does not parse torrents or impose an old client's
file-choices-only-after-add limitation. Unknown metadata is an explicit state,
never an invented file list or silently started payload transfer. Add is
available before magnet metadata arrives, so a slow swarm does not hold the
person in the dialog; the torrent then wants every file, and file choices move to
the inspector's Files view.

**Owner ruling: several sources added together are added at once, with no
dialog and no question.** This covers one drop or paste, and several files opened
from Explorer that reach the window together. They go to the default folder with
the default options, and the torrents they add are selected in the list, so the
person sees that all of them arrived. A torrent already in the list is selected
where it is and keeps its trackers, because offering a merge would be a
question. A source that fails is reported in the window's error toast and
dropped. File choices for each torrent are made later in the Files view. One
drop of thirty torrents opens nothing, because a cascade of dialogs is the
failure this protects against. Several files chosen through Add torrent file
open one Add dialog, unless the person turned that dialog off; then they are added
with the default options, and a source the engine refuses opens the dialog with
its error. Sources that arrive
while the Add dialog is open join it, so a second dialog never opens. The dialog has a
Don't show this dialog again check box, which turns off only the dialog preference
for the source kinds submitted: torrent files, magnet links, or both in a mixed
dialog. A magnet-only submission never changes the torrent-file preference, and
cancelling changes neither preference.
Add keeps field validation beside the magnet or destination, and refused Add,
empty file selection and disconnection in its inline error bar, so the open
draft keeps its explanation and recovery action visible. Source preview
failures use the existing error toast, once on the transition into failure
during polling rather than on every poll; they do not resize an active editor.
Source rows retain their failure descriptions. Clipboard access failures also
use the existing error toast because they do not invalidate the draft.
Disconnection already disables editing and keeps Restart in the dialog. The
error bar takes space only while visible; the dialog keeps its fixed size and
footer, so a potential error does not permanently take space from the file
browser or magnet editor.
If a source has no wanted files when a single-source task becomes a batch,
Select all files beside that source restores a valid choice. Keep prior file
choices until this explicit action; adding another source does not reset them.

Equivalent staged sources keep all their original inputs on the one draft entry.
Reconnect reacquires them through the engine's existing preview owner, preserving
distinct tracker URLs and the user's choices. Literal input deduplication keeps
case-sensitive tracker paths distinct.

When sources require the Add dialog, those arriving during another modal task
remain in the Add draft and open when that task closes. Turning a source kind's
dialog preference off allows direct addition during other tasks; an already-open Add dialog
still owns new sources and its choices. Failure to show a dialog does not cancel
its input. One window-owned shortcut path keeps application actions out of modal editors;
Close and Exit still follow the unfinished-input rules.

The preview follows the shared [file browser](#file-browser) rules.
Keep file identity and selected bytes clear; when a known list
has no wanted files, explain why Add is unavailable. The destination starts from
the configured default or last-used download folder, initially Windows' Downloads
known folder, and remains changeable through a native picker. A failed
free-space check must not be presented as proof of an invalid folder.
The destination also offers recent folders: the default folder and the folders of
the newest torrents, six at most. They are derived from the default folder and
existing torrents rather than stored, so removing a torrent removes its folder
from the list. Dropping a single
folder on the open dialog makes it the destination. Free space for the destination's
drive appears under it, and a warning replaces it when the wanted files do not
fit; the warning never blocks Add, because the person may free space first.
Network drives show no free space, because asking a disconnected one can block the
window.

The dialog shows the folder and options on the left and files on the right, with a splitter between
them that keeps its share of the width while the dialog is open. An icon toggle,
whose tooltip reads Hide folder and options, gives the files the whole width; the left side starts hidden when the files would
have too little room beside them, so file names stay readable in a small window. ContentDialog cannot
be resized by dragging, so the dialog has Maximize and Restore in its header: the
default size suits a typical torrent, and Maximize fills the window and follows it.
Close follows Maximize at the right of the header, where Windows places it, and
does what Cancel does.
The magnet field wraps one link across multiple visible lines and scrolls
vertically, using the available right-pane space. Wrapping does not insert line
breaks into the link. Paste and Preview sit at the right below it, using the
footer's button-width, inter-button gap and uniform padding tokens. When sources
are previewed, they share the pane with the editor so its height cannot hide
the preview.
The commit button is Add, and its tooltip says whether the torrent starts; when
several torrents, including typed magnet text, will be added, the tooltip gives
the count.

Keep one dialog with a reachable native footer. A long body scrolls; the virtualized
file list has a finite viewport and owns its collection scrolling. Do not create
another page or state model solely to accommodate a short window.

Commit captures source and choices once, indicates pending work, and does not
promise that Cancel can undo an accepted command. A confirmed addition reveals
the torrent. A duplicate in the dialog preserves the existing torrent's saved choices
and data and offers a path to it; when its source lists trackers the existing
torrent lacks, it offers to add them. Direct batches follow the no-question
policy above. A rejected choice returns to the relevant field;
an uncertain outcome follows the protocol's reconciliation rules.

A new torrent may use files another torrent already uses, such as the same
content from a second tracker; the [shared-files policy](../engine/files.md#shared-files)
allows it, and verification reuses the files. Pieces that do not match are
downloaded again over those files, so the dialog names the torrents that already
use files at the chosen destination before Add. To replace an older torrent, the
person removes it, keeping its files, and adds the new one; the initial release
has no automatic Replace action.

## File browser

Search within files changes visibility, not wanted choices. Bulk selection has
an explicit scope. Filtering retains folders that still match, so their existing
expansion does not collapse when the person types. Folders form an expandable hierarchy. A folder's wanted checkbox
and priority apply to every descendant, including filtered or collapsed files.
Folder priority offers Leave unchanged to preserve individual child priorities;
Normal, High, and Low apply once to all descendants. Mixed wanted choices show
an indeterminate checkbox. Bulk Select all/none acts on files matching the
search. The same file browser serves Add and the inspector, so these rules have
one owner. Keep its summary, search, and bulk actions on one compact row and give
the list the remaining viewport, because files are the task's primary content.
Expand all and Collapse all appear when the list has folders, and each row shows
an icon for its file type so a long list can be scanned.
The compact action row offers Priority for selected rows, with the same choices
in the row context menu. F2 on a passive row opens that choice for the selection
when the current row belongs to it, otherwise for the current row alone. The
inline priority editor changes only its own row. All entry points use the same
priority operation: expand folders to their descendant files, union by file
identity, and apply each file once. This keeps an overlapping folder and child
selection from submitting the child twice. Ordinary tree, checkbox and ComboBox
keyboard behavior remains native.

The File column stays first and visible. Size and Priority are available in
both Add and the inspector; Progress belongs only to the inspector because an
addition has no download progress yet. Size, Priority and Progress keep at least
their fitted widths during fills, so sizes and choices stay readable. Progress
percentages never wrap. Neither host permits row reordering.
Sorting applies to siblings, using file names and numeric sizes, priorities
and byte-weighted progress rather than formatted text. A new torrent clears
row selection so paths shared with the previous torrent cannot inherit command
targets. Search preserves wanted choices, priorities and expansion; selection
follows the table's visible-row pruning. Folder values include all descendants,
so a displayed total describes the same files its command changes.
Row Ctrl+A selects visible command targets without changing wanted state.
Changing a wanted checkbox preserves row selection, and Space uses the native
checkbox behavior. Mixed priority does not present a uniform value or change
files merely by opening its menu; it sorts before Skip in ascending order.
The context menu follows the table's target selection, and priority changes
retain the existing Add-draft or inspector commit and failure path.

## Inspector and edits

Filters and Inspector use the shared [Pane](workspace.md#panes-and-filters)
for chrome, controls and compact presentation. Docking determines the compact
strip's edge; an undocked pane stays expanded without Pin. The workspace owns
accepted visibility, placement, compact/maximized state, saved dimensions and
departure guards. Pane buttons request changes through that owner, so shared
drawing cannot bypass an unfinished edit or move another pane.

**Owner ruling:** the six inspector sections use a native SelectorBar at the
start of one header row. Each section, and each Library section, shows a Lucide
icon before its name, as Microsoft's SelectorBar guidance does. The torrent name is centred in the space between the
SelectorBar and the Pin and Close icon buttons. The owner
could not get used to a left navigation pane, and one row keeps the sections
visible while the working view gets the full inspector width. The person
adjusts the inspector's height with the split.

Pin collapses the shared inspector to a ten-pixel horizontal bar immediately
above the status bar, within the workspace. The ten-pixel slot contains an
eight-pixel button and a two-pixel top gap separating it from the content.
A centred Lucide Ellipsis opens it
again. The bar clips the icon without increasing its height. Collapsing preserves
the target, section, requested height and unfinished edits; it does not close the
inspector. Close hides it through the existing draft guards. View > Inspector is
available in both modes and uses that same guarded hide action, then restores the
previous compact or expanded presentation when shown. Explicit Properties and
file-reveal commands expand it so the requested content is visible.

General, Files, Peers, Trackers, Speed, and Pieces answer different questions;
request data only for the visible view. Preserve useful data coverage and choose
each layout for its task. The Pieces view is the [Pieces map](#pieces-map).
Peers and Trackers use the existing TableView, sharing its header, selection,
column, keyboard, and scrolling behavior with the main torrent table. Files and
the Add dialog's file browser use TableView's
[hierarchical rows extension](../../../lib/TableView/docs/hierarchy.md#files-browser-integration)
for wanted, size, progress, and priority content, because folders require
hierarchy. Torrent-specific file commands stay in the application. Tracker error
status text uses the critical brush; other tracker states remain neutral, so
failed trackers stand out without colouring every healthy row.

### Peers and connections map

Peers has one compact State column: Known, Connecting, Handshake, or Connected, with
a leading Lucide icon. Known means a retained address without a current connection;
its connection statistics show a dash. Tracker Status likewise has a leading state icon. Icons
are static, use the existing inline size, and stay out of automation; the text
still names every state. An unidentified client shows a dash. Connection uses
Plain, RC4, or TLS, with unknown encryption until the handshake finishes. Its
tooltip distinguishes handshake obfuscation from encrypted payload. Web seeds
report unknown encryption when their sample lacks that information, and their
upload tooltip explains that they only provide downloads.
Peer State tooltips explain the connection stage and discovery sources; rate
tooltips show interest, choke and observed bandwidth, network or disk waits.
These facts explain the current sample without claiming one cause for a slow
transfer.

Peers starts with Address, Download speed, Upload speed, Progress and State,
so current transfer activity stays visible before secondary connection details.
Client, Country, City, Connection, Downloaded and Uploaded follow.
Country and City are separate sortable columns after Client.
Country shows the country's flag at the cell's text size followed by its name;
City shows the city name when the installed data provides one. The flag is
decorative and stays out of automation. Tooltips show each value in full when
its column is narrow. A missing country or city shows a dash. There is no switch:
the columns show what the installed data can resolve
([peer location](../peer-location.md)).

Peers shows the map and table by default, so a fresh install exposes both peer
locations and connection details. A saved view choice takes precedence.
The Peers header has one icon-only Lucide ellipsis button beside the existing
pane actions. Its flyout groups Map only, Table only and Map and table under View,
and Countries and Cities under Map detail. Cities is disabled without city data;
Get city data starts the download directly and disappears once installed. Download
progress uses the existing status bar, without a Settings detour or an additional
subtitle. The active tab supplies native buttons, commands, icons, translated
labels and menu descriptions with their enabled or checked states to one header
action slot.
The inspector owns button presentation, spacing, focus order and compact layout,
so tabs do not style header groups or manipulate another tab's actions. At narrow
pane widths, primary actions remain available in the tab's More menu; its opening
handler uses the header's compact state. Pane owns the vertical separator between
visible tab actions and its pane buttons, and presents pin, maximize/restore and
close consistently in both cards. These controls request the existing window
layout operations and draft guards.
Tab menus are built when opened from the current selection, language and provider
availability, so a menu cannot depend on the last content refresh.
The Library model owns section availability, the accepted section and cancellation
when leaving Information. A file without Information temporarily shows File while
retaining the person's section choice. Inspector exposes city-data availability
and its download command; views consume these decisions without inspecting datasets.
There is no additional toolbar or footer row. When both
are visible, the inspector chooses the arrangement that fits the larger map in
the available space, rather than a fixed portrait/landscape breakpoint. The
shared Splitter adjusts their proportions without switching arrangement during
the gesture. Each orientation retains its proportion in the saved layout.
The map and table share live peer observations and keep individual connections
in the table. Connection and active counts join the existing status-bar context;
map hover details describe the location, rates and precision.
The map area shows total download and upload rates for the inspector's selection
as a Speed statistic, the same shared statistic as the General section, with no background so it blends into the map. These totals come from
torrent summaries, including transfers with no mapped location, and use the
existing rate formatting and download/upload theme brushes. The shared peer-view
menu offers Speed position: Top-left, Bottom-left or Bottom-right, saved with
the peer layout and starting at Bottom-right. Top-right stays reserved for the
map's close button. The zoom and Fit controls sit at the bottom centre, so
moving Speed never moves them.
[Peer location](../peer-location.md#map) owns aggregation, precision and rendering.
With no torrent selected, Peers shows all torrents and Speed shows session
history. With multiple torrents selected, both sections combine only that
selection. General, Files, Trackers and Pieces require one torrent and are
disabled otherwise; leaving one of them for an aggregate scope selects Peers.
Scope normally follows the selection. The toolbar and View menu offer Selected
connections map (Ctrl+M) and All connections map (Ctrl+Shift+M). Selected is
available only with a transfer selection on Torrents; All is also available from
Library. These commands use the existing inspector: hide filters, select Peers,
choose Map only and maximise the pane. The selected scope stays on those
transfers even when a table filter drops their rows; All includes new transfers
as they arrive. Neither command changes the table's selection or filters.
The window layout remembers an open map and its scope, so a window closed on
the map opens on it again, over the restored workspace that Back returns to.
A new installation, which has no saved window layout, opens on All connections
map, because the world map makes a better first view than an empty table; the
menu bar keeps Add, and a drop anywhere on the window adds a torrent.
One temporary entry in window navigation restores the caller's workspace,
filter pane and inspector presentation through Back, Alt+Left or Escape. Choosing
another map scope retains that original return point. An explicit layout change,
including filters, toolbar, inspector open/close, compact/restore, section or peer
view, discards the return point and continues with the chosen layout, because
Back must not undo the person's layout choices. Map pan, zoom and Fit keep it.

### Trackers

Tracker headers stay short: Downloads and Next. Header and value tooltips explain
completed downloads reported by the tracker and the next announce or retry;
an unreported count stays a dash, not zero. Status and Message tooltips retain
per-local-endpoint and info-hash-version status, consecutive failures and raw
diagnostics. The visible message belongs to the displayed aggregate status,
so a working route cannot inherit another route's error. Diagnostic text also
appears as automation help. These details add no columns or network requests.

### General

General follows `app/prototypes/general-prototype.html`, variant H, in two titled
groups. Its statistic captions, larger values, state heading and monospaced
identifiers retain their approved roles, making the summary distinct from
detailed property rows.
Transfer holds the status and percentage, a native progress bar coloured as the
same torrent's bar in the table, so completion adds no colour of its own, then
completed content and time left beside the two live rates. Errors replace the
progress summary; unknown metadata has indeterminate progress without an
invented percentage. Below them, downloaded and uploaded bytes, ratio,
seeds, peers and speed limits form a statistics strip. Each statistic is as wide
as its content and the free width falls between them, so the strip spans its
group instead of leaving one wide empty column at its end. The strip takes one
row when its values fit, else two rows of three, else one column, so a long
value never runs past the group's edge. Properties holds
folder, individual info hashes, magnet link and comment beside added time,
piece count and size, privacy, creation time and creator. Folder and copy
buttons follow their values and act on the inspected torrent. Each group opens
with its group icon and title, as Settings and Add groups do. Both groups sit on
the inspector's own surface, which is already a card, and a divider separates
them, so the groups add no surface of their own. A Lucide icon in the secondary text colour leads
each label; privacy shows a lock or a globe, so its icon also states the value.
Double-clicking a value opens the place that changes it:
a rate or the speed limit opens the torrent's Speed limit dialog, which also
shows the global limit; the folder opens Move files; ratio opens the seeding
ratio in Settings > Transfers; and seeds or peers open the connection limit in
Settings > Network. The other values are facts of the torrent or its history,
so they open nothing. Below 760 effective pixels of content width, Properties
becomes one column, so values retain readable space.

Inspector chrome follows the shared [Pane presentation](workspace.md#panes-and-filters).
Tables inside it follow the
[TableView visual contract](../../../lib/TableView/docs/tableview-contract.md#8-rendering-layout-and-visual-language),
so the inspector does not define a second table appearance.
Files, Peers and Trackers have no extra horizontal table padding: TableView owns
their row and header spacing. A table directly below the pane header has an
eight-pixel top gap, so its header stays distinct from the pane divider.
Toolbars and non-table content keep their content insets. Edit and Reannounce
sit above the table at the right, with equal widths and twelve-pixel spacing
between buttons and above and below the command row. They are immediate table
commands, not dialog completion actions.

Apply individual choices and explicit file commands through the same commit
rules. When a coherent edit needs a draft, keep one active editor bound to the
torrent identity; protect only its unfinished input when changing context or
closing. An untouched inspector and an already-applied change never trigger a
save prompt. A removed target cannot receive a write.

Files permits Select none on an existing torrent. It changes wanted choices
without removing the torrent or its downloaded files; Add still requires at
least one wanted file, because a new download otherwise has no useful work.

### Move and Remove dialogs

Move torrent moves the torrent's own files; other files in its folder stay.
Its title is Move torrents for multiple selections. List each torrent's name
and size once in a bounded scroll area, using the same row as Remove torrents.
A separator divides the list from two aligned rows: From and To, the paths in
a flexible middle column, and Browse beside To. From shows the common save
folder or Multiple locations; each torrent's tooltip reveals its location.
To is editable. The list scrolls while these rows and the dialog's actions stay
visible. Multiple selections show count and total size in the existing footer.
Preserve choices when submission is refused. When other torrents use the files,
name them and offer to move them together.

Move files and Remove are available from selection actions, the row context
menu and command search; Remove has the bin icon. Remove torrents names the
torrent count in its title and groups the torrents by folder with each
torrent's size. A separator divides the list from the check boxes, and the
list scrolls while they stay visible. When the downloaded files are ticked, the
dialog lists outside torrents whose shared files will be kept. It opens once
the file scope arrives, which the engine answers from memory, and Remove is
always available, following [the deletion ruling](../engine/files.md#removal-and-moves).
Its default follows [Buttons](../ui.md#buttons).

Include shared torrents is an explicit choice; the engine rechecks the scope
when the command executes. The engine detects files already at the destination
and verifies them, following [engine moves](../engine/files.md#removal-and-moves), so the
dialog needs no file-location checkbox or repeated resulting path. A refusal
keeps the destination and choices. Accepted
work appears as Moving files with the share of bytes placed; a row error says
where the files are.

### Speed

The Speed view shows the [selected torrent's speed history](../engine.md#state-and-work),
combined history for multiple selected torrents, or session history when nothing is selected,
and replaces its graph when the selected torrent changes. It continues while WinUI
is closed, so reopening shows what happened meanwhile. Unknown gaps, such as the
time before an engine restart, are not interpolated into invented history.

The person can look at any time in the retained day. The mouse wheel over the
chart zooms between 5 minutes and 24 hours around the time under the pointer,
and dragging pans; + and − zoom and Page Up and Page Down pan from the keyboard.
A view shorter than 30 minutes shows the present only, because the engine keeps
one sample a second for just the last five minutes and one a minute before
that. The 5-minute, 1-hour, 6-hour and 24-hour choices stay visible and show the
present. A view of the past stays still while new data arrives, and Now returns
to the present.

The chart shows the trend, not every sample: per-second rates jump with each
burst from a peer. Every view divides its time into 60 equal parts and draws
the average of each part that has samples as a smooth curve that never rises
above or falls below those averages. Download is a filled area and upload a dashed
line, so the two differ without colour. Round clock times are marked under the
chart.

A one-line legend per direction gives the current average and the peak, so the
chart keeps its height in a short inspector. The legend and the rounded scale
use the same averages, so the text never disagrees with the line. Pointing at
the chart, or moving through it with the arrow keys, shows that time's values in
the legend instead; this is the equivalent textual fact of any point.

One page owner allocates table and inspector space. The inspector can shrink
freely during a drag. On release below twice its header height, it becomes
compact through the same operation as its pin; reopening restores the height
from before the drag. Cancelling a drag restores that height without collapsing.
Keyboard and automation adjustments commit immediately. Window resizing alone
never collapses the inspector. Preserve selection and unfinished edits.
The splitter is keyboard-adjustable and reports its range. Its grip is dim at
rest and accent-coloured on hover or during a drag; High Contrast retains full
opacity so the grip stays visible. The shared Splitter uses a 32-by-4-DIP grip
inside a 16-DIP strip in either orientation: 12 DIPs of whitespace plus the
grip's thickness. Add uses that same strip without extra margins, so the
whitespace matches its 12-DIP gap between stacked sections.

## Pieces map

The Pieces map answers two questions: how far the download has come, and whether
it can finish. It states the answer in words, because a grid of colours alone
leaves the user to work out the conclusion. The minimum square size keeps the map readable; filled tiles and the rarity
rule below preserve its visual language. Change that design only with
[review evidence](../../../app/AGENTS.md#design-and-review) of a better choice in use.

- **States.** A piece is *verified* when the engine has checked it, and
  *downloading* while it is being received. A missing piece is *unavailable*
  when no connected peer has it. It is *rare* when the connected peers that have
  it number at most 15 % of those that have the torrent's best-available piece,
  rounded up and never less than one, because a fixed peer count means something
  different in a large swarm and a small one. Otherwise it is *common*. While no
  peer is connected, availability is unknown, so missing pieces are *missing*,
  never unavailable.
- **Status.** Above the map, at the reading start of the first line, a short
  answer gives the conclusion, led by an icon in its InfoBar severity colour so
  it reads at a glance: Waiting for metadata, Complete, Can finish, Can't tell
  yet, or Can't finish yet. The reason is help, not the answer, so it is in the
  answer's tooltip and automation help text: connected peers have every
  remaining piece, no peers are connected, or no connected peer has a number of
  pieces, with the files they belong to. The reason says remaining, never
  missing, because the legend's Missing state means only pieces whose
  availability is unknown. Naming the files lets the user skip them and let the
  rest finish. A list of files, here and in the detail line, names the first
  three and counts the rest, so a long list cannot push the map out of view or
  fill the screen; Files lists every file.
- **Legend.** On the same line as the status, at the opposite end, as a chart
  places its key beside its title, each state shows its swatch, its name, and
  its count in semibold, as the Speed legend shows its values. Sharing the line
  saves a row of squares in the short inspector. The counts are the legend, so
  the two cannot disagree. Entries keep their own width with even gaps between
  them, because equal-width entries leave uneven gaps after the short ones. The
  legend stays on one line: in a narrow pane each entry keeps its swatch and
  count and trims its name, whose full text is in its tooltip and accessible
  name.
- **Detail.** The second line describes one square: its piece number or range,
  how many connected peers have its missing pieces that are not downloading,
  the count of each state it holds, each after a small copy of its legend
  swatch so the eye matches it to the legend without reading, and the files it
  belongs to. The peer count is the count for one piece, and the lowest and
  highest for several, because a merged square's colour cannot show that
  spread; while no peer is connected, it is unknown. The count reads only as
  peers, such as "87 peers", so the line keeps room for the rest; its tooltip
  says they are the connected peers that have the missing pieces. The line shows the square
  last pointed at while the pointer is over the map, otherwise the selected
  square, otherwise, while the map shows squares, a hint to point at or select
  one. A gap between squares keeps the last square, so the text does not
  flicker while the pointer crosses the map. The details sit in the page rather
  than in a tooltip, because a tooltip covers the squares beside the pointer and
  users read square after square. The range is in semibold and the rest in
  secondary text. How many pieces are verified out of the total, as in "2,300
  of 24,208 pieces", the piece size, and how many pieces each square holds sit
  at the end of the line in secondary text; the total alone does not say how
  far the download has come, and the per-square number is next to the square
  it explains. The line keeps its height and trims
  the file list instead of wrapping, so the map never moves while the pointer
  crosses it.
- **Squares.** Squares are all the same size and sit in groups of eight, so the
  eye keeps its place; the last group across and down may be shorter. The map
  fills the space under the detail line, because empty space under a short map
  looks unfinished. When the torrent has fewer pieces than fit, the squares grow
  to the largest size at which every piece still has its own square. Squares
  never shrink below the readable size: when the torrent has more pieces than
  fit, each square covers a contiguous range of pieces that differs from the
  others by at most one piece. When a row is full, the width left over by the
  last whole square widens the gutters between groups, so the map ends at the
  same edge as the line above it instead of leaving unexplained space; when the
  rows fill the height, the height left over widens the gutters between row
  groups in the same way. The map is aligned to the top. In a right-to-left language the first piece is at the top right,
  as a progress bar starts at the right.
- **Squares that cover several pieces** show the state most of their pieces
  have; on a tie the worse state wins, in the order unavailable, rare, common,
  missing, downloading, verified. A square that holds more than one state gets a
  small dot in its top-right corner, so the user knows its colour does not
  describe every piece. A square that holds an unavailable piece but shows
  another state also gets a dot in its bottom-left corner in the unavailable
  colour, so a piece that can stop the download is never hidden by a healthier
  majority. Its corner carries the meaning without colour.
- **Drawing.** Squares are tiles with the Fluent control corner radius, filled
  with Fluent theme colours and drawn without outlines: verified is solid
  accent, as the torrent's progress bar in the table, a downloading square fills
  with accent as its data arrives, so progress moves while the user watches,
  common is a light accent tint, missing is neutral, rare is hatched in the
  strong control stroke grey, and unavailable is crossed in the critical colour.
  Rare and unavailable keep the neutral fill, so the map reads as one accent
  progress picture and only the piece that can stop the download carries a
  warning colour. The hatch and cross are drawn as smoothly as XAML shapes. The
  solid fill, the partial fill, the hatching,
  and the cross keep the states apart without colour; missing needs no mark of
  its own, because it appears only while common, rare, and unavailable cannot.
  In High Contrast, system colours replace the fills, so every square also gets
  an outline, as Fluent controls do there.
- **Pointer and keyboard.** The map is one focus stop with at most one selected
  square, which a click and the arrow keys, Home, and End set; the first arrow
  key selects the first square, and Up and Down stay put at the first and last
  rows, as a Fluent grid does. The selection stays on the same pieces when a
  resize regroups the squares; leaving the view or choosing another torrent
  clears it with the map's data. The selected square keeps its accent outline,
  as Fluent marks selection, when the map loses focus, because the detail line
  can still describe it. Pointing at a square gives it a thinner outline without
  moving the selection, because a pointer passing over the map must not move the
  keyboard's place. A key press is the latest input, so it shows the selected
  square until the pointer moves again. The selected square's detail line is the
  map's UI Automation value, and Ctrl+C copies it; without a selection, the
  value is the status answer and its reason.
- **Drawing cost.** Draw the squares into one bitmap and redraw only when the
  data changes, because one element per square is too slow at thousands of
  pieces.
- **Data.** The [Pieces detail contract](../pipe-api.md#pieces-detail) owns the
  payload and its visible-view scope, so this view defines no second read path.
