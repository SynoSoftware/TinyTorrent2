# Implementation handover — 2026-10-05

## Native row selection milestone — 2026-10-06

The owner rejected the selected-row stripe. It is removed; native selection
fill and keyboard focus remain. The capture build, 36-case populated-library
journey, test-project compilation and fresh visual review pass. No suite ran.
Exact evidence is at the top of [morning-report.md](morning-report.md).

Concurrent owner toolbar/splash/design-document edits arrived during the
ordinary build, which failed in XAML compilation. Preserve those edits and do
not claim its executable is current. The stripe proof predates them. Pending
Capture.cs/docs/testing.md changes extend the Trackers fixture; its multiline
language-switch assertion has a reviewed diagnostic correction awaiting build
and a focused rerun. Check actual native newline handling before changing the
product parser. All broader release evidence gaps remain open.

## Native caption checkpoint — 2026-10-06

The owner's accepted UI checkpoint is committed as `6809410`. The ordinary
Release app now includes the committed production changes and current checked
engine. The focused shell journey passes all 50 native hit classifications and
its command/navigation outcomes. Its old Resume assertion was corrected to
respect global Pause all; no product behavior or layout changed. Source review
has no remaining finding. Exact builds and the final isolated run are recorded
at the top of [morning-report.md](morning-report.md).

No engine rebuild or full suite was needed. All launched processes are closed.
Physical Windows input, accessibility/scaling and the previously recorded
live-download/filesystem gaps remain unverified; top-level hit queries do not
close them. The whole-product release goal remains active.

## Accepted UI checkpoint — 2026-10-06

The owner's raised inspector, table-header, file-list, empty-dialog-title and
search-result edits are preserved unchanged in this checkpoint. The existing
successful app build and completed Add/search/library captures include this
source. Do not restore the old flat inspector or call its accepted elevation a
regression. The fresh visual review and schedule correction are complete;
`e462f14` contains that correction. Exact evidence and release limits remain in
[morning-report.md](morning-report.md).

A fresh functional audit also reports zero actionable source blockers against
milestones 3–5, including these ten UI files. Native desktop/accessibility and
the recorded live-download/filesystem evidence gaps remain unverified. No new
build, tests or launch were run merely to repeat existing evidence.

## Schedule feedback milestone — 2026-10-06

The fresh broad capture reviewer found one counted defect: rejected schedule
saves left their error offscreen. The single error now appears beside the
editor's actions and is revealed without taking focus. The app build and all
twelve schedule capture/journey cases pass; the reviewer's first correction
re-review reports zero remaining counted findings. The accepted layout and
schedule rules are preserved. Exact evidence and native-input limitations are
at the top of [morning-report.md](morning-report.md).

This closes the supplied-image visual gate, not whole-product release
acceptance. Keep the owner's concurrent UI diff intact. The remaining native
desktop, accessibility and live-download evidence gaps below still apply; do
not repeat unchanged engine checks or a broad suite for this UI-only milestone.

## Selection continuation — 2026-10-06

The remaining #54 marquee rollback and stale deferred-click defects are fixed.
Three focused gesture cases and four existing in-process UIA selection cases
pass; source adversarial review and the app build pass. No visual layout changed.
Exact evidence is in [morning-report.md](morning-report.md). Commit `6b6163b`
contains this correction. Whole-product release acceptance stays open; the
fresh capture gate and its correction are recorded above that evidence.

## Keyboard and contrast continuation — 2026-10-06

The TableView keyboard/header and caption contrast corrections are settled.
One app build passes, the complete 300-torrent library capture passes, and native
focus reveals the last header in LTR/RTL without moving the rows. Bounded source
and visual reviews report no counted correction defect. The Add/layout run saved
both passing Light/Dark caption previews but exceeded its launcher bound before
finishing the full matrix; do not describe that run as a complete pass. Exact
evidence and limits are at the top of [morning-report.md](morning-report.md).
Preserve the owner's uncommitted inspector, file-list, table-header and search
work. Actual OS High Contrast, physical input and native desktop evidence remain
open, as does whole-product release acceptance. Do not repeat engine checks for
these UI changes or rerun the broad capture simply for a green report.

## Previous continuation — 2026-10-06

The current 300-torrent library journey passes, including native filter/search
submission, selection reveal, multi-file inspector and unchanged fixture bytes.
Its old provider exception did not recur; no workaround was introduced. A fresh
visual review found one compact Spanish Add label losing meaning. Shorter wording
resolves it in Light/Dark; correction re-review has zero counted findings.
Add also gains eight native localized access keys. Physical Alt remains unproven.
Exact builds, captures and review scope are at the top of
[morning-report.md](morning-report.md).

