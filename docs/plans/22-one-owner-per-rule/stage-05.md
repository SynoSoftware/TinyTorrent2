# Stage 5 gate record: Settings

Gate done on 12 November 2026, by reading, on the working tree that holds
stages 1, 2, 3 and 16b. Nothing was built or run. This record takes the place
of [the stage's text](../22-one-owner-per-rule.md#5-settings) for the session
that implements it. Paths without a prefix are under `app/src`; line numbers
are those of that working tree, and each reference also names its member.

## Where the stage stands

Every finding has an owner, an interface and its callers. The stage adds no
file, no product text and no catalogue key. It departs from the plan in two
places, both under [Decisions the plan did not settle](#decisions-the-plan-did-not-settle):
Connection setup keeps its own layout and only asks `FieldColumns` whether its
width fits, and the change to `FieldColumns` also reaches the Inspector, whose
header and Library details read the same width.

The decisions, each with its reason below:

1. `Setting.HasRow` says whether Settings shows a setting in a row; search
   lists only those.
2. `Overview` is the one list of model changes that refresh the Settings
   summaries. The page's Home links follow `Overview`'s change notice.
3. `FieldColumns` follows the Windows text size. Speed limits, Home and About
   use it. Connection setup asks `FieldColumns.Fits`.
4. `Editor.SelectTheme` goes; the theme saves through `Setting.Select`, which
   takes over its "cannot edit" guard.
5. `Setting` has one member that writes a saved value as field text; the
   duration summary and Connection setup read `ConfirmedText`.
6. `Schedule.RangeAt` answers "which range covers this day and minute".
7. `PeriodSpan.Step` and `PeriodSpan.LastStep` hold the 15-minute step.
8. One `SettingsPage.CurrentPanel` finds the shown category's panel; the four
   category tables stay.

## Gate step 1: the findings marked "to re-read"

| Finding | Rule | Result |
| --- | --- | --- |
| `Editor.SelectTheme` (`Settings/Editor.cs` 483) against `Setting.Select` (`Shared/Setting.cs` 293) | A choice saves the value the person selected through its setting; the value the setting already shows saves nothing, and a setting that cannot be edited takes no choice. | Kept. They differ in two cases: `SelectTheme` refuses before it changes `Input` when the setting cannot be edited (`Select` sets `Input`, then `Commit` refuses and leaves a draft); and `SelectTheme` commits again when the chosen word equals a shown draft (`Select` returns). Its `Cancel` branch (488 to 492) changes nothing that the `Input` setter has not already done. |
| A saved number as input text (`Shared/Setting.cs` 157 to 162, 225 to 230; `Settings/Editor.cs` 325 to 330) | A saved number shows in its field in the field's unit, in the person's number format, and as empty text where 0 means unlimited. | Kept. Copies: `Setting.Confirm` (157 to 162), `Setting.SelectUnit` (227 to 230 for the saved value, 226 for the draft), `Editor.DurationText` (325 to 330). The gate found a fourth: `ConnectionSetup.Input` (`Settings/ConnectionSetup.cs` 779 to 784), which writes the saved capacity as the capacity field's text. |
| The category tables (`Settings/SettingsPage.xaml.cs` 135, 478, 503, 667) | The panel of the shown category is the loaded panel whose name is its navigation item's `Tag`. | Kept for the three lookups that state this rule: `ShowCategory` 427, `LoadCategory` 493 to 496, `AlignCategory` 607. **Dropped** for the four tables, because each holds a different fact. `CategoryItems` (667) maps `SettingsCategory` numbers to items and is the only copy of that mapping. `CategoryPanels` (478) lists the loaded panels: they are `x:Load="False"` fields that stay null until `FindName` loads them, so no item can reach its panel without loading every category. The icon table (135) holds each item's glyph and asset, Home included. `LoadCategory`'s chain (503) builds each category's own content. |
| "Which period is at this day and minute" (`Settings/Week.cs` 475, 682, 723) | The period at a day and minute is the period of the shown range that covers that minute. | Kept. `Week.Hit` 475 to 479, `Week.OnContextRequested` 682 to 685, `Week.OnKeyDown` 723 to 726. The gate found a fourth in the capture journey `Shell/MainWindow/Capture.cs` 1422, which reads the range's mode. |
| The 15-minute step (`Settings/PeriodSpan.cs`; `Settings/Week.cs` 133, 739) | Gestures and keys move periods and the time cursor in 15-minute steps, and the last step of a day starts at 23:45. | Kept. `PeriodSpan.Snap` 19 to 20; `PeriodSpan.Adjust` 30 (1425, 15), 32 (1425), 37 and 43 (15); `Week.Minute` 133 (1425); `Week.OnPointerPressed` 506 (a new period of one step); `Week.OnKeyDown` 736, 739, 745. No other schedule file writes the step. |

The three items the plan does not mark "to re-read" were read on both sides
too, and hold:

- **No row.** `MainViewModel.SettingSuggestions` (`Shell/MainViewModel/Finding.cs`
  396 to 404) skips `CapacityDownload`, `CapacityUpload`, `DeleteDownloaded` and
  `DeleteTorrentFile` by name, and `Editor` (70, 71) states the reason for two
  of them. Comparing the registered settings with the page confirms that
  exactly these four have no row: every other one is bound by a
  `Setting="{x:Bind Model.…}"` row, built by `SpeedLimits.AddRate`, or named in
  `SettingsPage.FindControl` (Adapter, Encryption, Language, Theme).
- **Summaries.** `Overview`'s constructor (`Settings/Overview.cs` 19 to 33) and
  `SettingsPage.OnMain` (216 to 232) list the same `MainViewModel` changes;
  `OnMain` adds `FreeSpaceStatus` and `ShowsFreeSpace`, which Home's Downloads
  link reads (`Settings/SettingsPage/Home.cs` 24 to 26). Library changes have
  two lists too: `SettingsPage.OnProviders` (237 to 244) and
  `Editor.AttachLibrary` (`Settings/Editor/Plugins.cs` 69 to 77), and the second
  refreshes `Overview` twice for one change: `Changed(nameof(VideoSummary))`
  raises `Editor.PropertyChanged`, which `Overview` already follows, and then
  `Overview.Refresh()` runs again.
- **Field columns.** `SpeedLimits.UpdateProfiles` (`Settings/SpeedLimits.xaml.cs`
  124 to 134, 640 DIPs), `SettingsPage.UpdateHomeLayout`
  (`Settings/SettingsPage/Home.cs` 179 to 191, 900), `AboutPage.UpdateColumns`
  (`Shell/AboutPage.xaml.cs` 78 to 94, 720) and `ConnectionPage.UpdateColumns`
  (`Settings/ConnectionPage.xaml.cs` 96 to 116, 880) each multiply a width by
  the text scale and switch two columns by hand. `FieldColumns`
  (`Controls/FieldColumns.cs` 61) compares an unscaled width. Two more readers
  of that width exist: `InspectorPane.ActionsOverflow`
  (`Shell/InspectorPane/Header.cs` 10) and `Details.MeasureOverride`
  (`Library/Details.cs` 571). `FieldColumns.IsWide` (26) has no reader.

## Gate step 2: questions the stage leaves open

The stage lists no open question. The design raised six; each answer cites the
code or a contract.

1. **Which `Select` behavior wins.** `Select`'s. `SettingsPage.RefreshText`
   selects every ComboBox item again after a language change
   (`Settings/SettingsPage.xaml.cs` 336 to 348), and `SettingsRow.OnSelected`
   relies on "a selection the binding makes equals the setting, so Select saves
   nothing for it" (`Controls/SettingsRow.cs` 213). With `SelectTheme`'s rule,
   that re-selection would save a failed choice again without the person. The
   guard comes from `SelectTheme` and matches `Setting.Toggle` (274): a choice
   on a setting that cannot be edited would leave a draft that `Depart` saves
   later (`Shared/Setting.cs` 252 to 270).
2. **Whether Connection setup can use `FieldColumns`.** No. One width decision
   moves three grids and sizes two reservations
   (`ConnectionPage.UpdateColumns`, 96 to 116): the inputs and the proposal
   (`Body`), the meters and the status (`MeasurementBody`), and the test
   toolbar, whose options move under the heading while Test stays beside it
   (`TestOptions`, `Settings/ConnectionPage.xaml` 311). Two slots cannot hold
   the toolbar's move, and the input and status columns are fixed widths that
   grow with the text size ("these reservations scale with system text size",
   [settings](../../contracts/ui/settings.md#connection-setup)), which
   `FieldColumns`' widths do not.
3. **Whether About's divider fits `FieldColumns`.** Only if the separator also
   turns horizontal. The [About contract](../../contracts/ui/workspace.md#about-and-credits)
   says "At narrower widths the columns stack and the divider becomes
   horizontal"; `FieldColumns` draws its separator only side by side (69 to 72).
   Its one other user, Library's Info fields (`Library/Details.cs` 137), then
   shows the divider above its metadata when stacked. One separator look for
   both is one rule; a second look for About would be a flag for one caller.
4. **Whether each `FieldColumns` holds its own `UISettings`.** No. Library
   builds a new `Details`, with up to three `FieldColumns`, for each selected
   file (`Shell/InspectorPane/Library.cs` 184), so an instance each would make
   Windows objects only to drop them at the next selection. One static instance
   serves every `FieldColumns`.
5. **Whether the Inspector header and Library's section columns read the same
   width.** Yes. The header moves its actions into More below
   `FieldColumns.WideMinimum` (`Header.cs` 10), which is "the Wide and Narrow
   states" of the one inspector card ([Library presentation](../../contracts/library/presentation.md#details-card)).
   `Details.MeasureOverride` (571) decides whether Synopsis and Cast sit side by
   side with the same `SectionMinimum` that its audio case gives to
   `FieldColumns` (340). If `FieldColumns` scales and these do not, the
   inspector has two Narrow widths at a larger text size.
6. **Which settings have no row.** The four above.

## Gate step 3: owners, interfaces and callers

Candidates were compared with `/codebase-design`. Each table lists the chosen
design first.

### `Setting.HasRow`

| Candidate | Result |
| --- | --- |
| `internal bool HasRow { get; init; } = true;` on `Setting`, set at registration | **Chosen.** The fact belongs to the setting, beside `Condition` and `Section`, and the registration already states why (`Editor.cs` 70, 71). |
| A list on `Editor` of settings without a row | Rejected. A second collection beside `All` that must agree with registration. |
| Register them outside `All` | Rejected. `Apply`, `Save`, `PrepareLeave` and `RefreshText` need them. |

| Owner | Interface | Callers |
| --- | --- | --- |
| `Setting` (`Shared/Setting.cs`) | `internal bool HasRow { get; init; } = true;` with the summary "Whether Settings shows the setting in a row of its own, which search lists and reveals." | `Editor` registrations of `DeleteDownloaded`, `DeleteTorrentFile` (588 to 593, comment 70, 71 stays), `CapacityDownload`, `CapacityUpload` (904 to 909, with one comment: Connection setup edits them, so they have no row) set `HasRow = false`. `MainViewModel.SettingSuggestions` (`Finding.cs` 396 to 404) yields a setting only `if (setting.HasRow)` and names none of the four. |

### `Overview` owns the summary refresh

| Candidate | Result |
| --- | --- |
| `Overview` raises `PropertyChanged` for every model change a summary reads; `SettingsPage` refreshes Home's links when `Overview` changes | **Chosen.** The page learns nothing about which values its links read. |
| `Overview.Reads(string? propertyName)`, asked by `SettingsPage.OnMain` | Rejected. The list has one owner, but the page still subscribes to the model, the editor and Library for the same purpose. |
| `Overview` also computes the Home link descriptions | Rejected. It moves twelve link values out of the page's link composition (`Home.cs` 14 to 126), which this stage does not need. |

| Owner | Interface | Callers |
| --- | --- | --- |
| `Overview` (`Settings/Overview.cs`) | No new member. The `MainViewModel` list in its constructor gains `FreeSpaceStatus` and `ShowsFreeSpace`. A class summary states the contract: it raises `PropertyChanged` while Settings shows, whenever a value that the header facts or Home's links read changes. `Refresh` becomes private. | `SettingsPage.OnLoaded` subscribes `Model.Overview.PropertyChanged += OnOverview`, `Release` unsubscribes; `OnOverview` calls `QueueHome()`. `OnMain` keeps only its `RefreshSubtitles()` branch (212 to 215). `OnProviders` keeps only `ShowImdb()` (245, 246). `OnModel` keeps only `RefreshSubtitleCoverage()` (262, 263). `Editor.AttachLibrary` drops `Overview.Refresh()` (`Plugins.cs` 76): `Changed(nameof(VideoSummary))` already reaches `Overview`. |

Library changes reach `Overview` through `Editor.AttachLibrary`, whose list
(Status, Option, Providers, IsAutomaticDownloadEnabled) contains
`OnProviders`' list. Every Home link value is covered: the editor's values
through `Editor.PropertyChanged`, the model's through the list, Library's
through the editor, and the language through `RefreshText`.

### `FieldColumns` follows the text size

| Candidate | Result |
| --- | --- |
| `FieldColumns` multiplies its minimum by the text scale and re-measures when it changes; Speed limits, Home and About become `FieldColumns`; Connection setup and the two Inspector readers call `FieldColumns.Fits` | **Chosen.** The column switching exists once, and every width that decides two columns grows with the text size in one place. |
| Only `FieldColumns.Fits`, with each page keeping its grid code | Rejected. The column switching stays written four times. |
| Connection setup also becomes `FieldColumns` | Rejected (step 2, question 2). |

`Controls/FieldColumns.cs` after the change:

- `private static readonly UISettings Display = new();`, with a comment that one
  instance serves all columns because Library builds new ones for each
  selection.
- `internal static bool Fits(double width, double minimum) => width >= minimum * Display.TextScaleFactor;`
- `public GridLength PrimaryWidth { get; set; } = new(3, GridUnitType.Star);`
  beside `SecondaryWidth`. `MeasureOverride` sets column 0 to `PrimaryWidth`
  when wide and to `1*` when stacked.
- `MinimumWidth` keeps its name and default; its summary says it is the width
  at 100% text size.
- `SeparateSecondary`: wide, a 1-DIP divider beside the secondary column with 24
  DIPs inside it, as now; stacked, the same divider above it, with 24 DIPs
  below it (`BorderThickness (0,1,0,0)`, `Padding (0,24,0,0)`).
- `MeasureOverride` decides with
  `Fits(WidthReference?.ActualWidth ?? availableSize.Width, MinimumWidth)`.
- The constructor subscribes `Display.TextScaleFactorChanged` on `Loaded` and
  unsubscribes on `Unloaded`; the handler calls
  `DispatcherQueue.TryEnqueue(InvalidateMeasure)`, as the pages do now.
- `IsWide` is deleted: it has no reader, and its unscaled copy would answer
  differently.

| Caller | Change |
| --- | --- |
| `Settings/SpeedLimits.xaml` `Profiles` (31 to 68) | `controls:FieldColumns x:Name="Profiles" Margin="0,16,0,0" MinimumWidth="640" Spacing="8" StackSpacing="8" PrimaryWidth="*" SecondaryWidth="*"`, the two `StackPanel`s as `FieldColumns.Primary` and `FieldColumns.Secondary`, without grid definitions or `Grid.Column`. `SpeedLimits.xaml.cs` loses `_display`, `OnTextScale`, `OnProfilesSize`, `UpdateProfiles` and their subscriptions; `Profiles.Margin` in inline mode (38) stays. |
| `Settings/SettingsPage.xaml` `HomeGrid` (420 to 483) | `controls:FieldColumns MinimumWidth="900" Spacing="8" StackSpacing="8" PrimaryWidth="*" SecondaryWidth="*"`, same shape. `Home.cs` loses `OnHomeSize` and `UpdateHomeLayout`; `SettingsPage.OnTextScale` (253 to 258) keeps only `AlignCategory`. |
| `Shell/AboutPage.xaml` `Layout` (13 to 90) | `controls:FieldColumns MaxWidth="800" VerticalAlignment="Center" MinimumWidth="720" Spacing="32" StackSpacing="24" PrimaryWidth="*" SecondaryWidth="*" SeparateSecondary="True"`; the identity `StackPanel` is `Primary` without the `Identity` border; `Links` is `Secondary`. The file gains `xmlns:controls`. `AboutPage.xaml.cs` loses `_display`, `OnLayoutSize`, `OnTextScale`, `UpdateColumns` and the call in `RefreshText`. |
| `Settings/ConnectionPage.xaml.cs` `UpdateColumns` (101) | `var stacked = !FieldColumns.Fits(Body.ActualWidth, 880);`. It keeps `scale` and its subscription for the four reservations (99, 100, 103, 111). |
| `Shell/InspectorPane/Header.cs` `ActionsOverflow` (10) | `!FieldColumns.Fits(Surface.ActualWidth, FieldColumns.WideMinimum)` |
| `Library/Details.cs` `MeasureOverride` (571) | `var hasWidth = FieldColumns.Fits(availableSize.Width, SectionMinimum);` |
| `Settings/SettingsPage.xaml.cs` `Elements` (631 to 656) | A case `FieldColumns columns => new[] { columns.Primary, columns.Secondary }.OfType<FrameworkElement>()` before `Panel panel`. Without it the walk stops at the column presenters, and `AlignCategory`, `Reveal` and `FindControl` no longer see the rate rows of Schedule's inline Speed limits (`ScheduleSpeedSection`, `SettingsPage.xaml` 1468 to 1480). |
| `Shell/InspectorPane.xaml` 423, `Library/Details.cs` 37, 130, 335 | No edit. They follow the text size through the owner. |

### The theme saves through `Setting.Select`

| Candidate | Result |
| --- | --- |
| Delete `Editor.SelectTheme` and `Editor.CanSelectTheme`; `Setting.Select` starts with `if (!CanEdit \|\| value == SelectedValue) return;` | **Chosen.** One way to save a choice. |
| Keep `SelectTheme` as a call to `Theme.Select` | Rejected. A member that only forwards ([naming](../../agents/naming.md#files)). |
| Give `Select` `SelectTheme`'s retry of the shown value | Rejected (step 2, question 1). |

| Caller | Change |
| --- | --- |
| `Settings/ThemePicker.xaml.cs` `OnTheme` (57) | `await Model.Theme.Select(theme);` |
| `Settings/ThemePicker.xaml` 108 | `IsEnabled="{x:Bind Model.Theme.CanEdit, Mode=OneWay}"`; `Editor.Refresh` refreshes every setting when editing becomes possible or not (`Editor.cs` 1188 to 1194). |
| `Shell/MainViewModel.cs` `SwitchTheme` (489) | `Settings.Theme.Select(_dark ? "light" : "dark")` |
| Capture journeys (20 calls) | `Model.Settings.Theme.Select(…)`: `Capture.cs` 830, 978, 1678, 2003, 3236, 3270; `CaptureLibraryFiles.cs` 43, 99, 123, 163, 193, 220, 268, 488, 563; `CaptureLibrary.cs` 64; `CaptureEdits.cs` 21; `CapturePeers.cs` 1275; `CaptureSearch.cs` 21; `CaptureSettings.cs` 97. Each awaits the save as before, or waits for `Model.Theme` afterwards. |
| `Setting.Select`'s summary | Adds that a setting that cannot be edited takes no choice. |

`Editor.CanSelectLanguage` stays: the language takes another path
(`Editor.SelectLanguage`, 1114).

### A saved value as field text

| Candidate | Result |
| --- | --- |
| One private `InputOf(JsonElement)` that writes a saved value as field text, and one `InputOf(double)` that writes a number in the field's unit; `Confirm` and `SelectUnit` store `_confirmedInput` from it; `Editor` and `ConnectionSetup` read `ConfirmedText` | **Chosen.** |
| Compute `_confirmedInput` on each read | Rejected. `ConfirmedText` is read for every snapshot (`MainViewModel.ObserveSpace` reads `Destination`, `Shell/MainViewModel.cs` 1380) and by every summary, so each read would make a new string. |
| `Strings.NumberInput(double)` beside `RateInput` | Rejected. After the change only `Setting` writes such text, and the unit is the setting's state. |

| Owner | Interface | Callers |
| --- | --- | --- |
| `Setting` | `private string InputOf(JsonElement value)`: text as written; empty for any other non-number and for 0 where `HasUnlimited`; `Strings.RateInput` for a rate; otherwise `InputOf(value.GetDouble())`. `private string InputOf(double number) => (number / DurationScale).ToString("G", CultureInfo.CurrentCulture);` | `Confirm` 157 to 162: `_confirmedInput = InputOf(value);`. `SelectUnit` 226: `_input = InputOf(number * scale);`; 227 to 230: `_confirmedInput = _confirmed is { } confirmed ? InputOf(confirmed) : string.Empty;`. |
| `Setting.ConfirmedText` | Unchanged | `Editor.DurationText` (325 to 330): the confirmed text, a space and the unit's word, or `Strings.NoValue`; both callers pass only values above 0 (`SeedingSummary` 160, 163). `ConnectionSetup.Open` (388, 389): `_settings.CapacityDownload.ConfirmedText ?? string.Empty`, and `ConnectionSetup.Input` (779 to 784) is deleted. `Editor.cs` loses `using System.Globalization`. |

`AddSourceChecks.ps1` 124 finds `Setting.Confirm` by name, so no other member
of `Setting` is named `Confirm`.

### `Schedule.RangeAt`

| Owner | Interface | Callers |
| --- | --- | --- |
| `Schedule` (`Settings/Schedule.cs`, beside `Ranges`, 358) | `internal ScheduleRange RangeAt(int day, double minute) => Ranges(day).First(range => minute >= range.Start && minute < range.End);` with a summary that every minute of a day has a range, because `Ranges` covers 0 to 1440 (379). | `Week.Hit` 476 to 479, `Week.OnContextRequested` 682 to 685, `Week.OnKeyDown` 723 to 726: `.RangeAt(…).Period`; capture `Capture.cs` 1422: `schedule.RangeAt(1, 60).Mode`. |

A private member of `Week` was the other candidate. It was rejected because the
capture journey asks the model the same question.

### `PeriodSpan.Step`

| Owner | Interface | Callers |
| --- | --- | --- |
| `PeriodSpan` (`Settings/PeriodSpan.cs`), which the [architecture](../../contracts/architecture.md#ownership-decisions) makes the owner of snapping and clamping | `internal const int Step = 15;` and `internal const int LastStep = 1440 - Step;` | `Snap` (19, 20); `Adjust` 30, 32, 37, 43; `Week.Minute` 133; `Week.OnPointerPressed` 506; `Week.OnKeyDown` 736, 739, 745. The keys move the same cursor that a press snaps, so they keep one step. |

### `SettingsPage.CurrentPanel`

| Owner | Interface | Callers |
| --- | --- | --- |
| `SettingsPage` | `private FrameworkElement? CurrentPanel => _category is null ? IndexContent : CategoryPanels.FirstOrDefault(panel => panel.Name == (string)_category.Tag);` | `ShowCategory` 422 to 428: `_category == next && CurrentPanel is not null`. `LoadCategory` 493 to 499: `existing = CurrentPanel`; `FindName` keeps the name. `AlignCategory` 605 to 607: returns for Home or when `CurrentPanel` is null. The panel always loads before `AlignCategory` runs, because `ShowCategory` loads it in the same call (`Motion.Enter` runs its update at once, `Controls/Motion.cs` 76 to 105), so the null case only replaces `First`'s exception. |

## Decisions the plan did not settle

1. **`Select`'s rule wins**, with `SelectTheme`'s guard (step 2, question 1).
2. **Connection setup keeps its layout code** and asks `FieldColumns.Fits`
   (step 2, question 2). This contradicts the plan, which lists it as a page
   that uses `FieldColumns`.
3. **The Inspector header and Library's section columns follow the same
   width** (step 2, question 5). The plan does not list them; they are
   consumers of the changed rule.
4. **The separator turns horizontal when stacked**, for About and Library Info
   alike (step 2, question 3).
5. **One `UISettings` for every `FieldColumns`** (step 2, question 4). Every
   other owner holds its own; this one is built for each Library selection.
6. **`FieldColumns.PrimaryWidth`** carries the equal columns of the three pages.
   `SecondaryWidth="3*"` would do the same against the hidden 3* primary,
   which the reader would have to know.
7. **0 is empty text only where 0 means unlimited**, as `Confirm` writes it.
   `SelectUnit` wrote empty for 0 in any duration and `ConnectionSetup.Input`
   for anything not above 0. Both durations and both capacities have
   `HasUnlimited`, and the engine stores no negative capacity, so nothing
   changes.
8. **Only the panel lookup of the category tables is one rule** (step 1).
9. **`Overview.Refresh` runs once for a Library change**, not twice.

## Gate step 4: consumers of every changed name

Searched: `app/src` with the capture journeys and `#if CAPTURE` blocks, all
XAML, and `app/tests/*.ps1`.

- `Editor.SelectTheme`: the 22 callers in the theme table above.
  `Editor.CanSelectTheme`: `ThemePicker.xaml` 108 only. No test script.
- `Setting.Select`: `Controls/SettingsRow.cs` 217;
  `SettingsPage.OnEncryption` 832 and `OnAdapter` 838, whose controls are
  disabled while the setting cannot be edited, so the guard changes nothing for
  them; and the theme callers.
- `Setting.HasRow`: new; `Editor` and `Finding.cs`.
- `Setting.Confirm`, `SelectUnit`: same names and signatures.
  `AddSourceChecks.ps1` 124 calls `Confirm` on a boolean setting, whose text
  stays empty. `SettingChecks.ps1` runs `Commit`, `Submit` and `Reject`, which
  do not change.
- `ConnectionSetup.Input`, `Editor.DurationText`: private; their files hold
  every use.
- `Overview.Refresh`: `Plugins.cs` 76, removed. The XAML binds `Overview`'s
  properties (`SettingsPage.xaml` 94 to 179, 382 to 405), which do not change.
- `FieldColumns.IsWide`: no reader. `FieldColumns.WideMinimum`: `Header.cs` 10.
  `FieldColumns.MinimumWidth`: `Library/Details.cs` 340, 608 to 610 (0 and
  infinity, unchanged by the scale). `FieldColumns` in XAML:
  `Shell/InspectorPane.xaml` 423, and the three pages above.
- Removed handlers and names: `OnProfilesSize` (`SpeedLimits.xaml` 36),
  `OnHomeSize` (`SettingsPage.xaml` 420), `OnLayoutSize` (`AboutPage.xaml` 18),
  the names `AlternativeProfile`, `HomeRight`, `Layout`, `Identity`. No capture
  journey or test script names them.
- `Schedule.RangeAt`, `PeriodSpan.Step`, `LastStep`, `SettingsPage.CurrentPanel`:
  new; callers above.
- No test script names a changed member. No catalogue changes.

## Files the change may edit

Product code:

- `Shared/Setting.cs`
- `Controls/FieldColumns.cs`
- `Settings/Editor.cs`, `Editor/Plugins.cs`, `Overview.cs`,
  `SettingsPage.xaml`, `SettingsPage.xaml.cs`, `SettingsPage/Home.cs`,
  `ThemePicker.xaml`, `ThemePicker.xaml.cs`, `ConnectionSetup.cs`,
  `ConnectionPage.xaml.cs`, `SpeedLimits.xaml`, `SpeedLimits.xaml.cs`,
  `Week.cs`, `PeriodSpan.cs`, `Schedule.cs`
- `Shell/AboutPage.xaml`, `Shell/AboutPage.xaml.cs`,
  `Shell/InspectorPane/Header.cs`, `Shell/MainViewModel.cs`,
  `Shell/MainViewModel/Finding.cs`
- `Library/Details.cs`

Capture journeys: `Shell/MainWindow/Capture.cs`, `CaptureLibraryFiles.cs`,
`CaptureLibrary.cs`, `CaptureEdits.cs`, `CapturePeers.cs`, `CaptureSearch.cs`,
`CaptureSettings.cs`.

Documents: `docs/contracts/ui.md`, `docs/contracts/ui/settings.md`,
`docs/contracts/library/presentation.md`.

Shared with other stages, each in other members: stage 4 changes `QueueHome`'s
body (`Home.cs` 149 to 158), which this stage keeps calling, and edits
`SettingsPage.OnFieldKey` (888) and `OnFieldDeparture` (912) and
`Library/Details.cs` 66. Stage 6 edits `Week.UpdateDrag` (602). Stage 12 edits
`SettingsPage.xaml`, `SpeedLimits.xaml` and `ConnectionPage.xaml` later.

## Contract changes, as proposed text

**`docs/contracts/ui.md`, "Windows, layout, and themes"**, after the paragraph
that starts "Design against available window space":

> Two columns of content stack into one below a minimum width that grows with
> the Windows text size, so larger text never squeezes a column. `FieldColumns`
> owns that rule for Settings Home, Speed limits, About and the Inspector;
> Connection setup asks it whether its width fits.

**`docs/contracts/ui/settings.md`, "Category headers"**, after "…a language
change reaches them while the tab stays shown.":

> Home's links refresh when `Overview` reports a change, so the page keeps no
> second list of the model values its summaries read.

**`docs/contracts/ui/settings.md`, "Connection setup"**, after the sentence that
starts "The approved constrained layout":

> The inputs, the measurement and the test options stack at one width, which
> `FieldColumns` decides with the text size. The page keeps its own layout,
> because the test options move under the heading while Test stays beside it,
> which two columns cannot hold.

**`docs/contracts/library/presentation.md`** 324 to 326 becomes:

> - Info's field columns and General's share one geometry, `FieldColumns`: two
>   content slots, fixed 3:2 columns with a 48-pixel gap, stacked below 760
>   DIPs at 100% Windows text size and below a proportionally wider width at a
>   larger text size; the card's Narrow state starts at the same width. Info's
>   metadata column has a divider beside it, which moves above it when the
>   columns stack. Content, headings and actions stay with their owners.

`CONTEXT.md` needs no change: the stage adds no product term. `HasRow`,
`RangeAt` and `Step` are code facts, and "row" is the Settings contract's word
for a setting's line.

## What changes for a person

- At a Windows text size above 100%, the Inspector's General and Library Info
  columns and Library's Synopsis and Cast columns stack at a width that grows
  with the text size, and the Inspector's Play and Show in Torrents move into
  More at that width, as the Settings pages already do. At 100% nothing
  changes.
- Library Info shows a divider above its metadata when that column stacks
  below the synopsis in a narrow window, as About does.
- About's divider sits about 30 DIPs further right: the identity column is that
  much wider and the links that much narrower, because the divider's 32 DIPs
  now sit outside the equal columns. In a right-to-left language the divider
  sits between the columns; the page's own swap mirrored it a second time
  under the mirrored window (`Shell/MainWindow/Chrome.cs` 718). No shipped
  language is right-to-left.
- After a theme save whose reply was lost and which the engine did not apply,
  Switch theme asks for the theme the field already shows, which saves nothing,
  where it saved it again before. The person picks the theme again in
  Appearance.
- After the engine refuses a theme save, Switch theme no longer tries the
  refused theme again, because `Setting.Select` saves nothing for the value
  the field already shows. Settings still shows the refused theme's failure.
  This was decided after the review (see [Review fixes](#review-fixes)).

Search lists the same settings. The summaries refresh on the same changes.

## Cost

- **Agent work:** about four hours for the implementing session: about 30
  files, 20 of them one-line capture edits. One hour for the independent
  review of the diff.
- **Builds:** none of its own. The stage joins batch B's one app build with
  `-Capture`, which is required: 20 capture calls change and `Capture.cs`
  1422 calls `RangeAt`.
- **Check scripts:** none. No script runs a path the stage changes: see
  `SettingChecks.ps1` and `AddSourceChecks.ps1` under step 4.
  `NavigationChecks.ps1` drives `Navigation`, not the page.
- **Capture modes whose subject changes**, for the stage 12 capture review:
  SettingsLayout (Home, Speed limits, Schedule's inline limits, Connection
  setup), Full (About wide and stacked, the Inspector's General, every
  category), LibraryFiles (Library Info stacked, with its divider), Schedule
  (the week's step and keys) and Search (settings in search).

## What reading could not settle

- **That XAML sets `PrimaryWidth="*"` and `SecondaryWidth="*"`** on these plain
  properties. `InspectorPane.xaml` sets only `Primary` and `Secondary` on
  `FieldColumns` today. If the compiler or the page load refuses the text, the
  pages set the widths in their constructors, or the two widths become
  dependency properties. Opening Home, Speed limits and About in any capture
  mode above answers it.
- **That the Inspector header re-arranges after a live text-size change.** The
  review settled it: it did not, because `OnHeaderSize` follows the whole pane,
  whose size a text-size change leaves as it is. See [Review fixes](#review-fixes).
- **How About and Library Info look** with the moved divider. The capture
  review answers it.

## Implementation notes

Implemented on 12 November 2026 on top of stages 1, 2, 3 and 16b (committed
in 5f8d2f6) and stages 4, 6 and 7 (uncommitted), by reading; nothing was built
or run. Where the code differs from the record:

- **The equal column widths are set in code, the record's fallback.** The
  repository proves that XAML text sets a plain `double` or `bool` property of
  a control (`WrapPanel.ColumnSpacing="24"` in `Torrents/FileDialog.xaml`,
  `SettingsRow.IsAdvanced="True"`), but nothing proves it for a plain
  `GridLength`: no XAML in the repository sets one, and the generated type
  information holds `GridLength` as a stub without a text conversion. So
  `MinimumWidth`, `Spacing`, `StackSpacing` and `SeparateSecondary` stay in
  XAML, and `PrimaryWidth` and `SecondaryWidth` are set to `1*` in
  `SettingsPage.InitializeHome`, the `SpeedLimits` constructor and the
  `AboutPage` constructor. The About columns keep the name `Layout` for that.
  `FieldColumns.PrimaryWidth`'s summary says why.
- **`FieldColumns` builds both column definitions empty** and sets column 0 in
  `MeasureOverride`: `PrimaryWidth` side by side, `1*` stacked.
- **`Editor/Plugins.cs` keeps one comment** where `Overview.Refresh()` was: the
  overview follows every change of the editor, so a later reader does not add
  the second refresh back.
- **`Schedule.RangeAt`'s summary excludes minute 1440**, which has no range;
  every caller clamps below it.
- **Control bodies that the stage touched have braces**, as the owner's code
  style asks: `Setting.Select`, `Setting.SelectUnit`, `SettingSuggestions`,
  `SettingsPage.OnMain`, `OnProviders`, `OnModel`, `LoadCategory` and
  `AlignCategory`.
- **Stage 4's changes stay intact.** `QueueHome` keeps its `TurnCallback`;
  this stage only changes who calls it (`OnOverview`). Stage 4's edits in
  `SettingsPage.OnFieldFocus`, `OnFieldKey`, `OnFieldDeparture`,
  `Library/Details.cs` and `ConnectionPage.xaml.cs` are in other lines. Stage
  6's `Week.UpdateDrag` is untouched.
- **The Connection setup contract text** sits after "…whether changes are ready
  to apply.", the end of the sentence that starts "The approved constrained
  layout".

## Review fixes

The review of the stage found two defects and one choice for the lead. The
stage session fixed the defects in the same tree, by reading; nothing was
built or run.

1. **A live text-size change did not re-arrange the Inspector's actions**
   (medium). `ActionsOverflow` depends on the text size, but `ArrangeActions`
   ran only when the whole pane changed size (`OnHeaderSize` on `Surface`) and
   from `ShowActions`, which returns when the buttons are unchanged. At 125%
   and then 100%, Open and Show in Torrents disappeared from the header and
   from More; the other way they showed in both. `InspectorPane` now holds a
   `UISettings` (`Shell/InspectorPane/Header.cs`), subscribes to its
   text-size change while loaded (`Shell/InspectorPane.xaml.cs`, the `Loaded`
   and `Unloaded` handlers), and the handler enqueues `ArrangeActions` on the
   dispatcher, as `FieldColumns` does for its own measure. The pane has its own
   instance, because it is built once for the window; the More menu reads
   `ActionsOverflow` when it opens, so it follows. `Library/Details.cs` needs no
   subscription: it decides in `MeasureOverride`, and a text-size change
   re-measures it, because its text grows and its two `FieldColumns` now
   invalidate their own measure on the change, so the size it measures changes.
   Its cache already keys on the text scale (`_columnScale`).
2. **Two contracts kept text the change made wrong** (low).
   `docs/contracts/ui/torrents.md` (Properties becomes one column) now says
   that the 760-pixel width applies at 100% text size and grows with it.
   `docs/contracts/library/presentation.md` now gives Info its own widths, a
   32-DIP gap and a 384-DIP metadata column (`Library/Details.cs` 130 to 137),
   and keeps the 3:2 columns and 48-pixel gap for General and the File page.
3. **Switch theme after a refused theme save** (the lead's decision, no code
   change). It no longer tries the refused theme again, because
   `Setting.Select` returns for the value the field already shows. The lead
   accepted it under the rule for rare edge cases: Settings still shows the
   failure, and a retry in `Select` would bring back the re-save after a
   language change that this stage removed. It is listed under
   [What changes for a person](#what-changes-for-a-person).
