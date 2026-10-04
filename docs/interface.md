# Interface

Target interaction and presentation for the new WinUI application. The
[architecture](architecture.md) owns product scope; the [engine](engine.md) owns
operation semantics. Existing TableView behavior follows its
[TableView contract](../lib/TableView/docs/tableview-contract.md). This document does not
claim a product UI is implemented or visually verified.

Common sense and intuitive use take precedence when choosing the interaction.
Make the ordinary path obvious and short, preserve the user's work, and handle
routine recovery automatically. Expose an error or choice when it changes what
the user can do, not merely because an internal state changed. The
[product principle](architecture.md#usability-comes-first) governs the guidance below.

## Design before implementation

Describe the affected layout, information order, commands, states, sizing, and
keyboard paths before writing production UI. Review them from user,
product-design, Microsoft Fluent, and keyboard-only perspectives. Use independent
review for substantial new journeys. Resolve concrete findings before
implementation, matching review depth to the change; a wording fix does not need
a new screen design.

Review both clutter and missing essentials: every visible element should support
a decision, and every required task needs a discoverable path. A successful build
cannot establish usability or visual conformance. Screenshots can verify a
design later; they do not replace understanding its interactions. Runtime checks
follow [testing](testing.md) and the repository's desktop-launch restriction.

## Native controls and visual authority

The interface must feel modern and lightweight: current Windows 11 visuals,
immediate response to input, and no decoration that costs attention or
responsiveness. Application-authored WinUI surfaces follow Microsoft's
[Fluent 2 principles](https://fluent2.microsoft.design/design-principles) as Windows
implements them: the
[Windows design guidance](https://learn.microsoft.com/en-us/windows/apps/design/)
for geometry, color, typography, materials, layering, and motion. A departure is
a defect unless this document or the TableView contract records it as an
exception with its reason. Lucide icons are the recorded exception for
iconography. Surfaces that Windows draws, including pickers, system dialogs, and
the tray menu, keep the appearance Windows gives them.

The owner selected the LabForms caption button template, dimensions and chrome
tint. These caption resources are an explicit visual exception because the app
actions must look like the adjacent Windows caption buttons. Keep native focus
visuals and automation. The caption logo uses the canonical SVG so it remains
sharp at different display scales; Windows shell icons use the canonical ICO.

Use the native platform's expression of Fluent. Web component APIs and examples
do not override WinUI's control semantics or require a second design system.

Use standard WinUI controls and documented Windows patterns for their semantics,
input, focus, automation, sizing, and states. Compose them before creating or
retemplating a control. A custom control must solve a concrete need and preserve
equivalent keyboard, theme, focus, and accessibility behavior.

The WinUI resource system is the visual authority. Reuse platform controls,
styles, type roles, brushes, and corner resources, then existing project styles.
A new semantic resource needs demonstrated reuse. A one-place spacing default
does not need a global token. TableView's stricter resource rules stay with its
contract. Do not add a palette, spacing framework, icon runtime, or parallel
styling system.

Build surfaces with Fluent layering: the window's base material, the platform
content layer above it, and platform card surfaces where a Windows pattern
groups content that way, such as a list of settings. Keep emphasis restrained;
do not add cards, borders, decorative headings, or repeated explanations as
decoration. Group related controls closely; separate distinct tasks. Preserve
the internal metrics of native controls. Choose local composition values from
multiples of four effective pixels and established project relationships, not a
screen-specific set of near-identical resources.

Keep existing coherent conventions unless a concrete problem warrants change.
Consult the current documentation for a materially affected control rather than
applying a general style rule against its semantics.

## Text, icons, and typography

Use concise, action-specific labels. Include the object when context does not
make it clear; use familiar OK and Cancel labels where their meaning fits.
Prefer a specific verb for a consequential decision. Clarity and natural
translation determine label length, without a fixed word limit.

Keep instructions needed to complete a task, errors, and decision information
visible beside the relevant controls. Avoid redundant explanations.
[Tooltips](https://fluent2.microsoft.design/components/web/react/core/tooltip/usage/)
provide supplementary help, available on keyboard focus as well as hover. Full
paths and other useful facts are available through focus, selection, copy, or
accessible descriptions, rather than hover alone.

Add an icon when it helps recognition or scanning; ordinary text buttons need
none. Keep Lucide for application-authored icons and retain platform-owned
control glyphs. Preserve native sizing, padding, hit areas, and button semantics;
do not replace a standard dialog button merely to add an icon. Decorative icons
stay out of automation; icon-only actions have explicit accessible names.

Use WinUI's system fonts, language-aware fallback, and standard type-ramp roles
and weights. Use sentence case and restrained emphasis. Preserve native control
text metrics and support text scaling rather than shrinking text to fit. Allow
text to wrap or expose the full value when trimming is appropriate. Text,
accessibility names, formatting, and direction follow the single
[localisation contract](localisation.md).

## Buttons

A button runs a command or opens a surface. A control that shows a setting, such
as the alternative speed limits or the Errors shortcut, is a toggle or a selection
control: it shows which state is current, and a button looks the same in every
state. Label wording and icon use follow
[Text, icons, and typography](#text-icons-and-typography).

- **One act, one name.** The same act has the same label, the same accelerator,
  and, where it shows one, the same Lucide icon in the toolbar, the context menu,
  and dialogs. Where [Main window](#main-window) names a command, that name is
  the label. A person who learns Pause once recognizes it everywhere, while two
  names for one act read as two acts.
- **The accent marks the default button.** The platform's accent style goes on a
  surface's default button, the one Enter runs, and every other button keeps the
  standard style. The default is the commit, such as Add or Save, except in the
  Remove and Delete files dialogs, where Cancel is the default so that Enter cannot
  remove a torrent or delete data. A surface with no commit has no accent. The
  accent then never points at an act
  that Enter does not run, and a row of accents never hides the main action.
- **An icon-only button names itself in a tooltip,** with its accelerator, on
  keyboard focus as well as on hover. With no visible label, the tooltip is where
  a person learns what the icon does.

## Windows, layout, and themes

Design against available window space and content pressure, using effective
pixels. Support monitor/DPI changes, long translations, text scaling, and the
minimum usable window size. Essential actions remain reachable when content
overflows. Do not introduce a second layout framework or a phone breakpoint into
a desktop task.

WinUI owns its saved window placement. Restore against current monitor work
areas and DPI, recovering a reachable position and usable size. Preserve normal
Windows move/resize behavior, caption buttons, and title-bar accessibility.
Use documented title-bar and backdrop APIs. The product window uses desktop
acrylic as its base material, with the platform's fallback where acrylic is
unavailable. Microsoft recommends Mica for a window's base, but the owner chose
acrylic so that the window shows a blurred view of what is behind it. Menus,
flyouts, and other transient surfaces keep the platform's acrylic. A material
is never a substitute for readable content or hierarchy.

Use theme resources for values that change with Light, Dark, or contrast themes.
Respect system accent, contrast, animation, and text preferences. Preserve the
expected rest, hover, pressed, selected, focused, disabled, loading, and error
states of standard controls. Meaning must survive without color or motion.

Motion is the platform's: built-in control animations and WinUI theme
transitions with their own durations and easing. Do not add another animation
system or decorative motion. Motion never delays input or a state change. When
the user turns animations off in Windows, the same changes stay clear without
substitute motion.

Required contrast is 4.5:1 for ordinary text and 3:1 for large text and essential
non-text indicators such as focus and selection. Measure actual adjacent colors
in relevant rendered states; a resource name does not prove contrast. Inactive
controls are exempt from the minimum non-text ratio. Custom touch targets follow
Windows sizing guidance where touch is supported; do not enlarge every dense
desktop control mechanically.

## Keyboard and accessibility

Every meaningful pointer action has a keyboard path. Use native focus groups,
arrows, Home/End, access keys, and accelerators; reaching every element by Tab
alone is insufficient. Focus order follows the task. Dialogs and menus return
focus predictably, including when resizing replaces the original invoker.

Keep a visible native focus indicator at the actual keyboard location, distinct
from selection. Correct misplaced focus at its owner rather than hiding the
indicator. Follow Microsoft's
[visual feedback guidance](https://learn.microsoft.com/en-us/windows/apps/develop/input/guidelines-for-visualfeedback).

Input precedence is the composing editor or popup, then the active dialog/view,
then the shell. Editors retain text selection, clipboard, undo, cursor, Delete,
Enter, and Escape behavior. A Delete inside an editor cannot remove a torrent;
Enter in a multiline editor cannot commit the entire form. Follow
[composition protection](localisation.md#ownership-and-live-behavior).

F6/Shift+F6 may move between substantial task regions; use it where that reduces
work. Lists and navigation are composite focus groups. Hidden, disabled, and
decorative content does not add focus stops. Expose accelerator metadata and
implement the same behavior; a UI Automation description alone is not a shortcut.

Give controls persistent labels and matching accessible names. Expose useful
roles, values, states, headings, and relationships. Row identity is its torrent
name, relative file path, peer endpoint, or tracker URL, not a CLR type name or
every changing numeric value. Announce operation outcomes once without stealing
focus; do not announce every transfer tick. Graphs and maps have equivalent
textual facts.

## Committing edits

Apply independent choices when the user commits them. A switch applies when
flipped; a selection applies when chosen. Validate text and numbers before
committing on Enter or an appropriate focus departure. Escape cancels unfinished
field input; typing, IME composition, or clicking Cancel must not accidentally
submit partial text. Show pending work and field errors in place. This follows
the distinction in Microsoft's [toggle guidance](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/toggles).

Use an explicit Save/Cancel editor only when values form one coherent change,
such as a tracker list. Keep its draft until submitted or cancelled. Viewing
details, changing a setting that has already applied, and ordinary navigation
do not create a dirty page. Ask Save/Discard/Cancel only when leaving would lose
actual unfinished input; Cancel keeps the editor and focus. A pending accepted
command is engine work, not an unsaved draft requiring another confirmation.

Send only intended changes through the [engine's edit path](engine.md#committed-edits).
Refresh confirmed facts without replacing the user's current input. On refusal,
keep that input and explain the actionable reason at the affected control.
Reconnection preserves unfinished input but does not submit it automatically.
Language selection retains its immediate, in-place behavior.

## Product journeys

### Main window

The download window extends acrylic content into its title bar, following the
LabForms title-bar layout and caption-matched button template. Icon-only commands
sit beside the native minimize, maximize and close buttons, using the same
32-pixel height, 46-pixel hit width and hover treatment. They retain accessible
names, keyboard shortcuts and focus cues. A theme button switches Light/Dark in
place; a language button uses the current language abbreviation, never flags,
and switches through the same language preference as Preferences. One chrome
row leaves the torrent table more space and avoids a separate Windows caption
above the commands. Native dragging, resizing and caption semantics remain.
The first window has a 720 by 560 effective-pixel minimum, updated for monitor
DPI. The application name trims within the space left by the commands and
caption buttons, so enlarging text cannot push those controls out of reach.
Add torrent file opens the native picker first; cancelling returns to the table
without a draft. The selected source then opens the Add form for destination,
Start paused and Add/Cancel, following the Show the Add form preference.

The torrent table is the primary workspace, with an optional inspector and
focused Add and Preferences tasks. Text search finds a known torrent; an Errors
shortcut isolates torrents that need attention and shows when errors exist.
Status and progress remain visible and sortable in the table. These serve finding,
troubleshooting, and scanning without a permanent filter sidebar or status and
tracker dropdowns taking space from the primary workspace. Tracker information
belongs in the selected torrent's inspector. TableView owns generic interaction.
The application menu contains global actions. The three-dot command overflow
contains secondary actions for the selection, while the row context menu also
offers the primary selection commands. Both call the same command owner.
Properties belongs only in the row context menu, for one selected torrent while
the inspector is closed; omitting it otherwise avoids an action with no effect.
Disable the three-dot menu when no torrent is selected, so its scope is clear.
Show the selected count at the top of command overflow; the row context menu names
the torrent for a single selection. Clicking
outside dismisses it; Escape dismisses it and returns focus to its invoker. Use
native MenuFlyout behavior, because command menus should respond as Windows users
expect.

Preferences keeps the caption identity and shows its page name in place of
torrent commands. Clicking TinyTorrent or choosing Torrents in the application
menu returns to the table, preserving selection and the inspector. This avoids
a separate return row consuming content space. The caption logo reveals the
application menu glyph on hover and keyboard focus, following Forge's chosen
interaction; omit the flip when animations are disabled so the affordance stays
clear without motion.

The table starts with Name, Size, Progress, Status, Down speed, Up speed, ETA,
Ratio, Seeds/Peers, and Added; the person can hide, show, and reorder them.

Torrent commands are Pause, Resume, Force start, Open, Open folder, Copy magnet
link, Copy info hash, Move, Verify, Remove, and Delete files. Open hands the file
of a single-file torrent, or the folder of a multi-file torrent, to Windows as
Explorer does, only on the person's request. Double-click and Enter on a row
open the inspector; Properties in the torrent context menu does the same. The
panel has a Close action rather than an ambiguous toolbar toggle, because its
entry points already identify the torrent being inspected. Add torrent file opens
the native picker, and Add magnet
link opens a field for the link even when the Add form is turned off; the
source then follows the Show the Add form preference. Pause all and Resume all
are in the window and the tray, and keep each torrent's own
[paused or running state](engine.md#state-and-work). Exit is in the window as
well as the tray.

Dropping torrent files or magnet text on the window, or pasting them with Ctrl+V
while the table has focus, opens Add with those sources. An empty list says how
to add a torrent. A status bar shows total download and upload speed, the
alternative speed toggle, whether incoming connections arrive or the selected
network interface is absent, and Update available when a newer release exists.

Shortcuts match qBittorrent's, so people who move from it keep their habits.
TinyTorrent has no Print, Save, or Refresh command, so Ctrl+P, Ctrl+S, and
Ctrl+R serve torrent actions as they do there. Torrent shortcuts act while the
table has focus; an editor keeps its own keys.

| Key | Action |
| --- | --- |
| Ctrl+O | Add a torrent file |
| Ctrl+Shift+O | Add a magnet link |
| Ctrl+V | Add the pasted magnet link or torrent file |
| Ctrl+F, Ctrl+E | Text filter |
| Ctrl+A | Select all torrents |
| Enter | Open the inspector, as double-click does |
| Ctrl+S | Resume |
| Ctrl+P | Pause |
| Ctrl+M | Force start |
| Ctrl+R | Verify |
| Ctrl+Shift+S | Resume all |
| Ctrl+Shift+P | Pause all |
| Ctrl++ / Ctrl+- | Move up / down in the queue |
| Ctrl+Shift++ / Ctrl+Shift+- | Move to the top / bottom of the queue |
| Delete | Remove |
| Shift+Delete | Delete files |
| Alt+O | Preferences |
| Ctrl+W | Close the window |
| Ctrl+Q | Exit |

Remove keeps data; delete-data is an explicit, distinct decision. Each confirms
once with the affected torrent names or count, a specific action such as Remove
or Delete files, and a safe Cancel action. Remove confirms because a removed
torrent cannot be restored without its torrent file or magnet link. Delete files
also states the file scope, that deletion is permanent, and that files other
torrents use are kept. Deletion bypasses the Recycle Bin,
because people delete a torrent's files to free disk space. The dialog
opens with focus on Cancel, so Enter cannot delete data by accident. Use
standard [ContentDialog buttons](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/dialogs-and-flyouts/dialogs).
Routine pause, resume, and applied settings need no confirmation.

Retain recognizable identity and last-known read-only values on disconnect, mark
them stale, and disable writes until the engine is available. Current speed is
unknown while disconnected and displays an em dash; old rates must not appear
current. Show loading separately from an empty list. Progress fills its column
and uses distinct paused and error brushes alongside the written status, so state
is visible without depending on color. Routine outcomes are announced to assistive
technology without a visible toast; actionable failures appear in the affected
message bar or field. Pending operations
and failures remain visible without exposing internal protocol machinery.
Reconnection preserves presentation state according to the protocol contract.
After reconnecting, show the confirmed list; do not leave a permanent busy state.
A missing row means the torrent was removed, not that its files were deleted; a
deletion failure arrives as a notification.

### Add

The task answers: what is being added, which files are wanted, and where they go.
Use native source/destination pickers and an editable magnet input with an
explicit Paste action. Read the clipboard only after the relevant user action.
Preserve accepted input and choices when a picker is cancelled or a replacement
source fails; successful replacement deliberately starts a new preview.

Show preview progress immediately, keep cancellation available while acquiring
metadata, and expose file choices when metadata is ready. The engine supplies
that metadata; the UI does not parse torrents or impose an old client's
file-choices-only-after-add limitation. Unknown metadata is an explicit state,
never an invented file list or silently started payload transfer. Add is
available before magnet metadata arrives, so a slow swarm does not hold the
person in the form; the torrent then wants every file, and file choices move to
the inspector's Files view.

Several sources added together, by multi-selection in the picker, by opening
several files from Explorer, or by one drop, share one form: their names and
sizes, one destination, Start paused, and Add all. A source already in the
list is marked Already added, with the offer to merge its trackers. Sources that
arrive while the form is open join it. File choices for each torrent move to the Files view.
Thirty torrents are one form, not thirty. The form has a
Never show again check box, which turns off the Show the Add form preference
where the person meets the form.

Search within files changes visibility, not wanted choices. Bulk selection has
an explicit scope. Folders form an expandable hierarchy. A folder's wanted checkbox
and priority apply to every descendant, including filtered or collapsed files.
Folder priority offers Leave unchanged to preserve individual child priorities;
Normal, High, and Low apply once to all descendants. Mixed wanted choices show
an indeterminate checkbox. Bulk Select all/none acts on files matching the
search. The same file browser serves Add and the inspector, so these rules have
one owner. Keep its summary, search, and bulk actions on one compact row and give
the list the remaining viewport, because files are the task's primary content.
Keep file identity and selected bytes clear; when a known list
has no wanted files, explain why Add is unavailable. The destination starts from
the default download folder, initially Windows' Downloads known folder, and remains changeable through a native picker. A failed
free-space check must not be presented as proof of an invalid folder.

Keep one form with a reachable native footer. A long body scrolls; the virtualized
file list has a finite viewport and owns its collection scrolling. Do not create
another page or state model solely to accommodate a short window.

Commit captures source and choices once, indicates pending work, and does not
promise that Cancel can undo an accepted command. A confirmed addition reveals
the torrent. A duplicate preserves the existing torrent's saved choices and data
and offers a path to it; when the new source lists trackers the existing torrent
lacks, it offers to add them. A rejected choice returns to the relevant field;
an uncertain outcome follows the protocol's reconciliation rules.

A new torrent may use files another torrent already uses, such as the same
content from a second tracker; the [shared-files policy](engine.md#shared-files)
allows it, and verification reuses the files. Pieces that do not match are
downloaded again over those files, so the form names the torrents that already
use files at the chosen destination before Add. To replace an older torrent, the
person removes it, keeping its files, and adds the new one; the initial release
has no automatic Replace action.

### Preferences

Group settings by user task and the actual libtorrent product, not the old
daemon's fields or fixed categories. Background choices have one engine owner;
UI-only preferences have one WinUI owner. There are no remote profiles or
connected-server scopes in this local product.

Use the [commit rules](#committing-edits): ordinary settings apply individually,
with no page-wide Save step or confirmation on close. Reveal dependent fields
when relevant; keep an explicit editor's actions reachable. Native navigation
and scrolling handle smaller windows. Preferences uses a full page with horizontal
category selection and grouped sections, so settings have room without obscuring
the task. Returning to torrents preserves selection and the inspector view.
Preferences keeps this LabForms layout independently of the torrent inspector's
vertical tabs. Put Browse beside the default download path,
and beside Add's destination, using the native Windows folder picker. Cancelling
the picker preserves the current path and other unfinished input.
Do not show an engine field dump. Disk
caching remains [automatic engine policy](engine.md#disk-write-caching), not a
Preferences choice.

Include, grouped by task: the default download folder and Show the Add form;
global and alternative speed limits; queue limits for active downloads and
seeds; seeding ratio and time limits; connection limits; the network interface,
port mapping, and listen port; completion notifications; preventing idle sleep
while downloading on mains power, and also while seeding; Check for updates,
following the [update model](architecture.md#installation-and-updates); and
language. Each uses the existing settings path. Reaching a seeding limit pauses
the torrent; nothing is removed without a request. Resuming that torrent by
hand lifts the limit for it, so it seeds on as asked instead of pausing again.
The sleep switch names its mains-power condition, so a laptop that sleeps on
battery does not surprise its owner.

Scheduler presents one weekly overview with normal limits, alternative limits,
and paused periods, because separate schedules obscure their combined effect.
Each period has start days, start/end times, and a choice of alternative limits
or pause, edited with native checkboxes, TimePicker controls, and radio buttons.
Normal limits apply outside periods; pause takes precedence on overlap. Overnight
periods end on the following day. The schedule is disabled by default and repeats
in local time. Its engine owner preserves individually paused torrents and manual
Pause all, so a scheduled boundary cannot undo the person's explicit pause.

Preferences offers one Start when I sign in switch and an Open torrents with
TinyTorrent action covering `.torrent` files and magnet links. These call the
engine's [registration owner](engine.md#windows-registration). The latter
finishes automatically when TinyTorrent is already the default; otherwise it
takes the person to the supported Windows choice and refreshes on return. Keep
Remove TinyTorrent as a handler available too. Present mixed file/link defaults
in ordinary language only when they need action, without a registry-status panel.

Keep a Windows Startup settings link beside the sign-in control for Windows'
independent override. Open the relevant [Windows Settings page](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings),
using the general page when a more specific route is unsupported. Show failures
beside the affected action and preserve the user's work. Routine successful
changes need neither a confirmation dialog nor a technical explanation.

### Inspector and edits

General, Files, Peers, Trackers, Speed, and Pieces answer different questions;
request data only for the visible view. Preserve useful data coverage and choose
each layout for its task. The Pieces view is the [Pieces map](#pieces-map).
Peers and Trackers use the existing TableView, sharing its header, selection,
column, keyboard, and scrolling behavior with the main torrent table. Files uses
a native TreeView with wanted, size, progress, and priority content, because
folders require hierarchy and TableView's contract excludes tree rows. Reuse the
file browser in Add; do not extend TableView with torrent-specific tree behavior.

Apply individual choices and explicit file commands through the same commit
rules. When a coherent edit needs a draft, keep one active editor bound to the
torrent identity; protect only its unfinished input when changing context or
closing. An untouched inspector and an already-applied change never trigger a
save prompt. A removed target cannot receive a write.

Move moves the torrent's own files; other files in its folder stay. Show the
folder the files will be in, so choosing the torrent's own folder instead of
the folder that contains it is visible before the move. Preserve choices on
failure. When files are already at the destination, offer Use the files there,
following [engine relocation](engine.md#removal-and-relocation). When other
torrents use the files, name them and offer to move them together.

The Speed view shows the [engine's session-wide speed history](engine.md#state-and-work),
whichever torrent is selected, and its heading says All torrents, so nobody
reads it as the selected torrent's speed. It continues while WinUI
is closed, so reopening shows what happened meanwhile. Offer the last five
minutes and the last 24 hours. Unknown gaps, such as the time before an engine
restart, are not interpolated into invented history. Provide current/peak text
alongside a chart.

One page owner allocates table and inspector space. Remember an explicit split
adjustment within current usable bounds; the splitter is keyboard-adjustable and
reports its range. The window's minimum size fits the table and the inspector at
their minimum heights, so there is no second layout for small windows. Preserve
selection and the requested split; do not hide an inspector while continuing its
detail work.

### Pieces map

The Pieces map answers two questions: how far the download has come, and whether
it can finish. It states the answer in words, because a grid of colours alone
leaves the user to work out the conclusion. Square size, the drawing of squares
that cover several pieces, the rare limit, and the colours follow the previous
TinyTorrent map; change them only when the
[implementation review](#implementation-review) shows a better choice in use.

- **States.** A piece is *verified* when the engine has checked it, and
  *downloading* while it is being received. A missing piece is *unavailable*
  when no connected peer has it. It is *rare* when the connected peers that have
  it number at most 15 % of those that have the torrent's best-available piece,
  rounded up and never less than one, because a fixed peer count means something
  different in a large swarm and a small one. Otherwise it is *common*. While no
  peer is connected, availability is unknown, so missing pieces are *missing*,
  never unavailable.
- **Status.** Above the map, one sentence gives the conclusion: Complete, Waiting
  for metadata, No peers connected, All missing pieces are available, or the
  number of unavailable pieces and the files they belong to. Naming the files
  lets the user skip them and let the rest finish.
- **Legend.** Under the status, one row shows each state with its swatch and its
  count, then the piece count and piece size. The counts are the legend, so the
  two cannot disagree. The row wraps when the panel is narrow.
- **Squares.** Squares keep one readable size and sit in groups, so the eye
  keeps its place. They never shrink: when the torrent has more pieces than fit,
  each square covers an equal, contiguous range of pieces. The map is centred
  and aligned to the top. In a
  right-to-left language the first piece is at the top right, as a progress bar
  starts at the right.
- **Squares that cover several pieces** show the state most of their pieces
  have; on a tie the worse state wins, in the order unavailable, rare, common,
  missing, downloading, verified. A square that holds more than one state gets a
  small triangle in its top-right corner, so the user knows its colour does not
  describe every piece.
- **Drawing.** Use Fluent theme colours. A downloading square shows how much
  of it has arrived, so progress moves while the user watches. Fill, hatching,
  and border keep every state readable without colour.
- **Pointer and keyboard.** The map is one focus stop. Pointing at a square, or
  moving to it with the arrow keys, Home, or End, shows a tooltip with its piece
  numbers, the files they belong to, the count of each state, and, for a single
  missing piece, how many connected peers have it. The same text is the map's
  UI Automation value, and Ctrl+C copies it.
- **Drawing cost.** Draw the squares into one bitmap and redraw only when the
  data changes, because one element per square is too slow at thousands of
  pieces.
- **Data.** While the Pieces map is visible, the engine sends the verified
  pieces, the connected-peer count for each piece, and the pieces being
  downloaded with their received share. Each time the map opens, it also sends where each file starts in the piece
  sequence. Nothing is sent while the map is hidden, because availability is one
  number per piece and a large torrent has tens of thousands of pieces.

## Implementation review

Verify the affected journey at relevant sizes, themes, text scales, and input
methods under [testing](testing.md). Review native semantics, reachable actions,
focus return, automation, live text, and bounded hidden work. State the evidence
and gaps explicitly; a static screenshot cannot establish all of them.

[Historical reviews](archive/interface-review.md) preserve earlier
experiments. Their approvals, old shortcuts, Transmission limits, and unsupported
features do not expand this contract or replace a design for the actual product.
