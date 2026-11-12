# Stage 1 gate record: name the facts

Gate done on 12 November 2026, by reading, on commit a4312a7. No app file
changed between 7e0ee84 and a4312a7, so the plan's line numbers hold. This
record takes the place of
[the stage's text](../22-one-owner-per-rule.md#1-name-the-facts) for the
session that implements it. Paths without a prefix are under `app/src`.

## Where the stage stands

Every item has an owner, an interface and its callers, except one: the
4-DIP drag distance keeps its two copies until stage 6 (decision 8). The
stage adds no file. It changes what a person sees only by the stage's
"Settled as" choices, listed under
[What changes for a person](#what-changes-for-a-person).

## Gate step 1: the findings marked "to re-read"

Every finding is kept. Each states its rule in one sentence.

| Finding | Rule | Copies |
| --- | --- | --- |
| Limit mode text | A limits choice has one name wherever the app names it: Standard limits, Alternative limits or No limits. | `Shell/MainViewModel/Limits.cs` 105 to 112; `Settings/LimitSelector.cs` 83 to 89; `Settings/ConnectionSetup.cs` 350 to 353; the gate found `Settings/Schedule.cs` 38 to 41 (`FormatFixedLimits`) beside 464 (`FormatMode`) |
| Incoming-connection state | Incoming connections are in one of four states, decided in this order: no adapter, routed through a proxy, port open, port closed. | Six properties in `Shell/MainViewModel/Actions.cs` 38 to 61 each restate the order |
| Theme | The theme setting's word chooses Light or Dark; any other word follows Windows. | `Settings/ThemePicker.xaml.cs` 41; `Settings/Overview.cs` 99; `Shell/MainWindow.cs` 491 |
| Movie or series | A file name with a season names a series; any other names a movie. | `Subtitles/Supplier.cs` 441; `Subtitles/Suppliers/OpenSubtitles.cs` 245; `Library/VideoLookup/Identification.cs` 159, 421, 427 |
| Plus and minus keys | The main keyboard's + and - keys are the virtual-key codes 0xBB and 0xBD, which `VirtualKey` does not name. | `Shell/MainWindow/Actions.cs` 61; `Torrents/SpeedGraph.xaml.cs` 42; `Torrents/PeerMap/Navigation.cs` 18. The gate adds Comma (`Shell/MainWindow.cs` 40), the same gap. |
| Split height | The inspector split starts 360 DIPs high. | `Shell/MainWindow/Workspace.cs` 26; `Placement.cs` 357, the default for a saved file without the field |
| Guess count | A person downloads at most 1 + `Acquisition.MaximumExtras` subtitles for a language. | `Subtitles/Batch.cs` 209 to 216 lists "up to 2, 3, 4" by hand; `Subtitles/Settings.cs` 138 already builds its list from `MaximumExtras` (`Acquisition.cs` 17) |
| File kinds as SQL numbers | SQL compares a file's kind through `FileKind`, never a written number. | `Library/Store.cs` 677, 678. The gate found `Library/Store/Identification.cs` 238 and the capture fixture `Library/Store.cs` 54 to 84. `Library/Store.cs` 39, 40 already binds `(int)FileKind.Video`. |
| Drag distance | A press becomes a drag once the pointer moves 4 DIPs. | `Settings/Week.cs` 602 (sideways only) and `Torrents/PeerMap/Navigation.cs` 137 (any direction, as the square 16). Only the distance is shared; each keeps its own measure. |

The other items were read on both sides. The gate found three more copies:

- **Peer facts:** the capture block `Shell/Inspector/Peers.cs` 19 to 22
  counts connected and active peers a third time.
- **File priorities:** `FileNode.Skip`, `Low`, `Normal`, `High`
  (`Controls/FileSelection.cs` 808 to 811) repeat the text keys.
- **Engine refusals:** `Shell/Notifications.cs` 491 compares words of a
  second engine list, as decision 1 states.

## Gate step 2: questions the stage leaves open

1. **Which words the refusal enum reads.** `CommandException` also carries
   a notification's problem word: `Shell/Notifications.cs` 192 to 198 builds
   one from the toast's `code`, which is a `ProblemKind` word
   (`engine/src/Torrent.cpp` 142 to 163), and 491 compares two of them,
   `move_failed` and `move_interrupted`. So the enum reads words of the
   engine's `ErrorCode` (`engine/inc/Enums.h` 702 to 735) and of
   `ProblemKind`, and its comment names both. The plan named only
   `ErrorCode`.
2. **Whether one "folder refused" member changes Add.** `AddDraft.cs` 224
   and the capture journey `Capture.cs` 3175 compare only
   `invalid_destination`, while Move and the toasts also take
   `move_destination_unavailable`. The engine's `add` refuses a folder only
   with `InvalidDestination` (`engine/src/Engine/Additions.cpp` 79);
   `MoveDestinationUnavailable` comes only from `CheckDestination`
   (`engine/src/Engine/Move.cpp` 11 to 22), which serves `move` and
   `check_destination`. One member therefore changes nothing for Add.
3. **Where the theme words become a choice.** `Editor` owns the theme
   setting (`Settings/Editor.cs` 139, 490, 827). The picker and the Home
   summary read the setting's input, the window its confirmed word, so the
   owner reads any word.
4. **How `Schedule` reaches the limit names.** `Schedule` holds its
   `Editor` (`Settings/Schedule.cs` 11), which already passes on
   `MainViewModel.FixedLimits` (`Settings/Editor.cs` 376).
5. **Where the peer facts live.** Every reader holds a `Peer.Values`:
   `Swarm` reads it directly (`Torrents/Swarm.cs` 172), `MapModel` builds its
   observation from a record's values (`Torrents/MapModel.cs` 1150), and the
   capture block reaches it through `Peer.Capture()` (`Torrents/Peer.cs` 549).
6. **Whether a restored window can use the title bar's width.**
   `RestorePlacement` runs from `ShowWhenReady` (`Placement.cs` 45 to 47)
   after the constructor's first `UpdateMinimum` (`Shell/MainWindow.cs`
   228). The title bar may not be measured yet; then the measured part is
   zero and the minimum is 720 DIPs plus the frame, never less than today.

## Gate step 3: owners, interfaces and callers

The stage adds no file. Enums go to `Enums.cs`, as
[naming](../../agents/naming.md#files) requires.

| Owner | Interface | Callers, and the member each calls |
| --- | --- | --- |
| `CommandException` (`Shared/Engine/PipeClient.cs`) and `enum EngineFailure` (`Enums.cs`) | `EngineFailure Reason`, read from the word once in the constructor. Members: `Other`, `Starting`, `Storage` (`storage_failed`), `MetadataUnavailable`, `AliasConflict`, `PreviewExpired`, `Destination` (`invalid_destination`, `move_destination_unavailable`), `Move` (`move_failed`, `move_interrupted`). `Code` stays, as the word and the text key. | `Shared/TorrentSource.cs` 229 `not Storage`; `Shell/Inspector.cs` 90 `Starting`, 1021 `MetadataUnavailable`; `Shell/MainViewModel.cs` 1209 `AliasConflict or MetadataUnavailable`, 1220 `AliasConflict`; `Shell/Notifications.cs` 490, 809 `Destination`, 491 `Move`; `Torrents/AddDraft.cs` 224 `Destination`, 710 and 889 `PreviewExpired`; `Torrents/MoveDraft.cs` 135 `Destination`; capture `Capture.cs` 3175 `Destination` |
| `Torrent` (`Torrents/Torrent.cs`) | `bool IsSourceUnavailable`: a move failed because its source folder is unavailable | `Shell/MainViewModel/Actions.cs` 242 |
| `Navigation` (`Shell/Navigation.cs`) | `internal static bool IsWorkspace(WindowPage)` | `Shell/MainWindow/Workspace.cs` 112, 119, 287, 318; `MainWindow/Actions.cs` 611; `Connections.cs` 38; `Chrome.cs` 357; `Shell/Inspector/Selection.cs` 25 |
| `Torrent` | `bool Matches(TorrentFilter)`, moved from `MainViewModel.Matches`, which is deleted | `Shell/MainViewModel/Finding.cs` 66, 181, 505; `Settings/Overview.cs` 87, 88; capture `CaptureConnections.cs` 65 |
| `Peer.Values` (`Torrents/Peer.cs`) | `bool IsConnected`: open or opening, any state but known. `bool IsActive`: connected and moving data. | `Torrents/Swarm.cs` 172, 176; `MapModel.Observation` holds both facts in place of the state, and `Group.Tally` (995, 997) reads them; capture `Shell/Inspector/Peers.cs` 20, 21 |
| `FilePriority` (`Controls/FileSelection.cs`) | A record of `Value`, `TextKey` and `Glyph`; `Skip`, `Low`, `Normal`, `High`, and `All` in menu order | `Controls/FileBrowser.xaml.cs` 174 to 177; `FileNode.PriorityIndex` 785; `FileNode` texts 808 to 811; `FileAction.Apply` 967 |
| `MainViewModel` (`Shell/MainViewModel/Limits.cs`) | `string LimitsTitle(LimitProfile)`; `Editor.LimitsTitle` passes it on, as `Editor.FixedLimits` does | `LimitsHeading` 105; `Settings/LimitSelector.cs` 83; `Settings/ConnectionSetup.cs` 350; `Settings/Schedule.cs` 38 and 464 |
| `MainViewModel` and `enum IncomingState` | Private `IncomingState IncomingState`: `MissingAdapter`, `Proxied`, `Open`, `Closed` | `Incoming`, `IncomingStatus`, `IncomingGlyph`, `IsIncomingProxied`, `IsIncomingOpen`, `IsIncomingClosed` in `Shell/MainViewModel/Actions.cs`. Their names stay, because `Shell/MainWindow.xaml` 1388 to 1405 and `Settings/Overview.cs` 28, 84 read them. |
| `Editor` (`Settings/Editor.cs`) | `static ElementTheme ReadTheme(string? word)` | `Settings/ThemePicker.xaml.cs` 41; `Settings/Overview.cs` 99; `Shell/MainWindow.cs` 491 |
| `Release` (`Shared/FileName.cs`) | `VideoKind Kind` | `Subtitles/Supplier.cs` 441; `Subtitles/Suppliers/OpenSubtitles.cs` 245; `Library/VideoLookup/Identification.cs` 159, 421, 427 |
| `FileName` (`Shared/FileName.cs`) | `static bool IsSubtitle(string path)`, over one set of extensions that `Kind` also reads; `SubtitleFile.IsSubtitle` is deleted | `Subtitles/Acquisition/Discovery.cs` 603; `FileName.Kind` 66 |
| `Acquisition` (`Subtitles/Acquisition/Storage.cs`, beside the job store's other statements) | `static bool IsActive(SubtitleOutcome)` and `const string ActiveOutcomes`, the same list as SQL parameter names that each statement binds, as `Due` (`Acquisition/Acquire.cs` 19) does | `Acquisition/Acquire.cs` 692; `Acquisition/Storage.cs` 465, 798; `Acquisition/Work.cs` 20; `Acquisition/Jobs.cs` 84 (twice) |
| `FileSelection` (`Controls/FileSelection.cs`) | `internal static readonly TimeSpan SearchPause`, the private `Pause` renamed | `FileSelection.cs` 469; `Library/Browser.cs` 899 |
| `DatasetRow` (`Shared/DatasetRow.cs`) | `static int Age(FileInfo installed)`, in whole days | `DatasetRow.cs` 167; `Shell/MainWindow/Datasets.cs` 64 |
| `ConnectionSetup` (`Settings/ConnectionSetup.cs`) | `const int MbitPerSecond = 125_000`, the bytes a second in one Mbit/s, as `RateScale` names its units (`Enums.cs` 167) | `ConnectionSetup.cs` 15, 642, 800, 801; `Shell/Inspector/Peers.cs` 107 |
| `App` (`App.cs`) | `const string Repository` (owner and name) and `const string RepositoryPage` (its GitHub page) | `App.cs` 24; `Shell/AboutPage.xaml.cs` 38; `Shell/CreditsPage.xaml.cs` 353; `Shell/Updates.cs` 22 builds its API address from `Repository` |
| `MainWindow` (`Shell/MainWindow/Chrome.cs`) | `MinimumWidth` and `MinimumHeight` in DIPs; `SizeInt32 MinimumSize(double scale)` in physical pixels with the frame | `UpdateMinimum` 606 to 612; `Placement.cs` 162, 163 (the first move to the display, as now) and 167 to 176 (the restored size) |
| `enum OemKey` (`Enums.cs`) | `Plus = 0xBB`, `Comma = 0xBC`, `Minus = 0xBD`, used as `(VirtualKey)OemKey.Plus` | `Shell/MainWindow/Actions.cs` 61, 62, 85, 515; `Torrents/SpeedGraph.xaml.cs` 42, 43, 736, 740; `Torrents/PeerMap/Navigation.cs` 18, 19, 259, 260; `Shell/MainWindow.cs` 40, 325 |
| `MainWindow` (`Shell/MainWindow/Workspace.cs`) | `const double DefaultSplitHeight` | `Workspace.cs` 26; `Placement.cs` 357 |
| `Acquisition.MaximumExtras` | Unchanged | `Subtitles/Batch.cs` 209 to 216 builds "up to" from it |
| `FileKind` | Unchanged. `Store.Match` and the capture fixture bind its number as a parameter, as `Library/Store.cs` 39 does; `Store/Identification.cs` 238 writes `{(int)FileKind.Video}` into its interpolated SQL, as the same statement's `Eligible` (19 to 26) already does | `Library/Store.cs` 677, 678; `Library/Store/Identification.cs` 238; capture fixture `Library/Store.cs` 54 to 84 |

## Decisions the plan did not settle

1. **The enum reads two engine lists** (step 2, question 1).
2. **One `Destination` member** for both folder words (step 2, question
   2). No behavior changes.
3. **`EngineFailure` and `Reason`.** `SubtitleException.Reason` is a
   `SubtitleFailure` and `UpdateException.Reason` an `UpdateFailure`
   (`Subtitles/Acquisition.cs` 777, `Shell/Updates.cs` 439).
   `CommandFailure` is taken by the record in `Shell/Notifications.cs`.
   Members keep the engine's member names where naming allows;
   `StorageFailed` and `MoveFailed` would repeat "Failure", so they are
   `Storage` and `Move`. `CommandException.Read` lists every word, so a
   reader finds the other side.
4. **The theme is WinUI's `ElementTheme`**, not a new enum: its `Default`,
   `Light` and `Dark` are exactly the three choices, and the window applies
   it as `RequestedTheme`. `MainViewModel.Theme` stays the setting's word,
   because the capture journeys select a theme by word and wait for
   `Model.Theme` to equal it (`Capture.cs` 926, 1065, 1394 and 11 more).
5. **The limit name takes a `LimitProfile`**, because three callers hold
   one. `LimitSelector` maps its three profile modes and keeps "Weekly
   schedule" for the fourth.
6. **`Observation` keeps the two facts, not the state.** A change from
   connecting to handshaking then changes no observation, so the map skips
   work it did for nothing (`Torrents/MapModel.cs` 394). Its one
   constructor takes the peer's record; six positional values would pass
   [naming](../../agents/naming.md#inputs-and-results)'s limit of four.
7. **OEM keys are an enum in `Enums.cs`**, because stage 1 adds no file and
   no base type owns keys. Comma joins, so one home names the gap.
8. **The drag distance waits for stage 6.** No type in `Shared` or
   `Controls` owns pointer gestures, and stage 1 adds no file. Stage 6
   creates the shared owner for custom-drawn controls in `Controls`, where
   the week and the peer map meet; the distance goes there.
9. **The repository's owner and name are the constant**, and the page and
   the API address are built from it, so the name is written once.
10. **The restored window uses the same minimum as the presenter**
    (step 2, question 6).
11. **The active-job SQL list keeps parameters**, as `Due` does, so each
    statement stays a compile-time constant. `IsActive` and
    `ActiveOutcomes` sit together with one comment.
12. **`IsSourceUnavailable` answers the row's word only.** `!IsMoving`
    stays in `FindRecoverable`, which asks whether recovery applies now.
13. **`Release.Kind` replaces only the movie-or-series choice.** The other
    `Season is null` reads ask whether a name carries a season
    (`OpenSubtitles.cs` 37, 101, 233, 234; `Identification.cs` 422) and stay.
14. **The priority combo keeps its XAML items.** Its texts and its index
    read `FilePriority`; the first item stays Unchanged, for folders.

## Gate step 4: consumers of every changed name

- **Product code:** the callers in the table above. The compiler finds the
  rest: `MainViewModel.Matches` and `SubtitleFile.IsSubtitle` are deleted,
  `Observation` changes its parameters, and the private constants leave
  their files.
- **XAML:** `Shell/MainWindow.xaml` 1388 to 1405 binds `IncomingGlyph` and
  the three `IsIncoming…` properties; `Controls/FileBrowser.xaml` 181 to
  198 binds `PriorityIndex`, `Unchanged`, `Skip`, `Low`, `Normal`, `High`.
  No name changes.
- **Capture journeys and `#if CAPTURE` blocks:** `Capture.cs` 3175;
  `CaptureConnections.cs` 65; `Shell/Inspector/Peers.cs` 19 to 22;
  `Library/Store.cs` 46 to 90. `CaptureFiles.cs` 270 compares another torrent
  word and stays, as the plan says. No journey names `Navigation.IsWorkspace`,
  `SubtitleFile.IsSubtitle`, `FileSelection`'s pause, `Observation`, the key
  constants or the limit and theme members.
- **`app/tests`:** no script names a changed member.
  `SourceChecks.ps1` builds `Shared.Release` by its constructor and
  `Year` (94, 99) and calls `FileName.Matches` (162); both stay.
  `NavigationChecks.ps1` calls `Navigate`, `Open`, `GoBack` and
  `SelectCategory`; they stay.

## Files the change may edit

`Enums.cs`, `App.cs`; `Shared/Engine/PipeClient.cs`, `Shared/TorrentSource.cs`,
`Shared/FileName.cs`, `Shared/DatasetRow.cs`; `Controls/FileSelection.cs`,
`Controls/FileBrowser.xaml.cs`; `Shell/Navigation.cs`, `Shell/Inspector.cs`,
`Shell/Inspector/Selection.cs`, `Shell/Inspector/Peers.cs`,
`Shell/MainViewModel.cs`, `Shell/MainViewModel/Actions.cs`,
`Shell/MainViewModel/Finding.cs`, `Shell/MainViewModel/Limits.cs`,
`Shell/Notifications.cs`, `Shell/MainWindow.cs`,
`Shell/MainWindow/Actions.cs`, `Workspace.cs`, `Connections.cs`, `Chrome.cs`,
`Placement.cs`, `Datasets.cs`, `Capture.cs`, `CaptureConnections.cs`,
`Shell/AboutPage.xaml.cs`, `Shell/CreditsPage.xaml.cs`, `Shell/Updates.cs`;
`Torrents/Torrent.cs`, `Peer.cs`, `Swarm.cs`, `MapModel.cs`, `AddDraft.cs`,
`MoveDraft.cs`, `SpeedGraph.xaml.cs`, `PeerMap/Navigation.cs`;
`Settings/Overview.cs`, `Editor.cs`, `LimitSelector.cs`,
`ConnectionSetup.cs`, `Schedule.cs`, `ThemePicker.xaml.cs`;
`Library/Browser.cs`, `Library/Store.cs`, `Library/Store/Identification.cs`,
`Library/VideoLookup/Identification.cs`; `Subtitles/Supplier.cs`,
`Subtitles/Suppliers/OpenSubtitles.cs`, `Subtitles/SubtitleFile.cs`,
`Subtitles/Batch.cs`,
`Subtitles/Acquisition/Acquire.cs`, `Storage.cs`, `Work.cs`, `Jobs.cs`,
`Discovery.cs`. Contracts: `docs/contracts/ui/settings.md`,
`docs/contracts/ui/workspace.md`, `docs/contracts/peer-location.md`,
`docs/contracts/library.md`.

## What changes for a person

Each is a "Settled as" choice of the stage.

- The Settings Home subtitle counts a completed torrent as seeding, as the
  Seeding filter does.
- A peer that is connecting or handshaking is not active on the map, as it
  already is not in the Peers status counts.
- `.ssa` and `.vtt` files show the Document icon and kind in the file
  browser and in Library, as `.srt` does.
- A window restored from its saved size is at least as wide as its title
  bar needs, frame included, as a window the person resizes is.
- A refusal the app does not handle apart still shows its own text: the
  word stays on the exception.

No product text changes, so no catalogue changes.

## Cost

No build in this session; the stage joins batch A's one app build with
`-Capture`. Checks on that build whose subject the stage changes:
`SourceChecks.ps1` (Library's kind filter, file kinds, subtitle jobs),
`AddSourceChecks.ps1` (the Add draft's refusal paths) and
`NotificationChecks.ps1` (the command error toast). Capture modes whose
journeys run changed code: AddLayout, FileControls, Connections, Peers, Map,
SettingsLayout, Schedule, SubtitleTasks and Dialogs. About four hours of
agent work for the gate and the change.

## Review fixes for stage 1

The review of the stage found four defects. The stage 2 session fixed them
in the same tree, by reading; nothing was built or run.

1. **One `$kind` for every configuration.** `Store.Match` bound a parameter
   named `$kind`, and the counts query calls `Match` once for each
   configuration on one command, so one name held two values. `Match` now
   writes the kind into the SQL as a number, `r.kind={(int)FileKind.Video}`,
   as `Store/Identification.cs` does, and binds nothing. The capture
   fixture `PrepareTiming` binds `$video` and `$audio` once each on its
   command, so it does not have the problem and stays.
2. **Saved `.ssa` and `.vtt` rows kept their old kind.** `FileSources`
   rewrote a `source_files` row only when its entry, name, path, size,
   completion or wanted state changed, and `Unchanged` skipped such a
   contribution whole. Both now compare the kind too, so a saved file takes
   the file-kind table's new kind the next time its torrent's files are
   read. `docs/contracts/library.md` states this.
3. **Two movie-or-series copies.** `FileSources` (the stored `type`) and
   `OpenSubtitles` (the search's `type`) now read `Release.Kind`. The
   OpenSubtitles query line above it still asks whether the name has a
   season, as decision 13 says.
4. **The skip priority written two ways.** `FileAction` and `FileNode` in
   `Controls/FileSelection.cs` now read `FilePriority.Skip` everywhere. A
   new `FileAction.Skips` answers "this choice skips the files" for the two
   places that matched `Set { Priority: 0 }`. `priority > 0` became
   `!= FilePriority.Skip.Value` for files, whose priority is never negative,
   so nothing changes.
