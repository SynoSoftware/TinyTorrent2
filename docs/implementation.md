# Implementation decisions and evidence

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

## Wire representation

Protocol version 1 uses a four-byte little-endian UTF-8 JSON frame length,
bounded to 16 MiB. The endpoint is `TinyTorrent.<logon SID>`; each connecting
client first receives `{type:"hello",version:1,session_id:"..."}`. Requests are
`{request_id:integer,command:string,...}`. Replies repeat `request_id` and have
`ok:boolean`, either `data` or `error:{code:string,detail:string}`. Native control
notifications are `{type:"activate"}`, `{type:"close"}`, and `{type:"sources"}`. A request's
`connection_id` is set by the engine pipe adapter, never trusted from the wire.

Commands are `snapshot`, `preview` (source, destination), `preview_detail`
(preview_id, destination), `add` (preview_id, destination, paused, optional
priorities), `cancel_preview` (preview_id), `merge_trackers` (preview_id,
torrent_id), `torrent` (torrent_id), `pause`, `resume`, `force`, `verify`, and
`remove` (torrent_ids), `queue` (torrent_ids with direction: up/down/top/bottom,
or before_torrent_id: string/null for a row drop; null means end),
`session_pause` (paused), `settings` (changes), `open`, `ready`,
`ui_closed`, `close_reply` (cancelled boolean), and `exit`. Current settings changes accept language (`en`, `es`)
and theme (`system`, `light`, `dark`); unknown fields or values are refused. A
settings acknowledgement confirms the same durable replacement as membership.
Settings also accept `default_destination` (absolute path), `show_add` and
`alternative_limits` (booleans), and `download_limit`, `upload_limit`,
`alternative_download_limit`, `alternative_upload_limit` (bytes per second,
integer 0 through INT_MAX; 0 means unlimited). Alternative limits initially use
10 KiB/s in each direction. Session pause is persisted as `all_paused` through
its command and preserves individual torrent intent.
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
Snapshot contains session_id, torrents, settings, language_saved, download_rate,
upload_rate, all_paused, has_incoming, stopping, loading, storage_failed, and startup_error.
Torrent rows contain torrent_id, name,
size (bytes), progress (0..1), status (stable code), paused, download_rate and
upload_rate (bytes/second), save_path, error (stable code), diagnostic detail,
added (Unix seconds), seeds, peers, downloaded/uploaded (bytes), queue
(libtorrent position), complete, incoming, forced, and hashes. `torrent` returns
the torrent's facts, name, metadata_ready, files, hashes, and magnet link.
All identities are strings. Settings are intended changes rather than replacement
snapshots. Input sources are bounded to 32 KiB and retained previews/parses to
256. `activate_sources` forwards sources, preserving relative-path meaning at
the launching process. `pending_sources` returns activations with activation_id
and sources; `sources_received` acknowledges activation_ids after the UI owns
them. The engine retains each accepted batch until that acknowledgement.

Persistence uses format 1 settings.json with authoritative membership and user
intent, plus per-identity libtorrent resume files. The ordered writer atomically
renames files using Windows replacement; a reply acknowledges saved membership
only after the replacement succeeds. Checkpoints run every 30 seconds, retaining
failed-save work for retry. This bounds crash loss of transfer progress without
turning a transfer tick into a disk write.
