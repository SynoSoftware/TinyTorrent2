# UI

Interaction and presentation of the WinUI application. The
[architecture](architecture.md) owns product scope; the [engine](engine.md) owns
operation semantics. Existing TableView behavior follows its
[TableView contract](../../lib/TableView/docs/tableview-contract.md).

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
follow [testing](../agents/testing.md) and the repository's desktop-launch restriction.

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
The title-bar Settings, language and theme shortcuts keep the existing custom caption-button style:
full caption height, square hover surface and caption-button width, so it feels
part of the window chrome. This is the exception to standard button visuals.

Use the native platform's expression of Fluent. Web component APIs and examples
do not override WinUI's control semantics or require a second design system.

The visual and interaction reference is `app/prototypes/prototype.html`, variant
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
use Lucide icons and separators between distinct action groups. Every expanded
menu item, including headings, choices and submenu parents, has an icon. Every
menu, MenuBar menus and context menus alike, is a list of entries that its owner
describes: headings, commands, choices, submenus and separators. MenuItems
renders all of them, so two menus cannot differ in appearance. It puts a
separator under each heading and keeps native menu fonts and theme colors;
owners do not create menu items or style headings.
Choice checkmarks do not replace their content icons in View.
MenuItems decides how a choice shows its state. In a menu that mixes choices
with commands or headings, a choice shows its state as its
icon: a checked, empty or mixed square. It is not a check-mark item there,
because one check-mark item gives every item in its menu a second icon column
that is empty beside the others. A menu of only on/off choices, such as View,
uses native check-mark items with Lucide icons, because no column stands empty.

The custom title bar contains one row: app-icon page switch, File/Torrent/View/Help
MenuBar, bounded Search, Settings, the language switch, the light/dark switch, and native caption
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
separate button metrics would break that consistency. Settings uses the Lucide
Settings gear icon. The language button sits between Settings and the theme button,
showing EN, ES, NL, 中文, PT or FR in the existing caption font. It opens the shared
context-menu renderer's language choices, with flags, native names and the current choice
checked. Its centred label uses the shared button dimensions and zero margin and
padding, so the language shortcut stays the same size as its neighbours. The theme button uses
Lucide Lightbulb for switching to light and LightbulbOff for switching to dark, so it
remains distinct from Settings. Its tooltip names the destination theme. It sits
immediately beside the native caption buttons, with no extra gap.
Windows still owns minimize, maximize and close; never simulate those controls.

Inset the app-icon page switch. Search centres on the whole title bar where space
permits, clamped between the menus and optional transfer speeds and caption actions
at narrower widths. Spare space remains draggable. Reserve Windows' caption insets plus
a command buffer. The optional transfer speeds form one compact, right-aligned
group: the gap between download and upload is smaller than the gap from Search.
Reserve width for the whole group, so shorter rates stay together and live
updates do not move Search or the caption actions.
Search is at most 320 effective pixels wide. The minimum window
width accommodates the measured menus, the app-icon page switch, a 200-pixel
search and the caption buttons, including translated labels. Unused title-bar space retains native dragging,
double-click maximize/restore and the system menu; controls receive client input.
The app-icon page switch receives client input. It has no system-menu hit region
or double-click-to-close behavior. Alt+Space and the unused caption area retain
Windows' system-menu behavior.

There is no workspace navigation pane, identity text, command overflow or permanent
Exit button. Language and theme are also chosen in Settings; their title-bar shortcuts
use the same owners, so both entry points preserve immediate, in-place updates.

