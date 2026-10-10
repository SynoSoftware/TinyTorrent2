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
product-design, Microsoft Fluent, and keyboard-only perspectives, with depth
matched to the magnitude of the change: a wording fix needs no new screen
design, and a substantial new journey gets independent review, because
building in the wrong direction costs far more than the review. Resolve
concrete findings before implementation.

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
The title-bar Settings and theme shortcuts keep the existing custom caption-button style:
full caption height, square hover surface and caption-button width, so it feels
part of the window chrome. This is the exception to standard button visuals.

Use the native platform's expression of Fluent. Web component APIs and examples
do not override WinUI's control semantics or require a second design system.

The visual and interaction reference is `app/prototype.html`, variant
C, commit `8614126` on `main`. Preserve its compact table, collapsible status
drawer, caption search and Settings composition.
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
the page switcher, bounded Search, Settings, the light/dark switch, and native caption
buttons. Add and the selection commands are on the toolbar, not the title bar.
Fit columns and Fill width are only in the table's column-header menu: in the
title bar they read as window commands, and the column header holds no buttons
because every part of it belongs to a column. A 24-pixel
icon and 32-pixel Search sit within a 48-pixel row so the content has breathing room.

**Owner ruling: keep the existing custom title bar; do not replace it with the
WinUI TitleBar control.**

Title-bar actions are ordinary Buttons with the existing
TinyTorrentCaptionButtonStyle, so they share its size, spacing and states with
each other and with the native caption buttons beside them; CommandBar's
separate button metrics would break that consistency. Settings sits immediately
left of the theme button, using the Lucide gear icon. The theme button sits
immediately beside the native caption buttons, with no extra gap.
Windows still owns minimize, maximize and close; never simulate those controls.

Inset the icon and leave 24 effective pixels between the menu and page switcher,
so page navigation reads as a separate group of controls. Search aligns to the
right of its available space, before optional transfer speeds and caption actions;
the spare space before it remains draggable. Reserve Windows' caption insets plus
a command buffer. Search is at most 320 effective pixels wide. The minimum window
width accommodates the measured menus, the page switcher as icons, a 200-pixel
search and the caption buttons, including translated labels. Unused title-bar space retains native dragging,
double-click maximize/restore and the system menu; controls receive client input.
The app icon is a native system-menu region: left-click or right-click opens
Windows' Restore/Move/Size/Minimize/Maximize/Close menu, double-click closes the
window, and Alt+Space opens the same menu. Windows owns these caption semantics;
the icon does not open an application command menu.

There is no application navigation pane, identity text, command overflow,
language switch or permanent Exit button. Language and theme
are chosen in Settings; the title-bar light/dark shortcut uses the same theme
owner.

