# Implementation decisions and evidence

## Approved application navigation

Four perspectives considered the remaining prototype navigation in sequence:

- Everyday user: keep Torrents, Settings and About together, and make returning
  to downloads preserve the selected torrent and inspector.
- Heavy seeder: keep session pause and limits reachable through their existing
  tray, shortcuts and search; a navigation menu need not duplicate them.
- WinUI designer: use a native MenuFlyout and a simple scrollable About surface,
  with the canonical vector icon and normal type roles on the shared acrylic.
- Product owner: follow the approved four-command menu, show the running version
  rather than prototype sample text, and add no help or promotional destinations.

The selected implementation keeps one `WindowPage` in the main view model.
The view owns native focus and the discard dialog. The same navigation guard
protects unfinished Preferences input when returning to Torrents or opening
About. Global search calls those same commands. This is a UI checkpoint, not
completion evidence for the pending engine refactor or milestone 4.

The update-check decision was considered in sequence by four roles. An everyday
user wanted a quiet, useful link rather than a failed-network dialog. A Windows
engineer wanted cancellation with the window lifetime and no resident worker.
A release maintainer wanted the public latest stable release, no credentials
and a bounded request. The product owner wanted the already-planned preference
to work while leaving installation and signing outside these milestones.
The selected implementation caches each attempt for 24 hours, compares the
published tag with the running assembly version, and opens the fixed project
release page only on request. GitHub's [release API documentation](https://docs.github.com/en/rest/releases/releases#get-the-latest-release)
defines the source. Missing release/network failures are silent; the cache is
optional UI state and cannot block Close.

The About/navigation Release build passed. The post-build Everything query
reported only the concurrent dependency checkout's existing `boostlook/doc/bin`
and `tools/perl/bin` input folders. Native capture found the isolated window but
required interactive app approval and timed out. The window-only copy was then
closed. No engine was launched, and painted navigation/acrylic acceptance is
still pending; build success is not visual evidence.

The fresh Astra shell review found four concrete defects. Add dialog cleanup
now cancels only the Add draft; confirmed addition reveal waits for that dialog
to finish. A filter refusal synchronizes the visible table and command selection
while the inspector owner retains unfinished or pending edits. Saved peer and
tracker snapshots are independently optional, so an incomplete inspector layout
recovers to the respective table defaults instead of throwing on first open.
The optional update request observes the preference owner's changes, including
an immediate off choice, rather than waiting for the next transfer snapshot.
These are source corrections; their native scenarios still need runtime proof.
The second Astra pass found no remaining findings in this shell scope. The
corrected managed Release build passed. The final Everything query reported
only the same two concurrent dependency-input folders. No new test was added:
the presentation and modal journeys require review in the product, and this
checkpoint changes no TableView library implementation. All owned UI and engine
processes are closed. Milestones 3–5 remain incomplete.

## First usable download

The first window extends acrylic content into its LabForms-style title bar, with icon-only
commands (Add, Pause, Resume, Exit) beside the native caption buttons, a
full-height TableView and a status/error region. Add opens the native file
picker immediately, followed by destination, Start paused and Add/Cancel.
Native controls retain their keyboard and automation semantics.
The table's identity is the durable torrent identity; telemetry updates stable
row instances. Disconnection retains rows and disables writes.

Decision review, in series:

- Everyday user: show the download immediately and make Pause and Resume visible.
- Product designer: let the table fill the window; put errors beside the task.
- Fluent designer: use desktop acrylic and native buttons and dialogs in the
  compact window.
- Keyboard user: expose Ctrl+O, Ctrl+P, Ctrl+S, Ctrl+W and Ctrl+Q; retain editor keys.
- libtorrent engineer: start additions under a zero-priority payload guard and
  release it only after membership and initial choices are committed.
- Windows engineer: use a message-only owner window, a logon ACL on the pipe,
  and exclusive ownership of the data directory.
- Heavy seeder: window closure must leave transfers and checkpoints running.
- Product owner: keep one engine command owner and one ordered storage writer.

The chosen design follows these recommendations because they make the everyday
download visible without introducing another state authority.

The first adversarial source review found and prompted these corrections:

- A transient checkpoint failure could permanently disable commands: the
  startup-blocking flag now represents only startup failure, and failed live
  checkpoints retry after 30 seconds.
- Failed Add retained a consumed preview: an explicit retry reacquires it with
  the same input and choices.
- Refusals and stopped transfers hid their reasons: the product retains stable
  error codes and supplementary diagnostics at the affected surface.
- Live sorting did not refresh: the host refreshes the projection once per batch.
- Dropped alerts discarded pending addition ownership: reconciliation retains
  guarded handles and queued storage work; late alerts never dereference a
  discarded addition pointer.
- An older checkpoint could count as the final checkpoint: shutdown settles
  older requests and storage writes before requesting the final set.
- Diagnostic failures and shutdown were unlogged: the existing writer handles
  a bounded/coalesced diagnostic queue, including startup failure. Rotation
  failure discards a batch instead of growing the current log past its cap.
- Serialization ran on the state owner: storage encodes on its writer, and the
  pipe encodes on its connection writer.
- The main table lacked its accessible name: the product now supplies Torrents.
- Future preference fields had no current behavior: milestone 1 retains only
  settings its current owners use; later milestones add their own choices.

Pinned-source inspection found that a torrent's paused flag represents its own
intent even while the session is paused. Shutdown therefore awaits pause alerts
only for torrents that were running before the session pause; already-paused
torrents need no new alert. Lost or timed-out completion is reported as recovery
required instead of waiting forever or treating it as success.

The reviewer withheld completion because runtime evidence was still pending.

The owner's first hands-on feedback replaced the separate caption and labelled
toolbar with one icon command row and made the native picker the first Add step.
The LabForms caption layout, dimensions and button template are the owner's
explicit chrome requirement. Its focus suppression is not copied: keyboard
location remains visible. Earlier TinyTorrent sketches provide visual references;
their state models do not define this product. Native title-bar insets and one
drag-region owner preserve Windows caption behavior.

Decision review, in series: the everyday user wants the file picker immediately;
the keyboard user requires focus, names and shortcuts despite hidden labels; the
Fluent designer keeps native caption buttons and platform button semantics; the
Windows engineer preserves move, resize and snap behavior; the libtorrent
engineer keeps preview and commit guards; the heavy seeder keeps window closure
independent from transfer; the product owner removes the redundant chrome row.
The selected design meets these requirements with existing platform controls.

The owner explicitly selected the LabForms caption template and its chrome
tint. Those caption resources are the exception to the usual platform brush
rule; the rest of the window uses the native material and control resources.
The caption logo uses the canonical SVG because the ICO looked pixelated at
32 effective pixels. Windows executable, taskbar, splash and tray icons keep
the canonical ICO, the format those Windows APIs accept.

Hands-on review found that a ContentDialog did not inherit a theme selected on
the content root. One dialog refresh owner now applies the root's theme and
direction before opening and while it changes, preserving the existing input.

The owner brought live theme and language controls forward into the first
window. Both use the ordinary settings commit owner; English and Spanish are
the shipped choices, matched against Windows UI language on first use. Theme
starts from the system and offers immediate Light/Dark switching. Language is
shown as EN/ES with accessible names, never flags. The remaining inspector and
Preferences journeys still belong to their later milestone.

The first pipe check exposed that .NET WindowsIdentity.Groups omits the logon
SID. The managed process now reads TokenLogonSid directly; its pipe and mutex
share that one identity owner. Frames, FailedCommit and Restart production
checks pass, with isolated evidence under artifacts/evidence. Real keyboard
TableView tests skipped because their host could not acquire foreground focus;
that run is not keyboard evidence. The Computer Use transport closed during
launch; the installed WinApp UI Automation harness continues the journey review.

