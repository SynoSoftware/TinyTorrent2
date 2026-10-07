# Morning report

## Destination recovery and a confirmed Debug assertion — 2026-10-06

#22's engine refusal was reproduced in the Add form. The old presentation
duplicated the destination error in the source preview and bottom bar, displaced
Options content, and left focus on Cancel. The correction uses the existing
status line under Download folder, clears only that refusal on folder edits,
and restores the native editor's focus. Engine validation remains authoritative;
the wire code is retained on CommandFailure rather than inferred from translated
text. Automatic-add reporting and unrelated source errors keep their existing
owners. No accepted screen was restyled.

All twelve English/Spanish, Light/Dark, requested 720x560, 1040x680 and 1280x800
journeys retained the rejected folder and source, focused EditableText, and
cleared the error after correction. One corrected journey successfully retried
Add paused. Evidence is in
`artifacts/evidence/UiSelfCapture-2b8ed539-cd2c-47b4-a747-482908ffbdb5/captures`.
The real refusal before correction is in
`artifacts/evidence/UiSelfCapture-5f3f337c-73f8-407d-b666-4fc8bf6a17ef/captures`.
The accepted minimum window size clamps the requested 720 case: actual client
width is 837 in English and 863 in Spanish. The other client widths are 1024
and 1264. No narrower-window support is claimed.

The fresh capture-only reviewer examined all 24 refusal/correction images and
found no counted visual defect. The existing single-line policy retains full
text through selection, tooltip and automation name. Physical keyboard input,
Narrator, High Contrast and reopening a retained failed draft remain unverified.
A final source-reviewed focus priority preserves magnet validation before an
older destination refusal; that combined-error case was not separately run.
No accepted screen was restyled and no general Add metadata transition is
claimed verified by this scoped review.

`artifacts/destination-after.log` records Debug/x64 capture compilation in
42.37 seconds; `artifacts/destination-final.log` records ordinary Debug/x64
compilation after the focus correction in 43.97 seconds. Both have zero warnings
and errors, compile the app and reuse both libraries. All launched processes
are closed and the generated-output scan is empty. The engine diff is unchanged;
the existing FilesSafety and CheckpointRetry passes remain current. No suite ran.

That successful retry exposed a separate, repeatable dependency assertion,
tracked in #140. With Pause all active, accepting an unknown-metadata magnet
crashes the Debug engine at libtorrent's `session_impl.cpp:3754`,
`t.want_tick()`. `torrent::set_paused` changes its per-torrent pause flag while
the session is already paused and returns before refreshing the tick list.
The app calls the normal `torrent_handle.pause()` API. The same assertion
appeared in two UI runs and a minimized headless reproduction.

Three short probes reused the existing binaries, without compiling the engine:

- Debug, session paused: fails in
  `artifacts/evidence/MagnetAssertion-8c7a7909-fae2-40a7-b6e2-5fdfd28dc555`.
- Debug, session running and torrent added paused: passes in
  `artifacts/evidence/MagnetAssertion-a8a74023-b39b-4fe3-a8d9-a4bfb689dc8a`.
- Release, session paused: passes in
  `artifacts/evidence/MagnetAssertion-b738a582-4b0c-4148-9901-c7cbea6f79b9`.

The probes check accepted membership through snapshots for three seconds and
normal Exit. They do not prove later transfer behavior. The disposable script is
`V:/temp/TinyTorrentMagnetCheck.ps1`; assertion stderr is retained in each evidence
folder. Dependencies stay untouched under the owner's ruling. No app pause
workaround or assertion suppression was added. UI correction captures use a
running session with individually paused torrents; they do not claim #140 fixed.

#139 is closed as unconfirmed after source review: folder controls follow a new
file tree, horizontal scrolling follows deliberate viewport/column changes,
InfoBars have no changing Title caller, and dialog button sets are established
when opened. No additional mechanism or test is justified without a concrete
displaced interaction. Actual Add/file-operation transitions retain their own
issues. This is a source disposition, not fresh runtime/accessibility evidence.

## Connection announcements and report triage — 2026-10-06

