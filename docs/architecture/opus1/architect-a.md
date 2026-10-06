# Review A: ownership, state and data flow

Review of [the first proposals](review.md), 2026-10-06. This reviewer checked
who owns each fact and decision, whether state is explicit, and whether each
rule has one implementation. Sources read: the review, the project contracts,
the app code, the uncommitted work, the open issues, and the closed issues they
reference (#11, #12, #13, #24, #84, #90, #92, #105, #108).

| # | Proposal | Verdict |
| --- | --- | --- |
| 1 | Settings module | Keep, and go further: delete the Limits dialog. |
| 2 | Dialog slot | Keep. Use a few private members, not a new type. |
| 3 | Shared draft contract | Downgrade to renames only. |
| 4 | Typed pipe messages | Reject. #12 already decided this. Keep only the error-text fix. |
| 5 | Source intake into AddDraft | Reject. Keep only one small move. |

The review missed some problems. The most important is the window's connection
and message state, which is directly behind open issue #20.

## 1. The five proposals

### Proposal 1: Preferences owns settings. Go further than the review.

**Evidence that the problem is real:**

- The four rate names are written three times: `SpeedLimits.Choices`,
  `Preference.IsRate`, and the field-to-section `switch` in Finding.cs.
- The KiB conversion is in three places: `MainViewModel.Limit` divides by 1024,
  `SaveLimits` multiplies by 1024 and checks the range, and `Preference.Confirm`
  divides by 1024.
- The number parse is in two places: `SpeedLimits.Apply` and
  `Preferences.TryValue`.
- `MainViewModel` keeps raw settings JSON in `_settings`. `Setting("show_add")`
  reads it, and `Apply` reads `theme`, `language` and `default_destination`
  directly. `Preferences` also holds `ShowAdd` and `Destination`, so the same
  facts have two readers.
- `SelectTheme` and `ChangeLanguage` send `settings` themselves and bypass
  `SaveSettings`. They share one `_settingsPending` flag. Because of that flag,
  a theme save disables the language command, and `Apply` ignores the engine's
  language while a theme save is in flight.

**Evidence the review missed:**

- The uncommitted `docs/morning-report.md` lists a release defect that the
  owner found: "a Settings-related result that opens a separate dialog".
- The new `MainWindow/CaptureSearch.cs` journey treats Ctrl+K "Speed limits"
  as failed unless it lands on Settings › Transfers with no Limits dialog open.
- `Scheduler.SpeedRequested` already navigates to `(Transfers,
  "download_limit")`.

**Recommended change:**

1. Delete the Limits dialog. The `Limits` command opens Settings at
   `(Transfers, "download_limit")`, the same target the Scheduler button uses.
2. The review's open design point then disappears. That point was: the dialog
   saves four values together, but the page saves one field at a time.
3. `docs/interface.md` still describes a speed-limit draft and a menu entry.
   Those lines need the same edit. Because that text still stands, confirm the
   deletion with the owner.

**What `Preferences` owns afterwards:**

- Every confirmed setting, including `theme`. Theme becomes an ordinary field.
  #24 already decided that a theme change may wait for the save.
- A kind for each field, as an enum in `Models/Enums.cs`: text, path, toggle,
  rate, count, port or ratio. The constructor receives it. The kind replaces
  `IsRate`, the name checks in `TryValue` and `Message`, the choice of resource
  group in `Label`, and the range check and ×1024 in `SaveLimits`.
- A section for each field, which replaces the switch in Finding.cs.

**What callers do afterwards:**

- `MainViewModel.ShowAdd` reads `Preferences.ShowAdd.IsOn`.
- The window's theme reads the theme field in `Preferences`.
- `Draft.UseDefault` gets its value from `Preferences.Destination`.
- AddDraft's "never show" goes through the `ShowAdd` field, not through a raw
  `SaveSettings(new { show_add = false })`.
- Language keeps its own path, because
  [localisation](../../localisation.md) requires the catalogue to be published
  before the save. With theme gone, `_settingsPending` always equals
  `_changingLanguage`, so one of them is deleted.

**What gets deleted:**

- `SpeedLimits.cs` with `LimitChoice`
- `MainViewModel.Speed`, `Limit`, `SaveLimits`, `_settings`, `Setting` and
  `SelectTheme`
- `ShowLimits` (about 55 lines), `_limitsDialog` and `_limitsClosed`
- the limits block in `RefreshText`
- the limits entries in `HasDialog`, `RefreshDialogs`, `CloseWindow`,
  `CanClose`, `HasDraft`, `CancelDraft`, `Refresh` and `Publish`

**Risks:**

- The uncommitted edits to `Views/Preferences.cs` touch the same file. Build on
  top of them, or wait until they are committed.
- The capture journeys for the Limits dialog in Capture.cs must change too.
- If the owner keeps the dialog, the dialog binds to the four rate
  `Preference` fields. Apply commits them in one `Preferences` call, and Cancel
  calls `Cancel()` on those four.

### Proposal 2: one dialog slot. Keep it.

**Evidence:**

- The rule "reopen Add if the draft is unfinished" is copied in
  `ConfirmRemove`, `ShowFiles` and `ShowLimits` (MainWindow/Actions.cs) and in
  `CloseWindow` (MainWindow.cs).
- The dialogs are listed by hand in `HasDialog`, `RefreshDialogs`,
  `RefreshText` and `CloseWindow`. `CloseWindow` uses four hide calls, four
  awaits and three "had dialog" flags.

**Shape.** There is no new class. `MainWindow` gets:

- one `ContentDialog? _dialog`
- one `Task` that completes when the dialog closes
- one stored action that refreshes the dialog's text
- one `ShowDialog(dialog, refreshText)` method

`ShowDialog` sets the theme and flow direction, awaits `ShowAsync`, and clears
the slot. Then, unless the window is closing, it reopens Add when the draft is
unfinished. That rule is written once.

- The discard prompt can use the same slot, because it never shows over
  another dialog.
- `CloseWindow` hides and awaits only the slot. It remembers which dialog to
  reopen if the user keeps their draft.
- After proposal 1, the slot holds Add, Remove, Files and the discard prompt.
- Dialog lifetime stays in the view, as
  [architecture](../../architecture.md) requires.

**Cost:** about 25 references in Capture.cs and CaptureFiles.cs use the named
dialog fields. The existing journey "Keep input did not recover the Add form"
already checks the riskiest path, so no new test is needed.

### Proposal 3: shared draft contract. Downgrade to renames.

**The problem is real:**

- `MainViewModel` lists its child owners by hand in six members: `CanClose`,
  `HasDraft`, `CancelDraft`, `Refresh`, `Publish`, and the subscriptions in the
  constructor.
- The same idea has different names: `HasChanges` and `HasDraft`; `Cancel`,
  `CancelDraft` and `Begin`.

**Why an interface does not pay:**

- After proposal 1 there are four owners.
- An interface removes only two short `Any(...)` lists. `Refresh` and
  `Publish` call different members on each owner, so they stay as they are.
- The four discard checks in the view have different scopes: the inspector
  only, Settings only, or everything. An interface does not merge them.

**Recommendation:**

- Use one name from [the product vocabulary](../../../CONTEXT.md), "Draft",
  in whichever change touches each owner. For example, `FileOperation.Cancel`
  and `AddDraft.Cancel` become `CancelDraft`.
- `SpeedLimits.Begin`, which is used as a cancel, is deleted by proposal 1.

### Proposal 4: typed pipe messages. Reject.

**Prior decision:** #12 proposed exactly this and was closed on 2026-10-05 as
"not pursued". A renamed reply key fails the first time its screen runs, and
the engine side (#92) already declares the keys once.

**Two of the review's points are also wrong:**

- **"The compiler catches renamed fields."** A C# record catches a rename on
  the C# side only. A key renamed in the engine still compiles. With
  System.Text.Json, a missing key becomes a silent default unless every member
  is marked `required`. Today `GetProperty` fails loudly.
- **"The transport stops knowing product names."** `PipeClient` knows
  `"snapshot"` because [the protocol contract](../../protocol.md) gives the
  once-a-second refresh, with at most one refresh in flight, to the client's
  request queue. That is the documented owner, not a leak.

**Keep only the error-text rule.** It is a duplicate rule, and #12 did not
decide it.

- The owner is `MainViewModel.FormatError`.
- Copies are in `App.cs`, `AddSource.Description` (AddDraft.cs) and
  `Preference.Message` (Preferences.cs).
- Move it to `Strings`, which already owns `Error(code, detail)`. Make every
  caller use it, and delete `FormatError`.

**New point that supports #12's reasoning:** `MainViewModel.Apply` uses
`TryGetProperty` with fallbacks for `loading`, `storage_failed`,
`startup_error`, `missing_interface` and `alternative_limits`. The engine always
sends all five (`Engine::State::Snapshot`, Session.cpp). So the fallbacks are
dead code. They also work against #12's premise that a renamed key fails loudly.
Use `GetProperty`, and delete the `Setting("alternative_limits", false)`
fallback.

### Proposal 5: source intake into AddDraft. Reject.

**Why:**

- `ReceiveSources` is the handoff that decides whether a closing window still
  accepts sources. #13 settled that it belongs in `MainViewModel`. It depends
  on `_closing`, `_ready` and `_connected`, and runtime checks verified it.
  Moving it into `AddDraft` splits the single owner that #13 asked for.
- The native picker's `.torrent` filter must stay in the view.
- The drop filter and the line split are two lines.

**Keep one small move:** move `IsAddOpen` from `MainViewModel` to `AddDraft`.
Its real users are AddDraft's polling start and stop, and the `AcceptMagnet`
check that releases a stale preview. Do it while building the dialog slot.

## 2. Problems the review missed

### A. Connection state is five flags, and the message mixes two facts

This is the cause of #20.

- `_connected`, `_loading`, `_storageFailed`, `_ready` and `_writable` describe
  states that cannot be true together, but they are stored as five booleans.
- `_writable` is a stored copy of a derived value. AGENTS.md says to store
  facts and decisions, and to derive the rest.
- The `Message` getter decodes these flags by priority. When the window is
  disconnected, it appends the old command error as the detail. `Severity` is
  computed separately.
- Two other places show this mixed text: `AddDraft.Message` shows
  `_owner.Message` while disconnected, and the Limits dialog binds
  `MainViewModel.Message`.

**Fix:**

- Use one enum for the connection phase: Connecting, Loading, StorageFailed,
  Ready, Stopping, Disconnected. Keep the reason and the startup error as data.
- `CanEdit`, `IsLoading`, the "first show" decision and the connection message
  then become switches on the phase. A phase change is announced in one place.
- Keep the command failure (`_error`) as a separate fact, with its own bar that
  the user can close, and its own severity.
- This follows #13's rule: an explicit phase where states cannot occur
  together, and independent facts kept apart. So `_closing`, `_closed` and
  `_picking` stay outside the enum.
- This covers two items of #20: "connection state and command failures share
  one bar" and "connection changes are not announced". Making the bar closable
  then becomes a XAML change. Whether a later success should clear an unrelated
  failure is a product decision.

### B. The hand-kept command list and the refresh of everything

- `MainViewModel.Refresh` names 30 commands by hand. A command left out keeps a
  stale enabled state. `Exit` and `OpenUpdate` are also refreshed separately,
  in `OnTaskChanged` and `CheckUpdates`.
- **Fix:** create commands through one private method that also adds each one
  to a list, and refresh that list. Do the same in `Preferences` and
  `Inspector`. Commands that belong to one item (`SchedulePeriod`,
  `AddSource`) keep their own refresh.
- The refresh has a measurable side effect. Each snapshot raises the
  Preferences "everything changed" notification twice: once in
  `Preferences.Apply` and once through `MainViewModel.Refresh`. `Week.OnModel`
  redraws on each one, so Week redraws twice a second while it is loaded.
- The uncommitted work already works around this (see G).

### C. Selection is held three times, and one rule is written twice

- The copies are `Torrents.Selection` in the table, `MainWindow._selection`
  (Workspace.cs, used only to undo a refused change), and
  `MainViewModel._selected` and `_current`.
- The TableView contract says that the table removes deleted rows from the
  selection and raises `SelectionChanged`. So the code in `Apply` that removes
  them from `_selected` and `_current` is a second copy of that rule. Delete
  it.
- Undo a refused selection change from the model's selection, and delete
  `_selection`.
- `_current` exists only for `TorrentError`. #29's design moves the error
  reason into the inspector's General view, which deletes `_current`,
  `TorrentError` and `HasTorrentError`.

### D. The capture harness is compiled into the product window

- Capture.cs (856 lines), CaptureFiles.cs (225) and the new CaptureSearch.cs
  (169) total about 1,250 lines. That is about 15% of `app/src`, inside the
  partial class of `MainWindow`.
- It reads private fields directly, so every refactor of `MainWindow` or
  `MainViewModel` must also rewrite it.
- The capture mode is a set of ten values. It is listed by hand in
  `IsCaptureReview` and read again in nine places in `CaptureReview`. The
  uncommitted change had to add `"search"` in two of those places. Parse the
  mode once into an enum.
- Whether this code ships in the Release executable is the owner's decision,
  because the owner's current candidate build is Release.

### E. "Torrent in error" is defined twice

- `ErrorCount` (MainViewModel/Actions.cs) counts `ErrorCode.Length > 0`.
- The Errors filter (`Matches(Errors)` in Finding.cs) also accepts `IsError`.
- The two agree today only because the engine reports status "error" only when
  it also sends an error code (`Torrent::Classify` and `Diagnose`).
- Define the rule once on `Torrent`. #29's "show the Errors toggle only while
  errors exist" needs that rule.

### F. The partial files of MainViewModel are split by history

- The class is about 1,140 lines in four files that share all private state.
- Finding.cs holds `Page` and the About text. Actions.cs holds `_settings`,
  source intake, the queue commands, and Open and Copy.
- Every command is built in the main file's constructor, but the commands are
  declared in other files.
- After proposal 1, A and B remove code, regroup the files so that each file
  declares the state it uses: lifetime and connection; commands with command
  search; the torrent list with projection and filters; source intake. Do not
  extract new classes unless one removes coupling between files.

### G. The uncommitted work

- **Two update paths for one draft value.** `PeriodDraft.SetSpan` uses narrow
  notifications plus the new `Preferences.RefreshDraft`. The `Start` and `End`
  setters still call the full `Refresh` and `_owner.Refresh()`. `Week.OnModel`
  now skips `HasDraft` by name. These are workarounds for B. Make the setters
  use the same narrow path, so the draft has one way to change.
- **Pooled borders must reset every property.** `Week.Draw` reuses one pooled
  `Outlines` canvas for outlines, handles and the keyboard cursor. Each use
  must reset every property that any other use sets: Width, Height,
  BorderThickness, CornerRadius and Style. Today each use does, but nothing
  enforces it. A property that one use sets later leaks into the others. Use
  one pool for each kind of element.
- **`CaptureSearch` is the specification for proposal 1.** Do proposal 1 so
  that this journey passes.

### H. Open issues

- **#20:** fixed by A and by moving the error-text rule (proposal 4).
- **#25:** proposal 1 removes the coupling between the theme save and the
  language command. The rest of #25 is presentation.
- **#29:** E and C. Moving the reason into the inspector deletes state from
  `MainViewModel`.
- **#22:** none of the five proposals helps. A destination refusal should
  follow AddDraft's existing `_magnetFailure` pattern: one failure for each
  field, and the view focuses the field that failed. Today `OnSubmit` can only
  call `FocusMagnetError`.

## 3. Recommended order

1. **Proposal 1, with the Limits dialog deleted.** The owner found it as an
   open release defect, the new CaptureSearch journey already expects the
   result, and it deletes the most code. It also makes proposals 2 and 3
   smaller.
2. **A, plus one owner for the error-text rule.** This fixes the structure
   behind #20 and changes the core of `MainViewModel` only once.
3. **Proposal 2, with `IsAddOpen` moved to `AddDraft`.** After step 1 it covers
   three dialogs and the discard prompt, and it simplifies `CloseWindow`.
   Budget for the changes to the capture harness.
4. **B, C and E.** Small, mechanical changes. E supports #29.
5. **F, regrouping the partial files.** Do it last, after steps 1–4 have
   removed code.

Proposals 3, 4 and 5 drop out, except for the renames, the error-text fix and
the `IsAddOpen` move.

None of these changes needs a new test project. The existing capture journeys
already check what the user sees after steps 1 and 3: Ctrl+K navigation to the
speed limits, and keeping a draft while the window closes. The design keeps all
three project constraints: one concrete `PipeClient`, no interface added for
tests, and dialogs and pickers stay in the view.
