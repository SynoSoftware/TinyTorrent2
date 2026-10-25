# Libtorrent indexing capabilities

Research checked on 2026-10-24 and 2026-10-25 against the dependency pinned in
[Dependencies.ps1](../../engine/src/Dependencies.ps1):
`SynoSoftware/libtorrent` commit `464f12919bee03de42e56fe1c891a9d1772bc913`.
The local checkout matches that commit and reports version `2.1.2.0`.
This records dependency capabilities; it does not specify a new product feature.

## Discovery and metadata

An infohash identifies a torrent's metadata. It does not contain a readable title
or file list. The three protocols provide successive parts of an indexing workflow:

| Protocol | Meaning | Libtorrent support |
| --- | --- | --- |
| [BEP 5: DHT](https://www.bittorrent.org/beps/bep_0005.html) | A distributed directory mapping known infohashes to peers. | Automatic peer discovery for eligible torrents; explicit `dht_get_peers()` and `dht_announce()`. |
| [BEP 51: DHT infohash indexing](https://www.bittorrent.org/beps/bep_0051.html) | Ask a DHT node for a sample of the hashes for which it stores peer announcements. | Both serving requests and querying nodes through `dht_sample_infohashes(endpoint, target)`. |
| [BEP 9: metadata exchange](https://www.bittorrent.org/beps/bep_0009.html) | Obtain a torrent's metadata from peers when starting with a hash or magnet. | The default `ut_metadata` plugin downloads and serves metadata; completion produces `metadata_received_alert`. |

[Session APIs](https://libtorrent.org/reference-Session.html#dht_sample_infohashes%28%29)
and [alerts](https://libtorrent.org/reference-Alerts.html#dht_sample_infohashes_alert)
expose the sampling result: hashes, additional DHT nodes, the responding node's
stored-hash count and its refresh interval. The target selects nearby routing
nodes, not matching hashes. Each call queries one endpoint; the application owns
the traversal and respects each remote node's returned interval. The local
default responder returns at most 20 hashes per sample and advertises a
21,600-second refresh interval; these settings do not control remote nodes.

The resulting workflow is: sample hashes with BEP 51, locate peers with BEP 5,
then retrieve metadata with BEP 9. The application stores and searches the names
and file lists it obtains. BEP 51 explicitly provides no direct search for a
specific torrent, and sampling does not guarantee complete network coverage.

BEP 9 transfers the info dictionary, not the original complete torrent file.
It supplies names, paths and lengths but does not recover original top-level
tracker lists, comments or creation dates. Resolution needs a reachable peer
that can provide metadata; observing a hash alone does not guarantee resolution.

For metadata-only retrieval, the session API documents `upload_mode`; clear
`auto_managed` so queue management cannot remove that mode, and keep the torrent
active. Read the result through `torrent_handle::torrent_file()` after the
completion alert. The local engine already has its own authoritative preview
guard in [Additions.cpp](../../engine/src/Engine/Additions.cpp), using
`default_dont_download` and unwanted file priorities; these are alternative
library mechanisms, not a reason to add a second product implementation.
`stop_when_ready` is unsuitable for waiting until metadata arrives because
metadata acquisition itself counts as downloading. See the
[flag documentation](https://libtorrent.org/reference-Core.html#torrent_flags_t).

## Metadata preservation boundaries

The pinned [torrent_info interface](../../3rdParty/libtorrent/include/libtorrent/torrent_info.hpp)
exposes exact info bytes through `info_section()`, the original file layout
through `layout()`, and verified v1/v2 identities through `info_hashes()`.
[file_storage::root()](../../3rdParty/libtorrent/include/libtorrent/file_storage.hpp)
returns an available per-file SHA-256 Merkle root, not a flat file checksum.
Extracted display names and file sizes alone cannot reproduce the original
info bytes or preserve all attributes for later download preparation.

The [metadata receiver](../../3rdParty/libtorrent/src/torrent.cpp) can validate
a sampled 20-byte key against a truncated v2 hash before revealing full
identities. A lookup key is therefore not proof of a v1 identity. Learning a
hybrid alias can also raise `torrent_conflict_alert` and pause conflicting
handles; crawler integration must account for existing transfer ownership.

[load_torrent.cpp](../../3rdParty/libtorrent/src/load_torrent.cpp) accepts absent
v2 piece layers and validates layers that are present. BEP 9 acquisition does
not establish that those outer layers or other complete-torrent fields were
obtained. Missing layers do not erase the filenames and roots already available
from the info dictionary.

Acquisition and standalone loading have different default bounds. The session's
[max_metadata_size](../../3rdParty/libtorrent/src/settings_pack.cpp) is 30 MiB,
and metadata reception decodes at depth 200. The standalone loader limits default
to a 10,000,000-byte file ceiling and depth 100. The file-size check belongs to
file loading; buffer loading still applies its decode limits. Local archive
reconstruction must use matching acquisition bounds and the dictionary-specific
`from_info_section` path, rather than reject accepted metadata through a smaller
default loader or treat an info dictionary as a complete torrent.

## Other facilities relevant to an indexer

| Facility | Use and boundary |
| --- | --- |
| Passive discovery | `dht_get_peers_alert` exposes hashes other nodes ask this node about; `dht_announce_alert` exposes hashes and announced peer endpoints. Enable the `dht` alert category. This sees traffic reaching this node, not all network traffic. [Alerts](https://libtorrent.org/reference-Alerts.html#dht_get_peers_alert). |
| [BEP 33: DHT scrapes](https://www.bittorrent.org/beps/bep_0033.html) | The local node serves Bloom filters for approximate seed/downloader populations. For querying, `dht_direct_request()` can send `get_peers` with `scrape=1`; application code must aggregate and interpret the returned filters. The public `dht_get_peers()` call has no scrape option. [Storage contract](https://libtorrent.org/reference-DHT.html#get_peers%28%29). |
| Tracker scrapes | `torrent_handle::scrape_tracker()` and scrape alerts expose tracker-reported seeders, downloaders and completed downloads for a known torrent. This enriches known entries rather than discovering arbitrary hashes. [Handle API](https://libtorrent.org/reference-Torrent_Handle.html#scrape_tracker%28%29). |
| BEP 32: IPv6 DHT | Dual-stack DHT support allows discovery over IPv4 and IPv6. [DHT state](https://libtorrent.org/reference-DHT.html#dht_state). |
| BEP 44: DHT items | `dht_get_item()` and `dht_put_item()` support immutable and signed mutable values. They can support an application's own small records; they do not supply an existing searchable torrent catalogue. [Session APIs](https://libtorrent.org/reference-Session.html#dht_get_item%28%29). |
| Custom integration | `dht_live_nodes()`, `dht_direct_request()` and `dht_storage_interface` expose routing contacts, custom RPCs and storage integration. Default DHT peer storage is an in-memory routing responsibility, not a durable metadata index. [Session](https://libtorrent.org/reference-Session.html), [storage](https://libtorrent.org/reference-DHT.html#dht_storage_interface). |

The current engine configures DHT bootstrap nodes, applies the person's DHT
setting and handles magnet metadata. Its inspected sources contain no calls to
`dht_sample_infohashes()` or passive indexing handlers. Libtorrent supplies the
network primitives; crawling policy, deduplication, metadata job limits,
persistence, search and catalogue presentation remain application work.

The [feature specification](../plans/14-torrent-indexing-plan.md) owns proposed
TinyTorrent behavior. The [Rats Search and Bitmagnet inventory](../plans/14-torrent-references.md)
compares other applications, whose protocol support, limits and preservation
choices differ from this dependency's capabilities.

Validation was source and documentation inspection. No build, network crawl or
metadata download was performed, so no discovery rate or coverage was measured.
