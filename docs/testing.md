# Testing that earns its cost

The default feedback loop for a small edit should take seconds, not a five-minute
suite followed by another five-minute suite. Choose evidence for the failure the
change could cause. Neither a test per change nor a full run per task is required.
This policy owns test scope across TinyTorrent; local instructions still govern
build entry points and permission to launch desktop applications.

## The everyday loop

Start with source reasoning, compiler guarantees, and existing coverage. Run the
smallest relevant existing check, using its native filter where available. Build
only the affected target through the repository's supported workflow when a
compile check is needed. Reuse valid build outputs; never test stale binaries.
Documentation and screen-copy edits do not justify an application build or suite.

Aim for seconds for routine checks. If the only available check takes five
minutes, decide whether this edit needs the evidence it provides. A focused
review or an authorized, narrow manual check can be sufficient for a low-risk
change. State what remains unverified. Do not build a new test framework merely
to avoid one slow run, and do not skip essential data-integrity evidence to meet
an arbitrary time budget.

Run a slower integration check after related edits have settled when the risk
actually crosses that boundary. Rerun it if a subsequent change affects what it
proved or a failure requires another attempt. Do not rerun unchanged checks just
because another agent reviewed the work or a small unrelated edit followed.
Coordinate one run and share its result.

Review the settled change, not intermediate code that is about to be replaced.
Finish and integrate a coherent working slice before its independent review;
keep only checks that could prevent data loss or catch a failure in the next
step. Repeated reviews of temporary arrangements cost time without establishing
the delivered behavior. Milestone completion still needs its final adversarial
review and relevant runtime evidence. Common sense takes precedence over a
mechanical review sequence.

A full suite is a deliberate integration or release check, or an explicit user
request. It is not the default completion gate for each bug fix. A known relevant
failure still needs resolution; postponing broad coverage does not excuse it.

The engine checks that start the Transfer peer download real payload and take
minutes, so they run only when the owner asks for them. `engine/tests/Checks.ps1`
refuses them without `-Transfer`.

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

Use this machine and the targeted checks below for the
[architecture](architecture.md).
Lifecycle behavior needs a real process check when it changes; a cache-policy
or hot-path change may need a short comparable workload. Cosmetic changes do not
inherit those costs. Respect the local restriction on launching WinUI tests or
samples; this policy does not grant permission to interrupt the desktop.

Report the relevant checks performed, their outcome, and any material gap.
Distinguish source review, compilation, automated behavior checks, and manual
observation. None should be described as stronger evidence than it provides.

Capture journeys are excluded from ordinary builds. Build the app with
`/p:EnableCapture=true` to include them; its output and intermediates use the
`_capture` suffix under `artifacts/`, so a diagnostic build cannot replace the
ordinary product. The option does not launch anything.
Launch the window executable in `artifacts/bin/TinyTorrent/debug_win-x64_capture/`,
or `release_win-x64_capture` for a Release build, with the engine beside it;
[Directory.Build.props](../Directory.Build.props)
names both.
Historical disposable launchers that name
`release_win-x64` must have both executable paths changed before reuse; capture
environment variables do not enable diagnostics in an ordinary binary.