Magnet-validation stability is committed as `6be4a69`. The remaining #20
announcement correction uses the native InfoBar: an open connection bar with
a changed nonempty message closes/reopens synchronously, as
[Microsoft documents](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/infobar#updating-the-infobar).
The existing notification owner registers the callback; there is no second
announcement path or saved message state. Closed bars and cleared text do not
reopen. Source review and ordinary Debug/x64 compilation pass in 45.26 seconds
with zero warnings/errors (`artifacts/connection-announcement-app.log`), reusing
both libraries. The output-location scan is empty. No additional UI journey or
test ran for this native event wiring; actual Narrator delivery remains unverified.

The startup-warning claim in #20 is outdated: ordinary startup is cloaked until
the first usable frame, with a failure revealing its recovery surface. The
capture mode deliberately bypasses that cloak. No second startup timer or
notification policy was added. Keep #20 open for physical announcement evidence.

#52 is closed as an unnecessary AI requirement: native Enter and Escape already
supply the Add/Cancel footer routes, alongside access keys for working controls.
User and keyboard perspectives favour those familiar keys; the developer and
WinUI perspectives favour retaining native dialog behavior without extra
shortcut plumbing. This is a source/design disposition, not new physical-input
evidence. Reopen for a reproduced broken native route.

Sixteen GitHub reports remain open, including the eight new investigations and
the narrowed #20, #22, #113, #121, #125 and #128–130. The last two commits' layout
fixes narrow #132 and #136 rather than claiming every case in either is fixed.
Whole-product release acceptance remains open. Existing engine safety passes
remain current; no engine edit followed them in this continuation.

## Magnet validation and current engine safety — 2026-10-06

Command-feedback milestone is committed as `e584b91`. The confirmed part of
#136 is now corrected: the inline magnet error precedes the existing action
row, keeping Paste/Preview at the bottom of the pane. No reserved error slab,
new control, styling or command implementation was added. The owner's QueueTop
addition and other concurrent work are preserved separately.

`artifacts/magnet-layout-after.log` passes Debug/x64 capture compilation in
49.75 seconds with zero warnings/errors; only the app compiled. In
`artifacts/evidence/UiSelfCapture-43cdd2bf-33e2-46c7-8273-35f827c24d5c/captures`,
all 12 English/Spanish, Light/Dark, requested-size combinations retain Preview's
position after native invalid-input submission (y=349, 469 or 589). Rejected
input remains editable and retained; the existing valid-preview recovery
journey also passes. Earlier current evidence moved the narrow actions 53
pixels. Sequential task reviews and the independent capture-only reviewer found
no counted defect in the changed surface. Minimum-width error text trims under
the owner ruling; the existing shared message remains selectable with its full
text. Physical keyboard, screen-reader and High Contrast verification remain
open. #136's other metadata/refusal transitions remain investigations.

The requested engine safety checks each passed once against the current engine,
including the owner's uncommitted queue changes:

- FilesSafety: `artifacts/evidence/FilesSafety-b2725e21-f46e-461a-9723-fb4420e281d0`.
  Shared-owner protection, collision refusal, grouped move, interrupted-move
  recovery and deletion preserve the expected payload and unrelated bytes.
- CheckpointRetry:
  `artifacts/evidence/CheckpointRetry-06e016b4-0379-443e-ae88-e5d99670fb23`.
  A blocked checkpoint reports failure, retries after release, and preserves
  membership and running intent across restart.

`artifacts/release-safety-engine.log` passes Release/x64 in 25.33 seconds with
zero warnings/errors. Its 16 compiled source files correspond to changes since
the previous Release build and consumers of the changed State header; dependency
libraries were reused. The engine diff stayed unchanged through both checks.
All launched processes closed and every output-location scan was empty. Do not
repeat these checks for later app-only or documentation work; a later relevant
engine change invalidates this evidence. No full suite or real-transfer peer ran.
The release goal is still active; these scoped passes do not close the remaining
desktop, accessibility, live-download and worklist investigations.

## Command-feedback milestone — 2026-10-06

`3d3f977` committed the hidden-sort correction and its two focused checks. The
next confirmed part of AI report #132 is command-error reflow: current baseline
`artifacts/evidence/UiSelfCapture-e7104154-9065-4e06-ab22-74ba11a70650/captures`
shows the inspector moving upward 45 pixels at 1040x680 and 57 at 1280x800.
At minimum width, the table minimum instead forces the inspector to shrink.

The existing command-error InfoBar now shares the connection bar's bottom
workspace overlay. One stack separates simultaneous messages; their existing
owners, commands and dismissal remain unchanged. No other footer state or
accepted screen was restyled. Before/after compilation reused the libraries;
the settled Debug/x64 capture build passed in 47.82 seconds, with no warnings
or errors (`artifacts/release-layout-after.log`). No engine build or suite ran.

After evidence is
`artifacts/evidence/UiSelfCapture-adfe7221-e1c4-41b6-bc5f-e4d83f1c9e0d/captures`.
All 12 language/theme/size combinations retain the table height, inspector
position and height, and footer position when the command error appears.
Native close-button invocation clears the error in all 12 and leaves the
underlying connection state unchanged. Messages are simulated; the isolated
engine stays connected. This is not a real-disconnection recovery check.
The accepted native minimum exceeds the requested 720 width; captures record
837-pixel English and 863-pixel Spanish client widths at that size request.

Sequential user, keyboard, accessibility, Fluent and UX reviews found no new
counted defect in this changed surface. The fresh visual reviewer agrees after
examining all 24 command/combined-error images. Eleven show both bars; English
Light 1040x680 contains only the command error because a model refresh cleared
the simulated connection state. That simultaneous-state image remains
unverified. The earlier diagnostic run failed for that same simulation mistake;
its assertion was corrected to check the actual connection state rather than
a manually forced view property. Physical keyboard use, screen-reader output
and High Contrast remain unverified. All launched processes closed; the
required output-location scan was empty.

The same current baseline reproduces #136: Spanish minimum-width invalid-magnet
feedback moves Paste/Preview upward 53 pixels. The owner's Add work is preserved;
that correction is separate. #132 remains open for its other footer transitions.
New AI reports still require evidence rather than automatic implementation.
The release goal remains active, including final engine safety checks after
the concurrent engine work settles and the previously recorded release gaps.

## Release goal resumed — 2026-10-06

The release goal is active. The earlier scoped acceptance does not establish
whole-product release readiness. `89570ea` checkpoints the owner's desktop
identity, registration, protocol and inspector changes. Further owner edits are
in progress in Add, Pieces and the engine; preserve them. The older statement
that no engine changed after FilesSafety/CheckpointRetry is no longer current.
Run those two checks once after the final engine change, not between these edits.

GitHub's 34 previously open reports were reconciled: 24 resolved or superseded,
10 retained with narrower implementation or verification scope. Local completion
is distinguished from publication. New AI-authored reports #132–139 are
investigation candidates, not owner requirements or established regressions.
The current contracts and owner rulings govern their disposition.

The confirmed #56 defect is corrected at TableView's existing sort validator.
Explicit sorting by an effectively hidden column now throws before changing
sort, row order, selection or layout events; initial schema capture uses that
same validator. Defensive saved-layout restoration is unchanged. Runtime
CellPadding updates remain outside the selected scope.

`hidden-sort-build.log` passed Debug/x64 compilation of TableView and its test
host, reusing Lucide. A concurrent MSBuild briefly locked the library output;
the existing copy retry succeeded. Only two targeted tests ran: rejection with
unchanged view/layout roundtrip, and initial rejection after a pending layout
hides the requested column. Both passed in about one second; results are in
`artifacts/TestResults/hidden-sort/hidden-sort.trx`. These checks catch an
invisible ordering state and a layout roundtrip that silently changes it, which
the compiler cannot detect. No full suite or product launch was performed.

Initial triage of the new layout reports:

- #132: the command error, torrent reason and filter label still occupy footer
  rows; measure their effect at fixed dimensions. The connection overlay already
  has evidence of stable workspace bounds; preserve it.
- #133–134: file-operation recovery and Settings feedback need fixed-size
  before/after bounds, not a blanket replacement of Auto rows or inline errors.
- #135: the latest Pieces heading/detail changes supersede parts of the report;
  live legend wrapping and Speed inspection still need current measurement.
- #136: retained `UiSelfCapture-0d08f16f-281a-46e7-9120-46c3c1ce316d` evidence
  shows the Spanish narrow magnet error shrinking the editor from 149 to 96
  pixels and moving Paste/Preview from y=337 to y=284. Destination and dialog
  action bounds stay unchanged. This proves that earlier failure, not acceptance
  of the owner's in-progress Add changes. Reproduce on the current build before
  editing the surface.
- #137: distinguish live status changes from switching torrents, and preserve
  editable multiline input. Full values must remain accessible when trimmed.
- #138–139: explicit edit/expand/resize transitions are not automatically
  defects. Measure routine feedback separately; the week pointer overlay and
  existing table scroll-offset protections do not justify replacement.

The owner's product processes remain running and were not interrupted. Current
self-capture and isolated engine checks must wait until the engine connection
is available; there is no new visual acceptance, desktop integration proof or
release approval in this checkpoint.

## Ownership and naming polish — 2026-10-06

Reviewed `203ca3f` and `c0353b7` against the current repository rules. The Settings
departure implementation stays unchanged: `Preferences` owns the save path and
the shared decision to leave. Capture edits now read each field's own section;
Pieces evidence uses the model's counts; `ReadPriorities` names its file read.
Library and synthetic Pieces captures share `CaptureMatrix` for language, theme
and sizing, with current native minimum width read after layout. Other older
capture journeys have similar loops; they are outside this scoped cleanup.

Independent source review passed. `capture-polish-app.log` passed Debug/x64
capture compilation in 60.64 seconds with zero warnings/errors, only the app's
two WinUI compilation passes and no source changes during the build. The output
scan was empty. No product behavior changed, so no journey, visual review or test
suite was repeated. Concurrent Pieces, resource, registration and contract edits
are outside this review and commit. No new type, framework or comment was added.

## Release continuation accepted with recorded limits — 2026-10-06

The final adversarial worklist reviewer accepts the reviewed milestones 3–5
continuation with no remaining concrete blocker. Its Settings finding is fixed
in `203ca3f`; its required bulk-priority and mixed/aggregate Pieces evidence is
now complete. This supersedes the pending status in earlier checkpoints below.
The review establishes the recorded scope, not unperformed desktop operations or
a distribution release. The owner-conventions checkpoint is `46e5a01`.

`LibraryCapture-88080534-8c7b-4f9e-843d-cc7bf429e73e` passed in 37.053 seconds with
72 scenes. Native selection of a folder, an overlapping child and an outside file
followed by the real High menu action persisted exactly `[7,7,7,4,4,4,4,4]`.
Normal restored the original priorities. Existing collapse/expand, search,
filter and header-focus journeys passed; all 300 torrents and 307 payload hashes
were retained. UI exit was zero and both owned processes closed.

Twenty-four additional images exercise the production Pieces model and renderer
with synthetic mixed states, partial progress and 20,000 aggregated pieces.
Independent image review in EN/ES, Light/Dark and all three requested sizes found
zero counted defects: legend/counts, patterns, progress and minority dots remain
readable and contained. These fixtures establish presentation; the live torrent
header belongs to another fixture and the images do not establish engine state
accuracy. No product layout or style changed for this evidence.

Requested 720x560 scenes use the accepted title-bar minimum: current synthetic
captures have client widths 837 in English and 863 in Spanish at scale 1.
Requested 1040x680 and 1280x800 yield client areas 1024x671 and 1264x791. These are
supported narrow-window reviews, not proof of a literal 720-pixel client width.

The final review disposed of the remaining worklist as follows. An unverified
check is not a newly discovered defect and does not justify speculative changes.

| Scope | Disposition and remaining limit |
| --- | --- |
| Background and desktop behavior | Existing close/recovery and registration observation evidence retained. Physical caption gestures, splash/foreground timing, Windows notification delivery/suppression, sleep/logon, registration repair/other-copy states and Windows default choice remain unverified. No concrete source or journey failure remains. |
| Details and preferences | Canonical search/Settings routes, schedule and ordinary edit recovery pass; late-field departure corrected. Populated Files/Trackers, prior live Peers/Speed evidence and current synthetic Pieces review retained. Physical keys, Narrator, OS High Contrast and scaling remain unverified; captures cannot establish them. |
| Move and delete files | Native submissions, collision/shared-file outcomes and final current-engine FilesSafety/CheckpointRetry passes retained. Physical picker/player integration, cross-volume/device failures, dropped alerts and remaining real-download marking/launch cases remain unverified. No additional mechanism is warranted by an observed failure. |
| Table hierarchy | Exact bulk targets and collapse reconciliation now verified. Comparable before/after flat-table performance and physical tree interaction remain unverified as permitted by the hierarchy contract. No measured slowdown is claimed or dismissed. |

No engine changed after its final safety passes. No full suite, dependency build,
owner-data mutation or external desktop automation ran. The ordinary app build
and focused source/runtime evidence are recorded below; all launched processes
are closed. Packaging and Distribution remain outside this task.

## Settings departure correction — 2026-10-06

Adversarial review found a concrete loss of an ordinary edit: Download could
change while the departure loop awaited Upload, after Download had already been
visited. The first navigation then hid the unapplied value. Departure now revisits
only fields that still cannot leave, using the same acceptance predicate as an
individual field. Existing offline and refusal behavior is preserved. Scoped
source re-review found zero remaining defects; no screen was restyled.

`UiSelfCapture-032e8aa2-fbf1-4798-af6d-9ad2ea0ddfef` passed in 6.404 seconds.
Its native Download editor changed to 161 when Upload began saving 32. The first
navigation applied both values with no prompt, draft or pending save. All eleven
edit journeys passed, including invalid input, pending acknowledgements and
schedule Save/Discard/Cancel. Original Download, Upload, port and periods were
restored; UI exit was zero and both owned processes closed.

`departure-pieces-corrected-app.log` passed capture compilation in 46.72 seconds;
`release-readiness-product-app.log` passed ordinary Debug/x64 compilation in
40.56 seconds. Both had zero warnings/errors and compiled only the app's two
WinUI passes, reusing current libraries. Source hashes stayed stable and the
Everything output-path scans were empty. The initial diagnostic build failed
on two span comparisons crossing an await; awaiting the arrays before comparing
them corrected the capture code. No engine build or full suite ran.

The selected-row stripe remains removed in `b1a21e0` and in this ordinary build.
Its historical contrast rationale does not override the owner's rejection.

## Checkpoint summary — 2026-10-06

Committed coherent steps: Files hierarchy `d23778a`, registration observation
`1706002`, desktop startup `dbb67d1`, and Pieces presentation `962f72a`. The final
owner-conventions checkpoint preserves Ctrl+comma for Settings, its displayed
shortcut, Windows-first guidance and the Debug/free-artifacts-lane build rules.
The shortcut still calls the existing Settings command. It is compiled and
source-checked; physical key delivery remains unverified. No additional build or
test was needed for these already compiled lines and documentation.

The release goal remains active. Next evidence must address real remaining gaps:
current mixed/aggregate Pieces rendering; exact bulk file-priority targets;
flat-table performance comparison; registration repair/other-copy states and
Windows choice/logon; physical desktop, accessibility and scaling; and the
previously recorded live-download/file-lifecycle cases. Current source, capture
and safety passes establish only their recorded scope. Do not rerun the full
suite or repeat the completed final file-safety checks for app-only changes.

## Pieces presentation checkpoint — 2026-10-06

The owner's Pieces changes share one palette between legend and map, align legend
entries when wrapping, and use the available map cells for near-equal contiguous
ranges. The accepted filled tiles and corner dots replace outlines and triangles
in ordinary themes; High Contrast retains system-color outlines. Source review
found zero concrete blockers and the current capture build includes these files.

The twelve Pieces views from
`LibraryCapture-5cc7b53c-434a-4cf1-9f43-d46f9adf9213` passed independent visual review
in EN/ES, Light/Dark and all three sizes. This is one missing piece with no peers:
mixed/aggregate states, progress fills and High Contrast still need current
rendered evidence. No live transfer or broad test was run for this presentation
checkpoint. Startup is committed as `dbb67d1`.

## Desktop startup checkpoint — 2026-10-06

Registration is committed as `1706002`. The owner's settled startup changes keep
the settings completion ahead of transfer loading, show a cold splash promptly,
and use recent warm-launch durations to avoid an unnecessary splash dwell.
The window's pipe client gives its own newly started engine five seconds before
showing a connection failure. This is a separate checkpoint from registration.

These sources passed the previously recorded scoped source review and are in
the current successful app/engine builds. The recorded Restart journey and the
latest FilesSafety/CheckpointRetry runs cover startup with saved state. They do
not prove visible splash timing, foreground behavior or the window's cold-start
failure presentation. Those remain explicit desktop verification gaps; no extra
build or repeated check was run merely to commit unchanged, compiled sources.

## Registration observation and final engine safety — 2026-10-06

The Files hierarchy checkpoint is committed as `d23778a`. Registration source
review found zero everyday functional blockers: engine registration remains the
single owner, both this-copy and other-copy entries count as on, and Settings
refreshes observation rather than storing another preference. The repair comment
now acknowledges partial writes; no runtime repair behavior changed in review.

`registration-capture-app.log` passed Debug/x64 in 61.55 seconds with zero warnings
or errors, compiling only the app's two WinUI passes. Source hashes stayed stable
and the output-path scan was empty. Initial run
`UiSelfCapture-9a080fd0-4550-4ea9-b32b-600cb040c9fb` passed, but its top/bottom views
missed the narrow registration controls. Targeted Startup and Default app views
close that coverage gap without changing product layout.

`UiSelfCapture-2b863117-f4fb-41ce-a6de-22a156b58197` passed in 29.387 seconds.
The engine observed both registrations as `none`; both native switches were off
and enabled, matching that observation. The notification toggle saved and restored
its value; completion feedback postponed during editing, returned afterwards and
expired. Independent image review found zero counted defects across all 24
targeted EN/ES Light/Dark registration views at the three supported sizes. UI exit
was zero and both owned processes closed. No Windows registration was changed.

`registration-final-engine.log` passed Release/x64 in 25.58 seconds, zero warnings
or errors. Only Registration.cpp, Main.cpp and Application.cpp compiled; the last
includes Registration.h through its own header. Engine source hashes stayed
stable and the output-path scan was empty. The engine SHA256 is
`0C3A31A2F52AD3CFA8F7CBBDB205C7C344DB66865A620C5B6B633CD8726355E4`.
FilesSafety passed once in `FilesSafety-163b2c97-add0-48e1-892e-a5b40187ec70` and
CheckpointRetry passed once in `CheckpointRetry-f52b8197-1b52-468c-b69d-680102a32767`.
Both used disposable stores, closed their engines and left the output-path scan
empty. No full suite, transfer peer or dependency build ran.

Registration writes/repair, other-copy caution states, actual Windows default-app
choice and sign-in remain unverified at runtime. These captures establish the
observed unregistered state only; they do not establish physical keys, Narrator
or High Contrast. Keep those release gaps explicit rather than treating source
review or file-safety checks as desktop integration approval.

## Files hierarchy checkpoint — 2026-10-06

The owner's TableView hierarchy and shared Files integration are ready for a
coherent checkpoint. Source review found zero counted defects across the library,
Files host, sample and focused checks. The selected-row stripe remains absent.
The priority-width and metadata-ready Add-preview corrections described below
belong to this same integration.

`hierarchy-journey-app.log` passed Debug/x64 capture compilation in 47.30 seconds
with zero warnings or errors. Only the app compiled, in its two WinUI passes;
TableView and Lucide were current. Source hashes were unchanged during the build
and the Everything output-path check was empty. The owner's current
`files-table-tests-build.log` already compiled all four hierarchy checks in
27.00 seconds; that test host was not launched or rebuilt.

`LibraryCapture-5cc7b53c-434a-4cf1-9f43-d46f9adf9213` passed in 25.879 seconds.
Native automation providers selected a child and an outside file, collapsed the
folder, and expanded it again. Collapse hid descendants, removed the hidden child
from selection, retained the outside file and moved current to the folder.
Expansion restored descendants without reselecting them or changing priorities.
The existing filter, named-torrent search and LTR/RTL header-focus journeys also
passed. All 300 paused torrents and 307 payload hashes were retained; UI exit was
zero and both owned processes closed. No transfer peer or full suite ran.

The run produced 48 scenes at the three supported sizes in EN/ES and Light/Dark.
An independent image-only reviewer inspected all twelve Files and twelve Pieces
views plus representative search/filter scenes and found zero counted defects.
Pieces evidence covers one missing piece with no peers, not mixed states or
aggregation. Physical keyboard input, Narrator, actual High Contrast, exact bulk
file-priority targets and flat-table performance comparison remain unverified.
This checkpoint does not close whole-product release acceptance.

## Files review and theme-close correction — 2026-10-06

The selected-row stripe remains removed in `b1a21e0`; current source has not
reintroduced it. The owner's current Files integration received a bounded
source review. Its model-replacement findings were corrected concurrently by
the owner before the capture build: Roots now uses OneWay binding, and the
model callback transfers subscriptions and clears obsolete selection. The
successful capture therefore includes the corrections, not a pre-fix baseline.

`UiSelfCapture-05dddca8-5dc0-4b5d-8561-20b40b4b9091` passed in 10.218 seconds:
an empty Add dialog acquired a local one-byte torrent preview through the real
engine, displayed its file in the current table, and cancelled normally. The
existing native priority save/restore and tracker journeys also passed. This
bounded preview check catches a stale file list that compilation cannot detect;
it starts no payload transfer. Its Capture.cs addition remains with the owner's
pending hierarchy work because it expects that new table-based Files control.

The fresh Files image review found one defect: the selected priority text was
hard-clipped in both languages at every size. Increasing that column's default
width from 150 to 200 makes “Do not download” and “No descargar” readable.
The first correction review inspected all twelve variants from
`UiSelfCapture-c63635f3-3f8e-41c6-9269-bc21e56243f9` and found zero remaining
counted defects in that scope. The width change stays with the pending Files
integration, without changing its controls or styling. Dropdown popup images
were black, so option pixels remain unverified; this is not evidence that the
real dropdown is blank. Physical input, Narrator and High Contrast remain gaps.

The initial Files matrix falsely failed its final comparison because it
deliberately changed the theme from system to light, then compared every
preference with the original snapshot. The diagnostic now restores the original
theme and language through their owners and records individual comparison
results. It still compares every field and priority; none is excluded.

That restoration exposed a real shutdown crash, twice: a queued ActualThemeChanged
callback entered UpdateColors after the native window was destroyed. WinApp's
debugger identified a NullReferenceException at Chrome.cs:94 during dispatcher
shutdown. Its log is `debug-9464-20261006-185902.log` under the local temporary
`winapp-dumps` directory. UpdateColors now uses the same `_allowClose` guard as
UpdateChrome. Cancelled closes still update normally. Source re-review found
zero defects; no new state or exception suppression was introduced.

The final run `UiSelfCapture-464e3040-2e1a-450e-9d94-fadc20e79d07` passed in
17.792 seconds, including all twelve Files scenes, exact preference/priority
retention, no remaining draft or pending operation, and process exit zero.
Earlier `c63635f3` and debugger run `4528c6f6` proved the state checks but crashed
during shutdown; do not count their process completion as passes.

Debug capture builds `files-preview-before-app.log`,
`files-priority-width-app.log` and `theme-close-app.log` passed in 57.40, 48.79
and 51.30 seconds with zero warnings/errors. Each compiled only the app's two
WinUI passes; libraries were up to date and source hashes stayed unchanged.
Output scans were empty and all launched app/engine processes closed. No suite,
Transfer peer or engine build ran. Earlier engine safety evidence predates the
owner's subsequent registration edits; this checkpoint does not verify those
changes or complete whole-product release acceptance.

This checkpoint commits the independent theme-close fix and the diagnostic's
appearance restoration. Preserve the owner's pending hierarchy/Files (including
the reviewed width), Pieces, registration, startup, shortcut and documentation
work. Do not commit a partial hierarchy implementation merely to include the
column width. The broader native desktop and release evidence gaps remain open.

