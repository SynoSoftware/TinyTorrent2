# Review C: the owner's standard

Review of [the first proposals](review.md), [review A](architect-a.md) and
[review B](architect-b.md), 2026-10-06. This review judges them by the owner's
standard in [handover](../../handover.md#coding-standard-and-goal):
easy-to-read, low-bloat code, not preservation of the current structure.

The reviewer checked the claims that decide something against the working
tree, the uncommitted diff, the engine snapshot code, the generated x:Bind
code, and open issues #20, #22, #24, #25, #29, #52, #78 and #112–#118. Nothing
was edited or built.

Issues #112–#118 were filed on 2026-10-06, after the architects wrote. Three of
them already cover parts of these proposals:

- #115: one dialog lifetime (proposal 2)
- #116: selection held three times (A-C)
- #114: Week redraws on unrelated refreshes (A-B, A-G)

The app has 8,507 lines of C#. The capture code is now 1,434 of them (17%),
because the uncommitted `CaptureLibrary.cs` adds 158 lines.

## Decisions on the disagreements

| Question | Decision | Why (verified) |
| --- | --- | --- |
| Theme inside `Preferences` (A) or left out (B) | **A: theme becomes an ordinary `Preference` field.** Language keeps its own path. | B's argument comes from [localisation](../../localisation.md), and that rule covers language only. #24 already decided that a theme change may wait for its save, which is how `Preference` works. Today `SelectTheme` and `ChangeLanguage` share `_settingsPending`, so a theme save disables the Language list and the reverse. That breaks the contract that a setting's control stays enabled. With theme moved, `_settingsPending` always equals `_changingLanguage`, so one of them is deleted. `ChangeLanguage` already merges rapid choices, so the Language list can stay enabled. |
| Connection-phase enum (A) or two properties and two message bars (B) | **Both, in one change.** The enum is the model. The two bars are the view. | `_connected`, `_loading`, `_storageFailed` and `_writable` allow combinations that make no sense, and nothing prevents them. `_writable` stores a derived value. AGENTS.md prefers explicit state over flags and says not to store derived values. The `Message` getter decodes the flags by priority. B's two bars fix what the user sees in #20, but without the enum the same flag decoding stays behind them. |
| Regroup the `MainViewModel` partial files (A) or no split (B) | **B: do not regroup.** | The partial files share all private state, so moving code between them changes no owner. It only moves lines and resets `git blame`. Each change below deletes code in the file where that code lives. |
| XAML text binding (B; A did not mention it) | **Do it.** It is the second-largest deletion. | See change 2. The technical claim is mostly verified. |
| Dialog lifetime as private members (A) or a small follow-up (B) | **A's shape, after change 1,** with B's rule that the drafts decide what to reopen. | #115 asks for this shape: a private value, no framework. It also names a recovery gap, which is confirmed in the code. |
| Delete the Limits dialog (A and B) | **Yes.** Confirming the interface.md edit is the owner's decision. | The current goal in handover.md sends Ctrl+K results to the screen that owns the setting. The morning report records "Speed limits opened the separate dialog" as a confirmed defect. The uncommitted Finding.cs already sends search to Settings › Transfers. The Torrent menu still opens the dialog, so one operation has two implementations. |

## Ranked changes

### 1. Settings has one owner and saves when the user leaves

This is proposal 1 with the Limits dialog deleted, plus the parent session's
finding about leaving the page, plus #112.

- **Speed limits.** Delete the Limits dialog. The `Limits` command opens
  Settings at `(Transfers, "download_limit")`, the same target that the search
  result and the Scheduler button use.
- **Field kind and section.** Each `Preference` gets two constructor arguments:
  a kind enum in `Models/Enums.cs`, and its `PreferenceSection`. The kind owns
  the parse, the range check and the KiB conversion. It replaces `IsRate`, the
  name tests in `TryValue` and `Message`, the branch in `Label` that chooses a
  resource group, and `SaveLimits`. The section replaces the name switch in
  Finding.cs. Do not add a class for each kind.
- **Theme.** Theme becomes a `Preference`. The theme button in the title bar
  and the Settings list save through that field. `Root.RequestedTheme` reads
  its confirmed value.
- **Other readers.** `MainViewModel.ShowAdd` reads `Preferences.ShowAdd`.
  `Draft.UseDefault` reads the confirmed destination. AddDraft's "never show"
  goes through the `ShowAdd` field.
- **Leaving with typed input.** Today the user types "500" in a limit, clicks
  Back, and gets "Discard unfinished changes?" with only Discard and Keep
  editing. The cause is `OnFieldDeparture`: it saves only when focus moves to
  another control inside the form. interface.md says Settings has "no
  page-wide Save step or confirmation on close". So:
  - Leaving the page, or closing the window, first saves every valid field
    draft.
  - The prompt remains only for invalid input and for the open period editor.
  - Constraint found in the code: `Navigate` returns false while
    `Preferences.IsPending` (Workspace.cs). If a save starts when focus leaves
    the field, the Back click would be lost. `Navigate` must wait for the save,
    not refuse.
- **A save never discards newer input (#112).** `Preference.Accept` sets
  `_input = _confirmedInput` in every case, so text typed during a save is
  lost. Keep the newer input. For a choice, such as the theme list, apply the
  latest choice when the save returns.
- **Number fields.** Replace the NumberBoxes with TextBoxes that use
  `UpdateSourceTrigger=PropertyChanged`, as the Destination field already
  does. This deletes `Helpers/TextEditor.cs`, `OnNumberLoaded`, `_editors`,
  and the NumberBox focus special case that the uncommitted work added. The
  departure and Enter handlers then read `field.Input` directly. The spin
  buttons are already hidden, so the only loss is Up/Down keyboard stepping. No
  contract asks for it.
- **Lists.** The Languages, Theme and Interfaces lists bind their selection
  TwoWay to the model. The `_refreshing` sync in `OnModel` and `RefreshText`
  goes away. The list of network adapters moves to `Preferences`. It is not
  verified whether `SelectedValuePath="Tag"` works on literal `ComboBoxItem`s;
  a list of choice objects avoids the question.

**Deleted:**

| What | Lines |
| --- | --- |
| `SpeedLimits.cs` | 64 |
| `ShowLimits` | 54 |
| Limits block in `Chrome.RefreshText` | 9 |
| `SaveLimits` | 15 |
| `SelectTheme` and `ChangeTheme` | about 17 |
| `_settings`, `Setting`, `Limit` | 6 |
| Section switch in Finding.cs | 8 |
| Limits entries in `CloseWindow`, `HasDialog`, `RefreshDialogs`, `CanClose`, `HasDraft`, `CancelDraft`, `Refresh` and `Publish` | about 10 |
| `SwitchLanguage`, `_settingsPending`, `CanSelectTheme`, the forwarding `Theme` | about 10 |
| The TextEditor workaround | about 35 |
| The `_refreshing` sync | about 25 |
| Unused `chrome.english` and `chrome.spanish` keys | 2 in each catalogue |

**Total:** about 260 lines deleted, about 40 added.

**Issues:** closes #112, and removes the coupling between the theme save and
the language list. Nothing live remains in #25: the title bar has no language
button any more, and Settings names each language by its own name. #25 can be
closed as obsolete. The new `CaptureSearch.cs` journey already describes the
expected result. The Limits journey in Capture.cs changes.

**Owner decision:** confirm deleting the dialog. interface.md has two
sentences about it: the "speed-limit draft", and Restart being reachable inside
it.

### 2. Bind translated text in XAML instead of setting it in code-behind

- **Today:** about 250 lines of `RefreshText` set text on named controls by
  hand, in Chrome, InspectorForm, PreferencesForm with four `Label` overloads,
  Scheduler, AddForm, FileForm and FileBrowser. `RefreshMenus` rebuilds every
  menu on each call. The `TextChanged` events on `MainViewModel`, `Preferences`
  and `Inspector` exist for this code.
- **Fix:** `Text="{x:Bind Model.Text.Get('group','key'), Mode=OneWay}"`, and
  the same for `AutomationProperties.Name` and, for #52, `AccessKey`.
- **Verified:**
  - Microsoft's "Functions in x:Bind" page allows constant strings in quotes as
    function arguments, and its example uses single quotes.
  - `Strings.Get` has exactly one overload with two arguments.
  - In the generated `Scheduler.g.cs`, `PropertyChanged(string.Empty)` updates
    every segment of the path, and intermediate segments update their children
    without comparing with the old value. So `Get(...)` should run again when
    `Publish` raises `""`, even though `Model.Text` keeps the same identity.
- **Not verified:** that the compiler accepts an instance method on
  `Model.Text` with literal arguments, and that the text changes live at
  runtime. Convert FileForm (11 lines) first and compile once.
- **Also gained:** open dialogs pick up a new language through their bindings.
  [Localisation](../../localisation.md) requires this, and today it costs one
  block for each dialog in `Chrome.RefreshText`. Table column headers may still
  need code; binding TableView's `DisplayName` is not verified.
- **Deleted:** about 200 C# lines and many `x:Name`s. The XAML grows only by
  attributes on existing lines.
- **Issues:** makes #52 (access keys), #22 and #26 one attribute each.

### 3. One dialog lifetime in MainWindow (#115)

- **Shape:** private members only: one `ContentDialog?` slot, one completion
  task, and one show path that sets the theme and flow direction.
- **Reopen rule:** decide what to reopen from the drafts, not from remembered
  flags. `Files.HasDraft` means show Files, and an unfinished Add means show
  Add. Write this once. Move `IsAddOpen` to `AddDraft`, which uses it for
  polling and for the `AcceptMagnet` check. The view still sets it.
- **Discard prompt:** stop calling the window-wide `RefreshText()` to fill its
  text. Today `ConfirmDiscard`, `ShowAdd` and `ShowFiles` each call it. That
  rebuilds every menu and calls `Torrents.RefreshView()` only to fill one
  dialog. Neither architect reported this.
- **Recovery gap (verified):** `keepDraft` is set only after
  `await Model.CancelClose()`. If that call throws, or if `Model.Close(true)`
  throws after Discard, the editor stays hidden while the window stays open.
- **Deleted:** four field and completion pairs, three copies of the "reopen
  Add" `finally`, `wasOpen`, `hadLimits`, `hadFiles`, the four-way hide and
  await in `CloseWindow`, and the `RefreshDialogs` list: about 50 lines. About
  15 lines are added, and about 30 references in the capture files change.
- **Issues:** closes #115.

### 4. Connection phase and two message bars (#20)

- **Model:** one enum in `Models/Enums.cs`: Connecting, Disconnected, Loading,
  StorageFailed, Stopping, Ready. The reason and the startup error stay as
  data. `_closing`, `_closed` and `_picking` stay separate facts.
  - `CanEdit`, `IsLoading`, `Rates`, `CanRestart`, and the connection message
    with its severity become switches on the phase.
  - "Ready" is set where the engine snapshot is read. The phase includes
    "Stopping" only so that `CanEdit` is derived instead of stored.
- **View:** two bars. A connection bar follows the phase; Connecting is not a
  warning. A separate failure bar shows `_error`, and the user can close it.
- **Dead fallbacks:** the engine always sends `loading`, `storage_failed`,
  `startup_error`, `missing_interface` and `alternative_limits`
  (`Engine::State::Snapshot`, Session.cpp). Replace the `TryGetProperty`
  fallbacks in `Apply` with `GetProperty`.
- **Deleted:** `_connected`, `_loading`, `_storageFailed`, `_writable`, the
  priority decoder in `Message`, and the mixed text in `AddDraft.Message`.
  About 30 lines deleted and about 20 added.
- **Issues:** closes #20.
- **Clearing a failure:** keep today's rule that a later success clears the
  failure. It covers the retry case. This is a UX default, not an open
  decision.

### 5. Raise only the change notifications that something needs (#114)

- **Typing:** the setter of `Preference.Input` calls `owner.Refresh()` on every
  keystroke. That raises `""` on `Preferences` and refreshes every field and
  every period. `MainViewModel.OnTaskChanged` runs, and Week redraws. Raise only
  `HasDraft` and `IsPending` instead.
- **Snapshots:** each snapshot refreshes `Preferences` twice, once in
  `Preferences.Apply` and once in `MainViewModel.Refresh`. Remove the second
  call.
- **Uncommitted workarounds:** after the two fixes above, delete
  `Preferences.RefreshDraft` and the `HasDraft` name skip in `Week.OnModel`.
  Make the `PeriodDraft.Start` and `End` setters use the same narrow path as
  `SetSpan`, so the draft has one way to change.
- **Command lists:** `MainViewModel.Refresh` lists 30 commands by hand, and
  `Preferences` lists 6. None is missing today. Create each command through one
  private method that also adds it to the list, so each command is listed once.
- **Size:** about 20 lines deleted and about 10 added.
- **Issues:** closes #114, together with the gate for hidden controls that #114
  asks for.

### 6. Move the weekly schedule out of Preferences.cs

- **What:** about 330 of the 634 lines are a second owner. The schedule saves
  as one list and has its own pending flag, error, draft and commands.
- **Fix:** move it to its own class. The name `Schedule` is already used by
  `schedule_enabled`, so run the [naming](../../naming.md) review.
- **Constraint:** this is a pure move, so it waits until the other
  contributor's schedule work is committed. When that work has landed, do this
  immediately before change 1, so that change 1 edits a file of about 300
  lines.

### 7. Small rules written more than once

- **Error text:** the rule exists four times: `MainViewModel.FormatError`,
  `App.cs`, `AddSource.Description` and `Preference.Message`. Put it once on
  `Strings`. About 6 lines deleted.
- **"Torrent in error":** three copies. `ErrorCount` and `TorrentError` test
  `ErrorCode.Length > 0`; the Errors filter also accepts `IsError`. Define the
  rule once on `Torrent`. This touches #29, which shows the Errors toggle only
  while errors exist.
- **Shortcuts:** they are listed in `AddShortcut` and again as
  `KeyboardAcceleratorTextOverride` strings. "Delete" and "Shift+Delete" are
  hard-coded English, so they show in English in the Spanish interface
  (MainWindow/Actions.cs). Derive the menu text from the one list. This touches
  #26.
- **Capture modes:** the mode names are listed in `IsCaptureReview` and
  checked again inside `CaptureReview`. The uncommitted diff edited both places
  twice, for "search" and for "library". Parse the mode once into an enum the
  next time the capture code changes.

### 8. Pooled elements in the uncommitted Week.cs

- **What:** `GetBorder` reuses Borders. Outlines, handles and the keyboard
  cursor share one `Outlines` canvas. Each use resets Width, Height,
  BorderThickness and CornerRadius today, but nothing enforces it.
- **Decision:** keep the reuse. `Draw` runs on every pointer move during a
  drag, and #114 records sluggishness that the user reported. Give each kind of
  element its own canvas, so reuse cannot leak a property from one kind into
  another. The performance gain itself is not measured.
- **Constraint:** this is the other contributor's live work. Raise it with
  them; do not edit it.

### Not touched by any change

- **#113** (bulk projection): nothing here addresses it.
- **#117 and #118:** engine work, outside `app/src`.
- **#116** (selection): already specified. Do it after change 3. A's claim that
  the prune in `Apply` repeats TableView's own prune is only partly right. The
  table prunes when `Project` runs at the end of `Refresh`. Deleting the
  `_selected` prune is safe only if the table raises `SelectionChanged`
  synchronously, and that is not verified. Leave it inside #116.

## Decisions that belong to the owner

1. **Delete the Limits dialog.** This also means editing the two interface.md
   sentences about it.
2. **Wording of the discard prompt.** interface.md says to ask
   "Save/Discard/Cancel" when leaving would lose input. The prompt offers only
   Discard and Keep editing. After change 1, it appears only for invalid
   Settings input and for explicit editors (period, trackers, Add, Files). A
   Save button in the prompt would have to call each editor's submit and its
   error path. The recommendation is to keep two buttons and correct the
   contract sentence.
3. **Whether the capture journeys ship in the Release executable.** Nothing
   excludes them from the build today. The owner's candidate build is Release,
   so excluding them changes which binary the captures test. Also decide
   whether to delete journeys whose findings are already recorded. handover.md
   says the capture should "stay a small verification aid".

## Proposals rejected

| Proposal | Reason |
| --- | --- |
| 3: shared draft interface | About 25 lines added to remove about 3. The four discard sites have different scopes. Use the same names (`HasDraft`, `CancelDraft`) when each owner next changes. |
| 4: typed pipe messages | #12 decided this. A missing key would become a silent default instead of today's `GetProperty` failure. Only the error-text rule is kept (change 7). |
| 5: source intake into AddDraft | #13 settled `ReceiveSources` in `MainViewModel`. The picker filter is a picker setting. Only the `IsAddOpen` move is kept (change 3). |
| A-F: regroup the `MainViewModel` partial files | Moves lines without changing any owner. The partial files share all state. |
| The first review's "class for each field kind" | Five types to replace about three switch arms. One enum does it (change 1). |
| B-E: move `OpenRequested` and `CopyRequested` into the view model | Saves about 6 lines and fixes no failure. Architecture puts Explorer and clipboard in WinUI. |
| B-F: one DataTemplate for each field kind in PreferencesForm | Saves about 40 attribute lines, but each AutomationId then needs a new source. #78 limited the related layout work to alignment. Not worth a new mechanism. |
| A separate "command registry" | Only the one private create-and-list method in change 5. No named registry type. |