For UI reviews, use the automation tree for controls, state, bounds and focus;
capture pixels when they establish a visual finding. A review launch may set
`TINYTORRENT_CAPTURE_DIRECTORY` to an absolute evidence directory and use
Ctrl+Shift+F12 to save the current XAML scene and open popup visuals, plus capture
time and dimensions. With `TINYTORRENT_CAPTURE_REVIEW=1` and an absolute
`TINYTORRENT_CAPTURE_STORE` identifying a disposable engine store, the same
diagnostic visits the real pages and cancels their dialogs automatically. It
uses a window outside the desktop, does not activate it, saves images and XAML
control bounds, and closes itself. A connected store mismatch stops the review
before changes. A second review window exits without activating an existing
application. Normal launches perform no automatic capture.
Use `TINYTORRENT_CAPTURE_REVIEW=smoke` to rerun the recovery journeys without
repeating the Settings viewport or full themes-and-sizes batches.
Use `TINYTORRENT_CAPTURE_REVIEW=add-layout` for the workspace headers and empty/long-link
magnet Add form in English and Spanish, Light and Dark, at the three review sizes.
It also captures the connection overlay with its action at the right and records
workspace bounds before and after; this is presentation evidence with a connected
engine, not a simulated connection failure.
The Add cases invoke Preview with invalid input and retain that input for correction.
They also submit a disposable magnet with a relative download folder, capture the
engine refusal and focus recovery, then correct the folder. One short-window case
retries Add paused successfully; every source and destination belongs to the
disposable store. These cases do not run unrelated journeys.
The English 1040-wide case also captures caption hover, pressed and disabled
visual states in Light and Dark through WinUI's state manager. This checks the
custom template's appearance, not pointer input or OS High Contrast rendering.
Use `TINYTORRENT_CAPTURE_REVIEW=shell` for the title-bar menus, narrow layouts,
themes, selection commands, secondary pages and retained filter state.
It also queries the review window's native `WM_NCHITTEST` response at the app
icon, menu and command centers, and unused caption space. This catches controls
classified as draggable or an icon without system-menu semantics. It sends no
input and establishes only top-level hit classification, not physical pointer
delivery, child input routing or native caption-button actions.
Use `TINYTORRENT_CAPTURE_REVIEW=preferences-layout` for General Settings and
completion feedback in English and Spanish, Light and Dark, at the three review
sizes. It reveals Startup and Default app sections, checks native registration
switches against their observed model state, and leaves Windows registrations
unchanged. It commits and restores one notification switch through its native
control. Completion captures feed a simulated engine notice into the production
UI handler; they do not prove Windows delivery, a real completed download or
Explorer launch.
Use `TINYTORRENT_CAPTURE_REVIEW=search` for native AutoSuggestBox result
submission: unavailable commands, Properties from Settings, speed-limit
navigation, named-setting focus and reopening suggestions. It captures localized
results across themes and sizes; it does not synthesize Ctrl+K keyboard input.
Use `TINYTORRENT_CAPTURE_REVIEW=edits` for ordinary Settings departure,
new input and explicit commits during a pending acknowledgement, and schedule
Save/Discard/Cancel. The acknowledgement races change the preference input
synchronously before the UI thread yields and verify its native display
afterward; ordinary departure uses native editors. A two-field departure edits
Download again when Upload starts saving, then verifies that the first navigation
applies both values. This catches an earlier field being left unsaved after the
departure loop has passed it. The check restores its
fixture values and periods. It does not simulate keyboard delivery or engine
refusal.
Use `TINYTORRENT_CAPTURE_REVIEW=library` only with the disposable library
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
Native
focus also reveals the last header in both flow directions without changing
vertical scroll. This does not deliver physical arrow/End keys. No live traffic
is implied by this paused fixture.
Use `TINYTORRENT_CAPTURE_REVIEW=traffic` only with the disposable loopback
launcher's `traffic-capture.json` manifest. It captures the active workspace,
General, populated Peers, mixed Pieces and nonzero Speed history across the
same matrix. It uses the existing Transfer peer and current engine; it does
not repeat completed-download integrity checks or send desktop input.
Use `TINYTORRENT_CAPTURE_REVIEW=schedule` for exact-minute overnight editing,
validation, cancellation, moving and removal, with the schedule overview,
selection and editor at all three sizes in English and Spanish, Light and Dark.
Use `TINYTORRENT_CAPTURE_REVIEW=desktop` with the disposable desktop launcher for
declining Exit with unfinished input and reconnecting an open tracker draft.
Its restart handshake affects only the launcher's own fixture engine; it does
not automate the desktop or establish native notification/power behavior.
Use `TINYTORRENT_CAPTURE_REVIEW=details` for the native priority, tracker,
live-language and preference recovery journeys without repeating the full
layout matrix. It captures populated Trackers in both languages and themes at
the three review sizes after saving several URLs and tiers through the real
editor, then restores the original list. The disposable fixture stays paused;
this establishes populated layout and edit outcomes, not live tracker responses.
`details-files` limits correction captures to Files and localized selected
Settings choices; it does not repeat those behavioral journeys.
Use `TINYTORRENT_CAPTURE_REVIEW=files` with the disposable files launcher for
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
design principles take precedence when preferences conflict. Record both the
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
shutdown during relocation. These scenarios guide relevant checks, not a suite
to run after every edit. Report untested conditions honestly.
