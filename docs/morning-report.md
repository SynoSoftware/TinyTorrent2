# Morning report

## Notification preferences and whole-tree checkpoint — 2026-10-06

The owner requested the whole diff be committed before continuing. `3f6de13`
includes the owner's concurrent shared-control, Add, inspector, table automation,
download-order and build changes, plus the earlier header corrections below.
It is not release approval of every feature in that combined diff.

Issue #130 now has three General notification switches: problems default on,
finished and added default off. Existing saved `notifications_enabled` choices
are preserved. Open-window notices use the existing pipe and persistent error
feedback; completion has a bounded overlay with the existing Open folder action.
It waits during editing and pauses its timeout on hover or focus. Windows notices
use the tray only with the window closed and the appropriate switch enabled.
The tray tooltip includes the error count; its error icon respects the problem
switch. Pending tray bursts retain bounded failure and completion counts when
the window opens. No notification history or competing folder command was added.

Source review found and corrected dropped burst counts and generic errors being
described as stopped torrents. The correction review reports no remaining
finding in that scope. The updated owner rulings are preserved: the new hint is
a tooltip/accessibility description, the action uses a one-word label with its
folder icon and full accessible name, and shared InfoBar messages trim on one
line with full text available to selection and tooltips. Existing Settings help
text elsewhere remains a separate contract mismatch; these accepted cards were
not restyled.

- Engine: `artifacts/notifications-engine-final.log`, 34.80 seconds, zero
  warnings/errors. Its 25 translation units follow the shared Engine.h change;
  no PCH or dependencies were rebuilt.
- App: `artifacts/notifications-app-reviewed.log`, 56.56 seconds, zero
  warnings/errors. Only the app compiled through WinUI's generated-XAML passes;
  Lucide and TableView compilation stayed up to date.
- A later owner FileBrowser edit was included in the staged snapshot. Its app
  compilation passed in `artifacts/whole-tree-checkpoint-build.log`, but the
  reopened window locked the executable copy. The window was closed normally;
  its existing background engine was left running. The retry,
  `artifacts/whole-tree-checkpoint-copy.log`, encountered still newer, unstaged
  FileBrowser edits in progress and failed XAML compilation (`Indent` and
  `HasFolders` bindings). Those later FileBrowser and search edits are preserved
  outside this checkpoint. Do not describe this changing working tree as built
  or reuse its incomplete output as verified. The notification captures above
  and engine checks concern their recorded candidates.
- SettingsPolicy passed in `artifacts/evidence/SettingsPolicy-667eeac4-3bbd-46a0-821d-2e55f5e83710`.
  The added assertions protect notification defaults and saved choices after
  restart; compiler and visual review cannot establish persistence.
- FilesSafety passed in `artifacts/evidence/FilesSafety-dc531f95-0aff-4dfd-a688-0850e12859cc`.
  CheckpointRetry passed in `artifacts/evidence/CheckpointRetry-8df91431-6521-427b-81c3-d2d8616b9a08`.
  Both ran once after the last engine change. No full suite or transfer check ran.
- Before: `artifacts/evidence/UiSelfCapture-ee7bd093-d5b5-495e-9bb8-5ef48537d001`.
  Final: `artifacts/evidence/UiSelfCapture-c79fdb87-183f-42a2-b443-43954f3344cb`.
  All twelve EN/ES, Light/Dark, requested-size cases passed in 27.638 seconds.
  A native switch committed and restored its value; completion feedback hid
  during Search editing, returned afterward and dismissed on timeout.
  Completion was simulated through the production UI handler, not downloaded.
- The review-owned UI and engine exited. Generated-output scans found nothing
  outside artifacts; Everything was available for the final checks.

Remaining evidence: actual Windows balloon delivery/suppression and tray error
icon, a live completed-download notice through the pipe, physical Open folder,
hover/focus timeout suspension, High Contrast and Narrator. Requested720 still
clamps to the existing 837 EN / 863 ES client minimum; this is not exact720 proof.
The independent visual gate reviewed Notifications and completion in all twelve
cases and found zero counted defects. Long torrent names, physical input and
assistive-technology behavior were not established by those images.

The next release work is still grounded in current code rather than open issue
labels. #128 (Mark of the Web), #129 (unfinished `.!tt` names) and #131 (client
identity/bootstrap/announce defaults) remain absent in source and are required
by the engine contract. They are fixed behavior, not missing Settings switches.
GitHub's older architecture issues include superseded implementations; their
open state is not evidence of a live defect. Also retain the current narrow-width,
native title-bar and accessibility gaps and the accepted-screen rule audit.

## Header, magnet editor and feedback correction — 2026-10-06

The table and torrent-properties headers now share WinUI's
`LayerFillColorDefaultBrush` and `DividerStrokeColorDefaultBrush`. The custom
caption contains only Add torrent and Add magnet after Search, followed by a
separator and theme; all three use the same existing caption-button style at
48 by 48, matching the native Tall caption buttons measured through read-only
UI Automation. Torrent selection commands remain in the menu. LabForms' custom
title-bar approach is retained: register the caption and mark only gaps between
controls as draggable, preserving physical-pixel/DIP conversion and native
caption ownership. Native hover and hit-testing behavior still needs a direct
Windows check; XAML captures cannot establish that the reported hover defect is
gone.

The magnet editor wraps one URI in the available pane, with Paste and Preview
centred below using the existing ActionButton and native dialog spacing token.
Its existing preview flow shares the pane with the input. The connection InfoBar
overlays the workspace above the footer instead of consuming a layout row.
The shared native InfoBar template puts its action in a right-hand column;
severity, theme, accessibility and control-state resources remain native.

- Final app build: `artifacts/header-overlay-verified-build.log`, 42.99 seconds,
  zero warnings/errors. Only the managed app compiled; Lucide and TableView
  compilation stayed up to date. No engine build or full suite ran.
- Final evidence:
  `artifacts/evidence/UiSelfCapture-0dd9ab32-e75c-4c90-8657-cefdfb77b216`.
  The focused capture completed all twelve EN/ES, Light/Dark size cases in
  36.411 seconds. Long input stayed intact; invalid input was retained and the
  fixture magnet reached the existing preview. No torrent was submitted.
- Workspace dimensions and footer positions stayed identical before/after
  showing the overlay. Nine overlay captures show the right-aligned action;
  live bindings restored the connected state before three snapshots (EN Light
  requested720, EN Dark requested1280, ES Dark requested720). This is presentation
  evidence, not a connection/restart journey.
- Requested720 is clamped to client widths 837 EN and 863 ES; requested1040 and
  1280 yield 1024 and 1264. Exact720 acceptance remains open. High Contrast,
  non-100% scaling, Narrator, native pointer/keyboard gestures and magnet-input
  keyboard scrolling were not exercised.
- Serial user, keyboard-user, accessibility, Fluent and UX image review, followed
  by independent visual re-review, found zero counted defects introduced by
  these corrections. The duplicate fixture preview combines a metadata-waiting
  title with an already-added row; that existing state ambiguity remains open.
- The first capture exposed a GridLength resource incorrectly used as a double
  StackPanel spacing. A Grid spacer now consumes that native token directly;
  the final run opens Add successfully. Both capture-owned processes exited,
  and the fallback generated-output scan found nothing outside artifacts.
  Everything IPC remained unavailable.

These changes remain uncommitted with the owner's concurrent batch. No broader
release approval is implied. Earlier typography evidence below is historical;
the current capture supersedes its much wider shell minimum.

## Add form consistency correction — 2026-10-06

The Add form now uses shared native text roles: Subtitle for its title, Body
Strong for group headings, and Body for values and status, including free space.
Magnet and folder groups share heading treatment and insets. Shared heading and
ActionButton text uses cap-to-baseline bounds for icon alignment. ActionButton
uses WinUI's DefaultButtonStyle and ContentDialogButtonMinHeight; inline and
footer buttons retain native corner and state resources, with no new size or
corner tokens. The accepted two-pane layout remains.

- Final app build: `artifacts/add-type-verified-build.log`, zero warnings/errors,
  70.88 seconds. Only the managed app compiled; Lucide and TableView compilation
  stayed up to date. No engine build, full suite or transfer tests ran.
