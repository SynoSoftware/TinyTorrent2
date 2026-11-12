# Stage 3 gate record: text and paths

Gate done on 12 November 2026, by reading, on the working tree that holds
stages 1 and 2. Nothing was built or run. This record takes the place of
[the stage's text](../22-one-owner-per-rule.md#3-text-and-paths) for the
session that implements it. Paths without a prefix are under `app/src`; line
numbers are those of that working tree.

## Where the stage stands

Every finding is kept, with one part dropped: the speed graph's scale ceiling
is another rule. The gate found nine more copies. The stage adds one file,
`Shared/FolderComparer.cs`, and no product text, so no catalogue changes. It
changes what a person sees only by the stage's "Settled as" choices, listed
under [What changes for a person](#what-changes-for-a-person).

## Gate step 1: the findings

| Finding | Rule | Result |
| --- | --- | --- |
| A count where 0 means unlimited | A count limit shows Unlimited for 0 and the number with digit grouping otherwise. | Kept. `Settings/Editor.cs` 325 (`CountText`, format `G`: 1000) and `Settings/ConnectionSetup.cs` 718 (`Count`, format `N0`: 1,000) differ today. |
| "Reason; N more problems" | A message about several problems shows the first one's text, then how many more occurred. | Kept. `Shell/Notifications.cs` 212 to 219 and 521 to 529; `Shell/MainViewModel/Notifications.cs` 248 to 254. |
| A period's length (to re-read) | A length of time shows its two largest units: minutes; hours; hours and minutes; days; days and hours. | Kept. `SchedulePeriod.Length` (`Settings/Schedule.cs` 520 to 523) stops at hours, so a full-day period reads "24 h"; `Strings.Duration` (`Shared/Strings.cs` 313) goes on to days. Periods are 1 to 1,440 minutes (`Schedule.cs` 510), so only the full day changes. |
| A rate (to re-read) | A transfer rate is its bytes in the largest unit, per second. | Kept for `SpeedGraph.Rate` (`Torrents/SpeedGraph.xaml.cs` 238), which writes `Strings.Rate` (`Strings.cs` 286) again. **Dropped** for `SpeedGraph.Ceiling` (326): it rounds the scale's top up to a step in the unit `Bytes` shows. That is the graph's axis rule; it formats no text. |
| Time 0 means none (to re-read) | The engine sends 0 for a time it does not have. | Kept. `Torrent.Moment` (`Torrents/Torrent.cs` 526), `Tracker.NextText` (`Torrents/Tracker.cs` 45), `Inspector.CreatedText` (`Shell/Inspector.cs` 171). This is a rule of reading the pipe, not of formatting, so its owner is the pipe reader (step 2, question 3). |
| The "—" text for no value (to re-read) | A value that is not known, or does not exist, shows an em dash. | Kept. 83 code sites in 22 files, `Strings.SpeedLimit` among them; two more are comments. All mean the same. |
| The label of an enum value (to re-read) | Each enum's value is named by its text group's message whose key is the value's name in lower case: a settings category by `settings`, a torrent filter by `filters`, a Library configuration by `library`. | Kept. The gate found two more enums with the same shape: an inspector section by `inspector` (`Shell/MainViewModel/Finding.cs` 375; `Shell/InspectorPane.xaml.cs` 546) and a tracker status by `trackers` (`Torrents/Tracker.cs` 21, 106), and a third Library configuration copy (`Library/EmptyState.xaml.cs` 65). |
| Shortcut text (to re-read) | A shortcut reads Ctrl, Alt and Shift in that order, then its key, joined by "+", from the `shortcuts` text; a control's tooltip names it in parentheses after the label. | Kept. `Shell/MainWindow/Actions.cs` 170 to 176 and 497 to 517; `Chrome.cs` 685 to 692; `Torrents/PeerMap/Navigation.cs` 62 to 64, which builds "Ctrl+0" by hand. |
| Day and time label (to re-read) | A moment of the week reads as its short day name, then its time. | Kept. `Settings/Scheduler.cs` 66 to 73; `Settings/Schedule.cs` 434. |
| Folders | Two paths name the same folder when their full paths are equal without a trailing separator, ignoring case; a path lies inside a folder when it starts with that folder's full path and a separator. | Kept. Six copies in five versions. The gate found four more: `Subtitles/SubtitleFile.cs` 172, and the capture journeys `Capture.cs` 2408, `CaptureFiles.cs` 435 (`SamePath`) and `CaptureTraffic.cs` 24. |
| Clipboard | A copy goes through the window's one clipboard write, which reports a failure. | Kept. `Torrents/PiecesMap.xaml.cs` 567 to 571 writes the clipboard itself and lets a failure escape the key handler; `Shell/MainWindow.cs` 154 to 165 reports it. |

Copies that look alike and stay:

- `Controls/FileSelection.cs` 955 (`FileTarget.Within`) compares paths
  inside one torrent: relative, with "/", and case-sensitive.
- Paths read from the Library database compare as text, ignoring case: the
  database's `PATH` collation (`Shared/Storage/Database.cs` 193),
  `Subtitles/Acquisition/Discovery.cs` 388 to 403 and the subtitle job and
  file comparisons in `Subtitles/Acquisition`. The app writes these paths in
  full (`Shared/FileSources.cs` 250, 350), so text comparison is the
  database's rule, not the folder rule.
- `App.cs` 186 and `Shared/Engine/EngineProcess.cs` 88 compare executable
  paths as launched; their comments give the reason.
- `Library/Browser.cs` 833 names a file kind in the Library filter
  vocabulary, which stage 7 makes one table. `Browser.cs` 1440 names one
  for a row. Both stay for stage 7.
- `Finding.cs` 457 reads `search_terms` by the category name: another group
  and another rule.
- Single owners of an enum's label stay where they are: `Pieces.Name`
  (`Torrents/Pieces.cs` 110), `Peer.StateText` (`Torrents/Peer.cs` 65).
- The XAML `TargetNullValue='—'` (`Shell/InspectorPane.xaml` 545) stays. XAML
  cannot name a C# constant there, and a getter that returned the dash
  would also put it into the torrent table's tooltip (`MainWindow.cs` 434).
- `Torrents/Tracker.cs` 39 to 44 read -1 as an unknown count. That is
  another engine word, not in this stage.

## Gate step 2: questions the stage leaves open

1. **What "1 day" reads.** The days unit of `Strings.Duration`:
   `units.days`, "{0:N0} d" in English (`Resources/en.json` 654), as every
   remaining time of a day or more already reads. The full-day period's time
   range keeps "All day" ([UI settings](../../contracts/ui/settings.md),
   "Full-day descriptions say All day").
2. **How the full path is taken.** `Path.GetFullPath`, only for a fully
   qualified path; any other path, such as an empty or half-typed folder,
   compares as written. The engine compares folders the same way
   (`engine/src/Engine/Files.cpp` 185 to 230: `absolute`, `lexically_normal`,
   the trailing separator removed, names compared without case), and it keeps
   save paths as the person typed them, so the app sees `D:/x/` and `D:\x` for
   one folder. Whitespace stays the Add dialog's input rule
   (`Torrents/AddDraft.cs` 95), because a path from the engine has none.
3. **Where the time reader lives.** `PipeClient`, beside `ReadOutcome`
   (`Shared/Engine/PipeClient.cs` 604). The [pipe API](../../contracts/pipe-api.md)
   says the engine sends Unix seconds and 0 until a time exists ("completed_on
   … 0 until complete"). [Code style](../../../AGENTS.md#code-style) keeps a
   convention a library defines at its boundary, so `Tracker.NextAnnounce` and
   `Inspector.Created` become `DateTimeOffset?`, as `Torrent.Added` is.
4. **Whether "inside" includes the folder itself.** No. Discovery scans a
   torrent's folder recursively only when it is a subfolder of the save path
   (`Subtitles/Acquisition/Discovery.cs` 344 to 347); its save path itself
   would scan the whole download folder. `IsLocationUnavailable`
   (`Shell/MainViewModel/Notifications.cs` 196 to 198) asks "same or inside"
   and calls both members.
5. **How the pieces map reaches the model.** It holds none: it receives
   `Pieces` and `Strings` (`Torrents/PiecesMap.xaml.cs` 118).
   `InspectorPane`, whose `Model` is the `Inspector`, loads it
   (`Shell/InspectorPane.xaml.cs` 140 to 160) and wires each section's
   commands there; the `Inspector` holds its `MainViewModel`
   (`Shell/Inspector.cs` 19). So the map raises an event, the pane passes the
   text to `Inspector.Copy`, and that calls `MainViewModel.Copy`, whose
   `CopyRequested` the window answers (`MainViewModel/Actions.cs` 516).
6. **Which counts take digit grouping.** The count limits, the copies the
   stage names and the example in the plan's goal. A queue position
   (`Torrent.QueueText`) is a position, not a count, and stays.

## Gate step 3: owners, interfaces and callers

Candidates for the folder owner, compared with `/codebase-design`:

| Candidate | Result |
| --- | --- |
| A static `Folders` or `Folder` type with `Same` and `Contains` | Rejected. `AddDraft.Folders`, `RemovalDraft.Folders` and `MoveDraft.Folder` are properties, and inside those types the name would bind to the property. The comparer the two `Distinct` and one `GroupBy` need would be a third member. |
| A `Folder` value type that holds a normalized path | Rejected. Every caller wraps every path, and a group key would lose the text the Remove dialog shows. |
| `FolderComparer`, an `IEqualityComparer<string>` with `Contains` | **Chosen.** `Distinct` and `GroupBy` take it as it is, `Equals` answers "same folder", and the group keeps its first path as written. |

| Owner | Interface | Callers, and the member each calls |
| --- | --- | --- |
| `Strings` (`Shared/Strings.cs`) | `internal const string NoValue = "—"` | Every `"—"` in product code: `Settings/Editor.cs` (19), `Torrents/Peer.cs` (10), `Settings/Overview.cs` (7), `Settings/Editor/Plugins.cs` (7), `Torrents/Torrent.cs` (5), `Shell/Inspector.cs` (5), `Torrents/Tracker.cs` (4), `Shell/MainViewModel/About.cs` (4), `Settings/ConnectionSetup.cs` (4), `Shell/MainViewModel/Limits.cs` (3), `Settings/SettingsPage/Home.cs` (3), `Shell/Inspector/Peers.cs` (2), `Strings.SpeedLimit`, and one each in `Torrents/SpeedGraph.xaml.cs`, `Torrents/PiecesMap.xaml.cs`, `Subtitles/Settings.cs`, `Shell/MainViewModel.cs`, `Settings/Speedometer.xaml.cs`, `Library/IProvider.cs`, `Library/Browser.cs`, `Controls/FileSelection.cs`; capture `Capture.cs` 1998 |
| `Strings` | `internal string CountLimit(double? count)`: `NoValue` for null, Unlimited for 0, else `N0`; beside `SpeedLimit` | `Editor.QueueTotal` 232, `PeersSummary` 256, 257 (`CountText` deleted); `ConnectionSetup.CreateQueueRows` 369 to 372 (`Count` deleted) |
| `Strings` | `internal string Problems(string first, int count)` | `Shell/Notifications.cs` 212, 521; `Shell/MainViewModel/Notifications.cs` 248 |
| `Strings.Duration` | Unchanged; its comment says "a length of time" | `SchedulePeriod.Length` (`Schedule.cs` 520) |
| `Strings.Rate` | Unchanged | `SpeedGraph.Rate` 238 keeps only its null case |
| `Strings` | `Get(SettingsCategory)`, `Get(TorrentFilter)`, `Get(LibraryConfiguration)`, `Get(InspectorSection)`, `Get(TrackerStatus)`, overloads beside `Get(WorkDescription)` | Category: `Finding.cs` 118, 396, 451; `Settings/Overview.cs` 38. Filter: `Finding.cs` 150, 477. Configuration: `Library/Browser.cs` 165; `Library/EmptyState.xaml.cs` 65; `Shell/MainWindow/Library.cs` 260. Section: `Finding.cs` 375; `Shell/InspectorPane.xaml.cs` 546. Tracker status: `Torrents/Tracker.cs` 21, 106 |
| `Strings` | `internal string Shortcut(VirtualKeyModifiers modifiers, VirtualKey key)`; `internal string ShortcutTip(string label, string shortcut)`, the label alone without a shortcut | `MainWindow.ShortcutText` (`Actions.cs` 497) keeps the command lookup and calls `Shortcut`; toolbar tips `Actions.cs` 175 and Back `Chrome.cs` 685 call `ShortcutTip`; `PeerMap/Navigation.cs` 62 calls both |
| `PipeClient` (`Shared/Engine/PipeClient.cs`) | `internal static DateTimeOffset? ReadTime(JsonElement value)`, `Torrent.Moment` moved | `Torrent.cs` 466, 467; `Tracker.cs` 62; `Inspector.cs` 1001 |
| `Schedule` (`Settings/Schedule.cs`) | `internal string DayTime(int day, int minute)`, beside `ShortDay` and `Time` | `Schedule.NextChange` 434; `Scheduler.OnTime` (`Settings/Scheduler.cs` 66) |
| `FolderComparer` (`Shared/FolderComparer.cs`, new) | `Instance`; `Equals(string?, string?)`; `GetHashCode(string)`; `internal bool Contains(string folder, string path)`, true only for a path below the folder | `Equals`: `AddDraft.HasNewDestination` 93; `Subtitles/SubtitleFile.cs` 172; capture `Capture.cs` 2408, `CaptureFiles.cs` 435 (`SamePath` deleted), `CaptureTraffic.cs` 24. As comparer: `AddDraft.Folders` 198; `MoveDraft.Folder` 42; `RemovalDraft.RefreshItems` 178. `Contains`: `Discovery.cs` 307, 347 (`Within` deleted); `IsLocationUnavailable` with `Equals` |
| `PiecesMap` and `Inspector` | `PiecesMap`: `internal event EventHandler<string>? CopyRequested`. `Inspector`: `internal Task Copy(string text)` | `PiecesMap.OnKey` 567 raises it; `InspectorPane.LoadSection` subscribes when it loads the Pieces section |

## Decisions the plan did not settle

1. **The time reader belongs to the pipe** (step 2, question 3), not to
   `Strings`: the rule is about the engine's word, and each reader formats
   the time its own way (`T` for a tracker, `Strings.Time` for a creation
   date).
2. **One constant for the dash, `Strings.NoValue`.** Each site keeps its own
   condition, because the conditions differ (null, not connected, not
   running); only the text has one owner.
3. **Enum labels are overloads of `Strings.Get`**, as `Get(WorkDescription)`
   is, one per enum that more than one member names. A generic member would
   still need each caller to know the group.
4. **`Problems` takes the total count**, which every caller holds.
5. **The day and time label stays with `Schedule`**, which owns the short day
   names and the time format it joins.
6. **`FolderComparer` sits in `Shared`**, in its own file, because Torrents,
   Subtitles and Shell use it and it is a type of its own, not a file name.
7. **The Add dialog keeps its whitespace trim** (step 2, question 2).
8. **A copy from the pieces map is a copy like every other.** It goes
   through `MainViewModel.Copy`, so its failure is reported as a failed copy
   of a magnet link is.
9. **`ShortcutTip` gives the label alone without a shortcut**, as the
   toolbar does today, so one member serves the three tooltips.
10. **Digit grouping applies to count limits** (step 2, question 6).

## Gate step 4: consumers of every changed name

Searched: `app/src` with the capture journeys and `#if CAPTURE` blocks, all
XAML, and `app/tests/*.ps1`.

- `Tracker.NextAnnounce` becomes `DateTimeOffset?`: `Tracker.cs` 46 to 50,
  62, 76; `InspectorPane.xaml.cs` 252 sorts by it, and a nullable time sorts
  as `Torrent.Added` does (`Shell/MainWindow.cs` 215). XAML binds `NextText`
  only (`InspectorPane.xaml` 1191).
- `Inspector.Created` becomes `DateTimeOffset?`: `Inspector.cs` 172, 792,
  805, 1001, 1344. XAML binds `CreatedText` only (`InspectorPane.xaml` 575).
- Private members deleted: `Torrent.Moment`, `Editor.CountText`,
  `ConnectionSetup.Count`, `Discovery.Within`, the capture `SamePath`. Their
  files hold every use.
- `SchedulePeriod.Length` keeps its name; `TimeLabel` (524) and
  `PeriodDraft.Duration` (`Schedule.cs` 653) read it. No journey reads it.
- `IsLocationUnavailable` keeps its name; the capture journey
  `CaptureDialogs.cs` 50 calls it with a path that is neither the root nor
  inside it, so its answer stays false.
- No test script names a changed member. `SettingChecks.ps1` and
  `UpdateChecks.ps1` construct `Strings`, whose constructor stays.
- No product text changes, so no catalogue changes.

## Files the change may edit

Product code: `Shared/Strings.cs`, `Shared/FolderComparer.cs` (new),
`Shared/Engine/PipeClient.cs`; `Controls/FileSelection.cs`;
`Settings/Editor.cs`, `Editor/Plugins.cs`, `ConnectionSetup.cs`,
`Overview.cs`, `Schedule.cs`, `Scheduler.cs`, `SettingsPage/Home.cs`,
`Speedometer.xaml.cs`; `Torrents/Torrent.cs`, `Tracker.cs`, `Peer.cs`,
`SpeedGraph.xaml.cs`, `PiecesMap.xaml.cs`, `PeerMap/Navigation.cs`,
`AddDraft.cs`, `MoveDraft.cs`, `RemovalDraft.cs`; `Library/Browser.cs`,
`EmptyState.xaml.cs`, `IProvider.cs`; `Subtitles/Settings.cs`,
`SubtitleFile.cs`, `Acquisition/Discovery.cs`; `Shell/Notifications.cs`,
`Inspector.cs`, `Inspector/Peers.cs`, `InspectorPane.xaml.cs`,
`MainViewModel.cs`, `MainViewModel/Notifications.cs`, `About.cs`,
`Limits.cs`, `Finding.cs`, `MainWindow/Actions.cs`, `Chrome.cs`,
`Library.cs`. Capture journeys: `Capture.cs`, `CaptureFiles.cs`,
`CaptureTraffic.cs`.

Contracts: `docs/contracts/localisation.md` (count format and the owner of
display formats), `docs/contracts/ui/settings.md` (a full day's length),
`docs/contracts/ui/torrents.md` (the folder rule; a failed copy from the
pieces map).

`CONTEXT.md` needs no change: the stage adds no product term.

## What changes for a person

Each is a "Settled as" choice of the stage.

- Connection and queue limits read 1,000 on the Settings pages, as they
  already do in Connection setup.
- A full-day schedule period's length reads "1 d" in English, in its row and
  in the period editor's Duration, where it read "24 h".
- Two folders written differently, such as `D:/Downloads/` and
  `D:\Downloads`, count as one: in Add's Set as default and recent folders,
  in Move's From, in Remove's folder groups, in the unavailable-location
  check and in subtitle discovery.
- A failed copy from the pieces map shows the copy error that a failed copy
  of a magnet link shows. Before, the exception left the key handler.

## Cost

No build in this session; the stage joins batch A's one app build with
`-Capture`. The capture build is required, because three journeys change.
Check scripts whose subject changes: `SourceChecks.ps1`, whose folder
discovery cases (1170 to 1214) run `Discovery` and `SubtitleFile.Names`.
`NotificationChecks.ps1` runs `Shell/Notifications.cs`, but none of its
cases builds an engine problem toast or a subtitle toast, which the change
touches. Capture modes whose journeys run changed text: Schedule,
SettingsLayout, AddLayout, Connections, Dialogs, Recovery and Details.
About three hours of agent work.

## Implementation notes

Implemented on 12 November 2026 on top of stages 1 and 2, by reading;
nothing was built or run. Where the code differs from the record:

- **Connection setup's queue rows pass a lambda**, `count =>
  Text.CountLimit(count)`, as its speed rows already do: a method group of a
  `double?` parameter does not convert to `Func<int, string>`.
- **`Editor.cs` keeps "—" in one comment**, which describes what a summary
  shows before the first snapshot.
- **Lines that grew past about 105 columns were wrapped**, where the dash
  became `Strings.NoValue`.
- **`Shell/MainWindow/Capture.cs` compares the capture store with
  `FolderComparer`**; a missing data directory now fails that check with its
  own message instead of an exception from `Path.GetFullPath`.
- **The stage 2 review fixes landed in the same files**: `Strings` also has
  `Protection(RequestBlockKind)`, and `PipeClient.VersionException` became an
  `IFailure`. They are recorded in [stage 2](stage-02.md#review-fixes).

## Lead decision after the stage, 12 November 2026

Decision 2 is replaced. `FolderComparer` compares text only: `/` becomes
`\`, a trailing separator is removed, and case is ignored. It no longer
calls `Path.GetFullPath`, because .NET reads the disk to expand a name that
contains `~`, and the comparer runs on the UI thread in Add, Move, Remove
and the location check, so a sleeping or unavailable drive would hold the
window. The engine's `lexically_normal` also resolves `.` and `..`
segments; the paths compared here come from the engine or a folder picker,
which never write them, so no person meets the difference.

## Review fixes

The review of stages 3 and 16b (scratchpad record `review-stage03.md`)
found four low items; the lead fixed them.

- L1: four sites still wrote an enum label's key by hand. The Connection
  breadcrumb and the Limits suggestion read `Get(SettingsCategory.Limits)`;
  the trackers input and the pieces map name read
  `Get(InspectorSection.Trackers)` and `Get(InspectorSection.Pieces)`. The
  text is the same.
- L2: the localisation paragraph now lists only the formats `Strings` owns,
  and says that a count limit, not every count, shows digit grouping.
- L3, a change a person meets that the record did not list: a torrent saved
  at a drive root, such as `D:\`, no longer makes subtitle discovery read
  the whole drive; it reads the root's own files, because a folder does not
  lie inside itself (question 4).
- L4: the reason that a copy goes through the window's one clipboard write
  stays only beside the pieces map's event.

The review also corrected the reason of the lead decision above: Add's
folder field accepts typed text, so a compared path can hold `.` or `..`.
The decision stands, because Add, Move and Remove never resolved those
segments before this stage, and the location check compares paths with
drive and share roots, where they do not change the answer.