The real notification icon exposed an Invoke action but opening required a
double-click, so accessibility activation did nothing. Decision review, in
series: the everyday user expects one click to open; the keyboard user needs
Enter and Space; the Fluent designer keeps the Windows notification icon; the
Windows engineer chooses version 4 notification semantics and the icon's anchor
for its menu; the libtorrent engineer keeps Open outside transfer state; the
heavy seeder preserves background transfer; the product owner sends every
activation to the existing Open operation. The implementation follows
[Microsoft's notification-area guidance](https://learn.microsoft.com/en-us/windows/win32/shell/notification-area).

### First journey evidence

The Release product was exercised through WinApp UI Automation, with actual
Windows pickers, buttons, row selection, keyboard input and notification-area
controls. Screenshots and isolated state are under `artifacts/evidence/chrome-review`.

- The native picker precedes the populated Add form. Adding the local 64 MiB
  fixture with a destination and Start paused produces one paused row, selects
  and reveals it, and exposes `transfer.bin` as its accessible name.
- External SelectionItem activation works. Real Ctrl+S and Ctrl+P change
  confirmed running/paused intent. Keyboard focus remains on the row. Hovering
  the table does not expose the former root-wide Ctrl+P tooltip.
- A real incoming loopback peer completes the download. Its SHA-256 equals the
  seed's `62B89C1E1DF10F82EB427DA59876867D19EDD50507DDB64B9953A46A381C9E9B`.
- Closing releases the UI process while downloaded bytes increase from
  38,264,995 to 42,238,112. Tray activation reopens the same identity; the actual
  tray Exit menu terminates the engine. Ctrl+W also exits the UI.
- Two simultaneous engine Opens and a second direct UI launch leave one engine
  and one UI. Automation reports one TinyTorrent taskbar window and no visible
  engine window.
- Live EN/ES and Light/Dark controls apply saved choices. An existing Add draft
  keeps source, destination and paused choice while labels and theme change.
  Keep editing restores it after native Close. The draft used during a controlled
  rebuild was restored through the picker before continuing; exact choices
  remain in `V:/temp/tinytorrent-open-draft.json`.
- Production `Frames`, `FailedCommit` and `Restart` checks pass. The expanded
  Restart also proves saved language/theme changes preserve torrent membership.
- The milestone's one full non-interactive TableView run passes 207 tests:
  `artifacts/TestResults/milestone1.trx`. Four earlier interactive keyboard tests
  skipped; actual product input supplies the narrow first-screen evidence.
- Release app, engine and fixture builds pass. Every completed build/test run
  was followed by the prescribed Everything check, which printed nothing.
  Building the x64 test target needs explicit `RuntimeIdentifier=win-x64` because
  Visual Studio's MSBuild process is x86; the corrected invocation passes.

The latest source review prompted selection/reveal after Add, clearing a
recovered Add error, the row's accessible name and a valid SelectionContainer
provider. These pass the first-screen checks. Narrator, high contrast, monitor
DPI changes, temporary long/RTL catalogues and unrealized-row automation still
need their relevant broader journey checks.

### First resource measurements

Working set and private committed memory are separate process counters. Shared
runtime pages and OS file cache are not added to these process totals. The
fixture caps its seed at 1 MiB/s for a comparable local workload.

| State | Engine working set / private commit | UI working set / private commit |
| --- | --- | --- |
| Empty idle engine | 19,636,224 / 4,562,944 bytes | Closed |
| First usable empty window | 20,021,248 / 4,804,608 bytes | 149,823,488 / 109,449,216 bytes |
| Active download, UI open | 26,566,656 / 6,148,096 bytes | 183,472,128 / 129,708,032 bytes |
| Active download, UI closed | 27,336,704 / 7,204,864 bytes | Process exited |
| Active seeding, UI closed | 21,692,416 / 6,152,192 bytes | Closed |
| Restored completed seed | 20,836,352 / 4,943,872 bytes | 161,492,992 / 119,840,768 bytes |
| Six language switches then Close | 20,721,664 / 4,849,664 bytes | Process exited |

Observed download rates are 918,508–1,074,815 bytes/s. The short closed-UI sample
uses 0.171875 CPU seconds in 2.566 seconds; this ramping sample is not a
steady-state CPU promise. Observed engine/UI working-set peaks are
28,958,720 / 183,902,208 bytes. Across six language switches, engine private
commit stays at 4,882,432 bytes. UI private commit is near 125.3 MB for five
switches, reaches 128.7 MB on the sixth, then is fully released on Close. This
short check shows no retained UI process or growing engine language state; it
does not prove a long-running UI heap bound.

The closed-UI seeding check uploads from 1,114,112 to 5,064,617 bytes in
4.062 seconds, reports 749,770 bytes/s and uses 0.265625 CPU seconds. Its local
leecher caps downloads at 1 MiB/s. Active seeding needs neither WinUI nor a
separate resident helper in the product; the leecher is only the test peer.
The leecher receives all 67,108,864 bytes and its SHA-256 matches the original.
Both resource fixtures and all review engine/UI processes are closed afterward.

Open to an observed usable window has upper bounds of 2,908 and 3,009 ms through
the engine launcher and 3,399 ms through actual tray activation. These include
command startup and UI Automation observation overhead. Failed tray attempts
and an early stale Process-object memory sample are excluded.

Current Release files, excluding PDB/XML development files, total 41,756,793
bytes for 56 app files and 9,791,488 bytes for the engine. Loaded modules identify
the installed shared .NET 10.0.12 runtime (80,006,873 bytes) and Windows App
Runtime 2.5.1.0 x64 (121,408,317 bytes). These installed runtime sizes are
reported separately; no installer or distribution work was performed.

Decision review, in series: the everyday user finds a roughly three-second cold
Open noticeable and needs the splash; the Fluent designer keeps input immediate
once visible; the keyboard user requires the first usable window to accept
commands; the Windows engineer separates cold loading from observation overhead;
the libtorrent engineer protects uninterrupted transfer; the heavy seeder
prefers the small closed-window engine; the memory engineer avoids counting
shared pages twice; the product owner retains two processes because Close
releases over 100 MB of UI private commit. Retain the split and native splash;
revisit cold Open in the whole-application check if later UI makes it slower.

## Language save review

Decision review, in series: the everyday user expects the switch immediately;
the keyboard user expects the current draft and focus to stay put; the Fluent
designer publishes the whole prepared surface together; the Windows engineer
keeps disk work asynchronous; the libtorrent engineer keeps storage failure
independent of transfers; the heavy seeder needs the tray to share the selection;
the localisation engineer separates the selected tag from the saved tag; the
product owner keeps these two facts at the existing engine owner. Select live
text immediately and acknowledge persistence separately, because a failed write
must neither undo the visible choice nor claim that it survives restart.

The focused FailedCommit run passed in
`artifacts/evidence/FailedCommit-6bc4a421-e946-498a-8553-de04ecb24481`.
The expanded FailedCommit check watches a live language reverting on failed
storage or being falsely treated as saved after restart. The prior membership
case cannot catch either failure; it tests an uncommitted torrent addition.
The shared TableView lookup now follows the same missing-key fallback contract
as the product and tray, without adding a second loader or a wording test.

## Caption minimum and live language

The final review found that an ordinary successful command could dismiss a
language save failure. The product now reads `language_saved` from each engine
snapshot and retains a localised unsaved-language warning until the engine
reports that choice saved, including after reopening the window. Pause and
Resume cannot erase that engine fact.

The normal window's minimum is 720 by 560 effective pixels. The native presenter
receives monitor-scaled pixels and updates with DPI, so the title-bar actions
remain beside the Windows caption buttons. The application name trims inside
the space left by the logo, actions and native insets rather than pushing them
outside the window.

Language publication follows catalogue preparation immediately; persistence
runs afterward. A pending choice rejects older snapshot language values, so a
slow save cannot delay or undo the visible switch. The engine owns live language
and whether that choice is saved; a failed save remains a visible error.

Decision review, in series: the everyday user needs every caption action at the
smallest window; the product designer trims the application name before a
command; the Fluent designer uses the native presenter's minimum size; the
keyboard user keeps caption commands and dialog buttons reachable; the Windows
engineer updates pixel bounds with monitor DPI; the heavy seeder keeps transfers
independent of window sizing; the localisation owner publishes prepared text
before storage; the persistence owner acknowledges only a completed save; the
product owner retains one live preference and an honest save outcome.

## Advisory issues and the current visual reference

The owner's GitHub issues are advisory evidence, not instructions. Variant C
at `http://127.0.0.1:8765/prototype.html?variant=C` is the current workspace and
inspector reference. The owner keeps final authority; exact LabForms caption
controls, live theme/language and no flags remain explicit rulings. The current
interface owner records desktop acrylic. Suggestions to remove those caption
controls or Spanish do not override the owner.

The reference was opened in one temporary browser tab, reviewed visually and
through automation, then closed. It has a compact table, search/Errors row,
quiet status bar and a lower inspector with vertical sections. Those latter
tasks enter through their owning implementation milestones, using TableView and
native WinUI controls rather than the prototype's browser state model.

Decision review, in series: the everyday user needs truthful transfer status;
the keyboard user needs reachable commands and error reasons; the Fluent
designer takes variant C's information hierarchy; the Windows engineer flushes
critical metadata before renaming and verifies test pipe ownership; the
libtorrent engineer uses upstream dirty state and clears disk errors on Resume;
the heavy seeder avoids rewriting idle torrents; the persistence engineer
reports overload through existing completion paths; the product owner fixes
concrete faults and rejects changes that contradict explicit owner rulings.

Accepted source findings: #44 checks now verify the server PID before sending
any command; an attempted Restart check against review engine 14604 refused
without changing its membership or stopping it. #37 progress cells stretch to
the column width. #42 rejected storage jobs receive failed outcomes on the
existing completion queue. #43 payload disk errors and Resume recovery use
libtorrent's error/upload-mode state; #33 checkpoint warnings retain actual
transfer status. #39 temporary metadata is flushed before replacement and
periodic checkpoints respect dirty state while retaining failed saves.
Source and focused runtime evidence do not claim a physical power-cut test.

The latest native build passed FailedCommit and Restart with test pipe ownership
enforced (`FailedCommit-56b8c85a-ac80-4eff-8a57-6efcd3ae7ddf` and
`Restart-1e048233-7a54-4f57-ac30-f52aa4248a2d`). `DiskError` watches the concrete
failure where a payload write error looks like a normal transfer and Resume
cannot recover after the destination is repaired. A local peer and an empty
directory colliding with the payload filename produced the error; removing that
fixture and using Resume received payload bytes again. The check passed at
`DiskError-1f7a9a29-6eda-43eb-af5f-e74dd9b08dfe`. This libtorrent runtime transition
is not guarded by compilation or the existing persistence checks. Both fixture
processes were closed. The actual stray-folder checks printed nothing.

The language warning now follows a current command error in feedback priority,
so a failed Pause is never hidden by an older unsaved-language warning.
Reopening the UI restored Spanish and its retained warning; the screenshot is
`artifacts/evidence/final-ui-review/unsaved-reopen.png`. Resizing through the
native edge below the minimum clamped to 720 by 560, with all caption actions
and native buttons in bounds (`final-ui-review/minimum.png`).

## MVVM ownership

The owner requires MVVM for the product. The implementation decision was reviewed
in series. The everyday user wants drafts and selection to survive refreshed
transfer data. The WinUI engineer keeps native picker, dialog and window lifetime
in views, with property and command bindings for presentation state. The engine
engineer retains one confirmed-state authority and one pipe client. The maintainer
prefers concrete view models and the existing notification interfaces over a new
framework or service layer. The product owner applies that small split to the
current screen before adding more surfaces.

`MainViewModel` owns the display collection, selection, connection state,
feedback, settings changes and window commands. `AddDraft` owns source,
destination, start-paused choice and preview/submit/cancel operations. The engine
continues to own accepted torrent state and persistence. `MainWindow` binds to
those presentation owners and retains only native UI behavior. Existing pointer
and keyboard gestures use the same commands. This split prevents draft decisions
and pipe commands from accumulating in control event handlers without introducing
parallel application state or speculative interfaces.

The fresh MVVM review found three defects: stale transfer rates after disconnect,
discarded connection diagnoses, and a successful durable Add appearing rejected
when its following refresh failed. Rate text now remains unknown until a usable
snapshot arrives; the transport retains a launch failure through reconnect
timeouts; and acknowledged addition completes while failed reveal refresh is
reported separately. Cancelling a preview keeps editing disabled until release
finishes. The view has no pipe client or JSON state.

The real UI exercised native file selection, two-way destination/start-paused
bindings, Add, selection, Ctrl+S Resume, pointer Pause, live Spanish and light
theme, closing and reopening the window, and Exit. The 64 MiB local download
completed with SHA-256
`62B89C1E1DF10F82EB427DA59876867D19EDD50507DDB64B9953A46A381C9E9B`, matching its
seed. Screenshots are `mvvm-ui/draft-es.png` and `mvvm-ui/transfer-light.png`.
Opening immediately during the old UI's teardown was ignored once; ordinary
Open after the process exited restored the confirmed row. The overlap case is
a recorded defect for Background and desktop behavior's activation/handoff work.
These runs do not claim that the later inspector or preferences journeys exist.

The final product build passed. In a real launch with this session's generated
engine executable temporarily hidden, the actionable missing-engine diagnosis
remained visible through three seconds of subsequent connection retries;
aggregate speeds displayed em dashes (`mvvm-ui/missing-engine.png`). The executable
was restored in `finally`, the same UI reconnected to the isolated review store,
and Exit closed both processes. The latest source re-review reported no remaining
actionable findings within First usable download and passed MVVM ownership.
All launched engine, product and transfer fixture processes were closed.

## Everyday torrent actions: design and ownership

First usable download was committed as `e0a5a92`. The next milestone extends its
existing owners; it does not introduce a second command or draft implementation.

The everyday user asks for one Add form when many sources arrive and an immediate
native file picker. The heavy seeder wants duplicate content to preserve existing
choices, with an explicit offer for new trackers. The libtorrent engineer requires
zero-priority guards before magnet acquisition and transfers that guarded handle
to accepted membership only after durable choices commit. The Windows engineer
retains launch inputs until the single UI acknowledges their ownership and treats
association input as data rather than maintenance options. The keyboard user keeps
editor keys and uses the established torrent shortcuts on the table. The WinUI
designer follows variant C: compact search/Errors row, the main table, a quiet
status bar, and a lower General inspector. The maintainer keeps one MVVM owner for
each draft and reuses one native file browser for Add and Files. The product owner
exposes real commands as their milestones implement them, rather than presenting
inert Move, Delete files or Preferences entries ahead of their owning work.

These answers preserve ordinary client behavior with the smallest arrangement:
one batch draft, one tree of file choices, one source handoff and one engine
operation path. Unknown magnet metadata permits Add with all files wanted; known
single-source metadata permits wanted and priority choices. Batch additions keep
one destination and paused choice, with later file editing in Files. Search changes
visibility alone. Folder choices affect all descendants; bulk actions affect the
files matching search. Preview work runs only while the form consumes it.

Queue order records the user's explicit ordering and applies it through
libtorrent's queue API, including restart restoration. It does not become another
transfer scheduler. Session pause is a saved session choice and never rewrites
per-torrent paused choices. Removal commits membership before removing a handle,
without a delete-data flag. Global and alternative speed limits are ordinary saved
choices applied through libtorrent; the status toggle shows which set is active.

The guarded magnet's save-path transition was also decided in series. The user
wants the chosen folder to be the actual destination. The libtorrent engineer
requires the asynchronous storage result before releasing priorities. The Windows
engineer avoids moving payload that a preview must never have created. The
maintainer retains the existing handle and three explicit addition phases rather
than removing and recreating a swarm. The product owner selects a paused
`reset_save_path` operation, followed by its completion alert, then the same
durable addition path. Failure removes only the unconfirmed handle and reports
the addition failure. No accepted torrent or existing payload is deleted.

The enum home follows the native language. In series, the C++ engineer chooses
`Enums.h`; the managed engineer retains `Enums.cs` for C#; the maintainer wants
one discoverable vocabulary per project; and the reviewer rejects padding a tiny
required home to meet the general fragmentation minimum. The product owner
selects those language-specific homes with that narrow minimum-size exception.

Focused native checks now prove magnet metadata without payload, cancellation
remaining absent after restart, saved session pause with individual stopped intent
preserved on Resume Transfers, Remove retaining real payload through restart, and
queue-down plus atomic row-drop ordering surviving restart. `PreviewGuard` watches
unconfirmed payload writes that the compiler and ordinary Add do not catch.
`RemoveKeepFiles` watches late checkpoint resurrection and accidental payload
deletion. `QueueOrder` retains a queue branch defect found in source review and
watches loss of the explicit order on restart. The existing `Restart` check now
also watches session resume starting an individually stopped torrent. A first run
of that extension incorrectly inspected the reply wrapper for appearance settings;
correcting the check to inspect its snapshot data made the existing behavior pass.

Evidence directories are `PreviewGuard-ef1e2b0f-cbd3-4aaa-bbc7-2950cbcd1928`,
`Restart-b2a89768-91e9-48f8-9a8e-b5735f399527`,
`QueueOrder-3c62d466-f1a9-4f6a-be7c-b5553b3cf592`, and
`RemoveKeepFiles-11171033-6a21-4aef-8807-82ae561d4faa` under
`artifacts/evidence`. Each run used the current native Release binary, closed its
engine and local peer, and passed the stray-folder check. These checks do not yet
claim hands-on completion of the WinUI journeys.

Rate-limit scope was decided in series. The everyday user expects a global
limit to cover every transfer. The heavy seeder accepts slower LAN transfers
while that explicit limit is enabled. The libtorrent engineer adds the global
peer class by socket type, preserving existing IP-class defaults. The maintainer
avoids a second LAN limit or exception toggle. The product owner chooses all
traffic under either configured pair; ordinary unlimited defaults still apply.

The fresh Everyday torrent actions review found six defects: routine snapshots
cancelled queue dragging; confirmed additions stayed hidden behind filters;
disconnected speed edits could be discarded; preview metadata errors were ignored;
queue shortcuts required a keypad; and paste/drop duplicated source decoding.
The application now refreshes queue/natural order only when queue positions
change, clears filters on confirmed revelation, retains refused speed-limit
drafts, displays preview errors, handles ordinary plus/minus keys, and uses one
paste/drop decoder. A second source review and the remaining UI evidence still
gate completion.

The second review found the alternative-limit toggle remained usable while
writes were unavailable, and a recovered preview retained an obsolete transport
error. The toggle now binds to the existing editing capability. A successful
preview clears its own recovered acquisition error while preserving an uncertain
Add outcome and explicit addition refusals. These fixes do not treat reconnection
as permission to submit unfinished input.

Queue refresh policy was decided in series. The everyday user wants a slow drag
to survive an unchanged transfer tick. The TableView maintainer preserves the
public contract that an explicit refresh cancels a drag. The libtorrent engineer
uses queue positions as the authoritative base order. The application maintainer
derives whether that order changed during snapshot application, without storing
a second queue. The product owner chooses no explicit refresh for unchanged
queue or natural order, with normal refreshes for other active sorts. The existing
projection also follows queue order so clearing a header sort preserves the same
meaning for a row drop. The library's public API and implementation stay unchanged.

Reconnect source retention was decided in series. The user keeps every supplied
magnet or file input when equivalent sources collapse to one torrent. The seeder
keeps distinct tracker URLs, whose paths can be case-sensitive. The libtorrent
engineer reacquires all retained inputs through the existing hash-based preview
owner. The maintainer stores those inputs as facts on the one draft entry rather
than a second preview implementation. The product owner selects literal input
deduplication and retains the full input list when previews coalesce. Reconnect
then rebuilds the same tracker intent instead of retaining only the first URI.

The current Windows review session is locked: WTSInfoEx reported session 1,
SessionFlags 0. The capture shows black client content despite a populated UI
Automation tree. Input review stops while locked; code and native checks continue.
Those circumstances establish no rendered or hands-on UI evidence and do not
mean that the user withheld authorization. The earlier incomplete picker attempt
was an automation targeting error, corrected in the First usable download review.

`SelectedTransfer-46224db3-a110-4041-a9a3-4c665f706209` proves a real selected-file
download: the skipped aligned 32 MiB file received no payload; the wanted file's
SHA-256 was `EC8ABFBE8A399AA3E2D30E329594B3EE12D5DA193A1AA52C9F5A654F66C2E302`,
matching its seed. Over ten-second payload intervals, the configured normal
131,072 B/s limit produced 112,776 B/s and alternative 524,288 B/s produced
472,644 B/s, including the local peer. Tracker merge and cancellation retained
one durable identity, the original destination, priorities 0/7, stopped intent,
payload and merged URL across restart. Force start set forced intent; ordinary
Resume cleared it. The check watches skipped payload writes, local limit bypass
and destructive duplicate merging, which acknowledgement checks cannot prove.
The fixture build and focused run passed; each stray-folder check was empty and
all launched processes closed. Separate checks below cover upload ceilings and
corruption repair. `QueueOrder-052e3563-8280-4bad-8ca1-3f28561d377e` also passed after
startup restoration was ordered before transfer activation.

Accepted unknown-metadata magnets exposed a real transfer failure in
`MagnetDownload-1fad4212-0f8d-4544-85c0-b7ac4f8c7c26`: no payload arrived.
A bounded Force diagnostic acquired metadata, but still showed zero wanted bytes.
The pinned libtorrent initializer retains `default_dont_download`; its runtime
flag setter does not release it. The decision was taken in series: the everyday
user expects Add to download every file when no selection is yet possible; the
seeder requires Cancel previews to stay harmless; the libtorrent engineer applies
explicit priorities after metadata instead of relying on an unsupported flag
change; the Windows engineer keeps saved intent independent of temporary metadata
acquisition; the maintainer uses the existing intent owner; the product owner
chooses this small correction and a real download/repair check. Running accepted
magnets acquire metadata outside automatic queue management, then the same owner
applies the saved choices and restores queue policy. Missing metadata cannot
establish completion. Dropped alerts reconcile the same saved intent and hashes,
except during shutdown or an unresolved accepted alias conflict.

`MagnetDownload-09fdff67-c7ba-452a-bf07-2bf9180e4d3b` passed: Add preceded metadata
and the local peer; the full 64 MiB payload completed in the chosen destination
with SHA-256 `62B89C1E1DF10F82EB427DA59876867D19EDD50507DDB64B9953A46A381C9E9B`.
No preview payload remained. An offline byte corruption changed that hash; after
restart, Verify and Resume restored it. An earlier harness incorrectly compared
download counters across restart/verification; that redundant assertion was
removed because the epochs are not comparable. The physical corruption and
repair assertions remain. The clean focused run passed, all owned processes
closed, and its stray-folder check was empty.

The third review found two further divergences: a fresh torrent displayed ratio
zero but sorted as infinity, and pasted or dropped magnets ignored Never show
again. Ratio now has one numeric owner for sorting and formatting. Supplied
sources now follow the same saved Add-form preference. The preference decision
was taken in series: the everyday user expects Never show again to apply to
paste; the keyboard user keeps the explicit Add magnet editor for entering a
new link; the seeder keeps the explicit duplicate tracker offer; the maintainer
uses the existing shared Add path; the product owner chooses those ordinary
semantics without a source-format exception.

`BatchUi-7eabfb34-a7a3-4235-bc10-ace6b4415448` exercised the real engine launch
entry with thirty distinct torrent paths and read the real WinUI automation tree.
One product HWND exposed one Add task, `30 sources`, and an enabled Add all
button; the engine still had no confirmed torrents. This proves batched launch
handoff and a populated form, without asserting Explorer association behavior
or rendered appearance. No keyboard or pointer input was sent on the locked
desktop. The isolated unconfirmed UI was terminated for cleanup, its engine
then completed coordinated Exit, and the stray-folder check was empty. This is
not evidence for the ordinary draft-close prompt. Registration and the actual
Explorer entry remain for the desktop milestone.

The fourth review found that sources arriving during Remove confirmation could
be acknowledged and then cleared when a second ContentDialog failed to open.
It also found two shortcut registration implementations with different modal
rules. The modal decision was taken in series: the everyday user keeps incoming
sources; the keyboard user keeps commands inside the active task; the Windows
engineer permits one ContentDialog at a time; the maintainer makes the existing
shortcut owner accept the platform accelerator and derives one modal guard;
the product owner defers Add until confirmation closes. Remove now exposes its
modal lifetime to that guard and Close, and failed display does not count as
user cancellation. Cancelled window closure restores the previous editor after
closing state ends, preserving its input.

`UploadLimits-875b320e-5524-4420-a5cb-109a857bc018` measured real upload with
WinUI closed. Ten-second payload intervals in the same engine lifetime produced
120,203 B/s for the normal 131,072 B/s ceiling and 450,295 B/s for the alternative
524,288 B/s ceiling. The local leecher received the payload; changing the active
pair replaced the normal limit. This measurement used an evidence script, not
another retained test of the already-covered global-limit rule. Both processes
closed and the exact stray-folder check was empty.

Unnamed magnets were decided in series: the user needs to distinguish a waiting
row; the keyboard user needs a searchable identity; the libtorrent engineer uses
the already-known full info hash until metadata names it; the maintainer derives
that fallback while taking the snapshot; the product owner chooses that ordinary
identity instead of a blank row. No additional stored display name is needed.

The fifth review found four concrete gaps. Native activation bypassed an open
draft when Show the Add form was off; the speed editor accepted newer typing
while its captured edit saved; General fetched a full file tree merely to read
the destination; Remove missed the established live dialog refresh. Existing
WinUI now receives native sources and applies the same Add choice used by paste
and drop; closed-window direct addition remains native. Speed editors and Apply
bind to the existing editing capability while submission is pending. General
reads the destination already carried by its row, eliminating its unnecessary
detail request and stale-reply state. Remove joins the common theme, direction
and text refresh with its captured confirmation scope.

The decisions were taken in series: the everyday user keeps the destination and
Start paused choice of the open form; the keyboard user retains unfinished input
and edits only when a save can accept it; the Windows engineer routes incoming
sources to the existing UI; the libtorrent engineer keeps closed-window addition
on its established native owner; the maintainer shares the existing Add decision
and uses summary facts for General; the Fluent designer updates every open
dialog together; the product owner chooses these existing owners without a new
protocol, state machine or presentation authority.

`OpenDraft-35645795-7a4e-4e7f-a26b-89fdfc77a759` verifies the native activation
fix against the real window: one source opened Add, Show the Add form was saved
off through the engine, and a second engine launch joined that existing form.
The automation tree showed `2 sources` and the confirmed list stayed empty.
No desktop input was sent. Owned processes closed and the stray-folder check
was empty. This proves source routing into the open draft; it does not prove
pointer edits of destination or Start paused, or Explorer associations.

The localisation owner now records the user's explicit live English/Spanish
requirement during implementation. English remains the canonical key/text
authority; Spanish accompanies settled implemented surfaces. Other catalogues
retain the original freeze until their translation task. This records the
user's ruling where future changes read it instead of leaving contradictory
development instructions.

The sixth review found Paste could assign a new magnet during Add's captured
submission, or after its view closed, and that moving a no-wanted-files source
into a batch hid its recovery controls. Paste and preview actions now share the
draft's editing capability; a clipboard reply applies only to its loaded,
editable view. A blocked batch source offers Select all files through the same
file-choice owner, without depending on its old search filter.

Batch recovery was decided in series: the everyday user needs an immediate way
to make Add all available; the heavy seeder keeps previously selected priorities;
the keyboard user gets a named action beside the offending source; the designer
keeps one compact form; the maintainer shares the existing wanted-choice rule;
the product owner selects explicit recovery rather than resetting selections
when a second source arrives.

The seventh review found that Get metadata staged a magnet while leaving the
same text pending for Submit. Removing its staged source could therefore add it
again. The draft now consumes that input synchronously when it stages the source,
before awaiting preview work, and the view calls that one operation. The staged
entry retains failed input and choices; explicit removal leaves no pending copy.

The eighth review found inconsistent Queue sorting for completed seeds and full
file-priority/tracker arrays copied into every routine summary. Queue display
now has one derived sort key shared by natural order and its column. Engine
queue moves operate only on downloads; seeds remain selectable but cannot move.
TableView gains one setup-only row reorder predicate through its public schema,
independent of selection eligibility, so the product does not invent a gesture
implementation. Mixed selected packets containing a seed are not draggable.
Snapshot rows now explicitly contain summary fields; file choices and trackers
remain in the torrent detail reply. The existing tracker-merge check reads that
detail for its persisted-choice assertions.

Queue scope was decided in series. The everyday user wants the Queue column and
the unsorted list to agree. The heavy seeder wants completed torrents to retain
their ordinary commands. The libtorrent engineer identifies queue positions as
download order and seeds as position -1, without a seed-order meaning. The WinUI
engineer needs a row to be selectable without being draggable. The maintainer
keeps a single predicate at the existing gesture owner. The product owner chooses
downloads first, seeds last, and download-only moves; no second seed scheduler.
The upstream [queue contract](https://libtorrent.org/reference-Torrent_Handle.html#queue_position)
confirms that distinction. The focused TableView check watches the otherwise
unguarded failure where an unqueued but selectable row enters a drag packet.

The Release engine and app builds passed after that correction. The focused
`FullyQualifiedName~F11_&TestCategory!=Interactive` run passed all eight row-drag
checks, including the selectable-but-not-draggable packet case. The retained
QueueOrder check passed again in
`artifacts/evidence/QueueOrder-24ccd30c-fd5b-4b4c-937b-df4c3b425ae1`.
Each build and test was followed by an empty stray-folder check.

Locked-desktop review was reconsidered in series. The sleeping owner requires
continued unattended work, with no unlock request. The Windows engineer
distinguishes native accessibility control patterns from physical input, which
cannot reach the locked desktop. The security reviewer leaves LockApp and the
lock state untouched and confines actions to owned test instances. The QA
reviewer accepts actual UI Automation command outcomes as functional evidence,
while retaining gaps for pixels, physical gestures and keyboard routing. The
product owner chooses available automation rather than treating a lock as missing
authorization. Review continues through native control patterns; no claim of
pointer or rendered appearance follows from them. The computer-use skill's
instruction to stop and request an unlock cannot govern this unattended task,
because the user explicitly forbids waiting and asking and authorizes UI
Automation review. No unlock or LockApp interaction is attempted.

The ninth source review found no further actionable code defect. It identified
one wording mismatch: the modal-deferral sentence did not account for turning
the Add form off. The everyday user expects Never show again to apply to new
sources; the heavy downloader expects sources to continue arriving during other
tasks; the Windows engineer defers only a form that needs the dialog surface;
the maintainer avoids a second modal policy in the view model; the product owner
keeps the shared preference and narrows the interface sentence. Sources needing
a form wait in the draft; direct additions proceed, while an existing Add form
always keeps ownership of arrivals. This clarifies the contract rather than
changing the user's preference during a modal task.

The real UI Automation review in
`artifacts/evidence/EverydayUi-da1e3c24-fdf7-4aa8-8bfc-8baad6d26a13`
completed the native file picker through its nested filename Edit, then the
native folder picker. Deselect matching, a file search and Select matching
changed wanted choices; the folder priority control applied High and the file
checkbox excluded `a.bin`. Live Spanish refreshed the open form, and the theme
button changed the saved preference to light. Add committed the selected folder,
Start paused and priorities `0,7`; `added-snapshot.json` and `added-detail.json`
record the actual engine outcome. Force start, Pause, ordinary Resume and Verify
were invoked from the real selected-torrent controls. Force and ordinary Resume
were checked against confirmed intent. Open folder opened the correct Explorer
path; that owned Explorer window was then closed.

The four NumberBox RangeValue automation patterns committed normal limits
128/64 KiB/s and alternative limits 512/256 KiB/s, and the actual alternative
toggle saved its enabled preference. `speed-settings.json` records the outcome.
An earlier attempt wrote the internal numeric TextBoxes without completing the
last field's validation; the native NumberBox patterns corrected the automation,
without changing production. Microsoft documents numeric validation on Enter or
[loss of focus](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/number-box).

An engine crash left the real Add form's wanted file, Start paused and edited
destination intact in `reconnect-before.json` and `reconnect-after.json`.
That first fault harness was insufficiently isolated: the UI automatically
started the default background engine before the explicit isolated restart.
No Add was submitted to that engine. The draft was cancelled and coordinated
Exit closed both owned processes. The recovery observation proves draft retention,
but not a same-store retry outcome; a corrected fault check must prevent that
automatic launch while replacing the isolated engine. All owned pickers, app,
engine and Explorer windows closed; the stray-folder check was empty.

The corrected same-store fault check passed. It held the owned UI only during
replacement of the isolated engine, preventing default-store startup. Add was
pending when that engine stopped; after reconnect, membership stayed at the one
previous torrent through repeated snapshots. The form retained its edited folder,
Start paused and unwanted file. Explicit retry then committed the second torrent
with priorities `0,7` and those original choices. The captures are
`isolated-draft-before.json`, `isolated-uncertain-reconnected.json`,
`explicit-retry-snapshot.json` and `explicit-retry-detail.json` in the same
EverydayUi evidence folder. Coordinated Exit closed the app and engine, and the
stray-folder check was empty.

The summary/detail change's selected-transfer check passed in
`artifacts/evidence/SelectedTransfer-5249dcd5-cae5-4a24-ae48-2f49b8c11756`.
The milestone-end TableView suite ran once with `TestCategory!=Interactive`:
208 passed, none failed or skipped, in 21 seconds. Its report is
`artifacts/TestResults/milestone2-final.trx`. Both checks ended with an empty
stray-folder check. Physical input tests were excluded because the desktop
remained locked; their UI and rendering gaps remain explicit.

The thirty-source functional UI review passed in
`artifacts/evidence/BatchUi-408c5bde-413e-48d0-85aa-e2e929eafcf6`.
Thirty separate native activations joined one real Add form, and Add all committed
all thirty with the selected folder and Start paused. Table search isolated
`transfer14.bin`; its queue menu moved it up, and Remove committed its removal.
Spanish refreshed the open Remove dialog before confirmation. These are control
pattern actions, not a claim of Explorer association or physical dragging.
The earlier Remove assertion read a snapshot before the asynchronous UI command
finished; the corrected harness waits for confirmed membership. It changed no
production behavior. All owned processes closed and the stray-folder check was
empty.

Hands-on automation also exposed an Add file-search defect: clearing and
rebuilding matching hierarchy collections collapsed a folder that still matched.
The everyday user expects the matching file to stay visible; the file-picker user
keeps wanted choices independent of search; the WinUI engineer retains tree nodes
instead of reusing their containers after a wholesale clear; the maintainer uses
one collection-reconciliation rule for roots and children; the product owner
chooses stable matching folders over resetting the entire tree. FileSelection
now removes only nonmatching nodes and inserts newly matching nodes in their
original order. No new expansion state or renderer is introduced.

The focused real-control search check passed after the Release app build in
`artifacts/evidence/EverydayUi-28d1f2b0-6481-431e-a6e3-527f552dfb85`.
Searching for `a.bin` retained its expanded folder; clearing search restored
`b.bin` without collapsing that folder, and the unwanted choice on `a.bin`
stayed off. The captured control tree is `file-search-cleared.json`. All owned
processes closed and the stray-folder check was empty.

The tenth fresh review found that routine torrent commands lacked an explicit
screen-reader outcome notification. The Narrator user needs confirmation without
moving focus; the WinUI engineer uses the existing table automation peer's
notification API; the libtorrent engineer distinguishes an accepted Verify from
finished verification; the maintainer keeps one presentation event and one native
announcement path; the product owner keeps transfer ticks silent. MainViewModel
now announces accepted commands and reported failures through that event. Add
announces its batch outcome once. The text says request accepted so asynchronous
work is not reported as finished. No live telemetry invokes the path. This uses
Microsoft's [notification API](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.automation.peers.automationpeer.raisenotificationevent).
Actual Narrator speech remains a hands-on gap on the locked desktop.

The eleventh fresh adversarial review found no further confirmed source defect.
It withdrew a padding-path candidate because no supported libtorrent layout
established its proposed failure. The final app Release build passed and the
stray-folder check was empty. A final Add search journey also passed in
`artifacts/evidence/EverydayUi-4b5aa9c7-dd30-47c5-a970-82c3c273edd3` and closed
its owned processes. Milestone 2's functional and source gates pass; physical
pointer/keyboard routing, actual Narrator speech and painted variant C/LabForms,
scaling and contrast acceptance remain explicitly unproven. The same thirty
native source activations used by Explorer were verified; exercising registered
Explorer associations follows the desktop registration milestone.

## Background and desktop behavior: implementation decisions

The everyday user needs Open to survive a window that is closing, and a Restart
action when transfers stop. The Windows engineer records the actual ready UI's
process and acknowledges activation rather than assuming a pipe write showed a
window. The maintainer separates initial direct-UI engine startup from later
failure recovery. The product owner chooses an explicit Restart after failure,
while reconnecting automatically if the engine returns independently. Recovered
drafts are never submitted by that restart.

For resident status work, the heavy seeder rejects a blocking status query per
torrent every second. The libtorrent engineer uses state-update alerts without
piece bitfields, retaining the last status per accepted torrent. The tray and
sleep owner consume a cheap aggregate of those statuses. The maintainer keeps
one status classification for window rows and native activity and one handle
index for alerts. The product owner accepts a one-second telemetry cadence;
commands and membership remain authoritative immediately. A new or restored
torrent gets one initial status before entering that cache. Completion feedback
requires payload received in this run, so restoring or verifying an existing
seed does not create a completion notification.

The Windows lifetime engineer uses the top-level broadcast window for session
end and a bounded noninteractive save. The storage engineer keeps asynchronous
completion pumping and labels normal save failure Retry or Exit anyway. The
designer uses native system colors and DPI for feedback; the accessibility user
keeps native tray semantics. The security engineer gives per-user registration
one writer and observes actual target values without touching Windows' default
choice. The product owner keeps the exact two-status-row, three-command tray.

For Exit during a pending window command, the everyday user expects Exit to
continue once it settles, rather than silently cancelling. The storage engineer
keeps the submitted edit alive through its reply; the Windows engineer waits on
the existing presentation notification while native dialogs and the owner keep
pumping; the product owner prompts only if unfinished choices remain afterward.
The same close path first stops new operations, then waits for the work each
owner already accepted (an addition, a source receipt, an edit) or the picker
to settle, and then applies its existing draft protection. Unrelated commands
stay available while an addition runs; each owner blocks only the actions that
conflict with its own pending work. The native owner retains its hung-UI
deadline instead of inventing another window timer.

For idle checkpoints, the heavy seeder avoids rewriting unchanged metadata just
because libtorrent's active-time counters advance. The ratio user keeps actual
uploaded bytes durable; the libtorrent engineer uses progress/config/state/
metadata conditions and includes counters when payload upload has changed. The
storage engineer requests an unconditional retry after an application write
failure, because generating resume data can already clear libtorrent's dirty
flags. The maintainer records only the uploaded counter from the last successful
checkpoint. Final shutdown remains unconditional. A not-modified response is a
successful skip rather than a torrent error.

The draft owner keeps edits on failure; the everyday user needs recovery inside
that same task. The accessibility reviewer rejects putting Restart behind a
modal form; the WinUI engineer binds the same command in each affected surface;
the maintainer retains one launch implementation. Add and speed-limit forms now
expose the main presentation owner's Restart command. The app Release build
passed after these controls and the pending-close corrections; the stray-folder
check was empty.

### Background review corrections

The fresh Astra review found a lost Exit request when ordinary Close already
showed a draft prompt, discarded failed-addition details in combined native
notifications, language-dependent tray number formatting, and missing taskbar
relaunch properties. The window now retains an incoming Exit through its active
close flow and sends cancellation when the person keeps editing. Native number
formatting uses Windows regional preferences. Notification and relaunch
corrections retain the first failed source and reason in a combined notice and
put the engine relaunch command, icon, display-name resource and shared AppID on
the actual ready product window.

For taskbar routing, the Windows engineer puts the relaunch properties on the
actual ready product window; the maintainer keeps their implementation beside
the existing native shell APIs; the everyday user expects a pinned task to
reopen TinyTorrent through its tray owner; the product owner chooses the engine
as the target, preserving one activation authority. A normal SetForegroundWindow
attempt follows restoration. Windows provides taskbar attention when it denies
foreground activation, as documented by
[Microsoft](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow).

For sleep preferences, the laptop user expects the mains-power switch to govern
both activities; the overnight seeder reads Also while seeding as an extension;
the Windows engineer keeps one SystemRequired request and never prevents display
sleep; the product owner keeps that familiar dependent choice. The download
switch is the master, and Also while seeding extends it rather than silently
remaining active after the master is turned off.

For the paused torrent count, the everyday user wants 1 torrent to read correctly;
the translator confirms both shipped languages have the same integer singular
rule; the Windows engineer reserves ICU for languages whose rules need it; the
maintainer rejects another dependency for the same answer. The product owner
chooses explicit singular and plural keys for EN/ES at their current scope and
records the condition for revisiting it in the localisation contract.

The tray user recognizes TinyTorrent from its icon and wants a shorter window
command. The everyday user chooses Show window to distinguish it from opening
a torrent file; the heavy seeder wants the existing window restored; the Windows
designer keeps one clear verb and object; the product owner chooses Show window.
The same operation and double-click behavior remain. For the status rows, the
accessibility user requires normal readable text; the Windows engineer preserves
disabled menu semantics and draws only their text; the maintainer keeps native
commands and system colors; the product owner accepts this small presentation
change because grey status falsely looks unavailable. This follows the user's
updated ruling that statistics must not be greyed out.

The everyday user wants opening feedback without a second titled window. The
Windows engineer keeps the existing native splash independent of WinUI; the
designer uses a centered product icon and short status without a caption; the
accessibility user retains a named native surface and native Retry/Close controls
on failure. The product owner chooses that captionless splash for both opening
and recoverable failure, following the user's explicit ruling.

For combined background errors, the downloader needs a useful reason, the heavy
seeder rejects a burst of modal messages, the Windows engineer uses the existing
tray notification with error severity, and the product owner keeps a bounded
first name/reason plus the failure count. That context now survives coalescing
for both failed additions and torrent errors. A failed tray pause uses the same
nonblocking notification path, so it cannot hold the engine tick in a modal
message loop.

The second fresh review found unchecked registration operation types, missing
names/reasons in combined runtime errors, and Windows application restart
selecting a default store after a custom-store crash. Registration rejects an
operation of the wrong type before dispatch. Combined errors retain their first
name/reason and error severity. Windows restart records the canonical data
directory with background activation; it never replays an addition. The managed
explicit Restart uses ProcessStartInfo.ArgumentList to preserve directory
arguments, including a volume root.

### Advisory engine issues 57–66

The user requested a fresh Sol 6.1 assessment of these advisory issues. It read
the current engine and contracts, proposed the concrete changes, and implemented
only the approved engine state/queue/name corrections. No GitHub issue was
closed, edited or consolidated.

| Issue | Disposition |
| --- | --- |
| 57 | Fixed the concrete overload failure: Change owns its bound and returns refusal; commands report overloaded and rejected guarded additions release their handles. No current missed slot release was found, so the broader queue rewrite remains advisory. |
| 58 | Fixed independent alias conflict, hash-save and checkpoint failure state. Hash membership saves retry and unresolved final hash writes cannot be reported as saved. Checkpoint success clears only its own failure. |
| 59 | Fixed the confirmed unnamed-magnet discrepancy and unnecessary full status query. Preview, row, detail and notification use one cached name/hash fallback. Broader preview/tracker/priority deduplication remains a structural advisory. |
| 60 | Fixed the stale Loading tooltip by using one tooltip calculation, including loading state. Executable-path, registry and cross-language identity literals remain structural advisories without another demonstrated behavioral disagreement. |
| 61 | Retained the existing JSON command boundary and reply composition. The tray calls the engine owner directly, without self-IPC. A typed-command/envelope migration adds no required user behavior here and remains advisory. |
| 62 | Fixed modal pause failure feedback and the same direct-add storage-failure path. Both use bounded nonblocking native notices; engine ticking continues. |
| 63 | Direct-add success now names the confirmed torrent; duplicates say already in the list. Failed magnets use a localised Magnet link label instead of raw private URI text. |
| 64 | Closed-set enums remain a design advisory; no reported typo establishes a runtime failure. The concrete multiplexed-error bug is fixed by independent facts rather than a blanket conversion of the wire protocol. |
| 65 | Reviewed new names at their use sites; no mass rename was made. Broader legacy vocabulary/style suggestions remain advisory. |
| 66 | Kept the native desktop lifetime owner and existing registration component. The current change fixes concrete lifetime failures there; splitting its Win32 callbacks solely by count would add interfaces between code that shares one lifetime. |

Decision review in series: the downloader prioritises lost work and stalled
transfers; the libtorrent engineer keeps alias identity independent of storage
outcomes; the Windows engineer keeps one live engine and native message owner;
the maintainer centralises the actual queue bound and name rule; the product
owner accepts these small fixes and rejects automatic broad restructuring.
The native Registration header stays concise: two consumers need its declaration
and the implementation hides the registry details. Padding it or moving Windows
declarations into unrelated engine state would reduce clarity merely to satisfy
the generic file-size guideline.

The user subsequently assigned another agent an engine structure pass: command
and alert handlers, change-slot ownership, shared rules, typed status/failure
modes, pipe-owned request IDs, and a separate native desktop decomposition.
These are now authorised changes, rather than proposals to reject mechanically.
The work above is its starting state and must be preserved. This orchestrator
leaves engine/src untouched during that pass and reviews its completed diff and
runtime evidence before the milestone commit.

The third fresh review found two remaining native feedback defects for that
pass: a failed tray pause is currently suppressed while WinUI exists, though
that window never received the failure reply; and the completion preference
incorrectly suppresses direct-add acknowledgements. It also found a WinUI
Restart launch error hidden by generic disconnection text. The latter now shows
the reported error in the same disconnected feedback used by main, Add and
speed-limit surfaces, retaining the Restart command and unfinished input.

### Background checks

The prototype author wants its study commit preserved; the repository maintainer
keeps one primary development branch; the implementer avoids creating a second
primary history; the product owner follows the user's intent to keep product
work separate from the prototype. The user moved the shared checkout back to
`main`. There is no branch named `master`; the user's primary-branch instruction
therefore uses the repository's existing `main`, without a rename or extra branch.

Release builds of Engine and TinyTorrent pass. Each build and focused runtime
check ends with an empty stray-folder check. All check-owned engines, WinUI
windows, peers and temporary browser processes are closed.

| Evidence directory under artifacts/evidence | What ran and passed |
| --- | --- |
| CheckpointRetry-e03240e7-1c82-4ca3-bfe5-05046b30ae29 | A blocked application resume-file write was reported; repairing it allowed the automatic retry without another command, and membership/running intent survived restart. |
| QueueOrder-ead9afb0-6e54-42b9-863c-ca4ab87bc51b | The expected queue order arrived and survived restart. The check now waits for the observable asynchronous status update instead of assuming an immediate cached snapshot; the expected order is unchanged. |
| EverydayUi-12fb5a91-9ee5-4e81-bf45-3345b1f13133 | Two real engine failures left the same Add window and its choices intact. No engine restarted silently. Each explicit Restart used the same store and reattached that surviving UI; Add then applied its original paused/file choices. |
| EverydayUi-de64be10-8e98-42d0-a7f2-1a828ed87db3 | Exit arriving during an ordinary Close draft prompt cancelled when Keep editing was invoked; the repeated overlap with Discard closed both processes successfully. |
| DesktopTray-fc9c0a65-a0c0-4684-9152-a7ea82cb838a | The real native menu exposed two disabled status rows, two separators and three commands through Win32 and MSAA. Pause/Resume changed session state while preserving the individually paused torrent. Exit ended the engine. |
| DesktopBackground-534d1ca2-2daa-4d01-adef-d37f29cf3450 | With WinUI absent, a 64 MiB local payload downloaded and matched SHA-256 62B89C1E1DF10F82EB427DA59876867D19EDD50507DDB64B9953A46A381C9E9B. The engine emitted exactly one completion event. |
| DesktopSessionEnd-3db9321c-57b2-4f80-ad63-7212c234a76b | WM_ENDSESSION sent only to the owned engine's broadcast window saved and exited in 584 ms; a deliberately blocked final checkpoint exited with failure in 193 ms. Durable membership remained recoverable. This did not log off Windows. |
| DesktopRegistration-884ac365-3efc-4d8b-8780-02df72fc176e | Running-engine forwarding and short-lived maintenance used the same registration owner. Handler/startup targets were exact; defaults and other handlers remained unchanged. Only absent TinyTorrent entries were created, and those were removed afterward. The check exposed the registry read-buffer size error, which is fixed. |
| DesktopWindow-171ba2a8-3804-4977-8b4b-ce242555d57b | Ordinary Close released WinUI while the engine continued; the first-close notice flag was durably saved; the actual native double-click callback opened a fresh usable window. The ready window's shell properties contained Engine as relaunch target, its icon/display resources and the shared AppID. |
| DesktopStartup-6efe2bd3-523a-453c-a808-240407793769 | An isolated delivery without WinUI showed native failure and Retry on a captionless surface. Retry opened the repaired delivery; readiness removed stale feedback. GetApplicationRestartSettings returned the canonical store and background arguments. |
| Frames-3b225228-dc56-430c-9c86-818b0b6a3dfd | Malformed registration operation input returned invalid_request; the next valid snapshot still worked. Existing fragmented/invalid/oversized frame checks also passed. |
| RestartFailure-21b111ce-bd48-4b42-8346-e3960ecd554a | Removing only the isolated delivery's engine executable made explicit Restart fail. The real Add form exposed the launch error and retained destination/Start paused. Restoring that executable and invoking Restart reattached the same UI and store without submitting its draft. |

The added CheckpointRetry check watches loss of a failed application checkpoint
after libtorrent has already cleared its dirty flag; prior failed-membership
coverage does not exercise that retry. The Frames extension watches malformed
registration terminating the owner through the new desktop dispatch branch;
framing alone cannot guard a valid envelope with an invalid operation value.
No TableView code changed in this milestone, so its full suite was not rerun.

Machine-check gaps remain explicit. The locked desktop prevents physical
pointer/keyboard and painted acceptance, actual Narrator speech, and notification
delivery observation. Native menu state and accessibility actions were checked;
normal text color is established by source using COLOR_MENUTEXT, not a pixel
measurement. Windows powercfg /requests requires administrator rights here, so
the global power-request counter was not observed. The mains/battery/display
policy is implemented and source-reviewed, but actual idle sleep and battery
transitions need a hands-on check. Registered default-choice UI and actual
taskbar pin/relaunch belong to later manual/release checks; no Windows default
was changed and no installer was produced.

## Details and preferences preparation

The owner's parallel engine pass has exclusive ownership of `engine/`. WinUI
preparation continues separately; the background milestone still needs its final
integration review before its completion commit.

The inspector design follows the reviewed Files prototype using native WinUI
controls. The following roles considered immediate file edits and navigation,
in sequence:

- Everyday user: a wanted checkbox or priority choice applies immediately; viewing
  progress must not create an unsaved page.
- Heavy seeder: keep a failed choice visible and bind it to the durable torrent
  identity, so removing and re-adding content cannot redirect an old edit.
- Product owner: keep the six required views and omit unrelated controls; one
  shared file browser serves Add and Properties.
- Fluent designer: use native navigation, the existing TreeView for
  hierarchy and TableView for Peers and Trackers. Use native progress indicators,
  theme brushes and text roles rather than copying web components.
- Keyboard user: retain expansion and focus while progress changes; all bulk
  actions state their filtered scope, while folder choices still affect every
  descendant.
- Accessibility reviewer: expose file identity, checkbox state, priority and
  progress in native controls; avoid announcing every transfer tick.
- Engine engineer: transmit only changed file indices and priorities. A tracker
  list is one coherent edit, with Save and Cancel, rather than several uncertain
  mutations.
- Maintainer: keep the file-choice rule at FileSelection. A separate Edited event
  distinguishes intended choices from ordinary refresh notifications.

The resulting design applies ordinary file choices once, retains failed choices
for an explicit retry, and protects only actual unfinished input. Inspector
detail requests belong to the visible section and durable target; obsolete
replies are consumed without replacing the current view. Progress refreshes
update existing tree nodes rather than rebuilding their expansion state.

The separate Preferences model and native form now compile through the existing
Release WinUI target. They are preparation, not yet connected to the application
page or treated as a completed journey. The four serial reviews chose individual
field commits for the everyday user; native navigation, editors and textual
schedule facts for Windows and accessibility; existing settings/registration
owners for engine semantics; and one field draft owner for maintainability.

Root review corrected a rate edit that relied on dispatcher snapshot timing:
SaveLimits returns its validated, saved byte values, and the preference confirms
those values. It also corrected focus departure into a draft-protection dialog
submitting that very draft; departure commits only within Preferences. Native
switch labels use the live catalogue. The compiler exposed and resolved the
C# 14 `field` keyword, generated names hiding platform members, and an unqualified
static x:Bind call. English/Spanish catalogue integrity passed, and every build
and check left the stray-folder query empty.

The actual shared-file Add journey passed with the latest WinUI build in
`SharedFilesUi-a942855b-2412-44c6-9943-887bcbe5ecea`: toggling the native checkbox
submitted priorities 0,4, the chosen folder and paused intent; the committed form
closed without a draft prompt. It used the previously verified isolated engine
binary, leaving the incoming engine source pass untouched. A PowerShell
test-harness variable shadowed its UI Automation condition on the first attempt;
renaming that variable corrected the harness without changing product code.

Four serial roles revisited navigation after laying out the lower inspector:
the everyday user keeps all six sections discoverable; the Fluent designer uses
native top NavigationView and its overflow; the keyboard user keeps the platform's
composite navigation and focus behavior; the product owner avoids increasing the
minimum window merely to fit six vertical rows. Top navigation was selected
because it preserves the working width and usable height of the file browser and
tables. The interface document owns that decision.

The prepared Inspector model owns one durable target, visible section, confirmed
facts and failed edits. Peer and Tracker rows carry sampled raw facts, with
regional formatting and live text derived when read. The Files view reuses
FileBrowser. Peers and Trackers declare public TableView schemas with stable
endpoint/URL keys and typed sort keys; no library change is required. The tracker
editor preserves its coherent draft until Save or Cancel. SpeedGraph uses native
paths with separate figures across unknown time gaps. PiecesMap uses one BGRA
bitmap, 16-pixel squares, 4-pixel gaps and 6-pixel gutters after each group of
eight, matching the previous native map. Geometry and theme changes invalidate
the raster; equal data does not. Grouped squares use the documented state tie
order, received fill and a mixed-state corner. Arrow/Home/End navigation, a
read-only UI Automation value and Ctrl+C expose the same range facts as hover.
These surfaces are not yet connected to the product or runtime-reviewed.

The owner's latest reference is `app/prototype.html` at `4082f7f`, variant C,
compact-polished. An isolated, hidden Chrome preview rendered the table, status
drawer and all six inspector sections; screenshots are in
`artifacts/evidence/Prototype-4082f7f-details`. The preview browser was closed.
The application connection was unavailable, so the prototype agent's running
critique could not be retrieved; no claim of coordination is made.

Four roles considered how to carry this updated owner ruling into WinUI, in
series. The everyday user wants the six detail sections visible beside their
content and status choices available without permanently taking table width.
The Fluent designer chooses native left NavigationView for inspector sections,
a native collapsible pane for status choices and AutoSuggestBox for search.
The keyboard and accessibility reviewer keeps platform selection, focus and
scrolling and excludes interactive caption controls from the drag region.
The product owner keeps the existing LabForms caption metrics and the explicit
language and theme actions, with no flags. The result supersedes the earlier
top-inspector decision: the latest owner-selected prototype owns the layout;
WinUI, MVVM and TableView continue to own behavior and control semantics.

Their pending engine representation is explicit so integration has one answer:
`torrent` takes `view` (general/files/peers/trackers/pieces), with `include_files`
when opening Pieces. General adds comment, creator, created (Unix seconds),
piece_size and nullable private; Files adds downloaded bytes. Peer rows contain
endpoint, client, transport, incoming, encrypted, progress, download_rate,
upload_rate, downloaded and uploaded. Tracker rows contain url, tier, status,
seeds, leechers, downloaded, next_announce (Unix seconds or zero), and message.
Pieces returns metadata_ready, piece_size, peers, verified booleans, per-piece
availability, downloading index/progress pairs and, on opening, files with path,
first_piece and exclusive end_piece. `history` takes range five_minutes/day and
returns timestamped aggregate-rate samples; absent time remains a gap. `edit`
takes torrent_id and intended changes: indexed priorities or a complete URL/tier
tracker list. These fields remain preparation until the engine implements them.

The owner ruled that code about to change does not receive repeated intermediate
reviews. Testing now explicitly requires integration of a coherent slice before
independent review; the final milestone adversarial review remains. The updated
native layout is integrated in one batch: a SplitView status drawer, an
AutoSuggestBox in the caption, the lower inspector with native vertical
NavigationView, and the full Preferences page. The finding model owns one status
filter and bounded suggestions; its commands call established command owners.
Settings results navigate to fields without changing them. The inspector
suspends optional detail requests while Preferences is shown. One window-owned
confirmation task coordinates draft protection across navigation and Exit.
The native Thumb splitter exposes keyboard adjustments and its bounded height
through the RangeValue automation pattern. These are source changes awaiting
the batch build and runtime evidence; engine detail/settings integration is
still pending the separately owned engine pass.

The approved prototype is now `8614126`, variant C. Its final changes widen Add
and correct focused-row activation, focus return and shortcut scope. The native
host uses TableView activation, preserves its selection owner, returns inspector
focus to the table, and routes selection shortcuts from the toolbar and inspector
while leaving editors and Preferences alone. Add uses the approved 864-pixel
native dialog width, bounded by the window. Inspector and Preferences controls
are created when first opened, keeping their controls out of the initial-window
path. The owner's continuous-acrylic ruling applies the existing chrome tint
once at the root; caption, table workspace and filter drawer have transparent
fills above that same backdrop. TableView remains unchanged.

The integrated UI compiled in Release and passed the native journey in
`artifacts/evidence/NativeLayout-252fe99b-1f1b-4c59-81e5-b26424876ce1`:
status filtering, Enter on a torrent row, all six inspector navigation items,
the splitter's RangeValue adjustment, Preferences navigation, rapid language
choices, live theme switching, retained unfinished folder input, Keep editing,
and Discard followed by ordinary Close. Both owned processes exited. This used
the verified pre-refactor engine; it does not establish the pending detail wire
fields or the refactored engine's behavior. Painted acrylic, contrast, physical
pointer input and Narrator speech remain unverified.

Filter choices now keep their six identities across status ticks; their labels
and counts are computed from the current catalogue and torrent facts. Replacing
the choices each tick invalidated native selection during the first journey.
The later Close-check failures came from the automation helper reaching controls
of a dismissed dialog before its completion; onscreen targeting and a complete
theme-change journey separated the two user actions. No Close behavior was
weakened to pass that check.

Four roles considered saved layout independently, in sequence. The Windows
desktop engineer chose normal bounds, saved display scale and current work-area
recovery. The everyday user wanted the chosen table layout and inspector split
back on reopening. The keyboard user required a reachable caption and controls
after removing a monitor. The product owner kept this optional UI state out of
the engine and refused to block Exit over a layout-file failure. The chosen
implementation stores one WinUI-owned window snapshot on Close and uses
TableView's public layout API; no control-library change is needed.

The owner identified the repeated Windows Security popup as the engine's
firewall prompt. Earlier advice about a download marker addressed a different
tool and did not fix it. Native UI checks had copied Engine.exe into a new
delivery directory each time; Windows' program rules identify those separate
paths. Further checks use `artifacts/checks/delivery/Engine.exe`, with separate
stores and evidence directories. The owner-requested administrator script
`V:/temp/TinyTorrent-AllowFirewall.ps1` grants TCP/UDP inbound access on Private
and Public profiles only to that fixed path and the existing product build.
It is not product startup behavior or installer work. No administrator token is
available to this task, and no firewall rule has been changed by it.

The named stray-folder check's installed Everything CLI carries a download
marker in protected Program Files. An identical, validly signed copy in
`artifacts/tools/Everything/es.exe` has no marker and is used for these checks.
The Everything IPC endpoint is unavailable inside the restricted process, so
checks use its normal-process endpoint or the equivalent filesystem traversal.
The concurrent dependency checkout produced `3rdParty/tools/perl/bin` and
`3rdParty/boost/tools/boostlook/doc/bin`; these are downloaded input directories,
not recursive WinUI output. They were reported and left intact. The owner is
replacing vcpkg in another thread; that work remains outside this UI pass. The
owner gave up on vcpkg because Visual Studio's vcpkg integration deleted every
installed library in the middle of an ordinary build and compiled Boost,
OpenSSL and libtorrent again, after an unrelated tool change altered its
package fingerprint. [Third-party dependencies](architecture.md#third-party-dependencies)
records the ruling and the arrangement that replaced vcpkg.

## Wire representation

Protocol version 1 uses a four-byte little-endian UTF-8 JSON frame length,
bounded to 16 MiB. The endpoint is `TinyTorrent.<logon SID>`; each connecting
client first receives `{type:"hello",version:1,session_id:"...",data_directory:"..."}`. The absolute data directory
keeps the same store for explicit Restart, which starts the engine beside the
window rather than a path a pipe peer reports. Requests are
`{request_id:integer,command:string,...}`. Replies repeat `request_id` and have
`ok:boolean`, either `data` or `error:{code:string,detail:string}`. Native control
notifications are `{type:"activate"}`, `{type:"close"}`, and `{type:"sources"}`. The pipe
passes the engine each request's connection beside the request, so no request field
can claim another connection's previews.

Commands are `snapshot`, `preview` (source, destination), `preview_detail`
(preview_id, destination), `add` (preview_id, destination, paused, optional
priorities), `cancel_preview` (preview_id), `merge_trackers` (preview_id,
torrent_id), `torrent` (torrent_id), `pause`, `resume`, `force`, `verify`, and
`remove` (torrent_ids), `queue` (torrent_ids with direction: up/down/top/bottom,
or before_torrent_id: string/null for a row drop; null means end),
`session_pause` (paused), `settings` (changes), `open`, `ready`,
`ui_closed`, `activate_reply` (available boolean), `close_reply` (state:
waiting/closing/cancelled), and `exit`. Activation acknowledgement lets Open wait through an
old window's close path without losing the request. Current settings changes accept language (`en`, `es`)
and theme (`system`, `light`, `dark`); unknown fields or values are refused. A
settings acknowledgement confirms the same durable replacement as membership.
Settings also accept `default_destination` (absolute path), `show_add` and
`alternative_limits` (booleans), and `download_limit`, `upload_limit`,
`alternative_download_limit`, `alternative_upload_limit` (bytes per second,
integer 0 through INT_MAX; 0 means unlimited). Alternative limits initially use
10 KiB/s in each direction. Settings also accept `notifications_enabled`,
`prevent_sleep` and `prevent_sleep_seeding` (booleans).
Session pause is persisted as `all_paused` through
its command and preserves individual torrent intent. The desktop host records
`background_notice_shown` through its own engine call; the settings command
refuses it.
`registration` takes an `operation` string: `observe`, `register_handlers`,
`unregister_handlers`, `enable_startup`, `disable_startup`, `open_defaults`, or
`open_startup`. Its data contains `handlers_registered`, `startup_enabled`,
`startup_target`, `torrent_default` and `magnet_default`; an unavailable default
query is null. A partial failure includes the observed data with the refusal.
The same operations are available through `Engine.exe --registration OPERATION`
without starting transfers or WinUI.
Snapshot settings contain the live language, with `language_saved` indicating
whether it matches the saved preference. The store retains only saved settings;
a failed save leaves the live choice selected and reports the failure.
Preview replies contain preview_id, name, size, files (index, path, size,
priority, padding), metadata_ready, hashes (full v1/v2 hexadecimal strings),
trackers (URLs), merge_available, error, shared_with (torrent names), and
duplicate torrent identity when present. Add returns torrent_id after storage
commit with duplicate:false, or the existing torrent_id with duplicate:true.
File preview parses
metadata without creating a payload handle; destination is applied at Add.
Another instance of the storage worker reads and parses preview sources, so a
slow share cannot hold up metadata commits; destruction cancels its blocked read.
Snapshot contains session_id, torrents, settings, language_saved, download_rate,
upload_rate, all_paused, has_incoming, stopping, loading, storage_failed, and startup_error.
Torrent rows contain torrent_id, name,
size (bytes), progress (0..1), status (stable code), paused, download_rate and
upload_rate (bytes/second), save_path, error (stable code), diagnostic detail,
added (Unix seconds), seeds, peers, downloaded/uploaded (bytes), queue
(libtorrent position), complete, incoming, forced, and hashes. `torrent` returns
the torrent's facts, name, metadata_ready, files, hashes, current content folder,
and magnet link.
All identities are strings. Settings are intended changes rather than replacement
snapshots. Input sources are bounded to 32 KiB and retained previews/parses to
256. `activate_sources` forwards sources, preserving relative-path meaning at
the launching process. `pending_sources` returns activations with activation_id
and sources; `sources_received` acknowledges activation_ids after the UI owns
them. The engine retains each accepted batch until that acknowledgement. A
window that has begun closing takes no more sources, so they stay with the
engine.

Persistence uses format 1 settings.json with authoritative membership and user
intent, plus per-identity libtorrent resume files. The ordered writer atomically
renames files using Windows replacement; a reply acknowledges saved membership
only after the replacement succeeds. Checkpoints run every 30 seconds, retaining
failed-save work for retry. This bounds crash loss of transfer progress without
turning a transfer tick into a disk write.

## WinUI review corrections

The requested whole-app source review found five actionable failures: F2 missed
the focused TreeViewItem's priority control; tracker editing left its covered
table in keyboard navigation; feedback at the minimum window size could clip the
inspector; Add cancellation could erase a newly acknowledged activation; and a
second UI launch swallowed Open refusals and transport errors. The corrections
use the existing row container, visibility and size owners, release only the
cancelled batch before awaiting preview cancellation, and share greeting/outcome
parsing between ordinary and forwarding connections. A fresh adversarial source
review found no additional issue in that six-file diff. Compilation exposed a
WinUI generated-binding error in implicit bool-function-to-Visibility conversion;
using the existing Visibility-returning function fixed it. The isolated Release
app build passed. These source and compiler checks do not establish runtime
focus, forwarding failure presentation, or corrected minimum-window geometry.

Five roles considered forwarding failure in sequence. The Windows engineer
keeps one workspace. The everyday user requires visible failure instead of a
launch that silently disappears. The accessibility reviewer chooses the native
error dialog's keyboard and system rendering. The maintainer keeps one wire
outcome parser. The product owner chooses the small native bootstrap dialog,
because no WinUI workspace exists in the forwarding process and the engine must
keep running independently.

The running-app reviewer observed Preferences at 1026 by 673 logical pixels:
Transfers began around x272 and General around x289, wasting the left quarter
and clipping the General Browse action at the right edge. Evidence is in
`artifacts/evidence/RunningUiReview-20261004-01`. Add's single-torrent preview and
invalid-source state were also captured. Desktop activity from another review
prevented dependable input coverage; populated tables, Inspector, other sizes,
themes, languages and recovery states were not established. A separate runtime
review corroborated Settings allocation in issue 78 and reported invalid-magnet
recovery in 79 and ambiguous all-day schedule text in 80. Its caption and footer
comments on issues 41 and 48, and theme comment on 24, remain advisory and must be
checked against the owner's explicit caption requirements. Ordinary horizontal
scroll clipping is not a separator or a TableView padding defect.

The owner then stopped shared-desktop automation. That restriction applies to
review agents as well as the orchestrator: subsequent work uses source and
isolated compilation, with no launch, capture, resize or input on the desktop.

Four roles considered the requested in-app capture path, in sequence. The
reviewer uses the automation tree for most facts and pixels for actual visual
questions. The Windows engineer chooses RenderTargetBitmap for owned XAML and
separately rendered popup children, with Microsoft's documented capture limits.
The everyday user requires an opt-in action that neither activates nor takes
over the screen. The product owner keeps it a review diagnostic with no menu or
automatic capture. Setting TINYTORRENT_CAPTURE_DIRECTORY to an absolute directory
enables Ctrl+Shift+F12; PNGs and a JSON manifest record scene dimensions and elapsed
time. Native chrome, system dialogs and desktop acrylic remain outside that
capture's scope. Runtime capture correctness and speed remain unmeasured while
desktop interaction is stopped.

Four roles considered the NavigationView ruling in sequence. The everyday user
wants visible, familiar destinations instead of a logo that becomes a menu. The
Fluent designer chooses the native 48-pixel compact rail: it keeps destination
icons visible without the extra header height of the minimal pane. The keyboard
reviewer keeps native composite focus
and restores selection when Keep editing refuses navigation. The product owner
keeps Torrents primary, Settings and About secondary, with the existing caption
actions, single acrylic surface and shared commands. The owner's latest ruling
supersedes the approved prototype's application-menu gesture.

Four roles considered restoring the schedule in sequence. The everyday user
needs the saved week before its definitions, even while scheduling is off. The
Fluent designer uses a bounded native card, hour ruler and Grid star columns
weighted by minutes, rather than text summaries or fixed pixel widths. The
keyboard user gets native Buttons, full day/time/mode names and the existing
period commands; translation preserves the focused timeline control. The
product owner retains all saved definitions and existing Add, Edit, Remove and
speed-limit owners, without sample periods or new engine semantics. Normal gaps
remain read-only. The view now includes the legend, period hierarchy and actions;
all-day descriptions use one formatter. Appearance now offers the native system,
light and dark choices through the existing theme owner.

Four roles considered the advisory layout and recovery findings in sequence.
The desktop user needs visible actions within the Settings viewport. The Fluent
designer anchors all categories to the same bounded left edge and groups limits
with rates, separately from errors. The keyboard user keeps rejected magnet text
editable, with local feedback and focus, rather than disturbing the valid preview.
The product owner collapses the empty Add preview allocation and empty status
messages, retaining populated preview bounds and the requested caption commands.
These correct issues 78, 79 and 83 and the actionable part of 48 in source; 24
and 80 are covered by the theme selector and all-day formatter. Issue 85's old
application menu is removed by NavigationView. Issue 84's unsupported engine
settings remain unfinished integration work, not evidence of implemented choices.
No new runtime verification has been performed under the desktop restriction.

The settled adversarial source review found that a URI-only magnet guard still
accepted malformed torrent identities, clearing their editable input before the
engine refused them. The fix asks the existing engine preview owner first, then
publishes the accepted source; failures retain the input and the valid preview.
Preview acquisition and same-content merging stay shared. Four roles chose this
in sequence: the everyday user keeps recoverable input; the libtorrent engineer
keeps one validity authority; the desktop engineer keeps pending controls and
cancelled previews scoped to their draft; the product owner avoids a second
magnet parser. The reviewer also removed two obsolete application-menu clauses
and caught the focus correction checking the ScrollViewer wrapper instead of
the existing AddForm reference. That path now uses the form reference.

The final scoped source review has no remaining concrete findings. The isolated
Release app build passed without warnings; English and Spanish preference keys
and format placeholders match. The required Everything folder query stalled and
was stopped; the equivalent filesystem traversal found no stray output folders.
No app or engine was launched, and no suite or desktop interaction was run.
Rendered layout, proportional timeline geometry, contrast, focus restoration,
live language/theme behavior and capture timing remain runtime evidence gaps.

## Background integration after the engine refactor

The owner completed the engine refactor while continuing its polish. A fresh
milestone-3 adversarial source review found two Open failures: the command replied
with success during Exit although Open did nothing; and a disconnected, living
UI process bypassed the readiness timeout after its deadline had been cleared.
Open now returns whether it accepted the request, and that existing process gets
a bounded readiness wait without a second launch.

Four roles chose these corrections in sequence. The everyday user requires a
window or a visible refusal. The Windows engineer keeps one process and one
readiness deadline owner. The heavy seeder preserves coordinated Exit instead of
reviving the window during final saving. The product owner uses the existing Open
operation and stopping outcome rather than retaining another activation queue.

Source inspection also confirmed advisory issue 38: waiting more than 30 seconds
at an unfinished-input prompt falsely reports an unresponsive UI. Four roles
answered in sequence. The everyday user needs time to decide. The accessibility
reviewer needs time to read the prompt without a second failure surface. The
Windows engineer distinguishes the acknowledged human wait from unacknowledged
startup or unfinished closure. The product owner reuses close_reply with three
explicit states and adds no heartbeat. Waiting suspends that deadline; continuing
closure restores it; Cancel or a failed preparation cancels Exit. Overlap with an
already open Close prompt sends the same waiting acknowledgement.

Current runtime evidence still predates the refactor. These corrections are not
milestone completion evidence until their current integration is checked. Shared
desktop interaction remains stopped; no current engine or UI has been launched.

The second review caught a retained Open during ordinary window teardown: a
clean process exit left its readiness deadline set, blocking the replacement.
That clean exit now clears the deadline only for the retained Open, which then
uses the existing launch owner. The focused re-review has no remaining finding.
The isolated Release engine and app builds passed. After the final native build,
Everything reported that its IPC server was unavailable; the equivalent folder
traversal found no generated folders outside artifacts and 3rdParty.

## Details integration while desktop checks are deferred

Four roles considered sequencing in order. The desktop owner needs the screen
left alone. The implementation engineer can finish command support without
opening a window. The reviewer keeps compilation and source review distinct from
runtime evidence. The product owner chooses useful integration work over an
idle wait. Milestone 3's source corrections are settled; its current runtime gate
and completion commit remain deferred. Preparing milestone 4 does not declare
milestone 3 complete or replace its missing evidence.

Four roles considered schedule overrides in order. The everyday user expects
Resume Transfers and the alternative-limits switch to act when used. The heavy
seeder expects the next scheduled boundary to restore automatic policy. The
libtorrent engineer keeps one session pause and rate owner, separate from
individual torrent intent. The product owner chooses temporary manual overrides
for the current schedule mode, cleared at the next mode change. Manual Pause all
always wins; an absent selected adapter cannot be bypassed. Saved periods and
normal/alternative rates remain unchanged by these temporary overrides.

Four roles considered seeding limits in order. The everyday user expects a
reached ratio or seeding-time limit to pause, never remove files. The heavy seeder
expects explicit Resume to continue that seed. The libtorrent engineer notes
that upstream seed limits demote queue priority rather than establish this stop
policy. The product owner chooses the existing durable Pause intent and one saved
per-torrent exemption for explicit Resume/Force of completed content. The ratio
uses uploaded bytes divided by the greater of downloaded bytes and verified
bytes, so existing seeds have a meaningful denominator. Zero disables either
limit; time counts actual seeding, not time spent paused. Editing the global
limits does not silently revoke a seed's explicit exemption.

Settings now persist the UI's queue, connection, seeding, network, update-check
and weekly-period choices through the existing document queue. Periods carry
Monday-zero days, start/end minutes and paused/alternative mode, with at most
128 definitions. Zero queue/connection limits mean unlimited. The summary's
top-level all_paused and alternative_limits describe effective policy; the
settings object retains manual choices. missing_interface identifies an absent
selected adapter. The existing UI status and rate-pair toggle use these effective
facts; tray status stays at two rows and three commands, with the adapter reason
in its tooltip. Selection of an unavailable adapter is retained and blocks the
session rather than falling back. Availability is reconciled once a second;
changed binding pauses old connections before the new settings apply.

The independent adversarial source review found three defects. Saved adapter
policy arrived after libtorrent started its default unrestricted listeners and
port discovery; construction now starts with no listeners or port mapping before
the sole policy owner applies saved choices. The seeding-time check used the
all-files counter; it now uses finished_duration for completed selected content,
which also excludes paused time. Saved midnight-to-midnight period definitions
and their accessible action names lacked All day; they now use the shared
catalogue description, while non-midnight overnight spans keep explicit times.
The re-review found no remaining concrete issue in the corrected paths.

The first combined native build exposed three metadata accessors omitted by the
selected ABI. General now reads the actual add_torrent_params annotations; the
Inspector integration section records their existing resume-data owner. The
corrected isolated Release engine and app builds both passed without warnings.
English/Spanish keys and placeholders passed. No application was launched.
The mandatory Everything query reported no available IPC server; a filesystem
traversal found no stray bin, obj, bin-fl or TestResults folders outside the two
declared output/dependency roots.

Two focused scenarios are prepared in engine/tests/Checks.ps1, with an optional
EnginePath for the isolated candidate. SettingsPolicy watches saved periods and
preferences, manual overrides preserving individual pause intent, and refusing
to resume through an absent adapter. CommittedFiles watches older checkpoints
defeating saved file choices, Select none removing membership, tracker tiers
being lost, or an explicit empty tracker choice restoring original trackers.
The existing checks cover none of these new state transitions. Both scenarios
retain the fixture's ownership check before sending any command. Only PowerShell
syntax was checked; neither scenario was executed under the desktop restriction.

Remaining milestone-4 evidence includes actual transfer outcomes under adapter
switching and limits, failed edits and restart, background-history use, and the
live inspector/language/RTL journey. The schedule's new native presentation
also still needs its rendered review. No milestone completion is claimed by
these source and compilation checks, and Move and delete files remains next.

## Inspector engine integration

`torrent` accepts `view`: `general`, `files`, `peers`, `trackers`, or `pieces`.
Each reply identifies `session_id` and `torrent_id` and collects only that view.
The older request without `view` retains its General/Files shape, including URL
strings in its saved `trackers` field, for existing action and check consumers.

General supplies `folder`, `magnet`, `hashes`, metadata `comment`, `creator`,
`created` (Unix seconds), `piece_size` (bytes), and `private` (null until metadata).
Files supplies `metadata_ready` and indexed `files`, with relative `path`, byte
`size`, `padding`, effective `priority`, and actual byte `downloaded` values.
Peers supplies `peers`: endpoint, client, transport, incoming/encrypted facts,
progress from 0 to 1, payload `download_rate`/`upload_rate` in bytes per second,
and payload `downloaded`/`uploaded` byte counters.

Trackers supplies URL/tier rows with status, scrape `seeds`, `leechers`, and
`downloaded` counts (-1 when unknown), `next_announce` in Unix seconds (0 when
unscheduled), and the raw tracker message/error. Four roles considered combining
libtorrent's endpoint and v1/v2 state in order. The everyday user needs a working
tracker to stay working when another route fails. The network operator needs
actual failures retained as diagnostic data. The libtorrent reviewer avoids
adding duplicate scrape counts for several announces to one swarm. The product
owner keeps the existing one-row-per-URL view: announcing takes precedence,
then any working route, then error, waiting, or disabled when no route is usable.
Scrape counts use the greatest reported value; the next time is the earliest
usable route's time, respecting its minimum announce interval.

Pieces supplies `metadata_ready`, `piece_size`, connected `peers`, a complete
`verified` bit list, corresponding `availability` counts, and indexed
`downloading` fractions from libtorrent's outstanding block data. `include_files`
adds relative paths with zero-based `first_piece` and exclusive `end_piece` only
when requested. Routine summary updates still omit piece bitfields.

`edit` carries `torrent_id` and intended `changes`: indexed `priorities` entries
(`index`, `priority`) and/or the complete intended `trackers` list (`url`, `tier`).
The canonical priorities remain 0/1/4/7; metadata and indexes are validated when
the queued edit executes, and the existing priority owner keeps padding at zero.
Four roles considered Select none in order. The everyday user expects the Files
command to work. The seeder keeps existing downloaded content and membership.
The libtorrent engineer supports an all-zero wanted selection without deletion.
The product owner keeps the one-wanted requirement at Add, where it gives the
new download useful work, and permits Select none in committed file edits.

Edits save choices through the existing document queue before applying them.
Priorities are built from those saved choices, so an earlier disk operation
cannot overwrite a later field choice. A priority reply completes only after
the effective vector matches; disk failure reports failure and Files still
shows actual progress/priorities. Saved intent remains available for retry.
Dropped alerts reconcile against the effective vector or report recovery
required. Exit drains accepted priority work before its final checkpoint, with
the existing 30-second bound rather than waiting forever for a missing outcome.
Restart supplies document priorities before addition and clears older resume
piece priorities, so an earlier checkpoint cannot defeat a committed selection.

Tracker URLs use libtorrent's validation for HTTP, HTTPS, and UDP; duplicate
URLs keep their first tier, and tiers are 0 through 255. Saved tracker choices
retain URL and tier. A missing choice retains resume defaults; an explicit empty
choice remains empty after restart. Existing saved URL strings read as tier 0.
`reannounce` calls libtorrent's existing operation and respects its interval and
paused-state rules.

`history` accepts `range`: `five_minutes` or `day`, returning `session_id` and
`samples` with Unix `time` and payload download/upload rates in bytes per second.
The state owner's existing one-second maintenance records all accepted torrents
while the window is closed. It retains at most 300 second samples and 1,440
averaged minute samples, including the current minute. Gaps discard an unfinished
minute rather than inventing its missing samples; a backward clock change starts
a new chronology. Nothing is saved, so engine restart starts empty.

Pinned libtorrent headers and their implementation supplied the field and
completion evidence. No application was launched and no test was executed for
this integration. The combined target build and adversarial review remain the
next checks; desktop behavior and real transfers remain deferred evidence.

The first combined native compile found that ABI 4 removes the old
`torrent_info` comment, creator, and creation-date accessors. Four roles chose
the correction in order. The everyday user needs the original torrent-file
annotations after restart. The libtorrent engineer identifies
`add_torrent_params` as their supported parser/resume representation; BEP9
magnet metadata contains only the info dictionary. The maintainer keeps one saved
representation in resume data, without adding application settings or a parser.
The product owner initializes the accepted torrent's annotation facts from its
initial/restored params. The existing resume writer retains those facts in every
checkpoint, including annotations learned when a guarded magnet preview gained
a torrent file that its existing handle received only as an info dictionary.
General reads those facts. Dependency ABI and headers stay unchanged; the
corrected target still requires the coordinated compile and review.

### File-operation decisions

Four interested roles considered partially overlapping cross-seeds in series.
The heavy seeder needs all shared torrents moved together, including torrents
that also have distinct files. The libtorrent engineer uses its asynchronous
move and verification operations instead of copying payload on the engine loop.
The Windows engineer checks destination collisions and retains path holds until
disk completion, including deletion after membership has gone. The maintainer
keeps one active payload operation and reuses the existing worker implementation
for selective deletion, so slow payload work does not block metadata commits.
Common sense selects a union destination preflight and sequential libtorrent
moves, retaining group files already moved and verifying all members. A group
with two different source files mapping to one destination is refused.

Four roles then considered unknown magnet paths. The everyday user prefers a
clear retryable Files busy or Metadata unavailable message to damaged downloads.
The seeder cannot approve deleting files whose other owners are still unknown.
The libtorrent engineer cannot learn a magnet's file paths before metadata.
The product owner avoids a speculative ownership system: one active operation
temporarily refuses new confirmations and waits for unresolved outside metadata.
These choices protect real files without altering ordinary shared-file addition.

### Move and delete integration while desktop checks remain deferred

The native engine now implements `file_scope`, `move`, and `delete_files` through
the existing durable command owner. Remove retains files. The WinUI selection
menu, row menu, and command search expose the new operations; Shift+Delete opens
the explicit permanent-delete confirmation with Cancel as default. Move shows
current and resulting content folders, can include the outside shared group, and
offers explicit Use files there with its verification warning. Native controls,
the existing folder picker owner, and the existing MVVM command paths are reused.
No TableView source changed. Moving hides download progress instead of inventing
a file-copy percentage; libtorrent does not supply that percentage.

One payload operation reserves its source and destination before an asynchronous
commit. A separate instance of the existing storage worker performs destination
preflight and selective deletion so payload work cannot block metadata commits.
Deletion first removes membership durably, then libtorrent removes private part
data; only the unshared payload union is deleted. Empty content subfolders are
removed, while the chosen save root and unrelated files stay. Failure remains a
notification and diagnostic even with the main window visible and completion
notifications disabled. A crash does not repeat removed payload work.

Relocation uses pinned libtorrent disk operations, with saved intent preserved.
All group recovery markers remain until every member has a known disk outcome
and the final group document commits. A changed or explicitly reused destination
also keeps a saved verification requirement until a safe destination checkpoint
commits. Startup invalidates old checkpoint piece claims before adding the
torrent when that verification is still required, including same-path recovery.
Unknown dropped-alert outcomes retain their path holds and report recovery;
Exit can report that uncertainty rather than silently replaying work.

The fresh Astra file-safety review found four concrete defects and a recovery
follow-up, all corrected in source:

- An Add could start between acceptance and the marker/membership commit. Path
  reservation now precedes that write and is released if it fails.
- A failed destination preflight left a held collision destination eligible for
  deletion. A known no-op preflight clears its new marker through the writer;
  deletion refuses an unresolved move. Held paths do not establish ownership.
- Clearing markers per member let a later libtorrent group rollback leave an
  earlier member resumable in the wrong folder. Only the final group commit
  clears them. The pinned `dont_replace` rollback supplied the failing scenario.
- A stale complete checkpoint could trust same-sized wrong destination bytes
  after a crash. Saved verification invalidates old claims on startup, and only
  a durably written, currently checked destination checkpoint clears it.
- An ordinary retry could overwrite or clear an older interrupted marker.
  Recovery now requires explicit Use files there before a new move choice.

The separate fresh Astra dialog review found four concrete problems, all fixed:
missing actionable recovery text, a false unresponsive warning while Exit waits
for the native folder picker, protocol JSON displayed on changed shared scope,
and source-sharing guidance incorrectly used for destination sharing. English
and Spanish name the recovery steps; human picker waiting sends the typed close
acknowledgement; shared scope remains a readable list; destination use names its
outside torrent and asks for another folder. Both reviewers' scoped rereviews
returned no remaining concrete source finding.

The final isolated Release engine and WinUI builds passed after those fixes.
The matching engine executable is copied beside the isolated WinUI output at
`artifacts/checks/ui-review-build/TinyTorrent.exe`; its SHA-256 matches the native
build. This pair is ready for later review without replacing a running copy.
An existing engine must first exit through its normal command, since the same
logon endpoint intentionally never starts a second engine. The pair was not run.
The compiled native target uses the owner's current refactor and pinned
`3rdParty` binaries. The EN/ES catalogues have matching keys and placeholders,
`git diff --check` is clean, and the expanded PowerShell check parses. No product,
engine, transfer host, native picker, or desktop interaction was launched. The
Everything query returned IPC-not-found; an equivalent filesystem walk excluding
`artifacts`, `3rdParty`, and reparse points found no stray output folders.

The prepared `FilesSafety` check earns its place by watching destructive outcomes
existing Remove-keeping-files coverage cannot catch: deletion of an outside
shared file, replacing a collision file, deletion reaching that collision after
a no-op move, an incomplete cross-seeded group, and replay or destruction from
an unresolved saved move marker. Its tiny private torrents share one payload
but have distinct info hashes. It also checks unrelated content stays and saved
removal survives restart. This is source-prepared evidence only: it was not run.
Its injected marker is not a measured crash during disk work. Real crash tests,
partial-overlap failure, cross-volume moves, dropped alerts, destination recovery,
native dialogs, keyboard/focus, language switching, contrast, and text scaling
remain runtime gaps. The earlier Background and Details runtime gates also stay
open; none of these source reviews substitutes for them or marks a milestone
complete. Distribution remains excluded.

### Recovery feedback distinctions

The completion audit found that the file dialog correction had made the shared
`recovery_required` text specific to Move files. Dropped-alert priority edits
and additions also return that code, so their failures wrongly suggested moving
payload. An active move with an unknown outcome also disables Move files, making
the same instruction unavailable until the engine exits and reopens.

Five roles considered the correction in series. The everyday user needs the
message to describe the failed operation, without suggesting unrelated file work.
The keyboard user cannot follow a disabled command and needs the available Exit
path first. The libtorrent engineer keeps unknown disk outcomes and their path
holds intact; clearer feedback must not release or replay the operation. The
maintainer uses the existing Move interrupted code for a saved interrupted marker
and one distinct typed problem for an active uncertain move. The product owner
chooses those distinctions over a recovery wizard or another state owner.

The general code now asks the person to check current torrent state before
retrying. A saved interrupted move returns `move_interrupted` for an ordinary
retry or deletion, retaining its existing explicit folder-recovery instructions.
An active uncertain move carries `move_uncertain`, whose EN/ES text explains Exit,
reopening, and explicit Exit anyway when the unresolved operation prevents normal
shutdown. No disk, pause, checkpoint, or recovery transition changed.
FilesSafety's two refusal assertions name the specific interrupted-move code;
their destructive outcomes and expected refusal remain unchanged. No text test
was added. The focused scenarios and actual recovery UI remain unexecuted.

The affected native Release build passed, and the isolated WinUI build was
refreshed so its embedded messages match the engine codes. The copied engine's
hash matches the native output. Both projects' EN/ES catalogues parse, have
unique and matching keys, and retain matching placeholders. Checks.ps1 parses;
no scenario ran. The prescribed Everything query again reported unavailable
IPC, while the fallback traversal found no stray output folders. No application
was launched. This routing correction has a local source review; the milestone's
final adversarial and runtime gates remain open.

## Advisory follow-up on 2026-10-05

The supplied engine and desktop review reports were compared with current source
and GitHub issue bodies and closing comments. Issues 49, 59, 64, 67–77, 81 and
82 are closed: sixteen issues, with source fixes present. Their closing comments
describe compilation, not executed engine checks. No issue was changed by this
triage. A default-output link failure is not a successful build; the subsequent
isolated engine and WinUI builds recorded above did link successfully.

The highest-priority remaining behavior is completion readiness. The pinned
libtorrent 2.1.2 implementation posts `torrent_finished_alert` before queuing
the disk release that later posts `cache_flushed_alert`. TinyTorrent's finished
handler immediately sends the completion notice, while `Torrent::Classify` and
the row's `complete` field also accept libtorrent's finished state. There is no
cache-flushed handler. Delaying only the balloon would therefore leave the row
and callers observing premature completion. The existing SelectedTransfer check
hashes the wanted payload immediately after reported completion and should keep
that assertion. The supplied three-of-five failures and 0.5–1.8 second delays
were not independently reproduced here; source ordering supports investigating
the failure, not claiming those measurements as this pass's evidence. A flush
alert can also result from manual flushing or removal, so it cannot unconditionally
declare a newly completed download.

Five interested parties considered the remaining work in series. The everyday
user wants Completed and the completion notice to mean the file is ready to use.
The libtorrent engineer requires the disk acknowledgement to correspond to the
completed payload, and rejects a sleep or weaker hash assertion. The Windows
designer preserves the requested LabForms caption styling and live language/theme
controls while separating transfer commands from application commands. The
maintainer treats the three residual structural issues as separate work rather
than expanding a completion fix into another engine rewrite. The product owner
prioritizes the observable completion defect, then caption composition, then
focused verification of source fixes already present. That order avoids paying
again for code already corrected without excusing a real readiness failure.

Issue 41 still has a concrete source concern: the caption's uninterrupted action
row includes transfer commands, language, theme and Exit. Keep the owner's
LabForms appearance requirement; do not restore the superseded application menu
or drop the live preference controls. Correct the grouping in that existing
surface. Other advisory UI findings have source corrections: shared settings
allocation (78), preference transport and engine support (84), footer proximity
and empty feedback (48), invalid-magnet preservation (79), empty Add allocation
(83), All day (80), and Follow Windows (24). NavigationView supersedes the menu
reported in 85. Their current rendered and interaction evidence still matters;
an open issue alone does not establish an unfixed source defect.

Issues 16, 17 and 61 remain open with narrowed structural scope. The desktop
lifecycle still uses interacting flags; Engine.h still hosts low-level shared
helpers; three desktop operations still enter the engine through wire JSON.
No new failing lifecycle scenario was established by this triage. These deserve
separate, bounded changes when their ownership benefit justifies them, rather
than blocking the functional work solely because the issues remain open.

The owner then authorized computer use to capture images. Neither a product nor
an engine was running at the new check. The matching isolated candidate was
launched with a disposable store under
`artifacts/evidence/advisory-triage-20261005/store`. Its empty main window was
captured at 1026 × 673, with a TableView header, centered empty state, native
NavigationView, related footer rates/limits, and Exit beside language/theme.
No populated transfer, settings page, contrast, or scaling result follows from
that capture. Settings navigation failed because the computer-use helper twice
reported a PickerHost window over its target point, including after activation;
input attempts stopped. This is an automation limitation, not proof that Settings
fails. Normal Exit was sent only after verifying the pipe server PID matched the
review-owned engine. Both candidate processes closed. No personal store was used,
no source fix or new test was made, and no GitHub issue was closed here.

The prescribed Everything query returned IPC-not-found. The fallback traversal,
excluding artifacts, compiled dependencies and reparse points, found no stray
output folders. The current root instructions do not prohibit that fallback;
its result remains distinct from a successful Everything query. Diff whitespace
checks passed. The earlier desktop restriction is relaxed for image capture;
destructive tests and the remaining milestone journeys were not run in this pass.

## Background completion corrections on 2026-10-05

The subsequent source audit reopened issues 49 and 67, so the preceding issue
closure count records the earlier observation, not their current disposition.
The other engine pass added disk-flush acknowledgement for downloaded content;
this pass retained that work and corrected recovery when the finish alert itself
is dropped. Effective finished state reconstructs the pending completion, and
an explicit flush obtains a new acknowledgement. Rechecks retain their existing
no-download-notice behavior. The row and notice share disk readiness.

Five interested parties considered the remaining choices in series. The everyday
user needs Completed to mean a usable file and Cancel Exit to keep working. The
libtorrent engineer requires acknowledgement of disk work and retention of
resume data whose generation already cleared dirty flags. The Windows engineer
distinguishes a broken pipe from a closed process and keeps the bounded readiness
wait. The keyboard user needs ordinary Wait/Cancel behavior without destructive
Escape defaults. The maintainer prefers the existing state, storage and splash
owners over a scheduler, counters or a second dialog framework. These answers
select one checkpoint write per torrent with its newest pending result retained,
the existing splash for Wait/Cancel during file work, and retained process
coordination after a disconnect during Exit. Headless and Windows session-end
shutdown keep their noninteractive behavior.

The caption's Exit command moved to a nonselecting NavigationView footer item.
Its pointer gesture and Ctrl+Q use the existing MVVM command, including task,
picker and connection guards. Language and theme retain native caption styling,
with spacing separating those preferences from transfer and window commands.
No TableView API or source changed.

A fresh Astra reviewer found four concrete issues: lost finished alerts bypassed
disk readiness; Exit treated a disconnected living UI as closed; file work had
no Wait/Cancel choice; and the interface still described caption Exit. All four
were corrected. Its scoped re-review also examined checkpoint coalescing and
reported no remaining source finding. Runtime evidence remains a separate gate.

The isolated WinUI Release build passed with zero warnings/errors. Its initial
attempt used a stale TableView reference assembly and failed on CanReorder;
ProduceReferenceAssembly=false selected the matching existing output assembly
without rebuilding the library. The settled native Release build passed in
57.86 seconds with zero warnings/errors. Enums.h and engine headers changed, so
their native consumers recompiled; only the existing engine target ran and no
dependency compiled. The earlier checkpoint build had compiled fourteen header
consumers in 81.60 seconds. Build logs are under artifacts/checks/engine-review
and artifacts/checks/ui-review-build. The adjacent candidate engine matches the
new native output, SHA-256
68AA3BDFE6FE57566D3630ED2F9538ED4CDC1F688F482E15B82B4495BDE23FE3.

CheckpointRetry passed against that binary in
artifacts/evidence/CheckpointRetry-a11cf6af-78d0-4745-bbd1-aae4e5539e5d:
the forced failed checkpoint became visible, recovered after the obstruction
was removed, and preserved identity and running intent after restart. This
checks retained failed-save recovery; it does not inject two overlapping alerts.
Serialization of those writes is established by the reviewed owner sequence.

## Functional review and user smoke test — 2026-10-05

The owner requires functionality and common sense to be checked before visual
polish, with sequential human-user roleplay as an additional smoke test. Native
WinUI principles prevail over persona preferences. The question is which
friction deserves correction before polishing the adopted pages.

New user: adding a torrent should start with the file picker; the preview must
explain destination and files. Invalid magnet text must remain editable beside
its error. A schedule needs real periods along a time axis, and opening an edit
must reveal it immediately.

Frequent downloader: apply speed limits together, keep rejected text for
correction, and show whether a change succeeded. Quietly replacing an invalid
entry with an old value makes the outcome impossible to trust.

Heavy seeder: separate session pause from individual choices, keep queued and
active status understandable, and identify session-wide speed history. Saved
schedule periods must remain visible while the schedule is off.

Keyboard user: focus should follow the task into an opened editor and return
to its invoker. Native navigation and dialogs should retain their keyboard
behavior. Dense captions should not turn every command into an equal-looking
group, and hidden controls must not become extra stops.

User managing shared files: removal and deleting files must remain distinct;
show scope before destructive work. If the torrent being edited disappears,
explain why Save is unavailable while preserving the unfinished text and Cancel.

WinUI designer: use native controls, stable content allocation, readable
hierarchy and semantic theme brushes. Keep the schedule's time geometry rather
than substituting text summaries. Persona requests for extra controls do not
override native behavior or justify expanding this small product.

Decision: fix silent invalid-input replacement and unavailable-target feedback
at their existing MVVM owners, and retain the real timeline. The period editor
brings its heading into view; day choices wrap before labels overlap. These
changes remove concrete task failures before margins or colors are adjusted.
The roleplay is a smoke test of the journeys, not proof that users were studied.

A fresh Sol functionality audit covered Main, Add, all five Preferences sections,
all six inspector sections, Limits, Remove, Move and Delete. It found the two
input/target failures above. Both are corrected in SpeedLimits and Inspector;
runtime confirmation is recorded separately. It found existing commands and
cancellation coherent, with no automatic destructive retry. No TableView source
or API changed.

The review capture runs the actual views in an off-desktop, nonactivating window
against a named disposable store. It saves pixels and control bounds and closes
itself. It never uses desktop input. Native chrome and desktop acrylic remain
outside RenderTargetBitmap evidence.

## Handover checkpoint — 2026-10-05

The owner asked to finish the pending task and stop for another project. The
unattended recovery smoke passed in 12,156 ms, retaining rejected speed-limit and
magnet text and an unavailable torrent's inspector draft with Save disabled and
Cancel enabled. Evidence is
artifacts/evidence/UiSelfCapture-e494957a-cdea-4359-b498-7bdd65569b6a/captures/review.json.
Its thirteen saved scenes include all Settings sections and the corrected Pieces
legend. Earlier failed limit-input runs are superseded by this specific passing
journey, not by a widened assertion or timeout.

The integrated app Release/x64 build passed with zero warnings/errors in 112.55
seconds. It includes the coordinated PipeClient deadline/admission corrections;
their connection-fault behavior still needs focused runtime evidence. Only the
app compiled, using existing TableView/dependency outputs. No full suite or new
adversarial review was run during this final checkpoint.

The prescribed Everything stray-folder check returned exit 0 and no paths after
the smoke run; a separate traversal also found no stray output folders. All
launched processes closed, and the final process check found no app or engine.
Milestones 3–5 remain incomplete and uncommitted. Resume from
[handover.md](handover.md), which records the pending closing/source-admission
question, completion gates and the cheapest next evidence.
