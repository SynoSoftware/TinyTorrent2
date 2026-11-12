# Stage 12 gate record: tokens, styles and shared controls

Gate done on 12 November 2026, by reading, on the working tree that holds
commit f169412 and the uncommitted work of stages 8, 9 and 10. Nothing was
built or run. This record takes the place of
[the stage's text](../22-one-owner-per-rule.md#12-tokens-styles-and-shared-controls)
for the session that implements it. Paths without a prefix are under
`app/src`; line numbers are those of that working tree, and each reference also
names its element or member. Two files that this stage edits hold uncommitted
work of stages 8 to 10; see [Overlaps](#overlaps-with-stages-8-to-10).

WinUI's own resources are cited from the package the app references
(`Microsoft.WindowsAppSDK.WinUI` 2.3.9, `TinyTorrent.csproj` 78), in
`V:\nuget\packages\microsoft.windowsappsdk.winui\2.3.9\lib\net6.0-windows10.0.17763.0\Microsoft.WinUI\Themes\generic.xaml`,
written below as `generic.xaml`.

## Where the stage stands

Every item has an owner with its callers, or stays as it is with its reason.
The stage adds two files, `Controls/SubtleButtonResources.xaml` and
`Controls/FolderBox.cs`, and three catalogue keys in each of the six
languages. It deletes the three Library table templates and builds those
tables in code from one definition per column.

The decisions, each with its reason under
[Decisions the plan did not settle](#decisions-the-plan-did-not-settle):

1. One dictionary, `Controls/SubtleButtonResources.xaml`, makes a native
   button subtle. It keeps one theme dictionary per theme.
2. One control, `Controls/FolderBox`, is the folder row's editor. "Help text"
   is the field's automation help text; "a tooltip" is Browse's tooltip, which
   names the folder Browse chooses.
3. Every failure shows `Segoe.CircleX`.
4. The rate displays stay as they are.
5. Secondary text has one style per text role: Body and Status.
6. The connection preview rows, the progress cells, the Library Name and
   Title cells and the Inspector's copy fields stay as they are. The Library
   column definitions get one owner in code.
7. `SettingsLink` owns Speed limits' Connection setup link, and takes the
   Settings card geometry: a 20-DIP icon column and a 24-DIP gap.
8. `SettingsRow` shows a switch row's On or Off from its setting.
9. The two share sliders take one Slider style; the capacity fields and the
   Add dialog's headings stay.
10. The schedule legend keeps its own colours, with a comment that names
    them.
11. Dividers get two App.xaml styles: a horizontal line and a vertical line.

## Gate step 1: the findings marked "to re-read"

| Finding | Rule | Result |
| --- | --- | --- |
| Standard and Alternative limit controls (`Settings/ConnectionPage.xaml` 496, 526, 558, 599) | A share slider runs from 5 to 100 percent in steps of 5. | **Kept for the two sliders** (578 to 590, 619 to 631): the six range attributes are written twice. **Dropped for the two capacity fields** (496 to 525, 526 to 556): each is a `TextBox` headed by its direction icon and a `FieldHeader`; the shared parts already have owners, and what differs is their data. |
| Section headers in `Torrents/AddDialog.xaml` 101, 220, 314 against `controls:Section` | — | **Dropped.** Two of the three labels name their controls through `AutomationProperties.LabeledBy` (128 for Destination, 337 for Magnet), and `Section` keeps its title inside its template (`App.xaml` 1477 to 1482), where `x:Bind` cannot reach it. The magnet heading sits above a star-sized row (309 to 313), which `Section`'s `StackPanel` template (`App.xaml` 1458) cannot give. The UI contract gives `Section` the Inspector's groups ([Text, icons, and typography](../../contracts/ui.md#text-icons-and-typography): "Section owns Inspector group headings ... in both Library and Torrents"). |
| Link header (`Settings/SpeedLimits.xaml` 65 to 104 against `Controls/SettingsLink.cs` 62) | A settings link is a card button with its icon, its title, its description and a trailing chevron. | **Kept.** The workspace contract already names the owner: "The shared SettingsLink owns the row presentation for Settings and About" ([workspace](../../contracts/ui/workspace.md#about-and-credits)). The copy differs in geometry (a 20-DIP icon column and a 24-DIP gap against `SettingsLink`'s automatic column and 16-DIP gap), in the title's style (`TinyTorrentBodyTextStyle` against none), in automation (its icon is not `Raw`; it has no help text) and in a `MinHeight` of 36 that `TinyTorrentSettingsLinkStyle`'s 68 with 16-DIP padding already gives. |
| The toggle row, 35 times in `Settings/SettingsPage.xaml` | A switch row shows its setting's state, On or Off, beside the switch. | **Kept.** 33 rows write `StateText="{x:Bind controls:SettingsRow.State(Model.X.IsOn, Model.OnText, Model.OffText)}"` for their own `Setting="{x:Bind Model.X}"`. Two rows have no setting and stay: Video information's automatic download (541) and the Settings start page (837). `Settings/WindowsIntegration.xaml` 32 and 123 use the helper in plain text blocks, not rows, and stay. |
| Legend colours (`Settings/Scheduler.xaml` 63 against `Settings/Week.xaml` 33) | Each schedule mode has one fill and one outline, in the week and in its legend. | **Kept, and stays as it is** (step 3). The legend's three swatches (`Scheduler.xaml` 63 to 67, 81 to 85, 99 to 103) repeat the brush pairs of `WeekTrackStyle` (`Week.xaml` 33 to 36), `WeekAlternativeStyle` (50 to 57) and `WeekPausedStyle` (58 to 64). |
| Secondary text set by hand at about 26 sites | Secondary facts keep their text role and use the secondary text brush ([Text, icons, and typography](../../contracts/ui.md#text-icons-and-typography)). | **Kept for two roles.** Body and the secondary brush, 10 sites: `App.xaml` 855 (video choice year) and 1583 (the property `Field`'s label); `Library/LibraryTable.xaml` 34, 65, 72, 180, 187; `Torrents/FileDialog.xaml` 31, 201, 235. Status and the secondary brush, 8 sites: `Shell/MainWindow.xaml` 311 (filter count), 1188, 1222, 1334, 1352, 1371, 1427 (status bar labels); `Torrents/AddDialog.xaml` 161 (free space). **Dropped for the rest**, listed under step 3. |
| Dividers | A divider is a 1-DIP line in `DividerStrokeColorDefaultBrush`: across its container, or 16 DIPs tall between items on one line. | **Kept.** Vertical: `App.xaml` 988 (`Pane`'s `ActionSeparator`), `Shell/MainWindow.xaml` 30 (`TinyTorrentWorkDividerStyle`), 1110 (`WorkSeparator`), 1214 (`MemoryDivider`). Horizontal: `App.xaml` 1444 (`InspectorDividerStyle`), `MainWindow.xaml` 1068 (the line above the status bar). **Dropped** for borders that are the edge of their own element: `SettingsRow`'s bottom border (`App.xaml` 1326), the preview rows' top borders (`ConnectionPage.xaml` 14, 52, 403), the `Pane` heading (`App.xaml` 960), `FieldColumns`' rail (`InspectorRailStyle`), the speed graph's grid lines and the section borders of `FileDialog.xaml` (152, 213) and `Shell/CreditsPage.xaml` (14). |

The items the plan does not mark "to re-read" were read on both sides and
hold, except where step 3 says why one stays:

- **Button brush aliases.** `App.xaml` 595 to 750 (the `InfoBar` Close button)
  and `Shell/MainWindow.xaml` 82 to 111 (the workspace switch) point the
  `Button*` brushes at the `AppBarButton*` brushes, so the button stays flat
  until pointed at. The first copy is WinUI's own `InfoBar` template text
  (`generic.xaml` 22196 to 22240), 12 aliases in three theme dictionaries; the
  second has 6 of the 12.
- **Folder rows.** `Settings/SettingsPage.xaml` 957 to 985 (Destination), 996
  to 1025 (incomplete folder), 1336 to 1359 (watched folder), 1360 to 1384
  (watched torrents' download folder). Same grid, path field and Browse. The
  watched-folder rows lack the field's help text, Browse's tooltip and Browse's
  automation identifier.
- **Failure glyph.** `Segoe.CircleX` at `Controls/CheckStatus.xaml` 41 and
  `SettingsPage.xaml` 1538; `Segoe.CircleAlert` at `Shell/MainWindow.xaml` 954,
  `Torrents/AddDialog.xaml` 186 and `Torrents/PiecesMap.xaml` 138. The gate
  found three more failure glyphs in code: `Torrents/Torrent.cs` 167
  (`TorrentState.Error`, also the Errors filter through
  `Shell/MainViewModel/Finding.cs` 464), `Torrents/Tracker.cs` 29
  (`TrackerStatus.Error`) and `LibraryRow.Glyph` in `Library/Browser.cs`
  (`Segoe.TriangleAlert` for a file that did not open).
- **Rate display.** The title bar (`MainWindow.xaml` 181 to 220), the status
  bar (1228 to 1267) and the Inspector (`Shell/InspectorPane.xaml` 277 to 328)
  write the direction glyph and brush by hand; the table cells use the Field
  styles (`App.xaml` 1647 to 1699).
- **Secondary text style.** `TinyTorrentRatingSecondaryStyle` (`App.xaml` 816
  to 822) and `BatchDialogLabelStyle` (`Library/BatchDialog.xaml` 9 to 15) are
  the same style: `TinyTorrentBodyTextStyle` with `TextFillColorSecondaryBrush`.
- **Templates.** The connection preview rows (`ConnectionPage.xaml` 10 to 47,
  48 to 97), the progress cells (`Controls/FileBrowser.xaml` 152 to 172,
  `MainWindow.xaml` 607 to 627), the Library Name and Title cells
  (`Library/LibraryTable.xaml` 9 to 32, 41 to 63) and column definitions (193 to
  285, 286 to 336, 337 to 384), and the Inspector's hash fields
  (`InspectorPane.xaml` 452 to 476, 477 to 501).

## Gate step 2: questions reading answered

### Whether a moved style changes clipping, wrapping or spacing

Each change with whether its values change, and the one capture mode that shows
it. The mode names are `Capture.ps1 -Review` values (`app/tests/Capture.ps1`
2 to 3; `Shell/MainWindow/Capture.cs` 33 to 81).

| Change | Values | Capture mode |
| --- | --- | --- |
| `SubtleButtonResources.xaml` for the `InfoBar` Close button | Unchanged: the same 12 aliases as `App.xaml` 599 to 746. | `add-layout` (its `connection-overlay` page shows the closable status bar, `Capture.cs` 2850 to 2873) |
| `SubtleButtonResources.xaml` for the workspace switch | The switch gains the four foreground and two disabled aliases. In Light and Dark the foregrounds are the same brushes (`ButtonForeground*` at `generic.xaml` 359 to 362 and 5968 to 5971; `AppBarButtonForeground*` at 1666 to 1669 and 7275 to 7278), and the switch is never disabled (no `IsEnabled` in `MainWindow.xaml` 74 to 119 or in code). In High Contrast the pointed and pressed text changes; see [What changes for a person](#what-changes-for-a-person). | `add-layout` (its `header` page). The capture has no High Contrast, so the High Contrast change is settled by reading only. |
| `FolderBox` for the four folder rows | Unchanged layout: the same 360-DIP width, 12-DIP gap, `*` and `Auto` columns and left-to-right path. The watched-folder rows gain Browse's tooltip, the field's help text and two automation identifiers, none of which take space. | `settings-prototype` (Transfers, expanded, in English Dark at 1280 and Spanish Light at 720, `Shell/MainWindow/CaptureSettings.cs` 87 to 160) |
| Failure glyph | Same size, colour and box; another glyph of the same font. | `add-layout` (its `command-error` page shows an error notification, `Capture.cs` 2812 to 2822, and its Add pages show the destination error) |
| `TinyTorrentSecondaryTextStyle` | Unchanged: the style's setters are the attributes each site removes. Sites keep their other local values, such as `FileDialog.xaml`'s `TextWrapping="Wrap"`. | `library-files` (Library's Folder, Type, Year, Modified and Kind cells) |
| `TinyTorrentSecondaryStatusStyle` | Unchanged, as above. | `add-layout` (status bar and the Add dialog's free space) |
| Divider styles | Unchanged: the styles hold the values the sites write; each site keeps its own gap. | `add-layout` (status bar line, the Inspector's General divider in its `headers` page) |
| `SettingsLink` geometry and title style | Changed: the text starts 8 DIPs further right; the title takes `TinyTorrentBodyTextStyle` (20-DIP line height). Speed limits' Connection link keeps its look. | `settings-prototype` (Home's links and Speed limits) |
| Switch rows' state text | Unchanged text: `Setting.StateText` returns the words `Model.OnText` and `Model.OffText` return (`Settings/Editor.cs` 476, 477), from the same catalogue. | `settings-prototype` |
| `ConnectionShareStyle` | Unchanged values. | None needed; reading settles it. |
| Library columns built in code | Unchanged values, if the per-column table below is copied exactly. | `library-files` (all three configurations, `CaptureLibraryFiles.cs` 77 to 88) |

One capture review on one capture build therefore runs three modes:
`add-layout`, `settings-prototype` and `library-files`. Credits shows
`SettingsLink` too, but no mode saves the Credits page (`CaptureMemory.cs` 48
to 59 opens it without a capture).

### Whether the three identical theme dictionaries can become one

No. Reading the platform settles it. WinUI writes the same twelve aliases once
for each theme in its own `InfoBar` template (`generic.xaml` 22199, 22213,
22227: `Default`, `HighContrast`, `Light`), and writes identical
`StaticResource` text in its `Default` and `Light` dictionaries for the
`AppBarButton*` brushes themselves (1662 to 1673 and 7271 to 7282). A
`StaticResource` in a theme dictionary resolves against that theme's
resources, so the identical text yields Light, Dark and High Contrast brushes.
One `Default` dictionary would give Light the Dark brushes. The new file keeps
three dictionaries, keyed `Light`, `Dark` and `HighContrast` as
`Controls/ButtonResources.xaml` and `App.xaml` 14, 63, 116 are.

## Gate step 3: owners, interfaces and callers

Candidates were compared with `/codebase-design`. Each table lists the chosen
design first.

### `Controls/SubtleButtonResources.xaml`

| Candidate | Result |
| --- | --- |
| A resource dictionary file, loaded as each button's `Resources`, as `Controls/ButtonResources.xaml` is for `ActionButton` (`Controls/ActionButton.cs` 50 to 53) | **Chosen.** A button's own resources are the only place WinUI's lightweight styling reads, and `Resources` is not a dependency property, so a `Style` cannot set it. |
| Add the aliases to `ButtonResources.xaml` | Rejected. That file holds a labelled button's disabled fill, another rule; every `ActionButton` would turn flat. |
| `TinyTorrentSubtleButtonStyle` on both | Rejected. Its template is a fixed 32-DIP icon button without a drop-down chevron or the `InfoBar` Close button's sizing. |

| Owner | Interface | Callers |
| --- | --- | --- |
| `Controls/SubtleButtonResources.xaml` (new) | A `ResourceDictionary` whose `ThemeDictionaries` hold `Light`, `Dark` and `HighContrast`, each with the 12 `StaticResource` aliases of `App.xaml` 599 to 646: `ButtonBackground`, `ButtonForeground`, `ButtonBorderBrush`, each plain and `PointerOver`, `Pressed`, `Disabled`, pointing at the `AppBarButton` brush of the same name. | `App.xaml` `InfoBar` template, Close button: `<Button.Resources><ResourceDictionary Source="ms-appx:///Controls/SubtleButtonResources.xaml" /></Button.Resources>` replaces 595 to 750. `Shell/MainWindow.xaml` `WorkspacePicker`: the same element replaces 82 to 111; the comment at 81 stays. |

The `InfoBar` comment at `App.xaml` 363 to 367 stays true: the template keeps
WinUI's resources, now from a file.

### `Controls/FolderBox`

| Candidate | Result |
| --- | --- |
| A `Grid` subclass built in code, as `FieldColumns` is (`Controls/FieldColumns.cs` 8), holding the path field and Browse, bound to one `Setting` | **Chosen.** As a panel it stays out of `SettingsRow`'s editor handling (`SettingsRow.Connect` takes only a `Control`, `Controls/SettingsRow.cs` 167), and `SettingsPage.Elements` walks into it (`Settings/SettingsPage.xaml.cs` 629), so `FindControl` still returns the path field (801 to 808) and the page's field handlers still find the row (`RowOf`, 848). |
| A `UserControl` with XAML, as `CheckStatus` | Rejected. It is a `Control`, so `SettingsRow` would bind its enabling, name and help text onto the box, and `FindControl` would return the box instead of the path field. |
| A data template in `App.xaml` | Rejected. Browse needs a click handler or command that a template in `App.xaml` cannot reach. |
| A folder kind on `Setting`, with `SettingsRow` building the editor | Rejected. A folder is text to `Setting`'s parsing (`SettingKind`, `Enums.cs` 899), so a new kind would be a presentation fact in the value kind. |

`Controls/FolderBox.cs`:

- `public sealed partial class FolderBox : Grid`. The constructor sets
  `Width = 360`, `ColumnSpacing = 12`, columns `*` and `Auto`, a `TextBox`
  with `FlowDirection.LeftToRight` in column 0, and an `ActionButton` with
  `Glyph = Segoe.FolderSearch` in column 1.
- `public Setting? Setting` (dependency property). When set, the box binds, with
  the setting as source as `SettingsRow.Bind` does (`SettingsRow.cs` 205): the
  field's `Text` to `Input` (two-way, on each change), the field's and Browse's
  `IsEnabled` to `CanEdit`, the field's `AutomationProperties.Name` to `Label`
  and its `AutomationProperties.HelpText` to `Message`.
- `public string BrowseText` and `public string BrowseTip` (dependency
  properties): Browse's word and its tooltip.
- `public event EventHandler<Setting>? FolderRequested`, raised by Browse's
  click while `Setting` is set.
- Automation identifiers follow `Pane` (`Controls/Pane.cs` 179 to 183): on
  `Loaded`, the box reads its own `AutomationProperties.AutomationId` and gives
  the field that identifier followed by `Path`, and Browse that identifier
  followed by `Browse`.

| Caller | Change |
| --- | --- |
| `SettingsPage.xaml` 960 to 984 (Destination) | `<controls:FolderBox Setting="{x:Bind Model.Destination}" BrowseText="{x:Bind Model.Text.Get('add', 'browse'), Mode=OneWay}" BrowseTip="{x:Bind Model.Text.Get('add', 'browse_tip'), Mode=OneWay}" FolderRequested="OnFolder" AutomationProperties.AutomationId="Destination" />` |
| `SettingsPage.xaml` 1000 to 1024 (incomplete folder) | The same, `Model.IncompleteFolder`, `BrowseTip` from `settings.browse_incomplete_tip`, identifier `IncompleteFolder`. |
| `SettingsPage.xaml` 1339 to 1358 (watched folder) | The same, `Model.WatchPath`, `settings.browse_watch_tip`, identifier `WatchFolder`. |
| `SettingsPage.xaml` 1364 to 1383 (watched torrents' download folder) | The same, `Model.WatchDestination`, `settings.browse_watch_destination_tip`, identifier `WatchDestination`. |
| `SettingsPage.OnFolder` (`SettingsPage.xaml.cs` 838 to 842) | `private void OnFolder(object? sender, Setting setting) => FolderRequested?.Invoke(this, setting);` `RowOf` stays for the field handlers. |
| `SettingsPage.OnFieldFocus`, `OnFieldKey`, `OnFieldDeparture`, `FindControl`, `Elements` | No edit. The path field is still a `TextBox` inside the row, and the box is a `Panel`. |
| `Shell/MainWindow/Workspace.cs` 224 (`PickSettingsFolder`) | No edit; `SettingsPage.FolderRequested` keeps its type. |

### The failure glyph

| Candidate | Result |
| --- | --- |
| `Segoe.CircleX` written at every failure, and the rule stated once in the UI contract, as the critical colour already is | **Chosen.** Every site already writes its glyph and its brush, and the three code sites pass a glyph string into bindings. |
| A `FontIcon` style for failures in `App.xaml` | Rejected. The Pieces map's sign has its own style (`PiecesMapSignStyle`, `PiecesMap.xaml` 81), the proxy's icon must stay in automation (`SettingsPage.xaml` 1542, 1543), and the three code sites cannot use a style. |
| A glyph resource set in `App.cs`, as the direction glyphs are (`App.cs` 60, 61) | Rejected. The code sites would still name `Segoe.CircleX`, so the rule would have two homes. |

| Caller | Change |
| --- | --- |
| `Shell/MainWindow.xaml` 954 (the notification card's error icon) | `Segoe.CircleX` |
| `Torrents/AddDialog.xaml` 186 (destination error) | `Segoe.CircleX` |
| `Torrents/PiecesMap.xaml` 138 (`ErrorSign`) | `Segoe.CircleX` |
| `Torrents/Torrent.cs` 167 (`GetGlyph`, `TorrentState.Error`) | `Syno.Segoe.CircleX`. The state column (`MainWindow.xaml` 654 to 660) and the Errors filter (`Finding.cs` 464) follow. |
| `Torrents/Tracker.cs` 29 (`StatusGlyph`, `TrackerStatus.Error`) | `Syno.Segoe.CircleX` |
| `Library/Browser.cs` `LibraryRow.Glyph` (`OpeningFailure is not null ? Segoe.TriangleAlert`) | `Segoe.CircleX`. The cell keeps its secondary brush (`LibraryTable.xaml` 22, 54). |
| `Controls/CheckStatus.xaml` 41, `SettingsPage.xaml` 1538 | No edit. |

Caution keeps `Segoe.TriangleAlert` (`MainWindow.xaml` 947,
`Shell/MainWindow/Datasets.cs` 127, `Controls/FieldHeader.xaml` 33,
`Settings/WindowsIntegration.xaml` 93 and 146, `ConnectionPage.xaml` 430,
`Torrents/RemovalDraft.cs` 83). No failure uses `Segoe.CircleAlert` afterwards.

### The rate display stays

The plan asked the three hand-written rate displays to use the Field styles.
They stay, each for its reason; the direction tokens they share already have
one owner each (`TinyTorrentDownloadGlyph` and `TinyTorrentUploadGlyph`,
`App.cs` 60, 61; `TinyTorrentUploadBrush`, `App.xaml` 27, 76, 130; the platform's
`AccentTextFillColorPrimaryBrush`).

- The Field styles are table cells. Their label has no text style
  (`App.xaml` 1665 to 1678), as every other table cell has none, and the app
  takes the table's appearance from the TableView contract
  ([WinUI instructions](../../../app/AGENTS.md)).
- The title bar's rates (`MainWindow.xaml` 181 to 220) use the Status style,
  which the UI contract gives to "compact facts beside icons", and name their
  text for Narrator (`AutomationProperties.Name` at 197 and 217). The Field
  template has no text role setting and no name on its label, so the move would
  change the text's line bounds and drop the spoken direction.
- The status bar's rates (1228 to 1267) are two of the status items that
  `Shell/MainWindow/Chrome.cs` sets, measures and hides through their text
  blocks (`SetStatus` 207 to 220, `ShowStatus` 267, 268, `ReserveDigits` 291,
  292, 325, 326). A `Field` would take them out of that mechanism.
- The Inspector's rates (`InspectorPane.xaml` 277 to 328) use
  `InspectorValueStyle` with full line bounds beside the summary. The UI
  contract keeps that: "Approved feature compositions, including Inspector
  General, retain their distinct type roles."

### Secondary text styles

| Candidate | Result |
| --- | --- |
| One style per text role: `TinyTorrentSecondaryTextStyle` (Body) and `TinyTorrentSecondaryStatusStyle` (Status) | **Chosen.** Both roles carry secondary facts today, 10 and 8 times. |
| Only the Body style | Rejected. The status bar's free space is the contract's own example of a secondary fact, and it is Status text. |
| Rename `TinyTorrentRatingSecondaryStyle` and keep its name for ratings | Rejected. The style is the general secondary text; the rating picks it among three. |

| Owner | Interface | Callers |
| --- | --- | --- |
| `App.xaml` `TinyTorrentSecondaryTextStyle`, in the place of `TinyTorrentRatingSecondaryStyle` (816 to 822), before its first use at 858 | `TargetType="TextBlock"`, `BasedOn="{StaticResource TinyTorrentBodyTextStyle}"`, `Foreground` `TextFillColorSecondaryBrush`. | `App.xaml` 855 to 860, 1583 to 1589; `LibraryTable.xaml` 34, 65, 72, 180, 187; `FileDialog.xaml` 31, 201, 235; `BatchDialog.xaml` 36, 45, 107, 132 (and `BatchDialogLabelStyle`, 9 to 15, is deleted); `LibraryTable.RatingStyle` (`LibraryTable.xaml.cs` 25). Each site removes its `Style` and `Foreground` pair and keeps its other attributes. |
| `App.xaml` `TinyTorrentSecondaryStatusStyle`, after `TinyTorrentStatusTextStyle` (876 to 883) | `BasedOn="{StaticResource TinyTorrentStatusTextStyle}"`, `Foreground` `TextFillColorSecondaryBrush`. | `MainWindow.xaml` 311, 1188, 1222, 1334, 1352, 1371, 1427; `AddDialog.xaml` 161. |

Sites that stay, with their reason:

- Caption text in the secondary brush (`FileBrowser.xaml` 165, 206;
  `ConnectionPage.xaml` 591, 632, 656, 662, 670; `InspectorPane.xaml` 656 to
  690; `MainWindow.xaml` 158, 620; `AddDialog.xaml` 388; `Torrents/SpeedGraph.xaml`
  118). Caption is a smaller type role.
- Text blocks with no style (`ConnectionPage.xaml` 28, 78, 386, 388, 445, 455,
  462, 755; `PiecesMap.xaml` 309 to 429; `Shell/AboutPage.xaml` 52, 67;
  `Shell/CreditsPage.xaml` 29; `MainWindow.xaml` 544, 566, 977, 987;
  `InspectorPane.xaml` 1275; `Settings/Speedometer.xaml` 21, 26;
  `WindowsIntegration.xaml` 58, 69; `FileBrowser.xaml` 130). Body's no-wrap,
  trimming and centring would change them; several wrap on purpose.
- `TinyTorrentTitleDetailTextStyle` (`App.xaml` 888 to 897) already holds the
  brush.
- `Week.xaml`'s `WeekSecondaryStyle` (26 to 32). `Week.cs` swaps a caption
  between it and `WeekTextStyle` by key in the week's own resources (182, 408).
- Runs and icons, which are not text blocks.
- `TinyTorrentRatingPrimaryStyle` and `TinyTorrentRatingSubduedStyle` stay
  beside the new style; `RatingStyle` picks among the three.

### Templates

| Template | Result |
| --- | --- |
| Library column definitions (`LibraryTable.xaml` 193 to 384) | **One owner in code**, below. Each column looks the same in every configuration that shows it: its cell template, width, shrink and alignment agree in all three tables today. |
| Connection preview rows (`ConnectionPage.xaml` 10 to 47, 48 to 97) | **Stays.** The label cells differ by design: a limit row sits under its heading with an indented direction icon (60 to 77). One template would choose the indent and the icon from the glyph, and WinUI data templates cannot share their parts. |
| Progress cell (`FileBrowser.xaml` 152 to 172, `MainWindow.xaml` 607 to 627) | **Stays.** The cells bind two item types (`FileNode`, `Torrent`) and different bar facts (indeterminate for a file being read, error for a torrent). The shared parts have owners: `Progress.SelectStyle` (`Controls/Progress.cs`), `Overlay` and `TinyTorrentProgressStyle`. A shared control would add an element to every row of the torrent table. |
| Library Name and Title cells (`LibraryTable.xaml` 9 to 32, 41 to 63) | **Stays.** They bind different text, and only Name keeps left to right. A data template takes no parameter, and a control per cell adds an element to every visible row. |
| Inspector's field with a copy button (`InspectorPane.xaml` 452 to 476, 477 to 501) | **Stays.** The two hash fields are one composition over two values. Their parts have owners: `Field`'s template, `InspectorIdentifierStyle`, `TinyTorrentSubtleButtonStyle` and `TinyTorrentButtonIconSize`. A shared template would need the Inspector to expose each hash as an object. What is written by hand is the icon-only button's icon, at 26 places in the app; see [Found and left](#found-and-left). |

Library columns, in `Library/LibraryTable.xaml.cs`:

- `private static (string Id, bool Shown)[] Columns(LibraryConfiguration configuration)`:
  each configuration's columns in order. The first cannot be hidden.
  - Videos: title, type, year, duration, resolution (hidden), bitrate (hidden),
    subtitles, imdb, tmdb, rottentomatoes, popcornmeter, metacritic (hidden),
    genres, cast (hidden), size (hidden), name (hidden), folder (hidden),
    modified (hidden).
  - Music: title, artist, album, track, duration, year, bitrate (hidden),
    genres (hidden), name (hidden), folder (hidden), size (hidden), modified
    (hidden).
  - Files: name, folder, size, modified, kind (hidden), title (hidden),
    duration (hidden), resolution (hidden), bitrate (hidden).
- `private Column Define(string id, bool shown, bool canHide)`: a new `Column`
  with `Id`, `CellTemplate` from this control's resources, `IsVisible` and
  `CanHide`, and the column's look from one switch, which throws for an unknown
  id as the sort switch does (241):

| Id | Template key | Look |
| --- | --- | --- |
| title | `Title` | Width 300 |
| name | `Name` | Width 300 |
| folder | `Folder` | Width 300 |
| type, year, resolution, subtitles, modified, kind, track | `Type`, `Year`, `Resolution`, `Subtitles`, `Modified`, `Kind`, `Track` | `CanShrink` false |
| duration, bitrate | `Duration`, `Bitrate` | `CanShrink` false, right-aligned |
| size | `Size` | `CanShrink` false, width 110, right-aligned |
| imdb, tmdb, metacritic | `ImdbRating`, `TmdbRating`, `MetacriticRating` | `CanShrink` false, width 110, right-aligned |
| rottentomatoes | `RottenTomatoesRating` | `CanShrink` false, width 140, right-aligned |
| popcornmeter | `PopcornmeterRating` | `CanShrink` false, width 120, right-aligned |
| genres, cast, artist, album | `Genres`, `Cast`, `Artist`, `Album` | Defaults |

- `Create` (123 to 136): `var table = new Table();`, its automation identifier
  `LibraryTable`, then `table.Columns.Add(Define(...))` for each entry of
  `Columns(_configuration)`. The schema, the sort-key switch (138 to 243) and
  the rest of `Create` stay. The three table templates in `LibraryTable.xaml`
  are deleted; the cell templates stay.

The TableView library supports columns built in code before the table loads
(`lib/TableView/tests/TestData.cs` 33; `Column` is "setup-only schema",
`lib/TableView/src/Column.cs` 5 to 8).

### `SettingsLink` owns the Connection setup link

| Owner | Interface | Callers |
| --- | --- | --- |
| `Controls/SettingsLink.cs` | Unchanged members. The constructor's grid (67 to 70) gets `ColumnSpacing = 24` and a first column of 20 DIPs, the geometry of `SettingsSection`'s header (`App.xaml` 1244 to 1246) and of a `SettingsRow` icon (`SettingsIconStyle`'s 12 DIPs plus the row's 12, `SettingsPage.xaml` 24 to 28). `_header` takes `TinyTorrentBodyTextStyle`, as `SettingsSection`'s header does (`App.xaml` 1257 to 1259). | `Settings/SpeedLimits.xaml` 65 to 104 becomes `<controls:SettingsLink x:Name="Connection" Click="OnConnection" />`. `SpeedLimits.xaml.cs` sets `Glyph = Segoe.Gauge` in its constructor and `Header` and `Description` (`settings.connection_setup`, `settings.setup_hint`) in the constructor and in `OnText` (84 to 89), as `Home.cs`, `AboutPage.xaml.cs` and `CreditsPage.xaml.cs` fill their links in code. `BorderThickness` (47), `FindControl` (108) and `FocusConnection` (112) work on the new type unchanged. `Settings/SettingsPage/Home.cs` 138, `Shell/AboutPage.xaml.cs` 10 to 20 and `Shell/CreditsPage.xaml.cs` 201: no edit. |

### Switch rows

| Owner | Interface | Callers |
| --- | --- | --- |
| `Shared/Setting.cs` | `public string StateText => _text.Get("settings", IsOn ? "on" : "off");` with a summary that it is a switch setting's state in the person's language. `Editor.RefreshText` refreshes every setting on a language change (`Editor.cs` 1177, 1178), so the text follows. | `SettingsRow` |
| `Controls/SettingsRow.cs` `Attach` (155 to 163) | Beside the header and error bindings: when `Content` is a `ToggleSwitch` and the row has no local `StateText`, bind `StateText` to `Setting.StateText`. The class summary (45 to 50) adds that a switch row shows its setting's state. | The 33 rows in `SettingsPage.xaml` whose `StateText` reads their own setting drop the attribute. Rows 541 and 837 keep `SettingsRow.State`. |

The switch's own `IsOn` binding stays in each row's XAML. A `{Binding}` set in
code on `IsOn` is a local binding that the switch can replace when the person
toggles it, while the page's `x:Bind` sets the value again at each change.

### `ConnectionShareStyle`

| Owner | Interface | Callers |
| --- | --- | --- |
| `ConnectionPage.xaml` `UserControl.Resources` | `<Style x:Key="ConnectionShareStyle" TargetType="Slider">` with `Minimum` 5, `Maximum` 100, `StepFrequency` 5, `SmallChange` 5, `SnapsTo` `StepValues`, `IsThumbToolTipEnabled` false. | The sliders at 578 and 619. |

### Schedule legend stays

WinUI styles inherit along one line (`BasedOn`). A legend swatch needs the
mode's colours and the swatch geometry (`SchedulerSwatchStyle`,
`Scheduler.xaml` 10 to 15); a week period needs the colours and its High
Contrast outline (`WeekPeriodStyle`, `Week.xaml` 40 to 42). One owner of the
colours would write the geometry or the outline once per mode, three copies
for the two copies of each brush pair today. `Week.xaml` gets one comment
above `WeekTrackStyle` naming the legend in `Scheduler.xaml` that shows the
same fills and outlines, so a change to one reaches the other.

### Dividers

| Owner | Interface | Callers |
| --- | --- | --- |
| `App.xaml` `TinyTorrentDividerStyle` (renames `InspectorDividerStyle`) | `TargetType="Border"`: `Height` 1, `Background` `DividerStrokeColorDefaultBrush`, `AutomationProperties.AccessibilityView` `Raw`. | `InspectorPane.xaml` 418; `MainWindow.xaml` 1068 to 1075, which keeps `VerticalAlignment="Top"` and `IsHitTestVisible="False"`. |
| `App.xaml` `TinyTorrentVerticalDividerStyle` (new) | `Width` 1, `Height` 16, `VerticalAlignment` `Center`, the same brush, `Raw`. No margin: the container decides the gap. | `App.xaml` `Pane` template's `ActionSeparator` (988 to 997), which keeps `x:Name`, `Margin="12,0"` and `Visibility="Collapsed"`; `MainWindow.xaml` `MemoryDivider` (1214 to 1220), which keeps its name; `MainWindow.xaml` `TinyTorrentWorkDividerStyle` (30 to 35) becomes `BasedOn` it with only `Margin="10,0"`; `WorkSeparator` (1110 to 1118) uses `TinyTorrentWorkDividerStyle` and keeps `x:Name` and `Visibility`. `Shell/MainWindow/Work.cs` 51 to 54 loses its `SetAccessibilityView` call, which the style now makes. |

Both styles sit in `App.xaml` before the `Pane` style (927), because a
`StaticResource` reads only keys defined above it in the same dictionary.

## Decisions the plan did not settle

1. **One dictionary per platform look, three theme dictionaries in it.** The
   workspace switch takes all 12 aliases, not its 6, because WinUI pairs each
   app-bar background with its own foreground. With the 6, High Contrast draws
   the pointed text in `SystemControlHighlightBaseHighBrush`, the highlight
   colour (`generic.xaml` 2985, 2921), on `AppBarButtonBackgroundPointerOver`,
   the same highlight colour (4478, 2932): the workspace name disappears while
   pointed at or pressed. The app-bar foreground there is the highlight text
   colour (4482, 2910).
2. **The folder row's "help text" is the path field's automation help text**,
   the setting's message, as the Destination and incomplete-folder fields have
   today. Visible help text is not added: the owner ruling allows it only when
   it adds real value ([Text, icons, and typography](../../contracts/ui.md#text-icons-and-typography)),
   and each row keeps the description its setting has.
3. **The folder row's "tooltip" is Browse's**, and it names the folder Browse
   chooses: the button ruling puts in the tooltip what the one word does not
   say, and Add (`add.browse_tip`) and Move (`file_action.browse_tip`) already
   name their folder this way. The path field gets no tooltip: a tooltip never
   repeats the text its element shows, and a field's full text is reachable by
   focus and selection. The incomplete folder's Browse said "Choose the
   download folder", which is wrong for that row.
4. **The folder row's automation identifiers come from the box**, as `Pane`
   names its buttons. The four path fields change from `DefaultDestination`,
   `IncompleteFolder`, `WatchPath`, `WatchDestination` to `DestinationPath`,
   `IncompleteFolderPath`, `WatchFolderPath`, `WatchDestinationPath`; Browse
   from `BrowseDestination` and `BrowseIncomplete` (and none) to
   `DestinationBrowse`, `IncompleteFolderBrowse`, `WatchFolderBrowse`,
   `WatchDestinationBrowse`. No check, capture journey or code reads any of
   them (step 4).
5. **Every failure shows `Segoe.CircleX`.** The UI contract's colour ruling
   says "icon shapes ... keep every meaning without colour". `CircleAlert`, a
   circled exclamation mark, shares the notification card's slot with `Info`, a
   circled i (`MainWindow.xaml` 938 to 958), and the state columns with
   `CircleQuestionMark` for an unknown state (`Torrent.cs` 168,
   `Tracker.cs` 31); in High Contrast only the inner mark tells them apart.
   `CircleX` resembles neither, pairs with `CircleCheck` as the check result
   already does (`CheckStatus.xaml` 31, 41; `SettingsPage.xaml` 1529, 1538),
   and caution keeps its triangle. WinUI's `InfoBar` marks its Error severity
   with its own status glyph (`InfoBarErrorIconGlyph`, `generic.xaml` 36490);
   the app keeps that glyph inside message bars.
6. **A Library file that did not open shows the failure glyph**, not the
   caution triangle, in the cell's secondary brush.
7. **The rate displays stay** (step 3).
8. **Secondary text gets a Status style too.**
9. **The Library columns are built in code.** The rule has three copies, and
   the per-id sort switch already sits in `Create`, so one per-id definition
   in the same file keeps a column's facts together.
10. **`SettingsLink` takes the Settings card geometry and Body title.** The
    workspace contract makes it the owner of Speed limits' link, and the
    UI contract asks equivalent groups to share heading treatment and content
    insets. With its 16-DIP gap the Connection setup link would sit 8 DIPs left
    of the limits card above it, and on Credits the single-contribution link
    rows already sit 8 DIPs left of the `SettingsSection` expanders between
    them (`CreditsPage.xaml.cs` 197 to 215).
11. **`Setting.StateText`** carries the switch words, so `SettingsRow` needs no
    catalogue and the row states nothing.
12. **The capacity fields, the Add headings, the preview rows, the progress
    cells, the Name and Title cells, the hash fields and the legend stay**
    (steps 1 and 3).
13. **A divider's gap belongs to its container**, so the divider styles set
    none and `TinyTorrentWorkDividerStyle` keeps the work area's 10 DIPs.

## Gate step 4: consumers of every changed name

Searched: `app/src` with the capture journeys and `#if CAPTURE` blocks, all
XAML, `app/tests/*.ps1`, `docs/contracts` and `docs/agents`.

- The `Button*` alias keys: the platform's keys; consumers are the two buttons.
  `Controls/SubtleButtonResources.xaml`: new; `App.xaml` and `MainWindow.xaml`.
- `FolderBox`, `Setting`, `BrowseText`, `BrowseTip`, `FolderRequested`: new;
  the four rows. `SettingsPage.OnFolder`: private; `SettingsPage.xaml` 980,
  1020, 1355, 1380 (`Click="OnFolder"`, which becomes `FolderRequested`). The
  removed names `Destination`, `Browse`, `IncompleteFolder`, `BrowseIncomplete`
  (`x:Name` in `SettingsPage.xaml`): no reader; `Capture.cs` 3034 names the Add
  dialog's own `Destination`. The automation identifiers in decision 4: no
  reader.
- `Segoe.CircleAlert`: `MainWindow.xaml` 954, `AddDialog.xaml` 186,
  `PiecesMap.xaml` 138, `Torrent.cs` 167, `Tracker.cs` 29; no capture journey or
  script reads a glyph. `Torrent.GetGlyph`: `Torrent.cs` 152, `Finding.cs` 464.
  `LibraryRow.Glyph`: `LibraryTable.xaml` 20, 52.
- `TinyTorrentRatingSecondaryStyle`: `LibraryTable.xaml.cs` 25 only.
  `BatchDialogLabelStyle`: `BatchDialog.xaml` 36, 45, 107, 132.
  `TinyTorrentSecondaryTextStyle`, `TinyTorrentSecondaryStatusStyle`: new;
  sites in step 3. No contract names a style here.
- `InspectorDividerStyle`: `InspectorPane.xaml` 418 only.
  `TinyTorrentWorkDividerStyle`: `Shell/MainWindow/Work.cs` 50.
  `ActionSeparator`: `Controls/Pane.cs` 164 reads it by name, which stays.
  `WorkSeparator`, `MemoryDivider`: `Work.cs` 140, `Chrome.cs` 284, by name,
  which stays.
- `SettingsLink`: `Home.cs` 11, 138; `AboutPage.xaml.cs` 10 to 20;
  `CreditsPage.xaml.cs` 170, 201; `SpeedLimits.xaml` (new);
  `docs/contracts/ui/workspace.md` 297. `TinyTorrentSettingsLinkStyle`:
  `SettingsLink.cs` 64, `SettingsPage.xaml` 40, `Home.cs` 142, unchanged.
  `Connection` (`SpeedLimits`): `SpeedLimits.xaml.cs` 47, 108, 112.
- `Setting.StateText`: new; `SettingsRow`. `Setting` is reached by reflection
  in `app/tests/SettingChecks.ps1` 10, `AddSourceChecks.ps1` 106 and
  `UpdateChecks.ps1` 73, through members that do not change.
  `SettingsRow.State`: `SettingsPage.xaml` 541, 837 and the 33 rows that drop
  it; `WindowsIntegration.xaml` 32, 123. `Editor.OnText` and `OffText`: the
  same sites, `Editor.cs` 285 and `Editor/Plugins.cs` 27.
- `ConnectionShareStyle`: new; `ConnectionPage.xaml` 578, 619. The sliders'
  automation identifiers `ConnectionStandard` and `ConnectionAlternative`
  stay.
- `LibraryVideosTable`, `LibraryMusicTable`, `LibraryFilesTable`:
  `LibraryTable.xaml.cs` 131 to 133 only. The table's automation identifier
  `LibraryTable` stays; no check reads it.
- Catalogue keys: `add.browse` and `add.browse_tip` keep their Add dialog
  readers (`AddDialog.xaml` 133, 136) and the Destination row. The three new
  keys have only their rows.

## Files the change may edit

Product code:

- `App.xaml`
- `Controls/SubtleButtonResources.xaml` (new), `Controls/FolderBox.cs` (new),
  `Controls/SettingsRow.cs`, `Controls/SettingsLink.cs`
- `Shared/Setting.cs`
- `Shell/MainWindow.xaml`, `Shell/MainWindow/Work.cs`,
  `Shell/InspectorPane.xaml`
- `Settings/SettingsPage.xaml`, `Settings/SettingsPage.xaml.cs`,
  `Settings/SpeedLimits.xaml`, `Settings/SpeedLimits.xaml.cs`,
  `Settings/ConnectionPage.xaml`, `Settings/Week.xaml`
- `Torrents/AddDialog.xaml`, `Torrents/PiecesMap.xaml`,
  `Torrents/FileDialog.xaml`, `Torrents/Torrent.cs`, `Torrents/Tracker.cs`
- `Library/Browser.cs`, `Library/BatchDialog.xaml`,
  `Library/LibraryTable.xaml`, `Library/LibraryTable.xaml.cs`
- `Resources/en.json`, `es.json`, `fr.json`, `pt.json`, `nl.json`, `zh.json`

No capture journey and no check script changes.

Documents: `docs/contracts/ui.md`, `docs/contracts/ui/settings.md`.

### Overlaps with stages 8 to 10

`git status --short` shows two of these files changed by the uncommitted work
of stages 8 to 10. The implementer re-reads both before editing, and edits only
the members named here:

- `Library/Browser.cs`: stages 8 to 10 changed it in six places (diff hunks at
  HEAD 12, 151, 447, 472, 1294, 1318). Stage 12 edits only
  `LibraryRow.Glyph` (HEAD 1384, 1385; working tree 1395).
- `Settings/SettingsPage.xaml.cs`: stage 9 changed `UpdateRequested` and
  `DeleteRequested` (HEAD 52, 53), `ShowImdb` (HEAD 233, 234) and the dataset
  rows in `LoadCategory` (HEAD 518 to 525). Stage 12 edits only `OnFolder`
  (HEAD 839 to 843).

An owner's change starts only when no other session has uncommitted edits in
its files ([Order, tracks and cost](../22-one-owner-per-rule.md#order-tracks-and-cost)),
so these two edits wait until stages 8 to 10 are committed. Every other file
above has no uncommitted change.

## Localisation

Three new keys in the `settings` section of every catalogue. Each translation
reuses that catalogue's own words for the row's label (`incomplete_folder`,
`watch_path`, `watch_destination`) and the phrasing of its `add.browse_tip`.

| Key | en | es | fr | pt | nl | zh |
| --- | --- | --- | --- | --- | --- | --- |
| `browse_incomplete_tip` | Choose the incomplete downloads folder | Elegir la carpeta de descargas incompletas | Choisir le dossier des téléchargements incomplets | Escolher a pasta de downloads incompletos | De map voor onvoltooide downloads kiezen | 选择未完成下载文件夹 |
| `browse_watch_tip` | Choose the folder to watch | Elegir la carpeta que se vigila | Choisir le dossier à surveiller | Escolher a pasta a monitorar | De te bewaken map kiezen | 选择要监视的文件夹 |
| `browse_watch_destination_tip` | Choose the download folder for watched torrents | Elegir la carpeta de descarga de los torrents vigilados | Choisir le dossier de téléchargement des torrents surveillés | Escolher a pasta de download dos torrents monitorados | De downloadmap voor bewaakte torrents kiezen | 选择监视的种子的下载文件夹 |

No text changes otherwise. `Setting.StateText` reads the existing
`settings.on` and `settings.off`.

## Contract changes, as proposed text

**`docs/contracts/ui.md`, "Text, icons, and typography"**, after the paragraph
that starts "Every button carries a Segoe icon":

> Every failure shows one glyph, the circled cross (`Segoe.CircleX`): an
> errored torrent or tracker, a failed check, a folder that Add cannot use, an
> error notification, the Pieces map's error and a Library file that did not
> open. Its shape differs from information, caution and an unknown state, so it
> reads without colour. The InfoBar keeps WinUI's own severity glyphs.

In the same section, "App.xaml owns the shared title and body styles." becomes:

> App.xaml owns the shared title and body styles, including one secondary
> style for each of the Body and Status roles.

**`docs/contracts/ui.md`, "Buttons"**, after the item "An icon-only button is
subtle":

> A native button that must look subtle in its own template, such as the
> workspace switch and the InfoBar's Close, takes
> `Controls/SubtleButtonResources.xaml`, which points its brushes at WinUI's
> app-bar button brushes in each theme.

**`docs/contracts/ui.md`, "Native controls and visual authority"**, after the
paragraph that starts "Build surfaces with Fluent layering":

> A divider is App.xaml's 1-DIP line in the divider stroke brush, across its
> container or 16 DIPs tall between items on one line; the container sets the
> gap around it.

**`docs/contracts/ui/settings.md`, "Cards and categories"**, after "Trailing
controls align to the actual right inset.":

> Card headers, icon-led rows and Settings links share one geometry: a
> 20-DIP icon column and a 24-DIP gap to their text. A switch row shows On or
> Off beside its switch, from its setting. A folder setting's editor is its
> path and Browse; the path's accessible help is the setting's message, and
> Browse's tooltip names the folder it chooses.

`CONTEXT.md` needs no change: `FolderBox` is a control, not a product term.

## What changes for a person

- Every failure icon is a circled cross: the torrent list's error state and
  the Errors filter, tracker errors, the Pieces map's error, error
  notifications, Add's folder error and a Library file that did not open (a
  triangle before, in the cell's grey).
- Settings links on Home, About and Credits start their text 8 DIPs further
  right, in line with the card headers; on Credits the link rows now line up
  with the expanders between them. Their titles use the Body style's 20-DIP
  line height.
- The incomplete folder's Browse tooltip names that folder. The two watched
  folder Browse buttons get tooltips, and Narrator reads the setting's message
  on their fields.
- In High Contrast, the workspace switch's name stays readable while pointed at
  or pressed.

Nothing else changes: the moved styles, templates and columns keep their
values.

## Capture review and cost

- **Agent work:** about five hours for the implementing session: `FolderBox`
  and its four rows, the Library columns, about 15 XAML files, six catalogues
  and two contracts. One hour for the independent review of the diff. The gate
  took about three hours.
- **Builds:** none of its own. The stage is batch F, one app build with
  `-Capture`.
- **Check scripts:** none. No script runs a path the stage changes (step 4).
- **One capture review** on that build, three modes: `add-layout`,
  `settings-prototype` and `library-files`. Each mode's subject is in step 2.
  The machine time of these modes was not measured; `settings-prototype` is
  the longest, because it saves every Settings category in two languages and
  themes, collapsed and expanded.

## Found and left

- **The workspace switch's chevron disappears while pointed at in High
  Contrast.** WinUI draws it in `DropDownButtonForegroundSecondaryPointerOver`,
  the highlight colour in High Contrast (`generic.xaml` 5320), on the
  highlight-coloured background the switch takes from the app-bar brushes. It
  was the same before this stage; no alias in this stage covers the chevron.
- **Icon-only buttons write their icon by hand** at 26 places in eleven
  files (every `TinyTorrentSubtleButtonStyle` user, and
  `TinyTorrentInspectorActionTemplate` at `InspectorPane.xaml` 12 to 19 read by
  `Shell/InspectorPane/Header.cs` 20): a `FontIcon` with `SegoeFont` and
  `TinyTorrentButtonIconSize`. One owner, such as a `ContentTemplate` on
  `TinyTorrentSubtleButtonStyle` that draws a glyph given as the button's
  content, would reach Shell, Settings, Torrents, Library, Subtitles, Shared
  and Controls files, so it is separate work and not part of this stage.

## What reading could not settle

- **How Home, About and Credits look with the 24-DIP gap.** The capture review
  shows Home and Speed limits; About shows in the `1` mode, and no mode saves
  Credits.
- **That the code-built Library columns match the deleted templates.** The
  per-column table above is copied from `LibraryTable.xaml` 193 to 384; the
  `library-files` capture compares the result.
- **That `FolderBox`'s identifiers are set before an automation client asks.**
  It sets them on `Loaded`, before the person can reach the rows; no check reads
  them today.
- **That the High Contrast change of the workspace switch shows as read.** The
  capture runs Light and Dark only; the brushes are settled by `generic.xaml`.

## Implementation notes

Implemented on 12 November 2026 by reading only; nothing was built or run.
Every item above is in the working tree except the two held edits below.

### Held edits

`Library/Browser.cs` and `Settings/SettingsPage.xaml.cs` held uncommitted work
of stage 9, so these edits waited until stage 9 was done. They are now made;
see [Held edits, made after stage 9](#held-edits-made-after-stage-9).

- `Library/Browser.cs`, `LibraryRow.Glyph`, and `Library/Details.cs`: the
  review found that `Glyph` has two meanings, because Details' File type
  field reads it and shows the failure glyph after a failed open. `LibraryRow`
  gets a kind glyph, today's expression without the failure branch. Details'
  File type field reads the kind glyph, and `Glyph` becomes the failure ?
  `Segoe.CircleX` : the kind glyph. The Inspector heading
  (`Shell/InspectorPane/Library.cs`) keeps `Glyph`.
- `Settings/SettingsPage.xaml.cs`, `OnFolder`: the method becomes
  `private void OnFolder(object? sender, Setting setting) =>
  FolderRequested?.Invoke(this, setting);`. `RowOf` stays for the field
  handlers.

### Differences from this record

- `FolderBox` names its parts when its own `AutomationProperties.AutomationId`
  is set, through `RegisterPropertyChangedCallback`, instead of on `Loaded`.
  XAML sets the identifier while it creates the box, so the parts have their
  identifiers before any automation client can ask.
- `LibraryTable.Define` has one arm per column identifier, because each
  identifier has its own template key; the arms keep the record's order.
- `SpeedLimits` fills the link's header and description in one
  `RefreshConnection` method, which the constructor and `OnText` call.
- `SettingsLink._header` drops its local `TextTrimming`, which
  `TinyTorrentBodyTextStyle` already sets. `ActionSeparator` drops its local
  `VerticalAlignment`, which `TinyTorrentVerticalDividerStyle` sets.
- `LibraryTable.xaml` drops its `table` namespace, which only the deleted table
  templates used.
- Speed limits' Connection setup link now reads its setup hint as automation
  help text and hides its icon from automation, as every `SettingsLink` does.
- `Segoe.CircleAlert` (`lib/Glyphs/src/Segoe.cs`) has no user left. The file
  is outside this record's list, so the constant stays.

### Review fixes

- **The folder path field takes the row's help text** (review M1). This
  replaces decision 2. `SettingsRow.Update` sets the row's help text (its
  error, description, caution and unit) on its content. For a folder row the
  content is the `FolderBox`, a panel with no automation peer, so Narrator
  never read that text, and the path field had only the setting's message.
  `FolderBox` now copies its own `AutomationProperties.HelpText` to the path
  field when it changes, and no longer binds the field's help text to
  `Message`; the message still reaches the field as the row's error. Narrator
  now reads the Incomplete folder and watched-torrents download folder
  descriptions, as it does for every other Settings field. The sentence in
  `docs/contracts/ui/settings.md` now says the path takes the row's help.
- **A switch row shows its state whichever arrives first** (review L1).
  `SettingsRow.Connect` binds `StateText` when its editor becomes a
  `ToggleSwitch`, with the same local-value check, instead of `Attach`.
  `Connect` runs when the setting or the content changes, so a row built in
  code with its setting before its switch also shows On or Off.

### Held edits, made after stage 9

- `Settings/SettingsPage.xaml.cs` 838 and 839: `OnFolder` takes
  `(object? sender, Setting setting)` and raises `FolderRequested` with that
  setting. `RowOf` stays for the field handlers.
- `Library/Browser.cs` 1395 to 1400: `LibraryRow.Glyph` is `Segoe.CircleX`
  after a failed open, otherwise the new internal `KindGlyph`, which holds
  the former kind expression. The table cells (`LibraryTable.xaml`) and the
  Inspector heading (`Shell/InspectorPane/Library.cs`) keep `Glyph`.
- `Library/Details.cs` 86: the File type field reads `KindGlyph`, so it keeps
  the file's kind after a failed open (review M2).
