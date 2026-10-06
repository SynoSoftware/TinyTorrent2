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

The logo uses the canonical SVG so it remains sharp at different display scales;
Windows shell icons use the canonical ICO. Windows owns the caption buttons.
Application commands use native WinUI controls and their standard states.
The title-bar theme shortcut keeps the existing custom caption-button style:
full caption height, square hover surface and caption-button width, so it feels
part of the window chrome. This is the exception to standard button visuals.

Use the native platform's expression of Fluent. Web component APIs and examples
do not override WinUI's control semantics or require a second design system.

The visual and interaction reference is `app/prototype.html`, variant
C, commit `8614126` on `main`. Preserve its compact table, collapsible status
drawer, caption search and Preferences composition.
Implement them with native WinUI controls and the existing TableView and command
owners. The owner's inspector-section decision below supersedes the prototype's
vertical inspector sections, and the owner's MenuBar decision supersedes its shell
and navigation. Keep the prototype unchanged; it is a historical reference,
not an implementation target for those parts. Prototype sample data and browser
code are not production architecture.

**Owner's decision: keep the native WinUI MenuBar.** It is the discoverable home
for commands and replaces the application navigation pane. Its dropdown commands
use Lucide icons and separators between distinct action groups; menu headings
retain native text presentation.
In a menu that mixes choices with commands, a choice shows its state as its
icon: a checked, empty or mixed square. It is not a check-mark item there,
because one check-mark item gives every item in its menu a second icon column
that is empty beside the others. A menu of only on/off choices, such as View,
uses native check-mark items with Lucide icons, because no column stands empty.

The custom title bar contains one row: app icon, File/Torrent/View/Help MenuBar,
bounded Search, Add torrent file and Add magnet link, a small separator, the
light/dark switch, and native caption buttons. Resume, Pause, Verify and Remove
stay in the Torrent menu, not the header. Each button runs the same command as
its menu item. Keep the existing
custom title bar; do not replace it with the WinUI TitleBar control. A 24-pixel
icon and 32-pixel Search sit within a 48-pixel row so the content has breathing room.

**Owner's decision: retain the custom toolbar implementation.** Compose ordinary
Buttons with the existing TinyTorrentCaptionButtonStyle, not CommandBar or
AppBarButton. All app-side header actions share its size, spacing and states;
the theme button sits immediately beside the native caption buttons, with no
extra gap. CommandBar's separate button metrics break that visual consistency.
Windows still owns minimize, maximize and close; never simulate those controls.

Inset the icon, separate logical groups, and reserve Windows' caption insets plus
a command buffer. Search is at most 320 effective pixels wide. The minimum window
width accommodates the measured menus, a 200-pixel search and the caption buttons,
including translated labels. Unused title-bar space retains native dragging,
double-click maximize/restore and the system menu; controls receive client input.
The app icon is a native system-menu region: left-click or right-click opens
Windows' Restore/Move/Size/Minimize/Maximize/Close menu, double-click closes the
window, and Alt+Space opens the same menu. Windows owns these caption semantics;
the icon does not open an application command menu.

There is no application navigation pane, identity text, command overflow,
language switch or permanent Exit button. Language and theme
are chosen in Settings; the title-bar light/dark shortcut uses the same theme
owner. Torrents is the normal workspace; Settings and About
have a contextual Back button and Alt+Left route to it, with the existing draft
guards. Keep continuous acrylic.

