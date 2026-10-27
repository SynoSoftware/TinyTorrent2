# Workspace

The [shared UI policy](../ui.md) applies to this contract. This file owns the
window shell and workspace presentation; [torrent interaction](torrents.md)
owns the commands and Inspector content, and [status and feedback](feedback.md)
owns the footer and notifications.

## Native controls and visual authority

The visual and interaction reference is `app/prototypes/prototype.html`, variant
C, commit `8614126` on `main`. Preserve its compact table, collapsible status
drawer, caption search and Settings composition.
Implement them with native WinUI controls and the existing TableView and command
owners. The [Inspector ruling](torrents.md#inspector-and-edits) supersedes the
prototype's vertical inspector sections, and the MenuBar ruling below supersedes its shell
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
Choices retain their content icons beside native state indicators, including
in menus that also contain commands. Mutually exclusive choices use native
radio items in named groups; independent options use native toggle items, so
the control communicates whether several choices can apply. A mixed option
keeps the mixed-square indicator. The described state remains authoritative
when navigation is refused by an unfinished draft.

The custom title bar contains one row: workspace dropdown, File/Torrent/View/Help
MenuBar, bounded Search, the language switch, the light/dark switch, and native caption
buttons. Add, the selection commands and Settings are on the toolbar, not the title bar.
Fit columns and Fill width are only in the table's column-header menu: in the
title bar they read as window commands, and the column header holds no buttons
because every part of it belongs to a column. A 24-pixel
icon and 32-pixel Search sit within a 48-pixel row so the content has breathing room.

**Owner ruling: keep the existing custom title bar; do not replace it with the
WinUI TitleBar control.**

Title-bar actions use the existing custom caption-button appearance: full
caption height, square hover surface and caption-button width, so they feel part
of the window chrome. They are ordinary Buttons with
TinyTorrentCaptionButtonStyle, so they share its size, spacing and states with
each other and with the native caption buttons beside them; CommandBar's
separate button metrics would break that consistency. The language button sits
before the theme button, showing EN, ES, NL, 中文, PT or FR in the existing caption font. It opens the shared
context-menu renderer's language choices, with flags, native names and the current choice
checked. Its centred label uses the shared button dimensions and zero margin and
padding, so the language shortcut stays the same size as its neighbours. The theme button uses
Lucide Lightbulb for switching to light and LightbulbOff for switching to dark,
so it remains distinct from the Settings gear. Its tooltip names the destination theme. It sits
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

**Owner ruling: Torrents and [Library](../library/presentation.md#presentation-and-interaction)
are peer pages.** Torrents remains the main page.

The inset app-icon position contains a native DropDownButton showing the current
workspace's icon and localized name. Its menu opens below the button, with
Torrents and Library as native radio choices rendered by MenuItems, using
the same descriptions and commands as View. Moving through choices does not
navigate; activating one uses the same navigation owner and departure guards
as Ctrl+1 and Ctrl+2. The confirmed workspace remains selected when
departure is refused. Secondary pages retain their caller in the selector.
The logo represents Torrents, never an individual torrent or Show in
Torrents action. Global command search offers both pages. The second menu belongs to the
current page: Torrent on Torrents and Library on Library. View shows the current
page's presentation, with Filters first. Filters uses one command
and one shared pane visibility state: changing pages changes its content, not
whether it is open. The Inspector and the toolbar follow the same rule. Each
page keeps its own filter criteria. File and Help do not change.

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

## Saved workspace

WinUI owns its saved window placement. Restore against current monitor work
areas and DPI, recovering a reachable position and usable size. Preserve normal
Windows move/resize behavior, caption buttons, and title-bar accessibility.
On Close, WinUI saves normal bounds, their display scale, maximized state, the
requested inspector split and the three tables' public layout snapshots in
`window.json` beside the engine data. It also saves the view: the page and
Settings category, the filter and whether the filter pane is open, the selected
torrents and current torrent, whether the inspector is open and on which
section, the torrent table's scroll position, and which connections map is
open. Reopening the window shows it
as the person left it; a saved torrent that no longer exists is left out of the
selection. The view is restored once, from the first snapshot whose storage
loaded. A window closed before then saves nothing, so an Exit during startup
does not replace the saved view with defaults. The engine does not read or
write this file.
A missing or damaged layout uses the declared defaults under
[saved-value recovery](../architecture.md#saved-value-recovery); a layout write failure
does not keep the window or engine open. Layout recovery never changes downloads.

## Workspace motion

Torrents sits to the left of Library: the page's table slides left
when entering Library and right when returning to Torrents. The
filter and inspector content slide with it in the same direction, so the
whole workspace moves as one gesture. The title bar, the toolbar and the filter
and inspector frames stay in place.

## Main window

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
menus and their shortcuts. Ctrl+K, Ctrl+F and Ctrl+E
focus the current page's search. Scope labels in global search distinguish a
command for the selection from a command for all torrents.

## Panes and filters

The shared `Pane` control owns the Filters and Inspector surfaces: its template
uses the platform's `LayerOnMicaBaseAltFillColorDefaultBrush` background,
a one-pixel `SurfaceStrokeColorDefaultBrush` outline that stays visible in Light
and Dark, and `OverlayCornerRadius` corners. Both end four pixels
above the workspace's bottom edge. Filters
retains its four-pixel side insets; the Inspector has a four-pixel right inset
to match the table header's right edge. When Filters is compact or hidden,
the workspace supplies a four-pixel left inset for the Inspector; expanded
Filters already supplies that gap with its right inset. The shared Filters body owns a
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
starts again by itself. Queued also includes Waiting to check and Waiting to
move, because those torrents wait for their turn too. One shared owner keeps pane visibility and its expanded or compact
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
so the command never adds an introductory stop. The [Settings index card](settings.md#cards-and-categories) keeps its
approved introductory text and transitions.

## About and Credits

Keyboard and search paths invoke the same owners. About shows the product
identity and running version on the same acrylic surface. The current page
belongs to the main view model.
About uses the approved split identity composition: the logo and name share a
baseline, the product description and two-line promise follow, and Version is a
plain label and value beneath them. The telemetry promise carries an asterisk;
its localized tooltip and automation help explain the reporting protections and
the [Microsoft runtime limitation](../architecture.md#privacy), including usage
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

## Selection and secondary pages

Leaving Settings follows the two owner rulings in
[Committing edits](../ui.md#committing-edits). The inspector keeps its target and draft
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

## Toolbar

The toolbar, a row above the page content that View shows or hides, is a
CommandBar of AppBarButtons in groups split by AppBarSeparators. Torrents
and Library share it, and View shows or hides it on both at once.
Add and Magnet come first on every page, and the current page's commands
follow. Each page shows only commands that can act on it: a command that never
applies on a page is hidden there, and one that is only unavailable for the
moment is disabled, as Microsoft's command guidance says. So no button acts on
another page's hidden selection, and none stays disabled for good. On
Torrents, the groups follow a torrent's life, and the most used commands come
first: Start and Stop; Open folder and Properties; Verify;
Remove. Remove is a group of its own at the end of the selection commands, so
the destructive command sits apart from the frequent ones. Library shows Open,
Open folder and Properties. These use the same commands and Lucide icons as the
page's menu. At the row's right end, Selected
connections map and All connections map form one group, because they are two
scopes of one view. Library shows only All connections map, because Selected
needs a Torrents selection. Settings comes last, in a
group of its own, because it acts on the application and not on torrents; Edge and File Explorer also put their
settings at the end of the bar. **Owner ruling: the toolbar buttons are
icon-only,** because text labels make the interface look cheap. Each button
keeps its label as its accessible name and its text in the More menu, and its
tooltip names it with its shortcut. When
the window is too narrow, the CommandBar moves the commands that do not fit
into its More menu instead of clipping them. The menus keep every command. Copy magnet
link stays off it because its link icon is Add magnet link's. Add comes first and stays enabled
without a selection; with the toolbar hidden, the File menu, its
shortcuts, drag-and-drop and paste still add torrents. The buttons that act
on the selection are disabled without one, as their menu items are.
With the toolbar hidden, File > Settings, Ctrl+, and search open Settings.
The toolbar is one Tab stop, and the arrow keys move inside it. It starts visible,
and the window layout remembers it. A maximized inspector fills the workspace
below the toolbar, so the page's commands stay reachable.

Dropping torrent files or magnet text on the window, or pasting them with Ctrl+V
while the table has focus, follows the [Add policy](torrents.md#add): several sources are
added directly; a single source follows its torrent-file or magnet dialog preference.
An empty list says how to add a torrent.

## Keyboard commands

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