## Current engine startup and safety evidence — 2026-10-06

The inspector/toolbar checkpoint is `74c35e9`. Read the current root instructions
before another build: Debug is the default, and occupied outputs require the
lowest free artifacts lane. The Release exception applies to Checks.ps1.

The owner's pending engine startup changes received a scoped source review.
No concrete regression was found. Store::Drain swaps out its completion queue,
so deferring resume reads from the settings completion really returns to the
desktop owner first. The relevant new runtime evidence is Restart; the two
file-safety checks below also satisfy the owner's explicit final-engine gate.

`startup-candidate-engine.log` passed in 1.00 second with zero warnings/errors;
all compilation, resources and linking were up to date. No native file or
dependency was rebuilt. Source hashes stayed unchanged. The checked Release
Engine.exe SHA-256 is
`F2780046329698648D7DF5084CFBD048774CAF45508BF6C3DBE50543714E14E3`.

All three existing checks passed once against that candidate:

- `Restart-c2de29e5-be04-412c-b239-729119f5c659`: restored membership, identity,
  saved pause/session intent, appearance and download-order choices, including
  damaged-resume fallback through the changed startup sequence.
- `FilesSafety-36ceea31-8697-4e61-84d1-24af9aa63bd9`: the existing disposable
  file-operation safety workflow.
