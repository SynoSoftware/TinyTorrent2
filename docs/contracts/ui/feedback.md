# Status and feedback

The [shared UI policy](../ui.md) applies to this contract. Feature contracts
supply the facts and recoveries; this contract owns their presentation.
[Background work](../background-work.md) owns reporting and registration, and
the [engine notification policy](../engine/notifications.md#notifications-and-sleep) owns
which engine events are reported.

## Status bar

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

While torrents move, the move's copy rate follows the upload rate with the
rows' Moving folder icon, such as `1,233.4 MiB/s`. It belongs with the rates,
not with background work, because a move happens to torrents and its progress
stays in their rows. It reads in KiB/s, or MiB/s from 1 MiB/s, never GiB/s,
counts copied bytes only, because a rename moves none and a sparse file's holes
copy at once, and yields with the rates. While torrents are verified, the rate
libtorrent checks their files follows in the same way, with the rows'
Checking icon.

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
the existing Resume toast may explain a refused resume, and Settings > Network >
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

**Owner ruling:** Long-running background work appears on
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
contract and the list of registered work are in [Background work](../background-work.md).
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
and it never implies Library space or the selected file's disk. An unavailable
free-space measurement hides the whole item, including its icon, text and
tooltip; this optional fact needs no permanent unavailable message. Torrent IP reads
`Torrent IP: …` from reported torrent-network addresses. An unavailable IP
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

Colours follow the [shared transfer and severity rule](../ui.md#native-controls-and-visual-authority),
including system colours in High Contrast.

## Feedback placement

Choose the surface by what the person needs to do, because a disappearing
message cannot be the only explanation of unfinished or failed work.

| Situation | Surface |
| --- | --- |
| Routine stop, start, addition in the open window or applied setting | The changed state and one accessible outcome announcement; no visible success toast. |
| Start or Force start while a global pause prevents transfers | The start takes effect, so the row changes from Stopped to All paused at once, and an informational toast says when the torrent starts: when all transfers resume, at the time the scheduled pause ends, or when the network adapter is available. It never says the start failed, because the person would press it again. Its action is Resume, which lifts the global pause; its tooltip says that a scheduled pause is lifted only until the next schedule change and stopped torrents stay stopped. An unavailable adapter offers Settings instead. Screen readers hear the toast instead of the routine announcement. The toast stays until the person closes it or the pause ends. |
| Resume all while the network adapter is absent | Resume all lifts the person's own pause, but the missing adapter still pauses every transfer, so the same toast says that transfers start when the adapter is available and offers Settings. |
| Refused setting or failed editor action | Persistent feedback beside the field or inside that editor, following [Committing edits](../ui.md#committing-edits). |
| Failed command without an editor, such as Stop or Open folder | An error toast that stays until the person closes it or the engine accepts a later command the person caused, separate from the message bar. Reads, such as the files a Move dialog shows or an Add preview, and messages the window sends on its own, such as its start and close handshake, do not close it. |
| The engine did not start or stopped, or the download state could not be read or saved | The [message bar](#message-bar), with Restart for the engine and Open folder for the engine data folder. Recovery clears it. While the engine starts, the list shows its loading state and no message bar shows. |
| The running engine is another version | The message bar, titled Engine version mismatch, says where that engine runs and the way out: Exit in the tray for another folder's engine, reinstalling for a folder holding both versions. It has no action, because the fix is outside the window and Restart cannot replace a running engine. Recovery clears it. |
| Shared Library/subtitle source or database failure | The message bar, which names the cause from SQLite's error code. A read-only `library.db` offers Repair, which clears the Read-only attribute; an incompatible database offers Delete. Denied access, a disk error, a full drive or another program using the file offers Open folder beside Retry, because the person fixes it outside the app. Any other failure shows its own message with Retry, through the existing source and feature recovery paths. A table or supplier card does not repeat this shared condition. |
| A lasting subtitle supplier problem while Automatic subtitles is On or requested manual work waits, such as refused access | The message bar as a warning, titled Subtitles paused, with Settings, or with Retry when the download folder refused a file, because that is fixed outside the app. Subtitle Settings shows state only, never the problem again. |
| Fetch metadata left files unidentified because the search limit refused their search | One timed warning toast when the batch ends, titled Search limit reached, counting those files, with Settings, which opens the search limit. |
| A subtitle that could not be downloaded or saved | One error toast, titled Subtitle not saved, naming the latest subtitle file and counting the others, with Settings, which opens the Subtitles card. It stays until the person closes it. |
| A language choice that could not be saved | The message bar as a warning, with Settings. |
| The selected torrent has an error | The details pane shows the error in its summary and progress bar; the window adds no message for it. |
| A finished download | A success toast for each download, titled Download finished, with the torrent name below it and Open folder, as the [notification policy](../engine/notifications.md#notifications-and-sleep) defines a finished download. It closes by itself. |
| A torrent stopped by an error, a move that failed, or a deletion that failed after removal | A background error toast, titled Torrent stopped, Move failed or Files not deleted, separate from a failed user-command toast. Live storage problems update and resolve quietly under engine authority. A failed move's body gives the reason, then the file it concerns, because the engine's detail for it is only that file. A stopped torrent also stays in the Errors count. With the window closed, the engine's [notification policy](../engine/notifications.md#notifications-and-sleep) shows its native popup instead. |
| A decision requiring consent | The existing dialog for that operation. |

**Owner ruling: a message bar or toast never repeats itself.** The person
reads the title, the torrent, the message and the action as one message from
top to bottom, so the same words never appear twice among them. As Fluent 2
asks, the title summarises for scanning and the message adds only the reason
or the next step, never a restatement of the title, such as The operation
couldn't be completed under Action failed.

### Message bar

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

### Toasts

Events show as toasts in one stack in the bottom-right corner of the window,
above the status bar and above the message bar while it shows, following the
[Fluent 2 toast](https://fluent2.microsoft.design/components/web/react/core/toast/usage):
a finished download, a failed command, a torrent the engine stopped, a
subtitle that could not be saved, and a start that waits for a pause to end. A toast shows a severity icon, a bold
title, the torrent it concerns, the reason or next step, and actions at
the bottom right, and Close at the top right.

A missing, invalid, disconnected or full download location is reported once per
drive root or UNC share, naming the cause, root and number of affected torrents.
The engine owns its lifetime: updates are quiet and confirmed recovery removes
it, including before a delayed notice first appears. Dismissed notices do not
return merely because the count changes. Background problems have at most four
visible error toasts, separate from the persistent failed-user-command toast.
They list no torrent names and offer Open folder for the root. Other torrent problems show their download folder and offer Show torrent
and Open folder. Show torrent reveals that torrent through the existing navigation owner;
Open folder resolves its current folder when invoked. These actions leave the
problem available until dismissed, so inspecting the torrent does not remove
its explanation. A removed torrent disables its actions. The inspector shows
one error line combining outcome and cause, such as `Torrent stopped: The
download path is invalid.`, without a separate generic Error heading. Other
torrent problems combine their explanation and relevant detail on one line,
with the full text in the tooltip when clipped. The line uses the summary's
full width instead of competing with rate columns. Its wording describes the
torrent's current problem: move refusals and interrupted moves name the move,
and a failure to save state names what was not saved. A save failure does not
claim that transfers stopped; the actual transfer status remains above the bar.

A finished download and a problem the engine reports also show More beside
Close, because the engine sends them and a Settings switch turns them off.
As on the engine's popup, More shows only while the pointer is over that toast; it also shows while keyboard focus is inside the toast or its
menu is open. Hidden, it keeps its space, so the title does not reflow and Tab
still reaches it. Its menu offers what the engine's popup menu offers: turn off
this kind of toast, turn off all notifications, or open the notification
settings. The engine names each toast's switch and turns the switches off,
so both menus turn off the same toasts. Each choice closes the toast. A
failure of the person's own command has no More, because no switch turns it
off.

- The title says what happened, in one line without a final period: a noun
  and a past-tense verb, such as Download finished, Stop failed or Torrent
  stopped. A failed command's title names the command, so the error text
  below keeps only the reason and the next step.
- Every line keeps to one line, as work-surface text does. A line that does
  not fit ends with an ellipsis, and its full text shows in a tooltip only
  then, because a tooltip that repeats readable text adds nothing.
- The action is a specific verb, such as Open folder.
- A new toast arrives at the bottom and moves older ones up, 16 pixels apart.
- A finished download closes after seven seconds on screen. At most four
  toasts that close by themselves show; a fifth removes the oldest, because
  the event stays visible where it happened, such as a finished row. Its
  action, Open folder, closes
  it, because the person has acted on it.
- An error toast stays until the person closes it, because a disappearing
  message cannot be the only explanation of failed work. This includes the
  result of a background task that failed; the same task failing again
  replaces its earlier toast, so a task that restarts by itself, such as
  automatic metadata, never stacks failures. The resume toast
  stays until the person closes it or the pause ends.
- The seven seconds pause while the pointer is over the stack, keyboard focus
  is in it, a toast's More menu is open, the window is inactive, or the stack
  is hidden, so a toast that
  arrives unseen waits for the person.
- The stack takes no room, so showing or hiding a toast moves no page
  content. It never takes focus, and it hides while an editor has focus,
  because it must not cover what the person is typing.
  Timed toasts already displayed are retired when the stack hides, so later
  toasts do not replay them. Error and resume toasts survive the hide and
  show again. New toasts received while hidden wait to be shown.
- **Owner ruling:** toasts show above an open dialog, so a result
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
