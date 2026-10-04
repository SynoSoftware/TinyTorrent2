# TinyTorrent2

A native Windows torrent client built around libtorrent and WinUI 3.

The target has two processes: a native engine that owns downloads and the tray,
and a WinUI application that runs only while its window is open. Synapse supplies
the single reusable table implementation.

Start with the [documentation guide](docs/README.md). It gives the reading order,
the authority for each decision, and the distinction between current code and
planned work.

## Current baseline

The reusable code is under `winui3/`: Synapse, its sample, and its control tests.
Open [Synapse.slnx](winui3/Synapse.slnx) to inspect them. The new engine and product
UI have not been implemented; the sample is a control demonstration.

The original repository, `../TinyTorrent`, remains the reference for earlier
native presentation work and is left untouched. Transmission and its client
architecture are outside the new product. Reuse code only after checking its
responsibility against the [architecture](docs/architecture.md).