- `CheckpointRetry-f134e84d-b826-481b-befd-d378dace79a3`: failed checkpoint
  recovery and durable identity/intent after restart.

All used disposable stores. No Transfer peer, full suite, application build or
desktop automation ran. Output scans after the build and each check were empty;
the final process query found no Engine.exe or TinyTorrent.exe. The owner's
Debug instance was closed through its normal window/engine Exit paths using the
standing authorization, without forced termination.

These checks do not observe native splash visibility, minimum dwell or warm
suppression, and they do not validate the concurrent file-tree/Pieces changes.
Keep the engine and other owner edits intact; this evidence checkpoint does not
commit their unfinished work or establish complete release readiness. Do not
repeat these checks unless later engine changes affect their proof.

## Inspector viewport and toolbar checkpoint — 2026-10-06

Tracker data correction `609bbc5` is committed. The subsequent narrow capture
finding is fixed: inspector sizing now uses the bounded outer workspace height,
subtracts the visible toolbar and retains the existing table/splitter reserve.
Workspace resizing, toolbar measurement and visibility changes update that one
calculation. This preserves the owner's toolbar and inspector styling instead
of feeding an overflowing child grid's height back into its own allocation.

The owner's coherent toolbar/menu slice is included in this checkpoint: shared
selection command bindings, View/search visibility, saved placement, localized
menu access keys and menu access-key scopes. Source review found zero counted
defects in those dependencies or the sizing correction. Newer Ctrl+comma edits
arrived after the build and remain unstaged with the owner's other ongoing work.

