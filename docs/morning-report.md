# Morning report

## Current continuation — 2026-10-05

The exact current goal is in [handover.md](handover.md#current-goal). Work has
resumed; milestones 3–5 remain open. The entries below this section describe
earlier candidates and are not evidence for the owner's engine refactor.

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
  UI and engine processes closed. The fresh milestone reviews and the two
  mandated current-engine safety checks remain ahead.

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

## Remaining checks and what to try by hand

These are pending work, not requests to unlock the computer or approve a launch.
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
