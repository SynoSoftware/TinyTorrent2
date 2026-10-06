# Review B: code volume and WinUI idiom

Review of [the first proposals](review.md), 2026-10-06. This reviewer checked
how much code does work that WinUI, XAML binding or plain C# already does, what
an experienced WinUI engineer would delete, and where a proposal would add
types or indirection instead of removing code. Sources read: the review, the
project contracts, closed issues #11, #12, #13, #84, #90, #92 and #105, and all
of the app code except `Placement`, `Updates` and the model files. Nothing was
edited or built.

The working tree changed during the review: Finding.cs, MainWindow.cs,
Workspace.cs and PreferencesForm.xaml.cs became modified. The notes on the
uncommitted work describe that later state.

**Verdict:** keep proposal 1, but delete the Limits dialog instead of only
`SpeedLimits`. Keep proposal 2 as a small follow-up. Reject proposals 3, 4 and
5.

The largest problem the review missed: about 250 lines of code-behind set
translated text on named controls by hand. XAML binding already does this job.

## 1. The five proposals

### Proposal 1: Preferences owns settings. The problem is real; change the fix.

**Verified:**

- The four rate names appear in `SpeedLimits.Choices`, in the `Preferences`
  constructor, in `Preference.IsRate`, and in Finding.cs.
- The ÷1024 conversion is in `MainViewModel.Limit` and `Preference.Confirm`.
  The ×1024 conversion is in `SaveLimits`.
- The same number parse is in `SpeedLimits.Apply` and `Preferences.TryValue`.
- `MainViewModel._settings` exists only to feed `Setting` and `Limit`.

**Where the review overstates it:** rates already have one write path.
`Preferences.Submit` calls `MainViewModel.SaveLimits`, the same method the
dialog uses. Moving theme and language into `Preferences` would conflict with a
written requirement: [localisation](../../localisation.md) requires the
language to be published before it is saved, and rapid changes to be merged.
`ChangeLanguage` does that. Leave both out.

**Change the fix: delete the Limits dialog instead of binding it to the four
fields.** The evidence:

- morning-report.md records that the owner reported "a Settings-related result
  that opens a separate dialog" as a defect.
- The uncommitted work already sends the search result to Settings › Transfers
  (Finding.cs).
- CaptureSearch.cs treats anything else as a failure.

The Torrent menu's Limits item still opens the dialog, so one operation now has
two implementations. AGENTS.md forbids that. If the `Limits` command calls
`RequestPreferences(new(Transfers, "download_limit"))`, both paths are the same.
This also removes the review's open design point: four values saved together
versus one value at a time.

**What gets deleted:**

- SpeedLimits.cs (64 lines) with `LimitChoice`
- `ShowLimits` (54 lines), `_limitsDialog` and `_limitsClosed`
- the limits block in `Chrome.RefreshText` (9 lines)
- the limits branches in `CloseWindow`
- `Speed` everywhere in `MainViewModel`, and `LimitsRequested`
- `_settings`, `Setting` and `Limit`; `ShowAdd` reads `Preferences.ShowAdd.IsOn`
- `SaveLimits` (15 lines); the KiB rule moves into `Preference`
- the section switch in Finding.cs (8 lines)
- the Limits dialog journey in Capture.cs and the dialog branch in
  CaptureSearch.cs

**What gets added:** each `Preference` gets two constructor arguments: a kind
enum in `Models/Enums.cs`, and its `PreferenceSection`. They replace every check
on the field name: `IsRate`, the name tests in `TryValue` and `Message`, and
the section switch. A class for each kind, as the review proposed, would add
five types to replace about three switch arms.

**Estimate:** about 170 lines removed, 15 added.

