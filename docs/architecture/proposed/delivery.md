# Delivery plan

Implement the [selected architecture](README.md) in complete slices. This plan
does not request immediate product changes, authorize desktop launches, or turn
the historical review reports into additional contracts.

## Governing goal

The owner's goal is easy-to-read, low-bloat code, not preservation of the current
structure. Refactor when it materially simplifies the code, removes duplication,
clarifies ownership, or makes responsibilities easier to understand. Avoid
abstraction or function splitting only when it adds indirection, ceremony, or
navigation cost. Do not preserve bad code merely because fixing it counts as a
redesign. Apply that judgment to work already underway and to deferred issues.

First inspect the current working tree and related issues: Settings, schedule,
search and capture work was changing while all three folders were written.
Build on that work rather than restoring the reviewers' earlier baseline.
No slice requires a new project, general service interface or test framework.
The [retained evidence](evidence.md) records the old call paths and measurement
limits, so these slices do not depend on the original review folders. Recheck
current code and issue status before implementing a historical deletion target.

The sequence is not a requirement to finish all UI cleanup before addressing
responsiveness. Projection publication can ship immediately. Its one hard
dependency is that the Queue settling exemption follows it, because removing
the hold first makes the old per-row rebuild sequence more expensive.

## 1. Protect input and establish the Settings owner

