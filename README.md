# <img src="resources/tinyTorrent.svg" width="48" alt="" /> TinyTorrent2

[![License: MPL-2.0](https://img.shields.io/badge/License-MPL--2.0-orange.svg)](LICENSE)
![Platform: Windows x64](https://img.shields.io/badge/Platform-Windows_x64-blue.svg)
![UI: WinUI 3 · Fluent 2](https://img.shields.io/badge/UI-WinUI_3_%C2%B7_Fluent_2-8b5cf6.svg)
![Engine: libtorrent](https://img.shields.io/badge/Engine-libtorrent-darkgreen.svg)

**Full features without the bloat.**

TinyTorrent is a native Windows BitTorrent client with a beautiful WinUI 3
interface following Fluent 2 design. Manage downloads, control bandwidth, and
inspect your transfers in one place. Low memory use, a responsive interface,
and predictable behavior guide its development.

## Features — what you need, without the bloat

- **Add and organise.** Torrent files and magnet links, drag-and-drop, batch
  additions, metadata previews, file selection, priorities, and download queues.
- **Control transfers.** Pause, resume, force start, normal and alternative speed
  limits, scheduled limits and pauses, and seeding limits.
- **See what's happening.** Search and error filtering, file progress, peers,
  editable trackers, a pieces map, and speed history.
- **Manage your files.** Open folders, verify downloads, move files, and choose
  between removing a torrent and deleting its downloaded files.
- **Use a native interface.** Acrylic surfaces, light and dark themes, keyboard
  shortcuts, and live English and Spanish language switching.

## Close the window, free UI memory, keep downloading

The native libtorrent engine handles downloads and seeding. The interface runs
in a separate process and exits when you close its window, releasing its memory
while transfers continue.

TinyTorrent remains visible in the notification area. Use its tray menu to
reopen the window or choose **Exit** to stop transfers and close the application.
It runs as a desktop application, not a Windows service.

## Why I built it

I grew up using torrent clients that were around 160 KB. They were small, fast,
and did what I needed. Seeing how much memory even simple tray applications can
use made me want to build my own. qBittorrent's interface never appealed to me,
either. I wanted a full-featured client that looked good, felt native to Windows,
and was careful with resources.

I initially aimed for a 1 MB executable, but saving a few megabytes on a program
used to download gigabytes matters less than how well it runs. Background
transfers were already lean; the UI was where I needed to improve memory use,
speed, and responsiveness. That's why I chose a native WinUI 3 interface.

I chose libtorrent because, in my experience, it is more proactive than
Transmission about establishing connections at startup. Running Transmission
as a service also made it too easy to forget it was still active. For a desktop
torrent client, I prefer a visible application that I can exit whenever I need to.

## Download or build

Visit [GitHub Releases](https://github.com/SynoSoftware/TinyTorrent2/releases)
for published builds, or build the application yourself using the steps below.

### Build from source

You'll need Windows, Visual Studio, the .NET SDK, and the project's precompiled
`3rdParty/` dependencies.

Double-click [Release.cmd](Release.cmd). It builds Release x64 and opens
`artifacts/release/` with the generated installer. Without a signing certificate,
the installer is labelled unsigned for local testing.

See the [release instructions](installer/README.md) for setup, signing, and
versioning. Each run advances the shared build number, even if the build fails.

## Development

For development, open [TinyTorrent.slnx](TinyTorrent.slnx) and use **Debug x64**.
Build output goes to `artifacts/`.

The [documentation guide](docs/README.md) covers the architecture, engine,
interface, and testing. Read the [contribution instructions](AGENTS.md) before
making changes.

## Licence

The project code is licensed under the [Mozilla Public License 2.0](LICENSE).
Logos, artwork, and other branding assets are covered by a separate
[proprietary and trademark notice](resources/LICENSE).