**Needs the owner:** [interface.md](../../interface.md#committing-edits)
names a "speed-limit draft" modal. That sentence changes.

**Tests:** none. A wrong conversion factor shows in the field right after a
save. There is no app test project, and [testing](../../testing.md) rules out a
test that watches for no named failure that nothing else catches.

### Proposal 2: one dialog slot. Real but modest; do it after proposal 1.

**Verified:**

- The rule "reopen Add if it is unfinished" is copied in three `finally`
  blocks in MainWindow/Actions.cs, and a variant is in `CloseWindow`.
- `CloseWindow` hides and awaits four named dialogs and keeps three flags to
  reopen one.

After proposal 1 there are three dialogs plus the close prompt.

**The fix:**

- One field holds the open dialog, and one completion is set when it closes.
- One `Show(ContentDialog)` path sets the theme and flow direction and
  completes the slot.
- The reopen decision comes from the drafts, not from remembered flags:
  `Files.HasDraft` means show Files, and an unfinished Add means show Add. Put
  the "Add is unfinished" check on `AddDraft`, not in the view.

**Estimate:** about 25 lines removed, 12 added. Most of the gain is in
`CloseWindow`, which is 80 lines today.

**Limits:** the text block for each dialog in `RefreshText` stays, unless the
text is bound as in item A below. `RefreshDialogs` stays: it sets each dialog's
theme by hand, because a `ContentDialog` does not take the window's theme.

### Proposal 3: one contract for unfinished input. Reject.

- Each of the four confirm-discard sites in the view concerns one owner:
  `OnInspectorClose` and `SelectTorrent` (the inspector), `Navigate`
  (Preferences), and `CloseWindow` (all owners).
- The only site that covers all owners already uses `Model.HasDraft` and
  `Model.CancelDraft`.
- In `MainViewModel`, `CanClose`, `HasDraft` and `CancelDraft` are each one
  expression.
- An interface with five implementations and a list would add about 25 lines
  to replace 3.
- [Naming](../../naming.md) allows an interface only when a caller chooses
  between real implementations.
- Closed issue #13 already settled close ownership: `CanClose` must be derived
  from the owners' own work, and it is.

**Cheap alternative:** use consistent names (`HasChanges` or `HasDraft`,
`Cancel` or `CancelDraft`). Proposal 1 removes the odd one, `Speed.Begin`.

### Proposal 4: typed pipe messages. Reject.

- Closed issue #12 decided "not pursued" on 2026-10-05. No new evidence
  reopens it, and the evidence found supports the decision:
  - Readers mostly read different messages. `save_path` and `folder` appear in
    `Torrent.Update`, `FileOperation.ReadLocations` and
    `Actions.OpenTorrent`, each from a different reply.
  - With default System.Text.Json options, a typed record gets a default value
    when a key is missing. `GetProperty` throws, so a renamed key would fail
    more quietly than it does today.
  - Records for about a dozen replies would add 100–150 lines.
- **Keep one part:** the rule that turns an exception into error text exists
  four times: `App.cs`, `MainViewModel.FormatError`, `AddSource.Description`
  and `Preference.Message`. Make one method on `Strings` or `CommandFailure`
  and use it everywhere. No new type is needed.

### Proposal 5: source intake into AddDraft. Reject.

- The picker's `.torrent` filter is a setting of the native picker
  (`FileTypeFilter`), not a second copy of a rule. The only real filter is on
  the drop path.
- `IsAddOpen` is the dialog's lifetime, and
  [architecture](../../architecture.md#command-and-presentation-flow) keeps
  dialog lifetime in the view.
- Closed issue #11 settled AddDraft ownership.
- At most, move the polling start and stop from the `IsAddOpen` setter to
  `AddDraft`. The line count stays about the same.

## 2. Problems the review missed

### A. Translated text is set by hand in code-behind

This is the largest item by code volume. The text-setting methods total about
250 lines:

| Method | Lines |
| --- | --- |
| `Chrome.RefreshText` | 69 |
| `InspectorForm.RefreshText` | 64 |
| `PreferencesForm.RefreshText` and four `Label` overloads | 73 |
| `Scheduler.RefreshText` | 23 |
| `AddForm.RefreshText` | 12 |
| `FileForm.RefreshText` | 11 |
| `FileBrowser.RefreshText` | about 9 |

- Many of the 242 `x:Name` attributes, and the `TextChanged` events on
  `MainViewModel`, `Preferences` and `Inspector`, exist only for this.
- Every owner already raises `PropertyChanged(string.Empty)` when a new
  language is published. A binding such as
  `Header="{x:Bind Model.Text.Get('preferences','language'), Mode=OneWay}"`
  would therefore refresh by itself.
- **Estimate:** about 200 C# lines removed. The XAML grows only by attributes
  on lines that already exist.
- **Not verified:** nothing was built. One compile of FileForm (11 lines) would
  prove that x:Bind accepts string-literal arguments in this project before the
  rest is converted.
- Table column headers may still need the existing `RefreshView` call.

### B. Selection is copied between view and model by hand, with guard flags

- There are 24 `_refreshing` references in five files. Examples:
  - `PreferencesForm` syncs the Languages, Theme and Interfaces lists in
    `OnModel` and `RefreshText`.
  - `MainWindow` syncs the Filters list in `OnModelChanged` and
    `OnFilterChanged`.
  - `InspectorForm` syncs its section list and range selector.
- **Fix:** bind `SelectedValue` or `SelectedItem` TwoWay, and the guard flags
  go away.
- `PreferencesForm.RefreshInterfaces` builds the list of network adapters and
  the "unavailable interface" choice in the view. That is view-model data, so
  move it to `Preferences`.
- **Estimate:** about 40 lines removed.

### C. NumberBox is worked around instead of used

- `PreferencesForm` sets `ValidationMode="Disabled"` on its NumberBoxes and
  binds `Text`. It then reaches into the inner TextBox through
  `Helpers/TextEditor.Find` to read each keystroke. There are four callers, and
  the uncommitted work adds a fifth to set focus.
- **Fix:** use a TextBox with `UpdateSourceTrigger=PropertyChanged`, as the
  Destination field already does. The draft model does not change. This
  deletes TextEditor.cs, `OnNumberLoaded`, `_editors` and the special cases.
- **Estimate:** about 35 lines removed.
- **Cost:** the number fields lose NumberBox's Up and Down arrow stepping.

### D. Preferences.cs holds two owners

- Preferences.cs is 634 lines. About 330 of them are the weekly schedule: the
  members from `Edit` to `Time`, and `SchedulePeriod`, `PeriodDraft`,
  `DayChoice` and `ScheduleRange`.
- The schedule saves as one list, with its own pending flag, error and draft.
  [Naming](../../naming.md) allows a split at the edge of a transaction.
- **Fix:** move the schedule to its own owner file. The name `Schedule` is
  already used by the `schedule_enabled` field.
- This is a pure move. It keeps proposal 1 from colliding with the schedule
  work underway.

### E. Small items

- **Dead command:** `SwitchLanguage` is never run. Nothing binds or invokes it;
  only `CanExecute` is read. Replace it with a bool.
- **Orphan keys:** `chrome.english` and `chrome.spanish` are unused.
- **Events that do not need the view:** `OpenRequested` and `CopyRequested`
  send Explorer and clipboard actions through the view. Architecture says those
  use Windows directly.

### F. Repeated XAML in PreferencesForm

- Nine NumberBox rows and eight ToggleSwitch rows each repeat six to eight
  identical attributes.
- After proposal 1 gives each field a kind, one DataTemplate for each kind
  would remove about 40 lines. Each AutomationId then needs a source.
- Do this only together with #78 or item C, because they change the same rows.

### G. The keyboard-shortcut list exists twice

- One copy is the `AddShortcut` registrations in the `MainWindow`
  constructor. The other is the `KeyboardAcceleratorTextOverride` strings in
  `RefreshMenus` and `AddSelection`.
- The menu text "Delete" and "Shift+Delete" is hard-coded English, so it also
  shows in English in the Spanish UI.

### H. The other large files

- MainWindow.cs is large mostly because of `CloseWindow`, which proposals 1
  and 2 shrink.
- `MainViewModel` is about 1,140 lines with its partial files. No split is
  needed; proposal 1 and item E remove its dead weight.

## 3. The uncommitted work

| Change | Verdict |
| --- | --- |
| Preferences.cs and Scheduler.xaml | Good. Removes the `PreviewLabel` pass-through. `SetSpan` replaces two copies of the start/end assignment. `SetSpan` and `RefreshDraft` exist only because the normal setters call the full `Refresh()`, which raises a change for every property; acceptable. |
| Week.cs: grid split | Good. `DrawGrid` redraws only when the size, text or scale changes. |
| Week.cs: tooltip in XAML | Good. `Tip` and `HoverTip` are declared once instead of rebuilt on every draw. |
| Week.cs: element reuse | Questionable. `GetBorder` and `Trim` reuse elements. Outlines, handles and the cursor share one canvas, so every reuse must reset Width, Height, CornerRadius and BorderThickness. Nothing in the diff or the morning report measures a need for reuse. Pointer capture is on `Week` itself and the canvases are not hit-testable, so recreating the elements is safe. Ask for the measurement, or go back to clearing and creating them (about 30 lines removed). |
| Finding.cs | The search result goes to Settings, but the menu item still opens the dialog: one operation, two implementations. Proposal 1 fixes this. |
| PreferencesForm.xaml.cs | The new focus special case for NumberBox is item C. |
| Capture.cs and CaptureSearch.cs | 169 lines added. The capture mode names are listed in `IsCaptureReview` and checked again in `CaptureReview`, so a new mode needs two edits, and this change made both. One enum, parsed once, removes that. |

## 4. The capture code

- It is about 1,250 lines in three `MainWindow` partial files, about 14% of
  the app, and it ships in the product executable.
- The pixel capture (`CaptureUi`, `CaptureLayout` and `CapturePage`, about 110
  lines) must run in the process, and [testing](../../testing.md) documents
  it. That part is in the right place.
- The rest is scripted journeys that read private `MainWindow` fields and
  throw on failure. In practice it is a UI test suite.
- handover.md says the capture "should stay a small verification aid;
  extending it into another automation product would repeat the same mistake."
  It is now larger than that.
- It also makes product changes more expensive: deleting the Limits dialog
  needs edits in two capture files.
- **Recommendation:** stop growing it, and use the mode enum. Ask the owner to
  delete the journeys whose findings are already recorded. Moving it to a
  separate automation project outside the process would add a product, not
  remove one.

## 5. Open issues

- **#20 (message bar):** the issue text describes code that no longer exists
  (`ShowError`, `RefreshFeedback`). The defect remains: `MainViewModel.Message`,
  `Severity` and `HasFeedback` merge connection state and command failures into
  one bar that the user cannot close. No proposal helps. The fix is two
  view-model properties and two InfoBars.
- **#22 (Add form):** it cites `MainWindow.OnAdd`, which no longer exists; the
  form is AddForm.xaml now. Item A makes the accessible names of the Browse
  buttons one XAML attribute each.
- **#25 (language button):** mostly out of date.
  - There is no language button in the title bar, and
    [interface](../../interface.md) forbids one.
  - Settings shows each language by its own name (English, Español).
  - `ChangeLanguage` already publishes the language before saving it.
  - What remains is the dead `SwitchLanguage` command and the orphan keys
    (item E).
- **#26 (title-bar tooltips):** the "six Tab stops" point is out of date; the
  title bar now holds the menu bar, the search box and the theme button. The
  live part is item G.
- **#48 (status bar):** separate from all the proposals. Split `Rates` into two
  bound values.
- **#52 (access keys):** item A makes each access key one XAML attribute.
  Without it, each one adds a code-behind line and an `x:Name`.
- **#78 (Settings layout):** it changes the same rows as item F, so fix #78
  first or together with F.

## 6. Recommended order

1. **Let the uncommitted work land.** It touches Finding.cs, Preferences.cs,
   PreferencesForm and the capture files.
2. **Move the schedule out of Preferences.cs (item D).** It is a pure move, so
   step 3 then edits a file of about 300 lines without conflicts.
3. **Do proposal 1 in the changed form.** It fixes the defect the owner
   reported and deletes the most code that holds a rule. It needs the owner's
   agreement to change the speed-limit sentence in interface.md.
4. **Bind translated text (item A).** Start with FileForm, which also proves the
   binding with one compile. This is the largest deletion by volume, and it
   makes #52, #22 and #26 cheaper.
5. **Do proposal 2.** Once the Limits dialog is gone, it is a small change.
6. **Do the small items:** B, C, E, the single error-text rule and the capture
   mode enum.
