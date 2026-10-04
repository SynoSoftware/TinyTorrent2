# Product WinUI

Follow the [root instructions](../AGENTS.md). This directory will hold the
TinyTorrent product UI described by the [architecture](../docs/architecture.md).
The reusable table and its development hosts live in
[lib/TableView](../lib/TableView/AGENTS.md).

Before changing product screens, read the [interface contract](../docs/interface.md).
Text and formatting follow [localisation](../docs/localisation.md); test scope
follows [testing](../docs/testing.md).

Reference [TableView.csproj](../lib/TableView/src/TableView.csproj). Keep torrent
commands, persistence, and product preferences in their product owners; the
library owns generic table mechanics. Build affected WinUI projects with Visual
Studio MSBuild. Launch the product only when explicitly requested.