`inspector-viewport-app.log` passed in 69.54 seconds with zero warnings/errors.
It compiled TableView after concurrent hierarchy edits and the capture app,
two WinUI passes each; source hashes did not change during compilation. The
paused isolated run `UiSelfCapture-fafe8ae7-c681-4001-bc0f-28d859ba577d` passed
in 11.71 seconds, retaining the exact tracker-save/reopen/restore proofs and
producing the twelve EN/ES Light/Dark views. Its UI and engine exited, and the
generated-output scan was empty. No engine build or suite ran.

Parent review confirmed the narrow inspector boundary and scrollbar above the
footer. The image-only adversarial review's first correction review reports zero
remaining findings: all four narrow variants are contained, supplied bottom-scroll
views show the remaining rows, and representative medium/large views retain their
layout. Physical keyboard navigation, real High Contrast/Narrator, toolbar state
across a real window restart and the owner's concurrent feature changes remain
outside this evidence. The interrupted ordinary build remains unverified; this
checkpoint uses the successful capture build and does not claim a current normal
Release executable or complete release readiness.

## Multiline tracker save milestone — 2026-10-06

The populated details journey exposed a product bug: WinUI's multiline TextBox
returned CR-only separators, and the tracker parser deleted them. Four entered
URLs became one concatenated URL in the disposable store. The failed run is
`UiSelfCapture-e9d66782-86da-49f5-a048-e782510e110b`; it recorded four CRs and
zero LFs. Inspector now normalizes input to LF at its existing draft boundary,
constructs the original text in that same form, and splits on LF when saving.
Opening an unchanged multiline list also no longer creates a false draft.

Source review found no remaining defect. `trackers-line-endings-app.log` passed
in 69.98 seconds with zero warnings/errors. It compiled the app and TableView's
two WinUI passes each: concurrent hierarchy sources arrived before the build.
Source hashes stayed unchanged throughout it. The focused run
`UiSelfCapture-44e1e715-9e3c-47b6-b3c0-ed5760bab8d6` passed in 13.37 seconds:
four exact URLs/two tiers saved, language/input/model/focus retained, untouched
reopening created no draft, and the original tracker list and preference were
restored. Its own UI/engine exited; output scans were empty. No suite ran and
no engine was rebuilt. This proves the app correction with the existing engine
binary, not the owner's subsequent engine edits or live tracker responses.

The fresh image-only reviewer inspected all twelve populated Trackers variants
and the draft. It found one narrow-layout defect: the inspector extends behind
the footer, hiding lower rows and its horizontal scrollbar. Captured bounds
confirm that the inner grid reports 486 DIPs while its workspace has only 397.
That correction is in progress separately; the tracker data fix is verified.
Requested 720 widths remain clamped to 837 English / 863 Spanish client widths.
Physical keyboard, Narrator and High Contrast remain unverified.

The ordinary `trackers-line-endings-release.log` stopped during TableView XAML
compilation without a completion record; its execution handle is gone and no
build controller remains. Do not count it as a pass. The successful capture
build supplies compilation evidence for this milestone. Concurrent owner
toolbar, Pieces, pipe, hierarchy and engine work remains separate.

## Native row selection milestone — 2026-10-06

The owner rejected the vertical selected-row stripe. It was already in initial
import `c2b9907`, attributed to SynoSoftware; this repository does not identify
its original writer. Its comment justified it using earlier low-contrast fill
measurements. The stripe and its obsolete visual state are now removed, leaving
native row selection fill, focus, drag opacity and UI Automation unchanged.
The table contract records the owner's decision, so a later review does not
reinstate the stripe. Three tests requiring that removed stripe were deleted;
their image helper moved unchanged to its remaining sort-arrow test consumer.

The existing `LibraryCapture-a1cd25b0-ee02-419f-8edd-a785bc528816` images and
the owner's screenshot establish the baseline. After capture
`LibraryCapture-88900975-9ad0-4603-8381-be00720e9deb` passed all 36 scenes in
37.20 seconds, with 300 members and 307 payload hashes unchanged. The parent
reviewed all twelve selected-row variants; native fill remains visible without
the stripe. A fresh image-only reviewer independently inspected those twelve,
four narrow inspectors, four filters, header focus and two baseline comparisons.
Its five-role review found zero counted defects. Physical input, actual High
Contrast and Narrator remain outside these images. Minimum width still clamps
the requested 720 sizes.

`selection-native-app.log` passed in 75.67 seconds, compiling only TableView
and the capture app's two WinUI passes each. `selection-test-compile.log` passed
in 45.16 seconds, compiling only the two test-project passes; the test host was
not launched and no suite ran. Both builds had zero warnings/errors. Output
location scans were empty and the disposable library processes exited. Source
and test-cleanup review found zero remaining findings.

The ordinary build `selection-native-release.log` failed in XAML pass 2 with
WMC9999 after 63.61 seconds. Concurrent owner edits changed MainWindow.xaml
during compilation; the generated MainWindow.g.cs was left empty. This is not
a successful ordinary build, and its output is not claimed current. No cache
or source was deleted to hide the failure. New owner toolbar/splash/design-doc
work is preserved outside this milestone. The stripe's passing capture build
predates that concurrent work.

Populated Trackers evidence is still in progress. Run
`UiSelfCapture-72cd3b41-6519-4ca5-b3a2-4b9ef9f5744e` stopped at the existing
compound language-switch assertion after replacing its one-line fixture with
four URLs across two tiers. It did not reach tracker Save. The pending diagnostic
now compares native TextBox text before/after the switch and records separate
input/model/focus outcomes and newline counts; it is reviewed but not yet built.
The final URL/tier assertion remains. If native input uses CR-only separators,
inspect the tracker parser before treating the failure as a harness-only issue.
Keep that pending Capture.cs/docs/testing.md work separate from this commit.

