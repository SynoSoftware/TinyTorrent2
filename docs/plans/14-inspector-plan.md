# Shared inspector lifecycle

Status: implementation in progress. The responsiveness checkpoint
is commit `efa1972`. This plan applies to the application's one shared inspector,
including Torrents and Library.

Current stage: shared ownership and both selection paths are implemented; focused
source review and validation remain. The plan and its WinUI-specific supplemental
review were independently assessed as sound before implementation. Implementation
review must check the resulting cancellation and retained-content paths before
the one final build.

## Outcome and scope

The table accepts and displays selection without waiting for inspector work.
An open, expanded inspector observes the active workspace's accepted selection,
requests only what its current section needs, and displays results as they arrive.
Closing detaches that observation and cancels its work once. Subsequent selections
do no inspector work. Opening observes the current selection, without replaying
the selections made while closed.

The existing MVVM approach, TableView, domain owners, one pipe connection and
Library database remain. This work corrects presentation ownership and lifetime;
it does not introduce an MVVM framework, event bus or generic data-source layer.
The separate [communication plan](communication-plan.md) stays paused: an active
inspector can consume the existing detail protocol without redesigning the engine.

Assumptions: closed, compact, minimized and off-workspace content have no active
inspector demand. Compact presentation retains its target, section and unfinished
edits. Library's visible table rows and explicitly requested background operations
keep their own lifetimes; closing the inspector cancels only work it owns.

## Source findings

| Current path | Problem this plan removes |
| --- | --- |
| `MainViewModel.Select` calls `Inspector.Show`, `RefreshScope` and `Refresh`. | Selection orchestrates inspector loading and presentation instead of publishing accepted state. |
| `RefreshScope` clears data, invalidates work and can send a release while the inspector is closed. | A closed inspector still incurs housekeeping for subsequent selections. |
| `Library.Browser.Select` starts `ReadDetail`; that read checks page visibility rather than inspector visibility. | Selecting Library rows can read inspector details while the inspector is closed. |
| `RefreshInspector` computes torrent and Library activity separately; Library omits minimization. | The same pane has different lifetime rules depending on its content. |
| `InspectorPane` binds torrent content and separately observes Library content. | One visual shell lacks one presentation owner for activation and publication. |

The checkpoint already moves expensive preparation off the UI thread, cancels
obsolete work, publishes peer batches and retains displayed content. Preserve
those behaviors while removing the orchestration that made the coupling possible.

## Ownership

| Owner | Responsibility |
| --- | --- |
| TableView | Input, selection visuals, focus and generic cancellable menu preparation. It knows nothing about the inspector. |
| Workspace selection owners | Accepted selection and its change notification: `MainViewModel` for Torrents and `Library.Browser` for Library. They do not reset, load or refresh inspector content. |
| Shared inspector view model | Active workspace observation, section, requested subject, displayed content, cancellation lifetime and publication of current results. One owner applies these rules across the app. |
| Torrent and Library detail modules | Domain-specific reads, preparation and commands using their existing pipe, store and provider owners. They receive the active cancellation lifetime rather than independently deciding whether the inspector is open. |
| InspectorPane | Bind the shared inspector's presentation and render the appropriate content using the existing pane, section and detail controls. |

Evolve the current inspector ownership into a shared presentation owner under
`Shell`; keep torrent-specific behavior in `Torrents` and Library-specific behavior
in `Library`. Move the existing implementation rather than wrapping it with a
second orchestrator. The shared owner's interface covers activation, section
choice and guarded departure; callers do not learn read/reset/refresh ordering.
Use existing concrete owners and .NET notifications. A new interface or target
type must express an actual distinction required by both content kinds, rather
than hide them behind `object`, strings or a speculative plugin system.

Implementation: the existing `Inspector` moves into `Shell`, with its shared
selection lifecycle in `Inspector/Selection.cs`. Torrent rows, files, pieces and
maps keep their existing domain implementations; Library still reads through
`Browser`. No second inspector class or forwarding facade is introduced.

## Lifecycle and publication

1. The workspace resolves an unfinished-edit guard when necessary, commits its
   accepted selection and lets the table display it. This path never awaits data
   loading, parsing, menu preparation or inspector rendering.
2. While active, the inspector observes accepted selection. Its notification
   handler only cancels obsolete work and schedules the latest subject after the
   selection render turn. Synchronous event delivery must not reintroduce loading
   or collection processing into the selection call stack.
