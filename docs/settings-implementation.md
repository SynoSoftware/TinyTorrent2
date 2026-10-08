# Settings implementation plan

## First directive: use common sense

**Serve the user's task, not the plan.** Apply
[Usability comes first](architecture.md#usability-comes-first) before each slice:
this plan can contain mistakes, stale source assumptions, and unnecessary work.
Its purpose is to preserve the intended outcome, not make every proposed
mechanism mandatory.

- Name the user-visible result and the failure the change prevents. Inspect the
  current owner before editing; if the behavior already works, reuse it and
  record the evidence instead of implementing it again.
- Choose the smallest complete change using established Windows patterns and
  existing code. Simplify, combine, reorder, or remove unnecessary implementation
  steps when the agreed behavior is preserved. Update this plan with the reason
  so the next implementer does not restore the discarded work.
- Treat the owner's settled decisions and working features as constraints.
  "Common sense" is not permission to drop the scheduler, change the selected
  layout, or silently omit an agreed capability. A conflict with an owner ruling
  or a material change to scope or behavior needs a concrete finding resolved
  with the owner; continue unaffected work meanwhile. Routine implementation
  choices do not need another approval.
- Keep proof proportional to the change. Verify the consequence that could go
  wrong, rather than creating a framework or test suite to satisfy a heading.

Prepared 2026-10-08 against the current working tree. Implementation status below
separates source changes from verification. The owner has authorized Debug x64
builds and the app's off-screen capture review against a disposable store.

## Implementation status — 2026-10-08

The integrated native settings build in Debug x64. The settings capture now passes
English/Dark and Spanish/Light journeys, including atomic preset submission,
navigation and shutdown. File-policy verification passes admission, restart,
recycle recovery, watcher deduplication and seed limits. Actual connection
measurement now passes with successful hold/release, cancellation and preservation
of independent pause intent. Requests use the provider's tested 10 MB sample tier.
The evidence and its scope are listed below. The prototype remains the design
reference; no browser UI is shipped.

| Stage | Current source status |
| --- | --- |
| 1: Baseline | Existing keys and effective defaults traced before adding settings. Missing keys preserve existing transfer behavior; prototype sample values do not replace production defaults. |
| 2–3: Native presentation | Index/search, compact cards, one advanced switch, category-wide unit/switch alignment, native tooltips, and existing settings are integrated. Queue and peer controls are in Speed limits. Schedule has its own category, cap summary, and visible inactive state; existing period editing is retained. Duration fields have minutes/hours selectors. Fractional inputs use native TextBox controls so display rounding cannot create drafts or alter saved precision. |
| 4: Additional controls | Network/discovery, overall queue and peer limits, slow-torrent handling, bandwidth accounting, checking concurrency, UI refresh interval, title speeds, and free-space visibility are connected to their existing owners. |
| 5: History | Engine aggregation supports both interval settings, retains completed timestamped samples, and bounds partial/completed history by time and count. A headless runtime check demonstrates five- and ten-second aggregation, preservation across a change, and saved intervals after restart. The window refresh interval does not change engine sampling. |
| 6: Manual Connection setup | Native responsive child page, editable connection capacity, proposed/current values, derived preset identity, and atomic intended-change submission are integrated. No simulated test is exposed. |
| 7: Addition/file policies | Admission defaults, launch pause, layout, duplicate handling, exclusions, recycle/permanent deletion, inactivity/combined seed rules, watched folders, and completion verification are connected to engine owners. The focused file-policy check passes restart/intent, payload preservation, recycle recovery, watcher deduplication and Any/All inactivity limits. |
| 8: Connection testing | Native WinHTTP measurement, temporary session suspension, three-minute hold, retest, cancellation and restoration are implemented. Actual measurement, explicit release, cancellation, preservation of an existing pause, restoration after failure, successful retest without releasing suspension, real three-minute expiry, and client-disconnect restoration pass. |
| 9: Integration evidence | Debug builds pass without warnings. Settings captures pass both language/theme cases, atomic preset submission, no reading-created drafts, navigation and shutdown. A separate Windows High Contrast capture at 200% text passes both languages; inspected pictures confirm native cards, category-wide unit alignment, readable duration selectors, Connection setup stacking and stable actions. Detail reads remain a separate workstream. |

Stage 7's fields have native controls, persistence and engine behavior, with
the focused file-integrity and restart evidence listed here. This does not
claim every settings combination was exercised.

### Decisions applied in this slice

Current evidence under `artifacts/evidence/`:

- `Capture-settings-prototype-d3722cbe-791a-4a8a-bd5f-5f55450b32de`:
  passing native journeys and inspected wide/dark, narrow/light captures.
- `SettingsFiles-c3161ae2-78b8-457f-9910-ebaef494af82`:
  passing focused admission, persistence, recycle, watcher and seeding checks.
- `ConnectionTest-3316bfc2-cace-4856-8eda-4c16a6f56342`:
  cancellation and failure restoration pass; engine log identifies HTTP 403.
- `ConnectionTest-e5234852-7882-4676-a46b-0f4f54e92dcb`:
  corrected real measurement succeeds (approximately 392.5/191.7 Mbps), enters
  Holding with 179 seconds remaining, and releases successfully. Cancellation
  and preservation of an existing session pause also pass.
- `ConnectionTest-3edecbf3-99fa-4fee-80e9-fc8c08c46e92`:
  successful retest retains suspension and resets the hold; the actual
  three-minute expiry and client disconnect both restore engine activity.
- `SettingsHistory-158a1d79-1494-4929-91d6-715ee9b12200`:
  headless aggregation changes from five to ten seconds without erasing
  completed samples; both history intervals and the UI interval survive restart.
  Exact timestamp spacing is not a standing assertion: samples record actual
  engine observations, including partial buckets at configuration boundaries.
- `Capture-settings-prototype-3b2d45fc-a3a8-4525-8d12-a7456cd3eb8b`:
  Windows High Contrast and 200% text, confirmed in capture metadata. Both
  language journeys pass. The review corrected short value columns, preset
  wrapping and narrow duration-unit selectors. Original Windows preferences
  were restored and the disposable processes closed.

Connection-test phase changes use the window's existing accessibility
announcement path, without announcing every countdown tick. A new engine's
idle snapshot clears the preceding session's test failure. The prototype's
redundant "Find a setting" label is removed; native search already uses its
placeholder and accessible name.

### Acceptance scope

The field inventory below is implemented through the named native controls
and engine owners. Existing registration, startup, notification, sleep, update,
proxy and scheduler operations remain with their established implementations.
New admission and file policies share those operations rather than adding a
second foreground/background path. Source tracing covers validation, defaults,
commit ordering, refusal and disconnect paths; the runtime evidence above
covers the new data-integrity and lifecycle risks.

This record distinguishes those source guarantees from measured results. It
does not claim every combination of settings or an optimal bandwidth policy
for every connection. No total-memory or performance improvement is claimed.
Subtitles and the independent detail-read redesign remain outside this delivery.

Request-level WinHTTP probes isolated the failure to a 16 MiB download request;
both directions accept the documented 10 MB sample. The engine now uses that
per-request ceiling, with the existing 128 MiB total ceiling per direction.
The normal Debug app and engine build without warnings, including typed deletion
mode and the small dialog declaration grouped with its SpeedLimit owner.

- Window refresh is 1,000–10,000 ms through the existing PipeClient timer.
  Operational window preferences use the existing settings store; they do not
  create a second configuration file or add fields to window placement.
- Presets own the two standard bandwidth caps and three queue limits. Reduced
  uses 50% and 2/2/3; Balanced uses 85% and 3/5/8; Full speed removes bandwidth
  caps and uses 3/5/8. Returning from Reduced therefore restores useful queue
  activity. These are explicit policies, not measured performance recommendations.
  Peer limits, alternative caps, saved periods, and individual pause intent stay
  unchanged. A saved weekly schedule stays selected; other modes switch to
  standard limits when applying the proposal.
- Preset identity is derived from confirmed controlled values and stored
  connection capacity. It becomes Custom when those values no longer match.
  There is no saved preset profile. Capacity facts and all proposed changes use
  the existing atomic settings submission, with no Apply for an empty proposal.
- Production defaults retain unlimited overall activity and unlimited
  per-torrent connections. A finite per-torrent connection limit starts at two,
  matching the pinned libtorrent contract. Slow thresholds remain 2 KiB/s and
  protocol overhead remains included. These differ deliberately from HTML
  examples because an upgrade must preserve existing behavior.
- Last destination changes only after a successful admission. Preallocation is
  chosen when preparing a new source; an already prepared addition keeps its
  storage mode. Neither choice moves existing files.
- Wire settings additions and connection testing use protocol version 10. The parallel detail-read
  document and implementation remain with their owner. Review peer-detail
  invalidation at network changes when that implementation becomes available;
  this slice does not introduce a competing cache or query path.

Source checks cover JSON parsing, duplicate resource keys, matching English and
Spanish placeholders, XAML structure and literal resource references, and diff
whitespace. Source review also corrected outgoing interface filtering, explicit
Torrents versus Back navigation, and policies on pending additions. These checks
do not establish native binding compilation, rendered layout, or runtime safety.

### Earlier source trace — superseded by integration evidence above

Traced the changed UI input, search, navigation, settings submission, engine
validation/persistence, policy application, addition, polling, history, and
window-lifetime paths, including refusal and disconnect handling. Fixed:

- Inactive schedule editing was closed by every engine snapshot; the obsolete
  fixed-mode close path is removed. Unavailable engine state now reports unknown
  schedule status instead of implying the weekly schedule is active.
- Slow-torrent inputs displayed KiB/s but used byte/s maxima. The input maximum
  now derives its units at the Setting owner.
- Three empty numeric XAML resources could fail page loading; their intended
  theme opacity values are restored.
- Ordinary Settings entry skipped the index, and saved index state restored
  General. An absent category now denotes the index in navigation and placement;
  existing saved category numbers retain their meaning.
- Index navigation lacked keyboard focus transfer, and existing search results
  retained the previous language. Both follow their current page/text state.
- Connection setup could finish navigation after the person left Settings;
  the continuation checks the active page.
- A completed Connection setup Apply could return to Settings during window
  closure. The return now respects closure and dialog state; queued focus also
  checks that Settings is still the active content.
- A destination acknowledgement could briefly show free space from the old
  volume. Visibility now requires the sample path to match the confirmed folder.
- Optional title speeds could use an over-wide child layout as their fit test;
  the calculation now uses available caption width.
- The existing engine check's handshake expectation now matches protocol 10.

The engine trace found no additional source-level faults in queued-save ordering,
failed admission, restored/pending handles, policy propagation, or history bounds.
Queue/checking help now states that forced transfers can bypass these limits.
Structural checks and diff checks passed before native verification. The
integration evidence above records subsequent builds, captures and runtime checks.

### Ownership and naming review — 2026-10-08

- SettingsPage has one traversal for field discovery, disclosure, and visible
  row alignment. Its category panels come from XAML; only the persisted enum
  mapping remains explicit because stored ordinals differ from visual tab order.
- MainViewModel's limit owner supplies schedule status and mode labels. Setting
  owns confirmed numeric access; Connection setup does not interpret raw JSON.
- TransferPreset names the transfer policy rather than its current editor.
  Capacity validation and queue calculations have one implementation. The private
  Limits record keeps the five values of one proposal: two bandwidth caps and
  three queue limits. Splitting its constructor into arbitrary wrappers would
  obscure that cohesive result.
- DiskSpace owns local-volume queries and unavailable/network-drive handling for
  the two actual callers. Neither caller delegates filesystem policy to another
  view model. There is no interface, cache, or forwarding wrapper around it.
- PipeClient owns and disposes its refresh timer with its other lifetime
  resources. SetRefreshInterval retains three words to distinguish configuring
  the cadence from requesting an immediate snapshot. Existing Show-prefixed
  settings retain the established preference vocabulary.
- ApplyPolicy takes a torrent handle; its name does not repeat that argument's
  scope. The history Range stays private and owns aggregation for the two actual
  ranges, rather than adding separate recent/day implementations.

The review removes redundant notifications and capability forwarding while
preserving settings keys, saved choices, navigation behavior, and transfer policy.
Connection setup uses explicit validation and status branches, named proposal
arguments, and one change-set calculation per Apply. Category lookup, advanced
disclosure, and saved-page restoration use straightforward control flow.
Subsequent verification uses the owner's authorization for Debug builds and
off-screen capture; see the integration evidence above.

## Outcome and scope

**Owner ruling:** The Subtitles tab is excluded from this goal. Its separate
implementation is not a prerequisite or part of Settings acceptance here.

Implement the selected Settings experience from
[the prototype](../app/settings-options-prototype.html) and its
[implementation notes](../app/settings-options-prototype.md): C's search and
category index with compact rows, one advanced switch, shared tab-wide alignment,
native controls, and the complete agreed feature inventory.

Keep the existing application, settings, protocol, scheduler, and transfer
owners. Replace affected presentation in place; do not ship competing Settings
pages, a preset-profile store, or a browser-based implementation. Each stage
below ends with a usable integrated slice and its own completion criteria.

Existing stored choices survive. Sample HTML values are not migration defaults:
for example, native `show_add` defaults to true while the prototype initially
selects background addition. Missing new keys use deliberate backward-compatible
defaults; they must not change an existing installation's transfer behavior.

This plan covers all 77 prototype fields and the scheduler. It also preserves
existing native behavior absent from the sample. Menus and tray actions retain
their existing command owners; their sample HTML is not a separate rewrite task.
Coverage means accounting for each field, not treating an HTML placeholder as
authorization for an arbitrarily large subsystem. Keep a field's status explicit:
implemented, remaining, or excluded by an owner decision. A stage cannot quietly
declare it unnecessary and drop it from the inventory.

## Existing owners to extend

| Concern | Existing source and intended responsibility |
| --- | --- |
| Settings presentation | [SettingsPage.xaml](../app/src/Views/SettingsPage.xaml), its code-behind, [SettingsRow](../app/src/Controls/SettingsRow.cs), and [SettingsSection](../app/src/Controls/SettingsSection.cs). Own composition, shared row geometry, native input, and focus. |
| Settings values and submissions | [Settings.cs](../app/src/Views/Settings.cs). Reuse confirmed values, drafts, conversion, validation, pending edits, and intended-change submission. |
| Window preferences | [Placement.cs](../app/src/MainWindow/Placement.cs) is the current saved-view path to inspect. Reuse suitable existing persistence, but do not force unrelated settings into a placement type merely because it writes a file. Keep one clear owner for each preference and operational settings at the engine. |
| Scheduler | [Schedule.cs](../app/src/Views/Schedule.cs), [Scheduler.cs](../app/src/Controls/Scheduler.cs), and [PeriodEditor.xaml](../app/src/Controls/PeriodEditor.xaml). Reuse their editing and save behavior. |
| Engine settings | [Settings.cpp](../engine/src/Engine/Settings.cpp), [State.h](../engine/inc/Engine/State.h), and [Policy.cpp](../engine/src/Engine/Policy.cpp). Own accepted values, persistence, and effective policy. |
| Addition and files | [AddDraft.cs](../app/src/Views/AddDraft.cs), engine Additions/Previews/Files, and [Desktop/Application.cpp](../engine/src/Desktop/Application.cpp). All entry paths use the existing admission and file-operation decisions. |
| UI refresh | [PipeClient.cs](../app/src/Services/PipeClient.cs). Its current periodic refresh is one second. Preserve the command-priority, bounded read path. |
| Engine history | [SpeedHistory.cpp](../engine/src/SpeedHistory.cpp), [SpeedHistory.h](../engine/inc/SpeedHistory.h), and engine Session. The engine samples and aggregates while WinUI is closed. |
| Chrome and status | [Chrome.cs](../app/src/MainWindow/Chrome.cs), MainWindow, and MainViewModel. Reuse the live speeds and current status instead of adding another telemetry stream. |
| Text | Existing app/engine English and Spanish catalogues, through their current Strings owners. Use the [localisation contract](localisation.md). |

Recheck these locations when beginning a stage: the working tree contains active
changes outside this task. Their presence does not authorize reverting or
reformatting them. This plan introduces no project split or general-purpose
settings, polling, or background-job framework.

## Parallel work: inspector detail reads

The related work is [Inspector detail reads](architecture/detail-reads.md)
(`detail-reads.md`, referred to in discussion as `details_read.md`). It is still
marked under review. That work owns asynchronous detail queries, retained
section data, query/alert correlation, invalidation, and held-reply recovery.
Settings consumes its eventual read contract; it does not build another detail
cache or redesign its transport. Leave that document's design to its editor.

Most Settings work can proceed independently. Coordinate these specific joins:

| Settings work | Parallel boundary |
| --- | --- |
| Stages 1–3: layout, controls, scheduler | Proceed. Settings owns its page and shared row layout; detail reads owns inspector collection. Coordinate any edits to shared MainViewModel/navigation code instead of replacing each other's version. |
| Stage 4: UI refresh interval | Settings owns the preference and PipeClient cadence. Preserve the detail-read scheduling contract. D9 currently proposes no window or protocol change; if its performance findings change that, integrate the accepted client path before wiring the interval. |
| Stage 5: history | SpeedHistory aggregation can proceed separately. Serialize edits to engine Session/Tick and the inspector's history presentation with the detail-read work. History remains engine-owned and is not a retained torrent-detail section. |
| Stages 4, 7, 8: settings, operations, test suspension | Changes that alter retained detail must use the detail-read owner's invalidation mechanism at their effective boundary. Network reconnection and temporary test suspension can invalidate Peers even though neither is a per-torrent edit. |
| Shared engine/protocol integration | State.h, Alerts.cpp, Session.cpp, Commands.cpp, shutdown/disconnect handling, and protocol definitions need one active writer per overlapping edit. Sequence those edits and inspect the combined result; separate checkouts alone do not resolve behavioral conflicts. |

### Refresh means UI demand, not engine sampling

The current path is `PipeClient.Refresh` → snapshot →
`MainViewModel.Apply` → `Inspector.Observe` → the visible section's read.
Thus the configurable interval affects routine list refresh and indirectly
visible detail demand. Immediate reads on selection, reopening, or a completed
edit retain their existing behavior; hidden sections do not acquire a new timer.
Neither this interval nor the detail cache changes engine history sampling.

D3 proposes a usable-copy age of about three seconds. With the 1,000–10,000 ms
UI setting, longer intervals can make each detail read wait for fresh data.
Keep freshness at the detail-read owner: do not enlarge its age limit merely
to hide a wait, add a second poller, or claim that lowering refresh frequency
fixes a blocking read. The held-reply performance question remains with that
work's Q1; its proposed timing targets are not newly imposed requirements here.

### Shared alert and lifecycle assumptions

Read D2, D4–D8 before touching their callers. In particular, D6 currently uses
`query_save_path` to distinguish detail status from routine status. History and
Settings changes must preserve the agreed routine query flags. A new operation
must not issue or consume the same uncorrelated alert queries independently of
the detail owner. If a real new caller invalidates an assumption, resolve it
there rather than duplicating correlation logic.

At integration, review invalidation for new network settings and temporary test
suspension/restoration, plus the existing edit/removal paths. Invalidate affected
data, not every section on every UI preference change. Disconnect and shutdown
must perform both responsibilities: finish/cancel pending reads and release any
test-owned suspension. Neither cleanup path replaces the other.

Before implementing an overlapping change, identify the current writer and the
agreed boundary. Land the detail-read behavior before finalizing Settings code
that depends on it, then review the combined diff. Coordinate any actual wire
changes and the protocol version; D9's proposed unchanged detail fields do not
authorize unversioned Settings additions. No new queue, pipe, or coordination
framework is needed for this collaboration.

## Decisions before dependent work

These decisions are local gates, not reasons to hold up unrelated UI work.

| Decision | Planned disposition and reason |
| --- | --- |
| Layout and disclosure | Settled: C's index/search plus compact rows; one advanced switch; tab-wide numeric/switch alignment only when visible units exist. Do not reopen this during implementation. |
| Schedule editing outside Weekly schedule | Adopt the prototype's ability to inspect/edit saved periods without activating them, with a clear inactive state. Preserve the native scheduler's gestures, draft handling, and ordered saves. Reconcile the older hide-when-inactive contract when doing stage 3; the prototype is not a replacement scheduler. |
| New schedule period | Keep the native create/open-default-period and individual commit behavior. The HTML's new-period Save/Cancel is a demonstration shortcut, not a request for a second schedule editor. |
| Preset policy | Use the handoff's 50%/85%/uncapped and 2/2/3 versus 3/5/8 table as the initial candidate. Validate units, supported limits, combined queue behavior, and recovery from Reduced before enabling Apply; this is not a requirement to discover optimal performance for every connection. Keep peers unchanged and explain caps without promising speed. Existing defaults remain unchanged until a person applies a preset. |
| History interval change | Recommended implementation: retain timestamped completed samples, end partial aggregation at the change boundary, and start new buckets with the new interval. Apply time/count bounds without inventing missing detail. Record this policy at the engine owner before stage 5; do not erase the chart merely to simplify configuration. |
| Real connection testing | Provider selection and real traffic-quiescence evidence gate stage 8. M-Lab was a candidate, not a selected dependency. Compare a small native integration against existing dependencies and provider requirements before committing. Manual Connection setup can ship first; simulated Test cannot. |
| Features exceeding a simple setting | Watched folders and new file/seed policies require dedicated slices in stage 7. If pinned libtorrent or existing operations cannot support a prototype choice reasonably, report the concrete gap and a smaller alternative; do not silently omit the choice or build an unrelated subsystem. |

Contract changes belong with their implementation stage. Resolve differences
listed in the [handoff](../app/settings-options-prototype.md#differences-to-reconcile-during-implementation)
explicitly; preserve fixed owner rulings and report any remaining conflict.

## Delivery order

Stages 1–3 establish the native presentation and preserve existing behavior.
Stages 4–5 add settings and telemetry controls at their owners. Stage 6 delivers
manual Connection setup once its queue controls exist. Stage 7 finishes the
remaining transfer policies. Stage 8 adds real measurement and safe temporary
suspension. Stage 9 establishes final integration evidence. Stage 8's provider
gate does not block manual setup or the other settings.

This is a delivery sequence, not a dependency chain or a required commit count.
Stage 6 needs stage 2's navigation and stage 4's overall queue limit, but not
history configuration, the other network controls, or watched folders. Stage 5
is independent of presets. Finish and verify coherent slices as they become
ready; stage 9 consolidates evidence instead of postponing all verification.
The parallel detail-read work gates only the shared integration points above,
not the entire Settings implementation.

### 1. Establish the field and persistence baseline

- Verify the inventory below against the current app settings, engine codec,
  desktop registration, and libtorrent policy. Mark each as reuse, extension,
  or new behavior; distinguish an existing operation from an exposed setting.
- Preserve all existing settings keys and saved torrent intent. Add only the
  new settings needed by each later stage, with validation at their real owner.
  UI labels and field identifiers do not rename wire keys.
- Record production defaults and units at those owners. Existing unlimited
  conventions differ by libtorrent setting; centralize each boundary conversion
  instead of passing HTML's zero directly to every API.
- Confirm native search/navigation and saved-view restoration paths before
  moving controls, including persisted Settings categories.
- For each new field, state whether it affects existing torrents, future
  additions, or only the window. Set its missing-key default to the current
  effective behavior, including libtorrent defaults currently left implicit.
  A new switch must not turn an existing engine capability off on upgrade.

**Complete when:** every field below has a known owner and delivery stage,
existing values have a preservation path, and unsupported capabilities have
specific findings rather than silently missing controls.

### 2. Shared Settings shell and row layout

- Implement the category index, search, horizontal category selection, and single
  advanced switch in the existing SettingsPage. Search reaches advanced fields;
  navigation reveals their actual row. Keep category, focus, draft, and return
  position through normal navigation and refresh.
- Add the category-wide value/unit alignment decision once to the shared layout.
  Numeric rows and native switches consume that same geometry across cards.
  Determine unit presence from visible rows, including disabled ones, and update
  it when advanced visibility changes. Preserve switch placement when no units
  are visible. Do not use separate per-card measurements or local margin fixes.
- Keep row height stable for errors, dependencies, and changing state. Use native
  trimming, tooltips, accessible full text, and clear behavior for unlimited
  inputs. Retain native keyboard semantics and existing shortcuts.
- Apply the reviewed Lucide category/card/action mappings and shared button
  styling. Retain acrylic and the established decorative gears. Remove prototype
  experiment controls from the native design.
- Add matching English/Spanish text as each native surface is implemented.

**Complete when:** all existing settings remain editable through the new shell;
advanced disclosure and search work; numeric boxes and switch tracks align across
cards without affecting tabs that have no units. No backend behavior changes are
needed for this stage.

### 3. Place existing controls and preserve the scheduler

- Move queue and peer limits to Speed limits using their existing Setting
  instances. Separate standard and alternative caps and keep one owner for the
  active mode used by Settings, menu, and tray.
- Give Schedule its own category. Place the compact actual standard/alternative
  speed summary above it, with Edit returning to their existing fields.
- Embed the existing scheduler and period editor. Adapt inactive schedule
  presentation as above; do not change execution merely by opening/editing it.
  Preserve drag creation/move/resize, keyboard operation, overlap precedence,
  overnight periods, save ordering, invalid drafts, and focus recovery.
- Reuse native proxy editing, default-app repair/registration, startup, background
  addition, notifications, sleep prevention, and exit behavior. Sample dialogs
  must not replace these richer working operations.

**Complete when:** no original setting or scheduler gesture is lost; changing
category or speed-limit mode preserves periods; summary speeds match the single
settings owner; the existing installed-handler and background paths still work.

### 4. Add bounded settings and window preferences

Implement small coherent groups rather than one large protocol change:

- Queue/network controls: overall active limit, per-torrent peer cap,
  slow-torrent settings, connection attempts, transport/address family,
  discovery, and bandwidth accounting. Extend the existing policy owner and
  apply per-torrent session defaults to existing/new handles where the setting
  promises that scope. Respect private torrents, force-start behavior, adapters,
  and proxy restrictions.
- Checking concurrency: bind it through the same settings/policy path. Reuse the
  already implemented disk buffer, file pool, checking-memory, and hash-thread
  controls with accurate units and memory claims.
- Window preferences: title-bar speeds and free-space visibility. Allocate title
  bar width using existing chrome constraints, including search and caption
  buttons; omit optional title-bar speeds when they cannot fit. Use live values
  already available to the window.
- UI refresh interval: persist a window-owned millisecond value, default 1000,
  initially accepting the prototype's 1000–10000 range. Change only PipeClient's
  periodic UI refresh, including live changes and reconnects. Maintain one
  refresh loop, at most one in-flight refresh, and immediate command-driven
  updates. Account for the inspector reads currently triggered by snapshots,
  following the parallel-work agreement above. Leave engine ticks, preview
  polling, and history sampling alone.

Each engine setting extends load/save, accepted changes, application of policy,
confirmed snapshots, and native UI as one slice. Version changed messages through
the existing protocol contract. A control is not complete when only its value
can be persisted; its promised operational effect must exist.

**Complete when:** each new control has its real effect, survives the appropriate
restart, rejects invalid input without losing drafts, and leaves unrelated
settings intact. Existing installations keep their prior policy until edited.

### 5. Configurable engine-owned history

- Extend SpeedHistory's aggregation intervals for the recent and day ranges using
  the choices in the handoff. Keep the engine's one-second input sampling and
  transfer execution independent of both storage aggregation and UI refresh.
- Persist the selected intervals through engine settings, while sample history
  itself remains in-memory and session-wide. Use timestamp/range and count bounds
  appropriate to the finest supported cadence so closed WinUI cannot cause
  unbounded accumulation.
- Implement the interval-change policy selected above. Preserve gaps, handle
  clock discontinuities consistently with the existing history owner, and adapt
  the chart to returned timestamps rather than assuming uniform one-second or
  one-minute spacing.
- Wire the two Advanced controls and update the engine/history contract.

**Complete when:** the engine continues bounded history while WinUI is closed;
reopening retrieves it; changing UI refresh does not change its samples; changing
storage granularity neither invents detail nor silently changes transfer timing.

### 6. Manual Connection setup and derived presets

- Add the native Settings child page and one concrete draft owner for capacity,
  preset selection, proposal, and navigation. Use the existing settings submission
  path; no saved preset profiles or second settings transaction system.
- Implement one recommendation calculation used by preview and Apply. Convert
  units deliberately; compare canonical values to avoid rounding-only changes.
  Derive the preset indicator from the two standard caps and three queue limits.
  Persist the capacity facts needed to derive it after reopening; do not persist
  the computed preset name. Use suitable existing window preference storage for
  those inputs, keeping unsubmitted edits in the page draft. Base the displayed
  applied preset on confirmed settings and the capacity associated with their
  successful application; a rejected Apply must not replace those facts.
- Keep peer limits, alternative caps, periods, slow-torrent behavior, memory,
  and seeding stop rules unchanged. Include any proposed active-mode change in
  the comparison. Changing limits never releases an independent pause.
- Apply intended changes together through the existing engine settings command.
  Failure keeps the proposal editable; an uncertain reply follows existing
  reconciliation rather than announcing success. Apply is unavailable when
  invalid, unchanged, or busy.
- Preserve the two-column-to-stacked layout, fixed footer, stable changed-value
  table, and title-bar Back/Alt+Left return. Leave out the live Test action until
  stage 8 is real; never ship the sample 200/40 Mbps result as a measurement.

**Complete when:** manual setup works end to end; Reduced → Full speed restores
the agreed queue capacity; manual controlled edits produce Custom; peer-only
edits do not; presets remain derived after reopening and failed saves.

### 7. Complete addition, file, and seeding policies

Deliver these as separate vertical slices through existing operations:

1. **Addition defaults:** start-on-add, start-paused-on-launch semantics,
   bring-dialog-forward, queue placement, last-used destination, folder layout,
   duplicate policy, and preallocation. Apply shared defaults to foreground,
   background, and later watched-folder additions. Keep activation intent,
   actual saved pause intent, and the requested start default distinct.
   Start-paused-on-launch applies when the engine starts, not every time WinUI
   reopens; verify that its interaction with the already persisted Pause all
   setting is clear before adding a second preference for the same effect.
2. **File selection and deletion:** skip patterns and the deletion preference.
   Define a small documented pattern syntax using the existing file-selection
   owner; prevent a skipped-everything torrent from appearing to download.
   Preserve existing shared-file checks, confirmation, and Windows Recycle Bin
   behavior. A preference does not grant permission to delete files silently.
3. **Seeding/check completion:** inactivity limit, any/all stop-rule combination,
   and optional verification after completion. Extend existing seed-limit and
   verification operations; avoid repeated checks or automatic re-pausing after
   an explicit user resume. New inactivity and post-download verification default
   to disabled; the any/all combination defaults to the existing stop-rule
   behavior. Preserve existing per-torrent choices and resume exemptions.
4. **Watched folder:** one engine-owned watcher using the shared source admission
   path, independent of WinUI. Handle initial contents, incomplete file writes,
   recursion, duplicate events, and destination/start policy without repeated
   additions. Keep scope to torrent files and the controls shown in the prototype.
   Define source-file handling explicitly before wiring it; preserving source
   files is the default. No script runner or generalized automation subsystem.

Before implementing a larger slice, identify the smallest working behavior and
the existing operations it can reuse. A watcher that can repeatedly re-add a
removed torrent needs its re-admission policy settled, not an ever-growing
collection of retries. Bring that concrete choice to the owner if existing
behavior does not settle it; continue the other slices.

**Complete when:** the same source/defaults produce consistent results through
all supported addition paths; folder-default edits never move existing data;
new seed/check policies respect user intent; watched additions work with WinUI
closed and do not loop on the same source.

### 8. Real connection measurement and temporary suspension

- Select and document a provider/integration after checking its native API,
  traffic cost, data handling, cancellation, and dependency footprint. This is
  bounded research for one operation, not a network optimizer.
- Give the engine one concrete connection-test operation and explicit lifecycle
  states. It owns temporary suspension, measurement, deadlines, cancellation,
  completion/error outcomes, and restoration. Reuse the existing policy owner;
  add neither persisted pause intent nor per-torrent pause/replay loops.
- Establish that peer traffic is quiescent before measuring, with a bounded
  stopping timeout. A session-pause call or zero displayed rates alone is not
  proof. Use genuine operation progress or an indeterminate indicator.
- On success, retain suspension for three minutes while presenting the results.
  Retest within the hold reuses suspension; each successful retest restarts the
  hold. Editing a draft does not extend it. Navigation, Apply, page cancellation,
  expiry, test cancellation, failures, and client loss release the operation's
  suspension through the same owner. Independent pause reasons stay effective.
- Keep long network work off the engine/UI critical path and out of the single
  request slot. Use the existing pipe for progress and completion, not another
  transport. Bound active phases as well as the post-test hold so a lost client
  cannot leave an in-progress operation suspended indefinitely.
- Connect Test/Cancel/Retry to the stable action slot and native inline status.
  Results populate the draft; only explicit Apply changes settings. A restore
  failure must remain visible and recoverable rather than claim completion.
- Keep settings-commit failure and test restoration independent. If Apply fails,
  preserve its draft, but still release temporary suspension on exit or deadline.
  UI editing or a pending settings acknowledgement cannot extend the hold.

**Complete when:** actual measurement works and every termination route releases
only its temporary restriction; previously paused torrents stay paused; commands
and the UI remain responsive; a second successful test avoids unnecessary
resume/suspend cycling. No simulated provider, timing, or progress remains.

### 9. Integration review and delivery evidence

- Reconcile all inventory rows and update the relevant active contracts and
  implementation evidence. Remove superseded direct callers when replacing a
  path; retain one owner for every setting and gesture.
- Review the native page using the handoff's acceptance criteria and compare
  against the prototype for structure, not browser-specific metrics. Include
  English/Spanish, Light/Dark/High Contrast, narrow width, and text scaling.
- Exercise real persistence and error paths where source reasoning cannot prove
  behavior: multi-field preset apply, changed cadences, watched additions, and
  the temporary-suspension lifecycle. Reuse the existing targeted harnesses;
  do not add a generic test framework or tests that just copy the field table.
- Preserve working scheduler and source-admission journeys. Measure memory/CPU
  effects before making resource-saving claims; a smaller buffer or slower UI
  refresh is not evidence of a bounded total working set.
- With the integrated detail-read implementation, check the default interval,
  both sides of its accepted cache-age boundary, and 10,000 ms. Cover cold and
  warm reads, selection changes, reopening the pane, and commands during a held
  read under representative activity. Reuse that work's measurements rather
  than running a duplicate performance study. Verify affected detail refreshes
  after network changes and test suspension, and that disconnect cleanup handles
  pending reads and temporary suspension together. These checks do not waive
  the current execution restrictions.

**Complete when:** every promised field has native UI and actual behavior, known
limitations are explicit, applicable targeted checks pass, and the new Settings
experience preserves existing torrent state and features. A manual-only setup
milestone may complete stage 6, but does not complete the full measurement scope.

## Field coverage and delivery stage

IDs below refer to the HTML inventory, not new production keys. **Reuse** means
an existing native control/operation was found; **extend** means related behavior
exists but the prototype adds a choice; **new** means implementation is needed
for the proposed setting or operation. These are source findings, not runtime
verification. Stage 1 confirms the exact mapping before edits.

| Prototype fields | Baseline and stage |
| --- | --- |
| `default_app`, `startup` | Reuse desktop registration and repair paths; 2–3. |
| `tray`, `splash`, `confirm_exit` | Reuse native startup/closing settings; 2–3. |
| `notify_problems`, `notify_finished`, `notify_added`, `prevent_sleep`, `seeding_sleep`, `updates` | Reuse desktop/engine settings; 2–3. |
| `show_add` | Reuse foreground/background admission preference; 2–3. |
| `start_paused`, `start_download`, `raise_add`, `queue_top`, `layout`, `duplicates` | Extend launch/addition/queue/merge behavior with explicit defaults; 7. |
| `exclude`, `patterns` | New skip-policy setting over existing file selection; 7. |
| `destination`, `use_incomplete`, `incomplete`, `suffix` | Reuse folder/suffix settings and operations; 2–3. |
| `last_folder`, `preallocate` | Extend addition defaults; 7. |
| `deletion` | Extend existing removal/file operations with a preference; 7. |
| `ratio`, `seed_time` | Reuse existing seeding limits; 2–3. |
| `inactive_time`, `seed_rule` | Extend seed-limit policy; 7. |
| `watch`, `watch_path`, `watch_recursive`, `watch_destination` | New engine watcher through existing admission; 7. |
| `adapter`, `port`, `mapping`, `encryption`, `proxy` | Reuse network settings and proxy editor; 2–3. |
| `transport`, `ip_family`, `outgoing_rate`, `dht`, `pex`, `local_discovery` | Extend accepted settings and network policy; 4. |
| `preset` | New manual child page and derived proposal, 6; real Test, 8. |
| `limit_mode`, `download_limit`, `upload_limit`, `alt_download`, `alt_upload` | Reuse effective limit owner, relocate/group controls; 2–3. |
| `downloads`, `seeds`, `connections` | Reuse settings, move to Speed limits; 2–3. |
| `active_total`, `ignore_slow`, `slow_down`, `slow_up`, `slow_wait`, `per_torrent`, `overhead`, `limit_lan` | Extend policy/settings; 4. Current overall active limit is hard-coded unlimited. |
| Schedule category and saved periods | Reuse native Schedule/Scheduler/PeriodEditor, no ordinary field ID; 3. |
| `language`, `theme`, `external_ip` | Reuse existing preferences/display; 2–3. |
| `title_speeds`, `free_space` | Extend window presentation preferences and existing status values; 4. |
| `refresh_interval` | New window preference over existing fixed UI polling; 4. |
| `disk_buffer`, `open_files`, `check_memory`, `check_threads` | Reuse engine resource settings; 2–3. |
| `checking` | Extend policy with checking concurrency; 4. |
| `recheck_finished` | Extend completion/verification behavior; 7. |
| `recent_interval`, `history_interval` | New engine aggregation settings over existing history; 5. |

## Verification and execution constraints

The owner authorized Debug x64 builds and the app's hidden capture review against
a disposable store. Finish related edits before building; reuse passing evidence
and rerun only checks whose behavior changed. `detail-reads.md` remains a separate
workstream and does not block settings delivery.

When execution is authorized, follow [testing.md](testing.md) and root build
rules: finish a coherent slice before building, use affected Debug x64 targets,
respect artifacts lanes and dependency restrictions, and inspect compiled-file
scope and the recursive-output check. Do not launch desktop or transfer tests
without the required authorization. Record what each check establishes rather
than treating a successful build as proof of UI quality or engine recovery.
