# TinyTorrent2

A native Windows torrent client built around libtorrent and WinUI 3.

The target has two processes: a native engine that owns downloads and the tray,
and a WinUI application that runs only while its window is open. TableView supplies
the single reusable table implementation.

Start with the [documentation guide](docs/README.md). It gives the reading order,
the authority for each decision, and the distinction between current code and
planned work.

## Generate an installer

Double-click `Release.cmd`. It builds and generates an installer, then opens
`artifacts/release/`. No configuration questions are required; without a signing
certificate, the installer is labelled unsigned for testing. See the
[release instructions](installer/README.md) for signing, versions, and prerequisites.
Each run advances the shared version's build number, starting with `0.1.0.1`.

## Current baseline

The reusable control, its development hosts, and its documentation are under
`lib/TableView/`; the Lucide icon font it uses is under `lib/Lucide/`. Open
[TinyTorrent.slnx](TinyTorrent.slnx) for the native and managed projects. The
engine scaffold creates a libtorrent session and exits; torrent operations and
the product UI under `app/` remain unimplemented. The existing
[build integration](docs/architecture-current.md#build-integration) routes build
output to `artifacts/`.

The original repository, `../TinyTorrent`, remains the reference for earlier
native presentation work and is left untouched. Transmission and its client
architecture are outside the new product. Reuse code only after checking its
responsibility against the [architecture](docs/architecture.md).