## Native caption checkpoint — 2026-10-06

The accepted owner UI is committed as `6809410`. The ordinary Release app has
also been rebuilt with the committed production fixes: `release-current-app.log`
passes in 54.70 seconds with zero warnings/errors. Only the app's two WinUI
compilation passes ran; TableView and Lucide were reused. Its copied Engine.exe
matches the already checked engine binary. The generated-output scan was empty.

The diagnostic shell journey now asks its own window for native hit
classifications at realized control centers and unused caption space. This
catches an interactive control becoming draggable without sending pointer input
or changing the owner's desktop. The helper keeps the existing `Capture` prefix;
`HitRegions` distinguishes this native check from image capture. Production
builds exclude it, and no product layout or behavior changed.

The first run, `UiSelfCapture-02b7a8c7-f60d-458f-b990-51399905ff60`, passed all
40 English hit queries, then exposed an incorrect existing harness assertion:
Resume was expected to make a torrent visibly active while global Pause all
remained on. The engine correctly saved its individual pause choice as false.
The corrected journey waits for the invoked command's localized acknowledgement
and checks the persisted individual choice while global pause remains true.
Source review caught an overly broad announcement wait; its correction-only
re-review reports zero remaining findings. That failed run is not a full pass.

The final `caption-shell-final-app.log` build passed in 66.59 seconds with zero
warnings/errors, compiling only the app's two WinUI passes. The earlier
`caption-shell-app.log` build (48.78 seconds) preceded the review's acknowledgement
filter correction; this extra compilation was needed for changed diagnostic code.
All output-location scans were empty; no engine build or suite was run.

Final run `UiSelfCapture-543c83b7-c2d1-4323-b60c-faa939fbd153` passed in 11.72
seconds. All 50 native queries matched: English Light/Dark at 1024 and 837 client
pixels, Spanish Dark at 863. The icon returned system-menu hit semantics, each
app control returned client semantics, and unused space returned caption
semantics. These widths reflect the accepted minimum, not exact-720 evidence.
Native menu invocation saved Resume=false and Pause=true while retaining global
pause. Recorded outcomes show Settings/About/back routes, theme switching,
retained filters after closing the drawer, and one matching search result.
Both disposable processes exited and the output-location scan was empty.

A Debug instance appeared during compilation, so the first launch attempt
refused to attach. Under the owner's existing authorization, its window was
closed normally and its engine sent normal Exit before the final run. Both
exited; the shutdown helper could not confirm the external process's exit code,
so that owner-session shutdown is not counted as a storage-check pass.

This closes native hit classification only. Physical dragging, double-click,
system-menu input, native caption-button actions, actual High Contrast/Narrator,
DPI/text scaling and the recorded live-download/filesystem gaps remain open.
The fresh source and supplied-capture reviews found no remaining counted defect;
their scope does not establish these unverified outcomes or distribution readiness.

## Accepted UI checkpoint — 2026-10-06

The owner's ten pending files retain the raised, rounded inspector, shared table
header band and short dividers, file-list cell alignment and header selection,
collapsed empty dialog title and simpler command suggestions. These are the
intentional changes described in the owner's attached report, not regressions.
They are included unchanged in this checkpoint.

The passing `schedule-feedback-app.log` build already includes these exact
source edits; none was changed after that build. Reusing it avoids another
compilation of unchanged code. Existing completed captures also include them:
Add `UiSelfCapture-0d08f16f-281a-46e7-9120-46c3c1ce316d` (12 cases), search
`UiSelfCapture-058843c3-dc47-4cf5-b1fd-3def26a6bc69` (18 completed routes/cases),
and Library `LibraryCapture-a1cd25b0-ee02-419f-8edd-a785bc528816` (36 cases).
Their manifests were re-read and all report no failure. The fresh visual gate
below covers these surfaces. No additional build, test or launch was needed.

The GitHub list was refreshed: the same 34 agent-filed issues remain open.
Their open state is not a defect verdict. Current source already separates
connection and command errors, supplies caption shortcut tooltips, routes
Settings search to its authoritative editor and sends notices to the open UI
instead of a Windows balloon. These traces do not prove physical keyboard or
Windows notification delivery. Schedule feedback is committed as `e462f14`.

A fresh functional reviewer then checked current source against milestones 3–5,
the active contracts and recorded outcomes. It found zero actionable blockers:
command routes share their existing owners; tray/activation/Exit and history
retain their lifecycle owners; all contracted preferences exist; failed
inspector edits retain drafts; Move/Delete preserve shared-file scope,
collisions and interrupted recovery. It also reviewed all ten pending owner
files and found no functional source reason to withhold their commit. This
closes the source gate without new tests. It does not turn the recorded native
desktop, accessibility, live-download and exceptional-filesystem gaps into
verified outcomes, nor establish distribution readiness.

## Fresh release capture review — 2026-10-06

A fresh reviewer saw only the goal, capture images and their manifests. It
reviewed Library/filter/inspector, Add, Settings, search, Schedule, Move/Delete,
live-traffic details and desktop recovery across the captured EN/ES, Light/Dark
and three-size combinations. It found one counted defect: a rejected schedule
save leaves the correction message above the visible editor and Save action.
There were no other counted visual findings in those supplied images.

The current baseline `UiSelfCapture-98faa2cc-31c0-468d-8f88-2bba3697af42`
confirms that defect. All twelve schedule cases and the exact-minute overnight,
rejected-save, cancel, move and remove journeys passed in 120.91 seconds. The
invalid draft and existing saved periods were retained. Both disposable
processes exited and the generated-output scan was empty. Behavioral success
did not make the missing visible rejection feedback acceptable.

This is a visual gate, not proof of complete feature correctness. Actual High
Contrast, Narrator, physical keyboard delivery, DPI/text scaling, populated
trackers and native caption/tray/picker/default-app behavior remain unverified.
Historical captures prove only their recorded scope; the newer partial Add and
timed-out broader Files run are not complete clean-exit passes. The full release
goal remains open. Selection cancellation is committed as `6b6163b`.

The correction moves the single existing error directly after the period editor
and reveals it after a rejected operation. It uses the shared body-text style,
keeps the full text selectable and accessible, and neither takes focus nor
scrolls on ordinary draft typing. Non-editor failures still use that same error.
No scheduling rule or accepted editor layout changed. The existing schedule
journey now captures each rejected-save viewport without moving the scroll
position for the image, so capture itself cannot hide a missing reveal.

- `artifacts/schedule-feedback-app.log`: 43.82 seconds, zero warnings/errors;
  only the app's two WinUI compilation passes ran, with TableView and Lucide
  reused. The output-location scan was empty.
- The settled source review found zero counted defects in bindings, error
  ownership, delayed reveal or focus retention. No code comments were added.
- Correction capture `UiSelfCapture-e2d0ce8c-c2ff-4ca2-8bad-d1a2bc3484c2`
  passed all twelve combinations and existing schedule journeys in 140.46
  seconds. All 88 frames were stable. No TinyTorrent or Engine process remained,
  and the generated-output scan was empty.
- The parent reviewed the twelve rejected-save viewports in sequence as a new
  user, a keyboard user, an accessibility reviewer, a Fluent designer and a
  clarity reviewer. The message is readable with Save visible in every case;
  source confirms focus is not moved. High Contrast and actual assistive-input
  behavior cannot be concluded from these pixels. No counted correction defect
  or further layout change was identified.
