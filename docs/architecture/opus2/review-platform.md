# Platform review of the UI-thread findings

Review of [findings.md](findings.md) against the code and WinUI behaviour,
2026-10-06. Source was read only. Nothing was built, launched or measured.

Evidence labels used below:

- **Proven**: the code path was read and does what is stated.
- **Measured**: a closed issue measured it; the issue number is given.
- **Inferred**: follows from documented or well-known WinUI behaviour, but was
  not observed in this app.
- **Unverified**: plausible, not checked. Do the check named beside it before
  acting.

## Conclusion

1. The steady one-second tick is not the main problem. #106 measured at most
   3.5 ms of UI-thread work per refresh. The broad `PropertyChanged("")`
   notifications in finding 1 are real waste, but WinUI pays only for realized
   rows and ignores writes of equal values, so they stay cheap. Leave them.
2. The largest CPU risk is finding 2, and it is worse than written. Every
   per-row change to `VisibleTorrents` makes the table rebuild its whole view.
   `MainViewModel.Project` also emits one `Move` for every row that a torrent
   passes when it moves down. So Move to bottom, a download that finishes
   near the top of the queue, a filter change and the first snapshot each cost
   (changed rows × all rows) rebuild work. Nobody has measured this with the
   table attached.
3. Three visible problems need no busy thread at all:
   - Under the Queue sort (the initial sort), the table's 3-second settling
     interval also holds the user's own queue moves. A second Move up within
     3 s of the first lands up to 3 s late.
   - Under any other sort, each 3-second settle removes and re-inserts the
     realized rows that change place, and the ListView's default add/delete
     and reorder transitions animate them.
   - The status bar's two toggle buttons move sideways whenever the rate text
     changes width.
4. The search box does not filter the list. The "search keystroke" path in
   findings 1 and 2 does not exist.
5. Target design: the projection publishes one source update per change; the
   row-order column is exempt from settling; a rebuild that moves nothing
   leaves focus and the hosted selection alone; inspector peers and trackers
   keep stable row objects. No new layer, scheduler or hidden-view mechanism.
6. One probe run, as in #106 but with 2,000 torrents, a filter change and a
   Move to bottom, settles whether item 2 is a material freeze.

## 1. The findings, checked

