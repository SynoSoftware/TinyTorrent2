# Settings prototype: implementation notes

Companion to [settings-options-prototype.html](settings-options-prototype.html).
Recorded 2026-10-08. Read both before implementing these Settings changes.

The HTML demonstrates presentation and interaction with sample data. This file
preserves decisions, reasons, ownership boundaries, and limitations that are easy
to miss in the HTML. Neither file proves production integration or performance.
This handoff was checked against source; no application was run or compiled.

For delivery order, existing owners, field coverage, and completion criteria,
use the [implementation plan](../docs/settings-implementation.md).

## Start here

1. Use **C: Search and index** as the selected direction: category cards and
   search, with D's compact setting rows. D remains a useful direct category
   view; A, B, and E are comparison variants, not additional product modes.
2. Read the [documentation guide](../docs/README.md),
   [interface contract](../docs/interface.md), and
   [WinUI instructions](AGENTS.md). Reconcile the specific differences listed
   below before changing their production owners. This companion is a design
   handoff, not a replacement engine or protocol contract.
3. Trace each proposed control to its existing owner. The HTML's `sections`
   array is the field inventory: labels, grouping, advanced visibility,
   dependencies, help, units, and sample values. Its identifiers are prototype
   identifiers, not proposed wire keys or persistence names.
4. Preserve working behavior while adopting the design. In particular, preserve
   the scheduler, background addition, individual pause intent, and existing
   settings that the HTML only partly demonstrates. Record actual integration
   and verification separately from this prototype.

The product direction is a capable, compact Windows app. qBittorrent was a
source of useful options, not a requirement to reproduce every menu or setting.
An attractive placeholder is not evidence that its backend feature exists.

## Navigation, disclosure, and editing

- Keep the existing app chrome. Settings uses the title-bar Back button and
  Alt+Left; an additional "All settings" row wastes vertical space. Back from
  Connection setup returns to Speed limits and restores the previous scroll
  position and focus to its launching row.
- There is **one Show advanced options switch**. It reveals advanced rows,
  cards, and the Advanced category together. Advanced options may belong inside
  an ordinary category; the Advanced tab is not a dump of every uncommon option.
  Hiding advanced options does not reset their values or stop their effects.
- Search covers all settings, including hidden advanced settings and familiar
  synonyms. Selecting a hidden result reveals it and goes to its real owner;
  search is not a second editable copy of Settings.