- Before: `artifacts/evidence/UiSelfCapture-fa5de6f3-6a7f-4f5d-bf6a-2e3ed950eb1b`.
  Final: `artifacts/evidence/UiSelfCapture-cc2eff3c-b14b-4d8d-ac3b-5bd1d4245bc8`.
  The focused `add-layout` journey completed all twelve EN/ES, Light/Dark,
  requested-size cells in 19.678 seconds, with no failure. Review-owned app and
  engine processes exited. The fallback output-directory scan found no output
  outside artifacts; Everything IPC was unavailable.
- Serial user, keyboard-user, accessibility, Fluent and UX review found no
  visible defect in the corrected empty Add form. The independent visual
  reviewer rechecked the owner's font, alignment and button corrections across
  all twelve cells and reported zero counted findings. This is a visual result,
  not proof of keyboard or screen-reader behavior.
- The current shared shell minimum clamps both requested 720 and 1040 widths:
  final client widths are 1181 in English and 1207 in Spanish. The 1280 request
  yields 1264 client pixels; client heights are 551, 671 and 791 at scale 1.
  Exact narrow-width acceptance is still open. High Contrast, scaling, Narrator,
  hover/pressed states, populated previews and enabled Add were not exercised.

Verification exposed a concurrent startup defect: RefreshMenus read toolbar
commands before x:Bind assigned them, throwing on a null dictionary key. The
later table-load error obscured that first exception. Tooltip lookup now uses
the existing model commands directly. Temporary tracing and an unsuccessful
initialization-order experiment were removed. Final startup and captures pass.

The existing Debug instance was closed normally under the owner's earlier
authorization. Its normal engine Exit ended the process, but the attached-process
helper did not confirm a zero exit code; that instance's persistence outcome is
unverified. Isolated capture engines subsequently exited successfully.

These corrections remain uncommitted with the owner's concurrent shared-control,
shell and engine work; no unrelated batch was committed by this correction.
This does not close the broader release gate or validate those engine changes.

## Current continuation — 2026-10-06

### Handoff to architecture work — `c7b9326`

The owner wants the in-flight release corrections finished before continuing
with `docs/architecture/proposed/`. That bounded batch is committed as `c7b9326`.
It is a checked starting point for architecture work, not release approval of
the changing shared checkout. No broader refactor was started in this pass.

- Every Speed limits entry point now opens Settings / Transfers and focuses
  Download. The competing dialog and its lifetime paths are gone.
- Ordinary Settings departure awaits saves, applies valid input, restores
  invalid input, and keeps refused input on Settings. A late acknowledgement
  preserves newer text. A subsequent explicit commit retains its submitted
  value without automatically submitting later text. Cancel clears deferred
  intent; there is no reconnect replay.
- Explicit editors use Save / Discard / Cancel through their existing save
  owners. Close admission remains closed while saves settle. Failed Save
  restores the actual editor and its error/focus. Source review caught and
  corrected a file retry masking a tracker error, and departure overlooking
  a field edited while another save was pending.
- The owner's separate download/upload/paused status fields are included with
  the footer correction. Below 960 DIP, connection state occupies a second,
  left-aligned line. This commit retains the custom title bar and accepted
  Settings composition.

Focused evidence, under `artifacts/evidence/`:

| Evidence | Outcome and limit |
| --- | --- |
| `UiSelfCapture-f462bf25-e588-4a0a-af5e-e7bd32cfdf7f` | Search passed in 21,061 ms: native result submission, native MenuBar Limits, Properties from Settings, named editor focus, unavailable commands and reopen handler; 12 localized captures. Physical Ctrl+K delivery remains unverified. |
| `UiSelfCapture-06a45c16-72ac-4a15-b0fa-39c06253824f` | Nine edit journeys passed in 9,189 ms: valid/invalid/pending departure, newer input, queued explicit commit, pending Cancel, and schedule Save/Discard/Cancel. Original fixture values and periods restored. |
| `DesktopCapture-2800c8b9-f6ae-4890-9301-7e66688fccba` | Exit Cancel in the 12-state matrix, failed Save retaining magnet input/error/focus, tracker draft across engine restart, and history advancing without UI passed in 28,139 ms, before the final footer correction. |
| `DesktopCapture-e4111dc7-63cf-4b92-b280-c36ab1e0450a` | Final footer matrix and desktop recovery outcomes passed in 31,880 ms. Fresh correction review closed narrow Spanish clipping with zero remaining counted footer defects. |
| `LibraryCapture-a31b50ec-1eee-4a3f-abff-603c0ea712a7` | Current rerun stopped before selection: `CreatePeerForElement(Filters)` threw COM `0x8001010E`. The initial capture shows 300 paused torrents; subsequent journeys and matrix did not run. Read-only diagnosis found no concrete production cause. Thread/factory affinity remains unproven. |

The edit diagnostic's first run (`UiSelfCapture-bf651171-f7c8-48a6-8f16-9271f17fdcfa`)
passed four journeys, then exposed its own asynchronous TextChanged assumption.
Real Enter copies editor text before Commit. Corrected tightly timed races
change preference input synchronously before yielding, then verify the native
display; the other input journeys use native editors. The check still requires
confirmed 257 beneath later draft 258. No production behavior or expected value
was weakened. These checks protect lost input, lost navigation and failed-close
recovery, which the earlier journeys did not exercise.

Footer baselines: `LibraryCapture-b76572c8-d385-4165-ac06-7b9db8abc4d5`
passed in 19,601 ms with 300 torrents/307 payload files retained;
`TrafficCapture-d05b13c8-f470-491b-bbe5-84ee2aa47672` passed in 46,353 ms
with 60 scenes plus 12 General-bottom images and bytes 606,082 to 3,506,076.
The first adaptive-state attempt did not move the text in this Window.
`DesktopCapture-7684afad-d116-4a83-8ffa-c19cc388bbab` proved the size-handler
correction made the second line visible, but fresh review found the last Spanish
letters clipped. Left alignment resolved that finding in the final evidence.

Parent review applied the requested roles in order: the new user reaches the
normal Limits destination and sees distinct Save/Discard/Cancel actions; the
keyboard user retains input and focus after failure; labels and states are
readable, while Narrator/High Contrast remain unproved; Fluent and UX review
confirmed the narrow connection message no longer collides or loses letters.
The fresh reviewer inspected all 12 prompts, failed-Save recovery and selected
search destinations with no counted finding, then all 12 footer images and the
four corrected narrow images. These are bounded visual verdicts, not claims
about uncaptured owner redesigns. Further style changes were not pursued.

App-only builds passed: `edits-close-build.log` (72.65 s) and
`footer-alignment-build.log` (44.19 s), zero warnings/errors. Intermediate
`edits-final-build.log` failed on a DLL held by a concurrent Release build;
`edits-footer-final-build.log` passed in 125.60 s while a separate Debug build
was active. Successful builds compiled only the app, with two C# and two XAML
compiler calls. The longer run spent 53.0 s in C# and 65.4 s in XAML, not extra
native targets or recursive copies; the final run spent 17.5 s and 23.6 s.
Avoid overlapping builds in this shared checkout. Prescribed output-location
checks were empty with the correct Windows exclusions. No engine build or full
suite ran; prior current-engine FilesSafety, CheckpointRetry and payload evidence
below remains applicable.

All UI/engine processes launched by this pass are closed. Existing windows and
engines were closed normally under the owner's prior final-check authorization.
The separate speed demo's state remains in its evidence store; its independently
launched Transfer peer was not touched.

Concurrent schedule gestures, projection/TableView, graph rendering,
Add/FileBrowser and inspector presentation, appearance timing, resources and
architecture-document consolidation remain outside `c7b9326`. The final app
build includes those working files and existing referenced binaries: it is not
an isolated build of the commit, or evidence for later edits/unbuilt TableView
changes. Start architecture work from the current tree and `c7b9326`, inspect
those changes first, and do not recreate the removed Limits implementation.

### Earlier release continuation

