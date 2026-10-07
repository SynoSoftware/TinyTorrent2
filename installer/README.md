# Generate an installer

Double-click `Release.cmd` in the repository root. It generates the installer,
then opens `artifacts/release/`. There are no setup questions. The first run
downloads and verifies a portable Inno Setup compiler and Microsoft runtime
installers; later runs reuse that cache. Visual Studio, the .NET SDK, and the
owner's compiled `3rdParty/` dependencies must already be available.

From PowerShell, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\Build.ps1
```

The shared version lives in `Directory.Build.props`. Each run reserves the next
build number before compiling: `0.1.0.1`, `0.1.0.2`, and so
on. The engine, UI, installer, and filename all use that same number. The command
updates the shared file, so commit it with the release; cleaning `artifacts/`
does not reset the count. A failed build consumes its number too.

To move to another product version:

```powershell
.\Release.cmd -Version 0.2.0
```

That starts at `0.2.0.1`; subsequent runs continue incrementing its build number.

The command builds Release x64 for the engine and publishes the product UI. It
uses a free artifacts lane when an application or another build occupies the
default output. It packages a fresh publish directory, checks that both
executables have the requested version, includes licence notices, and writes
the installer and its SHA-256 checksum to `artifacts/release/`. Logs and staging
files remain under `artifacts/installer/` if something needs investigating.

## Signing

Without a signing certificate the filename ends in `-unsigned.exe`, for local
testing. To sign the application, installer, and uninstaller, supply the
thumbprint of a code signing certificate in your Windows Current User
certificate store:

```powershell
.\Release.cmd -Certificate YOUR_40_CHARACTER_CERTIFICATE_THUMBPRINT
```

Alternatively, set the `TINYTORRENT_CERTIFICATE` environment variable once.
Signing uses Windows SDK SignTool with SHA-256 and a timestamp. The certificate
and its private key stay in the certificate store.

## What the installer does

Setup installs for the current user under Local App Data. It checks compatible
.NET, Windows App Runtime, and Visual C++ installations and downloads only
missing prerequisites from the verified Microsoft URLs pinned into this
installer. Internet access is needed when a prerequisite is missing; Microsoft
prerequisite installers can request administrator approval. Cancelling or a
failed runtime check stops setup before application files are replaced.

First installation offers torrent/link handling and optional sign-in startup.
Upgrades preserve those choices, request the engine's coordinated Exit, and
resume background transfers if TinyTorrent was running. Setup asks the installed
engine to exit before replacing it, so shutdown uses that release's pipe protocol.
Uninstall uses the
same Exit and registration owners and preserves saved state, downloads, and
shared runtimes. The [installation contract](../docs/architecture.md#installation-and-updates)
owns these decisions.

Generating an installer does not publish it or establish product release
acceptance. Before publication, validate fresh installation with missing
runtimes, upgrade with active transfers and an unfinished edit, cancellation,
and uninstall on a disposable Windows machine. The command does not launch
TinyTorrent or run these installed journeys.
