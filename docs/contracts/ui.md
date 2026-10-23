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

## Read for the change

Read this shared policy for every product UI change. Then read only the linked
sections for the surfaces and interactions affected, so feature detail does not
obscure the rules shared by every screen.

| Change | Reference |
| --- | --- |
| Title bar, menus, search, navigation, filters, toolbar or saved workspace | [Workspace](ui/workspace.md) |
| Settings categories, reveal and focus, Connection setup, weekly schedule or Windows integration | [Settings](ui/settings.md) |
| Torrent commands, queue or per-torrent limits | [Torrent table and commands](ui/torrents.md#torrent-table-and-commands) |
| Add or shared file browser | [Add](ui/torrents.md#add) or [File browser](ui/torrents.md#file-browser) |
| Move or Remove | [File-operation dialogs](ui/torrents.md#move-and-remove-dialogs) and [removal policy](ui/torrents.md#remove-and-unfinished-tasks) |
| Inspector views, speed chart or Pieces map | [Inspector](ui/torrents.md#inspector-and-edits), [Speed](ui/torrents.md#speed) or [Pieces](ui/torrents.md#pieces-map) |
| Status bar, progress presentation, message bars, toasts or recovery feedback | [Status and feedback](ui/feedback.md) |
| Library-specific interaction | [Library](library/presentation.md#presentation-and-interaction) |
| Peer-map rendering, aggregation or precision | [Peer location](peer-location.md#map) |

[WinUI instructions](../../app/AGENTS.md) own design and review workflow.
[Localisation](localisation.md) owns text and formatting; [testing](../agents/testing.md)
owns the evidence needed to verify a change.

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
The [title-bar shortcuts](ui/workspace.md#native-controls-and-visual-authority)
are an exception to standard button visuals.
Use the native platform's expression of Fluent for control semantics.

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

Light dialogs use the native tertiary solid fill for the body, base fill for
sections, and secondary fill for the footer. This softens large white editors
and distinguishes the groups and final actions without stronger borders.
Dark retains the native dialog fills and secondary card fill; High Contrast
retains system colors. Theme aliases own these relationships without a palette.

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

<a id="icon-sizes"></a>Icon sizes come from role tokens in `App.xaml`, so one
role has one size. That dictionary owns their numeric values.

| Role | Use |
| --- | --- |
| Chevron | Application-drawn replacement for a platform chevron |
| Inline | Body-text lines, status, fields, cells, toasts, labelled and title-bar buttons |
| Button | Icon-only buttons |
| Header | Group, section or card identity |
| Statistic | A statistic's caption and value, including Settings header facts |
| Surface | Dialogs, Inspector heading, workspace switch and Settings navigation |
| Toolbar | Toolbar buttons |
| Large | Empty states |

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
Shared Body and Status styles keep the same type size; Status uses tight line
bounds for compact facts beside icons. Approved feature compositions, including
[Inspector General](ui/torrents.md#general), retain their distinct type roles.

Shared controls and application resources own repeated presentation, so a
feature composes them rather than copying templates. Equivalent groups share
heading treatment and content insets; native controls retain their internal
spacing without per-screen negative-margin compensation. Section owns Inspector
group headings, details, actions and content gaps in both Library and Torrents.
Its approved inline title/type composition
shares a text baseline, an exception to the vertical-centre rule above, with
alignment held in that shared template.

## Buttons

Light labelled buttons use the native accent-disabled fill for disabled neutral
states, so their silhouette remains visible against dialog and section chrome.
Enabled buttons retain native rest, hover, pressed and focus states; Dark and
High Contrast retain their native disabled fills.

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
way from its Footer and the space its buttons leave free. Dialog content and
the action row share the uniform `ContentDialogPadding` token, so the title,
footer content and actions have the same inset on every side. The footer is
one item: the Don't show this dialog again check box, one fact such as a count
and total size, or a check's result with the
button that runs it. Dialog's footer type has only these kinds and refuses to
replace the check box, because two stacked items crowd the row. A dialog with
the check box shows its other facts in the body. The buttons keep the Windows order: the action first, then Cancel. A
dialog opens with focus on its first text box that leaves Enter and Escape to
the dialog, or otherwise on its default button, so the person can type or press
Enter at once.

Natural button widths share `ContentDialogButtonMinWidth`. Add's Paste and
Preview actions reuse that width token and the same uniform padding token as
the footer, with the pair at the right. The supplier/proxy
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
  and dialogs. Menus use the [torrent command names](ui/torrents.md#commands),
  and a button uses its one word, with the full name in the tooltip. A
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

Standard controls retain their built-in animations.
[Settings](ui/settings.md#category-motion) owns its drawer and category motion;
[Workspace](ui/workspace.md#workspace-motion) owns page transitions.
Motion expresses relationships, so one gesture never produces competing directions.

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

**Owner ruling: use TextBox for all numeric input, never NumberBox.** Its
native clear button keeps editing consistent. Numeric InputScope is a keyboard
hint; the field owner retains parsing, ranges, errors and commit behavior.
The native template and this one validation owner keep numeric fields consistent.

Apply a switch when flipped and a selection when chosen. Validate text and
numbers before committing on Enter or an appropriate focus departure. Escape
cancels unfinished field input. Typing, IME composition and Cancel must not
submit partial text. Keep pending work and field errors at the affected control.

Use an explicit commit editor when values form one coherent change, such as
Add, Move, account details or proxy configuration. Keep its draft until submitted
or cancelled; the feature contract owns departure behavior. Ordinary navigation,
viewing details and already-applied changes create no dirty page. An accepted
command is engine work, not an unsaved draft requiring another confirmation.

**Owner ruling: a setting applies immediately and never asks.** Navigation and
close capture valid input and continue without waiting for saves. Failures use
notifications, including after leaving Settings, so ordinary preferences never
trap the person on a page. [Settings](ui/settings.md#committing-settings) owns
input recovery, unavailable-engine behavior and schedule exceptions.

**Owner ruling: leaving an editor applies its input, and only Add and Move
ask.** Their confirmation protects against starting a download or a file move
the person has not confirmed. [Torrent editors](ui/torrents.md#unfinished-input)
own departure, failure and close behavior, including the unfinished-work question.

Account dialogs for a supplier's or provider's key or sign-in require explicit
Save and discard the draft on window close, because those details have not been
confirmed. Supplier changes preserve the automatic subtitle switch.

Send only intended changes through the [engine edit path](engine.md#committed-edits).
Refresh confirmed facts without replacing current input. On refusal, preserve
that input and explain the actionable reason at its control, or through Settings'
notification policy. Reconnection preserves unfinished input without submitting
it automatically. A modal editor retains a reachable recovery command so
recovery cannot require discarding the draft. Language changes apply in place.
