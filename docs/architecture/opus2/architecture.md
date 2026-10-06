# Consolidated architecture: the UI update path

This document is the result of the review in this folder. It records what
makes the WinUI app lag, the target design, and the order of work.

| Document | Role |
| --- | --- |
| [findings.md](findings.md) | First source-backed findings |
| [review-platform.md](review-platform.md) | WinUI platform architect |
| [review-state.md](review-state.md) | State ownership architect |
| [review-owner.md](review-owner.md) | Review against the owner's coding standard |
| This file | The settled result |

Evidence: code read on 2026-10-06; measurements from closed issues #105, #106,
#107 and #111. The app was not run. "Proven" means the code path was read.
Costs not taken from an issue are estimates.

## Current position

The pipe and JSON work already run off the UI thread. The blocking OS calls on
the UI thread were measured in #107 at tens of milliseconds and stay. The
once-a-second "everything changed" notifications were measured in #106 at
3.5 ms or less; they are untidy, but they do not cause the lag.

The lag has three proven causes. None needs a new thread or a new layer:

| Cause | What the person sees | Issue |
| --- | --- | --- |
| `Project` sends TableView one notification per changed row, and each notification makes the table copy, validate, sort and reconcile all rows | A freeze on first load (longer splash), a filter click, Move to bottom, and when a download finishes and its row travels into the seeding block | #113 |
| `Table.ViewOrder` holds the order for 3 s even under the Queue sort | A second Move up within 3 s shows late | #125 |
| The status bar puts the variable-width rate text left of the toggles | The Alternative limits and Errors toggles move sideways during a transfer | #48 |

The size of the #113 freeze is not measured. #105 measured only the collection
side, without the table attached.

## Target design

### Rule

The owner of a fact raises a change only when the fact changes. A consumer
reacts to that change. A view never needs to know whether it is hidden.

### Torrent list

- `Torrent` rows stay stable per identity and are updated in place
  (`docs/architecture.md`). `Torrent.Update` keeps its empty-name
  notification: only realized rows react, and WinUI ignores equal writes.
- `Project` is the only owner of membership and order. It runs when
  membership inputs change (a snapshot, the filter), not on every refresh. It
  replaces a read-only `VisibleTorrents` list only when the membership or the
  order differs, and the table receives one source update (contract 5.3
  allows it). An unchanged order publishes nothing.
- Delete the dead `Search` filter, `queueChanged`, and the
  `ObservableCollection` type on `Torrents` and `VisibleTorrents`.

### TableView

- The `DefinesRowOrder` column is not held by settling (#125). It lands after
  #113, never before.
- A rebuild that moves nothing skips the focus hand-off and the hosted
  `SelectionMode` toggle (#54 item 2).
- No batching API, no Reset to the ListView, no change to live-sort settling
  or its animation.

### Window and child view models

- Two refresh paths (#126): the broad `Refresh` for rare transitions
  (connection phase, `CanEdit`, picker, closing, language, errors), and a
  window-only notification for snapshot, selection and filter.
- This depends on one settings owner (#119) and one connection phase (#20),
  because today the children read raw settings and several flags.
- `Changed("")` on `MainViewModel` stays. Named property lists per trigger are
  rejected: each new getter would become a "remember to add it here" rule.

### Inspector

- Peers and trackers stay stable per endpoint and URL, as torrents do (#127).
- General binds the torrent's own formatted values (#127).
- Files keeps its per-node refresh, and nothing moves off the UI thread (#106).

## Disagreements settled

| Question | Decision | Reason |
| --- | --- | --- |
| Narrow the per-second notifications for CPU? | No | Measured at 3.5 ms or less (#106). |
| Split `Refresh` into five reactions? | No; two paths, later | Five named lists drift. The broad fan-out is right for rare transitions. |
| `Torrent.Update` detects changes? | No | No user-visible failure, and WinUI ignores equal writes. It would not need a stored derived value, because the row already stores its facts; the reason to drop it is the lack of benefit. |
| Visibility gates for hidden views? | No | #126 stops feeding hidden views on each snapshot. |
| Peer and tracker rows: now or after measuring? | Do it, after watching Peers in a real transfer | It is the established torrent pattern. |
| Element reuse in the uncommitted `Week.cs`? | Drop it | Drags snap to 15 minutes, so `Draw` runs at most 96 times per drag (`PeriodSpan.Snap`). The reuse saves almost nothing, and its property resets are not enforced. |
| `ObserveUpdates` only when the setting changes? | No | It is the only trigger of the daily update check. `Apply` calls it once per snapshot instead. |

## Order of work

Each step builds and ships alone.

1. #113: one projection publish. Rewrite the `WM_NULL` probe from #106 (it is
   not in the repository) and run it before and after with 2,000 torrents.
2. #125 and #54 item 2: TableView settling and the no-move rebuild.
3. #48: status bar rates in fixed-width fields; fixed-width `SpeedGraph`
   maximum label.
4. #122, then #119 with #112 and #120: one settings owner.
5. #20: one connection phase.
6. #126: two refresh paths; delete `Preferences.RefreshDraft` and the
   `HasDraft` skip in `Week.OnModel`.
7. #127: inspector rows and values.

## Decision for the owner

#119 asks whether to delete the Limits dialog. qBittorrent keeps a quick
global limits dialog as well as its options page, so both answers are
reasonable. Recommendation: delete it. The morning report already records
"a Settings-related result that opens a separate dialog" as a defect, and one
editor for four values removes a second draft and a second parser.
