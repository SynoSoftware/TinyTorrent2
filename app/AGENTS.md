# Product WinUI

Follow the [root instructions](../AGENTS.md). This directory will hold the
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
Studio MSBuild. Launch the product only when explicitly requested, except for
a capture review.

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