`2dc8406` prevents commit-only managed recompilation in local builds while
retaining CI provenance. Do not rebuild just to measure this change, and do not
repeat unchanged engine checks. Keep the owner's concurrent UI work intact.
Next: verify and address the surviving TableView keyboard/header concerns (#55)
and High Contrast caption-state concern (#47), using current source rather than
assuming all 34 open agent-filed GitHub issues are valid. Other native desktop
and accessibility evidence limitations remain. The full release goal is active.

## Current evidence checkpoint — 2026-10-06

`007dcde` commits the file-lifecycle milestone below. The following offscreen
Add/layout and search reviews reused that successful app build and passed;
their exact runs, visual review scope and limitations are at the top of
[morning-report.md](morning-report.md). Search now has current native submission
evidence for Properties from Settings, speed-limit navigation, named-setting
focus and reopening. This does not prove physical Ctrl+K delivery.

Preserve the owner's uncommitted inspector, file-list, table-header and search
changes. The current contract deliberately gives translated menus, a 200-pixel
Search and full caption buttons priority when computing minimum width; requested
720 captures clamp to 837/863 client pixels for English/Spanish. Do not count
that as exact-720 evidence or shrink the accepted title bar to obtain it.
No screen was changed in this evidence checkpoint. Native desktop, accessibility
and the recorded real-download/file-opening gaps remain; overall releasability
and final whole-product adversarial acceptance are still open.

## File lifecycle milestone — 2026-10-06

The fixed-behavior work #128/#129/#131 is now implemented: unfinished filenames
with shared-owner rename and retry, best-effort download marking, original-
extension Open, and the contracted client identity/bootstrap/announce defaults.
The final engine build, offline FileNames, FilesSafety and CheckpointRetry pass.
The bounded adversarial source gate closes after its second correction review
with no remaining counted finding. Exact evidence and unverified native/live-
download behavior are at the top of [morning-report.md](morning-report.md).
Do not repeat these engine checks for subsequent UI-only changes.

Preserve the owner's concurrent rounded inspector-card, file-list alignment,
table-header and search changes. They intentionally supersede the earlier
identical-header appearance and are not regressions merely because they differ.
The source UI work remains outside this milestone commit. Continue the remaining
narrow-width, native desktop and accessibility evidence work against its settled
design, without restyling accepted surfaces or treating open agent-filed issues
as proof of defects. The overall releasability goal remains active.

## Whole-tree and notifications checkpoint — 2026-10-06

`3f6de13` commits the owner's authorized whole working diff, including concurrent
shared controls, Add, inspector, table automation and engine download-order work.
The notification feature now has its three switches, existing saved-choice
compatibility, in-window completion action, closed-window Windows delivery and
tray error indication. Preserve the owner's newer design rulings; the notification
hint is not visible help text, and completion uses the shared one-line InfoBar.

Final app and engine builds pass. SettingsPolicy, FilesSafety and CheckpointRetry
pass against the current engine; the latter two ran once after its last change.
The twelve-cell Settings/notification capture and its native toggle and editing/
timeout journeys pass. Source re-review accepted the two corrected findings;
the independent twelve-cell visual review found zero counted defects.
Evidence and runtime limitations are at the top of
[morning-report.md](morning-report.md). This checkpoint does not settle release
readiness or prove all concurrent changes.

Further FileBrowser and search edits arrived after staging and remain outside
this checkpoint. A copy retry caught their XAML mid-edit and failed; preserve
that work and check its settled source before the next build. The staged app
source had compiled before an executable-copy lock; the final notification
candidate has its separate successful build and captures. See the report for
the exact distinction. An owner-started background engine remains running.

Continue with current-code checks for contract work #128, #129 and #131, plus
the existing narrow-width, native caption and accessibility evidence gaps.
Those three engine features are absent; they are fixed behavior, not additional
Settings switches. Do not treat older agent-filed GitHub issues as automatically
valid. Do not repeat the completed engine checks for documentation or UI edits.

## Header and feedback correction checkpoint — 2026-10-06

Preserve the current custom title bar, the two Add actions after Search, shared
48-pixel caption buttons, multiline magnet editor, overlay connection bar with
right-aligned action, and shared table/properties header surfaces. The final
app build and twelve-cell focused capture pass; independent image review found
no counted correction-induced defects. Exact720, native caption hover/gestures,
High Contrast, scaling and keyboard/screen-reader checks remain open. Full
evidence and the three missing overlay snapshots are recorded at the top of
[morning-report.md](morning-report.md). This correction is uncommitted alongside
the owner's concurrent work and does not settle overall release readiness.

## Add form correction checkpoint — 2026-10-06

The owner's typography, icon alignment and rounded-button corrections are in the
working tree. Shared styles use native WinUI type roles, button styling and
tokens. Preserve them with the concurrent Add and shell work. The focused final
app build and twelve-cell Add capture pass; the visual re-review reports zero
counted defects in those corrections. Current shell minimums prevent exact 720
and 1040 width verification. Evidence, the startup tooltip fix, process-exit
limitation and remaining checks are recorded at the top of
[morning-report.md](morning-report.md). No new commit or broader release approval
was made by this correction.

## Current handoff — 2026-10-06

The owner then authorized implementation of all eight selected architecture
slices. Checkpoint **`db6ec1d`** committed the preimplementation tree first.
Architecture changes are committed separately, with their owners described
in [architecture-current.md](architecture-current.md) and their slice mapping,
adversarial findings and focused evidence in the proposal's
[implementation checkpoint](architecture/proposed/evidence.md#implementation-checkpoint).
Use that record for architecture status; the earlier correction handoff below
is historical.

The implementation session recorded Release engine and app builds with zero
warnings/errors. Focused committed
file persistence, Settings edits, live-language details, unavailable-draft
recovery, and cancelled/failed Exit plus reconnect checks pass. No full suite
or transfer checks ran. Broader release acceptance, including the existing
large-library automation-provider gap, remains open.

Preserve the concurrent Add-dialog, splitter and torrent-option work. Its Add
pane layout was stabilized after a reproduced layout cycle by using star
columns; its design, resizing controls and options remain. Do not restore the
pixel-width feedback loop. Architecture and concurrent edits overlap in several
files. The architecture commit excludes those concurrent changes, which remain
uncommitted. Its separated snapshot was reviewed from source; no builds or tests
were run while preparing the commit.

## Release correction checkpoint — 2026-10-06

The owner chose to finish the in-flight release corrections, then proceed with
the selected [architecture](architecture/proposed/README.md). The correction
batch is committed as **`c7b9326`**. Its focused checks and bounded adversarial
reviews passed; broader releasability remains unfulfilled. Current evidence and
gaps are at the top of [morning-report.md](morning-report.md). Do not restart
general visual polishing before this architecture work.

Already handled: canonical Settings routing for every Speed limits entry point;
ordinary field departure; later input and explicit commit preservation across
acknowledgements; Save/Discard/Cancel through existing editor owners; failed
Exit recovery; narrow footer connection-state clipping. Preserve those behaviors
while changing ownership. The custom title bar and accepted Settings design
were not replaced or restyled.

Read the proposed architecture and its delivery/evidence files against current
source. Historical deletion targets are not all still present. Use separate
reversible commits for remaining ownership changes:

1. Finish the Settings owner and schedule transaction split: explicit field
   kinds/sections, authoritative conversion/submission, and removal of duplicate
   settings readers. Preserve language's immediate publication and the
   distinction between effective and saved alternative mode. Field
   acknowledgement/departure fixes are already present; rapid language choice
   is not new runtime evidence from this pass.
2. Consolidate remaining modal lifetime/recovery and accepted selection at their
   chosen owners. Keep complete interaction lifetimes and the early close/source
   admission gate. Do not add another save path or general framework.
3. Continue already-started projection, queue and drawing work from the owner's
   tree. Recheck code before implementing proposal #113, #114 or #125; do not
   duplicate work that is already there.

The checkout remains `main` and is deliberately **not clean**: concurrent owner
schedule, table/projection, graph, Add/FileBrowser, inspector, appearance-timing
and architecture-document edits remain intact. The status-rate fields were
included with the verified footer correction; other changes were excluded from
this pass's commit. Preserve the proposed-folder consolidation and historical
document removals. Review ownership before staging.

Final app build: zero warnings/errors. Search/MenuBar routing, nine focused edit
journeys and the final 12-state desktop/footer matrix passed. The footer
correction review has zero remaining counted defects. A new 300-torrent run
stopped in diagnostic peer creation with COM `0x8001010E`, before selection or
its matrix; its cause is unproved. Keep the prior library pass as prior evidence,
not a pass for the changing candidate. Physical keyboard delivery, native
desktop gestures, Narrator/High Contrast and subsequent owner UI work retain the
limits in the morning report. No full suite, engine rebuild or Distribution
work was performed.

All processes launched by this pass are closed. Coordinate the next build/run
with other work in this checkout: a concurrent Release build caused an
intermediate-file lock, and separate demos repeatedly owned the engine endpoint.
Do not force-close unrelated processes.

## Previous release continuation

Work resumed on 2026-10-05 under the current goal below. The first coherent step
retains the owner's app role folders, verifies the finished schedule, corrects
clipped short-period labels and fixes a reproduced window-shutdown crash.
Current evidence is at the top of [morning-report.md](morning-report.md).
The owner reopened product acceptance after finding command-search failures.
The earlier milestone reviews did not establish release readiness: they missed
actual search-result selection and used sparse fixtures. Their commits and
evidence remain useful, but the current release goal below supersedes their
completion claim. Commit `8af777e` corrects native search result routing,
numeric-setting focus, reopening search and narrow file-name clipping. Its
focused behavior checks and fresh visual correction review passed. Commit
`6b13ac9` corrects live inspector rate consistency, Spanish range selection and
rate headings. Its live-traffic journey and fresh correction review passed with
zero counted findings. Concurrent owner edits to list projection, table sorting,
status rates and graph width arrived during finalisation and remain uncommitted
by this pass; release acceptance of that changing candidate remains open. Read
the current evidence and precise limits in the morning report.

At the earlier handover, work stopped at the owner's request to move to another project. The pending app
recovery smoke check finished and passed. The five-milestone implementation is
still incomplete: milestones 1–2 have completion commits; milestones 3–5 have
substantial implementation and evidence but remain open. Distribution is outside
scope.

Read this file first on resuming, then the relevant contract and the evidence
ledger in [morning-report.md](morning-report.md). The fuller decision and reviewer
history is [implementation.md](implementation.md). Older runs are historical
evidence; they do not prove the entire current shared diff.

## Prime directives for the next executing agent

**Efficiency and common sense govern execution. The plan is a map.** Use it to
understand the destination, dependencies and promises to users. Choose the route
that produces a useful, native Fluent 2 application with the least unnecessary
work. A plan can be wrong; making it exhaustive or perfectly correct is not a
prerequisite for finishing the product.

Preserve explicit owner rulings, scope and data-safety requirements. When a plan
step adds cost without protecting a relevant user outcome, adapt the procedure
and record the reason briefly with the work. Do not start another plan-repair
project. Before each task, ask what user-visible result it advances, whether the
code is intended to stay, and what smallest evidence will establish success.

The HTML prototype is useful and imperfect. It demonstrates ideas using browser
layout, input, scrolling and focus behavior. Preserve useful information and task
relationships; use judgment to improve weaknesses and translate them into native
Windows behavior. Pixel matching, copying CSS geometry or making WinUI behave
like HTML is not the goal. A comfortable, coherent Fluent 2 experience is.

## Coding standard and goal

The owner's standard applies to work already underway and to deferred issues:

> My goal is easy-to-read, low-bloat code, not preservation of the current
> structure. Refactor when it materially simplifies the code, removes
> duplication, clarifies ownership, or makes responsibilities easier to
> understand. Avoid abstraction or function splitting only when it adds
> indirection, ceremony, or navigation cost. Do not preserve bad code merely
> because fixing it counts as a redesign. Apply that judgment to the work
> already underway and to the deferred issues.

**Minimum cleverness, not zero smartness.** Intelligent design and useful
abstractions are welcome. Avoid clever machinery that silently owns more
behavior than its name or scope suggests. An abstraction should make its
responsibility, effects and ownership easier to understand; it should not require
the reader to discover hidden policy. Earlier wording about smartness must not
be interpreted as a prohibition on abstraction or thoughtful design.

Judge the resulting code by how easily a reader can understand an operation and
its owner. A useful extraction or responsibility split earns its place through
that improvement. Preserve other contributors' ongoing changes while making
deliberate, coordinated improvements; preserving their work does not require
freezing the surrounding structure.

## Current goal

Make TinyTorrent2 releasable within the existing Windows desktop product scope: every everyday feature works reliably and comfortably; Ctrl+K results act predictably and navigate to the authoritative surface; commonly needed libtorrent settings have deliberate, documented coverage; populated screens are clear, calm, compact and consistent in English and Spanish, Light and Dark, at the supported normal and narrow sizes; accepted Settings, custom title bar and schedule designs do not regress; concrete release blockers are fixed and verified with proportionate focused checks and fresh adversarial review; coherent changes are committed, with honest remaining limitations recorded. Preserve the owner's concurrent edits. Do not run the full suite, rebuild dependencies, modify 3rdParty, automate the owner's desktop, or perform Distribution work.

The desired result is an application people can comfortably understand and use,
not merely compiled XAML or a pixel copy of the prototype. Task completion,
recovery, readable state, intentional grouping, native interaction and sensible
behavior at realistic window sizes are acceptance criteria. A required outcome
without evidence remains unfinished; state that plainly instead of substituting
a clean source review. Stop when this objective is achieved, or when the owner
explicitly pauses it again.

## Checkout and candidate

The shared checkout is `main`. The owner moved it there; this pass did not switch
branches. Inspect `git status` and preserve uncommitted work, including concurrent
engine edits. Account for ownership before staging a milestone commit.

The current candidate is
`C:\SynoSoftware\TinyTorrent2\artifacts\bin\TinyTorrent\release_win-x64\TinyTorrent.exe`.
The earlier `artifacts/checks/ui-review-build` candidate is historical and
predates the owner's Settings and engine commits. Keep the engine,
managed assemblies and runtime files together: each executable starts only the
other one beside it. A running engine owns the logon-scoped endpoint; another
data directory alone does not isolate a test.

`app/src` is grouped by role; [naming](naming.md#app-folders) lists the folders.

All processes launched by this pass are closed. The final process check found
no `TinyTorrent.exe` or `Engine.exe`. No desktop automation remains active.

## Plan status

| Milestone | Implemented checkpoints | Remaining release evidence |
| --- | --- | --- |
| First usable download | Completed previously, `e0a5a92` | Historical journeys and first resource measurements are recorded. |
| Everyday torrent actions | Completed previously, `6afbf65` | Historical evidence is recorded, with stated physical Explorer/drop and accessibility gaps. |
| Background and desktop behavior | `43e47eb`; later close/recovery journeys and `3f6de13` notification preferences | Actual Windows notification delivery, physical sleep/logon and native caption gestures remain unverified. |
| Details and preferences | `c9e3995`, later canonical Settings/search routing, `1eaf391` Add access keys, `245479e` header/contrast, `6b6163b` selection recovery; schedule feedback corrected after fresh visual review | Supplied-image gate closes with zero remaining counted defects. Physical keys, Narrator, OS High Contrast and scaling remain unverified; requested720 uses the documented current minimum. |
| Move and delete files | `c9e3995`; `007dcde` file lifecycle and final FileNames, FilesSafety and CheckpointRetry checks | Physical picker, cross-volume/device failures, dropped alerts, real-download marking and native unfinished-file launch remain unverified. |

These checkpoints do not claim release acceptance. Native search submission
currently reaches the authoritative Settings editor and selected inspector.
Every setting in the settled Preferences contract has an app field and control,
including the three notification switches; engine tuning is deliberately outside
that list. The latest 300-torrent library and focused selection/UIA checks pass.
Earlier live-traffic captures cover populated peers, mixed pieces and history;
they do not prove later changes or current transfer behavior. Transfer checks
remain owner-requested only.

The architecture implementation and its follow-up source review are recorded in
[the architecture evidence](architecture/proposed/evidence.md); its historical
deletion targets are not a new backlog. The current work is release verification
and concrete corrections, not another architecture pass. The owner's current
inspector, file-list, table-header and search work is included in the accepted
UI checkpoint above.
Keep its intentional design. The fresh capture gate closes after the schedule
feedback correction; it does not establish native input or complete feature
correctness. The morning report owns exact runs and their limits.
The sections below retain the earlier handover's history.

## Earlier handover: pending task finished

The final unattended recovery run passed in **12,156 ms**:

- Invalid speed-limit input remained `abc`; Apply showed an error and kept the
  dialog open. Two previous failed runs exposed the NumberBox/text connection.
  Removing its Text binding and reading its native editor through the existing
  owner resolved that failure.
- Invalid magnet input stayed editable with its error.
- Removing a torrent with an open tracker draft retained the draft, explained
  the unavailable target, disabled Save and left Cancel available.

Evidence:
`artifacts/evidence/UiSelfCapture-e494957a-cdea-4359-b498-7bdd65569b6a/captures/review.json`.
That run saved 13 scenes: all five Settings sections, overflowing sections at
their bottom, Pieces and recovery states. The Pieces image confirms its legend
labels and counts fit after measuring the native WrapGrid cells. Schedule labels
now stay on one line rather than splitting short segments into uneven rows.
Further visual acceptance remains open. These images predate the Settings cards
and title bar menus (`23af87a`, `3a1c5bb`), so they do not show those surfaces.

The latest app Release/x64 build passed with zero warnings/errors in **112.55 s**,
including the coordinated PipeClient changes. Log:
`artifacts/checks/ui-review-build/integrated-app-build.log`.
Only the app compiled; TableView and dependencies supplied existing outputs.
No full test suite ran for these changes.

After this smoke run, the prescribed Everything stray-folder query returned
exit 0 and no paths. A separate traversal also found no stray output folders.
Earlier IPC failures remain historical limitations.

Five focused engine checks already passed: CheckpointRetry, SelectedTransfer,
SettingsPolicy, CommittedFiles and FilesSafety. Their exact evidence directories
and limits are in the morning report. SelectedTransfer checked the wanted payload
immediately on reported completion. FilesSafety injects an interrupted move
marker; it does not crash a moving process.

## Why the WinUI interface fell short of the prototype

The implementation failed to produce a coherent native experience from the
useful design ideas. I treated functional coverage and native control selection
as more complete evidence than they were, and accepted screens before their composition
and interactions had been compared properly with the prototype. The owner had
to identify basic visual and usability problems repeatedly. Finding those
problems should have been part of implementation, not work left to the owner.

The recorded defects support these causes:

- **Information structure was lost during translation.** The clearest example
  is the original schedule: proportional periods, an hour ruler, feature state,
  legend and editing actions became seven gray bars with textual summaries.
  That removed the ability to understand the week at a glance. Better margins
  could not repair the lost meaning; the native layout needed to represent time.
- **Pages were assembled as controls rather than composed as tasks.** Settings
  categories had shifting left edges and poorly allocated content width; Browse
  clipped at an ordinary window size. Caption commands formed an equal-weight
  cluster, and status actions were separated from the information they controlled.
  Empty preview and feedback regions consumed space without conveying anything.
  Individually valid WinUI controls did not make these arrangements coherent.
- **Visual acceptance came too late.** Builds and source reviews established
  compilation, ownership and some behavior, but could not establish balance,
  readable density, alignment, clipping or a convincing shared surface. We spent
  too long checking source while obvious rendered defects remained. Native
  control defaults were accepted without enough inspection of the resulting page.
- **The work was fragmented and the review loop was excessive.** Engine work,
  advisory issues, source reviews, intermediate corrections and verification
  infrastructure repeatedly displaced finishing a coherent UI slice. Reviewing
  code that was still changing generated more work without proving the final
  screen. I should have narrowed and coordinated the work sooner rather than
  allowing hours of activity to stand in for a finished interface.
- **Desktop verification was inefficient and disruptive.** Repeated external
  navigation and screenshots occupied the owner's computer. Unattended in-app
  capture arrived late, after that approach had already cost time and trust.
  Early captures also caught animations or omitted dialog popup images, so their
  limitations needed resolving before drawing visual conclusions.

The prototype was better in the compared states because its hierarchy, grouping
and spatial relationships were designed together. Its schedule makes time visible; its
settings constrain content width and separate feature heading, status,
visualization, definitions and actions. Consistent alignment and spacing let
the eye identify what belongs together and what to do next. Those decisions
provide most of the improvement; reproducing a palette or adding decoration
does not recover them. This does not make every prototype decision correct or
suitable for a native desktop application.

The prototype also permits faster visual iteration and does not carry the full
engine, persistence and recovery obligations of the running application. That
explains part of the implementation cost, but does not excuse dropping its
design. MVVM, TableView and native WinUI controls can preserve the same meaning.
The remaining work is to compose them deliberately and verify actual states.

Some concrete regressions have now been corrected: the real timeline is back,
Settings allocation changed, NavigationView replaced the application context
menu, Exit moved to its footer (both later replaced by the title bar menus), and
input recovery passed. These corrections
do not establish that the current app matches the prototype's overall quality.
Complete visual acceptance remains open.

On resuming, finish one representative task and its shared layout rules before
expanding corrections across pages. Compare it with the approved prototype for
information and interactions, then judge its native rendering for functionality,
hierarchy, grouping, spacing, typography, theme and keyboard behavior at realistic
sizes. Reuse the existing command owners and capture evidence. Extend successful
patterns to related pages; review the settled result rather than repeatedly
auditing temporary code. The implementation agent owns identifying these defects
and showing their correction without requiring the owner to find them first.

## Using the WinUI skills and Fluent 2 effectively

Use [winui-design](C:/Users/user/.codex/skills/winui-design/SKILL.md) before
choosing a layout or writing changed XAML. Start with the person's task, the
information needed to complete it, and the grouping and command hierarchy.
Choose familiar native controls and consult a focused Gallery/sample lookup
when the pattern is unfamiliar. Keep the existing shell and TableView where
they serve the task. A sample is evidence about a pattern, not a reason to add
packages, build another framework or reproduce its whole application.

For this app, that means stable Settings content allocation, stronger spacing
between groups than within them, readable typography, purposeful theme brushes,
clear focus and error states, and resizing that keeps commands reachable. A
schedule needs proportional time geometry, but its editing, focus and dialogs
should feel native. Browser-style hover actions, simulated controls or identical
HTML dimensions should not displace better Windows behavior.

Apply Fluent 2 as design reasoning. Its [principles](https://fluent2.microsoft.design/design-principles)
emphasize platform familiarity, focus and inclusion. Its
[layout guidance](https://fluent2.microsoft.design/layout) explains how proximity,
space and alignment communicate relationships, and explicitly allows judgment
when uniform spacers break a pattern.

| Design question | What to inspect in TinyTorrent |
| --- | --- |
| Is the next action apparent? | Separate transfer commands, preferences and window controls; keep contextual actions near their target. |
| Does space express relationships? | Align related labels and fields, constrain Settings reading width, separate feature groups and collapse absent content. |
| Do surfaces and type convey hierarchy? | Use the requested unified caption/content backdrop, deliberate content grouping and native text styles; avoid decoration that competes with the torrent list. |
| Can people read and operate it? | Check state labels, focus, contrast, keyboard routes, accessible names and text expansion; color alone must not explain a state. |

Use spacing conventions to establish rhythm, then judge the composition. The
dense torrent table and an explanatory Settings form have different needs.
Identical numeric margins everywhere would not make them equally usable.
Choose the smallest effective resize or reflow change, consistent with
[Windows responsive layout guidance](https://learn.microsoft.com/en-us/windows/apps/design/layout/responsive-design).

Use [winui-code-review](C:/Users/user/.codex/skills/winui-code-review/SKILL.md)
on the settled, affected code after compilation. Check MVVM ownership, shared
command paths, bindings that actually update, draft retention, collection
identity, hidden-view work, disposal, keyboard semantics, theme resources and
localization. Tie findings to a concrete failure and correct their existing
owner. This is where a stale binding or blocking UI call can be caught; it cannot
establish visual balance or prove what happens when a real NumberBox loses focus.

Use the skills' intent with repository context. TinyTorrent already has its
command/notification infrastructure, JSON text catalogues and TableView. Replacing
them with Toolkit attributes, resw files or a sample ListView merely to satisfy
a generic checklist creates migration work and competing implementations.
Likewise, adding every available AutomationId or analyzer is not a substitute
for meaningful accessible names and reachable keyboard actions. Apply relevant
checks; keep existing authorities and validate the user outcome.

## What should have happened during the last 23 hours

The necessary engine and data-integrity work was real. The failure was also in
how I ordered work and judged progress: too many partially finished surfaces,
too much repeated source checking, and too little early acceptance of a native
page that somebody could comfortably use. There is no complete timing ledger
to assign percentages; the concrete waste is recorded in repeated review rounds,
build coordination, interrupted desktop inspection and corrections prompted by
the owner.

The schedule should have been treated as a functional information design from
the start. Settings should have had one stable layout established at a realistic
small window before all categories were expanded. Caption and status grouping
should have been judged as a whole, rather than declaring each individual button
acceptable. These decisions would have prevented rework rather than discovering
it through later reviews.

Builds needed to follow a coherent set of edits, and failure checks needed to
target the changed behavior. Reviews of temporary code, repeated broad reading
and speculative issue expansion delayed the visible result. Advisory findings
needed triage by current user impact and evidence, not automatic promotion into
more architecture. A required milestone review belongs at its stable completion
gate; it should not become a continuous loop while the implementation moves.

The external screen procedure should have been replaced sooner once it proved
slow and intrusive. The later capture batch produced 121 scenes in about 51
seconds, and the final focused recovery run took about 12 seconds. Those results
show that obtaining bounded evidence did not require occupying the desktop for
hours. The capture feature itself should stay a small verification aid; extending
it into another automation product would repeat the same mistake.

### A better working procedure

1. **Choose one meaningful result.** State the task and the failure to remove.
   Check functionality and common sense before polishing it. Read the relevant
   owner and contract, using the handover to avoid rediscovering completed work.
2. **Decide the native composition.** Use winui-design and the relevant Fluent
   principles. Keep useful prototype ideas, improve weak ones and choose native
   behavior. For a judgment call, use the requested sequential user roleplay
   briefly, then decide; do not turn it into more agents or speculative features.
3. **Make the coherent change.** Keep shared command paths and clear ownership.
   Refactor when it materially improves readability or responsibility boundaries.
   Fix repeated layout problems at their shared owner and local problems locally.
   Finish planned replacement before hardening or reviewing that code.
4. **Obtain proportionate evidence.** Build the changed target once when needed.
   Check the specific functional failure through the real owner/control. Inspect
   existing images or capture the changed state at relevant sizes. Expand testing
   only when a changed risk or observed failure warrants it.
5. **Judge the actual result.** Examine task completion, grouping, alignment,
   density, surfaces, type, focus and feedback together. Fix the highest-impact
   defect first. One representative page must work before copying its pattern.
   Do not wait for the owner to point out obvious visual failures.
6. **Review and finish.** Use winui-code-review on the settled diff and the
   required fresh reviewer at the milestone gate. Re-review actual corrections
   without reopening unchanged code. Commit meaningful completed work, record
   evidence and material gaps, and move on.

These are decision aids, not another rigid checklist. If a step no longer earns
its cost, choose a better route while retaining the required outcome and honest
evidence. Improvement means delivering the usable application sooner with fewer
rewrites, not producing a more elaborate plan or a longer review report.

## Implementation to retain

- MVVM owns product state and commands. Torrent, peer and tracker tables use
  TableView's public API. No TableView source changed in this pass.
- The prototype informs information and interactions; native Fluent 2 design and
  common sense guide the adaptation. The schedule has a proportional timeline,
  ruler, saved periods, legend, segment editing and period actions.
- A menu bar in the title bar holds File (add, Settings, Exit), Torrent, View
  and Help, beside search and the theme button. Language is set in Settings,
  Appearance. Caption and content intend one acrylic surface; XAML render
  capture cannot prove native chrome or desktop acrylic.
- Engine checkpoint writes are serialized per torrent, retaining the newest
  pending result. Completion waits for disk readiness. Exit retains process
  coordination after UI disconnect and waits for a running move or deletion
  without asking. Startup, save and unresponsive-window failures are tray
  notifications; the splash has no buttons.
- SpeedLimits validates raw input before saving. Inspector retains an unavailable
  torrent's draft. TextEditor supplies one native editor lookup for direct callers.
- Finding avoids repeated membership scans and unnecessary IndexOf calls on
  unchanged torrent projections (#105). It compiled; no large-list measurement
  is claimed.
- PipeClient bounds the greeting and each write-plus-reply exchange, disposal
  settles every queued command, and each view keeps at most one unsent read
  (#101, #102, #103, closed). A closing window takes no more sources, so they
  stay with the engine (#13, closed). Silent-peer, reconnect and disposal fault
  scenarios still need focused runtime evidence.

## Earlier resume sequence

1. Inspect the shared diff and concurrent work. Apply the coding standard above
   to current work and deferred issues: simplify bad structure where the benefit
   is material, preserve ongoing edits, and keep each decision at one clear owner.
2. Finish milestone 3's affected lifecycle evidence, then its settled review and
   commit. Avoid global power changes or driving an unrelated live engine.
3. Examine existing capture images before adjusting layouts. The larger batch
   has 121 scenes at three sizes in Light/Dark plus Spanish Schedule under
   `artifacts/evidence/UiSelfCapture-c11d60b6-5d6f-45f7-bb91-b1f865579d09/captures`.
   Its later recovery smoke failed before the fix. Some early frames caught native
   animations; current captures record pixel stability. Open popup images for
   dialogs: window.png alone can show only the underlying page. The batch
   predates the Settings cards and title bar menus, so capture those again.
4. Finish milestone 4's functional/visual evidence, then review and commit.
   Native chrome/acrylic, Narrator, High Contrast, text scaling, RTL, populated
   peers/trackers and active-transfer views remain incompletely verified.
5. Finish milestone 5's actual UI submissions and relevant recovery evidence,
   then review and commit. Update the morning report with completion commits and
   honest remaining limitations.

Self-capture lives in `app/src/MainWindow/Capture.cs`; its opt-in environment
variables and limits are documented in [testing.md](testing.md). The local
fixture launcher is `V:\temp\TinyTorrentRunCapture.ps1`. It refuses a pre-existing
engine, starts the matching candidate, checks the connected store and closes its
own processes. It depends on recorded fixture files and is a local verification
aid, not a distribution entry point.

Before each task, choose the smallest direct path to evidence. Finish coherent
changes before expensive review. Use focused checks for specific likely failures
and existing images for visual review. Reserve broad suites for stable code they
actually cover. Sequential user roleplay checks friction; native WinUI principles
take precedence. Avoid hardening code the next agreed change will replace.
