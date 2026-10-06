# Consolidated architecture: product WinUI app

[The proposed architecture](../proposed/README.md) replaces this document. It
is kept as one of the inputs to that proposal. Where they differ, follow the
proposal. The owner's later decisions are recorded in
[interface.md](../../interface.md#committing-edits): settings never ask before
leaving, and an explicit editor's discard prompt offers Save, Discard and
Cancel.

Result of four reviews of `app/src` on 2026-10-06: [the first proposals](review.md),
[review A](architect-a.md) (ownership and state), [review B](architect-b.md)
(code volume and WinUI idiom), and [review C](owner-review.md) (the owner's
standard). This document is the decision. The four reviews hold the evidence.

The standard is the owner's, from
[handover](../../handover.md#coding-standard-and-goal): easy-to-read,
low-bloat code. Refactor when it removes duplication, clarifies ownership, or
deletes code. Do not add a type, interface or file that only moves code.

## The decision in short

The window's structure is mostly sound. The transport, the inspector, the Add
preview, the file browser and the TableView seam stay as they are. The problems
are concentrated in five places:

1. **Settings** have two editors and several readers.
2. **Translated text** is set by hand in about 250 lines of code-behind.
3. **Dialogs** each repeat the same lifetime code.
4. **Connection state** is a set of flags, and the window shows it in one bar
   together with command failures.
5. **Change notifications** refresh everything, so Settings and the schedule
   do work that nobody asked for.

By the reviews' estimates, fixing these deletes about 560 lines of C# and adds
about 90. No change adds an interface, a framework, or a new project.

## Who owns what

| Concern | Owner after the change | Today | Issue |
| --- | --- | --- | --- |
| Each engine setting: value, draft, parse, unit, save | `Preferences`, one `Preference` per setting with a kind and a section | `Preferences`, `SpeedLimits`, `MainViewModel._settings` and Finding.cs | #119 |
| Speed limits editor | The Settings page, Transfers | The Settings page and the Limits dialog | #119 |
| Theme | An ordinary `Preference` | `MainViewModel.SelectTheme`, coupled to language | #119 |
| Language | `MainViewModel.ChangeLanguage`, which publishes before it saves | Same, but disabled during a theme save | #119 |
| Weekly schedule | Its own class, split out of Preferences.cs | Inside Preferences.cs | #122 |
| Translated text | XAML bindings to `Strings` | `RefreshText` methods in code-behind | #121 |
| Dialog lifetime | One private slot in `MainWindow` | Four field pairs and four copies of "reopen Add" | #115 |
| Whether Add is open | `AddDraft` (the view still sets it) | `MainViewModel` | #115 |
| Accepted selection and the inspector's response | `MainViewModel` | Table, `MainWindow` and `MainViewModel` | #116 |
| Connection state | One phase enum in `MainViewModel` | Five booleans | #20 |
| Command failure | A separate fact with its own bar | Mixed into the connection message | #20 |
| Failure text | `Strings` | Four copies | #123 |
| "Torrent in error" | `Torrent` | Three copies | #29 |
| Shortcut text in menus | The one list of shortcuts | A second, hard-coded list | #26 |
| Capture modes | One enum | Names listed twice | #124 |

These stay as they are: the `PipeClient` transport, `Inspector`,
`FileSelection`, `FileOperation`, `Placement`, the preview lifecycle in
`AddDraft`, `PeriodSpan`, `Week`, `Torrent` rows, and the TableView seam.

## Settings follow Fluent 2: they apply immediately

Fluent 2 says a switch "triggers an immediate change". The WinUI app settings
guidelines say a changed setting takes effect at once, without a confirmation
button. [interface.md](../../interface.md#preferences) says the same for this
product.

The Settings page mostly complies. Switches and lists save when they change.
Text and number fields save on Enter or when focus moves to another field. The
schedule period editor has Save and Cancel, because one period is a single
change made of several fields.

Three things do not comply, and #119 and #120 fix them:

- Leaving the page or closing the window with a typed value asks "Discard
  unfinished changes?" instead of saving the value (#120). After #120, only an
  open schedule period editor asks.
- The speed limits can also be edited in a separate dialog with an Apply button
  (#119).
- The Language and Theme lists are disabled while their save runs (#119).

## Order of work

1. **Let the uncommitted work land.** It changes Preferences.cs, Week.cs, the
   search, and the capture files. Every step below touches some of these files.
2. **Move the schedule out of Preferences.cs (#122).** This is a pure move, so
   the next step edits a file of about 300 lines.
3. **One settings owner, and saving when the user leaves (#119, #120, #112).**
   This deletes the most code that holds a rule, and it fixes a defect that the
   owner recorded. Do the three issues in one pass, because they change the same
   code.
4. **Prove the XAML text binding on FileForm, then convert the other forms
   (#121).** This is the largest deletion by volume. It also makes access keys
   (#52) and accessible names (#22, #26) one attribute each.
5. **One dialog lifetime (#115).** After step 3, the slot holds Add, Remove,
   Files and the discard prompt.
6. **Selection and the inspector (#116).** #116 already says to do it after
   #115.
7. **Connection phase and two message bars (#20), and one failure-text rule
   (#123).**
8. **Narrow change notifications (#114).** This also removes the workarounds in
   the uncommitted schedule work.
9. **The small rules:** "torrent in error" (#29), the shortcut text (#26), and
   the capture mode enum (#124). Do each when its code next changes.

#113 (bulk projection) is independent and can be done at any time.

## Decisions for the owner

These are product or risk decisions. The reviews cannot settle them.

Decided on 2026-10-06 and recorded in [interface.md](../../interface.md):

- **The Limits dialog is deleted.** Speed limits opens Settings at the speed
  limits, as its search result does (#119).
- **Settings never ask before leaving.** Leaving the page or closing the window
  applies each valid typed value and restores the saved value of an invalid
  one, as WinUI's NumberBox does by default. The Save/Discard/Cancel question
  is only for explicit editors, such as a tracker list or the Add form (#120).
  Before this, interface.md's Main window section said that leaving Settings
  "asks before discarding actual unfinished input". That contradicted its own
  Preferences section, and the code followed the wrong sentence.

Still open:

1. **The buttons of the discard prompt for explicit editors.** interface.md
   asks for Save/Discard/Cancel. The prompt has Discard and Keep editing. A
   Save button in the prompt would have to call each editor's submit and its
   error path. Recommended: keep two buttons, and correct the contract
   sentence.
2. **The capture review in the Release build (#124).** Should the scripted
   journeys ship in the product executable? Which journeys can be deleted
   because their findings are already recorded?
3. **Close #25?** The title bar no longer has a language button, and Settings
   names each language in its own language. What remains of #25 is removed by
   #119.

## Rejected

| Proposal | Reason |
| --- | --- |
| Typed pipe messages | #12 decided against it. A typed record would make a missing key a silent default; today `GetProperty` fails loudly. |
| A shared interface for drafts | It adds about 25 lines to remove about 3. The four discard checks have different scopes. Use the same names (`HasDraft`, `CancelDraft`) when each owner next changes. |
| Moving source intake into `AddDraft` | #13 settled `ReceiveSources` in `MainViewModel`. The picker filter is a picker setting. |
| Regrouping the `MainViewModel` partial files | They share all private state, so moving code between them changes no owner. |
| A class for each kind of setting | Five types to replace about three switch arms. One enum does it. |
| One DataTemplate for each kind of setting row | Saves about 40 attribute lines, but each AutomationId then needs a new source. |
| Moving Explorer and clipboard requests into the view model | Saves about 6 lines and fixes no failure. Architecture puts these in WinUI. |

## What is not verified

- Nothing was built or run. All findings come from reading the source and the
  uncommitted diff on 2026-10-06.
- The XAML text binding (#121) is verified on paper only. The first step of
  #121 is one compile of FileForm and a language switch at runtime.
- Binding the selection of literal `ComboBoxItem`s with `SelectedValuePath`
  is not verified (#119). A list of choice objects avoids the question.
- The table's `SelectionChanged` timing, which decides whether the model's own
  prune can be deleted, is not verified (#116).
- The line counts are estimates from reading the code.