- The fresh adversarial reviewer's first correction-only re-review inspected
  all twelve validation images plus the initial rejection. It reports the
  finding resolved with zero remaining counted defects. No second correction
  round, engine build, engine check or full suite was needed.

## Selection cancellation milestone — 2026-10-06

The remaining concrete #54 gesture defects are corrected. A press captures the
selection's items, current row, range anchor and logical focus together.
Cancelling a marquee restores that checkpoint through the existing reconciliation
owner, so unavailable rows are pruned and replacement instances are resolved.
Escape, withdrawal, unload and system cancellation share this restore path.
Completion and capture loss retain the result; an explicit host selection still
wins. Deferred release uses the existing current-row resolver, so a removed or
replaced pressed row cannot become stale current selection.

The strengthened existing marquee check starts with different current and anchor
rows, cancels a rectangle, and checks both the restored current and the next
Shift range. Its old selected-items-only assertion missed this failure. The new
two-case `DeferredRelease` check covers removal and same-key replacement while
pressed, which source reconciliation alone did not protect from a later release.
Both checks call the existing private gesture operations in the native test host;
they do not inject pointer or keyboard input or claim physical gesture coverage.

- `artifacts/selection-cancel-tests-build.log`: 25.94 seconds, zero warnings or
  errors; TableView and its test host compiled, Lucide was reused.
- `artifacts/TestResults/selection-cancel/selection-cancel.trx`: all three cases
  passed, 2.03 seconds for the run.
- Four existing, previously unverified UIA selection checks passed in
  `artifacts/TestResults/selection-automation/selection-automation.trx`, 1.80
  seconds, using the same binary. They prove in-process native provider behavior
  for unrealized rows, replacement, Single/None modes and unavailable rows.
  This closes that bounded #40 evidence gap; Narrator and external UIA clients
  remain unverified. No new automation tests were added.
- `artifacts/selection-cancel-app.log`: 41.20 seconds, zero warnings or errors;
  only the app compiled, reusing the current TableView and Lucide outputs.
- Adversarial source review found zero counted defects. No layout or visual
  styling changed, so no new image matrix was justified. All test hosts exited;
  output-location scans were empty. No engine build, engine check, full suite
  or external desktop automation ran.

The prior keyboard/contrast milestone is committed as `245479e`. A fresh broader
capture review is now in progress; this selection checkpoint does not claim
whole-product release acceptance. Preserve the owner's concurrent UI diff.
Advisory #56's dynamic padding and programmatic hidden-column sorting are not
exposed application operations (the app sets only its initial Queue sort), so
they remain library concerns rather than expanding this release correction.

## Table keyboard and contrast milestone — 2026-10-06

TableView now reveals a focused header or cell through its existing horizontal
scroll owner while preserving native vertical scrolling. Header menu movement
follows the visual direction in RTL, and the Fit button uses its measured desired
width instead of a collapsed control's old arranged width. These are the concrete
source concerns from advisory #55; no competing navigation implementation was
introduced.

The custom caption template retains its accepted Light/Dark colors and 48-pixel
geometry. High Contrast hover/pressed states pair Windows Highlight with
HighlightText; disabled uses GrayText at full opacity. Main-table progress text
and status glyphs inherit the row foreground so selection can supply the correct
contrast color. This addresses the identified #47 source concerns, but actual
OS High Contrast rendering remains unverified. The owner's inspector card,
file-list, table-header and search changes are preserved outside this commit.

- One app build, `artifacts/table-accessibility-app.log`: 49.84 seconds, zero
  warnings/errors. Only TableView and the app compiled (their two WinUI passes);
  Lucide was reused. No engine or dependency build ran.
- `artifacts/evidence/LibraryCapture-a1cd25b0-ee02-419f-8edd-a785bc528816`
  passed all 36 scenes plus the LTR/RTL focus journeys in 56.94 seconds. Both
  focused last headers became fully visible at horizontal offset 412 without
  changing vertical scroll. All 300 torrents and 307 payload hashes survived.
  Native programmatic focus is evidence of reveal behavior, not physical keys.
- The Add/layout run
  `artifacts/evidence/UiSelfCapture-40224db4-1b89-4b25-864c-915f7358c2b5`
  reached its 180-second launcher bound during Spanish Light at 1280 width.
  It saved 69 capture manifests; six include a frame that did not stabilize
  within the per-frame bound. This is an incomplete matrix, not a passing run.
  Both English 1040-wide caption-state previews were saved and reviewed before
  the timeout. No product exception was observed before the launcher stopped it;
  the slower rendering has not been explained. Do not rerun the matrix merely
  to turn this report green. The earlier complete Add evidence remains below.
- Adversarial source re-review reports zero blocking findings. Independent
  visual correction review inspected 18 library images and the two caption-state
  previews, finding zero counted defects. Caption glyphs remain centered and
  readable with equal button geometry; selected-row percentage and status remain
  readable in both themes. This is a bounded correction gate, not whole-product
  release acceptance.
- Review-owned UI and engine processes are closed. Generated-output scans were
  empty after each run. No full suite, Transfer peer, external desktop input or
  change to the owner's Windows contrast theme was performed.

Remaining evidence includes physical keyboard/menu movement, native Fit-button
interaction, OS High Contrast, Narrator, DPI/text scaling and native caption
gestures. The full release goal stays open. No new code comments were added;
the new focus handlers use the platform event names and the existing scroll
authority. Keep the engine checks from `007dcde`; these UI edits do not invalidate
them. Add access-key and compact Spanish corrections are committed as `1eaf391`.

## Populated library and Add accessibility milestone — 2026-10-06

The unresolved library-provider exception did not recur against the current
build. `artifacts/evidence/LibraryCapture-16edd9f0-e7a4-4ddd-a5ba-854b8cf8b6cf`
passed all 36 scenes and native filtering/search journeys in 18.08 seconds:
Errors hides the rows without losing membership, search reveals item 300 and
clears the hiding filter, Paused restores the full library, and the eight-file
inspector loads. All 300 torrents and 307 payload hashes were preserved. No
speculative provider workaround was added; this pass does not explain the old
COM exception or prove physical keyboard behavior.

A fresh reviewer inspected all 37 library images, the earlier twelve search
result images and six navigation destinations, and compact/wide Add, inspector
and connection samples. Its one counted finding was the truncated Spanish
first/last-piece option at minimum width. The correction shortens the shared
Spanish label to “Priorizar primera y última pieza”; its off wording uses the
same verb. This preserves the owner's one-line rule, geometry and typography.
The same reviewer inspected the corrected compact Spanish Light/Dark images
and found zero remaining counted defects in that correction.

Add now declares eight localized native access keys: destination, Browse,
paused, sequential, first/last, magnet input, Paste and Preview. They are unique
within each language and follow the existing catalogue and live-binding owners.
Enter/Escape retain the dialog's existing Add/Cancel behavior. No custom key
dispatcher or second command implementation was added. This addresses the Add
portion of advisory #52; physical Alt delivery is not claimed as verified.

- One app build, `artifacts/add-accessibility-app.log`: 62.46 seconds, zero
  warnings/errors. The shared build-props change recompiles Lucide and both
  WinUI passes for TableView and the app; no engine or dependency build ran.