- Ordinary settings use the existing individual commit and error behavior.
  There is no page-wide Save. Use the
  [committing-edits contract](../docs/interface.md#committing-edits) for pending,
  invalid, rejected, and uncertain edits; HTML assignments are not that protocol.
- Connection setup is a **Settings child page**, because it has measurement,
  temporary suspension, a proposal, and explicit Apply/Cancel. Proxy editing
  remains a compact dialog because its related fields must save together.
  Folder selection and default-app registration use their existing Windows
  surfaces. These tasks do not need the same container merely for consistency.

## Stable rows and shared alignment

The owner calls unexpected movement "shifting sand." A setting's state must not
move the controls below it just because help, validation, or a dependent value
changes. Keep dependent rows present and disable them when unavailable. Examples
are the incomplete folder, default versus last-used folder, proxy fields, and
sleep while seeding. Reserve space for state/error feedback when it is needed.
The explicit advanced switch and a responsive layout change are intentional
changes of composition, not reasons to allow ordinary state changes to reflow.

Labels, values, buttons, and work-surface messages stay on one line. Use native
end ellipsis with the full content accessible. Prefer short, clear wording and
adequate control widths; a clipped ComboBox choice is not acceptable. Help belongs
in native tooltips and accessibility descriptions, following the interface
contract. The multiline file-pattern editor contains user input; it is not
permission to wrap setting descriptions.

**Alignment is shared per visible tab/category, across all its cards.** This
supersedes the earlier per-card experiment:

| Visible content in the category | Numeric fields | Switches |
| --- | --- | --- |
| At least one field with a displayed unit or unit selector | Share a value column and reserve one trailing unit column, including for unitless fields | Track's right edge aligns with the numeric box's right edge; On/Off starts in the unit column |
| No fields with displayed units | Keep the ordinary right alignment without a reserved unit column | Keep their ordinary position; do not move them for this rule |

Determine this from the rows currently shown, not all fields in the model.
Recompute when advanced visibility changes. A disabled but visible unit still
occupies space. Unit selectors such as minutes/hours must fit the same trailing
column. Switch tracks retain their normal size. Other controls, such as paths
and full-width choices, keep their appropriate widths.

The HTML owns this once in the `.settings-category` layout rules. The all-settings
comparison view scopes it separately to each category. In WinUI, put this decision
at the shared category layout and let shared rows consume it; individual cards
must not measure independently or carry compensating margins. Reuse
[SettingsRow](src/Controls/SettingsRow.cs) and
[SettingsSection](src/Controls/SettingsSection.cs) rather than adding a parallel
settings framework. HTML pixel values illustrate alignment, not a requirement
to replace native control metrics.

## Controls, icons, and help

- Use native WinUI controls, theme resources, and the app's existing shared
  button/dialog styles. The prototype's browser controls, hard-coded palette,
  simulated caption buttons, and floating experiment toolbar do not ship.
- Unlimited-capable numeric fields accept a limit or Unlimited. Clearing removes
  the limit; keep a usable clear affordance on the focused, nonempty field.
  Preserve numeric validation and accessible naming. Choose the native edit
  control that supports this behavior; the browser's custom clear button is not
  a new control architecture. Map the UI's unlimited value through the existing
  engine contract, rather than assuming every engine limit uses HTML's zero.
- Changing duration units changes the presentation of the same duration, not
  its meaning. Preserve invalid input until corrected rather than silently
  changing a setting or losing the draft on a refresh.
- Use Lucide's bundled glyphs and established names from
  [Lucide.cs](../lib/Lucide/src/Lucide.cs). The HTML now loads the bundled font;
  recreate icons through the existing native integration, not hand-drawn SVGs.
  Its `icons`, section `icon`, and `categoryIcons` mappings show the reviewed
  choices. Categories have their own icons, independent of their first card.
- In Speed limits, Transfer limits uses sliders, Standard limits a gauge,
  Alternative limits switching arrows, Queue an ordered list, and Peer
  connections a network. Connection setup's navigation row has a trailing
  chevron and no redundant leading gauge. Its Test action can use the gauge.
  Reuse a glyph for the same meaning; variation for its own sake is not a goal.
- Use native `ToolTipService` behavior. Tooltips must not remain open merely
  because a mouse click left a control focused. Avoid unnecessary tooltips that
  repeat fully visible labels. Preserve keyboard access to full text and help,
  but do not reproduce the prototype's extra tab stops on every table value.
  The HTML's hover/focus timers are browser emulation, not production code.

## Where features belong

This is a placement guide, not a claim that every backend feature exists.
Consult `sections` for the complete field list instead of maintaining a second
copy of every label and range here.

| Category | Scope and implementation cautions |
| --- | --- |
| General | Default-app integration; sign-in/background startup, splash and start-paused choices; exit confirmation; notifications; sleep prevention on mains power; updates. Closing a window and exiting the engine remain distinct operations. |
| Transfers | Background versus dialog-based addition, start/queue placement, folder layout and duplicates, skipped files, download/incomplete folders, suffix and allocation, seeding stop rules, watched folders. Defaults for new torrents must not relocate existing data or rewrite existing torrent choices. |
| Network | Adapter, incoming port/mapping, encryption, transport and address family, connection attempts, proxy and peer discovery. Reuse the existing network and proxy owners, including private-torrent restrictions. |
| Speed limits | Connection setup, active speed-limit mode, standard caps, queue limits, peer limits, alternative caps, bandwidth accounting. Queue activity and transfer speed are separate concepts even though they share this category. |
| Schedule | Weekly schedule and its existing editing capabilities, plus a compact summary of the actual standard and alternative speeds. |
| Appearance | Language/theme, optional title-bar speeds, external IP and free-space visibility in the status bar. Title-bar speeds must fit the custom chrome; the status bar/tray remain available when title-bar space is insufficient. |
| Advanced | UI refresh interval, memory/open files, torrent checking, and engine-owned speed-history granularity. These controls have different owners despite sharing a category. |

Opening a torrent file or magnet link can add it without opening WinUI. The
background choice still honors the chosen destination, queue, pause, and
duplicate policies. An optional added notification provides feedback from the
tray; it is not a reason to open the main window. File associations, startup,
notifications, update checks, and sleep prevention use the existing desktop
integration. The prototype's sample dialogs and messages do not implement them.

## Connection setup

### Page and proposal

Keep the page inside Settings, with measurement/manual speed entry and preset
choice on the left and Current/Proposed settings on the right. When those
sections no longer fit comfortably, stack measurement first and proposal
second. Base the transition on content width. No splitter or scrolling modal
is required. The page body may scroll on a small window; Apply/Cancel stay in
the persistent footer, aligned and reachable.

Test, Cancel (test), and Retry occupy one fixed action slot. Progress is directly
under it in reserved space. Avoid fabricated percentages or transfer counts;
show indeterminate progress unless the engine can supply meaningful progress.
Normal progress is inline, not a toast or dialog. Announce phase changes for
accessibility without announcing each countdown tick.

Users can test or enter download/upload capacity manually in **Mbps**. Reduced
and Balanced require valid capacity in both directions; Full speed can remove
caps without it. Measurements create a proposal; they never apply automatically.
Emphasize changed cells and subdue unchanged ones while keeping rows in a stable
order. Keep peer limits visible as preserved values. Apply is enabled only when
the proposal is valid, actually changes settings, and no test phase is busy.

### Preset meaning

The final direction is bandwidth plus queue activity, with **peer connection
limits preserved**. Earlier suggestions to scale peers or queues continuously
with Mbps were not adopted. Queue limits must recover when moving from Reduced
to Balanced or Full speed; simply leaving Reduced's small queue unchanged fails
the owner's requirement.

The current HTML's candidate policy is:

| Preset | Download/upload caps | Active downloads | Active seeds | Active overall |
| --- | --- | --- | --- | --- |
| Reduced | 50% of each entered capacity | 2 | 2 | 3 |
| Balanced | 85% of each entered capacity | 3 | 5 | 8 |
| Full speed | Unlimited bandwidth | 3 | 5 | 8 |

These are prototype policy values, not validated performance recommendations or
claims about qBittorrent/libtorrent defaults. Keep the agreed behavior above;
validate the final policy against TinyTorrent's actual queue owner before
shipping. Full speed means no bandwidth cap, not unlimited queue activity.
The percentage caps do not guarantee bandwidth availability or torrent speed.

**Custom is derived**, not a fourth saved profile. Compare the current two
standard bandwidth caps and three queue limits with the chosen policy and
capacity. Manual changes show Custom when they no longer match a preset;
matching again can restore its name. Peer limits are outside this policy, so
editing only those does not make it Custom. Preserve individual editing of all
limits; Custom must not be an extra gate before controls become available.

Apply only the proposal's intended changes through the existing settings owner.
Alternative caps, saved schedule periods, memory, slow-torrent handling, and
seeding stop rules stay intact. The HTML preserves an active Weekly schedule;
otherwise it proposes Standard limits so the generated standard caps take
effect. That mode change is included in the preview. Changing limits must not
silently lift an independent pause.

The prototype converts Mbps to KiB/s before comparing its settings. Production
must use the engine's canonical rate units and deliberate rounding; otherwise
the preview can show a change that disappears on apply or falsely show Custom.

### Test and temporary suspension

The revised lifecycle deliberately supports testing twice without starting and
stopping torrents between successful tests:

1. Temporarily suspend torrent traffic for the test and wait for it to stop.
2. Measure download, then upload.
3. After success, show results and retain the temporary suspension for up to
   **three minutes**, with a visible resumption countdown.
4. Test again during that interval using the existing suspension. A successful
   retest starts a new three-minute hold; editing fields alone does not extend it.
5. Leaving Connection setup, applying, cancelling the page, or expiration of the
   hold releases the suspension. Explicit test cancellation, failure, or stopping
   timeout restores activity immediately through the restoring phase.

Restoration means **release this operation's temporary suspension**, not Resume
all. Preserve individual paused/running intent, queue order, and independent
global, schedule, or adapter restrictions. Account for policy changes made
during the operation rather than replaying a stale snapshot of torrent states.
Do not individually pause every torrent or persist a new user pause mode.

The engine owns suspension, its deadline, and recovery: losing or closing the
window must not strand transfers. Browser timers, a synthetic zero speed, and a
UI completion message are not proof that peer traffic stopped or was restored.
Use the existing pause/policy owner and verify its real quiescence semantics;
setting queue limits to zero is not sufficient. Restoration failure must remain
an honest recoverable state rather than falsely announcing completion.

**The HTML test is entirely simulated.** Its short timers, failure selector,
200/40 Mbps result, and sample speed display exercise UI states only. No speed
test provider or production integration was selected. M-Lab NDT7 was discussed
as a candidate, not an approved dependency. A provider still needs a deliberate
integration decision; until then, manual capacity entry is the real input path.

## Polling, history, and memory are different controls

| Control | Owner and meaning |
| --- | --- |
| Advanced → Interface → Refresh interval | WinUI's periodic requests for fresh engine data. Display/edit in milliseconds; prototype default 1000 ms, range 1000–10000 ms. This does not change engine timing, transfers, or history sampling. |
| Advanced → Speed history → Recent sample interval | Engine-owned aggregation for the five-minute history. Prototype choices: 1, 5, or 10 seconds. |
| Advanced → Speed history → Older sample interval | Engine-owned aggregation for the 24-hour history. Prototype choices: 10 or 30 seconds, 1 or 5 minutes. |
| Disk write buffer and checking memory | Engine/libtorrent resource targets with separate purposes; neither is a total app-memory cap. |

Current [PipeClient](src/Services/PipeClient.cs) still creates a fixed one-second
refresh timer. Adding the prototype field did not change it. Keep command-driven
refreshes responsive and the [protocol's](../docs/protocol.md#snapshots-and-detail)
bounded, command-priority read behavior when making the periodic interval
configurable. Do not apply it to unrelated preview or engine timers by accident.

History belongs to the engine, continues while WinUI is closed, is session-wide,
and remains bounded in memory. UI polling never supplies its samples. The
current [SpeedHistory](../engine/inc/SpeedHistory.h) and
[history contract](../docs/engine.md#state-and-work) use fixed cadences; exposing
granularity requires real engine work. Coarser storage loses detail; choosing a
finer interval later cannot recreate it. The treatment of already stored samples
when the interval changes remains to be specified at that owner.

Follow [disk-write caching](../docs/engine.md#disk-write-caching) rather than
copying a qBittorrent memory setting by name. The pinned libtorrent 2.1 design
does not provide the old configurable disk-cache size. Lowering a write buffer
can reduce queued-write memory, with a throughput tradeoff; it cannot promise a
maximum process working set or control Windows' cache.

## Scheduler: preserve the working feature

Schedule retains its own category and existing engine behavior. The compact
standard/alternative download/upload summary sits above the week, with Edit
leading to the existing Speed limits fields, so users can see what those names
mean without adding another editor below an already tall schedule.

Preserve local-time periods, overnight continuation, overlap precedence, and
standard limits outside special periods through the existing schedule owner.
The HTML demonstrates editing saved periods while the schedule is off; changing
the mode retains them. Its week, simple period editor, Add/Remove/Undo, and
sample data do **not** cover the production scheduler's complete interaction.
Keep the existing drag creation/move/resize, keyboard routes, save ordering,
validation, and focus behavior described in the
[interface contract](../docs/interface.md#settings). Reuse the existing scheduler
and period editor rather than rewriting them from this browser demo.

## Differences to reconcile during implementation

These findings prevent the implementer from silently choosing whichever file
was read last. They are not permission to erase working behavior or reopen the
owner's settled UI decisions.

| Area | Prototype/session direction versus current contracts |
| --- | --- |
| Placement and scope | This prototype places Queue and Peer connections in Speed limits and expands Advanced. The current interface contract still places Queue in Transfers and names a smaller Advanced inventory. Update the relevant contract alongside adoption; do not classify every sample field as already implemented. |
| Dependent rows and help | The owner's stable-layout and tooltip-only decisions govern. Older Settings prose still mentions revealing dependencies and visible descriptions. Read it with the newer text/row rulings, not as a reason to reintroduce wrapping help. |
| Schedule when inactive | HTML keeps saved periods editable while inactive; current interface prose shows a neutral fixed-mode week and hides the period editor outside Weekly schedule. Resolve this presentation difference explicitly while preserving saved periods and scheduler capabilities. |
| Adding a schedule period | HTML uses a new-period draft with Save/Cancel. The current production contract creates/opens a default period and applies edits individually. Preserve the existing implementation unless this difference is deliberately resolved; copying HTML would change the journey. |
| Queue implementation | HTML's explanatory text mentions how slow torrents count. Verify it against the actual queue/policy implementation, rather than assuming raw libtorrent `active_*` settings have exactly that behavior. The prototype must not introduce a second queue owner. |
| New asynchronous operation | The speed-test suspension/recovery lifecycle needs engine/protocol support. It is not implemented by the HTML. |
| Cadences and policy values | Configurable polling/history and the preset table extend fixed current behavior. Keep the ownership boundaries above; validate ranges/defaults at the real owner and update the corresponding contracts when implementing. |

## Focused acceptance review

Use [testing guidance](../docs/testing.md) to choose evidence when implementation
is authorized. The current prototype work carries an explicit no-run/no-compile
instruction; these are handoff criteria, not authorization to launch anything.

- C retains search, category index, compact rows, one advanced switch, and every
  existing feature. Search can reach an advanced setting without a duplicate editor.
- Across Speed limits, numeric fields and switch tracks align even in different
  cards. Across a category with no units, controls keep their ordinary position.
  Advanced on/off recomputes this once for the category. Check duration selectors
  as well as plain units, disabled rows, narrower widths, and larger text.
- Changing folder mode, incomplete-folder use, validation, and switch state does
  not add a wrapped row or displace neighboring controls. Clear removes only
  unlimited-capable limits. Full choice labels and paths remain accessible.
- Tooltips dismiss normally after pointer/focus changes and do not obscure the
  page merely because the last clicked control remains focused. Native keyboard
  navigation and meaningful shortcuts still work.
- Reduced → Full speed restores queue capacity; manual controlled changes show
  Custom; peer-only edits preserve preset identity. Apply cannot commit invalid
  or unchanged proposals. Scheduled periods, peer limits, and seeding rules survive.
- Success, retest, cancellation, failure, timeout, navigation, and window loss
  all release only the test's temporary suspension at the appropriate time.
  Previously paused torrents stay paused; a restored state is confirmed, not timed.
- Changing UI refresh leaves engine history continuous while the window is
  closed. History settings affect engine aggregation only. Memory help makes no
  total-memory guarantee.
- Scheduler gestures, keyboard editing, pending/invalid saves, overnight periods,
  and preserved schedules work at least as well as before the Settings changes.
- Native English/Spanish, Light/Dark/High Contrast, and text scaling follow the
  existing [localisation](../docs/localisation.md) and interface contracts. The
  prototype's English-only language choice is not localisation evidence.
