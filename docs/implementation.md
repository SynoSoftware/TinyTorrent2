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

## Wire representation

Protocol version 1 uses a four-byte little-endian UTF-8 JSON frame length,
bounded to 16 MiB. The endpoint is `TinyTorrent.<logon SID>`; each connecting
client first receives `{type:"hello",version:1,session_id:"..."}`. Requests are
`{request_id:integer,command:string,...}`. Replies repeat `request_id` and have
`ok:boolean`, either `data` or `error:{code:string,detail:string}`. Native control
notifications are `{type:"activate"}` and `{type:"close"}`. A request's
`connection_id` is set by the engine pipe adapter, never trusted from the wire.

First-screen commands are `snapshot`, `preview` (source, destination), `add`
(preview_id, destination, paused), `cancel_preview` (preview_id), `pause`
(torrent_ids), `resume` (torrent_ids), `settings` (changes), `open`, `ready`,
`ui_closed`, `close_reply` (cancelled boolean), and `exit`. Current settings changes accept language (`en`, `es`)
and theme (`system`, `light`, `dark`); unknown fields or values are refused. A
settings acknowledgement confirms the same durable replacement as membership.
Snapshot settings contain the live language, with `language_saved` indicating
whether it matches the saved preference. The store retains only saved settings;
a failed save leaves the live choice selected and reports the failure.
Preview replies contain preview_id, name, size, files (index, path, size), and
duplicate torrent identity when present. Add returns torrent_id after storage
commit, or the existing torrent_id for duplicate content. File preview parses
metadata without creating a payload handle; destination is applied at Add.
Snapshot contains session_id, torrents, settings, language_saved, download_rate,
upload_rate, all_paused, stopping, loading, storage_failed, and startup_error.
Torrent rows contain torrent_id, name,
size (bytes), progress (0..1), status (stable code), paused, download_rate and
upload_rate (bytes/second), save_path, error (stable code), diagnostic detail,
added (Unix seconds), priorities, seeds, peers, downloaded/uploaded (bytes), queue
(libtorrent position), complete, and incoming.
All identities are strings. Settings are intended changes rather than replacement
snapshots. Input sources are bounded to 32 KiB and retained previews/parses to
256. Explorer source batching belongs to Everyday torrent actions.

Persistence uses format 1 settings.json with authoritative membership and user
intent, plus per-identity libtorrent resume files. The ordered writer atomically
renames files using Windows replacement; a reply acknowledges saved membership
only after the replacement succeeds. Checkpoints run every 30 seconds, retaining
failed-save work for retry. This bounds crash loss of transfer progress without
turning a transfer tick into a disk write.