| Finding | Verdict | What a user would see |
| --- | --- | --- |
| 1. Every snapshot notifies everything | Real; overstated as a cause of lag | Nothing measurable: at most 3.5 ms per tick (#106) |
| 2. The projection talks to the table one row at a time | Real; understated; one path wrong | A freeze after a filter change, Move to bottom or a completion; a longer splash |
| 3. Inspector sections replace their data every second | Real | Files: 14–27 ms per tick at 10,000 files (#106). Peers and Trackers: see M5 |
| 4. Calls that block the UI thread | Correct; one-off | A short pause on one user action (#107) |

### Finding 1

- Proven: `Torrent.Update` ends with `RefreshText`, which raises
  `PropertyChanged("")`. The code that x:Bind generates treats an empty
  property name as "every property changed" and re-runs every OneWay binding
  on that row.
- Platform: only realized containers are bound. The table's `ItemsStackPanel`
  uses `CacheLength="0.5"`, which holds about 20 to 60 rows; the comment in
  the table's `Generic.xaml` records 36. A torrent without a container has no
  subscriber. The cost therefore grows with realized rows, not with the
  number of torrents.
- Inferred: the XAML property system ignores a write of an equal string or
  number, so an unchanged value causes no measure and no render. #106's
  3.5 ms is consistent with this.
- Not measured: torrents whose values change. Changed text re-measures its
  `TextBlock`, and the fixed column widths stop that at the cell (inferred).
  Expect a few milliseconds more, not a hitch.
- Wrong: "Refresh is also the reaction to ... a search keystroke". The search
  box binds `MainViewModel.Query`, whose setter raises only
  `Changed(nameof(Query))`. `MainViewModel.Search`, which `Project` filters
  by, is set only by `Reveal`. A keystroke runs `OnSearchChanged` and
  `FindSuggestions`, which are small.
- The hidden-form items (`Week.Draw`, `PreferencesForm`, `InspectorForm`) are
  real. WinUI keeps Collapsed elements loaded, so their bindings stay live, but
  it skips their layout. Settings was not part of any measurement. See 4.4.

Verdict: real waste with no user-visible failure on current evidence. Do not
spend the first change here.

### Finding 2

- Proven: `Source.OnSourceCollectionChanged` copies the whole source, and
  `Table.RebuildView` runs once for every notification. Each run:
  - validates every row (`ValidateRows`);
  - computes the order (`ViewOrder`), a full sort unless the order is held;
  - reconciles the private view (`View.Reconcile`);
  - reads the realized containers (`RealizedIndices`, two WinRT calls each);
  - sets the hosted `ListView.SelectionMode` to None and back;
  - moves focus off the rows and back, if a row holds focus;
  - re-applies the hosted selection (`ApplySelectionToContainers`);
  - makes every realized row update its visual state (`RowVisualsChanged`).

  That is a large fixed cost per call on top of the O(N log N) sort.
- Proven, and missing from the finding: `Project` puts `desired[index]` in
  place with one `Move` per index. A torrent that moves up costs one `Move`.
  A torrent that moves down costs one `Move` for each row it passes, because
  each following row is moved up separately. Each `Move` is one full rebuild.
  - Move to bottom on the first of N rows: N − 1 rebuilds.
  - A download that finishes: the engine sends libtorrent's
    `queue_position`, which is −1 for a finished torrent. `Torrent.QueueOrder`
    maps −1 to `int.MaxValue`, so the row moves from its queue place into the
    seeding block. The number of rebuilds is the distance it travels. The
    path is proven; the size of the hitch is not measured. It happens without
    any user input.
  - A filter change: one rebuild per row that leaves or arrives.
  - The first snapshot: one rebuild per torrent. The window stays cloaked
    until then (`ShowWhenReady`), so this shows as a longer splash, not as
    jitter.
- Wrong: "a search keystroke" (see finding 1). "A queue reorder" is one
  rebuild when a row moves up, and many only when a row moves down.
- Correct but harmless: `Project` sorts by queue order and the table sorts
  again. The source order is the row order that the table needs for natural
  order and for ties. The second sort is one O(N log N) pass.
- Missing: the `SnapshotApplied` handler in `MainWindow` calls `RefreshView`
  on every tick while the sort column is not Queue. Every such tick pays the
  fixed cost above, focus and selection churn included, even on the two ticks
  in three where the order is held and nothing moves.

Verdict: the most important CPU finding. #105 measured only the
`ObservableCollection` side: 191 ms for 9,999 moves on 10,000 rows, without a
table. Nobody has measured the table's share.

### Finding 3

- Peers and Trackers, proven: `Inspector.Read` builds new `Peer` and `Tracker`
  objects from each reply. The table matches them by key (`Endpoint`, `Url`)
  and replaces each realized row with the new instance in
  `View.InsertIntoRuns`. M5 covers what the user may see.
- Files, measured (#106): 14–27 ms per tick at 10,000 files, with one stall of
  58–66 ms in 12 s. This is the only measured per-tick cost above one 60 Hz
  frame (16.7 ms). It applies only to a large torrent with the Files section
  open. The owner closed #106, and nothing here changes that decision.
- Pieces, measured small (#106).
- General is read again on every tick although its data does not change.
  #111 measured the engine side at 6 ms or less. Harmless.

### Finding 4

Correct. These calls run once per user action, and #107 measured them at tens
of milliseconds at most. They are not regular jitter.

## 2. What the findings missed

### M1. Moving a row down costs one rebuild per row passed

Proven. Covered under finding 2.

### M2. Under the Queue sort, the user's own reorders wait for the settle

Proven from code; not observed.

- `MainWindow` sets `Torrents.Sort` to the Queue column, so the initial view
  is a sorted view.
- `Table.ViewOrder` applies the settling interval to every sorted column. It
  does not exempt the column whose `DefinesRowOrder` is true.
- The user presses Move up (Ctrl+Add). The next snapshot moves the row. If
  the table took a sorted order less than 3 s earlier, `HeldOrder` returns the
  old order, and `ScheduleSettle` shows the new one when the 3 s end.
- Result: the first move after a quiet period appears at once. The second and
  later moves within 3 s appear late, together. The same happens to a row
  drag dropped within 3 s of another reorder, and to a move made just after a
  torrent was added or removed, because those take a fresh sorted order.
- Contract: section 9 gives settling for "rapidly changing data such as speed
  and progress". The row order changes only by a command or by the engine's
  queue policy. Section 1.1 calls a table that leaves the user watching an
  unchanged screen a defect.

qBittorrent shows a queue move at the next refresh. This is the clearest lag
on input in the code. It costs no CPU, so no probe will see it.

### M3. A live-sort settle animates

Proven from code and the platform default. How it looks was not observed.

- Under a sort by a changing column (Down, Up, ETA, Progress, Status), the
  table takes the sorted order once per `SortInterval`, 3 s by default.
- `View.Reconcile` expresses a reorder at realized positions as `RemoveAt`
  followed by `Insert` (`RemoveFromRuns`, `InsertIntoRuns`). It never uses
  `Move` or a reset; the comments on `View` give the reasons.
- The table's template sets no `ItemContainerTransitions`, so the ListView's
  default set applies: `AddDeleteThemeTransition`, `ContentThemeTransition`,
  `ReorderThemeTransition` and `EntranceThemeTransition`. The comment in
  `Generic.xaml` says this is intended.
- So every 3 s, each realized row that changes place fades out, its
  neighbours slide, and it fades in at its new place. Under a speed sort,
  most of the screen moves.

Task Manager's Processes tab and qBittorrent re-sort live data without
animation: the rows jump. The 3-second cadence is already calmer than
qBittorrent's re-sort on every refresh; the animation is the difference. The
owner's requirement recorded on `View` is that rows which leave or arrive
animate when the view is repopulated. It says nothing about a settle. This
needs an owner decision; see 4.6.

### M4. The status bar moves its buttons during transfers

Proven from `MainWindow.xaml`; not observed.

The bottom grid has the columns Auto, Auto, Auto and Star. Column 0 holds
the `Rates` text, for example "Download: 1.2 MiB/s   Upload: 340.5 KiB/s".
Columns 1 and 2 hold the Alternative limits toggle and the Errors toggle.
When a rate gains or loses a digit, or changes unit, column 0 changes width
and both buttons move sideways. During a transfer this happens many times a
minute, and a pointer that aims at a toggle can miss it.

`SpeedGraph` has the same layout on a smaller scale. The `Maximum` label sits
in an Auto column to the left of the plot, so the plot shifts when the peak
label changes width.

Windows status bars put variable text in the last, stretching segment, so
nothing sits to its right.

### M5. Peers and Trackers replace their realized rows every second

Proven that the rows are replaced; the visible effect is inferred.

`View.InsertIntoRuns` raises a Replace (`this[i] = next`) for each realized
row whose instance changed. For a Replace, the ListView gives the container
new content, `CellsPanel.OnDataContextChanged` hands the new row to every
cell, and every cell binding runs again. The default `ContentThemeTransition`
can also play on that container (inferred). The likely result is a flicker of
every visible peer row once a second while Peers or Trackers is open. #106 and
#111 did not measure Peers with connected peers.

The torrent list does not have this problem, because `MainViewModel.Apply`
keeps one `Torrent` per identity and updates it in place. That is the
established pattern, and the architecture asks for it: "Keep row instances
stable and apply live property updates".

### M6. Every rebuild moves focus and resets the hosted selection

Proven that it happens; the effect is unverified.

`RebuildView` calls `_itemsView.Focus(FocusState.Pointer)` whenever a row
holds focus, before it knows whether anything will move, and
`RestoreRowFocus` puts focus back afterwards. It also sets the hosted
`SelectionMode` to None and back on every call. Inferred: the ListView clears
its selection when its mode becomes None, and `ApplySelectionToContainers`
then selects the rows again.

Under a non-Queue sort this runs on every tick, and on two ticks of three
nothing moves. Each run raises focus-changed events, and probably selection
automation events. Unverified: whether Narrator announces the focused row
again every second, and whether the platform's selection indicator replays
its animation on selected rows. Check both for 10 s with Narrator, or with the
event log in Accessibility Insights, with a focused and selected row under a
Down sort.

## 3. Busy thread or visible motion

| Symptom | Cause | Kind | When |
| --- | --- | --- | --- |
| Freeze after a filter click, Move to bottom or a completion | Finding 2, M1 | Busy thread | Grows with changed rows × all rows |
| Longer splash on open | Finding 2, first snapshot | Busy thread | Large libraries |
| A repeated Move up lands late | M2 | Delay without CPU | Queue sort |
| Rows shuffle with animation every 3 s | M3 | Motion | Non-Queue sort with live values |
| Status bar buttons shift | M4 | Layout motion | During transfers |
| Peer rows flicker | M5, inferred | Motion | Peers or Trackers open |
| One or two dropped frames per second | Files section, #106 | Busy thread | Large torrent, Files open |
| Narrator or indicator noise | M6, unverified | Events | Non-Queue sort, row focused |

The #106 probe sees only the first kind. It cannot see M2 to M6.

## 4. Target design

The rule behind each part: change notifications describe real changes, the
owner that knows about a change raises them, and the platform's own update
model does the rest.

### 4.1 Who detects change

- The engine snapshot reaches the dispatcher coalesced, as now
  (`MainViewModel.QueueSnapshot`). Keep this.
- `Torrent.Update` stays as it is. Its empty-name notification costs only
  realized rows (finding 1). Revisit it only if the probe shows a tick above
  one frame.
- `Project` is the only place that decides membership and order. It compares
  its desired order with the published one, and publishes only when they
  differ (4.3).
- The inspector follows the torrent pattern: one `Peer` per endpoint and one
  `Tracker` per URL, updated in place from each reply. This removes M5.

### 4.2 What a snapshot tick notifies

- Each torrent row, through its own `PropertyChanged`.
- The projection, once, and only when membership or order changed.
- `RefreshView` under a non-Queue sort, as section 5.3 of the contract asks.
  With 4.5 item 2, a held tick then costs O(N) model work and touches nothing
  in the ListView.
- The broad `MainViewModel.Refresh` stays. It is measured cheap, and narrowing
  it removes no failure that a user sees.

### 4.3 How the projection feeds the table

`Project` publishes one source update per change instead of one notification
per row. This removes M1 and the repeated rebuilds of finding 2.

Section 5.3 of the contract already allows two forms: assign a new list ("the
host assigns it again after changing its membership or order"), or raise one
Reset on a notifying source. The new list is simpler here because it adds no
type. `VisibleTorrents` becomes a read-only list that `Project` replaces and
announces by name, and the `ItemsSource` x:Bind becomes `Mode=OneWay`; it is
OneTime today. The capture code that calls `IndexOf` adapts.

The ListView still receives per-row notifications, because `View.Reconcile`
turns the new snapshot into removals and insertions at the positions that
need them. Rows that arrive or leave still animate. The ListView never
receives a Reset, so nothing flashes.

This is not the per-tick source replacement that the architecture forbids.
The list is replaced only when membership or order changed, which is exactly
when today's per-row notifications go out.

### 4.4 What hidden views do

Nothing new. Collapsed WinUI elements keep their bindings and skip layout.
That is normal platform behaviour, and #106 measured the inspector part as
small. If the probe shows Settings adding material time after it was opened
once, fix it at the owner: `Preferences.Apply` returns early when the
snapshot's settings equal the last ones. A view should never need to know
that it is hidden.

### 4.5 What the table library must and must not change

Must:

1. **Exempt the row-order column from settling.** Under a sort by the
   `DefinesRowOrder` column, `ViewOrder` takes the sorted order at once. This
   removes M2. Section 9 of the contract gets one sentence with the reason:
   that order changes only by a command, so holding it delays the user's own
   action.
2. **Make a rebuild that moves nothing cheap.** When the computed order is
   already the view (`View.IsAlready`), `RebuildView` skips the focus
   hand-off and the hosted `SelectionMode` toggle. It still reconciles the
   selection model, for eligibility, and commits. The reason is in the code:
   focus is at risk only when a container leaves, and that needs a
   notification. This removes the per-tick churn of M6. Do it when the check
   in M6 shows events per tick, or together with item 1, since both change
   the same small area.

Must not:

- add a batching, deferral or BeginUpdate/EndUpdate API, because the host can
  already publish once;
- raise Reset or Move to the ListView, or force a layout in the reconcile; the
  contract and the comments on `View` record why;
- change settling for live columns, which the owner ruled on and the contract
  justifies;
- dispatch, queue or order host updates, which section 5.3 keeps in the host.

### 4.6 Decisions for the owner

- **Should a live-sort settle animate?** Two reasonable answers exist. Task
  Manager and qBittorrent move rows instantly. I recommend instant settles,
  with animated arrivals and departures as now. The ListView applies its
  transitions per layout pass, not per notification (inferred), so the
  mechanism is not obvious. Prototype it in the TableView sample before any
  contract change. If no clean mechanism exists, keep today's behaviour.

M4 is not a decision: put the variable rate text where nothing follows it.

### Order of work

1. 4.3, one projection publish per change. Largest CPU risk; small change.
2. 4.5 item 1, the row-order exemption. Proven lag on input; one condition
   and one contract sentence.
3. M4, the status bar. Proven layout motion; markup only.
4. 4.1 inspector rows, after watching the Peers section during a transfer.
5. 4.5 item 2, after the check in M6.
6. The settle-animation decision.

## 5. The one measurement

Uncertainty: how long the UI thread stalls when the projection changes many
rows with the table attached. The answer decides whether 4.3 removes a real
freeze or is tidy-up.

Method: the #106 probe, which sends `WM_NULL` with `SendMessageTimeout` to the
real Release window about every 16 ms and times each round trip. Load the
headless engine with 2,000 generated paused torrents. In one run, take three
readings, each after 5 s idle:

1. Ten seconds of ticks under the Queue sort, as a baseline.
2. Click a filter that hides most rows, wait, then click All. Record the
   longest round trip after each click.
3. Select the first row and run Move to bottom (Ctrl+Shift+Subtract). Record
   the longest round trip.

A fourth reading is cheap in the same run: sort by Name and record ten
seconds of ticks, which prices `RefreshView` at 2,000 rows.

Reading the result: if readings 2 and 3 stay below about 100 ms, the per-row
path is tidy-up and 4.3 can wait. If they reach hundreds of milliseconds, 4.3
removes a real freeze. Record the torrent count; the #106 report does not
state the count it used.

This run sees only a busy thread. M2 to M6 need eyes: 30 seconds of watching
a real transfer sorted by Down, with a row selected and the Peers section
open, confirms or clears M3, M4 and M5.
