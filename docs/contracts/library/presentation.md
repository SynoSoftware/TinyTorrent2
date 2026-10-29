# Presentation and interaction

Part of the [Library contract](../library.md). Read its shared ownership,
membership and SQLite rules before changing Library presentation.

The owner approved the Sections composition described below. The
[implementation review](#prototype-implementation-review) records the accumulated
decisions and outstanding work. Historical alternatives remain in Git history;
this contract owns the current presentation rules.

Every part of the page reuses what the product already has for the same job:
the same controls, the same implementation and the same labels. A second
search box, card, file renderer or name for an existing command is a second
implementation that drifts from the first. A label is new only where the noun
is new.

Help text follows the [UI contract](../ui.md#text-icons-and-typography).
Each provider's information flyout carries the disclosure required by
[privacy requirements](providers.md#privacy-and-provider-release-requirements).

## Prototype implementation review

This is the acceptance checklist for the current Library design work, recorded
on 2026-10-10. It owns the pending prototype-to-app review, not a second UI
contract. The owner approved C — Sections and authorized native implementation.
Validation follows the current [root instructions](../../../AGENTS.md#working-rules)
and [testing policy](../../agents/testing.md). Application inspection is
authorized when a concrete visual question remains under the
[verification gate](../../agents/testing.md#verification-must-answer-a-question).

An unchecked item means native implementation and its appropriate evidence
have not both been established. Source review completes a clear requirement;
runtime checks resolve only specific remaining visual or interaction doubts.
The checklist does not require a screenshot per item. Record the
changed owner and evidence against each item when implementing; carry unfinished
items forward rather than asking the owner to rediscover them.

### Shared ownership

- [ ] Reuse the existing Inspector shell, Section, Field, responsive layout,
  typography, spacing, brushes, icons and commands. Audit their owners before
  adding anything; Library must not introduce parallel layout authority.
- [ ] Where a heading composition is missing, polish the shared composition
  and move its applicable consumers to that owner. Do not copy a Library-only
  implementation or add tokens without a distinct, demonstrated need.
- [ ] Reuse canonical app assets rather than copying them into a second icon catalogue.
- [ ] Implement the approved Sections composition below. Reuse the mature app's
  labels, commands and surrounding shell.

### Inspector composition

- [ ] Use the approved C — Sections composition: Synopsis and Cast form
  the main content, with a separate right metadata column. At narrower widths,
  sections reflow without clipping, collisions or excessive empty gaps.
  Cast uses its own column only when the side-by-side content fits the inspector's
  available height. Otherwise it stays below Synopsis with names wrapping across
  rows, even in a wide window, so the column does not introduce avoidable scrolling.
- [ ] Use one full-width heading above content and metadata. **Refresh metadata**
  sits in the card's More menu beside Fetch and Erase metadata, and retains its
  established command and availability rules. A file without information gets
  a Fetch button at the heading's far right, not stranded at the edge of the
  left content column. It opens the same provider list as Fetch metadata, then
  Settings.
- [ ] Place the media type beside the title, using the latest shared text
  baseline alignment and clear separation. Movie, TV episode and Audio use the
  same composition; do not restore a whole subtitle row merely for type or Year.
- [ ] For episodes, show the episode title first and Series · Season · Episode
  context beneath it. Series and Season are filter links; episode numbers remain
  readable context. Multi-episode files must not repeat the same series/season
  information for every episode.
- [ ] Put video metadata in this order: Ratings, Box office, Age rating,
  Director, Year, Genres. Music uses Track, Duration, Year, Genres. Year appears only in this
  metadata group. This supersedes the earlier Genres-before-Year arrangement.
- [ ] Keep genres last and allow all their filter links to wrap into the space
  below. Keep each genre name together. No clipped links, hidden remainder or
  "+N more" menu; spacing separates links without leading or stranded dots.
  This explicit genre-link treatment is an exception to ordinary single-line
  metadata, not permission to wrap every Field.
- [ ] Keep series ratings identified as series ratings. Preserve missing-score
  behavior and the existing information/provider states rather than inventing
  scores or new provider requests to fill the layout.
- [ ] Match the app's action glyphs and button treatment: Play, Show in Torrents,
  More and Close. Fetch metadata belongs in More at every width. A vertical divider
  separates these item actions from the inspector's Pin, Maximize and Close controls. Show in
  Torrents uses LocateFixed, never the software logo or a download action glyph.
  Play keeps its playback glyph rather than an external-window glyph.
- [ ] Retain readable synopsis line spacing, heading-to-body spacing and the
  established File tab and Inspector shell behavior. Review the entire window,
  not just the changed text block.

### Filters and navigation

- [ ] Year and every genre link use the existing filter operation and reveal
  the filter pane, retaining unrelated filters.
- [ ] Music Artist and Album are clickable filters using complete values, not
  pieces split at punctuation or display separators.
- [ ] Episode Series selects that show and clears an earlier Season choice.
  Season selects both its series and season atomically, never the same season
  number across unrelated shows. Use canonical series identity, not its label.
- [ ] All these filters participate in the existing choices, counts, active
  filter display and Clear behavior, including Files when such a filter is
  active. Keep active zero-result choices visible and human-readable.
- [ ] View starts with Filters in both modes. Its open/closed state has one
  shared window owner and survives switching between Library and Torrents;
  the selected filter criteria still belong to their respective page.
- [ ] Resolve the reported filter-pane opening jump/flash. Inspect the initial
  frame, motion direction and simultaneous content/Inspector movement; a smooth
  tail after an initial layout jump is not a fix. Native runtime diagnosis is
  still outstanding; the earlier static trace did not establish a cause.
- [ ] Double-click and Enter invoke Properties and never launch an external
  player. Playback requires an explicit Play/Open action, using the existing
  command owner.
- [ ] Preserve the existing Show in Torrents command: select the owning torrent
  and reveal the exact file in its Inspector's Files section, clearing a blocking
  file search and expanding its ancestors. Back / Alt+Left returns to the same Library selection,
  query, filters, scroll and Inspector context. Reuse the command from every
  offered surface; account for files held by more than one torrent through the
  existing origin model.
- [ ] Offer Show in Torrents directly in the Inspector title row beside the
  existing actions, with a tooltip and accessible name. Retain its More route
  when the narrow layout hides header actions. The owner approved this placement.
- [ ] Share Inspector Pin, Close and View visibility behavior across both modes.
  Pin retains the current content in a ten-pixel bottom bar with a horizontal
  Ellipsis; showing after hiding restores the collapsed or expanded presentation.
  Properties and exact-file navigation expand it. Collapsing preserves drafts;
  hiding uses the existing draft guards.

### Approved title-bar navigation

The [title-bar contract](../ui/workspace.md#native-controls-and-visual-authority) owns
the approved navigation and caption behavior.

- [ ] Add no navigation row. Switch pages through the workspace picker at the
  app-icon position; keep the File/current-page/View/Help menus.
- [ ] Use the actual `resources/tinyTorrent.svg` for Torrents and the Library
  face for Library. Do not substitute a Lucide download glyph for the brand.
- [ ] Make the switch the workspace picker that the
  [title-bar contract](../ui/workspace.md#native-controls-and-visual-authority)
  describes: the current page's icon and name, with Torrents and Library as its
  choices.
- [ ] Keep Ctrl+1 / Ctrl+2, search, current-page menus, focus and Settings/About
  return behavior consistent with the existing navigation owner.

### Outstanding decisions

- Library status-bar emphasis is under discussion. Review file counts and
  active filters against transfer/environment readings, including whether to
  repeat the filtered count. Preserve one shared status-bar owner;
  no new arrangement has been approved.

### Native composition

Native Library composes C through `Details`, shared `Section`, `FieldColumns`
and `WrapPanel`; `Browser` and `Store` own the extended filters. `OriginActions`
shares the one-source/direct and multiple-source/choice presentation across the
Inspector and Library menus, and `Browser.ShowTorrent` owns the operation.
`MainWindow` keeps page navigation and owns the app-icon switch. Filters comes
first in both menu modes, and Show in Torrents uses the locate glyph.

`MainViewModel.FilterPane` is the one owner of filter pane visibility; Library
requests opening instead of storing a second value. The pane is hidden, compact
or open. Close hides it; Minimize keeps a ten-pixel bar with EllipsisVertical
and leaves View > Filters checked. Hiding keeps the compact or expanded
presentation, which View restores, and the state carries across Torrents and
Library. Runtime verification of the native composition and the filter-opening
jump is still open.

- [ ] Audit all unbuilt native changes against the approved composition and
  interactions; earlier partial edits do not establish parity.
- [ ] Compare native code with this checklist item
  by item. Resolve drift in the owning control, formatter or command, and remove
  implementations made obsolete by the change.
- [ ] Build and run only the checks justified by
  [testing](../../agents/testing.md). Use the app's existing off-screen capture layer for
  native visual evidence; capture before/after when useful, at least after.
- [ ] Visually inspect movie, episode, multi-episode, music and long-genre
  content, missing information, wide/narrow layouts, and the affected language
  and theme states. Inspect interactions as well as still frames where motion
  or navigation is involved.
- [ ] Judge regression by whether the whole app and the changed area look and
  work worse, not pixel equality. Fix related problems found during review;
  record unrelated or deferred findings explicitly with their remaining work.
- [ ] Report implementation, source review, build/test evidence and visual
  evidence separately. Close this checklist only when the relevant evidence
  exists, and update the existing Library and UI contracts to the approved
  result rather than retaining competing specifications.

## Page and menus

**Owner ruling: Library is a page, a peer of Torrents, not a View of Torrents.**
The [workspace contract](../ui/workspace.md) owns the title bar, which holds the page
switcher, and the rule that the second menu is the current page's menu. On
Library, the menus are:

| Menu | Items |
| --- | --- |
| File | Unchanged. |
| Library | Open, Open folder, Properties, Show in Torrents; Fetch metadata, Download Subtitle; Erase metadata. The row context menu has the same groups and a selection heading. Download Subtitle is a direct action; multiple selections use Download subtitles. Single and multiple selections use the same command owners and Lucide icons. |
| View | Filters, Inspector and Toolbar, as on every page; then Videos, Music, Files as choices. |
| Help | Unchanged. |

## Search

The title bar follows the product's [search behavior](../ui/workspace.md#main-window).
Its Library placeholder is "Search Library". Live filtering uses the stored
Library fields described in [local search](files.md#local-search); no suggestion list covers the filtered rows.
Title-bar suggestions on both pages use the same accent- and case-insensitive
normalization, so one query finds the same spelling everywhere. The subtitle
category's summary and search terms live in the existing Settings suggestion
builder, not beside its navigation command.

## Configurations

**Owner ruling: Videos, Music and Files are configurations of one table on one
page.** Each configuration is a row set and its own saved column layout, sort
and filters. Nothing else on the page changes. The person chooses one in View;
Videos is the default, and the window remembers the last choice.

| Configuration | Rows | Default columns and sort | Hidden columns, available from the column menu |
| --- | --- | --- | --- |
| Videos | Video files | Title, Type, Released, Duration, Subtitles, IMDb, TMDB, Rotten Tomatoes, Popcornmeter, Genres; sorted by Title | Resolution, Bitrate, Metacritic, Cast, Size, Name, Folder, Date modified |
| Music | Audio files | Title, Artist, Album, Track, Duration, Year; sorted by Artist, then Album, then Track | Bitrate, Genre, Name, Folder, Size, Date modified |
| Files | Every file | Name, Folder, Size, Date modified; sorted by Name | Kind, Title, Duration, Resolution, Bitrate |

**Owner ruling: the table leads with what the person remembers.** In Videos and
Music, that is the title, not the file name or location, so Name and Folder
are hidden by default and appear at the end when the person adds them.

- Title is never blank. A file without video information or tags shows its
  cleaned file name. An episode's title includes SxxEyy and the episode name.
- Type, in Videos, is Movie or TV episode, from the provider match or else
  the file name.
- Released is a movie's release year or an episode's air year, or else the
  year in the file name.
- **Owner ruling: each rating site has its own column in Videos, and every
  available score appears in the Info details.** The sites are IMDb, TMDB,
  Rotten Tomatoes, Popcornmeter and Metacritic; Metacritic's column starts
  hidden. Each column holds only the score on the site's own scale, as
  `6.7`, `59%` or `50`; the header identifies its source. Details list the
  scores in one Ratings field with their source names, so they read as
  related facts. A series rating
  is identified as such rather than presented as an episode rating. A missing
  score stays empty; it is not zero.
  The scores come from metadata responses Library already saved for the same
  IMDb ID; showing ratings does not start another provider lookup.
- Kind, in Files, is the file's kind from its extension, as Explorer names it:
  Video, Audio, Picture, Document, Archive or Other.
- Date modified is the file's modified date from its
  [file facts](files.md#file-facts-and-music-information).

The status footer names the published configuration and file count, for example
"Videos · 19 files", applied filtering and selection. Its composition and
background-work presentation follow the [status-bar contract](../ui/feedback.md#status-bar).

## Filters

Filters use the existing filter drawer, opened from View > Filters, never a
toolbar button. Each configuration has grouped single-choice sections with
counts. Sections combine: a row must match every chosen section. Each section
lists choices that have rows. Counts apply the query and other sections' choices;
they ignore their own section's choice, so a person can see the alternatives.
Keep a selected choice visible even when its count reaches zero, so it can be
cleared. Clear resets every section. Closing Filters returns keyboard focus to
the active table, and count refreshes update the facet controls in place, so
focus and open choices survive. The facet dimensions stay separate.
Each section uses one compact row: a neutral Inline Lucide icon, its label and
a native ComboBox. Library gives the shared drawer enough width for these
rows, so labels do not consume a second line above each selector. Full selected
values remain available through tooltips and the native selector's automation.

| Configuration | Sections |
| --- | --- |
| Videos | Content: Type, Series, Season when a series is chosen, Genre, Year newest first. Playback: Resolution, Subtitles. Organisation: Metadata identified or not identified. |
| Music | Content: Artist, Album, Genre, Year newest first, from the tags. |
| Files | File properties: Kind, Size in Explorer's ranges from Empty (0 KB) to Gigantic (> 4 GB), Date modified from Today to Older. Content criteria followed through metadata links remain available below them. |

Only populated choices appear, along with any selected zero-count choice.
Resolution uses measured dimensions and the longer edge, so cropped widescreen
and rotated videos retain their useful size class: below 720p, 720p, 1080p,
1440p, and 4K or higher. Unknown dimensions have their own choice. The Subtitles
filter describes associated subtitle files; its tooltip distinguishes these
from embedded tracks, which the database does not catalogue. Metadata means an
accepted identification, not a promise that every descriptive field is filled.
These criteria use the existing joined row projection and need no schema change.

The date choices use calendar boundaries and can overlap: This week includes
Today but can begin in the previous month or year. A file counts in every range
that contains its date, so a range is never empty while it holds files.

## Details card

**Owner ruling: the details card is the torrent Inspector card**, with the same
implementation and layout: the card under a splitter, its title row, the
SelectorBar in that title row, the Wide and Narrow states, the empty state and
the error InfoBar with Retry. Library adds no second card.

- The title row shows the file's type glyph, its name and its size. The
  SelectorBar offers "Info" and "File" for both video and audio. A file that is
  neither opens on File, with the first item disabled.
  Play, Show in Torrents and More follow, then Close. In the Narrow state, Play
  and Show in Torrents move into More. Fetch metadata stays in More at every
  width.
- The approved C — Sections Info composition is recorded in the
  [prototype implementation review](#prototype-implementation-review).
  The File page and shared Inspector shell retain their existing
  structure.
- Info's field columns and General's share one geometry, `FieldColumns`: two
  content slots, fixed 3:2 columns with a 48-pixel gap, stacked below 760
  pixels. Content, headings and actions stay with their owners.
- A transient fetch, association or torrent notification updates the card in
  place. Fetch feedback and command availability change without replacing the
  card's controls, and a result whose content has not changed announces nothing
  new, so the person keeps a text selection. Selection, changed metadata,
  provider configuration and language still refresh the card. The torrent
  bindings stop tracking while the person is not on Torrents.
- Year and individual genres in Info apply the corresponding table filter and
  open the filter pane, so the person can see and change the active filters.
  They retain other filters and use the same filtering operation as the pane.
  In Files, these additional filters stay visible in the pane while active.
- The File page describes this one file, not its torrent, because General
  describes the whole torrent. It uses the same `controls:Field` grid:

| Left column | Right column |
| --- | --- |
| Name | Size |
| Folder, with Open folder | Type of file: Windows' name and the extension, for example "MKV Video File (.mkv)" |
| Torrent, with Show in Torrents; one row for each torrent that holds the file | Opens with: the default app's icon and name |
| | Created |
| | Modified |

Windows supplies the type name, the default app and its icon, as it does for
Explorer. The Open button's tooltip names the same app.

**Show in Torrents** is one command, from the Inspector title, its narrow More
fallback, the File page, the Library menu and the row context menu. Its LocateFixed
glyph means reveal the source; the app logo is not a torrent glyph. A single
origin opens directly; multiple origins offer named choices. It switches to
Torrents, selects the torrent and reveals the exact file in its Inspector's Files
section. Reveal clears a blocking file search, expands the file's ancestors and
selects and scrolls to the file, so a large torrent does not leave the person
searching for it. Back then returns to Library with the
same row selected.

**Metadata.** In the Library menu, the row context menu and the card's More
menu, Fetch metadata is a submenu that lists providers declaring per-movie downloads, so the person
chooses the provider for this request. The card's Fetch button offers the same
providers, followed by Settings. Refresh metadata uses the file's saved online
provider, independently of the default provider and automatic download setting.
Fetch metadata opens the same dialog for one file with or without
an existing match, starting at the chosen provider. It is titled "Fetch metadata", with Save and Cancel.
The search box carries the native query icon, and the shared Dialog owner gives
Title the initial focus.
With multiple movies selected, the command offers the [three scopes](providers.md#identification-and-early-enrichment) and
uses the same classification for counts and accepted work.
While it searches or saves, an indeterminate progress bar under the search box
shows that the provider request is running.
Title, an optional editable Year and the neutral Fetch ActionButton share one
compact row. Year starts from the selected entry's existing year and remains
unchanged if a search broadens; it is search input, not another saved year.
Submitted searches, including Enter in Title, use exactly these criteria and
never broaden them. Automatic searches on opening or provider changes try title
and year first, then title alone only after a genuine empty answer. Errors,
refusals and cancellations never trigger that retry. Both paths use the same
lookup, cache and protection owners; year-specific answers have distinct keys.
Results are ordered by distance from the requested year, with unknown years
last, and each result keeps its year in a separate visible column beside its
ellipsised title. Fetch has the shared download icon and
a tooltip distinguishing finding matches from saving one; there is no second
search button inside the field. Enter in the query follows Fetch's enabled
state. Empty queries and an in-flight request disable Fetch. A permitted
override reads Force, with the protection reason in its tooltip. Save remains
the dialog's default commit and applies only the selected match.
Request status and the resulting match count appear above the results and are
announced accessibly. No matches and errors remain inside the dialog. An
automatic year search that broadens adds “Title only” and a neutral Lucide Search
icon to that existing, non-wrapping status line. Its tooltip and accessible help
explain the fallback; no warning colour or additional row is used. A failed
Save keeps the query and results available for correction. Successful Save closes
the dialog, refreshes Library and announces the outcome without a duplicate
success toast, following the interface's feedback-placement rule.
A Provider choice above the search box lists per-movie download providers and
starts at the chosen provider. IMDb dataset management appears only in Settings.
Choosing another checks protection before searching; the search and the
saved match use that provider for this file only, and Settings stays unchanged.
When a provider cannot use the saved network route, the error offers a Settings
link that closes the dialog and opens Network settings. Other provider errors
offer no link, because Settings cannot fix them.
More holds Erase metadata. The Library menu and row context menu use these
same commands and labels.

## States

The page has no loading state; see the owner ruling under
[performance](lifecycle.md#performance-and-implementation-constraints).

| State | Presentation |
| --- | --- |
| No files | "No files yet", with a button that opens Torrents. |
| No matches | "No videos match" and the query (or music, or files), with one button for each other configuration that has matches, and its count, plus Clear search. The action buttons share the widest button's natural width through the shared ActionButton alignment. |
| Engine disconnected | The window's [message bar](../ui/feedback.md#message-bar). Cached search, filters, saved Inspector details, Properties, Open and Open folder remain available. Metadata changes and engine commands are disabled; automatic file reads and subtitle work pause. A database failure still makes the last view non-actionable. |
| Database unavailable | The window's [message bar](../ui/feedback.md#message-bar) reports the failure and offers Retry. Any last view stays non-actionable; normal torrent work remains available. |
| Automatic downloads off | Saved video information and lookup outcomes stay visible. Fetch metadata works with an explicitly chosen provider; Refresh metadata works with the saved online provider. Neither enables automatic downloading. A file without information shows "No metadata". Its Fetch button lists every provider, then Settings. The automatic download choice stays in Settings · Plugins, without an inspector or status-bar notice, because it is a preference like automatic subtitles. |
| Provider failure | A file without metadata shows "Waiting for TMDB", naming the default provider, and the status bar shows the same wait. A manual refresh reports its own provider's failure in the existing inspector message, preserving saved information. |
| Failed Open | The title-cell and card glyph show the failure, and the card shows an error InfoBar with Missing, Unavailable or Couldn't open, the time it was checked, and Retry. |

The empty states match the torrent table's typography and spacing and keep
their recovery actions. Shared failures use the workspace feedback without
shrinking the rows.

TMDB's and IMDb's attributions are under Credits on the About page, not on the
Library page.

## Interaction

Enter and double-click show and expand the selected file's inspector through
Properties, including when it was hidden or compact, so browsing does not
launch an external application. Only an explicit Open action
opens the file, targeting stable entry identity. Sorting or a late reply must
not change which file the action opens.
Preserve query, configuration, filters, selection and scroll position across
page navigation. When an entry disappears, recover focus predictably and stop
presenting its old details as actionable.

Follow existing keyboard, screen-reader, localization and theme conventions,
including English and Spanish. Routine enrichment errors never open a modal
prompt. A disconnect does not prove that the data source has withdrawn all its
files; reconcile with current engine state on reconnect.