- `artifacts/evidence/UiSelfCapture-0d08f16f-281a-46e7-9120-46c3c1ce316d`:
  all twelve Add/layout cases passed in 37.87 seconds, including retained invalid
  magnet input and visible preview. Minimum widths remain as documented below.
- `2dc8406` removes commit-derived informational versions and Source Link from
  local builds, so documentation commits no longer change those compiler inputs.
  SDK property evaluation confirms both remain enabled with
  `ContinuousIntegrationBuild=true`. The build's generated versions are `1.0.0`;
  its obsolete Source Link files were removed by normal incremental cleanup.
  No extra compilation was run to measure the next-build saving.
- Review-owned processes closed, generated-output scans were empty, and no full
  suite, Transfer peer, external desktop input or distribution work ran.

The current GitHub read returned 34 open agent-filed advisories. Open status is
not a defect verdict: #119's competing editor and #125's queue-settling path
are already corrected. Two source-backed concerns remain for the next pass:
#55 still uses a collapsed Fit button's ActualWidth, does not reveal horizontally
clipped keyboard focus, and maps menu movement without RTL; #47 still has
background-only caption hover and opacity-only disabled styling in High Contrast.
Their user-visible runtime impact needs proportionate verification. Preserve the
owner's pending table-theme and App.xaml changes while correcting them.
This bounded visual gate is not final whole-product release acceptance.

## Search and minimum-width evidence checkpoint — 2026-10-06

File lifecycle and client defaults are committed as `007dcde`. The owner's
concurrent UI changes remain uncommitted and preserved. This checkpoint adds
evidence only; it does not change or approve every part of that design work.

The already-built capture app ran two isolated, offscreen reviews. No rebuild,
full suite, Transfer peer or external desktop input was needed. Both reviews
closed their UI and engine; generated-output scans found no stray output.

- `artifacts/evidence/UiSelfCapture-c5a0a06f-d9a8-4c3a-8e62-6e516246143f`:
  Add/layout capture passed in 37.98 seconds. All twelve header captures were
  inspected across English/Spanish, Light/Dark and the three requested sizes.
  The inspector card, table labels and caption groups remain distinct and fit.
  The overlay preserves workspace bounds; Add, magnet and theme actions retain
  their shared 48-pixel width. Minimum-width Add and long-magnet samples were
  also inspected; this is not a new full Add acceptance review.
- `artifacts/evidence/UiSelfCapture-058843c3-dc47-4cf5-b1fd-3def26a6bc69`:
  search passed in 17.45 seconds. Native result submission hides unavailable
  Properties, opens the selected inspector from Settings, routes speed limits
  from Search and the menu to the same Transfers editor, focuses the named port
  setting without changing its value, and reopens focused Search. All twelve
  localized search-result captures were inspected with no overlap finding.

The current interface contract reserves measured translated menus, a 200-pixel
Search and full caption actions. A requested 720-wide window therefore clamps
to 837 client pixels in English and 863 in Spanish on this machine. This is the
deliberate current minimum, not an exact-720 pass. Do not shrink accepted caption
controls to satisfy the older size request. The 1040/1280 requests produce
1024/1264 client widths.

The serial role review found no counted visual defect in the inspected samples:
the first-time user can find destination, magnet input and Add; the large-library
user has distinct search destinations and the checked navigation routes; the
accessibility review found full tooltips and names for truncated Add options,
with full incoming-status text retained in its tooltip; the Fluent review found
aligned groups and the accepted inspector hierarchy; the UX review found no
hidden primary action or overlap. Different title/column typography and rounded
inspector framing are the owner's intentional baseline, not taste findings to
reverse. Search labels wrapping at the minimum are preserved as owner work.

Limits: images and native provider invocation do not prove physical Ctrl+K,
300-torrent keyboard use, Narrator, High Contrast, DPI/text scaling, or native
caption hover, dragging and system-menu gestures. Those remain separate release
evidence gaps, alongside the file-lifecycle gaps below. No new UI correction or
fresh final whole-product adversarial approval is claimed by this checkpoint.

## File lifecycle and client defaults milestone — 2026-10-06

The #128/#129/#131 slice implements fixed behavior, not more Settings switches.
New absent files use `.!tt`; display names stay unchanged and `disk_path` carries
the actual path. Finishing pauses and releases shared owners, performs one
Windows rename without copying or replacing bytes, and updates their libtorrent
mappings. A held file retries after 30 seconds without a download error. Startup
and Use existing share name reconciliation and verify adopted content. Preparation
admits eight torrents at a time, avoiding worker overload for a large library.
Open uses the actual filename with its original extension through one Windows
shell owner. Download completions attempt ZoneId=3; verification does not.
Client identity uses the shared MSBuild Version, with the contracted bootstrap
nodes and unlimited per-protocol announce counts.

The source gate found three Move defects: missed final-name collisions, Use
existing overlooking real filenames, and stale completion eligibility. Its first
re-review found a related checkpoint-marker race. All four were corrected; the
second correction review reports zero remaining counted findings in this slice.

- Engine build: `artifacts/file-lifecycle-engine.log`, 54.19 seconds, zero
  warnings/errors. Version preprocessor definitions require its PCH and all 26
  other translation units to compile. No dependencies were rebuilt.
- App build: `artifacts/file-lifecycle-app.log`, 53.10 seconds, zero
  warnings/errors. The shared props change invalidates Lucide's CoreCompile
  input; the owner changed TableView's XAML too. TableView and the app each use
  WinUI's two generated-XAML compilation passes. The Open integration passed
  source review; no screen or player was launched for this slice.
- `FileNames-bb71b3a7-ab80-41bd-9a74-00a28ee18328` passed under
  `artifacts/evidence/`: two shared owners, a held suffix across restart, retry,
  byte preservation, both persisted mappings, no Internet mark on verified bytes,
  final-name collision refusal and Use existing without redownloading.
  The first fixture incorrectly assumed fast resume discovers externally copied
  bytes; it was corrected to use the normal Verify command. The engine was not
  rebuilt for that test correction.
- `FilesSafety-f1fd9266-322f-47ee-83f5-adde1292cbef` and
  `CheckpointRetry-dbbc2248-f1e1-4e76-9d81-4f2949676204` passed once after the
  final engine change. One earlier FilesSafety launch refused an owner instance
  before executing the check. That window and engine then closed normally under
  the existing authorization. Each check closed its own engine.
- No full suite, Transfer peer, UI automation or distribution work ran. Required
  generated-output scans found nothing outside artifacts; Everything IPC required
  the desktop permission context.

Still unverified: positive Mark of the Web after a real download, native player
launch for an unfinished file, native association/security prompts, cross-volume
file behavior and dropped-alert recovery. A legitimate existing partial file
keeps its real name for verification; it is the documented suffix exception.
This milestone does not claim overall releasability. Concurrent inspector-card,
file-list, header and search work belongs to the owner and is excluded from this
commit; the accepted baseline is recorded below.

## Owner's concurrent design baseline — 2026-10-06

The owner confirmed that the current inspector-card and file-list work is
intentional, not a regression from the earlier shared-header correction. Preserve
the inset rounded inspector card, its visible semantic surface and edge, and the
file list's shared table padding, aligned headers and checkboxes, and bounded
columns. Column labels and the inspector title have different semantic roles;
their typography and native heights need not be identical. Use the current
interface contract and settled source for the exact tokens. Table-header styling
is still being refined concurrently; do not restore an earlier appearance or
claim the intermediate changes have passed visual review.

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