**Owner ruling: Torrents and [Library](library.md#presentation-and-interaction)
are peer pages.** A SelectorBar, the page switcher, follows the MenuBar and
offers Torrents (Ctrl+1) and Library (Ctrl+2); when the search box would drop
below 200 pixels, its items show only their icons, with the names and shortcuts
in tooltips. Global search also offers both pages. The second menu belongs to the
current page: Torrent on Torrents, Library on Library. View shows the current
page's presentation. File and Help do not change.

On Settings and About, a Back arrow at the left of the title bar, before the
app icon, and Alt+Left return to the page they were opened from, with the
existing draft guards. Show in Torrents on Library also shows Back on Torrents,
returning to Library. The arrow sits in the title bar, as in Windows
Settings, so the page keeps its full height. Keep continuous acrylic.

A second window launch forwards Open to the existing application. Check its outcome
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
window, a dialog or Settings, shows labels, values, states and errors.
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

Dialog bodies are the exception for prose: sentences supplied as plain strings
wrap so consent and confirmation text remains readable. Lists of facts, labels,
values and field errors keep one line per fact and expose their full text when
trimmed. The shared Dialog template owns this distinction.

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
dialog and pane titles use Subtitle; group headings use Body
Strong; field labels, values, commands and status text use Body. Secondary facts
such as free space keep that body size and use the secondary text brush instead
of smaller type. App.xaml owns the shared title and body styles.
The approved Inspector General composition in variant H retains its statistic
captions, larger values, state heading and monospaced identifiers; those roles
make its summary distinct from the detailed property rows. Shared Body and Status
styles keep the same type size; Status uses tight line bounds for compact facts
beside icons. Existing approved prototypes remain the authority for their
compositions rather than a reason to flatten them into identical rows.

Shared controls and application resources own repeated parts: Dialog owns dialog
chrome and buttons; SettingsRow and SettingsSection own settings rows and groups;
Field and FieldColumns own property rows and their columns; FieldHeader owns
editor labels with field errors; CheckStatus owns interactive check feedback.
ActionButton and the subtle/caption button styles own application buttons. The
TinyTorrent text, icon and surface styles in App.xaml supply their shared looks.
Strip, WrapPanel, Splitter and FileBrowser retain their existing layout and file
interaction responsibilities. A feature composes these parts without copying
their templates; purposeful page and Inspector compositions remain distinct.
Equivalent groups use the same heading treatment and content inset, so a person
can recognise the hierarchy without learning a different visual language in each
pane. Native controls keep their internal spacing; a dialog or page does not compensate
for it with negative margins.

## Buttons

**Owner ruling: dialog buttons keep their natural width and sit at the right
of the row. Supporting content for the button row, such as a status, a check
box or a button that acts on that status, holds the left of the row. Without
such content, buttons that fill more than two thirds of the row are centred.**
ContentDialog's own template stretches its buttons across the row, which on a
wide dialog draws two buttons of about 480 pixels and leaves no place for a
check box beside them. Buttons at the right sit where the eye finishes reading
and share the right edge of the content above. A gap of a third of the row or
more reads as a deliberate division; a smaller one reads as leftover beside
right-aligned buttons, so the buttons are centred with equal margins instead.
Every dialog therefore uses the shared Dialog control, which draws the row this
way from its Footer and the space its buttons leave free, with less padding
above and below. The buttons keep the Windows order: the action first, then Cancel. A
dialog opens with focus on its first text box that leaves Enter and Escape to
the dialog, or otherwise on its default button, so the person can type or press
Enter at once.

Natural button widths retain the normal dialog minimum. The supplier/proxy
footer is the approved exception: Check, Save and Cancel share the widest action
width, so the related actions align without clipping localized labels.

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

**Owner ruling: a confirmation's default button is the act the person chose.**
Remove, Delete and Exit default to the requested act, not Cancel, because the
person already chose the act and the dialog only confirms it, as File Explorer's
permanent-delete confirmation does. A Cancel default answers a request with its
opposite. The one exception is the question that a pending Add or Move raises
when the window closes: it asks about an act the person did not choose, so
Cancel is its default.

A button runs a command or opens a surface. A control that directly changes a
setting, such as View > Toolbar, is a toggle or a selection control: it
shows which state is current, and a button looks the same in every state.
Label wording and icon use follow
[Text, icons, and typography](#text-icons-and-typography).

- **One act, one name.** The same act has the same label, the same accelerator,
  and, where it shows one, the same Lucide icon in the toolbar, the context menu,
  and dialogs. Where [Main window](#main-window) names a command, menus use that
  name and a button uses its one word, with the full name in the tooltip. A
  person who learns Pause once recognizes it everywhere, while two names for one
  act read as two acts.
- **The accent marks the default button.** The platform's accent style goes on a
  surface's default button, the one Enter runs, and every other button keeps the
  standard style. The default is the commit, such as Add or Save, and in a
  confirmation it is the act the person chose, as ruled above. A surface with
  no commit has no accent. The accent then never points at an act
  that Enter does not run, and a row of accents never hides the main action.
- **An icon-only button names itself in a tooltip,** with its accelerator, on
  keyboard focus as well as on hover. With no visible label, the tooltip is where
  a person learns what the icon does.
- **An icon-only button is subtle.** It uses App.xaml's `TinyTorrentSubtleButtonStyle`:
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
A missing or damaged layout uses the declared defaults under
[saved-value recovery](architecture.md#saved-value-recovery); a layout write failure
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
Respect system accent, contrast, animation, and text settings. Preserve the
expected rest, hover, pressed, selected, focused, disabled, loading, and error
states of standard controls. Meaning must survive without color or motion.

Standard controls retain their built-in animations. Page and tab changes use
the LabForms SettingsPage pattern: outgoing and incoming content slide together
horizontally over 180 ms with cubic ease-out, clipped to their host. The direction
follows tab order. Changing the inspected torrent moves the new content gently
into place once; live statistics never restart the transition. These changes
do not fade through a blank surface. Rapid navigation continues from the current
positions rather than snapping back to the start. One motion owner supplies the
shared timing and easing. Motion never delays input or a state change. When
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
Enter in a multiline editor cannot commit the entire dialog. Follow
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
a tracker list, the Add dialog, or moving or deleting files.
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

**Owner ruling: leaving an editor applies its input, and only Add and Move
ask.** Fluent 2 applies a change without a confirmation step, so leaving a
schedule period or a tracker list applies it, as leaving a setting does, and
failed file choices are sent again. Nothing changes under the person's hands:
an edit that cannot be applied, because it is incomplete, invalid or refused,
keeps the person at the editor with its error, so they never leave believing
it applied. A duplicate schedule period is dropped, because the schedule
already holds it. A row that leaves the table's view, such as a torrent that
finishes under the Downloading filter, leaves the selection but not the
inspector: the inspector stays on that torrent, with any unfinished edit. When
the engine or the torrent is unavailable, file choices are dropped on leaving,
because Retry is unavailable and they have no Cancel, and closing the window
drops any edit that cannot be saved.

The subtitle supplier editor requires explicit Save and discards its draft on
window close, because changing supplier turns automatic subtitles off and stores
account details the person has not confirmed.

Add and Move still ask, because applying them would start a download or a file
move that the person has not confirmed. Save runs the editor's own action, Add
or Move; if it fails, the editor stays open with its error. Discard drops the
draft and continues. Cancel keeps the editor and focus. Deleting files
still shows its own confirmation of what will be deleted.

On window close, ask only for work the person has actually entered: a torrent
source or a move destination. Opening an untouched editor or changing Add or
Move options without a source or destination does not justify a question. The
question names the unfinished work and its primary action: Add or Move.
The Save and Discard tooltips say what each does, following the
[help-text ruling](#text-icons-and-typography). Cancel is the default button, so Enter cannot unexpectedly add a
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
The File menu contains Add torrent file, Add magnet link, Settings, Close and
Exit. **Owner ruling:** Close closes the window, as Alt+F4 does, and transfers
continue in the tray; Exit is the tray's Exit, which closes the window and stops
the engine. One word then names one action in the window, the tray and search.
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
without a draft. The selected source then opens the Add dialog for destination,
Start paused and Add/Cancel, following the Show dialog when adding torrents setting.

The torrent table is the primary workspace, with an optional inspector and
focused Add and Settings tasks. Outside Library, the caption contains native
search for torrents, commands and settings. Selecting a result reveals its
torrent, runs the existing command, or opens and focuses the named setting
without changing it. Typing does not silently filter the torrent table.
On Library, search filters the current configuration as the person types and
shows no suggestion list, keeping the filtered rows visible. Escape clears
the filter; Down or Enter focuses the rows. The query stays when the
configuration changes. Commands and Settings remain available through the
menus, title-bar Settings button and their shortcuts. Ctrl+K, Ctrl+F and Ctrl+E
focus the current page's search. Scope labels in global search distinguish a
command for the selection from a command for all torrents.

The Filters toggle opens a collapsible native pane with All, Downloading,
Seeding, Paused, Queued and Errors choices and live counts. Closing the drawer
keeps the chosen filter, identified in View > Filters and a status label.
Escape closes the drawer when focus is inside it. Clear filters lives inside
the drawer and calls the existing filter owner. Downloading includes metadata
acquisition, Seeding includes completed torrents and Paused includes session
pause. The drawer starts closed so the table keeps its full
width until the person asks to filter. Status and progress remain visible and
sortable in the table. Tracker information
belongs in the selected torrent's inspector. TableView owns generic interaction.
Settings opens from File and About from Help. Close and Exit
are File commands and keep their existing pending-work and draft guards.
Help places subtitle setup before application updates and About. Subtitle setup
restores the dismissible introduction on the Settings index, so closing that card
does not make its guidance unreachable. Native add/remove transitions close the
card and move the remaining categories into place without a layout jump.
Keyboard and search paths invoke the same owners. About shows the product
identity and running version on the same acrylic surface. The current page
belongs to the main view model.
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

Settings and About retain the same title-bar layout, with the Back arrow added
before the app icon. Back returns to the page they were opened from,
preserving its selection and the inspector. Torrent and View menus are disabled while
a secondary page is visible so commands cannot act on a hidden selection.

The table starts with Name, Size, Progress, Status, Down speed, Up speed, Time
left, Ratio, Seeds, Peers, and Added; the person can hide, show, and reorder them.
Time left reads in its two largest units, such as 3 d 4 h or 45 min, and 100
days or more reads as ∞, because a raw count of minutes cannot
be read at a glance. Seeds and Peers each show the connected count with the
swarm total in parentheses: the tracker's count when it
reports one, otherwise the peers this session has heard of. Peers excludes
seeds. Added reads as elapsed time, such as 3 hours ago, because how long ago a
torrent arrived is what the person compares; the exact date and time is its
tooltip.

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
open the inspector; Properties in the torrent context menu does the same.
On Library, Enter and double-click open the selected file through its Open command.
The pane has a Close action rather than an ambiguous toolbar toggle, because its
entry points already identify the torrent being inspected. Only Close closes
the pane. When no torrent or several are selected, or the inspected torrent is
removed, the pane stays open and shows how many are selected, because a pane that
closes and reopens as rows are selected moves the table under the person.
Add torrent file opens
the native picker, and Add magnet
link opens a field for the link even when the Add dialog is turned off; the
source then follows the Show dialog when adding torrents setting. Pause all and Resume all
are in the window and the tray, and keep each torrent's own
[paused or running state](engine.md#state-and-work). Exit is in the window as
well as the tray.

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

The toolbar, a row above the table that View shows or hides, is a CommandBar
holding Add, Magnet, Resume, Pause, Open, Properties, Verify, Remove and
Delete as AppBarButtons in
groups split by AppBarSeparators. Each button shows its label beside its icon,
so a person knows what it does without hovering; when the window is too narrow
for every label, the CommandBar moves the commands that do not fit into its
More menu instead of clipping them. The menus keep every command. Copy magnet
link stays off it because its link icon is Add magnet link's. Add comes first and stays enabled
without a selection; with the toolbar hidden, the File menu, its
shortcuts, drag-and-drop and paste still add torrents. The other buttons
act on the selection and are disabled without one, as their menu items are.
Settings stays available in the title bar even when the toolbar is hidden.
The toolbar is one Tab stop, and the arrow keys move inside it. It starts visible,
and the window layout remembers it.

Dropping torrent files or magnet text on the window, or pasting them with Ctrl+V
while the table has focus, follows the [Add policy](#add): several sources are
added directly; a single source follows Show dialog when adding torrents.
An empty list says how to add a torrent.

The status bar shows status only, on one line, and holds no buttons, because
a control there is easy to miss and mixes acting with reading. Each label is
short and starts with a Lucide icon so the person finds it at a glance; its
tooltip and accessible name give the full sentence. The left group answers how
fast transfers go and what holds them back. It shows the total download and
upload speed, each followed by its cap while one applies, such as "1.2 MB/s of
5 MB/s", so a download that a limit holds back explains itself. A rate's tooltip
names its limit and the pair it comes from, or says that no limit applies.
After the speeds, one item says what holds transfers back, and changes in place:

| Situation | Item |
| --- | --- |
| Nothing holds transfers back | None |
| The alternative pair applies | Alternative limits, with "until" and the time when the schedule ends them; the caps and what turned them on are in its tooltip |
| The person paused transfers | All paused |
| The schedule paused transfers | Paused by schedule, with "until" and the time when known |
| Only the absent network adapter pauses transfers | Paused; the connection item names the missing adapter, so this item does not repeat it, and the full sentence is in its tooltip |

A pause wins over the alternative limits, because limits change nothing while
nothing moves; for the same reason, the speeds show no cap while transfers are
paused. The item tells a person whose torrent resume a global pause blocks why
nothing moves. When the item changes on its own, such as when the schedule
pauses transfers, screen readers announce the new state, because nothing else
tells a person who cannot see the status bar. The right group describes the list and the
connection. From the left, it shows: Update available when a newer release
exists; the external IP while Appearance shows it; the number of torrents and,
while some are selected, how many, as File Explorer counts items; the active
filter and its count while one is chosen. On Library the list facts instead
name its configuration, row count and active filters; torrent selection and
filter facts stay with Torrents. The final item says whether incoming connections
arrive, a proxy is in use, or the selected network adapter is absent. The
right group is aligned to the right edge, so an item that appears or changes
moves the items to its left. Update available appears on its own, so it comes
first, where it moves nothing; the external IP follows it, because its address
changes on its own. The filter keeps room for its count with every torrent in
the list, so a torrent that changes state never moves the labels before it. A
rate keeps room for its longest text, so live rates never move the labels after
it. When the
line is too narrow, labels shorten to their icons, least important first.
Help > Update and command search open the download page. Double-clicking an
item opens the place that changes it, as Windows status bars do: a rate,
Alternative limits or a scheduled pause opens the limits choice in Settings >
Speed limits; the pause item opens the network adapter while it is absent; the filter
opens the filter pane; the connection item opens the port in Settings >
Network, the proxy server while one is in use, or the network adapter while
it is absent; and Update available
opens the download page. The torrent count has nothing to change, and All
paused ends with Resume all, a command that a double-click must not run because
the status bar only reads. Choosing limits
belongs in Settings, because an automatic schedule makes an on/off shortcut
ambiguous.

Settings > Speed limits groups the current mode, standard and alternative caps,
queue activity, peer connections, and bandwidth accounting. The weekly editor
lives in Settings > Schedule, with both pairs of caps summarized above it so
periods have a visible meaning. The mode choice is
No limits, Standard limits, Alternative limits or Weekly schedule, in one native
ComboBox, so no separate schedule switch can disagree with it. No limits is a
choice of its own because people read "Normal limits" as no limits, and a capped
normal pair then looked like a stalled download; No limits also keeps the typed
caps for later. A fixed choice applies all week until Weekly schedule is chosen
again: it turns the schedule off rather than overriding it until the next
scheduled change, because a choice that expires by itself surprises the person.
The periods stay saved. A pair's caps are editable only while that pair applies,
or will apply under the weekly schedule: standard limits whenever the schedule
is followed, alternative limits when it has an alternative period. Editing caps
that cannot apply looks like it changes the speed and does not. Caps, origin and
pause reason come together from the engine snapshot. The picker shows a pending choice at once,
keeps that choice focusable and temporarily disables the other choices. The
rest of the page shows the engine's confirmed choice, so it changes once, when
the choice applies.
Failure restores the confirmed selection and appears beside it; recovery clears
an obsolete connection error. Native keyboard and accessibility behavior is
retained. Colour is not the indication of applied limits.

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
| Ctrl+K, Ctrl+F, Ctrl+E | Focus global search outside Library, or the Library filter |
| Ctrl+A | Select all torrents |
| Enter | Open the torrent inspector, or the selected Library file, as double-click does |
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
| Alt+F4 | Close |
| Ctrl+Q | Exit |

**Owner ruling:** Alt+F4 closes the window and Ctrl+Q closes the window and the
engine; the File menu shows both keys. Alt+F4 is how Windows closes a window and
Ctrl+Q is how desktop applications quit, so the menu names keys people already
use. Ctrl+W does nothing, because in Windows it closes a document or a tab, not
the application.

Remove keeps data; delete-data is an explicit, distinct decision. Each confirms
once with the affected torrent names or count, a specific action button,
Remove or Delete, and a safe Cancel action. Remove confirms because a removed
torrent cannot be restored without its torrent file or magnet link. Delete files
also states the file scope, whether files go to the Recycle Bin or are deleted
permanently, and that files other torrents use are kept. The deletion setting
chooses the initial option; the confirmation makes that choice explicit so a
saved preference cannot hide an irreversible action. The dialog's default,
focus and buttons follow the ruling in [Buttons](#buttons).
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
| Resume or Force start while a global pause prevents transfers | The resume takes effect, so the row changes from Paused to All paused at once, and a dismissible informational notice says when the torrent starts: when all transfers resume, at the time the scheduled pause ends, or when the network adapter is available. It never says the resume failed, because the person would press it again. Resume lifts the global pause; its tooltip says that a scheduled pause is lifted only until the next schedule change and individually paused torrents stay paused. An unavailable adapter offers Settings instead. Screen readers hear the notice instead of the routine announcement. The notice clears when the pause ends. |
| Resume all while the network adapter is absent | Resume all lifts the person's own pause, but the missing adapter still pauses every transfer, so the same notice says that transfers start when the adapter is available and offers Settings. |
| Refused setting or failed editor action | Persistent feedback beside the field or inside that editor, following [Committing edits](#committing-edits). |
| Failed command without an editor, such as Pause or Open folder | A dismissible app-level error message, separate from connection status; no timed disappearance. Retain the existing policy for clearing it after later command outcomes. |
| Connection loss or unavailable storage affecting the application | A persistent InfoBar over the bottom of the workspace, above the status footer. It overlays every page without resizing its content; recovery clears the condition. |
| Shared Library/subtitle source or database failure | The same workspace InfoBar, with Retry through the existing source and feature recovery paths. A table or supplier card does not repeat this shared condition. Supplier-specific lasting problems remain in subtitle Settings. |
| Useful, noncritical event elsewhere in the open application | A temporary overlay in one consistent corner of the window, without moving page content and without covering a control or a row the person can click. Add this only for a named event whose existing presentation is insufficient. |
| A finished download | The temporary overlay, with the torrent name and Open folder, as the [notification policy](engine.md#notifications-and-sleep) defines a finished download. It sits bottom-right, above the status bar, with the window's other notices. It takes no room, so showing or hiding it moves nothing. |
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
question. A source that fails is reported in the window's error bar and
dropped. File choices for each torrent are made later in the Files view. One
drop of thirty torrents opens nothing, because a cascade of dialogs is the
failure this protects against. Several files chosen through Add torrent file
open one Add dialog, because choosing that command asks for it. Sources that arrive
while the Add dialog is open join it, so a second dialog never opens. The dialog has a
Don't show this dialog again check box, which turns off the Show dialog when adding torrents setting
where the person meets the dialog.
If a source has no wanted files when a single-source task becomes a batch,
Select all files beside that source restores a valid choice. Keep prior file
choices until this explicit action; adding another source does not reset them.

Equivalent staged sources keep all their original inputs on the one draft entry.
Reconnect reacquires them through the engine's existing preview owner, preserving
distinct tracker URLs and the user's choices. Literal input deduplication keeps
case-sensitive tracker paths distinct.

When sources require the Add dialog, those arriving during another modal task
remain in the Add draft and open when that task closes. Turning Show dialog when adding torrents
off allows direct addition during other tasks; an already-open Add dialog
still owns new sources and its choices. Failure to show a dialog does not cancel
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
addition has no download progress yet. Neither host permits row reordering.
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
breaks into the link. Paste and Preview sit centred below it with the shared
button treatment. When sources are previewed, they share the pane with the editor
so its height cannot hide the preview.
The commit button names its result, Add or Add paused; when several torrents,
including typed magnet text, will be added, its tooltip gives the count.

Keep one dialog with a reachable native footer. A long body scrolls; the virtualized
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
downloaded again over those files, so the dialog names the torrents that already
use files at the chosen destination before Add. To replace an older torrent, the
person removes it, keeping its files, and adds the new one; the initial release
has no automatic Replace action.

### Settings

Group settings by user task and the actual libtorrent product, not the old
daemon's fields or fixed categories. Background choices have one engine owner;
Window-only settings have one WinUI operational owner; the existing settings
store persists their choices alongside theme. There are no remote profiles or
connected-server scopes in this local product.

**Owner ruling: the Settings page background is the window's acrylic with two
translucent gear marks.** The page sets no background of its own, so the
window's acrylic shows between and around the cards. Two large Lucide Cog glyphs
in the accent colour overhang the top-left and bottom-right corners behind the
content, at 4% opacity in the light theme and 3% in the dark theme, and hidden
in High Contrast. They take no input and screen readers skip them. This is the
owner's chosen look for the page, and nothing in the layout depends on it, so a
redesign that does not know about it deletes it. A change to the page keeps
both; only the owner removes them.

Use the [commit rules](#committing-edits): ordinary settings apply individually,
with no page-wide Save step or confirmation on close. Keep dependent fields
visible and disable them while inapplicable, so changing a switch does not move
later rows. One Show advanced settings switch reveals advanced rows and cards
across categories. Search includes those settings and reveals the chosen row.
**Owner ruling: Settings uses the title-bar search and has no search box of its
own.** The title-bar search already finds settings, including advanced settings;
a second box duplicates the task and takes a row away from the settings. Keep
the title-bar search's existing scope of torrents, commands and settings.
The category index is an introductory convenience shown when Settings opens
from its button. It is not a Back destination: title-bar Back leaves Settings
directly from a category or the index, preserving the ordinary edit guards.
Keep an explicit editor's actions reachable. Native navigation
and scrolling handle smaller windows. Settings uses a full page with horizontal
category selection and grouped sections, so settings have room without obscuring
the task. Returning to torrents preserves selection and the inspector view.
Settings keeps this LabForms layout independently of the torrent inspector.
Each category holds LabForms sections: a borderless card headed by an icon, a
title and native tooltip help. Each setting in a section is one compact row: its
name on the left, its control on the right, and its error in the
same row, so feedback never moves the rest of the page. Rows have no separator
lines; rows that belong together, such as a pair of limits, sit on a borderless
inner card, because spacing and grouping separate them without the visual noise
of a rule under every row. Labels, values, and state text stay single-line with
native trimming and full accessible text. If any visible row in a category has
a unit, every numeric field and switch track in that category shares the value
edge; units and On/Off occupy the trailing column. Categories without visible
units reserve no column. Advanced disclosure recomputes this once for the category.
Put Browse beside the default and incomplete download paths,
and beside Add's destination, using the native Windows folder picker. Cancelling
the picker preserves the current path and other unfinished input.
Do not show an engine field dump. Advanced includes Memory and files (Disk write
buffer and Open-file limit), Torrent checking (Checking memory and Checking
threads), interface refresh and speed-history intervals, and the protocol and
storage choices in their task categories. Memory and checking controls follow
the [engine policy](engine.md#disk-write-caching). Each
control names its units, range, default, and speed/resource tradeoff in its
help. Buffer targets do not claim to cap total memory. Settings has a wider
shared header and body. Its window minimum accommodates the complete category
strip in the current language and text size, so every category stays visible
without horizontal scrolling.

The category order is General, Transfers, Speed limits, Appearance, Network,
Schedule, Subtitles, and Advanced. Routine download controls and personalisation
precede connectivity, scheduled automation, optional subtitles, and tuning.
Subtitles puts its main switch first, then supplier and languages, then Existing
videos for Find and Recheck: setup precedes maintenance of earlier downloads.
Appearance keeps language and theme first and groups status-bar choices together.

Transfers leads with Files, keeping each folder selector beside its destination,
then Adding torrents, File selection, Seeding limits, and Watched folder. Routine
file choices precede allocation and naming details. Speed limits shows the current
mode before the optional connection-setup aid, then groups
standard and alternative caps, queue activity, peer connections, and bandwidth
accounting. Schedule has its own category, with actual standard and alternative
caps and Edit at the right of their card header, then the same limits chooser
and current limits as Speed limits, above the existing weekly editor.
Connection setup is a child Settings page because its capacity inputs and
multi-field proposal form one explicit operation. Inputs and proposed changes
sit side by side when they fit and stack otherwise; Apply and Cancel stay in
the footer. Title-bar Back returns to the initiating setting. Ordinary settings
keep their individual commit behavior. The proposal shows current and proposed
values, emphasizes changes, and enables Apply only for a valid change. Preset
identity comes from confirmed settings, not a separate profile. Test measures
real download and upload capacity through the engine's
[temporary suspension](engine.md#connection-measurement). Test and Cancel use
the same action slot, with inline phase feedback and a resumption countdown.
Announce phase changes through the window's existing accessibility path, not
each countdown tick. Measurements populate the proposal; only Apply changes
settings. The provider and traffic cost are visible before testing; native
tooltips supply route and privacy details without moving other controls.
Files holds the default folder, the incomplete-filename suffix, and an optional
separate incomplete folder. Folder and suffix defaults apply to new torrents;
the help makes that scope explicit. Turning off Show dialog when adding torrents
lets torrent files and magnet links add in the background, respecting pause and
queue choices. General holds Windows integration, Notifications, Startup and
closing, Power, Updates, and Library. Windows integration groups torrent and
magnet associations with sign-in startup and its Windows Settings action.
Common feedback and window settings precede infrequent power choices,
maintenance, and optional video information.
Show advanced settings reveals deliberate tuning, not merely less-used features:
start-paused and splash settings, peer-connection caps, external-IP diagnostics,
and protocol, storage, and polling controls. Download locations, seeding limits,
VPN adapter choice, notifications, and sleep prevention remain ordinary settings.
Closing offers confirmation before Exit stops active transfers;
the engine's desktop host owns that decision so it also works without WinUI.
The open window shows the prompt as its own dialog; the host shows a native
prompt only when no window can. Closing only the window does not ask to stop transfers.

Include, grouped by task: the default download folder and Show dialog when adding torrents;
standard and alternative speed limits and when they apply; queue limits for active downloads and
seeds; seeding ratio and time limits; connection limits; the network adapter,
port mapping, listen port, connection encryption and proxy server; the
[notification switches](engine.md#notifications-and-sleep); preventing idle sleep
while downloading on mains power, and also while seeding; Check for updates,
following the [update model](architecture.md#installation-and-updates); and
language. Each uses the existing settings path. Reaching a seeding limit pauses
the torrent; nothing is removed without a request. Resuming that torrent by
hand lifts the limit for it, so it seeds on as asked instead of pausing again.
The sleep switch names its mains-power condition, so a laptop that sleeps on
battery does not surprise its owner. The seeding switch only extends it, so it stays
visible and is disabled while the sleep switch is off; hiding it would move the
page and hide the choice.

Settings > Network has a Proxy server section with one row: an icon for the
engine's check of the proxy in use, once it has ended, the proxy's type and
address or Off, and Edit. Edit opens the Edit proxy server dialog, because the
proxy's five values save together and a half-made proxy would stop every
connection. The dialog's Check connects with the typed values without saving
them; Check and its one-line result sit in the Footer at the left, and Save
and Cancel at the right. Save does not require a check. Choosing None or SOCKS4 disables the fields that it does not
use instead of hiding them, so the dialog keeps its size. While a proxy is in
use, the port and port-mapping rows are disabled, because peers cannot connect
in through a proxy.

General has a Notifications section with four switches in order of
importance: Notify about problems, Notify when a download finishes, Notify
when a torrent is added, and Notify when the window closes and transfers
continue. The order shows which notifications matter most. Turning all four off
stops every Windows notification except failures of something the person just
asked for. Problems are one switch, because no one needs to silence one kind of
problem and keep another.

Scheduler presents one weekly overview with standard limits, alternative limits,
and paused periods, because separate schedules obscure their combined effect.
Time runs left to right beneath a 00–24 hour ruler; each day has one row, and
segments occupy widths proportional to their duration. The week shows saved
periods, and a line marks the current day and time. While a fixed choice
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
content holds the mode ComboBox, days, start and end times, duration and Remove,
so editing controls do not compete with the header's expand gesture.
Every change applies at once, like every other setting, so the
editor has no Save or Cancel. Input the schedule cannot take, such as a period
with no day or a duplicate of another period, stays on screen beside its error.
Closing the period drops that input. Leaving Settings with it is refused, so the
person never leaves believing it runs. Leaving, closing or switching periods,
Add and Remove wait for a save and apply valid input made during it before
replacing the editor, so save timing cannot discard a change. Period changes
run in the order they are asked for, so a click made during one is never lost.
Add saves Monday to Friday, 09:00 to 17:00, with alternative limits, and opens
it; when the schedule already holds that period, Add opens it instead.
The scheduler is one control embedded in Settings, sharing its period and
save owner with the period editor rather than implementing scheduling rules twice.

Dragging empty time saves a period for that day and range and opens it. A click
on a period opens it; a click on empty time closes the open period. Dragging a
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
later Edit or Remove retains its meaning. A fixed choice stops application of
the periods and keeps them for the next time Weekly schedule is chosen. A refused save keeps the attempted
values in the open period beside the error, so the person can correct them or
close the period.
Full-day descriptions say All day rather than midnight to midnight.
Each period has days, start/end times, and a choice of alternative limits
or pause, edited with native checkboxes, TimePicker controls, and a ComboBox.
Standard limits apply outside periods; pause takes precedence on overlap. Overnight
periods end on the following day. Weekly schedule is not chosen by default; the
schedule repeats in local time. Its engine owner preserves individually paused torrents and manual
Pause all, so a scheduled boundary cannot undo the person's explicit pause.

Appearance offers Show external IP in the status bar, off by default. The status
bar shows the IPv4 and IPv6 addresses libtorrent reports, or Not available until
one is known. A changed network clears the old addresses; no external lookup
service is needed. Appearance also offers the application language and Follow Windows, Light, and Dark
through the existing theme owner. General's Windows integration group holds
Start when I sign in. Startup and closing holds Start in the notification area,
Ask before exiting with active transfers, and the advanced startup-paused and
splash settings. Start in the
notification area, off by default, makes
starting TinyTorrent start only the engine in the tray; opening TinyTorrent
while it runs still shows the window. The splash switch, on by default, serves
a person who opens TinyTorrent often and finds the splash in the way. All categories
share one viewport-constrained, centred content column, so a change of
category cannot move the page or push its actions outside the viewport.

Settings offers a Start when I sign in switch and an Open torrents with
TinyTorrent switch covering `.torrent` files and magnet links. Both call the
engine's [registration owner](engine.md#windows-registration) and show its
observed state. Turning the handler switch on finishes automatically when
TinyTorrent is already the default; otherwise it takes the person to the
supported Windows choice and refreshes on return. While Windows still opens
either kind with another app, Windows Default apps offers Open under the switch.
The row remains visible with its action disabled when no step is needed, so
registration changes do not move nearby controls or leave an unexplained gap.
While any torrent or magnet handler starts a program that is missing,
whichever app registered it, the switch's caution line names that program and
the action becomes Repair, which removes the broken handlers, asking Windows for
an administrator when some were registered for all users, and registers
TinyTorrent. Only Repair asks for an administrator, and it then shows
Windows' shield beside its name; the switch never asks. A problem notification about such a handler opens this page.
Turning it off removes TinyTorrent as a handler. Present mixed
file/link defaults in ordinary language only when they need action, without a
registry-status panel.

**Owner ruling:** an entry that starts another TinyTorrent copy is still
TinyTorrent's registration, so its switch shows on, with a caution line naming
that copy's executable; it never shows as unregistered.

While an entry starts another copy, the action under its switch becomes Use this
copy, which moves that entry to the running copy. Each switch keeps one action
that names the next step, so the row never changes height.

Keep Windows Startup settings directly below the sign-in control for Windows'
independent override: a shared settings row names the destination on the left,
with a neutral Open button and Lucide launch icon on the right. When another copy
owns startup, that same row offers Use with a tooltip naming the current copy.
This separates the destination from its action and keeps both aligned with the
other settings, without a floating text link. Open the relevant [Windows Settings page](https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings),
using the general page when a more specific route is unsupported. Show failures
beside the affected action and preserve the user's work. Routine successful
changes need neither a confirmation dialog nor a technical explanation.

### Inspector and edits

General, Files, Peers, Trackers, Speed, and Pieces answer different questions;
request data only for the visible view. Preserve useful data coverage and choose
each layout for its task. The Pieces view is the [Pieces map](#pieces-map).
Peers and Trackers use the existing TableView, sharing its header, selection,
column, keyboard, and scrolling behavior with the main torrent table. Files and
the Add dialog's file browser use TableView's
[hierarchical rows extension](../lib/TableView/docs/hierarchy.md#files-browser-integration)
for wanted, size, progress, and priority content, because folders require
hierarchy. Torrent-specific file commands stay in the application.

**Owner ruling:** the six inspector sections use a native SelectorBar, centred
in one header row between the torrent name and an icon Close button. The owner
could not get used to a left navigation pane, and one row keeps the sections
visible while the working view gets the full inspector width. The person
adjusts the inspector's height with the split.

General follows `app/general-prototype.html`, variant H, in two titled groups.
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
Double-clicking a value opens the place that changes it, as on the status bar:
a rate or the speed limit opens the torrent's Speed limit dialog, which also
shows the global limit; the folder opens Move files; ratio opens the seeding
ratio in Settings > Transfers; and seeds or peers open the connection limit in
Settings > Network. The other values are facts of the torrent or its history,
so they open nothing. Below 760 effective pixels of content width, Properties
becomes one column, so values retain readable space.

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
following [engine moves](engine.md#removal-and-moves). When other
torrents use the files, name them and offer to move them together.

Move files and Delete files are available from selection actions, the row context
menu and command search. Shift+Delete opens Delete files; Delete still opens
Remove, which keeps downloaded files. Delete files groups the torrents by
folder with each torrent's size, shows the torrent count and total size beside
its buttons, states the selected Recycle Bin or permanent-deletion action, and
lists outside torrents whose shared files will be kept. It opens at once with nothing to wait for, and
Delete is always available, following
[the deletion ruling](engine.md#removal-and-moves). Its default follows
[Buttons](#buttons).

Move files shows the current source folders, the chosen destination parent and
the resulting content folders. Include shared torrents is an explicit choice;
the engine rechecks the scope when the command executes. Use files there is a
separate explicit choice with a warning that verification downloads mismatched
pieces over those files. A refusal keeps the destination and choices. Accepted
work appears as Moving files without a file-copy percentage; row errors direct
the person to reopen Move files and choose the folder holding the files.

The Speed view shows the [selected torrent's speed history](engine.md#state-and-work)
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

One page owner allocates table and inspector space. Remember an explicit split
adjustment within current usable bounds; the splitter is keyboard-adjustable and
reports its range. The window's minimum size fits the table and the inspector at
their minimum heights, so there is no second layout for small windows. Preserve
selection and the requested split; do not hide an inspector while continuing its
detail work.

### Pieces map

The Pieces map answers two questions: how far the download has come, and whether
it can finish. It states the answer in words, because a grid of colours alone
leaves the user to work out the conclusion. The smallest square size and the
rare limit follow the previous TinyTorrent map, and the squares are filled tiles without
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