A second UI launch forwards Open to the existing application. Check its outcome
before exiting. If forwarding fails before a WinUI window exists, a native
Windows error dialog explains the failure; it does not create a second workspace
or block the engine.

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
make it clear. Button labels follow the one-word ruling in
[Buttons](#buttons); a menu item is read in a vertical list and may name its act
with a verb phrase.

**Owner ruling: help text is forbidden on work surfaces.** Help text explains
what a control or choice does. It appears only on a surface dedicated to help
and in
[tooltips](https://fluent2.microsoft.design/components/web/react/core/tooltip/usage/),
available on keyboard focus as well as hover. A work surface, such as the main
window, a form, a dialog or Settings, shows labels, values, states and errors.
Labels and the layout carry the meaning, and a sentence that explains them is
help text. Errors stay visible beside the control they concern.

**Owner ruling: work-surface text does not wrap, and no line is left empty to
separate text.** A label, value or message takes one line. A value too long for
its space trims with an ellipsis, and its full text is available through a
tooltip, focus, selection, copy or the accessible description, rather than
hover alone, so trimming is always preferred to wrapping. Spacing and grouping
separate content, not blank lines. Wrapping changes a line's height with its
text, which shifts everything below it as the text, the language or the window
width changes. A person scans a surface by its lines, and wrapped or empty lines
break that scan.

Every button carries a Lucide icon, as [Buttons](#buttons) rules; elsewhere,
add an icon when it helps recognition or scanning. Keep Lucide for
application-authored icons and retain platform-owned control glyphs. Preserve
native sizing, padding, hit areas, and button semantics. Decorative icons stay
out of automation; icon-only actions have explicit accessible names.

**Owner ruling: an application-authored icon is larger than the text beside it,
and it never enlarges its container.** The icon may extend into the container's
padding but never into its margin, so it does not touch neighbouring controls.
This holds for every Lucide icon in the product. Icon size is part of the
product's visual language: a larger icon is found at a glance, and rows and
buttons with icons keep the same height and alignment as those without.
Platform-owned glyphs, such as menu-item icons and control chevrons, keep their
native size.

**Owner ruling: everything on one line shares one vertical centre, and shared
styles decide it, not individual elements.** Text is centred by the middle of
its letters, halfway between the baseline and the top of the capitals, whatever
its size; an icon by the middle of its glyph; a check box by the middle of its
box. App.xaml's implicit styles and the shared controls own this. A screen does
not set vertical alignment, line bounds, margins or padding on one element to
move it into line: when a line is out of true, the fix goes into its shared
owner, so it reaches every screen at once. A person reads a line as one unit,
and a nudge on one element fixes one line while the next one stays wrong.

Use WinUI's system fonts, language-aware fallback, and standard type-ramp roles
and weights. Use sentence case and restrained emphasis. Preserve native control
text metrics and support text scaling rather than shrinking text to fit. Text,
accessibility names, formatting, and direction follow the single
[localisation contract](localisation.md).

Choose text by its role, not by the screen: page titles use the Title role;
dialog and form titles use Subtitle; group headings use Body
Strong; field labels, values, commands and status text use Body. Secondary facts
such as free space keep that body size and use the secondary text brush instead
of smaller type. App.xaml owns the shared title and body styles.
Equivalent groups use the same heading treatment and content inset, so a person
can recognise the hierarchy without learning a different visual language in each
pane. Native controls keep their internal spacing; a form does not compensate
for it with negative margins.

## Buttons

**Owner ruling: dialog buttons keep their natural width. When the dialog has
supporting content for the button row, such as a status or a check box, that
content holds the left of the row and the buttons sit at the right; when it has
none, the buttons are centred.** ContentDialog's own template stretches its
buttons across the row, which on a wide dialog draws two buttons of about 480
pixels and leaves no place for a check box beside them, while right-aligned
buttons beside an empty left look misplaced. Every dialog therefore uses the
shared Dialog control, which draws the row this way from whether its Footer is
set, with less padding above and below. The buttons keep the Windows order: the action first, then Cancel. A
dialog opens with focus on its first text box that leaves Enter and Escape to
the dialog, or otherwise on its default button, so the person can type or press
Enter at once.

**Owner ruling: a button is one word, one leading Lucide icon, and a tooltip
that carries everything the word does not say.** The three are one rule. The
word is one that people already know from other software, such as Add, Remove,
Move, Browse or Retry, never a synonym coined for this screen. A second word is
allowed only when one word would leave the person unsure which of two commands
the button runs, and the code says why. The rule governs the visible label:
tooltips, accessible names and menu items keep the words they need. A label is
one line at every width and never wraps. One familiar word with its icon is
read at a glance, while a label that is a phrase has to be read, and a tooltip
keeps the scope and the consequence that the word drops. The shared
`ActionButton` draws the icon before the word; every labelled button, the
shared Dialog's included, is one.

**Owner ruling: Fluent, not rainbow.** A button rests in the standard neutral
style, and colour is rare and means something: the accent marks the default
button, as below. A destructive button takes no alert colour; its word and its
confirmation carry the meaning, so colour is never the only signal.

**Owner ruling: a confirmation reads once, top to bottom.** The title asks the
act, such as Remove torrents?. The body adds only the facts of what the act
touches: its identity first, then what the title does not say, in words that do
not repeat the title. An irreversible consequence of a destructive act stays in
the body as one short line, because a tooltip must never carry the only warning;
everything else the act does goes to the confirm button's tooltip. A short
confirmation draws no panel around its text, because the dialog's frame,
spacing and buttons already separate its parts. A confirmation with no fact
beyond its title and buttons has no body.

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
- **An icon-only button is subtle.** It uses App.xaml's `SubtleButtonStyle`:
  no fill or border until pointed at, as the window's caption buttons are. A
  pane's Close then looks like the window's Close, and one act does not look
  like two different controls.

## Windows, layout, and themes

Design against available window space and content pressure, using effective
pixels. Support monitor/DPI changes, long translations, text scaling, and the
minimum usable window size. Essential actions remain reachable when content
overflows. Do not introduce a second layout framework or a phone breakpoint into
a desktop task.

WinUI owns its saved window placement. Restore against current monitor work
areas and DPI, recovering a reachable position and usable size. Preserve normal
Windows move/resize behavior, caption buttons, and title-bar accessibility.
On Close, WinUI saves normal bounds, their display scale, maximized state, the
requested inspector split and the three tables' public layout snapshots in
`window.json` beside the engine data. It also saves the view: the page and
Settings category, the filter and whether the filter pane is open, the selected
torrents and current torrent, whether the inspector is open and on which
section, and the torrent table's scroll position. Reopening the window shows it
as the person left it; a saved torrent that no longer exists is left out of the
selection. The view is restored once, from the first snapshot whose storage
loaded. A window closed before then saves nothing, so an Exit during startup
does not replace the saved view with defaults. The engine does not read or
write this file.
A missing or damaged layout uses the declared defaults; a layout write failure
does not keep the window or engine open. Layout recovery never changes downloads.
Use documented title-bar and backdrop APIs. The product window uses desktop
acrylic as its base material, with the platform's fallback where acrylic is
unavailable. Microsoft recommends Mica for a window's base, but the owner chose
acrylic so that the window shows a blurred view of what is behind it.
The caption and workspace share that same continuous acrylic surface and tint;
neither has an independent opaque fill or a contrasting title-bar band. Native
control states and transient surfaces keep their own necessary layering.
Menus, flyouts, and other transient surfaces keep the platform's acrylic. A material
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
submit partial text. Show pending work and field errors in place. A setting
saves too quickly for a pending display to help, so its control stays enabled,
keeps focus, and shows nothing until the save fails. This follows
the distinction in Microsoft's [toggle guidance](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/toggles).

Use an explicit Save/Cancel editor only when values form one coherent change:
a tracker list, a schedule period, the Add form, or moving or deleting files.
Keep its draft until submitted or cancelled. Viewing details, changing a setting
that has already applied, and ordinary navigation do not create a dirty page. A
pending accepted command is engine work, not an unsaved draft requiring another
confirmation.

**Owner ruling: a setting applies immediately and never asks.** Fluent 2 and
Microsoft's app settings guidance apply a setting without a Save or confirmation
step. Leaving Settings or closing the window applies each valid typed value, as
moving to another field does, and restores the saved value of an invalid one, as
WinUI's NumberBox does by default. If the engine refuses a valid value, the
person stays on Settings with the error beside the field, because the error
would otherwise be on a page they can no longer see.

An unavailable engine is different from a refused value. Back preserves unfinished
ordinary Settings input in the window and leaves the page; closing the window may
discard that input without a prompt. Neither operation waits for reconnection,
because ordinary Settings must not trap the person in an unavailable application.
A picker or an operation already in progress finishes before close continues.

**Owner ruling: leaving an explicit editor with unfinished input asks Save,
Discard or Cancel.** This is the familiar Windows choice for unsaved work, and it
keeps the input without making the person return to the editor first. Save runs
the editor's own action, such as Save, Add, Move or Delete; if it fails, the
editor stays open with its error. Discard drops the draft and continues. Cancel
keeps the editor and focus. Deleting files still shows its own confirmation of
what will be deleted.

On window close, ask only for work the person has actually entered: a torrent
source, changed trackers or file choices, a changed schedule period, or a move
destination. Opening an untouched editor or changing Add or Move options without
a source or destination does not justify a question. The question names the
unfinished work and its primary action: Add, Move, Save period or Save changes.
Its text explains what Discard drops and that Keep editing cancels closing the
window. Keep editing is the default button, so Enter cannot unexpectedly add a
torrent, move files or discard input while the person is trying to close.

Send only intended changes through the [engine's edit path](engine.md#committed-edits).
Refresh confirmed facts without replacing the user's current input. On refusal,
keep that input and explain the actionable reason at the affected control.
Reconnection preserves unfinished input but does not submit it automatically.
When the engine is unavailable, Restart is reachable inside an open Add draft
as well as the main window. A modal editor must not cover the
only recovery command and force the person to discard input to reach it.
Language selection retains its immediate, in-place behavior.

## Product journeys

### Main window

The download window extends acrylic content into its custom title bar.
The File menu contains Add torrent file, Add magnet link, Settings and Exit.
Torrent contains the existing selection commands, queue actions, Pause all,
Resume all and Speed limits. **Owner ruling:** Speed limits opens Settings at
the speed limits, as its search result does; there is no separate limits dialog,
because two editors for one setting disagree about when it applies. Commands
retain their selection availability and shortcuts. View contains checkable
Filters and Toolbar items; Help contains About. Every menu item has a localized
access key that is unique in its menu, as in other Windows applications: in
English, Alt+F, X exits.
Add is available in File and through the existing keyboard and search paths.
The first window has a 560 effective-pixel minimum height; its minimum width
keeps the complete title-bar row usable, starting at 720 effective pixels.
Native dragging, resizing, caption semantics and DPI behavior remain.
Add torrent file opens the native picker first; cancelling returns to the table
without a draft. The selected source then opens the Add form for destination,
Start paused and Add/Cancel, following the Show the Add form preference.

The torrent table is the primary workspace, with an optional inspector and
focused Add and Preferences tasks. The caption contains native search for
torrents, commands and settings. Selecting a result reveals its torrent, runs
the existing command, or opens and focuses the named preference without changing
it. Typing this global search does not silently filter the torrent table.
Ctrl+K, Ctrl+F and Ctrl+E focus it. Scope labels distinguish a command for the
selection from a command for all torrents.

The Filters toggle opens a collapsible native pane with All, Downloading,
Seeding, Paused, Queued and Errors choices and live counts. Closing the drawer
keeps the chosen filter, identified in View > Filters and a status label.
Escape closes the drawer when focus is inside it. Clear filters lives inside
the drawer and calls the existing filter owner. Downloading includes metadata
acquisition, Seeding includes completed torrents and Paused includes session
pause. The Errors shortcut in the status bar selects the same Errors filter;
there is one filter owner. The drawer starts closed so the table keeps its full
width until the person asks to filter. Status and progress remain visible and
sortable in the table. Tracker information
belongs in the selected torrent's inspector. TableView owns generic interaction.
Settings opens from File and About from Help. Exit is a File command and keeps
its existing pending-work and draft guards. Keyboard and search paths invoke
the same owners. About shows the product identity and running version on the
same acrylic surface. The current page belongs to the main view model.
Leaving Settings follows the two owner rulings in
[Committing edits](#committing-edits). The inspector keeps its target and draft
while another page is visible. The Torrent menu and row context menu share
selection commands.
Properties is available in Torrent for one selected torrent, and in its row
context menu while the inspector is closed.
Show the selected count at the top of Torrent; the row context menu names
the torrent for a single selection. Clicking
outside dismisses it; Escape dismisses it and returns focus to its invoker. Use
native MenuFlyout behavior, because command menus should respond as Windows users
expect.

Settings and About retain the same title-bar layout. Back returns to the table,
preserving selection and the inspector. Torrent and View menus are disabled while
a secondary page is visible so commands cannot act on a hidden selection.

The table starts with Name, Size, Progress, Status, Down speed, Up speed, ETA,
Ratio, Seeds/Peers, and Added; the person can hide, show, and reorder them.

Queue ascending and natural order put downloads in libtorrent queue order,
followed by completed seeds, whose Queue cell is empty. Seeds remain selectable
for torrent commands but cannot join a queue move or drag. Dropping downloads
before a seed means the end of the download queue. This keeps queue actions
meaningful instead of suggesting a seed priority that libtorrent does not use.

Torrent commands are Pause, Resume, Force start, Open, Open folder, Copy magnet
link, Copy info hash, Move files, Verify, Remove, and Delete files. Download in
sequential order and Download first and last pieces first are menu choices
beside them. An item is checked when every selected torrent
has that choice and mixed when only some do. Choosing a checked item turns the
choice off for all of them, and otherwise turns it on for all of them, so one
click makes a mixed selection consistent. The inspector does not repeat them:
the item already shows the choice where it is changed. Open hands the file
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

The toolbar, a row above the table that View shows or hides, holds Resume,
Pause, Open folder, Properties, Verify, Remove and Delete files, as subtle
icon buttons in groups split by dividers. It shows only the commands whose icon
a person recognizes without its tooltip, because Fluent asks a toolbar for
familiar icons; the menus keep every command. Copy magnet link stays off it
because its link icon is Add magnet link's in the title bar above. Its buttons
act on the selection and are disabled without one, as their menu items are; Add
stays in the title bar so it is visible while the toolbar is hidden. The
toolbar is one Tab stop, and the arrow keys move inside it. It starts visible,
and the window layout remembers it.

Dropping torrent files or magnet text on the window, or pasting them with Ctrl+V
while the table has focus, follows the same Show the Add form preference. Sources
join an already-open Add task; otherwise that preference decides whether the
form opens or addition proceeds directly. An empty list says how
to add a torrent. A status bar shows total download and upload speed, the
alternative speed toggle, whether incoming connections arrive or the selected
network interface is absent, and Update available when a newer release exists.

Shortcuts follow Windows conventions, so people keep the habits they use in
other Windows applications. TinyTorrent has no Print, Save, or Refresh command,
so Ctrl+P, Ctrl+S, and Ctrl+R are free for Pause, Resume and Verify. Torrent
shortcuts act while the table, selection toolbar or inspector has focus; an
editor keeps its own keys.

| Key | Action |
| --- | --- |
| Ctrl+O | Add a torrent file |
| Ctrl+Shift+O | Add a magnet link |
| Ctrl+V | Add the pasted magnet link or torrent file |
| Ctrl+K, Ctrl+F, Ctrl+E | Search torrents, commands and settings |
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
| Ctrl+, | Settings |
| Ctrl+W | Close the window |

Alt+F4 closes the window, as Ctrl+W does. Exit has no shortcut, as in other
Windows applications; Alt+F, X reaches it.

Remove keeps data; delete-data is an explicit, distinct decision. Each confirms
once with the affected torrent names or count, a specific action such as Remove
or Delete files, and a safe Cancel action. Remove confirms because a removed
torrent cannot be restored without its torrent file or magnet link. Delete files
also states the file scope, that deletion is permanent, and that files other
torrents use are kept. Deletion bypasses the Recycle Bin,
because people delete a torrent's files to free disk space. The dialog
opens with focus on Cancel, so Enter cannot delete data by accident. Its
buttons follow the dialog button ruling in [Buttons](#buttons).
Routine pause, resume, and applied settings need no confirmation.

Retain recognizable identity and last-known read-only values on disconnect, mark
them stale, and disable writes until the engine is available. Current speed is
unknown while disconnected and displays an em dash; old rates must not appear
current. Show loading separately from an empty list. Progress fills its column
and uses distinct paused and error brushes alongside the written status, so state
is visible without depending on color. Routine outcomes are announced to assistive
technology without a visible toast; actionable failures follow the
[feedback placement policy](#feedback-placement). Pending operations
and failures remain visible without exposing internal protocol machinery.
Reconnection preserves presentation state according to the protocol contract.
After reconnecting, show the confirmed list; do not leave a permanent busy state.
A missing row means the torrent was removed, not that its files were deleted; a
deletion failure arrives as a notification.

### Feedback placement

Choose the surface by what the person needs to do, because a disappearing
message cannot be the only explanation of unfinished or failed work.

| Situation | Surface |
| --- | --- |
| Routine pause, resume, addition in the open window or applied setting | The changed state and one accessible outcome announcement; no visible success toast. |
| Refused setting or failed editor action | Persistent feedback beside the field or inside that editor, following [Committing edits](#committing-edits). |
| Failed command without an editor, such as Pause or Open folder | A dismissible app-level error message, separate from connection status; no timed disappearance. Retain the existing policy for clearing it after later command outcomes. |
| Connection loss or unavailable storage affecting the application | A persistent InfoBar over the bottom of the workspace, above the status footer. It overlays every page without resizing its content; recovery clears the condition. |
| Useful, noncritical event elsewhere in the open application | A temporary overlay in one consistent corner of the window, without moving page content. Add this only for a named event whose existing presentation is insufficient. |
| A finished download | The temporary overlay, with the torrent name and Open folder, as the [notification policy](engine.md#notifications-and-sleep) defines a finished download. |
| A torrent stopped by an error, or a deletion that failed after removal | The dismissible app-level error message; a stopped torrent also stays in the Errors count. With the window closed, the engine's [notification policy](engine.md#notifications-and-sleep) sends a Windows notification instead. |
| A decision requiring consent | The existing dialog for that operation. |

InfoBar actions align to the right, with the message using the remaining width,
so recovery has a consistent place instead of moving with the message length.

Native control state notifications can satisfy routine accessible feedback;
do not add a second announcement of the same result. Keep field and editor
errors at their point of correction. Keep unresolved
app conditions visible independently of temporary messages. A transient overlay
never takes focus, covers the active editor or confirmation, or becomes the sole
place to recover from a failure. Announce its content once; any action must be
keyboard accessible, and its timeout pauses during pointer or keyboard interaction.
Combine repeated events instead of stacking an unbounded stream. Do not repeat
an engine desktop notification as an in-app toast for the same event.

[Fluent 2 toast guidance](https://fluent2.microsoft.design/components/web/react/core/toast/usage)
supports consistent floating placement for noncritical events; it describes web
components, not a native WinUI toast control. Native
[InfoBar guidance](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/infobar)
uses inline presentation for lasting conditions. These are different purposes,
so making every message float would hide useful context rather than improve it.

### Add

The task answers: what is being added, which files are wanted, and where they go.
Use native source/destination pickers and an editable magnet input with an
explicit Paste action. Read the clipboard only after the relevant user action.
Preserve accepted input and choices when a picker is cancelled or a replacement
source fails; successful replacement deliberately starts a new preview.
Getting metadata transfers pending magnet text into the staged source once.

Show preview progress immediately, keep cancellation available while acquiring
metadata, and expose file choices when metadata is ready. The engine supplies
that metadata; the UI does not parse torrents or impose an old client's
file-choices-only-after-add limitation. Unknown metadata is an explicit state,
never an invented file list or silently started payload transfer. Add is
available before magnet metadata arrives, so a slow swarm does not hold the
person in the form; the torrent then wants every file, and file choices move to
the inspector's Files view.

**Owner ruling: several sources added together are added at once, with no
form and no question.** This covers one drop or paste, and several files opened
from Explorer that reach the window together. They go to the default folder with
the default options, and the torrents they add are selected in the list, so the
person sees that all of them arrived. A torrent already in the list is selected
where it is and keeps its trackers, because offering a merge would be a
question. A source that fails is reported in the window's error bar and
dropped. File choices for each torrent are made later in the Files view. One
drop of thirty torrents opens nothing, because a cascade of dialogs is the
failure this protects against. Several files chosen through Add torrent file
open one form, because choosing that command asks for it. Sources that arrive
while the form is open join it, so a second dialog never opens. The form has a
Don't show the Add form again check box, which turns off the Show the Add form preference
where the person meets the form.
If a source has no wanted files when a single-source task becomes a batch,
Select all files beside that source restores a valid choice. Keep prior file
choices until this explicit action; adding another source does not reset them.

Equivalent staged sources keep all their original inputs on the one draft entry.
Reconnect reacquires them through the engine's existing preview owner, preserving
distinct tracker URLs and the user's choices. Literal input deduplication keeps
case-sensitive tracker paths distinct.

When sources require the Add form, those arriving during another modal task
remain in the Add draft and open when that task closes. Turning Show the Add
form off allows direct addition during other tasks; an already-open Add form
still owns new sources and its choices. Failure to show a form does not cancel
its input. One window-owned shortcut path keeps application actions out of modal editors;
Close and Exit still follow the unfinished-input rules.

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
F2 opens the native priority choice for the focused file row; ordinary tree,
checkbox and ComboBox keyboard behavior remains native.
Keep file identity and selected bytes clear; when a known list
has no wanted files, explain why Add is unavailable. The destination starts from
the default download folder, initially Windows' Downloads known folder, and remains changeable through a native picker. A failed
free-space check must not be presented as proof of an invalid folder.
The destination also offers recent folders: the default folder and the folders of
the newest torrents, six at most. They are derived from the default folder and
existing torrents rather than stored, so removing a torrent removes its folder
from the list. Dropping a single
folder on the open form makes it the destination. Free space for the destination's
drive appears under it, and a warning replaces it when the wanted files do not
fit; the warning never blocks Add, because the person may free space first.
Network drives show no free space, because asking a disconnected one can block the
window.

The form sets settings on the left and files on the right, with a splitter between
them that keeps its share of the width while the dialog is open. Hide settings
gives the files the whole width; the settings start hidden when the files would
have too little room beside them, so file names stay readable in a small window. ContentDialog cannot
be resized by dragging, so the form has Maximize and Restore in its header: the
default size suits a typical torrent, and Maximize fills the window and follows it.
Close follows Maximize at the right of the header, where Windows places it, and
does what Cancel does.
The magnet field wraps one link across multiple visible lines and scrolls
vertically, using the available right-pane space. Wrapping does not insert line
breaks into the link. Paste and Preview sit centred below it with the shared
button treatment. When sources are previewed, they share the pane with the editor
so its height cannot hide the preview.
The commit button names its result, Add or Add paused, and Add all or Add all
paused when several torrents, including typed magnet text, will be added.

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
Preferences keeps this LabForms layout independently of the torrent inspector.
Each category holds LabForms sections: a borderless card headed by an icon, a
title and a description. Each setting in a section is one row: its name and a
short description on the left, its control on the right, and its error in the
same row, so feedback never moves the rest of the page.
Put Browse beside the default download path,
and beside Add's destination, using the native Windows folder picker. Cancelling
the picker preserves the current path and other unfinished input.
Do not show an engine field dump. Disk
caching remains [automatic engine policy](engine.md#disk-write-caching), not a
Preferences choice.

Include, grouped by task: the default download folder and Show the Add form;
global and alternative speed limits; queue limits for active downloads and
seeds; seeding ratio and time limits; connection limits; the network interface,
port mapping, and listen port; the
[notification switches](engine.md#notifications-and-sleep); preventing idle sleep
while downloading on mains power, and also while seeding; Check for updates,
following the [update model](architecture.md#installation-and-updates); and
language. Each uses the existing settings path. Reaching a seeding limit pauses
the torrent; nothing is removed without a request. Resuming that torrent by
hand lifts the limit for it, so it seeds on as asked instead of pausing again.
The sleep switch names its mains-power condition, so a laptop that sleeps on
battery does not surprise its owner.

General has a Notifications section with three switches in order of
importance: Notify about problems, Notify when a download finishes, and Notify
when a torrent is added. The order shows which notifications matter most.
Turning all three off stops every Windows notification except the one-time
notice that TinyTorrent keeps running in the notification area. Problems are
one switch, because no one needs to silence one kind of problem and keep
another.

Scheduler presents one weekly overview with normal limits, alternative limits,
and paused periods, because separate schedules obscure their combined effect.
Time runs left to right beneath a 00–24 hour ruler; each day has one row, and
segments occupy widths proportional to their duration. The week stays visible
when empty or switched off, so the person can discover and prepare a schedule.
Its state, switch and Add period sit above the timeline. Normal time is quiet
background; selecting a period reveals its exact times, duration, Edit and Remove.
One collapsed Saved periods expander groups compact day/time entries by mode;
each opens the exact editor, so covered periods remain accessible without nested
disclosure or a separate selection step. Empty groups stay hidden.
The scheduler is one control embedded in Preferences, sharing its period and
save owner with the exact editor rather than implementing scheduling rules twice.

Dragging empty time opens Add with that day and range selected; an ordinary click
only focuses the week. Dragging a period horizontally preserves its duration;
selected start/end edges resize it. Thin inset grips appear on the hovered or
dragged occurrence, while every occurrence keeps its selection outline, so
recurring periods do not fill the week with handles. Gestures snap to 15 minutes and show a
live time/duration preview. Release saves a move or resize of a saved period.
While the exact editor is open, the draft remains draggable by its body or either
edge and updates the time fields live; Save commits it. Escape or lost pointer
capture restores the times before the gesture without discarding the draft.
The native time fields retain exact minute precision.
Blocks show their effective time ranges as well as their modes; hover and selection
expose the complete source period, so an overlap does not obscure its saved times.
The ruler reduces its tick count at narrow widths, and calendar geometry follows
Windows text size so labels do not collide. Exact time fields and selection actions
sit side by side when they fit, and stack when space is limited.
Keyboard arrows navigate days and times, Space selects, and Enter adds or edits,
so dragging is never required. Add period and the details list remain native
keyboard and accessibility routes to every operation.
Keyboard navigation displays the current day and exact time. Drafts identify their
unsaved preview and next-day endpoints; Save and Cancel return focus to the week or
Add period. Escape cancels the exact editor as well as an active drag.

Selecting a repeating period outlines every occurrence, including portions
covered by Pause, so moving one occurrence cannot silently change other days.
An overnight period shows its following-day portion and handles at its actual
endpoints. The fill still shows the effective mode, with Pause taking precedence.
Reject exact duplicate submissions; preserve distinct overlapping rules so a
later Edit or Remove retains its meaning. Switching off stops application of
the periods while leaving the editor usable. Save failures preserve the attempted
times in the exact editor beside an error, so the person can retry or cancel.
Full-day descriptions say All day rather than midnight to midnight.
Each period has start days, start/end times, and a choice of alternative limits
or pause, edited with native checkboxes, TimePicker controls, and radio buttons.
Normal limits apply outside periods; pause takes precedence on overlap. Overnight
periods end on the following day. The schedule is disabled by default and repeats
in local time. Its engine owner preserves individually paused torrents and manual
Pause all, so a scheduled boundary cannot undo the person's explicit pause.

Appearance offers the application language and Follow Windows, Light, and Dark
through the existing theme owner. General's Startup group holds Start when I
sign in, a Start in the notification area switch, and a Show the splash screen
while opening switch. Start in the notification area, off by default, makes
starting TinyTorrent start only the engine in the tray; opening TinyTorrent
while it runs still shows the window. The splash switch, on by default, serves
a person who opens TinyTorrent often and finds the splash in the way. All categories
share one viewport-constrained, centred content column, so a change of
category cannot move the form or push its actions outside the viewport.

Preferences offers a Start when I sign in switch and an Open torrents with
TinyTorrent switch covering `.torrent` files and magnet links. Both call the
engine's [registration owner](engine.md#windows-registration) and show its
observed state. Turning the handler switch on finishes automatically when
TinyTorrent is already the default; otherwise it takes the person to the
supported Windows choice and refreshes on return. While Windows still opens
either kind with another app, a Windows Default apps link sits under the
switch. Turning it off removes TinyTorrent as a handler. Present mixed
file/link defaults in ordinary language only when they need action, without a
registry-status panel.

**Owner ruling:** an entry that starts another TinyTorrent copy is still
TinyTorrent's registration, so its switch shows on, with a caution line naming
that copy's executable; it never shows as unregistered.

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

The accepted [hierarchical rows extension](../lib/TableView/docs/hierarchy.md#files-browser-integration)
supersedes this TreeView choice for both Files hosts and keeps torrent-specific
file commands in the application.

**Owner ruling:** the six inspector sections use a native SelectorBar, centred
in one header row between the torrent name and an icon Close button. The owner
could not get used to a left navigation pane, and one row keeps the sections
visible while the working view gets the full inspector width. The person
adjusts the inspector's height with the split.

The inspector is a layer above the workspace, because it shows one torrent's
properties rather than more of the table. It is a raised surface:
`CardBackgroundFillColorDefaultBrush` fill, `SurfaceStrokeColorDefaultBrush`
edge and `OverlayCornerRadius` corners, inset from the window edges so the base
acrylic shows between it and the table. These two tokens are chosen because they
stay visible on the dark acrylic base, where `LayerFillColorDefaultBrush` and
`CardStrokeColorDefaultBrush` differ from it by too little to see. Its header sits on that layer with a
`DividerStrokeColorDefaultBrush` bottom edge. Tables inside it follow the
[TableView visual contract](../lib/TableView/docs/tableview-contract.md#8-rendering-layout-and-visual-language),
so the inspector does not define a second table appearance.

Apply individual choices and explicit file commands through the same commit
rules. When a coherent edit needs a draft, keep one active editor bound to the
torrent identity; protect only its unfinished input when changing context or
closing. An untouched inspector and an already-applied change never trigger a
save prompt. A removed target cannot receive a write.

Files permits Select none on an existing torrent. It changes wanted choices
without removing the torrent or its downloaded files; Add still requires at
least one wanted file, because a new download otherwise has no useful work.

Move files moves the torrent's own files; other files in its folder stay. Show the
folder the files will be in, so choosing the torrent's own folder instead of
the folder that contains it is visible before the move. Preserve choices when
submission is refused, so the person can correct them without starting over.
When files are already at the destination, offer Use files there,
following [engine relocation](engine.md#removal-and-relocation). When other
torrents use the files, name them and offer to move them together.

Move files and Delete files are available from selection actions, the row context
menu and command search. Shift+Delete opens Delete files; Delete still opens
Remove, which keeps downloaded files. Delete files names the torrents and their
source folders, warns that deletion is permanent, and lists outside torrents
whose shared files will be kept. Cancel remains its default button.

Move files shows the current source folders, the chosen destination parent and
the resulting content folders. Include shared torrents is an explicit choice;
the engine rechecks the scope when the command executes. Use files there is a
separate explicit choice with a warning that verification downloads mismatched
pieces over those files. A refusal keeps the destination and choices. Accepted
work appears as Moving files without a file-copy percentage; row errors direct
the person to reopen Move files and choose the folder holding the files.

The Speed view shows the [engine's session-wide speed history](engine.md#state-and-work),
whichever torrent is selected, and its heading says All torrents, so nobody
reads it as the selected torrent's speed. It continues while WinUI
is closed, so reopening shows what happened meanwhile. Offer the last 5
minutes, 1 hour, 6 hours and 24 hours as always-visible choices. The mouse
wheel over the chart steps to a shorter or longer
choice; it is a shortcut, so the choices stay visible. Unknown gaps, such as the
time before an engine restart, are not interpolated into invented history.

The chart shows the trend, not every sample: per-second rates jump with each
burst from a peer. It draws averages of 5 seconds, 1 minute, 5 minutes and 15
minutes for the four choices, as a smooth curve that never rises above or falls
below the averages. Download is a filled area and upload a dashed line, so the
two differ without colour. Round clock times are marked under the chart.

A one-line legend per direction gives the current average and the peak, so the
chart keeps its height in a short inspector. The legend and the rounded scale
use the same averages, so the text never disagrees with the line. Pointing at
the chart, or moving through it with the arrow keys, shows that time's values in
the legend instead; this is the equivalent textual fact of any point.

One page owner allocates table and inspector space. Remember an explicit split
adjustment within current usable bounds; the splitter is keyboard-adjustable and
reports its range. The window's minimum size fits the table and the inspector at
their minimum heights, so there is no second layout for small windows. Preserve
selection and the requested split; do not hide an inspector while continuing its
detail work.

### Pieces map

The Pieces map answers two questions: how far the download has come, and whether
it can finish. It states the answer in words, because a grid of colours alone
leaves the user to work out the conclusion. Square size and the rare limit
follow the previous TinyTorrent map, and the squares are filled tiles without
outlines, as that map's were; change them only when the
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
  lets the user skip them and let the rest finish. A list of files, here and in
  the tooltip, names the first three and counts the rest, so a long list cannot
  push the map out of view or fill the screen; Files lists every file. Under the
  sentence, in secondary text, are the piece count and piece size.
- **Legend.** Under the status, one row shows each state with its swatch, its
  name, and its count in semibold, as the Speed legend shows its values. The
  counts are the legend, so the two cannot disagree. Entries share one width, so
  they line up in columns when the row wraps in a narrow panel.
- **Squares.** Squares keep one readable size and sit in groups, so the eye
  keeps its place. They never shrink: when the torrent has more pieces than fit,
  the squares fill the space under the legend completely, and each covers a
  contiguous range of pieces that differs from the others by at most one piece.
  The map starts at the same edge as the status and legend and is aligned to the
  top. In a right-to-left language the first piece is at the top right, as a
  progress bar starts at the right.
- **Squares that cover several pieces** show the state most of their pieces
  have; on a tie the worse state wins, in the order unavailable, rare, common,
  missing, downloading, verified. A square that holds more than one state gets a
  small dot in its top-right corner, so the user knows its colour does not
  describe every piece. A square that holds an unavailable piece but shows
  another state also gets a dot in its bottom-left corner in the unavailable
  colour, so a piece that can stop the download is never hidden by a healthier
  majority. Its corner carries the meaning without colour.
- **Drawing.** Squares are tiles with the Fluent control corner radius, filled
  with Fluent theme colours and drawn without outlines: verified is solid, a
  downloading square fills as its data arrives, so progress moves while the user
  watches, common is a light accent tint, missing is neutral, rare is hatched,
  and unavailable is crossed. The solid fill, the partial fill, the hatching,
  and the cross keep the states apart without colour; missing needs no mark of
  its own, because it appears only while common, rare, and unavailable cannot.
  In High Contrast, system colours replace the fills, so every square also gets
  an outline, as Fluent controls do there.
- **Pointer and keyboard.** The map is one focus stop with one selected square,
  which the arrow keys, Home, End, and a click move; it shows the focus outline
  while the map has focus. Pointing at a square gives it a thinner outline
  without moving the selection, because a pointer passing over the map must not
  move the keyboard's place. The square last pointed at or selected shows a
  tooltip with its piece number or range, the files they belong to, the count
  of each state it holds, and how many connected peers have its missing pieces
  that are not downloading: the count for one piece, the lowest and highest for
  several, because a merged square's colour cannot show that spread. While no
  peer is connected, that count is unknown. The selected square's text is the
  map's UI Automation value, and Ctrl+C copies it.
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
