# Product WinUI

Follow the [root instructions](../AGENTS.md). This directory holds the
TinyTorrent product UI described by the [architecture](../docs/architecture.md).
The reusable table and its development hosts live in
[lib/TableView](../lib/TableView/AGENTS.md).

Table design belongs to the
[TableView visual contract](../lib/TableView/docs/tableview-contract.md#8-rendering-layout-and-visual-language).
The app consumes that design rather than defining another table appearance,
so its tables remain consistent with every other library host.

Before changing product screens, read the [interface contract](../docs/interface.md).
Text and formatting follow [localisation](../docs/localisation.md); test scope
follows [testing](../docs/testing.md).

Reference [TableView.csproj](../lib/TableView/src/TableView.csproj). Keep torrent
commands, persistence, and product settings in their product owners; the
library owns generic table mechanics. Build affected WinUI projects with Visual
Studio MSBuild.

## Dialogs

Every dialog is a `Controls.Dialog` that `MainWindow` opens through `Interact`
and then `ShowDialog`, or `ShowEditor` when the primary button saves a draft.
These owners hold what every dialog shares, so a dialog states only its title,
content, buttons and outcome:

- `Dialog` places the buttons, makes the primary button the default, sets
  focus when the dialog opens, and lifts the size limit through `SizeToContent`.
- `Interact` opens one dialog at a time and none while the window closes,
  except the question about an unfinished draft. WinUI throws when a second
  dialog opens, for example when Exit arrives from the tray.
- `ShowDialog` sets the window root, the theme and the Cancel text, and
  refreshes the dialog's text when the language changes.
- `ShowEditor` takes the dialog's `IDraft`. It enables the primary button from
  `CanSubmit`, submits the draft, and keeps the dialog open while the draft is
  pending or after a refused submit. `SaveOnClose` saves an editor's typed
  input when the window closes.

A view other than `MainWindow` raises an event and lets `MainWindow` open its
dialog, as `SettingsPage.ProxyRequested` does, so the one-dialog rule holds.
When a dialog needs something these owners lack, add it to the owner, because a
copy in one dialog drifts from the others.

Follow [dialog prose and facts](../docs/interface.md#text-icons-and-typography)
when choosing a plain string or a `Lines` list for the body. Save and Cancel come
from the `dialog` text section, so every dialog uses the same word. Button words,
icons, tooltips, colour, placement and the default button
follow the [button rulings](../docs/interface.md#buttons).

## Capture review

A capture review is a diagnostic build that drives the app off-screen through
scripted journeys. It saves screenshots and control bounds in English and
Spanish, Light and Dark, at several window sizes. It does not take over the
desktop, so you may run one without a request when the change earns it.

It has found text and controls clipped or pushed out of view at narrow sizes or
in Spanish, input and drafts lost, and crashes while the window closes. Run one when
your change could cause such a defect in an area a mode covers. A change that
leaves layout, dialogs, input handling and window lifetime alone does not earn
one.

Each review costs a separate build and a run, and both hold the owner's
machine. Finish the edits first, choose the narrowest mode that covers the
change, and run it once. Look at the screenshots of the changed area, because
most layout defects were found there and not by the journeys' own checks.
[Testing](../docs/testing.md#windows-evidence)
lists the modes and how to build and launch them.
