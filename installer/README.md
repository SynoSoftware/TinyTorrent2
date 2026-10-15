# Generate an installer

Double-click `Release.cmd` in the repository root. It generates the installer,
then opens `artifacts/release/`. There are no setup questions. The first run
downloads and verifies a portable Inno Setup compiler and Microsoft runtime
installers; later runs reuse that cache. Visual Studio, the .NET SDK, and the
owner's compiled `3rdParty/` dependencies must already be available.
UPX must be installed at `C:\Program Files\upx\upx.exe`.

From PowerShell, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\installer\Build.ps1
```

The shared version lives in `Directory.Build.props`. Each run reserves the next
build number before preparing tools or compiling: `0.1.0.1`, `0.1.0.2`, and so
on. The engine, UI, installer, and filename all use that same number. The command
updates the shared file, so commit it with the release; cleaning `artifacts/`
does not reset the count. A failed build consumes its number too.

The release command checks the file and product versions, product name,
publisher and description of both applications and the installer before placing
it in `artifacts/release/`. The publisher comes from the shared build properties;
the command checks all embedded icon images against `resources/TinyTorrent.ico`.
Capture diagnostics are disabled explicitly, and the command rejects debug or
capture compilation symbols and capture members in the published application.
Debug symbols, test hosts and capture evidence are not installer inputs.
The native Release build defines `NDEBUG` to exclude assertion diagnostics;
the command also rejects native debug compilation symbols and debug runtime settings.
The application references the WinUI components it uses rather than the whole
Windows App SDK, avoiding unused AI, machine-learning, widgets and search files.
The published executables must be `TinyTorrent.exe` and `TinyTorrentUI.exe`.
The publish step includes the UI resource index with its embedded compiled XAML;
the release command rejects missing application or library resource indexes,
the Lucide and flag fonts and the DB-IP country data, so published
installations keep their icons and peer flags. The app build fetches the month's
country data, as [peer location](../docs/peer-location.md) describes.
The command compresses the staged engine with UPX, preserves its resources
(including properties and icons), and verifies the compressed data before
signing and packaging. Build outputs stay uncompressed.

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

Signing is optional. The filename is `TinyTorrent-<version>-x64-Setup.exe`
with or without a signing certificate. To sign the application, installer, and
uninstaller, supply the thumbprint of a code signing certificate in your Windows
Current User certificate store:

```powershell
.\Release.cmd -Certificate YOUR_40_CHARACTER_CERTIFICATE_THUMBPRINT
```

Alternatively, set the `TINYTORRENT_CERTIFICATE` environment variable once.
Signing uses Windows SDK SignTool with SHA-256 and a timestamp. The certificate
and its private key stay in the certificate store.

## What the installer does

Setup installs for the current user under Local App Data. The installer uses
Inno Setup's smaller x86 engine in 64-bit install mode; TinyTorrent and its
prerequisites remain x64. It checks compatible .NET and Windows App Runtime
installations and downloads only
missing prerequisites from the verified Microsoft URLs pinned into this
installer. Runtime installers and shared runtime files are not bundled in the
release. Internet access is needed when a prerequisite is missing; Microsoft
prerequisite installers can request administrator approval. Cancelling or a
failed runtime check stops setup before application files are replaced.
Setup checks the installed components again after each Microsoft installer runs;
when Windows needs a restart to finish a prerequisite, Setup offers that restart
before application files are replaced.
Visual C++ redistributables are unnecessary: the engine uses a static CRT, and
the Windows App Runtime uses Microsoft's
[Hybrid CRT](https://github.com/microsoft/WindowsAppSDK/blob/main/docs/Coding-Guidelines/HybridCRT.md).

First installation offers torrent/link handling and optional sign-in startup.
Upgrades preserve those choices, request the engine's coordinated Exit, and
resume background transfers if TinyTorrent was running. Setup asks the installed
engine to exit before replacing it, so shutdown uses that release's pipe protocol.
If that executable is missing or cannot start, Setup uses its bundled engine to
request Exit instead. A refused Exit still stops installation to protect unfinished
input. Inno Setup handles remaining file locks through Windows Restart Manager.
Optional handler or startup registration failures leave the app installed and
usable, with those choices available again in Settings. Uninstall uses the same
Exit and registration owners and preserves saved state, downloads, and shared
runtimes. Missing program files or failed optional registration cleanup do not
prevent removal. The [installation contract](../docs/architecture.md#installation-and-updates)
owns these decisions.

Generating an installer does not publish it or establish product release
acceptance. Before publication, validate fresh installation with missing
runtimes, upgrade with active transfers and an unfinished edit, cancellation,
and uninstall on a disposable Windows machine. The command does not launch
TinyTorrent or run these installed journeys.