3. The scheduled work captures the current workspace, subject and section. It
   uses available cached facts and starts only the reads needed for that section.
   Parsing, file access, store queries and expensive preparation stay off the UI
   thread. UI-bound changes return to the dispatcher and retain the 16 ms work
   budget. That budget is not a promised click-to-paint latency.
4. Another selection or section change cancels pending callbacks, reads and
   preparation for the old subject. Results are accepted only for the current
   activation, subject and section. Already accepted engine requests may finish;
   cancelling the inspector does not cancel unrelated consumers or durable edits.
5. Keep requested and displayed subjects distinct while replacing content.
   Existing tables and headings remain until a useful replacement batch is ready;
   never blank a populated view or insert a spinner over it. Publish the first
   useful batch, then subsequent batches, using existing row identity and native
   transitions. Retained content's commands must not act on the new subject.
6. Closing, collapsing, minimizing or leaving the active workspace detaches its
   inspector observation, cancels its outstanding work and releases its demand
   once. No selection-driven inspector refreshes or deferred publications continue
   while inactive. Cached content and guarded drafts may remain inert in memory.
7. Opening, expanding, restoring or switching workspace observes that workspace's
   current accepted selection and activates only its current section. The previous
   content's observation and demand end before the new content starts.

Library Info and File follow these same rules as torrent sections. Read stored
details only for active inspector content; keep automatic visible-row enrichment,
explicit Fetch metadata and app-wide downloads with their existing owners. Reuse
stored results without starting a second fetch for the inspector.

Preserve the established exceptions to following selection: an inspected torrent
that merely leaves a filter remains inspectable, explicit all-torrent or selected
peer scopes keep their meaning, and unfinished edits retain their departure
guards. These are inspector subject rules, not reasons for selection to drive
loading. Properties and Show in Torrents use the same shared activation path;
file reveal continues to expand, select and scroll the intended file.

## Implementation order

1. **Establish the shared owner.** Trace torrent and Library detail callers,
   separate inspector-only reads from table and explicit-operation consumers,
   and move activation and publication ownership into the shared inspector.
   Verify by source review that the pane has one activity decision, including
   minimization, and one current publication lifetime.
2. **Make selection publish state.** Attach selection observation only while
   inspector content is active. Remove direct inspector reset/read/refresh calls
   from both selection paths and the corresponding summary-selection repair path.
   Preserve the small guarded-departure transaction. Verify that closed-inspector
   selection reaches no inspector callback or read and active observation does
   only cancellation and scheduling before returning.
3. **Migrate both content kinds completely.** Route torrent sections and Library
   Info/File through the shared lifetime. Remove superseded visibility switches,
   duplicate context rules and view subscriptions. Keep progressive publication,
   retained content and subject-correct commands. Verify cancellation and publication
   across selection, section and workspace changes, including inactive states.
4. **Review and validate final code.** Perform the repository's focused review
   passes, resolve findings, then build once for the remaining runtime questions
   and reuse that output. Run only the focused checks justified below. Update
   architecture, workspace, torrent and Library contracts to their final ownership,
   then remove this completed plan and its guide link.

Each stage replaces its old path in the same change; no compatibility route or
parallel inspector implementation remains. Implementation is complete only after
both Torrents and Library use the shared lifecycle.

## Verification and completion

Source review must establish one authority for activation and publication, no
inspector orchestration in selection, and no inspector subscriptions or new reads
while inactive. Compiler and review evidence cover ordinary binding and ownership
changes; do not add a general test framework or run unrelated capture modes.

Extend the existing focused selection check only for failures it does not already
cover: Library detail reads while closed, reactivation on the latest selection,
and stale publication after a workspace switch. Use disposable data. Observe
inspector-owned requests separately from legitimate table enrichment; an inactive
inspector must issue none after its one-time release. A failure changes the
lifetime implementation before completion.

Use the same final output for a focused real click-to-selection observation in
Torrents with Peers visible and in Library with Info visible. The question is
whether inspector work still delays the visible selection; remaining stalls
require tracing the UI work responsible rather than another scheduling constant.
Report actual click/visual evidence separately from setter time and accessibility
notification timing. Retain existing valid checkpoint evidence instead of
repeating it without a changed behavioral question.

The result is accepted when both workspaces select without awaiting inspector
work; inactive inspector selection performs no inspector work; reopening follows
the latest selection; obsolete results cannot publish; and useful content remains
visible through progressive replacement. Existing edit guards, scope rules,
Properties, file reveal and shared pane state continue through the same owners.
