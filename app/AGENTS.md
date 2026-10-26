# Product WinUI

Follow the [root instructions](../AGENTS.md). This directory holds the
TinyTorrent product UI described by the [architecture](../docs/contracts/architecture.md).
The reusable table and its development hosts live in
[lib/TableView](../lib/TableView/AGENTS.md).

Table design belongs to the
[TableView visual contract](../lib/TableView/docs/tableview-contract.md#8-rendering-layout-and-visual-language).
The app consumes that design rather than defining another table appearance,
so its tables remain consistent with every other library host.

Before changing product UI, read the [shared UI contract](../docs/contracts/ui.md)
and the sections linked from its [change index](../docs/contracts/ui.md#read-for-the-change)
for the affected surfaces and interactions.
Text and formatting follow [localisation](../docs/contracts/localisation.md); test scope
follows [testing](../docs/agents/testing.md).

Before changing Settings navigation, read its
[icon and label owner ruling](../docs/contracts/ui/settings.md#settings-navigation), so the shared
24-DIP icons and visually centred labels survive styling and template changes.

Reference [TableView.csproj](../lib/TableView/src/TableView.csproj). Keep torrent
commands, persistence, and product settings in their product owners; the
library owns generic table mechanics. Build through
[compilation](../docs/agents/compilation.md).

## Design and review

Describe the affected layout, information order, commands, states, sizing, and
keyboard paths before writing production UI. Review them from user,
product-design, Microsoft Fluent, and keyboard-only perspectives, with depth
matched to the magnitude of the change: a wording fix needs no new screen
design, and a substantial new journey gets independent review, because
building in the wrong direction costs far more than the review. Resolve
concrete findings before implementation.

Review both clutter and missing essentials: every visible element should support
a decision, and every required task needs a discoverable path. Review layout,
native semantics, reachable actions, focus return, automation, live text and
bounded hidden work in the code first. Apply the
[verification gate](../docs/agents/testing.md#verification-must-answer-a-question)
before adding tests, launching the app or capturing screenshots. Review relevant
sizes, themes, text scales and input methods only where a concrete doubt remains;
an ordinary UI change does not require a runtime matrix. State the evidence and
any unresolved questions accurately.

## Dialogs

Every dialog is a `Shared.Dialog` that `MainWindow` opens through `Interact`
and then `ShowDialog`, or `ShowEditor` when the primary button saves a draft.
These owners hold what every dialog shares, so a dialog states only its title,
content, buttons and outcome:

- `Dialog` places the buttons and the one `DialogFooter` item, makes the
  primary button the default, sets focus when the dialog opens, and lifts the
  size limit through `SizeToContent`.
- `Interact` opens one dialog at a time and none while the window closes,
  except the question about an unfinished draft. WinUI throws when a second
  dialog opens, for example when Exit arrives from the tray.
- `ShowDialog` sets the window root, the theme and the Cancel text, and
  refreshes the dialog's text when the language changes.
- `ShowEditor` takes the dialog's `IDraft`. It enables the primary button from
  `CanSubmit` except while the window closes, submits the draft, and keeps the
  dialog open while the draft is pending or after a refused submit. When the
  dialog closes, it stops the draft's running work and discards the draft.
  When the window closes, it applies the draft's `ClosePolicy`: save, discard,
  or ask, as Add and Move do.

A view other than `MainWindow` raises an event and lets `MainWindow` open its
dialog, as `SettingsPage.ProxyRequested` does, so the one-dialog rule holds.
When a dialog needs something these owners lack, add it to the owner, because a
copy in one dialog drifts from the others.

Follow [dialog prose and facts](../docs/contracts/ui.md#text-icons-and-typography)
when choosing a plain string or a `Lines` list for the body. Save and Cancel come
from the `dialog` text section, so every dialog uses the same word. Button words,
icons, tooltips, colour, placement and the default button
follow the [button rulings](../docs/contracts/ui.md#buttons).

## Capture review

A capture review, when justified by the
[verification gate](../docs/agents/testing.md#verification-must-answer-a-question),
is a diagnostic build that drives the app off-screen through
scripted journeys. It saves screenshots and control bounds in English and
Spanish, Light and Dark, at several window sizes. It does not take over the
desktop. The [root owner ruling](../AGENTS.md#working-rules) governs whether a
review may run; the [pre-launch check](../docs/agents/compilation.md#verify-the-build-before-launch)
establishes which successful build the capture will actually run.

It has found text and controls clipped or pushed out of view at narrow sizes or
in Spanish, input and drafts lost, and crashes while the window closes. Choose
the smallest mode that answers the remaining question.

Reuse a current capture build across review modes; another mode alone needs no
build. [Testing](../docs/agents/testing.md#windows-evidence) owns validation scope and
the available journeys. When a visual doubt justified a capture, inspect the
relevant images to resolve that doubt; running the journey alone cannot do so.
