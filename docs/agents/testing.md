# Testing that earns its cost

The default feedback loop for a small edit should take seconds, not a five-minute
suite followed by another five-minute suite. Choose evidence for the failure the
change could cause. Neither a test per change nor a full run per task is required.
This policy owns test scope across TinyTorrent. [Compilation](compilation.md)
owns when to build, output reuse and failure handling; the root instructions govern
permission to run tests and launch desktop applications.

## The everyday loop

Choose evidence for each distinct failure the change could cause. Use source
reasoning, compiler guarantees and existing coverage first; run the smallest
existing check that adds evidence, with its native filter. Multiple checks of
the same behavior need a distinct failure each can catch. A full suite is for a
deliberate integration or release check, or an explicit owner request.

Include reflection-based test calls when reviewing consumers before a build.
Run every check on one build's output; [compilation](compilation.md) says how
to find the output that matches the source.
Run the installer (`Release.cmd`) only for
installer work or an owner request, because it also consumes a build number.
Documentation and screen-copy edits need no application build or suite.

Use the [focused review passes](compilation.md#focused-review-passes) for source
review. An independent review for a material correctness or ownership risk can
serve the corresponding pass; it need not duplicate it. After a finding, review
the correction and rerun only checks whose evidence it changes. Keep one shared
record of findings and results so agents can use completed work.

A known relevant failure needs resolution. When it reveals a stale harness,
inspect the affected calls together before rerunning, rather than rebuilding
after each setup error. Choose a slower integration check when the risk crosses
that boundary; avoid adding another framework just to shorten a run. Preserve
essential data-integrity checks and state what remains unverified.

The engine checks that start the Transfer peer download real payload and take
minutes, so they run only when the owner asks for them. `engine/tests/Checks.ps1`
refuses them without `-Transfer`. Checks that each guard one defect already
fixed, such as `WaitingMove`, also run only when the owner asks, so a run of
every check skips them; `Checks.ps1` refuses them without `-OptIn`.
`WaitingMove` checks that a Move waiting for another torrent's metadata
replies at once, sends one toast, and starts once the metadata is no longer
missing.

## What earns a test

**Owner ruling: high-return tests only.** Tests are not the safety net here;
review and measurement are. A test that does not earn its place is deleted later,
so writing it is work done twice, and the time comes out of the real deliverable.
A change is finished when the behavior is right, whether it carries one test or
none.

**One question decides it: what specific failure does this test watch for?**
Name what breaks, what the user would observe, and why nothing else already
catches it: an existing test, the compiler, libtorrent, or the platform. "A late
resume-data save re-adds a torrent the user removed, so it reappears after
restart" answers the question. "This code should have tests" does not, and
neither does a plan that lists a test out of habit. When there is no answer,
there is nothing to test: say so in the report and move on. An instruction to
"add tests" is bound by the same question.

Failures worth watching:

- Loss of the user's data or downloads: saved state that does not survive a
  restart, a destructive action that reaches another torrent's files, a late
  write that resurrects a removed torrent.
- The pipe contract: malformed, interrupted, or oversized messages, checked
  through a small set of sample frames that both sides read. Not a test for
  every field or enum.
- An invariant, pinned once. One test of the rule beats twelve rows of examples.
- A defect already paid for, that a plausible simplification would bring back.
  These tests preserve the evidence while the protected behavior remains required.
- A rule that nothing else guards.

**Presentation is reviewed in the product, never asserted.** Screen text, labels,
wording, XAML or source text, colors, spacing, screenshots and displayed number
strings change with every copy or layout edit while the behavior stands still.
A test on them forces a matching test edit and proves nothing. A measured
requirement, such as a contrast ratio, is a contract, not presentation. Getters,
pass-through mappings, framework behavior, every arm of an enum, a second example
of a rule already pinned, and a test that counts other tests watch nothing either.

Report each new or materially expanded test method with the failure it watches,
what would be observed, and why existing coverage misses it.

## A test asserts the outcome the failure would change

Assert what the user or the caller would see go wrong: the saved state, the files
on disk, the refusal, the reply on the pipe, the state a control is left in. The
expected value is a literal that the scenario predicts, never a value that the
production code under test computes again. A test that still passes when the
feature is deleted watches nothing.

Text finds a control; the assertion is what using it does: focus, selection, a
command enabled or disabled, an action taken, state saved. Exact text or bytes are
the assertion only when they are data or an external contract, such as a torrent
name, a path, or the bytes on the pipe.

A test runs the code and asserts what it did. It never reads a source file. Text
found in a `.cs`, `.cpp`, `.xaml` or resource file proves that a line was typed,
not that the product does anything. A rule about source structure or conventions
belongs to the compiler, an analyzer, or review.

## The cheapest layer, the smallest set

Prove the failure at the cheapest layer that can fail for that reason: engine
logic before a pipe round trip, and a pipe round trip before a running WinUI
window. A higher layer repeats a lower one only when it can fail for a reason the
lower layer cannot see. Pin a rule with one representative case, plus one case
for each boundary or failure mode that is materially different. A case that
differs only in its literal adds nothing.

A test is deterministic or it is a defect. Fix the source of the nondeterminism,
or delete the test. A retry, a sleep or a longer timeout hides the defect and
keeps its cost. Wait for observable completion, with a bound.

Reuse the fixtures that exist. A new fixture, builder or helper is justified only
by setup that a valuable test cannot avoid, because each one is a second
architecture the next engineer must learn. Production keeps its shape: a member
made public, an interface added, or a class split only so that a test can reach
it is the test bending the product.

## A failing test has three outcomes

1. Production is wrong: fix production.
2. The test pinned behavior that changed on purpose: delete or rewrite it, and
   report what it pinned and why that protection is no longer needed.
3. The test was wrong: fix it and say why.

Decide which before you touch either. Weakening an assertion, widening a
tolerance, copying the observed value into the expected one, raising a timeout,
or skipping the test is none of the three. When one change turns many tests red,
the tests were coupled to the implementation: that is evidence for the second
outcome, not a reason to update them all.

**When you change a string, search for the string.** A test's class name does
not say what it asserts. A search for the literal finds every assertion on it.
The same applies to a renamed member, a changed enum value, or a moved key.

**Deleting a test is a real option.** When a test you touch fails a rule above,
deleting it is the default. Report which behavior each deleted test pinned.
Deleting a test because it is red is the failure mode, not this.

## Windows evidence

`app/tests/AddSourceChecks.ps1 -BinaryDirectory <built-app-folder>` checks that
duplicate recovery preserves file choices and that a dialog's opt-out turns off
the settings passed to it in one save. It reads the settings command the
`Editor` queues instead of connecting to an engine. It does not exercise native
checkbox input or which setting Add passes for a magnet link or a torrent file.

`app/tests/SettingChecks.ps1 -BinaryDirectory <built-app-folder>` checks that a
refused earlier save neither discards newer input nor marks it as refused. It
completes the save's reply itself through a recording `SettingStore`, so it
needs no pipe, engine or WinUI.

`app/tests/NavigationChecks.ps1 -BinaryDirectory <built-app-folder>` exercises
the compiled window navigation owner without starting WinUI or the engine.
It watches contextual returns through secondary pages, same-page workspace
resets, nested Connection navigation, and refused or overlapping departures.
These checks cover history and command availability, not native button input,
focus, animation, or the editor guards themselves.
They also check that a temporary layout visit restores once after accepted
departure, survives a secondary page and a refused departure, and can be kept
without restoring the old layout.

`app/tests/NotificationChecks.ps1 -BinaryDirectory <built-app-folder>` exercises
the compiled toast owner without starting WinUI or the engine. It watches a
timed toast closing after its lifetime, a fifth timed toast removing the oldest,
a command error and a failed background result staying through the countdown
and a hidden stack, the same work failing again replacing its earlier failure,
and a seen timed toast retiring when the stack hides. It
does not cover the window's pause conditions, such as hover or editor focus.

`Capture.ps1 -Review connections` exercises the connections-map toolbar and View
menu commands, selected and all-transfer scopes, Back restoration, Properties
canceling the return point, selection loss through filtering and a Library
return through Settings. It also removes the disposable transfer to
check Back's missing-target recovery and a fixed speed scope with no remaining
transfers. It saves the original layout and full-map view for inspection.
This off-screen journey uses real controls and a disposable engine store. It
does not synthesize OS keyboard input or create live remote peer connections.

`app/tests/DistributionChecks.ps1 -BinaryDirectory <built-app-folder>` checks
the peer map's estimated distribution without starting WinUI. It watches the
most probable placement, where a second connection stays in a 90% region
instead of spreading to a 10% one, and checks that the regions used take all of
a country's traffic. It does not cover merging places into regions or drawing.

Use this machine and the targeted checks below for the
[architecture](../contracts/architecture.md).
Lifecycle behavior needs a real process check when it changes; a cache-policy
or hot-path change may need a short comparable workload. Cosmetic changes do not
inherit those costs. The [root validation ruling](../../AGENTS.md#working-rules)
governs running WinUI tests and samples; protect the owner's active desktop work.

Report the relevant checks performed, their outcome, and any material gap.
Distinguish source review, compilation, automated behavior checks, and manual
observation. None should be described as stronger evidence than it provides.

Capture journeys are excluded from ordinary builds. Build the app with
`/p:EnableCapture=true` to include them; its output and intermediates use the
`_capture` suffix under `artifacts/`, so a diagnostic build cannot replace the
ordinary product. The option does not launch anything.
Run a review with the launcher, [Capture.ps1](../../app/tests/Capture.ps1) `-Review <mode>`.
First perform the [pre-launch check](compilation.md#verify-the-build-before-launch)
against that launcher's resolved output; a normal-app build leaves capture
outputs unchanged.
The launcher stops development instances from this checkout in the current
Windows session before claiming the engine pipe. This pauses their transfers;
installed copies outside the checkout are left alone. The review still verifies
its engine process and disposable store before issuing commands, so stale
development processes do not require manual cleanup or expose personal data.
It prepares a disposable store from the fixture in `app/tests/Store/`, or
generates the torrents a mode needs, then starts the engine and hidden window
from `artifacts/bin/TinyTorrent/debug_win-x64_capture/` and fails when the
review reports a failure. Pass `-ArtifactsPath` when the capture build is in a
lane. Evidence goes to `evidence/Capture-<mode>-<id>` in that output folder.
Capture environment variables do not enable diagnostics in an ordinary binary.

For UI reviews, use the automation tree for controls, state, bounds and focus;
capture pixels when they establish a visual finding. A review launch may set
`TINYTORRENT_CAPTURE_DIRECTORY` to an absolute evidence directory and use
Ctrl+Shift+F12 to save the current XAML scene and open popup visuals, plus capture
time and dimensions. With `TINYTORRENT_CAPTURE_REVIEW=1` and an absolute
`TINYTORRENT_CAPTURE_STORE` identifying a disposable engine store, the same
diagnostic visits the real pages and cancels their dialogs automatically. It
uses a window outside the desktop, does not activate it, saves images and XAML
control bounds, and closes itself. A connected store mismatch stops the review
before changes. The window and its open popups must remain unchanged together
for 500 ms before they count as stable, so an entrance animation's initial delay
cannot produce a blank capture. The comparison leaves out the focused text box,
because its caret blinks about as often as that window, so the image shows the
caret on or off.
Long pages include overlapping middle captures as well as their
top and bottom, so controls between the endpoints receive layout review too.
A second review window exits without activating an existing
application. Normal launches perform no automatic capture.
Use `TINYTORRENT_CAPTURE_REVIEW=smoke` to rerun the recovery journeys without
repeating the Settings viewport or full themes-and-sizes batches.
Use `Capture.ps1 -Review peers` for the native peer map, header toggles, adaptive
arrangement, internal splitters and inspector height in Light and Dark. It checks global peers and
empty-selection Speed against the disposable engine before supplying synthetic
peer coordinates and rates to the real inspector for visual review. A country
database lookup uses the bundled MMDB; the synthetic city points do not establish
city-database accuracy or live transfer timing. The unknown-origin scene retains
markers without arcs. This journey also renders the country flags whose deferred
font binding previously failed when table cells were created. Its review report
records the map's draw times for 100, 1,000 and 10,000 known peers, for each
wheel step of a zoom from 1× to 128×, and for drags across the land at 8×,
32× and 128×.
Use `TINYTORRENT_CAPTURE_REVIEW=add-layout` for the workspace headers and empty/long-link
magnet Add dialog in English and Spanish, Light and Dark, at the three review sizes.
It also captures the engine-stopped message bar with its Restart action and records
workspace bounds before and after; this is presentation evidence with a connected
engine, not a simulated connection failure.
The Add cases invoke Preview with invalid input and retain that input for correction.
They also submit a disposable magnet with a relative download folder, capture the
engine refusal and focus recovery, then correct the folder. One short-window case
retries Add paused successfully; every source and destination belongs to the
disposable store. These cases do not run unrelated journeys.
The English 1040-wide case also captures the theme button's PointerOver
state in Light and Dark through WinUI's state manager. This checks the
custom template's appearance, not pointer input or OS High Contrast rendering.
Use `TINYTORRENT_CAPTURE_REVIEW=footer` to capture filter, update and memory
presentation at fixed window sizes, including the combined filter and update state.
The Paused/All filter changes use the real owner with an idle disposable torrent;
the update state is a presentation override retained through binding
refresh. The memory state turns on the real Show memory usage setting, waits
for the engine to confirm it and for a sample, then turns it off. The report records their survival and workspace bounds for review,
not layout assertions. It does not trigger an engine failure or open an update.
It then resumes the paused fixture through the real Resume command while every
transfer stays paused, and checks at every size, language and theme that the row
shows All paused and that the informational toast names the torrent and offers
Resume. It does not press that button, and pauses the fixture again afterwards.
These existing text and severity comparisons are diagnostic implementation,
not a requirement to assert translated presentation; review the captured
toast under [What earns a test](#what-earns-a-test). It does not cover a
scheduled pause or a missing adapter.
Use `TINYTORRENT_CAPTURE_REVIEW=shell` for the title-bar menus, narrow layouts,
themes, selection commands, secondary pages and retained filter state.
It also queries the review window's native `WM_NCHITTEST` response at the app
icon, menu and command centers, and unused caption space. This catches controls
classified as draggable or an icon without system-menu semantics. It sends no
input and establishes only top-level hit classification, not physical pointer
delivery, child input routing or native caption-button actions.
Use `TINYTORRENT_CAPTURE_REVIEW=settings-layout` for General Settings and
completion feedback in English and Spanish, Light and Dark, at the three review
sizes. It reveals Startup and Default app sections, checks native registration
switches against their observed model state, and leaves Windows registrations
unchanged. It commits and restores one notification switch through its native
control. Completion captures feed a simulated engine toast into the production
UI handler; they do not prove Windows delivery, a real completed download or
Explorer launch.
Use `TINYTORRENT_CAPTURE_REVIEW=search` for native AutoSuggestBox result
submission: unavailable commands, Properties from Settings, speed-limit
navigation, named-setting focus and reopening suggestions. It captures localized
results across themes and sizes; it does not synthesize Ctrl+K keyboard input.
`TINYTORRENT_CAPTURE_SEARCH_BASELINE=1` suppresses that mode's failure checks
for baseline images; such a capture is not evidence that the search checks pass.
Use `TINYTORRENT_CAPTURE_REVIEW=settings-prototype` for the implemented Settings
index and categories with advanced controls hidden and shown, plus manual
Connection setup. It uses English/Dark and Spanish/Light fixtures; its typed
capacity is sample input, not a network measurement. It also selects another
Library provider in its list, with automatic downloads off, and checks the same
saving and display as the edits review's choices before it restores the provider.
It also switches the language while Network is shown and checks the header's
first fact caption.
Its rendering-frame samples record category and Connection motion, rapid
navigation, and drawer height and clipping during expansion and collapse;
settled screenshots alone do not establish animation timing.
Its Queue destination visit also captures a 280-DIP header to inspect stacked
facts and trimmed values, then restores its automatic width. That constraint
applies only to the header within the review window.
Use `TINYTORRENT_CAPTURE_REVIEW=edits` for ordinary Settings departure,
new input and explicit commits during a pending acknowledgement, and schedule
edits applied on departure. The acknowledgement races change the setting input
synchronously before the UI thread yields and verify its native display
afterward; ordinary departure uses native editors. A two-field departure edits
Download again when Upload starts saving, then verifies that the first navigation
applies both values. This catches an earlier field being left unsaved after the
departure loop has passed it. It then selects another peer protocol, encryption
choice and network adapter in their controls, and checks that each saves and
shows the saved value, and that selecting the current value again saves nothing.
The ComboBox selections are set on the control without opening its list. The
check restores its fixture values, choices and periods. It does not simulate
keyboard delivery or engine refusal, and it needs one network adapter besides
Any adapter in the list.
Use `TINYTORRENT_CAPTURE_REVIEW=library` only with the
launcher's `library-capture.json` manifest. It checks native filter and named
torrent selection with 300 real paused torrents, then captures the populated
workspace, filter drawer, multi-file inspector and Pieces. Native hierarchy
providers check that collapsing a folder prunes its hidden selected child while
retaining outside selection, and expansion leaves priorities unchanged. Native
priority menu actions select a folder, an overlapping child and an outside file,
verify the exact persisted priority indexes, then restore the original priorities.
Separate synthetic Pieces captures exercise the production model and renderer
with mixed states, partial progress and 20,000 aggregated pieces. They replace
only the diagnostic map temporarily; the live torrent header describes another
fixture. These images establish rendering, not engine accuracy or live transfers.
No live traffic is implied by this paused fixture.
`TINYTORRENT_CAPTURE_REVIEW=library-files` visits the Library page and file
properties with its disposable two-file fixture across English/Spanish and
Light/Dark. Its separate `library-timing.json` branch is the retained timing
diagnostic; the [cancelled Library validation programme](../contracts/library.md#measurement-gates--cancelled)
does not authorize running it. Existing `app/tests/SourceChecks.ps1` and
`ProviderChecks.ps1` are focused source-membership and provider checks, not a
standing requirement to rerun them for every edit. Preserve their earlier
evidence and apply this document's test-selection rules to any future run.
Use `TINYTORRENT_CAPTURE_REVIEW=traffic` only with the
launcher's `traffic-capture.json` manifest. It captures the active workspace,
General, populated Peers, mixed Pieces and nonzero Speed history across the
same matrix. It uses the existing Transfer peer and current engine; it does
not repeat completed-download integrity checks or send desktop input. Like the
engine's transfer checks, it runs only when the owner asks, with `-Transfer`.
Use `TINYTORRENT_CAPTURE_REVIEW=schedule` for exact-minute overnight editing,
validation, cancellation, moving and removal, with the schedule overview,
selection and editor at all three sizes in English and Spanish, Light and Dark.
Use `TINYTORRENT_CAPTURE_REVIEW=desktop` through the launcher for
declining Exit with unfinished input and reconnecting an open tracker draft.
Its restart handshake affects only the launcher's own fixture engine; it does
not automate the desktop or establish native notification/power behavior.
Use `TINYTORRENT_CAPTURE_REVIEW=details` for the native priority, tracker,
live-language and setting recovery journeys without repeating the full
layout matrix. It captures populated Trackers in both languages and themes at
the three review sizes after saving several URLs and tiers through the real
editor, then restores the original list. The disposable fixture stays paused;
this establishes populated layout and edit outcomes, not live tracker responses.
`details-files` limits correction captures to Files and localized selected
Settings choices; it does not repeat those behavioral journeys.
Use `TINYTORRENT_CAPTURE_REVIEW=files` through the launcher for
shared Move/Delete confirmations, native submissions, collision refusal and
byte preservation. `files-layout` stops after the Move layout and ownership
refusal, preserving fixture membership and payload. These modes require the
launcher's `files-capture.json` manifest; they are not general-purpose actions
against an existing store. The system folder picker is outside XAML capture,
so the diagnostic supplies its disposable destination to the existing owner.

First review functionality and recovery through the existing owners. Then use
the images to examine hierarchy, spacing, alignment, typography, grouping,
surfaces, color, state, and responsive layout against relevant Windows and
Fluent guidance. A sequential roleplay of representative human users is an
additional usability smoke test: check discoverability, task completion and
avoidable friction. It is reasoning, not runtime evidence, and native WinUI
design principles take precedence when they conflict. Record both the
judgment and the actual evidence; neither replaces the other.

XAML images exclude native window chrome, system dialogs and desktop acrylic.
Record those limitations rather than claiming a full desktop capture. Do not
drive or capture the shared desktop while the owner needs it.

The review sizes 720x560, 1040x680 and 1280x800 name requested window dimensions.
The accepted title bar can raise the minimum width to keep its controls usable;
the captured client area also excludes native frame dimensions. Use the recorded
capture dimensions and scale for actual size claims. A scene labelled 720x560
does not prove a literal 720-pixel client width when Windows clamps the request.

## Resource checks

`Capture.ps1 -Review memory` records private committed memory, working set,
managed bytes and GC commitment at startup, after a Settings tour, after leaving
Settings for ten seconds, and after repeating the tour. It then visits Settings,
Connection, Library, Torrents, the global peer map and speed graph, About and
Credits five times, recording each pass and About's UI memory reading. The peers
fixture then measures the real table at 100, 1,000 and 10,000 rows, recording its
visual element counts, scrolling allocations and unload/reload samples. It takes
its screenshot after the measurements so rendering a capture cannot inflate them. These are
observations from an off-screen disposable window, not pass/fail memory limits.
For debugger or profiler attachment, set `TINYTORRENT_MEMORY_AUDIT` to an absolute
directory. Each checkpoint writes `<stage>.json` and `ready.txt`, then waits up to
two minutes for `<stage>.continue`. Without that variable, the journey proceeds
automatically and records its samples in `review.json`.

Clear ownership, bounded retention, on-demand UI, and sensible upstream defaults
guide the design before measurements exist. Do not invent a memory promise or
require a benchmark for every implementation choice.

Once a real download/seeding path works, take one short memory/throughput check
before expanding the UI. Repeat a focused whole-application check when the
intended functionality works, leaving time to address findings before release.
Between milestones, repeat only for a concrete regression or a change likely to
affect memory or throughput, after related edits settle.

For the milestone checks:

- Compare idle and active downloads/seeding with WinUI closed, added cost while
  open, and peaks during a representative operation.
- Record resident working set and private committed memory separately. Identify
  mapped/file-cache effects and avoid double-counting shared pages.
- Include throughput, CPU, the time from Open to a usable window, and release
  package size with required DLLs and runtimes. Size is secondary to correct,
  useful transfer behavior.
- Check that repeated UI open/close and language switching do not accumulate
  retained state. Closing WinUI releases its process and UI-only snapshots; the
  engine's speed history stays within its bound.

Use the same machine and comparable conditions. Prefer repeatable local input
over a volatile public swarm for transfer comparison. Synthetic rows can exercise
large lists without another computer. No benchmark service or hardware lab is
required.

Data-integrity changes need focused evidence when they occur. Broader fault and
release checks cover simultaneous launches, Unicode paths, malformed messages,
write failures, remove/re-add races, incoming seeding, actual peer limits, and
shutdown during a move. These scenarios guide relevant checks, not a suite
to run after every edit. Report untested conditions honestly.