Fix the late-acknowledgement overwrite in
[#112](https://github.com/SynoSoftware/TinyTorrent2/issues/112) first; this can
ship independently. Capture submitted input and retain any newer draft after
success, refusal or a snapshot. Keep controls enabled and do not auto-submit
subsequent text. A second Enter or discrete choice is explicit commit intent:
retain its value while the first save settles, even if more text is typed later.

Make ordinary valid field departure commit before leaving Settings, and continue
the original navigation after a successful save. Implement the
[committing-edits ruling](../../interface.md#committing-edits) for invalid input
and refusal; check that Back is not lost and unsuccessful departure restores
normal admission without automatic retry. Closing must not bypass admission.
Discrete choices retain their latest committed selection; they are not treated
as unsubmitted text. Reopen Settings after both valid and invalid departure to
verify the resulting value. Explicit schedule drafts retain their own guard.

Then route every speed-limit entry point to Settings. Remove SpeedLimits,
LimitChoice, ShowLimits, LimitsRequested and their lifetime/capture branches.
Give preference fields explicit kinds and sections, centralize conversions and
settings submission, and remove `_settings`, duplicate readers and obsolete
pending flags once callers have moved. Carry language's immediate publication
and latest-choice behavior through the move. Keep effective alternative mode
separate from its saved preference: an explicit normal-mode command must reach
the engine when a schedule currently selects alternative, even if normal is
already the saved value.

Move schedule state and its whole-list submission into a concrete owner while
making these edits, rather than scheduling a separate file-only shuffle first.
Ordinary preference saves and period-list saves remain different transactions.
Settings presentation fixes can accompany this slice where they touch the same
fields, but are not an excuse to rewrite every form.

**Evidence:** use the existing Settings/search/recovery journeys. The important
new outcome is that editing again during a held save reply preserves the second
draft and confirms only the submitted choice. Check invalid numeric input,
rate conversion and a rapid language choice without creating a test for every
field. A save that displays correctly is not by itself proof of correct durable
units; reuse the existing settings/restart evidence where conversion changes.

**Contracts:** follow the settled [Settings/editor behavior](../../interface.md#committing-edits)
and [Limits destination](../../interface.md#main-window). Preserve
`docs/localisation.md`'s publish-before-save behavior. #112 owns a late
acknowledgement overwriting newer input; #120 owns departure/commit behavior.
They are distinct defects sharing this implementation slice, not duplicates.

## 2. Settle modal lifetime and recovery

Implement [#115](https://github.com/SynoSoftware/TinyTorrent2/issues/115) against
the remaining dialogs. Delete per-dialog completion bookkeeping, repeated
hide/wait/theme lists, restoration flags and copied deferred-Add decisions.
Keep specific content construction, validation and native behavior direct.

Implement the chosen Save/Discard/Cancel prompt for unfinished explicit editors
through each editor's existing action and outcome, following the
[owner ruling](../../interface.md#committing-edits). Await that outcome before
continuing the original transition; this adds no shared draft interface or
duplicate submission path.
After resolving that editor, recheck remaining draft owners before closing.
Detach the native prompt before a required follow-up confirmation uses its slot;
continue awaiting the whole operation and exclude unrelated dialogs meanwhile.

**Evidence:** reuse Exit/Keep-input, Add and file-operation journeys. Exercise
unsuccessful closing after the editor is hidden and confirm the same valid
unfinished editor remains reachable. Verify completion does not race outstanding
submission and cleanup. Adapt diagnostic callers rather than keeping legacy
fields solely for them. Cover all three prompt outcomes, a failed Save and the
existing delete confirmation using the relevant journeys. Desktop execution
still requires explicit authorization.

**Contracts:** preserve close/source admission from the engine and protocol
contracts; update only changed ownership or interaction statements.

## 3. Consolidate selection and feedback

Implement [#116](https://github.com/SynoSoftware/TinyTorrent2/issues/116): all
product callers use one accepted-selection transition, the window loses its
rollback copy, and meaningful inspector refusal outcomes are consumed.
Keep membership reconciliation and unavailable-target drafts.

In the same presentation owners, derive writability and separate connection
feedback from command errors. Centralize failure formatting and the Torrent
error predicate where their callers are migrated. This is not a mandate to
introduce a single connection enum or reorganize every partial file.

Apply the interface's [feedback placement policy](../../interface.md#feedback-placement)
when separating those messages. Keep app-level conditions available across pages
and field/editor failures local. Inventory actual event callers before adding an
overlay: implement one window-owned host only if an identified noncritical event
needs it. Keep engine desktop notifications on their existing path, with no
duplicate in-app toast or new notification runtime.

**Evidence:** declined discard retains accepted selection and input; a target
removed while prompting is not accepted afterward; pending edits do not retarget;
a re-added torrent does not inherit an old draft. A prior command error must not
become the explanation of a subsequent disconnection. Page changes must not hide
an unresolved app condition; a failed command without an editor remains readable
in its separate app-level message. If an overlay is added, check keyboard access,
announcement, dismissal and that it neither moves content nor obscures an active
editor or confirmation. Reuse existing relevant
journeys and extend only missing consequential outcomes.

**Contracts:** update the narrow presentation/source map. Inspect current #20
and #29 before treating the refactor as completion of their UI requirements.

## 4. Remove redundant publication and drawing

Address [#113](https://github.com/SynoSoftware/TinyTorrent2/issues/113) and
[#114](https://github.com/SynoSoftware/TinyTorrent2/issues/114) as independent
changes where possible. They do not need to wait for every preceding cleanup.
They must respect the chosen draft and selection ownership if developed together.

**Working-tree checkpoint, 2026-10-06:** source inspection confirms that the
read-only projection and OneWay binding for #113, the DefinesRowOrder exemption
for #125, and separate rate fields for #48 are already present. Continue from
them; do not reimplement them. Dead Search, the duplicate queue read and the
possibility of an extra RefreshView after source publication remain. Retain a
signal for changed reorder eligibility even when the row sequence stays equal.
Status-field widths are still minima. The graph now places its scale label
right-aligned above the plot, removing the old label column. Reported compilation does not
establish runtime acceptance; see [verification limits](evidence.md#verification-still-needed).

Publish one completed changed projection as a read-only list property with a
OneWay ItemsSource binding; preserve row instances and avoid publication for
unchanged membership/order. Delete the incremental collection loops and confirmed
dead Search state and correct its English/Spanish empty-state wording. Replace
queueChanged's second JSON read with an old/new eligibility comparison and retain
the required batch signal. Adapt direct diagnostic callers.
Use a plain ordered Torrents list with the existing dictionary index, retaining
equal-QueueOrder tie order. Keep TableView's source contract and reconciliation.

After Settings ownership settles, separate window-only refresh from rare broad
propagation. Quiet unchanged Preferences snapshots and use schedule-specific
signals; do not introduce a hidden-page gate or mandatory per-row comparisons.
Call ObserveUpdates once per applied snapshot and on relevant preference changes
so daily checking and immediate disable remain intact. Retain gesture invalidation.

Once projection batching has landed, exempt the row-order column from settling
and remove unnecessary native focus/selection churn on unchanged rebuilds. When
a real handoff is necessary, restore physical row focus, which may differ from
the current item. These library changes can accompany #54 item 2; they do not
complete its other items or the separate #56 issue.

Stabilize status layout using #48's fixed-width rate fields, preserving the
selected grouping. Keep the graph's scale label above the plot; do not restore
the removed label column. Stable peer/tracker rows follow the existing Torrent
pattern inside Inspector, scoped to its session/target/context. Bind General
to the target's downloaded/remaining formatting. Do not bundle an asynchronous
detail-query rewrite.

**Evidence:** inspect capture/rebuild counts with TableView attached for one
bulk filter or startup publication, then check unchanged telemetry, selection,
queue drag and sort behavior. Move to bottom and a torrent completing near the
top are important bulk-reorder paths. Also check the last downloading row
completing while two other rows remain reorderable: unchanged source order must
not leave its move cursor or an active drag stale. Repeated Move commands within one settling
interval must show each confirmed Queue order promptly. Inspect focus and
automation events when a non-Queue refresh leaves rows in place; CPU timing
cannot establish their behavior. Check equal-queue ties after removal and addition,
filter eligibility changing with status, and rejection of old Inspector replies
after retarget/reconnect. For Week, check that an unrelated snapshot causes no
draw, a genuine change reaches it, and an invalid drag cancels. Check daily-update
deadline handling and disabling during an in-flight check by focused reasoning
and relevant existing coverage; do not wait a day for a test.

A short comparable workload is enough; do not turn source operation counts into
claimed UI milliseconds. The #106 WM_NULL probe was a throwaway and is not in
the repository. If fresh responsiveness evidence is needed, use available
profiling or recreate a small temporary probe outside the product. No permanent
capture framework is required, and desktop launch remains explicitly authorized.

**Contracts:** replacing a source list only when changed already fits TableView's
contract. Amend section 9 for the selected DefinesRowOrder settling exemption,
with its reason. Preserve the existing native-view notification and gesture
rules. When touching RebuildView, correct any surviving comment that claims a
drag cancels only when rows move: source acceptance/RefreshView follows the
control's broader cancellation rule. A new control batching API is not needed.
Do not change live-sort
animation as part of this work without the separate visual evidence identified
in the proposal.

## 5. Replace mechanical binding code

Preserve the existing language publication path, migrate one representative form, then
remove equivalent manual label assignments from touched forms. Use TwoWay
binding only for ordinary synchronous values. Keep prompt/commit logic, focus
and necessary menu/header updates explicit. Decide numeric control choice per
field; do not delete useful stepping to remove a helper.

**Evidence:** compilation verifies binding syntax; a live language change checks
visible and newly shown content, dialog text, accessibility metadata and retained
input/focus. Do not build once per form: settle a coherent implementation batch
and build the affected target once. No text or XAML-source assertion suite.

**Contracts:** the catalogue and live-language behavior remain unchanged.
Diagnostic mode parsing and removal of proven orphans can accompany their direct
caller changes; the remaining diagnostic cleanup is owned by slice 8.

## 6. Consolidate native edit behavior

Implement [#117](https://github.com/SynoSoftware/TinyTorrent2/issues/117) as one
private committed-edit operation used by merge and explicit Edit. Remove the old
duplicate save/apply paths in the same change. Do not merge Changes with Store or
change operation legality just to reuse a public command entry point.

**Evidence:** reuse committed-file and merge/restart checks for replacement,
explicit empty trackers, compound tracker/priority persistence, delayed effective
completion and storage refusal. Build the affected native target after all edits
settle, inspect what actually recompiled, and follow the existing transfer/launch
restrictions. The proposal itself ran none of those checks.

**Contracts:** preserve the engine's intended-field, identity and asynchronous
outcome requirements. No wire change is needed.

## 7. Finish the contained history extraction

Implement [#118](https://github.com/SynoSoftware/TinyTorrent2/issues/118)
independently and at lower priority. Move history's state with its algorithm,
remove unrestricted history access from Engine::State, and leave cadence and
session identity with the engine.

**Evidence:** compile the affected target and inspect preservation of existing
retention, gaps and partial-minute output. Use relevant existing coverage. Add a
test only for a specific unguarded failure introduced by the move; there is no
test obligation for class placement or named constants.

## 8. Reduce diagnostic maintenance

[#124](https://github.com/SynoSoftware/TinyTorrent2/issues/124) owns this cleanup;
it can proceed independently and alongside the slices that change capture
callers. Inspect each existing journey against the
[testing policy](../../testing.md#what-earns-a-test): retain consequential,
otherwise unguarded outcomes; remove redundant layout sweeps, obsolete
implementation assertions and repeated checks of an already-covered outcome.
Recording a finding is not itself grounds for deleting its regression check.

Parse modes once, adapt retained journeys to the new owners and remove their
old field dependencies in the same changes. Keep the in-process pixel capture.
Do not add a separate test project, public test-only members or a permanent
performance probe. Release packaging remains a separate decision.

**Evidence:** the final diff identifies what each retained or removed journey
protects, which overlap was eliminated and which existing check still guards
the outcome. Source-only cleanup does not require running every journey or
meeting a line-count quota. Run only the relevant authorized check if changed
behavior needs it. This gives capture growth an owner beyond enum parsing.

## Completion criteria

For each slice, read the whole final diff and follow the common action from its
entry point to its outcome. The old competing path is gone; a caller does not
coordinate multiple copies of the same decision; stored state represents a fact
or an intentional draft; any new type hides a concrete responsibility.

Follow [testing](../../testing.md) for evidence and build scope. Report source
review, compilation, runtime checks and measurements separately. Update the
active contract when behavior changes, retain these documents as the proposal
record, and link subsequent implementation from the existing GitHub issues.
Do not create new issues for items already covered by another review.

## GitHub issue map

Checked against GitHub on 2026-10-06. All sixteen review issues #112–#127 relate
directly to this proposal. They are the implementation tracking, not a second
architecture to adopt wholesale. The table records how each fits the selected
design; GitHub retains current status and subsequent implementation evidence.
No issue is completed merely because its proposal appears here.

| Issue | Selected scope | Placement and qualification |
| --- | --- | --- |
| [#112 — Save acknowledgement loses newer input](https://github.com/SynoSoftware/TinyTorrent2/issues/112) | Preserve later input while confirming the submitted choice. | Slice 1; may ship as an independent correctness fix. Distinct from #120. |
| [#113 — Bulk projection rebuilds](https://github.com/SynoSoftware/TinyTorrent2/issues/113) | Publish a changed read-only projection once. | Slice 4 can start independently; land before #125. |
| [#114 — Unrelated scheduler redraws](https://github.com/SynoSoftware/TinyTorrent2/issues/114) | Quiet unchanged settings and use relevant schedule signals. | Settings work plus #126. A visibility gate and hidden dirty state are not required. |
| [#115 — Dialog lifetime and recovery](https://github.com/SynoSoftware/TinyTorrent2/issues/115) | One complete interaction lifetime; restore the valid suspended editor after unsuccessful close; implement Save/Discard/Cancel through existing editor actions. | Slice 2, after Limits removal reduces its scope. Do not stop completion at ShowAsync or infer the suspended editor solely from draft presence. |
| [#116 — Accepted selection ownership](https://github.com/SynoSoftware/TinyTorrent2/issues/116) | One product transition for selection and inspector consequences. | Slice 3, building on dialog recovery; retain model membership and identity checks. |
| [#117 — Shared committed edits](https://github.com/SynoSoftware/TinyTorrent2/issues/117) | One private merge/Edit commit-and-apply implementation. | Slice 6; independent native work, preserving compound persistence and asynchronous completion. |
| [#118 — Speed-history ownership](https://github.com/SynoSoftware/TinyTorrent2/issues/118) | Group existing state with its retention/aggregation algorithm. | Slice 7; lower priority, no algorithm change or wider engine breakup. |
| [#119 — One settings owner](https://github.com/SynoSoftware/TinyTorrent2/issues/119) | Delete Limits; consolidate fields, conversion, readers and submission. | Slice 1. Preserve language's special orchestration; choose numeric controls per field instead of requiring blanket replacement. |
| [#120 — Commit Settings on departure](https://github.com/SynoSoftware/TinyTorrent2/issues/120) | Save valid ordinary input and revert invalid input without a prompt; refused saves keep Settings open with the field error. | Slice 1 with #119 and #112, because they touch the same code. Shared implementation does not make the defects duplicates. |
| [#121 — Bind translated text](https://github.com/SynoSoftware/TinyTorrent2/issues/121) | Replace mechanical assignments where binding preserves live language behavior. | Slice 5. Verify the existing publication path; no mandatory new Strings notification system. |
| [#122 — Schedule owner](https://github.com/SynoSoftware/TinyTorrent2/issues/122) | Move period state and whole-list transaction into one concrete owner. | Coordinate within slice 1 after inspecting ongoing edits; no mandatory file-only precursor or new project. |
| [#123 — Shared failure formatting](https://github.com/SynoSoftware/TinyTorrent2/issues/123) | One conversion from failure to user-facing text in Strings. | Slice 3 or the direct caller migration; no new type. |
| [#124 — Diagnostic code and modes](https://github.com/SynoSoftware/TinyTorrent2/issues/124) | Parse modes once and remove demonstrated redundant/obsolete checks. | Slice 8 and direct caller migrations in earlier slices. Do not delete valuable journeys because a finding was recorded; Release packaging remains separate. |
| [#125 — Queue settling delay](https://github.com/SynoSoftware/TinyTorrent2/issues/125) | Exempt DefinesRowOrder and amend the control contract. | After #113; retain live-value settling and existing animation. |
| [#126 — Routine child refresh](https://github.com/SynoSoftware/TinyTorrent2/issues/126) | Window-only routine notification plus broad propagation for relevant transitions. | After Settings/access ownership is clear. Preserve daily update checks; this does not require flattening all connection facts into one enum. |
| [#127 — Inspector row and formatting ownership](https://github.com/SynoSoftware/TinyTorrent2/issues/127) | Stable peer/tracker instances and target-owned General formatting. | Inspector follow-up in slice 4; preserve session/target/context guards and do not claim observed flicker without checking it. |

Related older issues also constrain these changes:

- [#20](https://github.com/SynoSoftware/TinyTorrent2/issues/20) owns connection
  and command feedback. Separate their facts and presentation; the issue's phase
  proposal does not supersede the distinction between startup readiness,
  last-known engine state and local closing.
- [#48](https://github.com/SynoSoftware/TinyTorrent2/issues/48) already selects
  fixed-width rate fields and their grouping. Preserve that decision.
- [#54](https://github.com/SynoSoftware/TinyTorrent2/issues/54) overlaps only in
  its focus/rebuild item. The other selection defects and
  [#56](https://github.com/SynoSoftware/TinyTorrent2/issues/56) retain separate
  acceptance work; the Queue settling change does not complete them.
- The remaining Add, accessibility, chrome and Settings-layout issues are
  product acceptance work. The architecture may help their implementation, but
  structural cleanup is not evidence that those user-visible defects are fixed.

Reconcile issue wording that still proposes a different mechanism with these
decisions before implementing that part. Keep its concrete failure and acceptance
criteria unless the owner's product decision changes them. Closed historical
issues are context, not automatically reopened work. Retired review references
point to this proposal and immutable archived sources; this consolidation does
not create duplicate issues or mark implementation complete.
