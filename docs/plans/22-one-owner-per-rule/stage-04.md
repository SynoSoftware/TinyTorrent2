# Stage 4 gate record: scheduling, dialogs, menus and commands

Gate done on 12 November 2026, by reading, on the working tree that holds
stages 1, 2, 3 and 16b. Nothing was built or run. This record takes the place
of [the stage's text](../22-one-owner-per-rule.md#4-scheduling-dialogs-menus-and-commands)
for the session that implements it. Paths without a prefix are under
`app/src`; line numbers are those of that working tree, and each cites its
member.

## Where the stage stands

Seventeen owners. Three are new files: the type `Shared/TurnCallback.cs` and
the static classes `Controls/VisualTree.cs` and `Controls/IconButton.cs`; the
others are members on existing owners. The gate found eighteen more copies
than the plan lists. It dropped two findings, and parts of two more.
The open question is answered: `Placement.Rendered` does not move. Stage 10's
question is answered: the new owner accepts a request from any thread.

The stage adds no product text, so no catalogue changes. A person sees two
changes, both "Settled as" choices: the Library menus hide the metadata
commands for a selection without a video, and Remove's primary button is
disabled while the window closes.

## Gate step 1: the findings

| Finding | Rule | Result |
| --- | --- | --- |
| Once per turn | An action requested several times in one turn runs once on the UI thread after that turn, at its stated priority. | Kept. The plan's eight: `MainWindow.QueueTorrentMenu` (`Shell/MainWindow/Actions.cs` 239), `ObserveStatus` and `QueueDragRegions` (`Chrome.cs` 141, 523), `InspectorPane.RefreshMap` and `QueuePeerRows` (`Shell/InspectorPane/Peers.cs` 431, 483), `InspectorPane.OnRows` and `QueueFacts` (`Shell/InspectorPane.xaml.cs` 298, 487), `SettingsPage.QueueHome` (`Settings/SettingsPage/Home.cs` 149). The gate found three more: `PiecesMap.QueueDraw` (`Torrents/PiecesMap.xaml.cs` 217), `MainViewModel.QueueSnapshot` and `QueueDetail` (`Shell/MainViewModel.cs` 1094, 1121), the last two requested from the pipe's reader thread. |
| `FrameCallback` written by hand | — | **Dropped** for `Placement.Rendered`: another rule (step 2, question 1). The capture journeys that await a `FrameCallback` (`CapturePeers.cs` 2066, 2283) already use the owner. |
| One `Confirm` beside `ShowDialog` (to re-read) | A confirmation asks one question in an interaction and acts only when the person chooses its primary button. | Kept for `ConfirmDatabaseDeletion` (`Dialogs.cs` 236), `ConfirmDownload` and `ConfirmDataDeletion` (`Datasets.cs` 16, 87), `ConfirmReset` (`Shell/MainWindow.cs` 797), and the capture copy of the data deletion dialog (`CaptureSettings.cs` 934). **Dropped** for `ConfirmMerge` (`Actions.cs` 498): both answers go on to submit inside the interaction, so it is a choice, not a confirmation. **Dropped** for `ConfirmExit` (`Shell/MainWindow.cs` 754): it sends `DeferClose` inside the interaction before the question shows (776), and a parameter for that step would serve one caller. |
| Remove's primary button (to re-read) | A dialog whose primary button submits a draft enables it while the draft can submit and the window is not closing. | Kept. `ShowEditor.EnableSubmit` (`Dialogs.cs` 143) has the rule; `ShowFileDialog` binds Remove's button to `CanSubmit` alone (`Actions.cs` 661 to 669). |
| Library key saving (to re-read) | Library decides what the account dialog's input saves: a typed key replaces the saved one, the built-in key clears a saved one, and an unchanged choice saves nothing, because saving restarts the provider's lookups. | Kept. The rule sits in `MainWindow.ShowKey` (`Shell/MainWindow/Library.cs` 272 to 297), in Shell. Subtitles keeps its own in `Acquisition.Edit` and `Save` (`Subtitles/Acquisition.cs` 417, 466), which is the shape Library takes. |
| One Library row menu | The Library menu bar, the row context menu and the inspector's More menu show the same metadata group: Fetch metadata, Refresh metadata, Choose or Download subtitles, then Erase metadata, each group after a separator. | Kept for that group: `MainWindow.LibraryMenu` (`Shell/MainWindow/Library.cs` 226) and `InspectorPane.LibraryMenu` (`Shell/InspectorPane/Library.cs` 216). The commands before it differ by host for a reason that holds: the inspector shows Open and Show in Torrents as its own buttons unless narrow, and is itself Properties. |
| `Library/Details.cs` 66 | Show in Torrents for an origin runs only while that row is selected and the origin is still listed. | Kept. `Details` writes its own command with `CanShowTorrent` alone; `OriginActions.Command` (`Library/OriginActions.cs` 47) has the full rule. |
| "Supports metadata" (found) | A selection supports metadata when it holds a video. | Kept. Spelled four times: `Browser.SupportsMetadata` (`Library/Browser/Information.cs` 41, the selected row only), `FetchMetadata`'s condition (`Library/Browser.cs` 386), `MainViewModel.CanDownloadSubtitles` (`Shell/MainViewModel/Actions.cs` 569), and now the menu group. |
| Copy magnet and copy hash (to re-read) | A torrent's hashes are its row's `Hashes`. | Kept for the hashes. `MainViewModel.CopyTorrent(true)` (`Shell/MainViewModel/Actions.cs` 489) asks the engine for a General detail to read `hashes`; the row already holds them, from the same engine function (`engine/src/Torrent.cpp` 1304 for detail, 1449 and 1474 for the row, both `Torrent::Hashes`), and the inspector reads the row (`Shell/Inspector.cs` 128, 130). Copying all hashes (window) and one per field (inspector) are different commands and stay. **Dropped** for the magnet: the window's read is a complete read, which the engine answers only after tracker detail (`engine/src/Engine/Detail.cpp` 18 to 20, 415 to 417), so it always has a magnet; the inspector's display read can arrive without one (`engine/src/Torrent.cpp` 1428 to 1430) and its command stays off until it does (`Shell/Inspector.cs` 263, 1002). Each read implements the rule for its own state. |
| Move and Download predicate (to re-read) | Move and Download act on listed torrents that are not moving, while editing works. | Kept. `MoveCommand` and `DownloadCommand` (`Shell/MainViewModel/Actions.cs` 212, 225) repeat it. |
| Two notification paths | A command failure replaces the shown error toast of its kind, or adds one. | Kept. `Notifications.Report(CommandFailure)` (`Shell/Notifications.cs` 66) is `ReportProblem` (86) with no kind. |
| "Features may continue" guard | A step that began with a subtitle worker attached goes on after a wait only while the window is not closing, the features are neither stopped nor resetting, and the same worker is attached. | Kept. Five copies in `Features.Refresh` (`Shell/Features.cs` 320, 337, 349) and `Features.Reconcile` (383, 407), each with a different subset. |
| Library recovery (to re-read) | A recovery shows as Recovery maintenance work and reports no success, because the cleared message bar shows it. | Kept. `Features.Retry` and `Features.Reset` (`Shell/Features.cs` 435 to 449, 497 to 516). |
| Peers and Trackers placeholder (to re-read) | A detail table without rows shows Loading while its read runs and Empty otherwise; its empty text shows once rows arrived. | Kept. `InspectorPane.Refresh` (`Shell/InspectorPane.xaml.cs` 453 to 457, 466 to 472). |
| Peer scope (to re-read) | The peer views cover every torrent when the person chose all, or when nothing narrower is chosen. | Kept. `Inspector.Scope` (`Shell/Inspector/Peers.cs` 357) and `IsGlobal` (372) spell the same cascade twice. |
| IME composition guard | While an input method composes text in a box, Enter, Escape and leaving the box belong to it ([localisation](../../contracts/localisation.md), "Native text composition and IME candidate handling get first refusal on input"). | Kept. `ConnectionPage` (`Settings/ConnectionPage.xaml.cs` 31, 120), `SettingsPage.OnFieldFocus`, `OnFieldKey`, `OnFieldDeparture` (`Settings/SettingsPage.xaml.cs` 883, 892, 916), `InspectorPane.LoadSection` (`Shell/InspectorPane.xaml.cs` 213 to 234). |
| Inside a container (to re-read) | An element is inside a container when the container is the element or one of its visual ancestors. | Kept. `MainWindow.IsInside` (`Shell/MainWindow/Notifications.cs` 122), `InspectorPane.HasTrackerEditorFocus` (`Shell/InspectorPane.xaml.cs` 273), `SettingsPage.OnFieldDeparture` (`Settings/SettingsPage.xaml.cs` 920 to 934). The gate found five capture copies: `Capture.cs` 369 to 373 and 464 to 466, `CaptureSearch.cs` 183 to 185, 270 to 271 and 296 to 297. |
| Name equals tooltip (to re-read) | An icon-only button's tooltip is its accessible name ([UI contract](../../contracts/ui.md#buttons), "An icon-only button names itself in a tooltip"). | Kept. `MainWindow.NameButton` (`Chrome.cs` 663) and `InspectorPane.RefreshPeerActions` (`Shell/InspectorPane/Peers.cs` 263, 269, 271). The gate found seven more: `Pane`'s local `Name` (`Controls/Pane.cs` 314), `FieldHeader` (`Controls/FieldHeader.xaml.cs` 56), `Details`' local `Action` (`Library/Details.cs` 523), `WorkStatus` (`Shared/WorkStatus.xaml.cs` 199), `InspectorPane.RefreshLibraryActions` (`Shell/InspectorPane/Library.cs` 206, 209), `Subtitles.Settings` (`Subtitles/Settings.cs` 398). |
| Open a path (to re-read) | Opening a path reports its failure in the window. | Kept. `MainWindow.Open` (`Shell/MainWindow/Opening.cs` 12) and the `FolderRequested` handler (`Shell/MainWindow/Library.cs` 68). |
| Popup theme (to re-read) | A popup takes the window root's theme and reading direction. | Kept. `MainWindow.RefreshPopups` (`Chrome.cs` 729) and `OnWorkOpening` (`Shell/MainWindow/Work.cs` 173). |
| Modifier keys read from the platform (to re-read) | — | **Dropped.** Each copy is one WinUI call, `InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Down)`, as its documentation shows. A member that wraps it only forwards, which [naming](../../agents/naming.md#files) refuses, and the plan keeps the table's own copy for the same reason ([What stays as it is](../22-one-owner-per-rule.md#what-stays-as-it-is)). `Shell/MainWindow.cs` 143 is a product rule of its own: Shift deletes permanently, as in File Explorer. |

Copies that look alike and stay:

- `BackgroundWork.Queue` (`Shared/BackgroundWork.cs` 49). Its queued flag is
  part of a throttle that its lock guards: `Operation.Report` reads it to
  decide whether to start the 100 ms timer (152), and `UpdateChecks.ps1` 84
  sets it by name.
- `Subtitles.Batch.OnChanged` (`Subtitles/Batch.cs` 114). Stage 10 replaces
  it with its view connection, which uses the new owner (step 2, question 2).
- `Inspector.Yield` (`Shell/Inspector.cs` 883). It awaits the end of the turn
  inside one read; it does not merge requests.
- `Settings/Week.cs` 85 and the peer map's frame queue
  (`Torrents/PeerMap.xaml.cs` 337). They draw inside the rendering event, on
  purpose, as the plan keeps for the map.
- Every plain `DispatcherQueue.TryEnqueue` that does not merge requests, such
  as `Library/Browser.cs` 490 to 518 and `Shell/MainWindow/Workspace.cs` 82.
- The entry checks of the operations in `Features`: `Observe` (134), `Retry`
  (424), `Delete` (478), `RetrySubtitles` (621), `ConfigureRoute` (244). They
  decide whether an operation starts, not whether a step goes on after a
  wait, and they differ on purpose: Retry and Delete may start while the
  window closes. The plan settles only the continuation guard.
- `Datasets.ConfirmDownload` keeps its own primary button condition
  (`dataset.CanDownload`) and its click handler, which starts the download
  in the click so that a dataset that can no longer download keeps the
  dialog open. `MainWindow.DownloadSubtitles` keeps `Batch.CanAct`
  (`Shell/MainWindow/Subtitles.cs` 30). Neither submits a draft.
- `MainWindow.Ask` (`Workspace.cs` 372) and `AskUnfinished` (`Dialogs.cs` 263)
  have two answers each, so they stay on `ShowDialog`.
- `MainWindow/Library.cs` 166 gives the workspace picker a name and a tooltip
  with different texts.

## Gate step 2: the questions

1. **Does startup need the moment at which `Placement.Rendered` resumes?**
   The plan's premise does not hold: `Rendered` does not resume inside the
   rendering event. It completes a `TaskCompletionSource` created with
   `RunContinuationsAsynchronously` (`Shell/MainWindow/Placement.cs` 93), so
   `ShowWhenReady`'s `await Rendered()` (54, 63) resumes from the dispatcher
   after the handler returns. The UI thread runs one item at a time, so that
   continuation runs after the frame the event started has been laid out and
   drawn. WinUI documents that `Rendering` occurs "when the core rendering
   process renders a frame" and `Rendered` "immediately after" it, both on the
   UI thread. `FrameCallback` posts at Low priority from the same event
   (`Shared/FrameCallback.cs` 22), and Low work, as documented, "will be
   scheduled when there isn't any other work to process". The awaited
   continuation goes through the window's synchronization context, which posts
   without a priority, so at Normal, the documented default.
   **Answer: `Rendered` stays.** Startup awaits a frame as a `Task` inside one
   sequential flow; `FrameCallback` is a cancellable callback. Moving would add
   a task wrapper and put the uncloak (`Reveal`, 66) behind every Low item
   queued during startup (menus, status bar, drag regions, the selection
   refresh), which delays the window's first appearance and shows nothing
   sooner, because those items render in a later frame either way. The two
   are different rules: resume a sequence after the next frame, against run a
   deferred refresh after it.
2. **Does the "once per turn" owner accept a request from a worker thread
   (stage 10, decision 12)?** **Yes.** `TurnCallback.Request` may be called on
   any thread; its action always runs on the dispatcher's thread. Two copies
   in this stage need it, `MainViewModel.QueueSnapshot` and `QueueDetail`, which
   the pipe's reader thread calls (`Shell/MainViewModel.cs` 732, 733), and the
   stage 10 view connection needs it. Under stage 10's rule its view connection
   uses `TurnCallback` and `Batch`'s interlocked flag goes. Stage 10 states the
   priority: `Batch` posts at Normal today (`Subtitles/Batch.cs` 119).
3. **Which priority does each caller keep?** Its own. Seven callers post at
   Low; `SettingsPage.QueueHome` and `PiecesMap.QueueDraw` post at Normal, the
   default; `QueueSnapshot` at Normal and `QueueDetail` at Low. The plan
   settles no timing, and the pieces map redraws after a resize: at Low it
   would wait behind every Normal item. The priority is the platform's own
   parameter, so each caller states it at construction.
4. **What does "the guard checks reset and a missing subtitle worker in every
   copy" mean for the first copy, which guards `Library.Observe`?** The worker
   the step began with must still be attached. A refresh that began with no
   features attached still tells Library its readiness, because that call is
   what tells Library it is offline (`Shell/Features.cs` 327 to 332). `_source`
   and `Subtitles` are set together in `Observe` (178, 188, on the UI thread,
   with no wait between) and cleared together in `Detach` (689 to 694, under the
   gate), so comparing the worker is comparing the source, which the first copy
   compares today. Inside `Refresh` the predicate adds nothing that can be
   false today, because `Refresh` holds `_access` and `Reset` and `Close` wait
   for it; it names the rule and removes the unchecked `Subtitles.SetConnected`
   after a wait (356).
5. **Does "a dialog's primary button is disabled while the window closes"
   reach every dialog?** It settles the one difference between the copies the
   stage names: `ShowEditor` disables while closing and Remove does not. It
   applies to every dialog whose primary button submits a draft. A dialog can
   be open while the window closes: `CloseWindow` keeps the window visible
   while a dialog is open and hides the dialog only after `PrepareClose`
   (`Shell/MainWindow.cs` 847 to 861), which can wait for pending work
   (`Shell/MainViewModel/Closing.cs` 39 to 64). Remove's click then closed the
   dialog and removed nothing (`Actions.cs` 676), and now the button is
   disabled. Confirmations keep today's behavior; `ConfirmReset` keeps its own
   check (`Shell/MainWindow.cs` 826), whose reason is its own act: it starts
   a close.
6. **Where does the Library key rule live?** In `Library.Browser`, beside
   `HasKey`, which becomes private: after the move only
   `ChooseAutomaticDownload` (`Library/Browser/Information.cs` 162) reads it.
   Turning automatic download on after the key is saved stays in `ShowKey`,
   because it is the step of the Settings request that asked for the key
   (`Settings/SettingsPage.xaml.cs` 181, `ProviderSetup`), and Library cannot
   name a Settings type.

## Gate step 3: owners, interfaces and callers

Candidates compared with `/codebase-design`:

| Owner | Candidate | Result |
| --- | --- | --- |
| Once per turn | Flags in each caller, as today | Rejected: eleven copies, and three leave the flag set when `TryEnqueue` fails (`PiecesMap.QueueDraw`, `QueueSnapshot`, `QueueDetail`). |
| Once per turn | A static `Queue(object key, Action)` keyed table | Rejected: a hidden registry, and every key is another thing to name. |
| Once per turn | **`TurnCallback`, one instance per action**, built with its dispatcher, priority and action, with one member `Request()` | **Chosen.** It is `FrameCallback`'s shape, the flag and its reset on a failed post live once, and the action delegate is made once instead of a closure per post. |
| Confirmation | `Confirm(interaction, dialog, refreshText)` returning `ShowDialog(...) == Primary` | Rejected: it hides one comparison; deleting it brings back nothing. |
| Confirmation | A `Confirmation` record of glyphs and texts | Rejected: it restates `Dialog`'s own properties, and the bodies differ (a string, `DataConfirmation`, `Lines`). |
| Confirmation | **`Confirm(dialog, refreshText, act)`**, which opens its own interaction | **Chosen.** Callers keep their dialog and text, and lose the interaction, the comparison and the act's placement inside the interaction. |
| Draft dialog button | A method that subscribes and returns an `IDisposable` | Rejected: a new convention for one rule. |
| Draft dialog button | **An optional `IDraft` on `ShowDialog`** | **Chosen.** `ShowEditor` and Remove both call `ShowDialog`; the draft is data, not a behavior switch, and `ShowDialog` already has the `try`/`finally` that unsubscribes. |
| Library row menu | The whole menu with a host parameter | Rejected: a flag for each host, which the plan's rules refuse. |
| Library row menu | **The metadata group in `MetadataActions`** | **Chosen.** It is the part both hosts repeat, and `MetadataActions` already owns the Fetch submenu and the Info page's Fetch button. |
| IME guard | Sets of composing boxes in each page, as today | Rejected: three copies, one of them sharing a flag across two boxes. |
| IME guard | A `ConditionalWeakTable` in `TextEditor` | Rejected: the state would follow a projected wrapper's identity, not the native box. |
| IME guard | **Attached properties in `TextEditor`**, as `Trimming` keeps its tooltip | **Chosen.** The state lives on the box. |

| Owner | Interface | Callers, and the member each calls |
| --- | --- | --- |
| `TurnCallback` (`Shared/TurnCallback.cs`, new) | `internal TurnCallback(DispatcherQueue dispatcher, DispatcherQueuePriority priority, Action action)`; `internal void Request()`. Any thread may request; the action runs on the dispatcher's thread, once after the turn, however many requests arrived before it ran. A request during the action queues another run, because the flag clears before the action. A failed post clears the flag. The flag is an `int` changed with `Interlocked.Exchange`, which orders the requester's data writes before the action's reads. | `Request()` from: `QueueTorrentMenu` (Low; the menu fill becomes a method whose body is today's lambda); `ObserveStatus` (Low; action `UpdateStatus`, which checks `_allowClose`); `QueueDragRegions` (Low; action `UpdateDragRegions`); `RefreshMap` and `QueuePeerRows` (Low); `OnRows` and `QueueFacts` (Low); `QueueHome` (Normal; keeps its request check, because `OnMain` reaches it on every transfer total change, `Settings/SettingsPage.xaml.cs` 223); `PiecesMap.QueueDraw` (Normal; keeps `_revision++`); `QueueSnapshot` (Normal) and `QueueDetail` (Low), whose data stays under `_snapshotGate` while `_applyQueued` and `_detailQueued` go. Each instance is a `readonly` field assigned first in its owner's constructor (`MainWindow`, `InspectorPane`, `SettingsPage`, `PiecesMap`, `MainViewModel`), because a field initializer cannot name an instance member and layout, the `Layout` setter (`Shell/InspectorPane.xaml.cs` 77) and the property callbacks registered in `PiecesMap`'s constructor request before the constructor returns. |
| `MainWindow.ShowDialog` (`Dialogs.cs` 100) | Gains `IDraft? draft = null`. With a draft, the primary button is enabled while `draft.CanSubmit` holds and `Model.IsClosing` does not, followed through the draft's and the model's `PropertyChanged` until the dialog closes. | `ShowEditor` passes its draft and loses `EnableSubmit` and its two subscriptions (143 to 147, 177, 178). `ShowFileDialog`'s Remove branch passes its draft and loses the binding (`Actions.cs` 661 to 669) and `using Microsoft.UI.Xaml.Data`. |
| `MainWindow.Confirm` (`Dialogs.cs`, beside `ShowDialog`) | `private Task<bool> Confirm(Dialog dialog, Action refreshText, Func<Task>? act = null)`: opens an interaction, shows the dialog, and when the person chose the primary button runs `act` inside the interaction, so no other dialog opens until it ends; true then. | `ConfirmDatabaseDeletion` (act `Model.Features.Delete`); `ConfirmDataDeletion` (act `dataset.Delete`); `ConfirmReset` (no act; keeps `|| Model.IsClosing` before `CloseWindow`); `ConfirmDownload` (no act; subscribes `Follow` and the click handler before the call and unsubscribes in `finally`); capture `CapturePlugins` (`CaptureSettings.cs` 934, no act). |
| `Library.Browser` (`Library/Browser/Information.cs`) | `internal AccountInput Key(string providerId)`: the provider's own key as `OwnKey(Kept)`, else `BuiltIn`. `internal Task SaveKey(string providerId, AccountInput input)` replaces `SaveKey(string, string?)` and holds the rule of step 1, with the comment that `ShowKey` has today. `HasKey` becomes private. | `MainWindow.ShowKey` (`Shell/MainWindow/Library.cs` 272) passes `Key(option.ProviderId)` as the saved input and calls `SaveKey(option.ProviderId, input)`, then `EnableAutomaticDownload` when asked. |
| `Browser.SupportsMetadata` (`Library/Browser/Information.cs` 41) | Redefined as "the selection holds a video": `Selection.Items.Cast<LibraryRow>().Any(row => row.Entry.Kind == FileKind.Video)`. For one selected row it equals today's value, so `CanRefresh` (84), which checks the count first, is unchanged. | `FetchMetadata`'s condition (`Library/Browser.cs` 386) reads `CanLookup && SupportsMetadata`; `MainViewModel.CanDownloadSubtitles` (`Shell/MainViewModel/Actions.cs` 569) reads `Library.SupportsMetadata`; `MetadataActions.Menu`. |
| `MetadataActions` (`Library/MetadataActions.cs`) | `internal static IEnumerable<MenuEntry> Menu(Browser browser, ICommand subtitles)`: separator, `Create(browser)`, Refresh metadata, the subtitles command ("choose" for one selected row, "download_title" for more), separator, Erase metadata; nothing when `SupportsMetadata` is false. The subtitles command is a parameter because it is `MainViewModel.DownloadSubtitles`, which Library cannot name. | `MainWindow.LibraryMenu` (`Shell/MainWindow/Library.cs` 226) ends with `.. MetadataActions.Menu(Model.Library, Model.DownloadSubtitles)`; `InspectorPane.LibraryMenu` (`Shell/InspectorPane/Library.cs` 216) adds `MetadataActions.Menu(library, _downloadSubtitles)` before `Guard`. |
| `OriginActions` (`Library/OriginActions.cs`) | `Command(Browser, Origin?)` becomes internal. | `Details`' File page (`Library/Details.cs` 66 to 76) calls `OriginActions.Command(library, origin)`. |
| `MainViewModel` copy and folder commands (`Shell/MainViewModel/Actions.cs`) | `CopyHash` copies `string.Join(Environment.NewLine, _selected[0].Hashes)` with no engine read; `CopyTorrent(bool)` becomes `CopyLink()`, the magnet only. `private bool CanChangeFolder(Torrent[] torrents)`: editing works, at least one torrent, each listed and not moving. | `CopyHash` and `CopyMagnet` construction (`Shell/MainViewModel.cs` 578 to 585); `MoveCommand` and `DownloadCommand` (212, 225) call `CanChangeFolder(targets())`. `CanMove` is taken by the queue commands (77). |
| `Notifications.Report(CommandFailure failure, string? kind = null)` (`Shell/Notifications.cs` 66) | Replaces `ReportProblem`; finds the error toast whose `Key` equals `kind`, which for null is today's `Error`. | `Report(Exception, string, …)` (64), `Report(Exception, MoveRecovery)` (56), `Receive`'s problem toast (225). |
| `Features` (`Shell/Features.cs`) | `private bool MayContinue(SubtitleAcquisition? subtitles)`: not closing, not stopped, not resetting, and `ReferenceEquals(subtitles, Subtitles)`. `private bool IsReady`: `_source is { IsReady: true } && _owner.IsConnected`. `private BackgroundWork.Operation BeginRecovery()`: begins Recovery work with `ReportsSuccess = false`. | `Refresh` captures `Subtitles` where it captures `_source` today and returns when it is null after `Library.Observe`; its three guards become `!MayContinue(subtitles)`, then `!MayContinue(subtitles) \|\| !IsReady` twice, and it calls `SetConnected`, `SetLanguage` and `Reconcile` on the captured worker. `Reconcile`'s guards become `!MayContinue(subtitles) \|\| !IsReady` and `!MayContinue(subtitles)`. `Retry` and `Reset` call `BeginRecovery()` and drop the two `ReportsSuccess = false` lines and their comments. |
| `InspectorPane.Refresh` (`Shell/InspectorPane.xaml.cs` 402) | A local function that takes a table, its empty text and whether its rows arrived, and sets the placeholder and the text's visibility from `loading`. | The Peers branch (449 to 458) and the Trackers branch (459 to 473). |
| `Inspector` peer scope (`Shell/Inspector/Peers.cs`) | `private IEnumerable<Torrent>? Narrowed`: the cascade of `Scope` with null where it means every torrent. `Scope => Narrowed ?? _owner.Torrents`. `IsGlobal => _section is Peers or Speed && Narrowed is null`. | `Scope`'s and `IsGlobal`'s readers are unchanged (`Shell/Inspector.cs` 69, 70, 75, 99, 563, 897, 957; capture `CaptureConnections.cs` 42, 70, 80, 106; `CapturePeers.cs` 1101, 1110). |
| `TextEditor` (`Controls/TextEditor.cs`) | `internal static void FollowComposition(TextBox editor)`, idempotent; `internal static bool IsComposing(TextBox editor)`. Two private attached properties, as `Trimming` keeps `Tip`: one whose change callback subscribes static `TextCompositionStarted` and `TextCompositionEnded` handlers, one that holds the state. The class becomes public, as every type in the app that registers a dependency property is (`Trimming`, `SettingsSection`); its members stay internal. | `ConnectionPage`'s constructor follows `Download` and `Upload`, and `OnApplyKey` asks `IsComposing((TextBox)sender)`; `_composing` goes. `SettingsPage.OnFieldFocus` follows the field, and `OnFieldKey` and `OnFieldDeparture` ask `IsComposing(editor)`; `_composing` and `_editors` go. `InspectorPane.LoadSection` follows `TrackerInput`, and its Escape handler and Save click ask `IsComposing(TrackerInput)`; `_composing` goes. |
| `VisualTree` (`Controls/VisualTree.cs`, new) | `internal static bool Contains(DependencyObject container, DependencyObject? element)`. | `MainWindow.HasToastKeyboardFocus` and `ShowToastMore` (`Shell/MainWindow/Notifications.cs` 119, 164; `IsInside` goes); `InspectorPane.HasTrackerEditorFocus` (keeps its "nothing focused" answer); `SettingsPage.OnFieldDeparture` (inside the row returns; else inside the page departs); capture `Capture.cs` 369, 464 and `CaptureSearch.cs` 183, 270, 296. |
| `IconButton` (`Controls/IconButton.cs`, new) | `internal static void Name(FrameworkElement button, string text)`: sets the accessible name and the tooltip to `text`, each only when it differs, as `NameButton` does, so a refresh does not replace an open tooltip. | `Chrome.cs` 660, 683, 684, 685 (`NameButton` goes); `Pane`'s local `Name` (`Controls/Pane.cs` 314); `FieldHeader` 56; `Details`' `Action` 523; `WorkStatus` 199; `InspectorPane.RefreshLibraryActions` 206, 209; `RefreshPeerActions` 263, 269, 271; `Subtitles.Settings` 398. |
| `MainWindow` popups (`Chrome.cs`) | `private void MatchRoot(FrameworkElement popup)`: the root's theme and reading direction. | `RefreshPopups` for the dialog, the toast stack and the work list; `OnWorkOpening` (`Work.cs` 173). |
| `MainWindow.Open` (`Opening.cs` 12) | Unchanged. | The `FolderRequested` handler (`Shell/MainWindow/Library.cs` 68) becomes `(_, path) => Open(new(path))`, as `Workspace.cs` 257 and 264 already call it. |

## Decisions the plan did not settle

1. **`Placement.Rendered` stays** (step 2, question 1).
2. **`TurnCallback` accepts requests from any thread** (step 2, question 2).
3. **Each caller keeps its priority** (step 2, question 3), so no timing
   changes.
4. **`TurnCallback` has no `Dispose`.** Every action checks its owner's state
   when it runs (`_allowClose`, `IsLoaded`, `_closed`), as today; nothing
   cancels a queued run.
5. **`ConfirmMerge` and `ConfirmExit` stay on `ShowDialog`** (step 1).
6. **The confirmation's act runs inside its interaction**, as the two deletions
   run today, so no dialog opens over a deletion in progress, and `Interact`
   reports its failure.
7. **The draft dialog rule lives in `ShowDialog`** (step 2, question 5).
8. **The Library key rule lives in `Library.Browser`; enabling automatic
   download stays with `ShowKey`** (step 2, question 6).
9. **The Library menu owner is the metadata group**, not the whole menu
   (step 3).
10. **`SupportsMetadata` reads the selection.** For a multiple selection,
    "a row that supports metadata" is a video in it: the batch Fetch, Erase and
    Download subtitles act on the selection (`Library/Browser.cs` 386, 401 to
    405; `Shell/MainViewModel.cs` 626 to 630), so a selected-row test would
    hide them for every multiple selection.
11. **Copy hash reads the row** (step 1). One engine round trip goes.
12. **The `Features` guard keeps the readiness check where it is today**,
    named `IsReady`, and the first copy keeps running `Library.Observe` with no
    worker attached (step 2, question 4).
13. **The modifier key finding is dropped** (step 1).
14. **`IconButton.Name` applies only where the name and tooltip are the same
    text.** Back is one of them, because `NameButton` gives it its shortcut
    tip as both (`Chrome.cs` 685). The workspace picker
    (`Shell/MainWindow/Library.cs` 166) and the map's Fit button
    (`Torrents/PeerMap/Navigation.cs` 63) keep their own calls, because their
    two texts differ.
15. **`app/AGENTS.md`'s Dialogs section names the new owners**, because a
    dialog writer looks there first: `ShowDialog` takes a draft's primary
    button, `ShowEditor` no longer does, and `Confirm` gets one bullet. The
    implementer loads `/writing-for-agents` before that edit.
16. **No product term changes.** `TurnCallback`, `VisualTree` and
    `IconButton` are code names; "confirmation" is already the
    [UI contract](../../contracts/ui.md#buttons)'s word. `CONTEXT.md` needs no
    change.

## Gate step 4: consumers of every changed name

Searched: `app/src` with the capture journeys and `#if CAPTURE` blocks, all
XAML, and `app/tests/*.ps1`.

- `ShowDialog`: `Dialogs.cs`, `Datasets.cs`, `Actions.cs`, `Shell/MainWindow.cs`,
  `Workspace.cs`, `Subtitles.cs`, capture `CaptureSettings.cs` 941. The new
  parameter is optional.
- `ReportProblem`: `Shell/Notifications.cs` 56, 225 only.
  `NotificationChecks.ps1` 62 finds `Report` by its `[Exception]` signature,
  which stays.
- `SaveKey(string, string?)`, `HasKey`: `Information.cs` and
  `Shell/MainWindow/Library.cs` only. The `EnableAutomaticDownload` calls in
  `SourceChecks.ps1` 235, 1090, 1145 are on `Store` and `VideoLookup`.
- `SupportsMetadata`: `Information.cs` 41, 84; `Browser.cs` 1106 raises it;
  `Shell/InspectorPane/Library.cs` 233. No XAML binds it.
- `OriginActions.Command`: `OriginActions.cs` only today.
- `LibraryMenu`: `Shell/MainWindow/Library.cs` 221; `Actions.cs` 361; capture
  `CaptureLibraryFiles.cs` 496 through `LibraryContextMenu`, which stays.
- `CopyTorrent`: `Shell/MainViewModel/Actions.cs` and `Shell/MainViewModel.cs`.
- `IsInside`, `NameButton`: their files only.
- `_applyQueued`, `_detailQueued`, `_menusPending`, `_statusPending`,
  `_dragPending`, `_mapQueued`, `_peerRowsQueued`, `_rowsQueued`,
  `_factsQueued`, `_homeQueued`, `PiecesMap._queued`, the three `_composing`
  fields and `SettingsPage._editors`: their files only; no check script names
  them. `UpdateChecks.ps1` 84 names `BackgroundWork._queued`, which stays.
- `IsGlobal` keeps its meaning for its capture readers (step 3).
- `NotificationChecks.ps1` and `UpdateChecks.ps1` build `MainViewModel` without
  its constructor; neither reaches `QueueSnapshot` or `QueueDetail`, whose new
  fields would be null there.
- No product text changes, so no catalogue changes.

## Files the change may edit

Product code: `Shared/TurnCallback.cs` (new), `Controls/VisualTree.cs` (new),
`Controls/IconButton.cs` (new), `Controls/TextEditor.cs`, `Controls/Pane.cs`,
`Controls/FieldHeader.xaml.cs`, `Shared/WorkStatus.xaml.cs`;
`Library/Browser.cs`, `Library/Browser/Information.cs`,
`Library/MetadataActions.cs`, `Library/OriginActions.cs`, `Library/Details.cs`;
`Subtitles/Settings.cs` (one call); `Torrents/PiecesMap.xaml.cs`;
`Settings/SettingsPage.xaml.cs`, `Settings/SettingsPage/Home.cs`,
`Settings/ConnectionPage.xaml.cs`; `Shell/Features.cs`, `Shell/Notifications.cs`,
`Shell/Inspector/Peers.cs`, `Shell/InspectorPane.xaml.cs`,
`Shell/InspectorPane/Peers.cs`, `Shell/InspectorPane/Library.cs`,
`Shell/MainViewModel.cs`, `Shell/MainViewModel/Actions.cs`,
`Shell/MainWindow.cs`, `Shell/MainWindow/Actions.cs`, `Chrome.cs`,
`Dialogs.cs`, `Datasets.cs`, `Library.cs`, `Notifications.cs`, `Work.cs`.
Capture journeys: `Capture.cs`, `CaptureSearch.cs`, `CaptureSettings.cs`.

Documents: `app/AGENTS.md` (Dialogs); `docs/contracts/library/presentation.md`
("Native composition": `MetadataActions` shares the metadata group across the
three menus and `Details` uses `OriginActions`' command; "Page and menus" and
the Metadata paragraph: the metadata groups show only for a selection that
holds a video).

Stages 5, 6 and 10 edit some of these files after this stage:
`SettingsPage.xaml.cs`, `Home.cs` and `ConnectionPage.xaml.cs` (stage 5),
`PiecesMap.xaml.cs` and `Shell/MainViewModel/Actions.cs` (stage 6), and
`Subtitles/Settings.cs` (stage 10). This stage changes only the members named
above in them.

## What changes for a person

- The Library menu, the row context menu and the inspector's More menu hide
  Fetch metadata, Refresh metadata, the subtitles command and Erase metadata
  when the selection holds no video. A Settled as choice; before, the window's
  two menus showed them disabled.
- While the window closes, Remove's primary button is disabled, as every
  editor's is. A Settled as choice; before, a click closed the dialog and
  removed nothing.
- Copy hash copies at once, with no engine request; the text is the same.
- Show in Torrents on the File page turns off when its row is no longer
  selected, as the menus' does.
- An icon-only button whose text did not change keeps its open tooltip on a
  refresh, as the title bar's buttons already do.

## Cost

No build in this session; the stage joins batch B's one app build with
`-Capture`, which is required because three capture journeys change.

Check script whose subject changes: `NotificationChecks.ps1`, whose command
error case (59 to 70) runs the merged `Report`. No other script reaches a
changed member.

Capture modes whose journeys run changed code: `shell`, `menus`, `footer` and
`workspace` (menu fill, status bar, drag regions); `selection`,
`selection-frame`, `peer-switch`, `peers` and `details` (inspector refreshes,
the Trackers editor's composition guard); `settings-layout`,
`settings-prototype` and `edits` (Home summaries, the data deletion
confirmation, the fields' composition guard); `files` (Remove's button);
`library-files` (the Library menus); `library` (Pieces drawing). Reading
answers each question these modes would ask, so none is needed for this
stage; the lead runs one only for a question that the batch's other changes
raise.

About seven hours of agent work: two for `TurnCallback` and its eleven callers,
one for the dialogs, one for the Library key and menus, one for the Shell
rules, one and a half for the Controls owners, and half an hour for the
documents; then one for the reviewer's findings. The gate took one session.

## What reading could not settle

- **The priority of an awaited continuation.** Step 2, question 1, takes
  Normal from WinUI's documented default for `TryEnqueue`, on the
  understanding that the window's synchronization context posts without a
  priority. The documentation page for that context was not found, and the
  answer does not depend on it: whatever the priority, `Rendered` never
  resumed inside the rendering event, and `FrameCallback`'s Low is the lowest.
- **Whether WinUI accepts an attached property registered on an internal
  type.** The design makes `TextEditor` public, as every registering type is,
  so it does not depend on the answer.
- **IME composition.** No capture journey types through an input method; the
  guard's logic is settled by reading, and its native delivery is not tested.
- **The hidden metadata group.** `library-files` selects videos only, so no
  existing capture shows a selection without one.

## Implementation notes

Implemented on 12 November 2026 against this record, by reading; nothing was
built or run. Every decision is implemented as written. These are the
differences from the record:

- **A fifth `NameButton` caller.** `Shell/MainWindow/Work.cs` 115 names the
  Work button. The gate's consumer search missed it. The button shows a label
  ("Work · 3"), but its name and its tooltip are one text ("Show all 3
  background operations"), so it calls `IconButton.Name` under decision 14.
  `IconButton`'s comment says what the method does for any button, not only
  an icon-only one.
- **A sixth capture consumer of `IsInside`.** `CaptureDialogs.cs` 127, the
  removal journey's focus check, calls `VisualTree.Contains`. The gate's
  search said "their files only" and missed this partial file.
- **`SupportsMetadata`'s change notice follows the selection.**
  `Browser.Select` (`Library/Browser.cs`) raised it only when `Selected`
  changed. The property now reads the whole selection, so the notice moves
  beside `Selection`'s, which every selection change raises. No listener reads
  it by name today: the menus rebuild from `Selection`.
- **The inspector's subtitles label follows the selection count.**
  `MetadataActions.Menu` chooses "Choose subtitles" for one row and "Download
  subtitles" for more, as the window's two menus did. The inspector's More
  menu showed "Choose subtitles" for every selection.
- **`QueueDragRegions` is gone.** `UpdateChrome` was its only caller and now
  requests `_dragUpdate` itself. `QueueTorrentMenu`, `ObserveStatus`,
  `QueueHome`, `QueueDraw`, `QueueSnapshot` and `QueueDetail` stay, because
  each does more than request: a version count, a guard, or data under a lock.
- **Names the record left open.** Actions: `PrepareMenus` (`FillMenus` is
  taken), `UpdateStatus`, `UpdateDragRegions`, `RefreshMap`, `DeliverPeerRows`
  (was `QueuePeerRows`), `RefreshTrackerRows`, `ShowFacts` (was `QueueFacts`),
  `RefreshHome`, `StartDraw`, `ApplyLatest`, `DrainDetail`. Fields: `_menus`,
  `_status`, `_dragUpdate`, `_mapRefresh`, `_peerRows`, `_trackerRows`,
  `_facts`, `_home`, `_draw`, `_apply`, `_detail`. `CopyTorrent(bool)` became
  `CopyLink()` and `CopyHashes()`. `DeliverPeerRows` and `RefreshTrackerRows`
  keep a third word, because the pane holds both tables.
- **`MainViewModel` assigns its two callbacks after `_dispatcher`,** not first.
  Nothing before that line can request them; the pipe client starts later.
- **Usings.** Removed where the change left them unused:
  `Microsoft.UI.Xaml.Data` (`Actions.cs`), `Microsoft.UI.Dispatching`
  (`Chrome.cs`), `Syno.TinyTorrent.Library` (`Shell/MainViewModel/Actions.cs`).
  `MainWindow.cs`, `InspectorPane.xaml.cs` and `SettingsPage.xaml.cs` take the
  `DispatcherQueuePriority` alias that `Actions.cs` already uses; three older
  lines in `MainWindow.cs` and `SettingsPage.xaml.cs` still spell the full
  name, because this stage does not touch them.
- **The contract's menu table also gains Refresh metadata,** which the menus
  showed and the table omitted. The video rule is stated once, in that table;
  the Metadata paragraph ("these same commands and labels") stays true.
- **Braces.** Lines that this stage writes or moves have braces on every
  control body; unchanged lines beside them keep their style.
