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

## Windows, layout, and themes

Design against available window space and content pressure, using effective
pixels. Support monitor/DPI changes, long translations, text scaling, and the
minimum usable window size. Essential actions remain reachable when content
overflows. Do not introduce a second layout framework or a phone breakpoint into
a desktop task.

WinUI owns its saved window placement. Restore against current monitor work
areas and DPI, recovering a reachable position and usable size. Preserve normal
Windows move/resize behavior, caption buttons, and title-bar accessibility.
Use documented title-bar and backdrop APIs. The product window uses Mica as its
base material, with the platform's fallback where Mica is unavailable; menus,
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

The torrent table is the primary workspace, with an optional inspector and
focused Add and Preferences tasks. Filtering belongs to the page; TableView owns
its generic interaction. Domain actions from toolbar, menu, and keyboard use the
same command owner. Remove keeps data by default; delete-data is an explicit,
distinct decision.

Before deleting files, confirm once with the affected torrent names or count,
the file scope, a specific action such as Delete files, and a safe Cancel action.
Use standard [ContentDialog buttons](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/dialogs-and-flyouts/dialogs).
Routine pause, resume, and applied settings need no confirmation.

Retain recognizable identity and last-known read-only values on disconnect, mark
them stale, and disable writes until the engine is available. Pending operations
and failures remain visible without exposing internal protocol machinery.
Reconnection preserves presentation state according to the protocol contract.
When an unresolved operation's outcome is no longer available, show what is
known and what the user can do next; do not leave a permanent busy state or infer
success from a missing torrent row. Unfinished file recovery remains visible
even after the corresponding outcome record expires. Normal expiry of an already
reported outcome record requires no prompt or warning.

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
never an invented file list or silently started payload transfer.

Search within files changes visibility, not wanted choices. Bulk selection has
an explicit scope. Keep file identity and selected bytes clear; when a known list
has no wanted files, explain why Add is unavailable. The destination starts from
the engine default and remains changeable through a native picker. A failed
free-space check must not be presented as proof of an invalid folder.

Keep one form with a reachable native footer. A long body scrolls; the virtualized
file list has a finite viewport and owns its collection scrolling. Do not create
another page or state model solely to accommodate a short window.

Commit captures source and choices once, indicates pending work, and does not
promise that Cancel can undo an accepted command. A confirmed addition reveals
the torrent. A duplicate preserves the existing torrent's saved choices and data
and offers a path to it. A rejected choice returns to the relevant field; an
uncertain outcome follows the protocol's reconciliation rules.

If payload paths are already used by another torrent, identify that torrent and
offer a distinct destination or a way to open the existing torrent. If the
overlap is discovered after accepting a magnet, show the new addition as unable
to start, with Change destination and Remove actions; removal here keeps files.
Do not describe it as a duplicate unless its content identity is a duplicate.

If the user wants to replace an older torrent, the existing torrent's Remove
action keeps its files for a subsequent addition to verify and reuse. Explain
that path without silently removing the old torrent. The initial release has no
automatic Replace action; closing Add or refusing an overlap preserves the
existing torrent.

### Preferences

Group settings by user task and the actual libtorrent product, not the old
daemon's fields or fixed categories. Background choices have one engine owner;
UI-only preferences have one WinUI owner. There are no remote profiles or
connected-server scopes in this local product.

Use the [commit rules](#committing-edits): ordinary settings apply individually,
with no page-wide Save step or confirmation on close. Reveal dependent fields
when relevant; keep an explicit editor's actions reachable. Native navigation
and scrolling handle smaller windows. Do not show an engine field dump. Disk
caching remains [automatic engine policy](engine.md#disk-write-caching), not a
Preferences choice.

Include port mapping and listen port, completion notifications, and preventing
idle sleep while downloading on mains power. Each uses the existing settings
path. Downloads and updates opens the release page; no background-check setting
is needed for the selected [update model](architecture.md#installation-and-updates).

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
each layout for its task.
The Pieces map's required behavior is described below.

Apply individual choices and explicit file commands through the same commit
rules. When a coherent edit needs a draft, keep one active editor bound to the
torrent identity; protect only its unfinished input when changing context or
closing. An untouched inspector and an already-applied change never trigger a
save prompt. A removed target cannot receive a write.

Move makes its scope clear: moving a dedicated torrent folder includes companion
files such as subtitles. A shared download directory is never silently moved as
one torrent's folder. Show source and destination, preserve choices on failure,
and give an actionable explanation for a collision or an unsafe scope, following
[engine relocation](engine.md#removal-and-relocation).

Speed history is bounded and collected only while observed; unknown gaps are
not interpolated into invented history. Provide current/peak text alongside a
chart. Pieces uses one lean raster path and one keyboard focus group, with block
navigation and accessible range/composition facts, rather than a visual element
per piece. Distinguish local possession, verification, connected-peer availability,
and unknown state according to actual engine facts.

One page owner allocates table and inspector space. Remember an explicit split
adjustment within current usable bounds; the splitter is keyboard-adjustable and
reports its range. If both panes cannot show their essential content, use the
same inspector in a single-pane presentation with Back. Preserve selection and
the requested split; do not hide an inspector while continuing its detail work.

## Implementation review

Verify the affected journey at relevant sizes, themes, text scales, and input
methods under [testing](testing.md). Review native semantics, reachable actions,
focus return, automation, live text, and bounded hidden work. State the evidence
and gaps explicitly; a static screenshot cannot establish all of them.

[Historical reviews](archive/interface-review.md) preserve earlier
experiments. Their approvals, old shortcuts, Transmission limits, and unsupported
features do not expand this contract or replace a design for the actual product.