The current release goal is in [handover.md](handover.md#current-goal).
**Release acceptance is open for the changing shared candidate.** This pass
fixed the reported Ctrl+K routes and the concrete defects found in populated
captures. Fresh visual correction reviews have zero remaining counted findings.
Concurrent owner edits to list projection, sorting, status rates and graph width
arrived during finalisation; the captures do not establish those edits' behavior
or appearance. Prior scoped milestone reviews are not blanket release approval.

Release work proceeds by concrete user failures:

- Command search: verify native result submission, unavailable commands,
  Properties from Settings, setting navigation and reopening with Ctrl+K.
- Everyday journeys: examine populated torrent and detail views, addition,
  selection, filtering and recovery; retain valid existing data-safety evidence.
- Settings: compare current controls with common tasks and record explicit
  coverage or exclusions rather than claiming every libtorrent option is needed.
- Acceptance: review changed surfaces in EN/ES, Light/Dark at the three sizes;
  run a fresh adversarial review of the settled candidate and document real gaps.

The owner's concurrent schedule, list, table and presentation changes remain
separate and are preserved. This pass committed `8af777e` and `6b13ac9` only;
in the shared Spanish resource, only the rate-heading hunk belongs to this pass.
The historical milestone evidence and commits follow below. Entries under
Earlier handover evidence describe older candidates, not the engine refactor.

### Release corrections — `8af777e`

Native search baseline:
`artifacts/evidence/UiSelfCapture-727fe009-a787-4908-a3e5-74f9467c23ed/captures/review.json`.
The generated AutoSuggestBox item's Invoke provider raised QuerySubmitted in
all four journeys. Unavailable Properties did nothing; Properties from Settings
opened an inspector behind the page; Speed limits opened the separate dialog;
the listening-port result navigated without focusing its editor. Its value was
preserved. These are confirmed defects, not taste findings. The earlier partial
run exposed a diagnostic focus-order error, corrected before this complete
baseline. No external desktop input was used.

Search correction evidence:
`artifacts/evidence/UiSelfCapture-8872cc4a-d429-4283-a430-9333deb60a46/captures/review.json`
passed all four native submissions plus the reopen-handler check in 20,311 ms.
Unavailable commands are omitted, Properties reveals its workspace, speed-limit
search opens Transfers, and the named numeric setting focuses its inner editor
without changing its value. Reopening proves the shortcut's shared handler and
actual popup; synthetic/physical Ctrl+K delivery remains unverified.
The app-only build `search-library-build.log` passed in 33.99 seconds with zero
warnings/errors. Engine and referenced libraries were not rebuilt.

All 12 search size/theme/language captures were reviewed sequentially: the new
user has labeled result scopes, the keyboard user reaches the actual setting,
the accessibility review finds text labels and visible focus but cannot infer
Narrator/High Contrast behavior, and the Fluent/UX reviews find no new collision
or unreadable search content. Narrow captions retain their existing language-
dependent minimum width; this is not an exact 720-pixel client-width claim.
The accepted Settings composition is retained; scrolling and bounded content
width are not counted as taste defects. The fresh capture-only reviewer also
found no counted search defect.

Populated-library evidence:
`artifacts/evidence/LibraryCapture-bf12b05a-0db1-487c-aaec-dd6dd20e0503` passed
in 18,736 ms with 300 real, private, paused torrents and 307 payload files.
Native filter selection hid the rows without removing membership; native search
submission revealed the long-named torrent at ordinal 299 and cleared the
filter. The eight-file inspector loaded its engine-backed file list. The
launcher confirmed unchanged membership and payload bytes and clean process
exit. Its exact disposable fixture launcher is retained as `check.ps1`.
The app-only `library-selection-build.log` passed in 38.24 seconds with zero
warnings/errors; its only intervening correction concerned the diagnostic's
native filter selection provider, not production search.

Parent review covered all 36 library size/theme/language images in the five
requested roles. The first-time user can identify paused state and file counts;
the large-library user can see the revealed last row and retained filter state;
the accessibility role can inspect visible labels but cannot establish spoken
output or High Contrast; the Fluent and UX roles found no additional collision.
The fresh reviewer independently inspected all 37 library images, including
the baseline, and counted one defect: narrow Files hard-clips names/extensions
inside the checkbox and wraps the root into clipped lines. This was fixed
at the shared FileBrowser owner. Table ellipses, viewport-boundary rows and
normal scrolling were not counted as defects; no taste-driven restyling follows.

Correction evidence:
`artifacts/evidence/LibraryCapture-b1a80e78-b886-4f92-b0f6-db3fcab0682a` passed
in 19,402 ms, retaining all 300 torrents and 307 payload hashes, with clean exit.
The final app-only `file-names-build.log` passed in 40.86 seconds, zero warnings
or errors. Only app compilation ran; referenced libraries supplied packaging
metadata. The required output-location and process checks were empty.
Parent and fresh reviewer inspected all 12 corrected Files images; correction
round one passed with zero remaining counted defects. Narrow names now mark
truncation and no longer wrap into clipped lines. Full names/paths remain in
automation and native tooltip properties; actual hover/focus tooltip display
was not captured. The independent source review found no additional concrete
blocker in the scoped changes.

Live-transfer baseline:
`artifacts/evidence/TrafficCapture-4f36594c-77c0-49f5-8176-d86d70b8374a` passed
its 60-state journey in 46,077 ms, with 12 additional General-bottom images.
A disposable loopback-bound engine downloaded from the existing Transfer peer
at a 64 KiB/s limit. Downloaded bytes advanced from 589,701 to 3,473,295 while
the inspector showed a real peer, mixed states among 65 pieces and nonzero
history. All three owned processes closed; seed hashes and membership stayed
unchanged. The app-only build passed in 36.62 seconds with zero warnings/errors;
no engine or dependency build ran and the output-location check was empty.

The fresh reviewer inspected all 72 images and counted two defects: Spanish
Speed retained the English selected range, and both Spanish peer-rate headers
truncated to the same text. Parent review also confirmed a summary inconsistency:
Speed compared an instantaneous snapshot rate with peaks from asynchronous
history, visibly allowing 66.5 KiB/s current against 61.5 KiB/s peak. Corrections
use live TextBlock range labels, concise distinct Spanish rate headers, and the
same sampled history for graph summary and peak. The status bar retains its
instantaneous rate.

Live-transfer correction evidence — `6b13ac9`:
`artifacts/evidence/TrafficCapture-89ec6e75-705f-445a-ba36-a1933b8a19b4` passed
the 60-state journey in 51,277 ms and retained 12 General-bottom images.
Downloaded bytes advanced from 606,082 to 3,899,292. The fixture retained its
membership and seed hashes; all launched UI, engine and peer processes exited.
The capture build `traffic-corrections-build.log` passed in 39.01 seconds with
zero warnings/errors.

The fresh capture-only reviewer inspected all 24 correction images: 12 Speed,
six Spanish Peers and six Spanish workspace images. Correction round one passed
with zero counted findings. Parent review confirmed all 12 Speed and six Spanish
Peers corrections, in addition to the earlier baseline task review. The new
user sees distinct rate labels; the large-library user retains the existing
table and inspector routes; the accessibility role can establish visible names
but not speech or High Contrast; the Fluent and UX roles find no new clipping
or unclear state. Historical graph samples and instantaneous status values have
different sampling times; the graph's own summary, peak and axis now agree.

Independent source review caught one correction-induced failure: retaining
history after disconnect would display a stale rate as current. Current rates
now use the existing `Inspector.IsAvailable` condition; graph history and peaks
remain. Its bounded re-review passed with no remaining finding. This final
two-line guard was source-reviewed and compiled, not given another live capture:
the connected branch produces the same images, while the unavailable branch
passes null to the existing unknown-value formatter.

The final app build `traffic-final-build.log` passed in 66.44 seconds with zero
warnings/errors. Its two C# invocations, three PRI calls and five copy entries
match the preceding app build; only the app compiled and referenced libraries
provided packaging metadata. The longer duration is not explained by expanded
compile/copy scope; the log has no per-stage timing to attribute it further.
The post-build output-location query and app/engine/peer process check were
empty. No engine build, dependency build or full suite ran.

The owner-mentioned `winui-ui-testing` skill was read. Its native automation
principles informed in-process provider invocation and focus checks. No
`winapp ui` external desktop input was sent; actual Ctrl+K key delivery, hover,
Narrator, High Contrast and native window gestures retain their stated gaps.

Next acceptance work is bounded to the concurrent changes once settled. The
later `SpeedGraph.xaml`, `MainWindow.xaml`, list projection and TableView sorting
edits are outside these commits and postdate all or part of this evidence. Do
not claim the current working tree is the captured candidate, replay unchanged
engine checks, or discard the owner's changes to recreate it. Verify the user
outcomes those edits can affect, then obtain correction review if needed.

The current engine's earlier transfer evidence predated the owner refactor, so
two existing focused loopback checks ran once against engine SHA-256
`70BDA19FBB2BC266238B74CE066BAE913AE09261F1ED29912BCD947E770D4AFC`:

- `SelectedTransfer-b6c74131-cd58-44d5-9c12-9d745c1677e5` passed. Selected payload
  matched the seed; skipped content stayed unwritten. Actual normal/alternative
  rates were 134,188/466,862 B/s against 131,072/524,288 B/s configured limits,
  within the existing check's measurement bounds. Tracker merge and restart
  preservation passed in the same focused journey.
- `MagnetDownload-a07d4d96-b869-439e-a0b8-f6bb58c740e3` passed. An unknown magnet
  acquired metadata and downloaded its four-megabyte payload; Verify and Resume
  repaired offline corruption to the original SHA-256. The disposable source
  fixture was generated by the existing Transfer utility.

All engine/UI/peer processes started for these checks exited. The required
output-location checks printed no paths. No engine build or full suite ran;
the existing current-binary FilesSafety and CheckpointRetry passes remain valid.

Settings coverage audit, before further product changes:

| Everyday need | Current coverage |
| --- | --- |
| Keep other apps usable on a limited connection | Global download/upload limits, alternative limits and weekly schedule. |
| Control simultaneous work and seeding | Active download/seed limits, global peer connections, ratio/time stops, queue order and force start. |
| Choose where traffic goes | Listen port, automatic port mapping and explicit network-interface binding. |
| Give one torrent a bandwidth allowance | No per-torrent rate controls; this is a real capability gap, not an undiscovered setting. |
| Choose peer-discovery or encryption policy | No app controls for DHT, PEX, LSD or encryption; libtorrent defaults apply. |
| Use a proxy | Explicitly outside the initial product scope. Interface binding is not a proxy implementation. |

Sequential product roles: a first-time user needs sensible defaults and clear
global limits; a large-library user needs predictable selection and queue
actions, with per-torrent rates a useful missing capability; a network specialist
needs honest descriptions of interface binding and discovery rather than implied
anonymity; a maintainer rejects an indiscriminate advanced-settings dump. The
release review must distinguish these missing controls from broken existing
features. No usage telemetry establishes a ranking of “most used” settings.

Scope decision: retain the initial product's existing global/alternative limits,
queue and seeding controls, and interface binding. Per-torrent limits and
discovery/encryption policy controls are explicit capability limitations for
this candidate; they are not represented as implemented or as proven demand.
Proxy remains outside the initial scope. An advanced-settings expansion would
need a coherent product change rather than incidental additions to this defect
pass. No claim of qBittorrent feature parity or complete libtorrent configuration
is made.

### Earlier milestone continuation

The first coherent step retains the owner's role folders and finishes the
accepted schedule. The current source compiled without a schedule compiler
error; the initial failure was an intermediate DLL held by another MSBuild
process, which exited without intervention. No Settings or title-bar restyling
was needed. A short period's labels wrapped and clipped in the actual capture;
explicit single-line trimming fixes that defect while selection and tooltips
retain its full description. An unrelated shutdown failure was reproduced in
the existing recovery journey. Local native symbols identified a minimum-width
setter reached through `AppWindow.Changed` during window destruction. Caption
updates now stop once the existing close owner commits to closing.

- Final app build: `artifacts/evidence/schedule-final-build.log`, 39.02 seconds,
  zero warnings/errors. Only TinyTorrent compiled, in its two XAML passes;
  Lucide and TableView reused their outputs. No engine build or full suite ran.
- Schedule before: `artifacts/evidence/UiSelfCapture-c3581096-4f3e-4c48-b47e-8f3da7e0ceb5/captures`.
  After: `artifacts/evidence/UiSelfCapture-a9f107d9-53a7-4f4d-a183-23fde4350157/captures`.
  All 12 size/theme/language combinations completed in 24,269 ms. Native controls
  retained invalid input, saved Monday 22:17–01:43, cancelled edits without
  changing saved data, preserved its 206-minute duration when moved, and removed
  only the review period. The timeline uses its production reschedule owner;
  this does not claim physical pointer-drag coverage.
- Original recovery-and-close journey: `artifacts/evidence/UiSelfCapture-5bad8e3a-c93b-4eaf-bc53-26835bcca86f/captures/review.json`,
  7,717 ms, all three recovery outcomes passed and the process exited normally.
  Rejected limit/magnet input stayed editable; a removed torrent's tracker
  draft remained with Save disabled and Cancel enabled.
- Sequential visual review: the first-time user can find Add and distinguish
  disabled scheduling from saved periods; the keyboard user has native fields,
  actions and a timeline focus route; the accessibility review finds text
  descriptions alongside colors but does not substitute for Narrator or a real
  contrast theme; the Fluent and UX reviews retain the accepted hierarchy and
  find no further counted defect in the reviewed schedule states. All three
  viewport sizes, both themes and both languages were captured. Narrow windows
  scroll the accepted page rather than hiding actions. Decorative watermarks
  are an accepted taste choice, not a defect to redesign.
- Every post-build/run output-location check returned no paths. All launched
  UI and engine processes closed. Fresh milestone verdicts and subsequent
  current-engine safety evidence follow below.

### Current engine safety evidence

The first coherent commit is `0f3ff54`. No engine code changed during this
continuation. The current native output and the engine beside the app both hash
to `70BDA19FBB2BC266238B74CE066BAE913AE09261F1ED29912BCD947E770D4AFC`.
The two required checks now passed once against that binary:

- `FilesSafety-3b060798-e17b-4009-a368-a9ecd8116f0b`: collision bytes preserved,
  outside shared owners protected, complete shared group moved/deleted,
  unrelated files retained, interrupted-marker recovery refused unsafe retries.
- `CheckpointRetry-766758d3-73fe-48d2-b8e7-48b8b7025857`: blocked checkpoint
  reported, retried after the obstruction was removed, and retained membership
  and running intent after restart.
- `InterruptedMove-e92b6aac-a449-4c37-8289-318c40808f37`: a separate focused
  check killed only its disposable engine during a real 1,024-file relocation.
  Twenty-five files had moved and 999 remained at the source. Restart reported
  `move_interrupted`, no moving operation or download rate, and preserved the
  destination marker. Every file existed at exactly one location with unchanged
  bytes. This closes the earlier injected-marker evidence gap. The exact probe
  is saved as `check.ps1` beside `result.json` in that evidence directory.

These directories are under `artifacts/evidence/`. The real-interruption probe
took under five seconds, with 2 MiB of local fixture payload and no transfer
peer. It does not manipulate or delete the owner's data. Every launched process
closed and each required output-location check printed nothing. No full suite
or engine build ran. Actual UI move/delete submissions are recorded below.

### Background and desktop behavior gate

Committed as `43e47eb`.

The current desktop journey passed in 17,502 ms with a clean UI exit:
`artifacts/evidence/DesktopCapture-9b76c9c8-c247-4c79-9be4-e1d9846b6918`.
All 12 language/theme/size combinations kept unfinished magnet input after
declining Exit. Cancel then cleared the recovered Add draft. Killing only the
fixture engine and restarting it retained torrent membership and the open
tracker draft in the same window. Save disabled while disconnected; Cancel
remained available. After the window closed, engine history advanced from
1791254481 to 1791254484 without a UI process.

The fresh capture-only reviewer found one defect: the disconnected warning
pushed the tracker editor's buttons below its visible bounds. Inspector sizing
now measures the available parent workspace, rather than its already oversized
child. The reviewer passed the correction in all six Spanish size/theme states
and both narrow bottom views. Sequential user, keyboard, accessibility, Fluent
and UX review found no further counted defect in these states. Still images
do not establish actual keyboard or screen-reader operation.

Final build: `artifacts/evidence/desktop-layout-build.log`, 27.24 seconds,
zero warnings/errors, only the app compiled. A preceding build rebuilt unchanged
libraries because the first commit changed SourceLink/assembly commit metadata;
subsequent app-only checks use `BuildProjectReferences=false` with the verified
library outputs. Every launched process closed; the output-location check was
empty. No engine rebuild or broad suite ran.

Remaining desktop plan checks were assessed for a concrete likely failure:

- Open/Close/Exit, protected input and engine restart: current process journeys
  above cover lost drafts, failed reconnect and shutdown crashes.
- Closed-window history: current timestamps advance; the fixed ring capacity and
  aggregation have one engine owner. A day-long wait adds no likely-failure proof.
- Completion notifications, first-close notice and tray failures: current source
  uses the engine-owned native tray and Windows notification-state gate. No code
  changed there; headless capture cannot prove visible Windows delivery.
- Sleep while the UI is closed: current engine-owned power request checks mains,
  active transfers and preferences, and releases on exit. Physical sleep and
  battery transitions remain unverified; changing this machine's power state
  would interrupt the owner without testing a changed implementation.
- Notification-area startup and associations: current registration owner uses
  quoted executable paths and per-user registration. No startup/default-app
  settings were changed on the owner's machine; an actual logon remains unverified.
- Native caption menus, drag, acrylic, High Contrast and Narrator retain their
  established owners. XAML captures do not prove those Windows surfaces.

Capture size names are requested outer-window sizes, not image dimensions.
Windows frame insets reduce the 1040/1280 captures to 1024/1264 client pixels.
The accepted caption's minimum width clamps a 720 request: the current desktop
captures are 734 client pixels in English and 760 in Spanish, at scale 1.
Thus exact 720-pixel outer-window operation is not established. The caption was
not narrowed or restyled to defeat its existing collision protection.

For file-operation recovery, the user role favors a clear retry; the Windows
designer keeps accepted background work on the torrent row; the storage expert
requires clearing a preflight marker when no files moved; the maintainer rejects
a second UI copy of engine move state. The chosen contract remains: a refusal
retains dialog choices, while an accepted operation's later error explains how
to reopen Move and choose the correct folder. No extra state mechanism was added.

### Details and preferences evidence

The current inspector and accepted Settings cards were captured in English and
Spanish, Light and Dark, at all three requested sizes (subject to the caption
minimum above). No Settings or schedule restyling was needed.

- `UiSelfCapture-94168166-627e-4ad4-8e6f-f9f18078a770` completed the English
  images before its old launcher bound interrupted the larger bilingual batch.
  These are image evidence, not a successful complete run.
- `UiSelfCapture-aee5a18a-1456-40c8-a79f-c87d8a498401` completed the Spanish
  matrix and native journeys in 139,454 ms, with no failure and a clean exit.
  Selecting High saved priority 7 and restoring the original choice succeeded.
  A live language switch retained tracker input and programmatic native focus;
  Save confirmed the tracker and restoring the original tracker list succeeded.
  The splash preference applied immediately and was restored. Entering port
  70000 retained the rejected input on focus departure; correcting it recovered
  the field. No physical keyboard or system-picker coverage is implied.
- Files had two concrete rendering defects: the priority label clipped, and
  Spanish bulk-action labels squeezed the summary into a one-character column,
  pushing the file list out of view. A 180-wide priority column and a separate
  wrapping bulk-action row preserve the existing commands and expose the files.
  `UiSelfCapture-f444aecc-8628-4e17-8bad-020d917b8236` captured that improvement
  in all 12 combinations in 21,970 ms, with a clean exit.
- The fresh capture-only reviewer examined inspector, Settings and schedule
  families and found one remaining defect: selected priority/interface labels
  sometimes retained the previous language. The correction keeps live native
  TextBlock labels instead of replacing selected string content.
  `UiSelfCapture-1c8092d4-27e3-4e75-8d03-96354eca55f1` completed the focused
  correction matrix in 72,239 ms with a clean exit: Files, Appearance and Network
  in all 12 combinations, plus a narrow reverse ES-to-EN switch. The forms stayed
  alive and priorities/preferences remained unchanged. Black priority-popup
  images do not establish dropdown readability.
  First correction review passed Files and Appearance but caught English
  “Any interface” in every Spanish Network view. WinUI moves selected UI content
  out of its item, so replacing the emptied item's content missed the displayed
  label. Static choices now retain named TextBlocks; dynamic interface choices
  use a notified label through a native item template. The final capture holds
  the selected theme constant while changing language, avoiding a selection
  change that could hide this failure.
  `UiSelfCapture-cf161c2a-da35-424c-b54e-036a52551653` completed that final
  correction matrix in 71,742 ms, with no failure and a clean exit. The retained
  forms, priorities and preference values passed their guards in both directions.
  The fresh reviewer's second correction review passed Files, Appearance and
  Network across all 12 combinations and the reverse switch, with zero counted
  defects or correction regressions. The accepted Settings cards, title bar
  and schedule retain their existing design.

An earlier Spanish-startup diagnostic raced the existing asynchronous language
load. Capture now awaits that same owner before choosing its matrix language.
The interrupted `UiSelfCapture-22d68de8-002c-40f4-88d7-a4e63a1ae0c0` run is not
behavioral evidence. Normal window placement already awaited language loading.

### Move and delete evidence

`FilesCapture-b61de94a-c8bb-4992-8078-f8f4100060c3` completed 72 matrix states
and all five native submission outcomes using disposable local payloads:
destination ownership refusal retained the dialog and choices; an occupied-file
collision retained source and destination bytes; a complete three-owner shared
group moved correctly; deleting one owner retained the other two and shared
bytes; deleting the remaining group removed membership and payload while leaving
unrelated sentinels intact. Its launcher reached its old five-minute bound during
the final PNG, so the overall run is **not** a clean-exit pass. The outcomes were
recorded before that failure, and saved membership/payload were checked afterward.

The fresh capture-only reviewer accepted the unchanged delete/collision views
and found two Move defects: empty facts consumed space needed for the options,
and the refusal could be out of view. Empty facts now collapse and a refused
submission reveals its feedback. `FilesCapture-70f40b2d-249f-4f42-8e91-364d8f4b5469`
completed the 36 Move states and native ownership refusal in 157,142 ms, with no
failure and a clean exit. All four fixture members and payload hashes remained
unchanged. First correction review resolved the empty-space finding; feedback
still clipped after shrinking the window. The body now follows the window size
and retains the bottom position only if the person was already there.
`FilesCapture-460ca8cb-a178-4f13-b405-248957498907` passed the final correction
run in 156,869 ms, with a clean exit, 36 states and unchanged membership/hashes.
The fresh reviewer's second correction review passed all 12 refusal states,
including all narrow variants: feedback and Refresh are visible. Zero counted
Move findings remain. Its immediate post-submit PNG is blank; the readable
final resize states and prior readable post-submit capture support this verdict.

All evidence directories above are under `artifacts/evidence/`, with images and
`review.json` in `captures/`. The files fixtures retain the exact launcher as
`check.ps1`. Native picker interaction, cross-volume device failures and dropped
native alerts are unverified. The changed UI adds no filesystem implementation;
current FilesSafety, CheckpointRetry and the real interrupted-move check provide
the targeted data-safety evidence. No engine rebuild or full suite was needed.

The Move correction build was
`artifacts/evidence/final-corrections-build.log`: 30.92 seconds, zero warnings or
errors. The log shows only TinyTorrent compilation; library packaging metadata
was read without recompiling the libraries. Output-location checks stayed empty.
The final selected-label build, `artifacts/evidence/selected-label-build.log`,
passed in 25.62 seconds with zero warnings/errors and the same app-only scope.

Sequential review of these surfaces used the requested roles. The first-time
user needed readable options and the reason a Move failed; the keyboard user
needed retained edits and reachable native commands; the accessibility role
required labels and text alongside visual state, with Narrator/High Contrast
explicitly unverified; Fluent and UX roles retained the accepted composition
and fixed only clipping, hidden state and space displacing needed content.
Watermark prominence and denser visual styling remain taste notes, not defects.

Remaining plan checks were assessed against a likely failure a person would see:

- Failed edits and live language: native rejected-input and retained-draft
  journeys above cover losing work; the fresh review's selected-label defect
  receives its own focused correction capture.
- Committed file/tracker choices: current native submissions confirm the engine
  answers; prior restart-policy evidence predates the owner's refactor and is
  not relabeled as current. No persistence code changed in this continuation.
- Peers and active speed/piece updates: the inspector matrix establishes layout,
  including empty states, not populated peer traffic. A new swarm/throughput
  exercise is not justified by these view-only corrections; that coverage remains
  unverified on the current candidate.
- RTL, enlarged text, High Contrast and actual Narrator speech: EN/ES and both
  normal themes are current evidence. No new RTL language or system accessibility
  setting is introduced for this scoped pass; these conditions remain unverified.
- Move/delete byte loss: current focused engine checks and native disposable
  submissions cover collision, shared ownership and actual interrupted movement.
  Cross-volume/device failure and deliberately dropped alerts remain unverified;
  adding another storage fault mechanism would not exercise a changed owner.
- Explorer batch opening, physical drag/drop and folder picking: retained native
  owners are outside offscreen XAML evidence. No unrelated desktop automation or
  default-application changes were introduced to manufacture a pass.

### Final review and completion

Completion commits: `0f3ff54` retains the owner's app folders and finishes the
schedule; `43e47eb` completes the background/desktop gate; `c9e3995` completes
Details/preferences and Move/delete with the reviewed corrections and diagnostics.
The last two milestones share that commit because their settled app edits were
compiled and reviewed together; reverting it removes that coherent correction.

The fresh final reviewer checked the handover plan, current source and evidence
after its capture-only review. Milestone 3's lifecycle/background owners,
milestone 4's inspector/preferences and milestone 5's file operations match the
plan. It found no additional counted product defect. The broad historical
“preserve choices on failure” wording was clarified to submission refusal,
matching the existing precise asynchronous-operation rule; this does not add a
second operation-history owner.

The final correction gates leave zero counted defects. The evidence limits
above remain limits, not claims of physical verification. All owned app/engine
processes are closed, the final output-location check prints no paths, and no
engine code, dependency, distribution work or full test suite was included.
The fresh reviewer confirmed final acceptance against the committed source and
this ledger; no further implementation or verification was required for that
scoped acceptance.

## Earlier handover evidence

Status checked on 2026-10-05. Work stopped at the owner's request; resume from
[handover.md](handover.md). This is an interim report: the five-milestone goal
is **not complete**. The first two milestones have commits and recorded runtime
evidence. Background integration, Details and preferences, and Move and delete
files have current source work and successful builds, but their current runtime
gates and completion commits remain open. Older runs do not prove the refactored
engine or the new operations.

The owner now requires unattended self-capture rather than desktop interaction.
The app visits its actual views in an off-desktop, nonactivating window against
a named disposable store, saves XAML images and control bounds, and closes.
The first batch captured 121 scenes across three sizes and two themes in about
51 seconds before its recovery smoke case exposed invalid-input handling.
That batch is visual evidence, not a passed recovery check. The final focused
run passed all three recovery journeys in 12,156 ms: rejected speed-limit and
magnet input remained editable; removal retained the inspector draft with Save
disabled and Cancel enabled. All launched processes closed; the final process
check found no app or engine. No
installer, signing, release check, dependency rebuild, or Windows default-handler
change was performed.

## Starting the candidate

The matching isolated build is:

`C:\SynoSoftware\TinyTorrent2\artifacts\checks\ui-review-build\TinyTorrent.exe`

Its adjacent `Engine.exe` is required. Start the application normally, without
administrator elevation. If another TinyTorrent engine is already running, use
its tray Exit first when convenient: the logon-scoped endpoint deliberately
attaches to the existing engine rather than creating a second one. This report
records the unattended viewport batch separately from the behavioral checks;
it does not claim native chrome or desktop acrylic are in XAML images.

A historical read-only process check found engine PID 23968 running from
`artifacts/bin/Engine/Release/Engine.exe` and UI PID 17000 from
`artifacts/bin/tinytorrent/release_win-x64/tinytorrent.exe`. These are the existing
delivery, not the isolated candidate above. They were left running. The focused
checks reject a pipe owned by an engine they did not start; changing the test's
data directory cannot isolate it from this live logon endpoint. No review or
build task remains running in the agent team. Current runtime verification
remained blocked at that observation; no unsafe test retry or bypass of its
ownership guard was attempted. The 2026-10-05 capture check found neither process
running and used a new disposable store. Five focused engine checks subsequently
passed against the current isolated native output, including transfer and file
safety. That earlier process lock no longer blocks their verification.

The candidate's engine SHA-256 matches the isolated native build:

`68AA3BDFE6FE57566D3630ED2F9538ED4CDC1F688F482E15B82B4495BDE23FE3`

Current native compilation used Visual Studio MSBuild on the existing engine
project, with the owner's compiled `3rdParty` dependencies. Managed compilation used Visual
Studio MSBuild on `app/src/TinyTorrent.csproj`, Release/x64. Both final builds
passed after the file-safety, dialog, and recovery-feedback corrections. The later
completion/checkpoint/Exit build passed in 57.86 seconds with no warnings/errors.
The matching app build passed using the existing TableView output assembly;
its first attempt found a stale reference assembly, corrected by selecting
ProduceReferenceAssembly=false. The five focused checks below establish their
specific outcomes, without proving every desktop journey. An earlier self-capture
build passed in 58.99 seconds, with 32.4 seconds in two C# passes and 21.9 seconds
in two XAML passes. The latest integrated app build passed in 112.55 seconds with
zero warnings/errors, including the coordinated PipeClient changes; its log is
artifacts/checks/ui-review-build/integrated-app-build.log.
Only the app compiled; reference projects supplied existing
outputs. An earlier attempt failed on an ambiguous dispatcher enum, corrected
at its call site. The WinUI executable requires its adjacent managed and runtime
files; its launcher size alone is not the application size.

Earlier prescribed Everything queries reported an unavailable IPC server;
fallback traversals found no stray output folders. After the final smoke run,
the prescribed query returned exit 0 and no paths. A separate traversal excluding
`artifacts`, `3rdParty` and reparse points also found no stray output folders.

## Milestones and evidence

The requirements remain those in
[First implementation](architecture.md#first-implementation). The detailed
journey, decision, and reviewer ledger is [implementation.md](implementation.md).

| Milestone | Commit and current status | Evidence and remaining gate |
| --- | --- | --- |
| First usable download | `e0a5a92` — Add a persistent torrent engine and MVVM download window. Historical functional/resource gate recorded. | Real picker, paused Add, selection, Ctrl+S/Ctrl+P, loopback download with matching hash, Close with continued transfer, tray reopen/Exit, one engine/window, saved membership/intent, failed storage, live EN/ES and theme with retained draft. `milestone1.trx` records 207 passed, zero failures/skips. Current expanded application still needs its later reviews. |
| Everyday torrent actions | `6afbf65` — Add everyday torrent actions and guarded batch addition. Historical functional/source gate recorded, with explicit interaction gaps. | Guarded magnet preview and cancellation, actual selected-file transfer, duplicate tracker merge, queue order/restart, Force/Resume, corruption repair through Verify, Remove keeping files, search and global/alternative limits. Batch UI snapshots show 30 additions and 29 after removal. `milestone2-final.trx` records 208 passed, zero failures/skips. Thirty native source activations were checked; registered Explorer associations, physical dragging, and complete keyboard routing were not. |
| Background and desktop behavior | Current integration is uncommitted; no completion commit. | Earlier real tray, background download, completion event, session-end save, registration, window/splash and explicit restart checks exist. Current Open/Exit readiness and human-wait corrections were source reviewed and built after refactoring. Re-run the affected current lifecycle journeys; actual idle sleep/battery transitions and notification delivery remain unobserved. |
| Details and preferences | Current integration is uncommitted; no completion commit. | `SettingsPolicy` and `CommittedFiles` passed against the current engine: saved limits/schedule/network policy, individual pause, priorities and tracker tiers survive restart. The unattended batch visited all five Settings and six inspector sections at three sizes in both themes. Hidden-view demand guards are present. Failed UI edits, closed-window history, live-switch/RTL/focus and recovery cases remain distinct gates. |
| Move and delete files | Current engine/UI integration is uncommitted; no completion commit. | `FilesSafety` passed against the current engine: collision refusal, outside-owner protection, shared scope, saved move recovery and unrelated-file preservation. It injects a durable interrupted marker rather than crashing a moving process. Native MVVM dialogs and command paths compiled and were captured. Current actual UI submission, crash timing and cross-volume failure remain separate gaps. |

The working tree includes the owner's engine refactor and other ongoing changes,
from concurrent passes. They have not been reverted or
included in a milestone commit by this pass. The checkout is `main`, the primary
branch to which the owner moved the shared tree; no branch was switched here.
Preparing later source work while desktop checks are deferred does not waive the
ordered completion gates or declare milestone 3 finished.

### Evidence locations

All paths below are relative to the repository root.

| Evidence | What it establishes |
| --- | --- |
| `artifacts/evidence/chrome-review` | First-screen picker, keyboard, transfer, close/reopen and draft captures; the detailed outcomes are in the first journey record. |
| `artifacts/TestResults/milestone1.trx` and `milestone2-final.trx` | The recorded TableView test results above. These are control tests, not tests of the later native refactor. |
| `artifacts/evidence/EverydayUi-da1e3c24-fdf7-4aa8-8bfc-8baad6d26a13` | Native control outcomes, saved `0,7` priorities, speed choices, retained uncertain draft, and explicit same-store retry. |
| `artifacts/evidence/BatchUi-408c5bde-413e-48d0-85aa-e2e929eafcf6` | `added-snapshot.json` has 30 torrents; `removed-snapshot.json` has 29. One form accepted separate native activations. |
| `artifacts/evidence/SelectedTransfer-5249dcd5-cae5-4a24-ae48-2f49b8c11756` | Selected payload transferred; the skipped file did not receive payload. |
| `artifacts/evidence/DesktopTray-fc9c0a65-a0c0-4684-9152-a7ea82cb838a` | Native menu/automation structure: two status rows, two separators, three commands; session pause preserved individual pause. Normal status ink is source evidence, not a pixel measurement. |
| `artifacts/evidence/DesktopBackground-534d1ca2-2daa-4d01-adef-d37f29cf3450/result.json` | Closed WinUI, matching 64 MiB payload hash, exactly one completion event. `PowerObserved` is false. |
| `artifacts/evidence/DesktopSessionEnd-3db9321c-57b2-4f80-ad63-7212c234a76b/result.json` | Owned-window WM_ENDSESSION save: 584 ms/exit 0; failed final checkpoint: 193 ms/exit 1. No actual Windows logoff was performed. |
| [Background checks](implementation.md#background-checks) | The remaining named checkpoint retry, registration, startup, restart failure and window evidence directories and their limits. These predate the current integration. |
| `artifacts/evidence/CheckpointRetry-a11cf6af-78d0-4745-bbd1-aae4e5539e5d` | Current failed resume write recovers and identity/running intent survive restart. |
| `artifacts/evidence/SelectedTransfer-4e120f98-5fd1-4143-9181-d62ad37ad7bb` | Current completed wanted payload matches immediately; skipped payload is absent. Normal/alternative caps measured 130,389/454,684 B/s. |
| `artifacts/evidence/SettingsPolicy-86fa9857-d64f-4b09-8f6b-64edf1da351b` | Current schedule, individual pause, manual override, saved settings and absent-adapter behavior. |
| `artifacts/evidence/CommittedFiles-c66c4d70-09b7-4c26-89f9-77afeb4ba020` | Current priorities and tracker tiers survive restart. |
| `artifacts/evidence/FilesSafety-96eed6a2-b593-4462-aa52-454fb3f23f36` | Current file-safety checks; interrupted state is injected, not a real crash. |
| `artifacts/evidence/UiSelfCapture-c11d60b6-5d6f-45f7-bb91-b1f865579d09/captures` | 121 actual XAML scenes at 1040×680, 1280×800 and 720×560 in Light/Dark, plus Spanish Schedule. The subsequent speed-limit recovery case failed and is not claimed as passed. |
| `artifacts/evidence/UiSelfCapture-e494957a-cdea-4359-b498-7bdd65569b6a/captures` | Final recovery check passed in 12,156 ms with 13 scenes. Invalid limit/magnet input remains editable; removal retains the inspector draft with correct Save/Cancel availability. |

`engine/tests/Checks.ps1` contains three focused scenarios now passed against the
current isolated engine on 2026-10-05. They
watch distinct failures: saved policy overriding individual pause or bypassing
an absent adapter; stale checkpoints defeating committed file/tracker choices;
and collision/shared-file/recovery handling damaging payload or resurrecting
removed membership. The file scenario's injected interrupted marker is not a
real crash test. These runs do not establish cross-volume failure or dropped-alert
behavior, and their current coverage does not substitute for those journeys.

## First resource check

These are **historical measurements from the first usable window**, recorded in
[First resource measurements](implementation.md#first-resource-measurements).
They have not been repeated for the expanded UI or current native refactor.
Working set and private commit are distinct counters, in bytes.

| State | Engine working set / private commit | UI working set / private commit |
| --- | --- | --- |
| Empty idle | 19,636,224 / 4,562,944 | Closed |
| First usable empty window | 20,021,248 / 4,804,608 | 149,823,488 / 109,449,216 |
| Active download, window open | 26,566,656 / 6,148,096 | 183,472,128 / 129,708,032 |
| Active download, window closed | 27,336,704 / 7,204,864 | Process exited |
| Active seed, window closed | 21,692,416 / 6,152,192 | Closed |
| Restored seed | 20,836,352 / 4,943,872 | 161,492,992 / 119,840,768 |
| Six language switches then Close | 20,721,664 / 4,849,664 | Process exited |

Observed Open-to-usable upper bounds were 2,908/3,009 ms via the engine and
3,399 ms via the actual tray. They include command and UI Automation observation
overhead, so they are not pure cold-start timings. Download rates were
918,508–1,074,815 B/s against a 1 MiB/s local cap. Working-set peaks were
28,958,720 bytes for the engine and 183,902,208 for WinUI. Short download and
seed CPU samples were 0.171875 CPU seconds in 2.566 seconds and 0.265625 in
4.062 seconds respectively; neither proves steady-state usage.

The completed 67,108,864-byte loopback payload hash was
`62B89C1E1DF10F82EB427DA59876867D19EDD50507DDB64B9953A46A381C9E9B`.
Six live language switches retained no UI process after Close. The short sample
does not establish a long-running heap bound. Installed shared runtime sizes,
reported separately at that baseline, were 80,006,873 bytes for .NET and
121,408,317 for Windows App Runtime; they are not process-private memory or the
current application delivery size.

## Decisions and why

Sequential roleplay answers are recorded at their owning implementation sections;
they were not parallel votes. The chosen answers use the owner's rulings and
common sense, rather than requiring every advisory suggestion.

| Decision | Chosen behavior and reason | Record |
| --- | --- | --- |
| Noticeable first Open | Keep the two-process split and captionless native splash: closing WinUI releases over 100 MB of private commit while transfers continue. Recheck startup after expanding the UI. | [Resource decision](implementation.md#first-resource-measurements) |
| Chrome, language and icon | Reuse the LabForms caption, one acrylic surface, native focus and window behavior; EN/ES without flags, live theme changes, SVG in the XAML caption and ICO where Windows APIs require it. | [First usable download](implementation.md#first-usable-download) |
| Batch/duplicate/queue behavior | One Add draft and guarded preview owner; preserve choices and tracker intent, defer only required dialogs, order the download queue without preventing seed selection. | [Everyday actions](implementation.md#everyday-torrent-actions-design-and-ownership) |
| Tray and background feedback | Two readable nonclickable status rows, Show window, Pause/Resume Transfers, Exit, and two separators. Preserve each torrent's intent; report tray-command failures without blocking the engine. | [Background corrections](implementation.md#background-review-corrections) |
| Native navigation and schedule | The owner's NavigationView ruling supersedes the prototype menu gesture. The prototype still owns the schedule's information structure: proportional time, ruler, saved periods, legend and actions, even when off. Do not populate fake sample periods. | [UI corrections](implementation.md#winui-review-corrections), [Preferences](interface.md#preferences) |
| Deferred desktop verification | Continue separable source integration, but keep runtime gates and milestone commits open. Review settled changes, not interim code that will be replaced. | [Deferred integration](implementation.md#details-integration-while-desktop-checks-are-deferred) |
| Human wait during Exit | Acknowledged draft prompts and native picker waits suspend the unresponsive-window deadline; continuing closure restores it. Busy engine work retains its bound. | [Background integration](implementation.md#background-integration-after-the-engine-refactor) |
| Schedule/seed/network policy | Manual Resume or alternative limits overrides the current scheduled mode until its next boundary; manual Pause always wins. An absent selected adapter cannot be bypassed. Explicit Resume/Force exempts a completed seed from stop limits. | [Details integration](implementation.md#details-integration-while-desktop-checks-are-deferred) |
| Inspector and annotations | Collect only the visible detail view. Keep background history bounded. Read annotations from the pinned ABI's add_torrent_params/resume representation, without a new parser or ABI change. | [Inspector integration](implementation.md#inspector-engine-integration) |
| Shared files and unknown magnets | Move the selected shared group through a union preflight and libtorrent operations. Keep source/destination holds until a known outcome. Wait for unknown file ownership rather than risk unrelated payload. | [File decisions](implementation.md#file-operation-decisions), [Engine contract](engine.md#removal-and-relocation) |
| Move progress and deletion | Show Moving files without a fabricated copy percentage. Delete membership durably before selective payload removal; never replay deletion after a crash. | [File integration](implementation.md#move-and-delete-integration-while-desktop-checks-remain-deferred) |
| Recovery feedback | Keep generic unconfirmed-command feedback separate from saved interrupted moves and active uncertain moves. The active case explains Exit/reopening because Move files is unavailable while its path holds remain. | [Recovery feedback](implementation.md#recovery-feedback-distinctions) |

## Reviewer findings and dispositions

The complete historical finding-by-finding ledger remains in
[implementation.md](implementation.md); the summary below points to its
dispositions rather than treating old issues as a fresh review. This pass did
not close or consolidate GitHub advisories. A later read-only check confirmed
another pass had closed sixteen engine issues; its source-only verification and
the remaining work are recorded in
[Advisory follow-up](implementation.md#advisory-follow-up-on-2026-10-05).

| Review | Findings and action |
| --- | --- |
| First download | Fixed permanent command disable after checkpoint failure, consumed-preview retry, hidden refusal reasons, stale sorting, dropped-alert ownership, stale final checkpoint acceptance, missing bounded diagnostics, state-thread serialization, missing table name, premature unused settings, Add selection/reveal and SelectionContainer support. The first journey section records the checks. |
| Everyday rounds 1–3 | Fixed unchanged ticks cancelling queue drag, Add hidden by filters, lost disconnected speed draft, ignored preview error, keypad-only queue keys, duplicated paste/drop decoding, enabled disconnected toggle, stale recovered error, ratio sort/display disagreement and supplied sources ignoring the saved Add preference. |
| Everyday rounds 4–7 | Fixed source loss during another modal task, duplicate shortcut policy, native inputs bypassing an existing draft, typing during captured speed save, unnecessary General detail fetch, missing Remove live refresh, late clipboard input modifying a closed/submitted draft, hidden no-wanted-files recovery, and staged magnet text being submitted again. |
| Everyday rounds 8–11 and runtime | Fixed seed queue reorder/display disagreement, full detail arrays in routine snapshots, collapsing matching file-tree folders and missing accepted-command accessibility announcements. Narrowed obsolete modal wording. The final reviewer withdrew the padding candidate because no supported libtorrent layout demonstrated it; no concrete source finding remained. Physical/Narrator evidence remains open. |
| Background | Fixed overlapping Close/Exit loss, combined errors losing the first name/reason, regional number formatting, missing window relaunch properties, invalid registration operation types, default-store restart after custom-store failure, hidden explicit Restart launch errors and tray failure suppression. The actual registry check also exposed and fixed its read-buffer size error. |
| Advisory engine issues 57–66 | Concrete queue refusal, independent error facts, display-name disagreement, stale tooltip, nonblocking failure feedback and direct-add/duplicate notices were addressed. Broader structure/type/naming proposals were initially advisory; the owner subsequently assigned an engine refactor. [The recorded issue table](implementation.md#advisory-engine-issues-5766) distinguishes the original dispositions from that later assignment; it is not a claim that those issues were closed. |
| Whole-app source review | Fixed F2 routing in the file tree, a covered tracker table staying in keyboard navigation, inspector clipping from feedback allocation, new activation loss during Add cancellation, and swallowed forwarding refusal/transport errors. Source re-review and build passed; corrected geometry/focus still need runtime review. |
| Advisory running UI review | Source corrections cover settings allocation/alignment (78), invalid-magnet recovery (79), system theme (24), All day (80), empty preview allocation (83), related footer grouping (48), and removed application menu (85). Caption grouping advice (41) was weighed against the explicit LabForms ruling. Horizontal scrollbar clipping was not treated as a broken separator. Later engine integration addresses the unsupported preference fields reported in 84; actual behavior remains untested. |
| Settled schedule/navigation review | Fixed engine-invalid magnets clearing editable input, obsolete application-menu clauses, and focus restoration checking the wrapper instead of AddForm. Source re-review passed. No rendered schedule acceptance is claimed. |
| Current background integration | Fixed Open falsely acknowledging during Exit, disconnected-live-window readiness without a second launch, human prompts triggering a false timeout, and a retained Open blocked by an old deadline after clean teardown. Scoped re-review passed. |
| Current details integration | Fixed unrestricted listeners/port discovery before saved adapter policy, seeding-time counting all files instead of completed selected content, and ambiguous midnight-to-midnight descriptions. Scoped re-review passed. |
| Current native file safety | Reserved the operation before async commit to close the Add race; cleared only new known-no-op collision markers and refused deletion of unresolved moves; retained all group markers until final commit to protect against libtorrent rollback; invalidated stale piece claims until a verified destination checkpoint; required explicit Use files there before replacing an interrupted marker. All five findings were corrected; scoped re-review passed. |
| Current file dialogs | Added actionable EN/ES recovery steps; acknowledged native picker waiting during Exit; removed protocol JSON from changed shared-scope feedback; gave destination sharing its own outside-owner/choose-another-folder guidance. All four findings were corrected; scoped re-review passed. |
| Subsequent completion audit | Found that shared recovery text incorrectly suggested Move files after Add/priority failures and while an uncertain move disabled that command. Generic, interrupted, and uncertain cases now have distinct guidance. Local source review, builds, catalogue checks and syntax check passed; this correction has not had a fresh adversarial or runtime check. |
| Advisory follow-up | Confirmed sixteen engine issue closures and the existing UI source corrections. Completion notices and row completion still precede the disk-flush acknowledgement; the reported hash lag was not independently reproduced. Caption grouping remains a source concern. Issues 16, 17 and 61 retain narrowed structural work. The bounded main-window capture does not verify Settings or populated inspectors. |

The current file reviews were fresh Astra reviews of the settled changes. Their
clean source results do not substitute for the unexecuted data-loss checks.
No TableView source changed in the schedule, background, details, or file pass.
Milestone 2's small setup-only row reorder predicate remains the reported library
change, accessed through the public API.

## Earlier handover's remaining checks (historical)

This list predates the completion evidence above; it is retained as history,
not the current release checklist. These were not requests to unlock the computer or approve a launch.
Before any data-loss journey, use disposable torrent payloads and an isolated
store; avoid driving a running personal engine through the shared logon endpoint.

1. Review the candidate at realistic window sizes and scaling. Check Settings
   reachability, unified caption/content, NavigationView focus, and the weekly
   timeline with actual saved daytime, overnight, overlapping, and all-day periods.
   Confirm that switching Off preserves the readable saved week and segment Edit
   changes the intended period.
2. Check current Open/Close/Exit and engine restart. Leave a draft prompt or folder
   picker open longer than 30 seconds; Keep editing must cancel Exit, and Discard
   must allow it to finish. Test ordinary Close followed by immediate tray Open.
3. Inspect the actual tray: readable status, maximum three commands, correct
   session Pause/Resume, Show window, double-click and Exit. Observe notification
   delivery with WinUI closed and a failed tray command with WinUI open. Exercise
   actual mains/battery idle-sleep behavior separately; the earlier power counter
   was not observed.
4. Exercise General, Files, Peers, Trackers, Pieces and Speed with a real local
   transfer. Fail a file/tracker save and retain the actual draft; restart and
   verify committed choices. Close WinUI during transfer, reopen Speed, and inspect
   the intervening history. SettingsPolicy/CommittedFiles already passed; repeat
   only when subsequent changes affect the failures those checks cover.
5. Switch EN/ES and theme with those surfaces and drafts already open; check
   retained input, names, focus, actual Narrator speech, high contrast, text scaling
   and the required temporary RTL/long-text exercise. English/Spanish catalogue
   consistency alone does not prove these interactions.
6. FilesSafety already passed against the candidate. Review real collision refusal,
   same-path Use files there, partially shared cross-seeds, cross-volume failure,
   a real interrupted move and restart. Confirm outside torrents and unrelated
   sentinels are untouched; confirm an interrupted torrent stays paused and is
   reverified before transfer. Unknown disk outcomes and late/dropped alerts still
   need behavioral evidence.
7. Exercise registered Explorer batch opening and physical drag/drop separately
   from the already recorded native activation and control-pattern checks.

After the affected runtime gates pass, obtain the required settled adversarial
review, commit milestone 3, then milestone 4, then milestone 5 with change-only
messages, and update this report with those commits and results. No completion
commit is justified yet. All previously launched review-owned processes were
recorded closed; no user-owned process has been closed during the deferred work.
