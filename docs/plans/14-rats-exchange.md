# Rats catalogue exchange

Working plan, 2026-10-26. The engine receives Rats Search catalogue records
over Rats' own protocol and makes them searchable at once. It builds on
[Rats compatibility](14-rats-catalogue-compatibility.md) and the byte-level
[Rats wire protocol](14-rats-wire-protocol.md); the
[catalogue contract](../contracts/indexing.md) receives the rules below when the
work lands.

## Owner decisions

- TinyTorrent writes its own receive-only client over TCP and links no librats
  ([implementation direction](14-rats-catalogue-compatibility.md#implementation-direction)).
- Rats protocol code lives in its own folder and is not intertwined with
  engine code. The engine owns everything Rats shares with other collection:
  scheduling, routing, storage, rules and presentation.
- Rats records appear in Search at once, marked as not verified. Downloading one
  fetches the real metadata first through ordinary Add, which then shows the
  verified file list. There is no background verification.

## Status

Implemented and checked on 2026-10-26. On loopback against released Rats
Search 2.3.5, a sharing peer's snapshot gave all 12 fixture torrents and a
non-sharing peer one batch of 10, all searchable as unverified. In a 20-minute
live run, the DHT gave 84 Rats peer addresses; 64 refused or did not answer,
and two delivered 1,503 records in ten minutes. What remains is the Search
marker seen on screen. The behaviour now lives in the
[catalogue contract](../contracts/indexing.md#rats-search-exchange); this plan
holds only what is still open. Departures from the design below:

- No temporary file. The host acknowledges snapshot bytes only after their
  records are stored, and the sender's 4 MiB window bounds memory.
- `Connection` in the Rats folder runs one `Session` over a `Dialer` connection
  on the network thread; the engine thread owns every decision.
- The sync table holds only when each peer's snapshot was last requested.
- `crawler.json` accepts `rats.peers`, so a known peer, such as the local one
  in the check below, needs no DHT lookup.

## Design

### Rats folder: `engine/inc/Rats/`, `engine/src/Rats/`, namespace `tt::rats`

Pure protocol logic without sockets, timers or engine headers: bytes in,
bytes to send plus events out. It depends only on OpenSSL, nlohmann JSON and
Boost.Beast's header-only inflate, all already in `3rdParty`.

- `Session`: the initiator side of one connection. Block framing, Noise XX with
  the `rats-search/4` prologue, sealed inner messages, typed JSON, and the
  file-transfer receiver (accept, cumulative PROGRESS every 256 KiB, COMPLETE).
  It reads and ignores every unsolicited server message, never sends a Storage
  sync request, and refuses blocks above the protocol's 64 MiB limit.
  Events: the peer's `client_info`, a `randomTorrents` batch, snapshot
  negotiation state, snapshot bytes, transfer complete or failed.
- `Snapshot`: an incremental `.ratsdb` reader. It checks magic, version and
  frame lengths, inflates each Qt `qCompress` frame with a bound on expanded
  bytes, and yields validated records. A truncated file keeps its complete
  frames.
- `Record`: v1 hash (40 hex), title, size, files as path and size, and the
  peer-claimed seeders and leechers. File order is not a torrent file index.

### Engine host: `engine/src/Engine/Rats.cpp`

- Routing: the proxy and adapter TCP dial moves out of `MetadataRequester` into
  one shared owner that both the requester and the Rats host use, so every
  connection obeys the saved route.
- Peers: while collection runs, the host looks up
  `278903a7e96d10ceec7ddefb8b9bcdd36305346c` with the session's DHT about every
  ten minutes. `Alerts.cpp` routes replies for that hash to the host, not the
  crawler. The host connects to at most two peers at once.
- Exchange: from each peer, one snapshot when its `client_info` shows sharing
  and that peer's last snapshot is older than seven days; otherwise one
  `randomTorrents` batch. The host imports records in bounded batches on the
  catalogue worker and stops importing at the catalogue budget.
- Collection on/off, pause, DHT, route and Exit apply as for the crawler.

### Catalogue

- An unverified record is a `catalogue` row with no `metadata` row. Its files
  go straight into the search tables at import and are published at once.
- Import skips hashes the catalogue already holds, verified or not. A record
  matching an exclusion rule is skipped and not remembered as rejected,
  because a peer's claim must not ban the real torrent.
- Triage treats a row without metadata as missing, so the crawler can still
  verify it. `Admit` on such a row replaces its name, size, count and files
  with the verified ones.
- Rebuild keeps rows without metadata and their files. Detail reads without
  extras. Preparing Add from an unverified row is refused; the app opens
  ordinary Add by hash instead.
- Schema version 5 adds a per-peer sync table (peer ID, last snapshot time,
  snapshot name). Saved catalogues migrate without losing data.

### Pipe and app

- Hits, sources and detail carry `verified`. Protocol 31.
- Search marks unverified results. For an unverified result both Download
  actions open ordinary Add by hash, where the person picks files from the
  verified list. Text changes ship in all six languages.
- `catalogue_status` adds Rats counters: peers found, connected, records
  imported.

## Work split and checks

| Piece | Owner | Files |
| --- | --- | --- |
| Rats folder and its unit checks | agent A | `engine/*/Rats/` |
| Catalogue rows, migration, CatalogueChecks | agent B | `Catalogue.*`, `engine/tests/CatalogueChecks.ps1` |
| Shared dial, Rats host, alerts, pipe, contracts | lead | `MetadataRequester.*`, new dial files, `Engine/*`, `docs/contracts/*` |
| Search UI and strings | agent C, after the pipe fields are fixed | `app/src/Search/*`, `app/src/Shared/Engine/Catalogue.cs`, `app/src/Resources/*.json` |

The lead builds once after review. Open questions that need the build:

1. Does our Noise client complete a handshake and receive a batch and a snapshot
   from a local released Rats v2.3.5 peer? Use the controlled setup in
   `artifacts/experiments/rats-compatibility-20261026/`.
2. Do unverified rows survive migration, rebuild and eviction, and upgrade on
   `Admit`? Answer it with focused CatalogueChecks cases.
3. Does Search show the marker and open Add by hash? Answer it with one capture
   of the catalogue review mode.

## Estimate

Two rounds: implementation plus review, then one build with checks and fixes.
About 3,000 lines across engine, app and tests, and about 8 to 12 hours of
agent time over one to two days of wall time.

Main risk: no independent Noise implementation has yet talked to a Rats peer.
Question 1 settles it.
