# State ownership review

Scope: who owns each fact, who detects its change, and how the change reaches
the screen. The question behind it is the owner's report that the app feels
laggy and jittery. Platform and binding costs belong to the other reviewer.

Evidence levels used below:

- **Proven**: read in the code on 2026-10-06.
- **Measured**: a number from a closed issue.
- **Inferred**: reasoning from code. Not run, not measured.

The app was not built or run for this review.

## Conclusion

The common cause is structural. No object detects when its own facts change.
Every boundary therefore says "everything changed", and every consumer does
its full work on every event. The structure has three parts:

1. Getters read facts across object boundaries in both directions. Child view
   models read the parent's `CanEdit`, `Text`, `IsConnected`, `Theme` and
   rates. The parent reads the children's `HasDraft` and `IsPending`. Because
   of this, no object can say which of its properties changed.
2. `MainViewModel.Refresh` is the one reaction to any change. It reprojects
   the table, recounts the filters, notifies itself, refreshes all five child
   view models and 30 commands. A snapshot, a row click and a picker opening
   all pay the same cost.
3. Consumers rebuild their own copy of "did X change": `Scheduler.OnModel`
   (`_editor == Model.Draft`), `InspectorForm.Refresh`
   (`_editing != Model.IsEditingTrackers`), `SpeedGraph.Show` (`changed`),
   `Scheduler.RefreshPeriods` (`SequenceEqual`), and the
   `IsNullOrEmpty(args.PropertyName) || …` branches in
   `MainWindow.OnModelChanged`.

The fix is to move change detection to the owner of each fact and to give
`Refresh` a separate reaction for each trigger. It needs no new framework or
type, and most steps delete code.

Three user-visible problems stand out. The mechanism of each one is proven
from code. None was observed at runtime, and only the third has a
measurement:

| Problem | What the user sees | Owner of the fix |
| --- | --- | --- |
| The projection reaches TableView one row at a time, and each row costs a full rebuild and a re-sort. | A long freeze at first load and on a filter change, when the list has thousands of torrents. The cost grows as N² log N; its size is inferred, not measured. | `MainViewModel.Project` (step 1) |
| TableView holds a reorder of the Queue column for up to 3 seconds after the last reorder. | After a second Move up within 3 s, or a queue drag, the row keeps its place while its Queue number already shows the new value. | TableView contract, section 9 |
| The Files section notifies every file node on every refresh. | A stall of 14–27 ms each second while Files is open (measured in #106). Attributing it to node notification is inferred. | `FileSelection.Apply` (step 5) |

## The findings, checked

| Finding in `findings.md` | Verdict |
| --- | --- |
| `Torrent.Update` raises `PropertyChanged("")` for every torrent on every snapshot | Real. The cost is bounded by realized rows, not by N: TableView does not subscribe to rows, so only the `x:Bind` code of on-screen containers reacts. Idle rows still re-evaluate about 20 bindings each second. |
| `MainViewModel.Refresh` produces about 45 `PropertyChanged` events per snapshot | Real. My count is 35 with the inspector closed and 43 with it open. Sources: `Preferences.Apply`, `Draft.UseDefault`, `Refresh` itself and `Inspector.Observe`, with each child `""` turned into 4 or 5 named events by `OnTaskChanged`. Each event runs `MainWindow.OnModelChanged`. |
| `Refresh` reacts to a search keystroke | **False.** The search box sets `Query`, which raises only `Query`. `MainViewModel.Search` is never set to a non-empty value: only `Reveal` writes it, to empty. `interface.md` says the global search "does not silently filter the torrent table". `Search` and `_search` are dead state. |
| `Preferences.Apply` and `MainViewModel.Refresh` both refresh Preferences | Real. Each of the 21 `Preference` objects raises `""` three times per snapshot: in `Confirm`, in `Apply`'s `Refresh`, and in the parent's `Refresh`. |
| `ApplySchedule` builds new periods each snapshot | Real but small: a few objects per second, and it already returns early when nothing changed. |
| `Week.OnModel` draws on every Preferences change | Real. The work underway skips drawing during a drag and on `HasDraft`. Outside a drag it still draws twice per snapshot once Settings has been opened. |
| `Inspector.Observe` refreshes and reads on every snapshot | Real. The inspector refreshes 4 times per snapshot when open and 2 times when closed. The per-second read stays, because #111 decided that reads are cheap for the engine. |
| `MainWindow.OnModelChanged` does window work on `""` | Real but cheap. The larger defect: it runs about 35 times per snapshot and calls `StartPlacement` and the `CanRestart` check each time. Both are one-time transitions. |
| `PreferencesForm` and `InspectorForm` stay loaded and react while hidden | Real. Both stay in the tree with `Visibility` collapsed. That `Collapsed` does not raise `Unloaded` is a platform fact that this review relies on but did not check. |
| Finding 2: the projection talks to the table one row at a time | Real, and worse than stated. A change in membership is never held, so each `Add` or `Remove` also re-sorts the whole view (`HeldOrder` returns null when the count differs, and `ViewOrder` then calls `SortedSnapshot`). Each notification therefore costs O(N log N), not O(N). The part about a search keystroke is false (see above). |
| `Project` sorts and TableView sorts again | Real but harmless. `Project`'s order is the table's natural order, which the contract needs. |
| `RefreshView()` after every snapshot when the sort is not Queue | Real. Contract 5.3 requires it after values used by the sort change. It is wasted when no row changed (step 2). |
| Finding 3: Peers and Trackers are rebuilt as new arrays | Real. Not measured with real peers (#106 and #111 both say so). No step proposed until it is measured. |
| `PiecesMap.Show` every refresh | Overstated. #106 measured it at a few milliseconds. |
| Finding 4: blocking calls | Measured small in #107. These are one-time pauses, not periodic jitter. |

## Diagnosis

### Facts that cross objects

The parent refreshes every child because child getters read parent facts.
These are the edges, all proven:

| Child getter | Parent or other fact it reads | How often the fact changes |
| --- | --- | --- |
| `CanApply`, `CanSubmit`, `CanEdit` in `SpeedLimits`, `FileOperation`, `Inspector`, `Preferences` (and each `Preference`), `AddDraft` | `MainViewModel.CanEdit` | On connect, disconnect, picker, close |
| Every text getter | `MainViewModel.Text` | On a language change, which already has its own path (`Publish`) |
| `Inspector.IsAvailable`, `IsRemoved` | `IsConnected`, `Torrents.Contains(target)` | On connection change or removal |
| `Inspector.Downloaded`, `Remaining` | The target `Torrent` | Every snapshot |
| `Inspector.DownloadRate`, `UploadRate` | Parent rates | Every snapshot |
| `Preferences.Theme`, `CanSelectLanguage`, `CanSelectTheme` | Parent theme and settings-pending state | On a settings change |
| `AddDraft.Message` | `MainViewModel.Message` | On connection change |

In the other direction, the parent reads `HasDraft`, `IsPending` and `IsOpen`
from the children (`CanClose`, `HasDraft`, `HasInspector`). `OnTaskChanged`
turns every child `""` into those named events.

Only two of these facts change every second: the target's values and the
rates. Both concern only the inspector. Everything else changes on rare
transitions. The fan-out in `Refresh` is right for those transitions. It is
wrong on the per-second path, the row-click path and the keystroke path.

### Responsibility that is unclear, duplicated or misplaced

All proven from code:

- **Row change detection outside the row.** `Apply` reads `queue` from the
  JSON to compute `queueChanged`, and then `Torrent.Update` reads it again.
  The row owns the value but does not report its change.
- **`queueChanged` → `RefreshView()` is redundant.** A queue change that
  moves visible rows also changes `Project`'s order, which updates the source
  and causes a rebuild under the current sort. A queue change that does not
  move visible rows needs no rebuild.
- **Confirmed settings have two owners.** `MainViewModel._settings` keeps the
  raw JSON for `ShowAdd`, `Limit()` and the alternative-limits fallback.
  `Preferences` keeps the same values as `Preference` fields. They are two
  answers to one question.
- **The speed limits have two editors.** The Limits dialog (`SpeedLimits`,
  `LimitChoice`) and Settings > Transfers (`Preference`) each keep a draft and
  parse the same four fields. The working tree already routes the command
  palette's "limits" entry to Settings, but the Torrent menu still opens the
  dialog. Whether the dialog stays is the owner's decision. If it stays, it
  should edit the same `Preference` objects.
- **Which settings section holds a field is decided in `Finding.cs`.**
  `PreferenceSuggestions` maps field names to sections, while the form's
  layout decides where each field really is. When a field moves in the XAML,
  the command palette opens the wrong category.
- **Torrent membership is stored twice.** `Torrents` (an `ObservableCollection`
  that no code observes) and `_byId`. `Inspector.IsAvailable` calls
  `Torrents.Contains`, a linear search, several times per inspector event.
- **Connection state is a combination of flags.** `_connected`, `_loading`,
  `_storageFailed`, `_writable` (stored, but derived from loading, storage and
  stopping), `_ready`, and `_sessionId.Length == 0` (which means "connecting"
  rather than "disconnected"). `Message`, `IsLoading` and `Severity` decode
  this combination with if-chains. #20's remaining defects (wrong severity,
  connection and command failure in one bar) come from it.
- **One-time transitions are polled.** `MainWindow.OnModelChanged` calls
  `StartPlacement` and `Reveal` on every model event. `ShowWhenReady` already
  starts the placement. (Inferred: the second call is redundant, because
  before the first connection `DataDirectory` is null and the call does
  nothing.) `OnTaskChanged` calls `ObserveUpdates` on every Preferences event,
  but the real trigger is only "Check for updates was turned on or off".

### Work underway

`PeriodDraft.SetSpan` and `Preferences.RefreshDraft` follow the target rule:
the owner raises named events, and only when a value changed. Two gaps:

- `PeriodDraft.Start`, `End` and `IsPaused` setters, and `DayChoice.IsChecked`,
  still call `Refresh()` and `_owner.Refresh()`. The time pickers and the
  drag therefore take two different paths for the same change.
- `RefreshDraft` raises both `HasDraft` and `WeekChanged`. `Preference.Input`
  needs only `HasDraft`, so it cannot use `RefreshDraft` without redrawing the
  week. Today each keystroke in any Settings field runs `Preferences.Refresh`:
  21 field events, every period, 6 commands and a `Week.Draw`.

## Target structure

### Rule

The owner of a fact detects when it changes and raises a named change, or
raises one `""` only when something actually changed. Use `""` without a check
only for a language change, where every string changes. Consumers react to the
names they use.

`""` stays acceptable inside a small view model for rare transitions that the
user starts, such as opening the tracker editor. It is wrong on the
per-second path, the selection path and the keystroke path.

### Torrent rows

- `Torrent.Update` compares the incoming values with its own and raises `""`
  only if one differs. It returns whether anything changed.
- `Apply` collects that result and deletes `queueChanged`. `SnapshotApplied`
  carries "rows changed".
- `MainWindow` calls `Torrents.RefreshView()` only when rows changed and the
  sort column is not Queue.
- This review does not propose named events for each property. They would need
  a map of which of the 16 display texts depends on which field, and that map
  would be one more thing to keep in sync. Whether the binding savings justify
  the map is a question for the binding reviewer.

### Projection and TableView

- `VisibleTorrents` becomes an `IReadOnlyList<Torrent>` that is replaced only
  when its membership or order differs, bound with `Mode=OneWay`. `Project`
  computes the desired array, returns if it equals the current one, and
  otherwise assigns it and raises `VisibleTorrents`.
- Contract 5.3 allows this: "the host assigns it again after changing its
  membership or order". The assignment takes the same path as a collection
  notification (`SetItemsSource` → `SnapshotChanged` → `RebuildView`). A change
  costs one rebuild instead of one per row.
- Selection is kept: the schema key reconciles it on every rebuild. A queue
  drag is cancelled in both designs, because `RebuildView` calls
  `CancelRowDrag` on every rebuild.
- `Project` runs from `Apply`, the `Filter` setter and `ClearFinding`. It does
  not run from selection, picker or language changes.
- The inspector-removed check moves out of `Project` into `Apply`'s removal
  loop, where removal is known.
- **This reopens part of #105.** #105 kept the same `Remove`, `Add` and `Move`
  sequence so that "selection and queue dragging see the same collection
  changes". The key-based reconcile and the drag cancellation now make that
  constraint moot, and #105 measured without TableView attached.

### Window-level state (`MainViewModel`)

`Refresh` splits into reactions, one for each kind of trigger:

| Trigger | Work |
| --- | --- |
| Snapshot | Rows, `Project`, filter counts, the named window values that change each second (`Rates`, `Incoming`, `ErrorCount`, `FilterLabel`, `TorrentError`), commands, `Preferences.Apply`, `Inspector.Observe`. If connection or access state changed, the access reaction too. |
| Selection | `SelectionText`, `HasSelection`, `TorrentError`, commands, retarget the inspector. |
| Filter | `Project`, `Filter`, `HasFilter`, `FilterLabel`, commands. |
| Access or connection (connect, disconnect, loading, storage failure, stopping, picker, closing, Add open, settings pending) | Today's full fan-out: `Changed("")`, every child `Refresh`, commands. These transitions are rare, so the fan-out is affordable. |
| Language | Today's `Publish` path, unchanged. |

**Commands:** `RefreshCommands` raises `CanExecuteChanged` for the 30 commands
after a snapshot, a selection change, a filter change and an access change.
Availability depends on row state (`IsMoving`, `Queue` of the selected rows),
so a snapshot must recompute it. Thirty events per second are cheap. Do not
add change detection for commands.

**What `MainViewModel` keeps:** window presentation state, selection, command
availability and commands, as `architecture.md` assigns them. It also keeps
the projection, which is about 20 lines and has no other owner.

**What it gives up:**

- Refreshing the children on every change.
- Detecting row changes (moves to `Torrent`).
- The raw settings copy: `_settings`, `Setting()` and `Limit()` are deleted.
  `ShowAdd` and `SpeedLimits.Begin` read the `Preference` fields.
- The dead `Search` filter.
- The field-to-section map (each `Preference` carries its section).

`OnTaskChanged` stays as it is. Once the children are no longer refreshed on
every snapshot, it runs only on child transitions.

**Connection state:** an explicit `Connection` state (Connecting, Loading,
StorageFailed, Ready, Stopping, Disconnected) replaces `_connected`,
`_loading`, `_storageFailed`, `_writable` and the session-identity check.
`CanEdit` stays `Ready && !picking && !closing`, as decided in #108 and #13.
The access reaction in the snapshot row above becomes
`if (connection != previous)`. `Message` and `Severity` become a switch, which
#20 needs.

### Settings (`Preferences`)

- `Preference.Confirm` returns early when the confirmed value is unchanged.
  `Preferences.Apply` raises `""` only when a field or the schedule changed.
  `MainViewModel` stops calling `Preferences.Refresh` on each snapshot.
- Keystrokes follow the pattern of the work underway. `Preference.Input`,
  `DayChoice.IsChecked` and the `PeriodDraft` setters raise their own names
  and tell `Preferences` that the draft changed, which raises `HasDraft`. The
  week redraw is raised only by changes to the period draft. `RefreshDraft`
  separates these two signals so that both callers can use it.
- `ObserveUpdates` runs when the confirmed `Updates` value changes in
  `Apply`, not on every Preferences event.

### Inspector

- `Observe` issues the read. It raises the rate names only when the section
  is Speed, and raises `""` only when the target's membership changed.
- `Read` raises a change only when the reply changed what the section shows.
  For General, `ApplyGeneral` reports whether a field differs. Its start
  refreshes only the `Retry` command.
- The General view binds `Target.*` for downloaded and remaining, like it
  already does for `RatioText`, `Status` and `AddedText`. `Torrent` then
  notifies these values itself, and the formatting has one owner.
  (`Torrent` gains `DownloadedText` and `RemainingText`. `Inspector.Downloaded`
  and `Remaining` are deleted.)
- `FileSelection.Apply` records which files changed their downloaded bytes or
  priority. It refreshes those files and their parent folders, and raises its
  own `""` and `Changed` only if something changed.
- `InspectorForm.OnModel` reacts to `Section`, `IsDay`, `History`, `Pieces` and
  `IsEditingTrackers` by name, not to every event.

## Order of work

Each step ships on its own. Before and after steps 1 and 3, use the
`WM_NULL` probe from #106 to measure UI-thread availability. Use the
300-torrent capture fixture (`CaptureLibrary.cs`) or a larger generated store.
Visit Settings once, sort by a non-Queue column, switch a filter, and hold an
arrow key in the table.

| Step | Change | Deletes | Size |
| --- | --- | --- | --- |
| 1 | Replace `VisibleTorrents` on change. Delete `Search`. Move the inspector-removed check to `Apply`. | The incremental `Remove`/`Add`/`Move` loops, the `ObservableCollection`, `_search` and its uses | About −20 lines in `Finding.cs`; one XAML attribute; `CaptureLibrary` adjusts `IndexOf` |
| 2 | `Torrent.Update` reports changes. `SnapshotApplied` carries "rows changed". `RefreshView` only when rows changed. | `queueChanged` and its separate JSON read | About +10 / −6 |
| 3 | Split `MainViewModel.Refresh` by trigger. Make `Preference.Confirm`, `Preferences.Apply`, `Draft.UseDefault` and `Inspector.Observe` notify on change. Remove `StartPlacement` from `OnModelChanged`, and react to `CanRestart` by name. | The per-snapshot and per-click fan-out | About ±60 lines across `MainViewModel`, `Preferences`, `AddDraft`, `Inspector` and `MainWindow` |
| 4 | Keystrokes and draft edits in Settings go through the work-underway pattern. | `owner.Refresh()` calls in the setters | About ±15 |
| 5 | `FileSelection.Apply` refreshes changed nodes only. `InspectorForm` reacts by name. | The per-node `""` each second | About ±20 |
| 6 | Explicit `Connection` state. | Four flags and the `_sessionId` check | About ±25; do it together with #20 |
| — | Settings single owner, the field section on `Preference`, `Torrents` as one membership store | `_settings`, `Setting`, `Limit`, the section map | Small; independent of the steps above |

Effect on open issues:

- **#54 (a rebuild moves focus):** steps 1 and 2 reduce how often a rebuild
  happens. They do not fix the round trip. With a non-Queue sort and active
  torrents, `RefreshView` still runs every second. The fix belongs in
  TableView.
- **#20:** step 6 provides the state that the severity and separation fixes
  need. It does not make the bar closable.
- **Decisions kept:** #103 (PipeClient is the only scheduler), #106 (no
  preparation off the UI thread; step 5 reduces notifications, not threads),
  #107, #111 (reads stay synchronous and repeat each second), #12 (no typed
  codec), #108 and #13 (the `CanEdit` rule).

## For the TableView owner

Three items are outside this review's ownership. All are proven from code;
none was observed:

1. **The Queue sort holds a reorder that the host asked for.** `ViewOrder`
   exempts only an unsorted view, an empty view and a zero interval. Under the
   default `Sort(QueueColumn)`, a source reorder with the same rows within 3 s
   of the last settle is held (`HeldOrder`). Example: press Move up twice in
   one second. The second move shows its new Queue number at once, but the row
   moves up to 2 s later. Contract section 9 makes this behaviour correct,
   because it holds "while a sort is applied". The code's own comment says
   natural order "belongs to the host, which reorders when it means to". The
   row-order column (`DefinesRowOrder`) is that order. Exempting it would fix
   the lag, but the contract must change first.
2. **#54** as above.
3. The comment in `RebuildView` says that a row drag is cancelled "only once
   the order beneath it moved". The code calls `CancelRowDrag()` on every
   rebuild, which is what contract 5.3 requires. The comment is the part that
   is wrong.
