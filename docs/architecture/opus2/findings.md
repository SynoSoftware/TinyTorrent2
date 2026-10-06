# UI-thread findings

This is the source-backed starting point for the reviews in this folder. It
answers one question: what work runs on the WinUI UI thread, and which of it
should not run there, or should not run as often.

Evidence level: every code path below was read on 2026-10-06. No cost in this
file was measured in this review, and the app was not launched. Measurements
quoted from closed GitHub issues are marked with the issue number.

## Current position

The pipe read and JSON parse run off the UI thread (`PipeClient.Run`,
`PipeClient.Read`). Snapshots are coalesced before they reach the dispatcher
(`MainViewModel.QueueSnapshot`). The problem is what happens after a snapshot
reaches the UI thread: almost every owner is told "everything changed" whether
or not anything changed.

A snapshot arrives once per second (`PipeClient.Refresh`) and once after every
accepted command (`MainViewModel.RequestSnapshot`).

## Existing measurements and decisions

| Issue | What it established |
| --- | --- |
| #105 (closed, fixed) | `Project` no longer uses linear searches for unchanged order. On a bare `ObservableCollection` of 10,000 rows: unchanged refresh 7 ms, full reversal 191 ms with 9,999 collection changes. The measurement did not include TableView, which rebuilds its view for every one of those changes. |
| #106 (closed, not planned) | Real Release window, generated paused torrents without peers: UI thread busy at most 3.5 ms per refresh with the inspector closed or on General. Files section: one 82–257 ms stall when it opens (mostly XAML), then 14–27 ms per refresh. Pieces: at most 4 ms per refresh after the first. |
| #107 (closed, not planned) | Synchronous OS calls on the UI thread take tens of milliseconds at most: adapter enumeration 14–27 ms, `Process.Start` of the engine 7.5 ms, `File.Move` about 1 ms. Left in place. |
| #111 (closed) | Engine detail queries are small except for extreme torrents. No asynchronous query owner. |
| #103 (closed, fixed) | Optional reads are replaceable per consumer and wait behind commands. |
| #12 (closed, not planned) | No typed protocol codec layer: no user-visible failure. |

Not measured anywhere: many torrents with changing values, a non-Queue sort
column, a filter or search change with TableView attached, and keyboard or
pointer input during a refresh.

## Finding 1: every snapshot notifies everything

Proven from code, cost not measured at scale.

- `Torrent.Update` (`app/src/Models/Torrent.cs`) ends with `RefreshText()`,
  which raises `PropertyChanged("")` for every torrent on every snapshot. Each
  realized row re-evaluates about 20 `x:Bind` bindings; about 10 of them format
  strings (`DownloadText`, `EtaText`, `AddedText` and others).
- `MainViewModel.Refresh` (`app/src/MainViewModel.cs`) runs `Project`, refreshes
  the 6 filter choices (each counts all torrents), raises `Changed("")`, calls
  `Refresh` on `Draft`, `Speed`, `Inspector`, `Preferences` and `Files`, and
  raises `CanExecuteChanged` for 30 commands. Each child refresh raises
  `PropertyChanged("")`, and `OnTaskChanged` turns each into 4 more named
  events. One snapshot produces roughly 45 `MainViewModel.PropertyChanged`
  events.
- `Refresh` is also the reaction to a selection change, a search keystroke, a
  filter change and most command state changes.
- `Preferences.Apply` calls `Preferences.Refresh`, and `MainViewModel.Refresh`
  calls it again. Each refresh notifies every `Preference` field and every
  `SchedulePeriod`. `ApplySchedule` builds a new `SchedulePeriod` per period on
  every snapshot to compare them.
- `Week.OnModel` (`app/src/Controls/Week.cs`) calls `Draw` on every
  `Preferences.PropertyChanged` except `HasDraft`. `Draw` reuses its block
  elements, but it recomputes ranges and labels each time.
- `Inspector.Observe` refreshes the inspector and issues a new read on every
  snapshot, for every section, including General whose data does not change.
- `MainWindow.OnModelChanged` reacts to `Changed("")` by setting the theme,
  placeholder, inspector size and selected filter, and by calling
  `StartPlacement`.
- `PreferencesForm` and `InspectorForm` stay loaded after their first open, so
  they keep reacting while hidden.

## Finding 2: the projection talks to the table one row at a time

Proven from code, cost not measured with TableView attached.

- `MainViewModel.Project` (`app/src/MainViewModel/Finding.cs`) issues one
  `Remove`, `Add` or `Move` on `VisibleTorrents` per changed row.
- TableView captures a full copy of the source and runs `RebuildView` after
  every collection notification (`lib/TableView/src/Body/Source.cs`,
  `OnSourceCollectionChanged`; `Table/Selection.cs`, `RebuildView`). The
  contract allows this (section 5.3: "after every observed collection
  notification, the table captures one ordered source snapshot").
- `RebuildView` is O(N): it validates every row, orders the view, reconciles
  the selection and refreshes realized row states.
- Startup, a filter change, a search keystroke and a queue reorder therefore
  cost O(changed rows × N) on the UI thread.
- `Project` sorts by queue order, and TableView sorts the view again.
- `MainWindow` calls `Torrents.RefreshView()` after every snapshot while the
  sort column is not Queue. TableView holds the order for its 3-second
  `SortInterval`, then re-sorts and moves realized rows on screen.

## Finding 3: inspector sections replace their data every second

Proven from code. #106 measured the steady state as small, except Files.

- Peers and Trackers arrive as new object arrays on every read, so TableView
  replaces and rebinds every realized row.
- `FileSelection.Refresh` raises `PropertyChanged("")` on every node; folder
  getters walk their subtree.
- `PiecesMap.Show` runs on every inspector refresh (2–4 per snapshot).
  `SameMap` has no same-instance shortcut, measured at under 0.1 ms in #106.

## Finding 4: calls that block the UI thread

Proven from code, measured in #107 as small. These cause one-off pauses on a
user action, not regular jitter.

| Call | Where | When |
| --- | --- | --- |
| `NetworkInterface.GetAllNetworkInterfaces()` | `PreferencesForm.RefreshInterfaces` | First open of Settings |
| `Process.Start` with shell execute | `MainWindow` `OpenRequested` handler | Open, Open folder |
| `File.Exists` and `Process.Start` | `PipeClient.LaunchEngine` | Restart command |
| `body.UpdateLayout()` inside `SizeChanged` | `MainWindow/Actions.cs`, `ShowFiles` | Each resize step while the Move or Delete dialog is open |
| `RefreshText()`: rebuild menus, `FindSuggestions`, `RefreshView` | `MainWindow/Chrome.cs` | Language change, and each time the Add, Files or Discard dialog opens |
| `UpdateChrome()` | 4 `SizeChanged` handlers, `XamlRoot.Changed`, `AppWindow.Changed` | Up to 5 times per window resize or move step |
| `Week.Draw()` | `Week.OnPointerMoved` and `Week.OnModel` | Up to twice per pointer move while a period is dragged |

## Work underway

The working tree holds uncommitted owner changes to `Week.cs`, `Week.xaml`,
`Scheduler.xaml`, `Preferences.cs` and `Capture.cs`. `PeriodDraft.SetSpan`
and `Preferences.RefreshDraft` are new: they raise named changes and
`WeekChanged` per drag step instead of a full refresh. These files are not
edited by the reviews.