**Owner ruling: Torrents and [Library](library.md#presentation-and-interaction)
are peer pages.** The app-icon position is their direct switch, with no separate
selector or extra row. It is an ordinary caption button that shows the current
page's icon and name: the canonical TinyTorrent logo and Torrents, or the
existing Library colour SVG and Library. It has the caption buttons' hover and
pressed states. While it is pressed, its icon shrinks to 80% and its icon and
name dim, because its colour icons do not follow the pressed text colour; on
release they spring back and the page changes, as taskbar icons do. Reduced
motion keeps only the dimming. It runs the same command as Ctrl+1 or Ctrl+2. Its width fits the longer
page name, so the menus do not move when the page changes. Its tooltip and
accessible name identify the destination and its shortcut. The logo represents
the app's Torrents mode, never an individual torrent or Show in Torrents action.
Global search also offers both pages. The second menu belongs to the
current page: Torrent on Torrents, Library on Library. View shows the current
page's presentation, with Filters first in both modes. Filters uses one command
and one shared pane visibility state: changing pages changes its content, not
whether it is open. Each page keeps its own filter criteria. File and Help do
not change.

On Settings and About, a Back arrow at the left of the title bar, before the
app icon, and Alt+Left return to the page they were opened from, with the
existing draft guards. Show in Torrents on Library also shows Back on Torrents,
returning to Library. The arrow sits in the title bar, as in Windows
Settings, so the page keeps its full height. Keep continuous acrylic.

One window navigation history owns the current destination, Back availability
and return destinations, including Connection setup inside Settings. Explicit
Torrents/Library navigation selects a workspace and clears return history;
Show in Torrents is a contextual visit that retains Library as its return.
Secondary pages retain their callers. Selecting a destination already in that
history returns to it without adding a duplicate. Back consumes a destination
only after departure guards accept it; a refusal preserves the current page
and history. While departure is pending, further navigation is unavailable.

Breadcrumbs describe page hierarchy; Back describes navigation history; related
settings provide shortcuts. A cross-link does not make its destination a child
of its source. Speed limits to Schedule selects Schedule in the sidebar and
shows Schedule as the page title, with no breadcrumb. Speed limits to Queue
selects Transfers, expands Queue and scrolls it into view, with no breadcrumb.
Connection setup is a child of Speed limits and shows Speed limits > Connection
setup. Back from Schedule opened through Speed limits returns to Speed limits
and restores its scroll position, so a shortcut does not lose the caller's place.

The title-bar arrow and Alt+Left use this same owner, so an empty history
both hides the arrow and disables Back. Page content and inspector state stay
with their existing owners when navigating.

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

**Owner ruling: colour marks transfer direction and the states that need
attention, so the window is not uniformly grey.** Every transfer rate and its
arrow take its direction's colour: download the accent text brush, upload the
upload brush in `App.xaml`, which has Light and Dark values and a system text
colour in High Contrast. The speed graph's lines, markers and legends use the
same two colours. A downloading torrent's state icon takes the accent, and an
errored torrent's state icon and progress bar take the critical colour. The
Downloading and Errors filter counts take the same colours while they are above
zero. The port icon is success green
when the port is open and caution amber when it is not confirmed or the saved
adapter is missing; a proxy route stays neutral. Active alternative limits show
a caution amber icon. Seeding and complete torrents stay neutral, because they
are the normal state of most rows and Fluent reserves semantic colour for
information that matters. Arrows, labels, icon shapes and line patterns keep
every meaning without colour. Text, surfaces, commands and other severity
feedback keep the platform resources.
The native list selection fill uses a restrained system-accent tint from the
same theme dictionaries, so selected rows stand apart from neutral surfaces
without extra row chrome. High Contrast retains the system selection colours.

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

**Owner ruling: help text on a work surface must add real value.** Help text
explains what a control or choice does. A work surface, such as the main
window, a dialog or Settings, shows labels, values, states and errors, and
shows help text only when it tells the person something the label and layout
cannot, such as a consequence they would not expect. That text follows
[Fluent 2 content design](https://fluent2.microsoft.design/content-design): one
short, plain sentence that never restates the title or label it belongs to,
and that no tooltip beside it repeats. Every other explanation goes in a
[tooltip](https://fluent2.microsoft.design/components/web/react/core/tooltip/usage/),
available on keyboard focus as well as hover. Visible text is read on every
visit, so a sentence that repeats the label or says what the layout already
shows costs the person time and pushes the controls apart. Errors stay visible
beside the control they concern.

**Owner ruling: work-surface text does not wrap, and no line is left empty to
separate text.** A label, value or message takes one line. A value too long for
its space trims with an ellipsis, and its full text is available through a
tooltip, focus, selection, copy or the accessible description, rather than
hover alone, so trimming is always preferred to wrapping. A tooltip keeps its
text on one line too. Spacing and grouping
separate content, not blank lines. Wrapping changes a line's height with its
text, which shifts everything below it as the text, the language or the window
width changes. A person scans a surface by its lines, and wrapped or empty lines
break that scan.

Dialog bodies are the exception for prose: sentences supplied as plain strings
wrap so consent and confirmation text remains readable. Lists of facts, labels,
values and field errors keep one line per fact and expose their full text when
trimmed. The shared Dialog template owns this distinction.

The approved Library Sections composition also wraps synopsis prose and its
filter content. Genre links flow as whole names onto further lines so every
filter remains available; they do not turn ordinary metadata fields into wrapped
rows. Artist and Album retain their whole filter value in their content section.

Every button carries a Lucide icon, as [Buttons](#buttons) rules; elsewhere,
add an icon when it helps recognition or scanning. Keep Lucide for
application-authored icons and retain platform-owned control glyphs. Preserve
native sizing, padding, hit areas, and button semantics. Decorative icons stay
out of automation; icon-only actions have explicit accessible names.
Actionable controls carry a stable `AutomationProperties.AutomationId`, so UI
checks find them; decorative content has none.

**Owner ruling: an application-authored icon is larger than the text beside it,
and it never enlarges its container.** The icon may extend into the container's
padding but never into its margin, so it does not touch neighbouring controls.
This holds for every Lucide icon in the product. Icon size is part of the
product's visual language: a larger icon is found at a glance, and rows and
buttons with icons keep the same height and alignment as those without.
Platform-owned glyphs, such as menu-item icons and control chevrons, keep their
native size.

<a id="icon-sizes"></a>Icon sizes come from eight role tokens in `App.xaml`, so one
role has one size and a size changes in one place:

- **Chevron**, 12: chevrons the application draws in place of a platform one.
- **Inline**, 16: an icon in a line of body text, including status bar, fields,
  table cells, notices, labelled buttons and title-bar buttons, whose
  neighbours are the window's small caption glyphs.
- **Button**, 20: the glyph of an icon-only button.
- **Header**, 20: the icon that leads a group, section or card.
- **Statistic**, 24: the icon beside a statistic's caption and value.
- **Surface**, 24: the icon that identifies a surface, such as a dialog title,
  the inspector heading, the workspace switch or Settings navigation.
- **Toolbar**, 24: toolbar buttons.
- **Large**, 32: empty states.

**Owner ruling: an icon that leads stacked lines is centred on all of them,**
such as a caption and its value or a title and its summary, so it identifies
the whole block rather than its first line. The InfoBar keeps WinUI's own
layout, which places its severity icon beside the title.

Each role takes the largest size its containers hold without growing. The
filter and inspector strips keep their sixteen-pixel glyph, because their clip
is part of the strip's geometry. Pictures, such as the logo, page artwork and
application icons from Windows, are not icons and keep their own sizes.

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
Torrent and file names and Library titles use the shared semibold identity
style, so the subject leads each row. Paths, types, years and timestamps use
the secondary text brush; rates and operational state retain ordinary body text.
The inspector's inline filename uses Body Strong so it identifies the subject
without competing with its tab navigation. Informational surface and section
icons use the secondary text brush; transfer and severity indicators keep their
semantic colours.
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
Section owns Inspector group headings, optional title details and actions, and
the gap before their content. Library and Torrents consume that same template;
the approved inline title/type composition shares a text baseline, an exception
to the general vertical-centre rule for controls and icons. The shared template
owns that alignment rather than per-screen offsets.
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
above and below. The footer is one item: the Don't show this dialog again check
box, one fact such as a count and total size, or a check's result with the
button that runs it. Dialog's footer type has only these kinds and refuses to
replace the check box, because two stacked items crowd the row. A dialog with
the check box shows its other facts in the body. The buttons keep the Windows order: the action first, then Cancel. A
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
  person who learns Stop once recognizes it everywhere, while two names for one
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
The caption and workspace share that same continuous acrylic surface and tint.
The dark tint is neutral grey, with only a ten-percent overlay so the desktop
acrylic remains visible. Neither has an independent
opaque fill or a contrasting title-bar band. Native
control states and transient surfaces keep their own necessary layering.
Menus, flyouts, and other transient surfaces keep the platform's acrylic. A material
is never a substitute for readable content or hierarchy.

Use theme resources for values that change with Light, Dark, or contrast themes.
Respect system accent, contrast, animation, and text settings. Preserve the
expected rest, hover, pressed, selected, focused, disabled, loading, and error
states of standard controls. Meaning must survive without color or motion.

Standard controls retain their built-in animations. Settings expanders retain
the native header and accessibility, with the owner's LabForms drawer animation:
the clipped body animates its height over 260 ms opening and 220 ms closing,
then returns to automatic sizing so following cards move with the reveal.
Motion expresses the
relationship between views, so one gesture never produces competing directions.
Torrents sits to the left of Library: their table and button bar slide as one
surface, left when entering Library and right when returning to Torrents. The
filter and inspector content slide with them in the same direction, so the
whole workspace moves as one gesture. The title bar and the filter and
inspector frames stay in place.

Ordered tabs use horizontal motion in their visible order and reverse when
returning, so direction has an observable meaning: the previous tab's content
leaves while the chosen tab's content arrives from that tab's side. Torrent and
Library inspector tabs use the same slide. Changing the selected torrent
or Library file gently fades its new details into place once; live statistics
and information updates never replay that entrance. These changes do not fade
through a blank surface. Shared motion uses 250 ms with cubic ease-out, clipped
to its host, without bounce or decorative staggering. Rapid navigation continues
from the current position or opacity rather than snapping back to the start.
One motion owner supplies the shared timing and easing. Motion never delays input
or a state change. When the user turns animations off in Windows, the same
changes stay clear without substitute motion.
**Owner ruling: Settings categories enter from outside the content viewport,
without fading.** A short nudge makes the replacement appear instant, and a
fade can superimpose pages. The title, summary and settings travel together by
the full viewport width, in navigation order. Entrances start off-screen before
layout can present the new content. Content creation and layout finish before
navigation animations start, so loading does not consume their visible duration.

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

**Owner ruling: use TextBox for all numeric input, never NumberBox.** The
standard single-line TextBox provides a clear button and keeps editing consistent
with other fields. Use numeric InputScope as a keyboard hint; the existing field
owner retains parsing, ranges, errors, and commit behavior. Do not replace the
native template or duplicate validation in the control.

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
moving to another field does, and restores the saved value of an invalid one.
**Owner ruling: Settings closes without
waiting for saves; failures are notifications.** Navigation and category changes
capture valid input and continue immediately. Accepted saves finish in their
existing owners, and failures use the application notification even after the
person leaves Settings. A reply belongs to the input submitted, so it cannot
mark newer input as refused or discard it. Window shutdown still drains accepted
work after hiding the window.

An unavailable engine is different from a refused value. Back preserves unfinished
ordinary Settings input in the window and leaves the page; closing the window may
discard that input without a prompt. Neither operation waits for reconnection,
because ordinary Settings must not trap the person in an unavailable application.
A picker or an operation already in progress finishes before close continues.

**Owner ruling: leaving an editor applies its input, and only Add and Move
ask.** Fluent 2 applies a change without a confirmation step, so leaving a
schedule period or a tracker list applies it, as leaving a setting does, and
failed file choices are sent again. Schedule save, Add and Delete failures use
notifications and never hold Settings navigation; leaving drops an incomplete
or duplicate period. Nothing changes under the person's hands:
an edit that cannot be applied, because it is incomplete, invalid or refused,
keeps the person at the editor with its error, except for Settings and its
schedule, whose failures follow the notification rule above. A row that leaves
the table's view, such as a torrent that
finishes under the Downloading filter, leaves the selection but not the
inspector: the inspector stays on that torrent, with any unfinished edit. When
the engine or the torrent is unavailable, file choices are dropped on leaving,
because Retry is unavailable and they have no Cancel, and closing the window
drops any edit that cannot be saved.

The account dialog, which asks for a subtitle supplier's or Library provider's
key or sign-in, requires explicit Save and discards its draft on window close,
because it stores account details the person has not confirmed.
Supplier changes preserve the automatic subtitle switch.

Add and Move still ask, because applying them would start a download or a file
move that the person has not confirmed. Save runs the editor's own action, Add
or Move; if it fails, the editor stays open with its error. Discard drops the
draft and continues. Cancel keeps the editor and focus. Deleting files
shows its own confirmation unless the person has chosen not to ask again.

On window close, ask only for work the person has actually entered: a torrent
source or a move destination. Opening an untouched editor or changing Add or
Move options without a source or destination does not justify a question. The
question names the unfinished work and its primary action: Add or Move.
The Save and Discard tooltips say what each does, following the
[help-text ruling](#text-icons-and-typography). Cancel is the default button, so Enter cannot unexpectedly add a
torrent, move files or discard input while the person is trying to close.

Send only intended changes through the [engine's edit path](engine.md#committed-edits).
Refresh confirmed facts without replacing the user's current input. On refusal,
keep that input and explain the actionable reason at the affected control,
or in the application notification for Settings.
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
Don't start and Add/Cancel, because choosing Add explicitly asks for that workflow.

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

The shared `Pane` control owns the Filters and Inspector surfaces: its template
uses the Settings cards' `LayerOnMicaBaseAltFillColorDefaultBrush` background,
a one-pixel `SurfaceStrokeColorDefaultBrush` outline that stays visible in Light
and Dark, and `OverlayCornerRadius` corners. Both end four pixels
above the workspace's bottom edge. Filters
retains its four-pixel side insets; the Inspector has a four-pixel right inset
to match the table header's right edge. The shared Filters body owns a
eight-pixel inset at the top and both sides for Clear and both modes' scrollable
choices, so their visible edges align. Torrents compensates the native item's
four-pixel horizontal background inset to match Clear's width. The platform owns the
fill and its theme and contrast fallbacks; the pane needs no separate
palette or floating shadow. Both panes share this material so their surfaces stay
consistent across Torrents and Library. In both modes, Clear sits directly below
the Filters header and above the scrollable choices. It is a compact,
full-width icon-and-text action aligned with the filter rows, using the shared
ActionButton's native neutral fill and outline in both modes. The button remains
visibly a button when enabled or disabled, rather than switching between a
transparent action and a filled disabled surface. It retains native interaction
states and stays visible but disabled when the existing command has no active
filters to clear, so the layout never shifts.
The same template owns the header's minimum height of 40 pixels, padding and bottom
divider. Pane also owns header composition: identity at the left, navigation
centered in the remaining space, actions at the right. Identity yields space to
navigation and actions and occupies at most half the header. Without navigation,
the supplied header retains the full width, as Filters does.
Each caller supplies its header, navigation, actions and body content, so changing the pane's
chrome changes both surfaces together. Titles share `TinyTorrentGroupTitleTextStyle`.

The Filters toggle opens a collapsible native pane with All, Downloading,
Seeding, Stopped, Queued and Errors choices and live counts. Each choice has one
monochrome Inline Lucide icon; its label and count retain native row alignment.
Closing the drawer
keeps the chosen criteria, identified by the status label.
Escape closes the drawer when focus is inside it. Clear filters lives inside
the drawer and calls the existing filter owner. Downloading includes metadata
acquisition and Seeding includes completed torrents. Stopped holds only
torrents with the Stopped status, because a torrent that a session pause holds
starts again by itself. One shared owner keeps pane visibility and its expanded or compact
presentation in both modes. Hiding preserves that presentation for the next show.
Close hides the pane completely and unchecks View > Filters. Minimize uses the
Lucide Pin icon and collapses the pane to a ten-pixel bar with a vertically
centred Lucide EllipsisVertical, keeping View > Filters checked. The ten-pixel
strip contains an eight-pixel button and a two-pixel right gap, separating its
highlight from the table. The icon clips within the button without widening it;
clicking the button expands the pane. View > Filters
restores a hidden pane's previous presentation and hides either visible state.
Explicit filter links expand the pane to reveal the selected choice.
Status and progress remain visible and
sortable in the table. Tracker information
belongs in the selected torrent's inspector. TableView owns generic interaction.
Settings opens from File and About from Help. Close and Exit
are File commands and keep their existing pending-work and draft guards.
Help places subtitle setup before application updates and About. Subtitle setup
opens Settings directly at Subtitles through the existing category navigation,
so the command never adds an introductory stop. The dismissible Settings index
card includes one concise benefit subtitle beneath its heading, using the same
secondary text style as the category cards. This requested introductory text is
an exception to the work-surface help-text rule. Native add/remove transitions
close the card and move the remaining categories into place without a layout jump.
Keyboard and search paths invoke the same owners. About shows the product
identity and running version on the same acrylic surface. The current page
belongs to the main view model.
About uses the approved split identity composition: the logo and name share a
baseline, the product description and two-line promise follow, and Version is a
plain label and value beneath them. The telemetry promise carries an asterisk;
its localized tooltip and automation help explain the reporting protections and
the [Microsoft runtime limitation](architecture.md#privacy), including usage
diagnostics during normal operation. The composition centres in the available
page viewport and scrolls when it no longer fits. A vertical divider separates
this open identity column from three Settings link rows: Synapse, source code and Credits.
At narrower widths the columns stack and the divider becomes horizontal.
Credits is a child destination in the existing navigation history and uses the
existing page slide. Its breadcrumb and the caption Back share that history;
returning to About restores focus to Credits. The shared SettingsLink owns the
row presentation for Settings and About, with an external-link glyph for
websites and a chevron for in-app navigation. Credits reuses SettingsSection
expanders and divided SettingsRow controls, with core technology first, then
artwork and optional providers. A group with one contribution is a direct link
row. SettingsRow owns the optional leading artwork slot spanning its title and
description; TMDB uses it for its required logo. Required provider notices sit
inside their rows. Licence links use the main window's existing external-open
owner, and the installer packages required distribution notices. Its scroll
viewport reaches the window edge, while the content stays bounded and centred,
so the scrollbar never overlays the contribution surfaces.
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
left, Ratio, Seeds, Peers, Added, and Completed; the person can hide, show, and reorder them.
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
[stopped or running state](engine.md#state-and-work). Exit is in the window as
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
holding Add, Magnet, Start, Stop, Open, Properties, Verify, Remove and
Delete as AppBarButtons in
groups split by AppBarSeparators. Selected connections map and All connections
map sit at the row's right end. **Owner ruling: the toolbar buttons are
icon-only,** because text labels make the interface look cheap. Each button
keeps its label as its accessible name and its text in the More menu, and its
tooltip names it with its shortcut. When
the window is too narrow, the CommandBar moves the commands that do not fit
into its More menu instead of clipping them. The menus keep every command. Copy magnet
link stays off it because its link icon is Add magnet link's. Add comes first and stays enabled
without a selection; with the toolbar hidden, the File menu, its
shortcuts, drag-and-drop and paste still add torrents. The other buttons
act on the selection and are disabled without one, as their menu items are.
Settings stays available in the title bar even when the toolbar is hidden.
The toolbar is one Tab stop, and the arrow keys move inside it. It starts visible,
and the window layout remembers it.

Dropping torrent files or magnet text on the window, or pasting them with Ctrl+V
while the table has focus, follows the [Add policy](#add): several sources are
added directly; a single source follows its torrent-file or magnet dialog preference.
An empty list says how to add a torrent.

The status bar answers, in order, what the current view contains, which
background operations are underway, and what is happening to torrent transfers.
A full-width, one-DIP top divider uses `DividerStrokeColorDefaultBrush` to
separate it from the workspace while retaining the continuous acrylic surface.
Its groups stay in one horizontal row, with collection context anchored left,
work in the middle, and the torrent session anchored right. It uses shared Body text, Lucide
icons and the established direction/state brushes. Its commands cancel tasks
or open their list when concurrent work cannot fit; settings, filters and
updates retain their visible command homes.

Context, background activity, and transfers are content-sized top-level groups.
Distribute spare width between those groups only. Within the activity group,
keep processes together in registration order, with subtle vertical separators.
Each process keeps its icon, label, progress and Cancel close together; neither
processes nor their internal controls consume distributed spare width.
Progress sits below the label and count. A 24-DIP icon spans both rows, with
Cancel centred alongside them, so the icon identifies the whole process.
The shared work presenter uses this layout in the footer, work list and Settings.
Keep flexible space on both sides of the activity group even when collection
context is absent, so work stays in the middle on Settings and About too.

Torrents shows its actual displayed/total torrent count when filtered, the
filter name and selection. Title-bar command search does not filter the table.
Library shows its published configuration, file count, applied filtering and
selection. Count files, not distinct movies; publish filtering with the rows,
so a pending query cannot relabel old results. Before a usable Library result,
show the configuration only, never an invented zero or a loading message.

Torrents reads `84 torrents`, `12 of 84 torrents · Downloading` and
`3 selected`. A filter with no matches reads `0 of 84 torrents · Downloading`
and the table keeps its empty-state action. With no managed torrents it reads
`0 torrents`, and before the first coherent snapshot `Connecting…`, never zero.
The bar carries no aggregate ETA, average progress, total ratio, peer count, DHT
count or lifetime bytes; the inspector and Speed limits give those facts their
context.

Library reads `Videos · 240 files`, `Music · 86 files` and `412 files` for
Files, which does not repeat the configuration's noun. A selection appends
`2 selected`; sizes stay in the table and inspector. A query or facets append
`Filtered` and one short criterion when it fits, such as `Genre: Drama`. No
matches read `Videos · 0 files · Filtered`, which differs from an empty Library;
a real failure reads `Library unavailable`. Counts cover current Library entries
only: no unfiltered denominator comes from a facet count or the Torrents
collection, and the bar shows no total bytes, metadata-coverage percentage or
subtitle-coverage count. The applied `RowsConfiguration` supplies the
configuration, so no second configuration state exists, and the filtered
indication describes the query that produced the displayed rows.

Settings shows its current section, such as `Settings · Network`, with a Settings
icon in the left group, from the navigation state; it has no collection count.
A selection count sits beside the collection count, not at the far end of the
window. Counts refer to the table, and rates always refer to all torrents. About has no collection facts. Retained disconnected data is
labelled Last known, while initial torrent connection says Connecting.
About and Credits show their navigation location, process memory and all-time
download/upload totals in the existing status bar. Appearance's Show memory
usage, off by default, shows the same memory item at the start of the transfer
group on every other page. The main view model samples resident working sets
off the UI thread, only while the memory item is shown;
the pipe client identifies the connected engine process. Tooltips state that
working sets include shared pages. All-time totals come from the same confirmed
engine snapshot as Settings, with unavailable values during disconnection.
The status bar retains ownership of presentation and overflow: memory yields
before background work, and totals use the existing transfer icons and fitting
path. This changes neither work registration nor its cancellation controls.

Rates describe all torrents and keep their down/up icons and colors. Numeric
caps appear only while confirmed and unpaused. A directional cap reads
`↓ 1.2 MB/s of 5 MB/s` and shows only that direction's cap. In Library the
transfer group is explicitly labelled Torrents, remains present across zero-rate
samples while managed torrents exist, and yields before Library work. At narrow
widths, rates may omit numeric caps while showing Limits on, which appears
while an effective nonzero cap applies and transfers are not paused; that marker
disappears with the rates. A cap describes the applied policy, not proof of why
a transfer is slow. Disconnection shows `Last known · 84 torrents`, `—` for
rates, and suppresses stale restrictions and healthy connection icons.

Each rate keeps aligned digits and a compact width measured for the active
format, units and cap, recomputed when language, scale, unit range or cap
changes. The bar reserves no two fixed 216-DIP speed fields and does not size
its right group for the longest hypothetical status.

The session uses the engine's confirmed pause reason, and the status view builds
no second precedence algorithm. Manual pause says All
paused and omits caps, and rate digits yield before this explanation; scheduled
pause names its end when known and otherwise says Paused by schedule; a missing
adapter says Paused · Adapter unavailable; connection setup says Paused for
connection setup and is never labelled a manual pause.
Alternative limits uses the caution Turtle and its known scheduled expiry; the
standard profile gets no label, because the caps explain it.
Pause takes precedence over limits. Full detail and existing announcements
retain the current notification owner. Library scopes these states to Torrents
and words them `Torrents paused`, `Torrents paused until 18:00` and `Torrents
paused · Adapter unavailable`. A missing-adapter hold gets no InfoBar of its own;
the existing Resume notice may explain a refused resume, and Settings > Network >
network adapter is the repair path.
The routine port/proxy item is shown only on Torrents, at the end of the session
group, and says nothing about general internet health or privacy. An unavailable
adapter stays an explicit torrent hold on Library.
Its short labels are `Others can connect`, `Connection unconfirmed`, `Using proxy`,
and `Network unavailable`. `Others can connect` uses the existing success icon,
`Connection unconfirmed` the caution icon and `Using proxy` stays neutral; the
text is neutral in every case. The item yields as a whole icon-and-text item
before core facts. Tooltips explain the observed incoming connection,
unconfirmed listening port, proxy route, or unavailable selected adapter, so
the status stays readable without claiming an unconfirmed firewall problem.

**Owner ruling, extended 2026-10-13:** Long-running background work appears on
every page with native progress and an adjacent Cancel X when cancellable.
This includes metadata batches and IMDb preparation; accepted subtitle work
also remains visible. Torrent transfers and checks keep progress in their rows.
The shared background-operation owner supplies coherent lifecycle/progress and
cancellation facts; the status bar owns their visual presentation. Concurrent
operations keep separate identities and cancel targets in stable order rather
than rotating messages. Each X has its own accessible name and tooltip,
requests cancellation from its operation owner, disables while cancelling and
remains visible until cleanup finishes. Recover focus to the next usable task
control, the work-list button or the page when a focused control becomes
disabled or disappears. Operations report their result through the existing
toast notification owner after cleanup, including failures and cancellations,
so a disappearing progress bar never leaves its outcome unexplained. Each
operation's owner reports success only when it tells the person something
new: work they started and wait for, such as a metadata batch, a manual
subtitle search, a data download or a protection reset, and an update check
that found a new version. Work that runs by itself, and work whose result
already shows in place, report only failures and cancellations: automatic
metadata, subtitles and file properties, subtitle file scans, torrent
additions, a details refresh, recovery and a deleted dataset. Background additions retain the
engine's notification policy. Results include available counts or error details and follow later
language changes.

Use the stock WinUI 3 ProgressBar template, brushes and animation. Metadata
shows completed attempts over the accepted total, including unmatched files;
never count the file currently being processed as completed. Its precise
fraction remains readable when the redundant graphical bar yields. IMDb
names its current phase, and every phase that counts something shows a
fraction: downloading uses the length the response declares, parsing reports
approximate progress through that source, and the episode and search-index
phases count the rows they write. Only optimization, the final flush and
installation are indeterminate, because SQLite and Windows report nothing to
count for them. Per-phase percentages are never presented as one overall
percentage. A
confirmed wait is named as such; accepted subtitle work alone does not prove
active network searching. A video-provider wait appears on Library's Videos view
only when explicit work does not occupy the space, and names the actual provider,
such as `Waiting for TMDB`; the detailed reason and recovery stay with the
existing feature surface. Music and Files show no ambient video-provider state.
Automatic download preferences stay in Settings · Plugins, because an intentional
choice needs no ambient notice.

An accepted metadata batch reads `Metadata · 2 of 7 processed` in Library and
`Library metadata · 2 of 7 processed` on Torrents, Settings and About, so it is
never mistaken for torrent metadata. Processed means completed attempts,
including no match or local protection, not successful matches. When all remaining
work waits, the text is `Metadata waiting · 2 of 7 processed`. A provider or
retry time is named only when the batch owner establishes that it explains the
whole wait, because a batch can contain several providers; the queue head and the
default provider's warning never establish it. The batch takes the activity slot
before ambient provider state. A displaced provider warning stays in the
inspector and Settings, and no text claims that provider blocks a different
batch. Competing messages never alternate on a timer.

Metadata work uses the Lucide Download icon and a confirmed wait uses Clock;
accepted subtitle work uses Captions. The X means **Stop fetching metadata**, not
dismiss, and keeps its native focus, hover and pressed states, a full accessible
name and a matching tooltip. After completion or failure the footer returns to
its resting context, without a lingering Ready. Subtitle work reads `Subtitle
work` when only accepted unfinished work is known, `Subtitles waiting` for a
confirmed blocker, and `Finding subtitles` only when its owner establishes active
searching; resumed persisted jobs are not proof of live network work. Found
counts successful outcomes and is never a progress fraction. Explicit
operations outrank ambient video-information state.

IMDb phases read `Downloading IMDb data · 284 MB` (determinate only with a
trustworthy byte total), the named parse or index phase, and `Finishing IMDb
preparation`, after which cancellation is unavailable because publication has
begun. `Cancelling IMDb import` disables Cancel and keeps the item until cleanup
finishes. Success removes the item and existing completion feedback owns the
outcome. A requested cancellation is not presented as a failure; a failure uses
the existing actionable feedback, never an endlessly busy bar. A ProgressRing
never replaces the bar: Windows guidance recommends the indeterminate bar for
work that lets the person keep using the app. An explicit IMDb import differs
from opening Library, which acquires no loading state.

The footer measures text in the current language and scale. Remove optional
update, default-download-volume space, torrent IP and routine port diagnostics
before core content; remove ordinary rates before active work and holds.
Shorten wording, then remove redundant bars and decorative icons before
sacrificing task identity, exact metadata counts or cancellation controls.
Selection and collection context yield when active operations need their room.
Do not replace essential meaning with anonymous icons or ellipses. When task
groups still cannot fit readably, retain the explicit metadata batch and its
Cancel inline, with a labelled work-count button opening a native Flyout of
all active operations in stable insertion order. Its count includes the inline
batch. Each row consumes the same operation and cancellation command; no
aggregate percentage or Cancel is introduced. A successor batch takes the
inline slot while its settling predecessor remains in the list. With no
successor, the settling batch remains inline until cleanup ends. The reporting
contract and the list of registered work are in [Background work](background-work.md).
Narrow concurrency layout requires native review before release.

At narrow widths the shortened forms are `Library metadata · 2/7`, with
“processed” in its accessible name, and `IMDb import`. Numeric caps move to the
rate's accessible description while Limits on stays visible. The metadata bar
yields before its exact fraction, and decorative icons before operation
identities and Cancel; IMDb's indeterminate indicator stays. Filter criteria
collapse to `Filtered`, and a compact collection phrase (`12/84 torrents`,
`Videos · 24 files`) keeps the selection. Long provider, filter and adapter names
ellipsize, with the full state in UI Automation and the Settings or filter
surface. At extreme text scale the collection count goes before an active batch
and its X or an active hold; the page, configuration and selection count stay
if they fit. A batch, selection or pause never becomes an unexplained glyph.
The last reduction step is not proof that content fits: the result is verified.
The batch's Stop target stays stable as its processed count changes, using the
digit width of the actual total.

The bar does not guarantee that every fact fits at every width. The desktop's
minimum width stays; optional status never raises it. Enlarged text can raise the
single row's height but never produces a second line, horizontal scrolling or
overlap.

Free-space and external-IP appearance settings remain opt-ins and describe
their scope, including that they are the first items omitted at narrow widths.
Free space shows a drive icon with, for example, `120 GB free`; it belongs to the
default download destination's volume, its tooltip names that folder and drive,
and it never implies Library space or the selected file's disk. Torrent IP reads
`Torrent IP: …` from reported torrent-network addresses. An unavailable
measurement says so instead of showing a stale healthy value. Both yield when the
footer has insufficient room. Update available remains passive discovery when
space permits, at the lowest layout priority, because no other passive update
notification exists; Help > Update owns its action. Passive footer text has no
hidden double-click navigation; the Windows status bar guidance discourages it,
and settings, filters and updates have visible keyboard-accessible homes.

Decorative icons are excluded from the accessibility tree. Each semantic group
has a complete accessible name, such as “12 of 84 torrents shown; Downloading
filter; 3 selected” or “All torrents, download 1.2 megabytes per second, limit 5
megabytes per second”. Static text is not exposed as a button. Significant
automatic holds and batch completion are announced once through the existing
notification owner, without duplicating an InfoBar announcement. The X takes part
in normal Tab order and activates with Enter or Space. Essential information and
recovery are available without hovering; tooltips supplement visible text and UI
Automation. Tooltips and UI Automation preserve full descriptions without
announcing every rate or processed file.

The bar adds no engine messages, polling, provider requests, settings or
scheduler. It derives its phrases from `MainViewModel` torrent and session facts,
the Library browser's applied rows, selection and ambient state, and the
`BackgroundWork` observations, and caches no second copy of engine or Library
state.

The bar continues the window surface: no raised card, acrylic island, rounded
container or accent-filled strip. It is about 40 DIP high at normal text scale
with a 16 DIP horizontal inset, vertically centred, and grows for scaled text
and the native Stop button instead of clipping. Text is the shared Body status
style at regular weight, never a smaller font to relieve crowding. Primary text
carries count, selection, activity and holds; secondary text carries optional
scope detail and background torrent telemetry. Small middle dots join a context
phrase; groups sit about 16 DIP apart and an icon sits 6–8 DIP from its label.
One leading Lucide List marks Torrents, and Film, Music or Files marks the
Library configuration; numbers carry no icons of their own. Transfer arrows are
Lucide down and up arrows at 16 DIP, holds use Pause, Clock and the
caution-coloured Turtle, and filtering is stated in text because a funnel alone
cannot say which scope the count describes. The bar has no animated
rearrangement, pulsing dot or rotating message.

Colours follow the owner ruling: the download rate and arrow use
`AccentTextFillColorPrimaryBrush`, upload uses `TinyTorrentUploadBrush` (#7756A3
Light, #C5A3E6 Dark), the alternative-limits icon uses
`SystemFillColorCautionBrush`, and the port icon uses success or caution while
proxy stays neutral. High Contrast uses system colours. Arrows, icons and text
carry every meaning without colour.

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
so Ctrl+P, Ctrl+S, and Ctrl+R are free for Stop, Start and Verify. Torrent
shortcuts act while the table, selection toolbar or inspector has focus; an
editor keeps its own keys.

| Key | Action |
| --- | --- |
| Ctrl+O | Add a torrent file |
| Ctrl+Shift+O | Add a magnet link |
| Ctrl+V | Add the pasted magnet link or torrent file |
| Ctrl+K, Ctrl+F, Ctrl+E | Focus global search outside Library, or the Library filter |
| Ctrl+A | Select all torrents |
| Enter | Show and expand the inspector for the selected torrent or Library file, as double-click does |
| Ctrl+S | Start |
| Ctrl+P | Stop |
| Ctrl+Alt+S | Force start |
| Ctrl+M | Connections map for the selected transfers |
| Ctrl+Shift+M | Connections map for all transfers |
| Ctrl+R | Verify |
| Ctrl+Shift+S | Resume all |
| Ctrl+Shift+P | Pause all |
| Ctrl++ / Ctrl+- | Move up / down in the queue |
| Ctrl+Shift++ / Ctrl+Shift+- | Move to the top / bottom of the queue |
| Delete | Remove |
| Shift+Delete | Remove, deleting files permanently |
| Ctrl+, | Settings |
| Alt+F4 | Close |
| Ctrl+Q | Exit |

**Owner ruling:** Alt+F4 closes the window and Ctrl+Q closes the window and the
engine; the File menu shows both keys. Alt+F4 is how Windows closes a window and
Ctrl+Q is how desktop applications quit, so the menu names keys people already
use. Ctrl+W does nothing, because in Windows it closes a document or a tab, not
the application.

Remove keeps data unless the person ticks a check box. One Remove torrents
dialog confirms every removal, by default, with the affected torrents, a Remove
button and a safe Cancel action. Remove confirms because a removed torrent
cannot be restored without its torrent file or magnet link. Two check boxes say
what else goes: the downloaded files, with their total size, and the .torrent
files the torrents were added from, with their size, shown only when a selected
torrent's .torrent file still exists, with n of m when only some do. As in File Explorer, the key chooses the
deletion mode and the dialog never asks it: Delete, or a click on Remove, sends
ticked files to the Recycle Bin, and Shift with either deletes them permanently.
The check box labels state that mode, so the button stays Remove. Only Remove
saves the check boxes for the next removal. Add, Remove torrents and the
window's Exit confirmation use the shared dialog footer's Don't show this
dialog again checkbox; only submitting saves that preference. Delete and
Shift+Delete then remove directly with the saved check boxes, and a refused
operation opens the dialog with its error.
Settings can restore confirmation. The dialog's default,
focus and buttons follow the ruling in [Buttons](#buttons).
Routine stop, start, and applied settings need no confirmation.

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
[feedback placement policy](#feedback-placement). Pending operations
and failures remain visible without exposing internal protocol machinery.
Reconnection preserves presentation state according to the pipe API contract.
After reconnecting, show the confirmed list; do not leave a permanent busy state.
A missing row means the torrent was removed, not that its files were deleted; a
deletion failure arrives as a notification with Open folder.

### Feedback placement

Choose the surface by what the person needs to do, because a disappearing
message cannot be the only explanation of unfinished or failed work.

| Situation | Surface |
| --- | --- |
| Routine stop, start, addition in the open window or applied setting | The changed state and one accessible outcome announcement; no visible success toast. |
| Start or Force start while a global pause prevents transfers | The start takes effect, so the row changes from Stopped to All paused at once, and an informational notice says when the torrent starts: when all transfers resume, at the time the scheduled pause ends, or when the network adapter is available. It never says the start failed, because the person would press it again. Its action is Resume, which lifts the global pause; its tooltip says that a scheduled pause is lifted only until the next schedule change and stopped torrents stay stopped. An unavailable adapter offers Settings instead. Screen readers hear the notice instead of the routine announcement. The notice stays until the person closes it or the pause ends. |
| Resume all while the network adapter is absent | Resume all lifts the person's own pause, but the missing adapter still pauses every transfer, so the same notice says that transfers start when the adapter is available and offers Settings. |
| Refused setting or failed editor action | Persistent feedback beside the field or inside that editor, following [Committing edits](#committing-edits). |
| Failed command without an editor, such as Stop or Open folder | An error toast that stays until the person closes it, separate from the message bar. Retain the existing policy for clearing it after later command outcomes. |
| The engine did not start or stopped, or the download state could not be read or saved | The [message bar](#message-bar), with Restart for the engine and Open folder for the engine data folder. Recovery clears it. While the engine starts, the list shows its loading state and no message bar shows. |
| The running engine is another version | The message bar, titled Engine version mismatch, says where that engine runs and the way out: Exit in the tray for another folder's engine, reinstalling for a folder holding both versions. It has no action, because the fix is outside the window and Restart cannot replace a running engine. Recovery clears it. |
| Shared Library/subtitle source or database failure | The message bar, which names the cause from SQLite's error code. A read-only `library.db` offers Repair, which clears the Read-only attribute; an incompatible database offers Delete. Denied access, a disk error, a full drive or another program using the file offers Open folder beside Retry, because the person fixes it outside the app. Any other failure shows its own message with Retry, through the existing source and feature recovery paths. A table or supplier card does not repeat this shared condition. |
| A lasting subtitle supplier problem while Automatic subtitles is On or requested manual work waits, such as refused access | The message bar as a warning, titled Subtitles paused, with Settings, or with Retry when the download folder refused a file, because that is fixed outside the app. Subtitle Settings shows state only, never the problem again. |
| Fetch metadata left files unidentified because the search limit refused their search | One timed warning toast when the batch ends, titled Search limit reached, counting those files, with Settings, which opens the search limit. |
| A subtitle that could not be downloaded or saved | One error toast, titled Subtitle not saved, naming the latest subtitle file and counting the others, with Settings. It stays until the person closes it. |
| A language choice that could not be saved | The message bar as a warning, with Settings. |
| The selected torrent has an error | The details pane shows the error in its summary and progress bar; the window adds no message for it. |
| A finished download | A success notice for each download, titled Download finished, with the torrent name below it and Open folder, as the [notification policy](engine.md#notifications-and-sleep) defines a finished download. It closes by itself. |
| A torrent stopped by an error, or a deletion that failed after removal | The error notice of a failed command; a stopped torrent also stays in the Errors count. With the window closed, the engine's [notification policy](engine.md#notifications-and-sleep) shows its native popup instead. |
| A decision requiring consent | The existing dialog for that operation. |

**Owner ruling: a message bar or toast never repeats itself.** The person
reads the title, the torrent, the message and the action as one message from
top to bottom, so the same words never appear twice among them. As Fluent 2
asks, the title summarises for scanning and the message adds only the reason
or the next step, never a restatement of the title, such as The operation
couldn't be completed under Action failed.

#### Message bar

A lasting problem with the whole application shows in one
[Fluent 2 message bar](https://fluent2.microsoft.design/components/web/react/core/messagebar/usage),
the WinUI InfoBar.

**Owner ruling: the message bar sits at the bottom of every page, above the
status bar.** Fluent 2 places a page-level message bar below the page's
command bar, and TinyTorrent's toolbar is not that command bar, so the bar
does not go at the top of the page.

- It shows the most important problem: the engine, then the download state,
  then Library and subtitles, then the subtitle supplier, then the language.
- Its title names the problem without a final period, such as Downloads
  stopped, and its message gives the reason and the next step.
- An error or warning always has the action that resolves it, because Fluent 2
  requires one: Restart, Open folder, Retry, Repair or Settings. A problem the
  person fixes outside the app may add Open folder before Retry, because
  Fluent 2 allows a second action.
- Title, message and actions share one line while they fit. Otherwise each
  takes its own line, with the actions at the bottom right, as the WinUI
  InfoBar and the Fluent 2 message bar place them.
- The person may close it. It returns when the problem changes and at the
  next start while the problem lasts, as Fluent 2 asks, so a closed bar never
  hides an unresolved problem for good.
- It overlays the content instead of taking room, so showing or hiding it
  moves nothing under the person. The WinUI InfoBar takes room in the layout,
  but a message that appears must not move what the person is looking at.
  While it shows, it covers the bottom of the content; Close uncovers it. Its
  severity colour sits on a solid base so the page does not show through.

#### Toasts

Events show as toasts in one stack in the bottom-right corner of the window,
above the status bar and above the message bar while it shows, following the
[Fluent 2 toast](https://fluent2.microsoft.design/components/web/react/core/toast/usage):
a finished download, a failed command, a torrent the engine stopped, a
subtitle that could not be saved, and a start that waits for a pause to end. A toast shows a severity icon, a bold
title, the torrent it concerns, the reason or next step, at most one action at
the bottom right, and Close at the top right.

- The title says what happened, in one line without a final period: a noun
  and a past-tense verb, such as Download finished, Stop failed or Torrent
  stopped. A failed command's title names the command, so the error text
  below keeps only the reason and the next step.
- Every line keeps to one line, as work-surface text does. A line that does
  not fit ends with an ellipsis, and its full text shows in a tooltip only
  then, because a tooltip that repeats readable text adds nothing.
- The action is a specific verb, such as Open folder.
- A new toast arrives at the bottom and moves older ones up, 16 pixels apart.
- A finished download closes after seven seconds on screen. At most four such
  toasts show; a fifth removes the oldest, because the event stays visible
  where it happened, such as a finished row. Its action, Open folder, closes
  it, because the person has acted on it.
- An error toast stays until the person closes it, because a disappearing
  message cannot be the only explanation of failed work. The resume toast
  stays until the person closes it or the pause ends.
- The seven seconds pause while the pointer is over the stack, keyboard focus
  is in it, the window is inactive, or the stack is hidden, so a toast that
  arrives unseen waits for the person.
- The stack takes no room, so showing or hiding a toast moves no page
  content. It never takes focus, and it hides while an editor has focus,
  because it must not cover what the person is typing.
  Toasts already displayed are retired when the stack hides, so later notices
  do not replay them. New notices received while hidden wait to be shown.
- **Owner ruling, 2026-10-11:** toasts show above an open dialog, so a result
  reported while the dialog is open, such as the supplier dialog's Check, is
  seen at once. The supplier dialog's Check result is a toast: Access checked
  closes by itself like a finished download, and a failure stays. Field
  errors stay beside their field.

Native control state notifications can satisfy routine accessible feedback;
do not add a second announcement of the same result. Keep field and editor
errors at their point of correction. A toast never becomes the sole place to
recover from a failure. Announce a message once when it appears or changes;
its action must be keyboard accessible. Combine repeated events instead of
stacking an unbounded stream. Do not repeat an engine desktop notification as
an in-app toast for the same event.

Fluent 2 describes web components, not a native WinUI toast control, so the
window builds the stack from ordinary controls.

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
question. A source that fails is reported in the window's error notice and
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
The commit button is Add, and its tooltip says whether the torrent starts; when
several torrents, including typed magnet text, will be added, the tooltip gives
the count.

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

Use the [commit rules](#committing-edits): ordinary settings apply individually,
with no page-wide Save step or confirmation on close. Keep dependent fields
visible and disable them while inapplicable, so changing a switch does not move
later rows. One Advanced mode switch reveals advanced rows and cards
across categories. Search includes those settings and reveals the chosen row.
**Owner ruling: Settings uses the title-bar search and has no search box of its
own.** The title-bar search already finds settings, including advanced settings;
a second box duplicates the task and takes a row away from the settings. Keep
the title-bar search's existing scope of torrents, commands and settings.
Settings uses the approved Windows Settings composition in
`app/prototypes/settings-windows-prototype.html`, variant C, and its accompanying notes.
Its sidebar contains Home, General, Transfers, Speed limits, Schedule,
Appearance, Network, Plugins, and Advanced. Advanced appears while
Advanced mode is on. The sidebar's TinyTorrent logo and name form a
native button that returns directly to the workspace Settings was opened from.
The sidebar's footer items are Advanced mode and then the same return action,
last in the pane. Both are navigation items that act instead of opening a page,
so they render and respond like the categories. Advanced mode's icon is a
checked square for On and an empty square for Off, keeping the state visible
in the compact rail. The return label names Torrents or Library
from the navigation owner's workspace, and both return actions use that workspace's existing command and departure
guards, so browsing Settings does not trap the person in its history.
The sidebar uses a compact icon rail at smaller widths
without a hamburger button; it does not set the window minimum
from translated category labels. Content is bounded at 1200 DIPs.

<a id="settings-navigation"></a>

**Owner ruling: every Settings navigation icon is 24 DIPs, including Advanced
mode and the return action.** The 16-DIP label's visible letters are vertically
centred against the icon; centring the font's line box leaves the text visibly
off-centre. The shared navigation text style owns tight line bounds and vertical
alignment. Labels are text elements in the native content slot and icons use
the native icon slot, so styling labels cannot hide the icons. Keep this
appearance in expanded and compact navigation and after live language or theme
changes.

Settings category icons use Microsoft's Fluent color SVGs at that
[Surface](#icon-sizes) size, to make the categories easier to recognize. This is
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

**Owner ruling: Settings tab headers put the category title above a summary row
with artwork, identity and status, and two compact facts with Lucide icons at
the end.** One shared header keeps the tabs
consistent. Facts describe the selected tab using its existing owners, so they
remain current without another query or stored summary. Measured content widths
move facts below the title, then into separate rows when necessary. Captions and
values and the single-line page title keep their full text in tooltips. Facts
use the shared statistic, the same as the inspector's statistics strip. Home uses
one Home title beside its logo, an activity subtitle, and session and all-time
totals, with download and upload in their theme colours.

Home provides curated shortcuts and the same theme, Windows integration and
automatic subtitle controls as their category pages. It shows facts supplied
by the native owners; prototype sample activity and totals are never production
data. The existing start-page preference controls whether Settings opens at
Home or General.

Use 24-DIP gaps between major tasks, 8-DIP gaps between sibling cards in either
direction, 8 DIPs from an external section heading to its card, and 16-DIP
horizontal card insets. Related rows share themed separators, no exterior row
gaps, a compact 52-DIP minimum or 64 DIPs with a description. Trailing controls
align to the actual right inset. Reserve an action column only where a group
has related actions. Numeric inputs use native TextBox, including its clear
button; optional limits use the Unlimited placeholder.

Use native WinUI Expanders with the shared Settings drawer animation described
above. Their headers contain the subject and a configured
summary from the existing settings owner; collapse that summary when expanded
so it does not leave an empty line above the controls. Notifications retains its consequence
description under the heading. Static group headings stay outside their cards,
except Home's overview groups, whose stronger headings sit inside their cards.
Home link hover and focus cover their full row with the content inset inside the
button; the group heading is not part of the interactive row.
Settings labels, values and feedback remain single-line, with ellipsis and the
full text available through tooltips and accessibility.

General begins with Windows integration, then Startup and closing,
Notifications, Power, Dialogs and confirmations, and automatic updates.
Registration warnings use the full row width below the first row of actions.
The native proxy dialog keeps its existing transaction, validation, feedback
slot and button placement.

Transfers contains Download locations, Adding torrents, Queue, Seeding limits,
Watched folder, and Skip files. Skip files
hides its pattern editor while off, retaining the saved patterns. Network begins with adapter
and proxy cards; advanced groups contain encryption, peer connections,
discovery and incoming connections. Plugins places video
information, with the IMDb data and City data rows, before supplier access,
wanted languages, automatic subtitles and
additional matches, followed by Library search, local scan and the shared
provider search limit. Video information separates Provider from the Download
automatically switch; configuring a provider, its credentials or its data does
not change automatic download intent. Provider configuration supports manual
requests independently of automatic work, so its controls stay available in
either switch state. The switch's neutral description states its background work.
The header facts identify the video provider and subtitle
supplier; automatic download state stays at each setting's control, so neither
preference is given extra prominence. The two data rows are one control with one pair of
dialogs, worded from each dataset's text section, so a download, update or
deletion of either data reads and behaves the same
([peer location](peer-location.md)). Filtering languages never discards hidden selections.
Search and scan feedback occupy reserved single-line slots, so results do not
shift controls.

Speed limits puts Limits to use before equally sized Standard and Alternative
profiles, then Connection setup. Both profiles remain editable in every mode.
Speed limits and Schedule use one editor and the existing decimal MB/s
conversion; connection capacity stays in Mbps. Schedule wraps that shared
editor in an expander and retains an always accessible four-choice mode selector
beside the weekly map. Periods keep their existing save owner, with when and
behavior summaries, inline days, 24-hour Start/End controls and Remove beside
them when space permits.

Appearance uses one responsive three-column native radio group for System,
Light and Dark. All previews share a compact 21:9 crop with 8-DIP clipped
corners; System splits the same Light/Dark artwork. Display language remains
a separate row below. There is no redundant theme dropdown. Free space,
external IP, memory usage and title-bar speeds use four directly accessible
switch cards. The external-IP and memory-usage cards follow Advanced mode;
hiding one keeps its saved value and removes its whole card from the layout.

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
[temporary suspension](engine.md#connection-measurement). The full-width test
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
Files holds the default folder, the incomplete-filename suffix, and an optional
separate incomplete folder. Folder and suffix defaults apply to new torrents;
the help makes that scope explicit. Separate Show the Add dialog switches for
torrent files and magnet links let either kind add in the background, respecting pause and
queue choices. General holds Windows integration, Notifications, Dialogs and
confirmations, Startup and closing, Power, Updates, and Library. Windows
integration groups torrent and magnet associations with sign-in startup and its
Windows Settings action.
Common feedback and window settings precede infrequent power choices,
maintenance, and optional video information.
Dialogs and confirmations starts with the Settings start page preference,
then follows the workflow: adding torrents (with the related bring-to-front
option), deleting downloaded files, then exiting. Spacing separates the start
page, Add options and confirmations while keeping related rows together. These
controls restore suppressed dialogs without needing advanced options. Transfers
keeps the defaults applied by Add rather than a second control for showing its
dialog. The background-running notice is restored by its Notifications switch,
so all notification preferences stay together.
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

Include, grouped by task: the default download folder and the two Add dialog preferences;
standard and alternative speed limits and when they apply; queue limits for active downloads and
seeds; seeding ratio and time limits; connection limits; the network adapter,
port mapping, listen port, connection encryption and proxy server; the
[notification switches](engine.md#notifications-and-sleep); preventing idle sleep
while downloading on mains power, and also while seeding; Check for updates,
following the [update model](architecture.md#installation-and-updates); and
language. Each uses the existing settings path. Reaching a seeding limit stops
the torrent; nothing is removed without a request. Starting that torrent by
hand lifts the limit for it, so it seeds on as asked instead of stopping again.
The sleep switch names its mains-power condition, so a laptop that sleeps on
battery does not surprise its owner. The seeding switch only extends it, so it stays
visible and is disabled while the sleep switch is off; hiding it would move the
page and hide the choice.

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

General has a Notifications section with four switches in order of
importance: Notify about problems, Notify when a download finishes, Notify
when a torrent is added, and Notify when the window closes and transfers
continue. The order shows which notifications matter most. Turning all four off
stops every engine notification except failures of something the person just
asked for. Problems are one switch, because no one needs to silence one kind of
problem and keep another.

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

Appearance offers Show external IP in the status bar, off by default. The status
bar shows the IPv4 and IPv6 addresses libtorrent reports, or Not available until
one is known. A changed network clears the old addresses; no external lookup
service is needed. Appearance also offers the application language and Follow Windows, Light, and Dark
through the existing theme owner. General's Windows integration group holds
Start when I sign in. Startup and closing holds Start in the notification area,
Start with transfers paused, and the splash setting. Exit confirmation belongs
to Dialogs and confirmations. Start in the
notification area, off by default, makes
starting TinyTorrent start only the engine in the tray; opening TinyTorrent
while it runs still shows the window. The splash switch, on by default, serves
a person who opens TinyTorrent often and finds the splash in the way. All categories
share one viewport-constrained, centred content column, so a change of
category cannot move the page or push its actions outside the viewport.

Settings offers a Start when I sign in switch and an Open torrents with
TinyTorrent switch covering `.torrent` files and magnet links. Both call the
engine's [registration owner](engine.md#windows-registration) and show its
observed state. Each switch works at once and never opens Windows Settings
([owner ruling](engine.md#windows-registration)). The handler switch is on only
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

### Inspector and edits

Filters and Inspector use the same Pane control for their surface, header,
pane buttons and compact strip, so presentation and focus behavior have one
owner. Dock is None, Top, Bottom, Left or Right; None hides Pin and keeps the
expanded presentation. An edge orients the compact strip, with Filters docked
left and Inspector docked bottom. CanClose and CanMaximize show the respective
buttons. IsCompact and IsMaximized describe the presentation accepted by the
workspace; clicking a pane button only requests a change. The workspace retains
visibility, placement, saved dimensions and departure guards, so shared drawing
cannot bypass an unfinished edit or move another pane.

General, Files, Peers, Trackers, Speed, and Pieces answer different questions;
request data only for the visible view. Preserve useful data coverage and choose
each layout for its task. The Pieces view is the [Pieces map](#pieces-map).
Peers and Trackers use the existing TableView, sharing its header, selection,
column, keyboard, and scrolling behavior with the main torrent table. Files and
the Add dialog's file browser use TableView's
[hierarchical rows extension](../../lib/TableView/docs/hierarchy.md#files-browser-integration)
for wanted, size, progress, and priority content, because folders require
hierarchy. Torrent-specific file commands stay in the application. Tracker error
status text uses the critical brush; other tracker states remain neutral, so
failed trackers stand out without colouring every healthy row.

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
([peer location](peer-location.md)).

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
as a Speed statistic, the same shared statistic as Settings and the General
section, with no background so it blends into the map. These totals come from
torrent summaries, including transfers with no mapped location, and use the
existing rate formatting and download/upload theme brushes. The shared peer-view
menu offers Speed position: Top-left, Bottom-left or Bottom-right, saved with
the peer layout and starting at Bottom-right. Top-right stays reserved for the
map's close button. The zoom and Fit controls sit at the bottom centre, so
moving Speed never moves them.
[Peer location](peer-location.md#map) owns aggregation, precision and rendering.
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
One temporary entry in window navigation restores the caller's workspace,
filter pane and inspector presentation through Back, Alt+Left or Escape. Choosing
another map scope retains that original return point. An explicit layout change,
including filters, toolbar, inspector open/close, compact/restore, section or peer
view, discards the return point and continues with the chosen layout, because
Back must not undo the person's layout choices. Map pan, zoom and Fit keep it.

Tracker headers stay short: Downloads and Next. Header and value tooltips explain
completed downloads reported by the tracker and the next announce or retry;
an unreported count stays a dash, not zero. Status and Message tooltips retain
per-local-endpoint and info-hash-version status, consecutive failures and raw
diagnostics. The visible message belongs to the displayed aggregate status,
so a working route cannot inherit another route's error. Diagnostic text also
appears as automation help. These details add no columns or network requests.

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

General follows `app/prototypes/general-prototype.html`, variant H, in two titled groups.
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

The inspector is a layer above the workspace, because it shows one torrent's
properties rather than more of the table. It is a raised surface:
the shared `Pane` template supplies its fill, thin outline, corners and header divider.
Its right edge aligns with the table header; its bottom inset
matches Filters. It shares the Filters pane's material,
so these two supporting surfaces stay consistent. Its header sits on that layer with a
`DividerStrokeColorDefaultBrush` bottom edge. Tables inside it follow the
[TableView visual contract](../../lib/TableView/docs/tableview-contract.md#8-rendering-layout-and-visual-language),
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
always available, following [the deletion ruling](engine.md#removal-and-moves).
Its default follows [Buttons](#buttons).

Include shared torrents is an explicit choice; the engine rechecks the scope
when the command executes. The engine detects files already at the destination
and verifies them, following [engine moves](engine.md#removal-and-moves), so the
dialog needs no file-location checkbox or repeated resulting path. A refusal
keeps the destination and choices. Accepted
work appears as Moving files without a file-copy percentage; row errors direct
the person to reopen Move files and choose the folder holding the files.

The Speed view shows the [selected torrent's speed history](engine.md#state-and-work),
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
opacity so the grip stays visible.

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
methods under [testing](../agents/testing.md). Review native semantics, reachable actions,
focus return, automation, live text, and bounded hidden work. State the evidence
and gaps explicitly; a static screenshot cannot establish all of them.
