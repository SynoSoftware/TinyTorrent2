# Faster torrent indexing

Research, 2026-10-26. This records protocol, source and measurement findings
about collection speed. It authorizes no implementation and does not change the
[catalogue contract](../contracts/indexing.md). No crawler, application, build,
benchmark or repeated third-party request ran for this investigation; each
external documentation page was read once.

## Answer

No order-of-magnitude faster way was found that keeps the hash-verified info
dictionary and the proxy rule at an acceptable cost.

- The crawler is limited by its peer-lookup stage, not by metadata download.
  In the measured warmed run, all 200 peer-lookup slots were busy, while
  metadata used at most 177 of its 400 slots. Only 8% of the hashes it looked
  up became a verified dictionary.
- The design has some unmeasured tuning headroom (more lookup slots, not
  holding a slot while it waits for per-node pacing). Any gain from it costs
  about the same factor in network traffic. Ten times today's rate needs about
  400 outgoing TCP connection attempts per second from the person's computer.
- Bitmagnet's own documented best case, 1,000 torrents per minute, is 6.5 times
  our measured 153 per minute, and Bitmagnet publishes no measurement for it.
- Bulk sources exist (magnetico and Bitmagnet databases, Rats Search
  snapshots), but none of them keeps the info dictionary. They can fill search
  quickly; they cannot fill the archive.
- HTTP torrent caches return complete `.torrent` files that we can verify
  against the hash. Their coverage, terms and limits are unknown, and every
  lookup tells a third party which hash we want. Their coverage cannot be
  measured without the bulk automated requests that this research excludes.
- Passive harvesting with many node IDs is the older method that BEP 51 was
  written to replace. Current Bitmagnet and magnetico do not use it, and it
  cannot run inside the shared libtorrent session.

## Why collection goes from hash to peers

Collection looks up peers for a hash because the network offers no lookup in
the other direction, from a peer to its hashes:

- **A peer names no inventory.** The handshake selects one hash, and BEP 9
  serves only that torrent; no standard message lists or switches torrents
  ([multiple torrents per peer contact](14-peer-metadata-research.md)).
  Reconnecting with guessed hashes succeeds only for hashes that peer holds.
- **The DHT is keyed by hash.** A node stores peers for the hashes closest to
  its own ID ([BEP 5](https://www.bittorrent.org/beps/bep_0005.html)), so one
  peer's torrents are spread across unrelated nodes and no query returns them.
  A BEP 51 sample is hashes from that directory, still without live peers.

Two sources do give hashes from a peer. Cooperating clients such as Rats Search
list their catalogue over their own protocol
([Rats compatibility](14-rats-catalogue-compatibility.md)). An incoming
`announce_peer` gives one hash together with the peer that just announced it;
feeding that peer to metadata is the announce row in the comparison below.

## Terms used here

- **Sample:** a BEP 51 `sample_infohashes` answer. A DHT node returns some
  hashes that it stores peers for.
- **Peer lookup (probe):** a BEP 5 `get_peers` query for one hash, sent to the
  node that sampled it.
- **Slot:** one unit of the crawler's concurrency limit. A job holds a slot
  until it finishes.
- **Dictionary:** the BEP 9 info dictionary whose SHA-1 equals the hash.

## Where the time goes

### Measured stage counts

The warmed translated-crawler run is the best recorded evidence. These are the
differences between the status snapshots at the start and end of its 600-second
collection window, after 600 seconds of warm-up
(`artifacts/measurements/catalogue-20261025/bitmagnet-live-warmed-20261026/summary.json`,
`collection_start` and `collection_end`; run described in
[discovery feedback](14-discovery-feedback.md), "completed warmed port
evidence").

| Stage | Count in 600 s | Rate or ratio |
| --- | ---: | ---: |
| Hashes in samples | 20,035 | 33.4/s |
| Peer lookups sent | 18,729 | 31.2/s |
| Lookups that returned peers | 7,737 | 41% of lookups |
| Metadata jobs started | 7,732 | 3.1 peer attempts per job |
| TCP connection attempts | 23,742 | 39.6/s |
| Attempts that failed to connect | 18,742 | 79% of attempts |
| Peers that supported `ut_metadata` | 1,790 | 7.5% of attempts |
| Dictionaries received | 1,496 | 2.5/s; 19% of jobs; 8.0% of lookups |
| Slots busy, peer lookup | 200 of 200 at both snapshots; peak 200 | full |
| Slots busy, metadata | 104 and 86 at the snapshots; peak 177 of 400 | not full |

The other failure stages in the same window were handshake 821, extension
2,389, receive 287 and validation 7. The cold default run shows the same shape:
peak lookup 200 of 200, peak metadata 131 of 400, and 11,476 of 14,558 attempts
failing at connect ([discovery feedback](14-discovery-feedback.md), "acquisition
evidence at collection end"). SQLite admission is not the limit: earlier
measured admission took a median of 0 ms and a maximum of 2 ms
([acquisition diagnosis](14-acquisition-diagnosis.md), "latency, pressure and
failure interpretation").

### Why the lookup stage is full

These are source facts. Their share of the measured slot time was not recorded.

1. **A lookup holds its slot while it waits for per-node pacing.** The crawler
   sends at most one DHT query per second to one IP address, after a burst of
   four (`engine/src/BitmagnetCrawler.cpp:348–369`). A lookup that pacing
   refuses stays in the active set and is retried on the next tick
   (`BitmagnetCrawler.cpp:275–279`, `:371–373`, `:545–563`). Every lookup goes
   to the node that sampled its hash (`BitmagnetCrawler.cpp:626–628`), and a
   libtorrent node returns up to 20 hashes per sample
   (`3rdParty/libtorrent/src/settings_pack.cpp:378`). Twenty lookups to one
   node therefore need about 16 seconds, and the average one waits about
   7 seconds before it is sent. The measured average slot time is
   200 slots ÷ 31.2 lookups/s ≈ 6.4 seconds, which agrees with this, but the
   run did not record pacing wait for DHT queries, so the cause is not proven.
   Upstream Bitmagnet does the same: its get_peers worker blocks in a 1/s,
   burst-4 per-IP limiter
   ([server/factory.go:67](https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/protocol/dht/server/factory.go#L67),
   [server/limiter.go:24–35](https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/protocol/dht/server/limiter.go#L24)).
2. **Samples are older than the peer lists behind them.** Libtorrent recomputes
   its sample at most every 21,600 seconds (6 hours) by default
   (`3rdParty/libtorrent/src/settings_pack.cpp:377`,
   `src/kademlia/dht_storage.cpp:565`), but removes a stored peer 45 minutes
   after its announce (`dht_storage.cpp:66`, `:544`). A sampled hash can
   therefore have no peers left at that node. This is consistent with 59% of
   lookups returning no peers. Bitmagnet then drops the hash
   ([get_peers.go:81–82](https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/dhtcrawler/get_peers.go#L81));
   our translation does the same (`BitmagnetCrawler.cpp:518–522`).
3. **Most announced peers cannot be reached by plain TCP.** The requester dials
   IPv4 TCP with a 3-second budget (`engine/src/MetadataRequester.cpp:290`,
   `engine/src/Engine/Bitmagnet.cpp:95`), as Bitmagnet does
   ([metainforequester/factory.go:36](https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/protocol/metainfo/metainforequester/factory.go#L36)).
   73% of attempts in the window lasted 3–6 seconds. Because metadata slots
   were never full, this latency costs connections, not throughput, at today's
   lookup rate.

The sampling stage was not the limit in this run. When the lookup queue is
full, an answered sample cannot pass on its hashes and keeps its own slot
(`BitmagnetCrawler.cpp:500–525`), so sampling slowed to match lookups. About
3,000 candidates were queued at both snapshots, and the database held 40,700
pending hash observations at the end.

### What ten times would require

At the measured conversion, 25 dictionaries per second needs:

| Quantity | Today | 10 × |
| --- | ---: | ---: |
| Peer lookups | 31/s | about 310/s |
| Metadata jobs | 13/s | about 130/s |
| TCP connection attempts | 40/s | about 400/s |
| Sample answers (about 20 hashes each) | about 1.7/s | about 17/s |

At libtorrent's 6-hour interval, 17 sample answers per second means about
370,000 different sampling nodes in rotation. The crawler's routing table held
636 nodes at the end of the warmed run. BEP 51 states the design goal that
one node can survey the whole DHT within a few hours
([BEP 51](https://www.bittorrent.org/beps/bep_0051.html)), so that number of
nodes is plausible, but no source measures how many DHT nodes answer BEP 51.
Duplicates also rise as the catalogue grows, so the rate of new torrents falls
over time; Bitmagnet's FAQ says the same
([faq.md:58](https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/bitmagnet.io/faq.md?plain=1#L58)).

## Approaches compared

"Unmeasured" means no primary source or measurement gives a rate.

| Approach | Expected rate and source | Verified dictionary? | Proxy and adapter | Cost and risk |
| --- | --- | --- | --- | --- |
| Current crawler, scaling factor 10 | Measured 2.5/s warmed, 1.1/s cold (above) | Yes | Yes: DHT uses the session; the requester uses the confirmed route (`Bitmagnet.cpp:143–171`) | Baseline |
| Upstream Bitmagnet itself | FAQ: "100 to 1,000 torrents per minute", depending on hardware, network and scaling factor; no method or measurement given ([faq.md:58](https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/bitmagnet.io/faq.md?plain=1#L58)). Not run on this machine. | Bitmagnet keeps no info bytes (below) | Not applicable | The top of the claim is 6.5 × ours |
| Larger `scaling_factor` | Unmeasured. Lookup slots are 20 × factor and metadata slots 40 × factor (`BitmagnetCrawler.cpp:259–273`). Upstream says returns may diminish above 10 and that it is not widely tested ([config.go:8–11](https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/dhtcrawler/config.go#L8)). | Yes | Yes | Already configurable in `crawler.json`, 1–1,000 (`Bitmagnet.cpp:60–62`). DHT and TCP traffic grow with the factor. |
| Do not hold a lookup slot during pacing wait, or spread one sample's lookups over time | Unmeasured. Could raise lookups per slot if pacing wait is most of the 6.4 s; not proven. | Yes | Yes | Small change in our translation; departs from upstream. Per-node load stays at 1 query/s. |
| Faster per-node pacing | Unmeasured. Libtorrent bans a node that sends more than 5 packets/s averaged over 10 s (`settings_pack.cpp:374–375`, `settings_pack.hpp` `dht_block_ratelimit`). Other implementations' limits were not checked. | Yes | Yes | Loads other people's nodes harder; risk of bans |
| Full iterative `get_peers` when the sampling node has no peers | Unmeasured. Applies to the 59% of lookups that return nothing; a stale sample may still have peers elsewhere. Libtorrent offers `dht_get_peers` without a torrent handle ([crawl strategy research](14-crawl-strategy-research.md), "what the existing libtorrent can do"). | Yes | Yes | Each lookup is many UDP queries; many of these swarms are probably dead |
| uTP and IPv6 in the requester | Unmeasured. Earlier libtorrent-crawler cohort: 18 verified with TCP+uTP versus 14 with TCP only, out of 128 hashes, in sequential public runs ([acquisition diagnosis](14-acquisition-diagnosis.md), "fixed random cohort"). The crawler parses IPv4 nodes and peers only (`BitmagnetCrawler.cpp:393–422`) and the requester dials TCP4 (`Bitmagnet.cpp:95`). | Yes | uTP is UDP: only a SOCKS5 proxy can carry it ([RFC 1928](https://www.rfc-editor.org/rfc/rfc1928)) | A uTP stack in the custom requester; small expected gain |
| Feed incoming announces (hash and announcing peer) to metadata | Unmeasured. Today `dht_announce_alert` keeps only the hash (`engine/src/Engine/Alerts.cpp:99–105`) although the alert carries the peer's address and port (`3rdParty/libtorrent/include/libtorrent/alert_types.hpp:1793–1795`), and these hashes only become pending observations (`engine/src/Engine/Indexing.cpp:375–383`). One node ID receives announces only for hashes near it; volume is not separately counted. | Yes | Yes | Small change; skips one lookup for each announce |
| Passive harvesting with many node IDs | No primary rate. BEP 51 says passive observation is inefficient and favours indexers with many IP addresses ([BEP 51](https://www.bittorrent.org/beps/bep_0051.html)). | Yes, after BEP 9 | Needs inbound unsolicited UDP; HTTP and SOCKS4 proxies carry no UDP; SOCKS5 relay of unsolicited datagrams not verified | Needs its own DHT stack and sockets outside the shared session; the protocol community treats it as abuse (below) |
| HTTP torrent cache by hash | Unmeasured; coverage unknown | Yes: SHA-1 of the info dictionary must equal the hash ([BEP 3](https://www.bittorrent.org/beps/bep_0003.html)). Fields outside `info` are unverified. | Yes: HTTPS over the confirmed TCP route | Third-party dependency; terms and limits not stated; discloses each hash; bulk use is automated scraping of one site |
| Import a magnetico, Bitmagnet or similar database | Bulk import; the rate is the import speed | No | Not applicable (local file) | No info bytes; file indices unreliable; unknown provenance and licence |
| Rats Search catalogue exchange | Not measured ([Rats compatibility](14-rats-catalogue-compatibility.md)) | No | No: librats exposes no proxy setting ([Rats compatibility](14-rats-catalogue-compatibility.md), last section) | See that report |
| BEP 33, 46, 50, 55 | No discovery effect (below) | Not applicable | Not applicable | None |

## The approaches in detail

### Tuning the current design

The translation reproduces Bitmagnet's stage sizes, pacing and timeouts
(`BitmagnetCrawler.cpp:236–273`; upstream
[factory.go:87–106](https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/dhtcrawler/factory.go#L87)).
Its one documented departure, honouring the full BEP 51 interval instead of
shortening it to 60 seconds, reduces how often a productive node is sampled
again (`BitmagnetCrawler.cpp:470–471`; upstream
[sample_infohashes.go:62–66](https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/dhtcrawler/sample_infohashes.go#L62)).
Shortening it would break BEP 51, which says an indexer should not ask again
within the interval ([BEP 51](https://www.bittorrent.org/beps/bep_0051.html)).

Metadata-side tuning (timeouts, parallel peers per hash, request windows) has
no throughput benefit at today's lookup rate, because metadata slots are never
full. The levers that can raise throughput are the lookup rate and the
conversion after it. Each gives a bounded gain and adds load in proportion; no
source supports a tenfold gain from tuning.

### Other crawler designs

- **Bitmagnet** uses BEP 51 sampling only. Its DHT responder stores announced
  peers so that it can answer other nodes, but no crawler stage reads them
  ([responder.go:85–98](https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/protocol/dht/responder/responder.go#L85);
  the only `PutHash` in `internal/dhtcrawler` is the lookup result in
  `get_peers.go:31`).
- **magnetico** (the original project, archived) used passive harvesting
  until v0.8.0. Its trawler presented a node ID made of the first 15 bytes of
  each contacted node's ID plus 5 of its own, and recorded every
  `announce_peer` it received as a hash plus peer
  ([service.go:124–190 at 10ba415](https://github.com/boramalper/magnetico/blob/10ba415c9adccfa97fafbb417188a401c10b96d3/cmd/magneticod/dht/mainline/service.go#L124)).
  Release v0.8.0 replaced it with BEP 51 sampling and removed the trawler
  ([commit 1fdfa131](https://github.com/boramalper/magnetico/commit/1fdfa131aaea);
  [indexingService.go:220 at 5546a94](https://github.com/boramalper/magnetico/blob/5546a941e4f3a2fa6a2bc8bff8876aa69780034b/cmd/magneticod/dht/mainline/indexingService.go#L220)).
  Its README gives no rate; it warns that the program has no rate limiter and
  uses all available bandwidth
  ([cmd/magneticod/README.md](https://github.com/boramalper/magnetico/blob/5546a941e4f3a2fa6a2bc8bff8876aa69780034b/cmd/magneticod/README.md)).
- **magnetico, tgragnato fork** (active) also samples with BEP 51, sending all
  lookups of a sample at once, under a global default of 500 requests per
  second ([opflags.go:21 at 43e62e3](https://github.com/tgragnato/magnetico/blob/43e62e3a8f4b5816afeac28a4e474425c2f8c7dd/opflags/opflags.go#L21);
  [indexingService.go:187–221](https://github.com/tgragnato/magnetico/blob/43e62e3a8f4b5816afeac28a4e474425c2f8c7dd/dht/mainline/indexingService.go#L187)).
  It does not record announced hashes (`:236–264`). It answers every
  `get_peers` with addresses of DHT nodes it knows, presented as peers
  (`:283–305`); its README calls these "ghost" replies. Its re-sampling check
  compares an interval in seconds with a Go duration in nanoseconds, so it
  passes for every valid interval (`:209`; `Interval int` in
  [codec.go:76](https://github.com/tgragnato/magnetico/blob/43e62e3a8f4b5816afeac28a4e474425c2f8c7dd/dht/mainline/codec.go#L76)).
  The README gives no rate.
- **Rats Search** queues announcing hashes with their peer and uses ten
  metadata jobs ([crawl strategy research](14-crawl-strategy-research.md)).

No project gives a measured rate with its conditions. The only published number
is Bitmagnet's FAQ range.

### Passive harvesting and its acceptability

An `announce_peer` gives a hash and a peer that announced it seconds ago, so a
connection is more likely to succeed than with a stale sample. To receive many
announces, a crawler must look like the closest node for many hashes, which
means choosing node IDs near other nodes or targets. The specifications treat
this as an attack:

- BEP 51 exists because passive observation is inefficient, favours
  "indexers with lots of unique IP addresses", and encourages spoofed node IDs
  and polluted routing tables
  ([BEP 51](https://www.bittorrent.org/beps/bep_0051.html)).
- BEP 42 ties node IDs to the external IP address to resist nodes "selected
  close to a specific target info-hash"
  ([BEP 42](https://www.bittorrent.org/beps/bep_0042.html)). Libtorrent supports
  this check but does not enforce it by default
  (`3rdParty/libtorrent/src/settings_pack.cpp:211`).

It also cannot run in the shared session: libtorrent runs one DHT node per
listen socket (`3rdParty/libtorrent/include/libtorrent/kademlia/dht_tracker.hpp:143–158`),
and the contract gives the session ownership of the DHT socket
([catalogue contract](../contracts/indexing.md), "crawler and configuration").
Inbound announces require a reachable node; a proxy must relay unsolicited UDP
for that to work, which was not verified for any proxy type.

### HTTP torrent caches

iTorrents documents `http://itorrents.net/torrent/<infoHash>.torrent` and an
upload API for "caching massive amounts of older files"; it states no terms,
limits or coverage ([automation page](https://itorrents.net/automation), read
once; the old `itorrents.org` page returned HTTP 403). A cache holds only what
someone uploaded, so its hit rate for random DHT hashes is unknown. Measuring it
needs many automated requests to that site, which this research excludes.

A returned file can be checked exactly: its `info` dictionary must hash to the
requested value, so a cache cannot pass off wrong metadata. It can also supply
trackers, which BEP 9 cannot. The costs are a third-party dependency, an
unknown service policy, and disclosure of every requested hash to that
operator. For on-demand use, that disclosure reveals what the person chose.

Hash2Torrent serves its own cache and otherwise fetches the info dictionary
from the swarm through BEP 9, returning a timeout when no peer has it; the page
also says all its public demo services are shut down
([Torrust announcement](https://torrust.com/blog/hash2torrent-retrieve-torrent-files-effortlessly)).
For uncached hashes it does the same work as our requester on another machine
and adds the same disclosure.

### Datasets and dumps

None of the inspected formats keeps the original info dictionary:

| Source | What it keeps | Source evidence |
| --- | --- | --- |
| magnetico SQLite (both forks) | `info_hash`, `name`, `total_size`, dates, seeders/leechers; files as `size` and `path`, with no index column. Export writes hash, name and files. | [sqlite3.go:525–537, 603–608 at 5546a94](https://github.com/boramalper/magnetico/blob/5546a941e4f3a2fa6a2bc8bff8876aa69780034b/pkg/persistence/sqlite3.go#L525); [fork export, sqlite3.go:717–751](https://github.com/tgragnato/magnetico/blob/43e62e3a8f4b5816afeac28a4e474425c2f8c7dd/persistence/sqlite3.go#L717) |
| Bitmagnet PostgreSQL | Name, size, private flag, files (first 100 by default), optional piece hashes (off by default). No raw info bytes. | [torrents.gen.go:17–31](https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/model/torrents.gen.go#L17), [torrent_pieces.gen.go:17–20](https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/model/torrent_pieces.gen.go#L17), [config.go:27–34](https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/dhtcrawler/config.go#L27) |
| Bitmagnet `/import` | Hash, name, size and classification hints; no file list | [import.md](https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/bitmagnet.io/guides/import.md?plain=1) |
| Rats Search snapshot | Searchable records without info bytes | [Rats compatibility](14-rats-catalogue-compatibility.md) |

Rebuilding a dictionary from stored fields is possible only where a source
keeps piece hashes and every original key. A rebuilt dictionary that hashes to
the right value would be genuine, but Bitmagnet stores pieces only on request,
drops files above 100 and keeps no other keys, so most records would fail. This
is a source reading; no rebuild was attempted.

Neither project publishes a dataset or states a data licence. Bitmagnet's
import guide uses a RARBG backup and says it cannot help anyone obtain it
([import.md](https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/bitmagnet.io/guides/import.md?plain=1)).
Licensing and copyright exposure of such data is the owner's decision; this
research does not settle it.

### BEPs and libtorrent

- **BEP 33** (DHT scrape) gives swarm-size estimates only and is already used
  ([catalogue contract](../contracts/indexing.md)).
- **BEP 46** stores a pointer to the latest version of a torrent under a known
  public key. **BEP 50** is a publish/subscribe service for subscribed topics.
  Neither discovers unknown torrents
  ([BEP index](https://www.bittorrent.org/beps/bep_0000.html),
  [BEP 50](https://www.bittorrent.org/beps/bep_0050.html)).
- **BEP 55** (hole punching) relays a connection through a peer that is already
  connected to both sides. A crawler with such a peer can already ask that peer
  for the metadata, so this does not help first contact.
- **Libtorrent** limits each node's store to 2,000 torrents and 500 peers per
  torrent, and returns 20 hashes per sample at most once per 6 hours by
  default (`settings_pack.cpp:370–378`). One libtorrent node therefore yields
  at most 20 new hashes per 6 hours, so hash supply grows only by reaching more
  distinct nodes.

## Hybrids and what they change for the person

DHT sampling returns hashes only. A name, size or file list exists only after
the dictionary arrives, or from a third-party record. A fast search-only record
therefore needs an outside catalogue.

| Hybrid | What the person gets | What they lose |
| --- | --- | --- |
| Import a catalogue for search; fetch the dictionary by BEP 9 when the person picks a result | Millions of searchable names at once | Results that cannot be downloaded when the swarm is dead; names and file lists that may be wrong until checked; "Download file" cannot use original indices until the dictionary arrives; no offline rebuild for those records. The contract would need an unverified record state, because admission now requires verified bytes ([catalogue contract](../contracts/indexing.md), "ownership and storage"). |
| Crawl as today, and try an HTTP cache when BEP 9 fails | Some failed hashes become verified records | Only if the cache holds them, which is unknown; each attempt discloses the hash |
| Import a catalogue, then fetch dictionaries for imported hashes in the background | Search fills at once; the archive fills at the crawler's metadata rate | The same lookup and connection costs as crawling, on hashes that are often old; each record is unverified until fetched |

## Measured and unmeasured

Measured, in our runs: the stage counts above; the 2.5/s warmed and 1.1/s
cold rates; lookup slots full and metadata slots not full; 79% connect
failures; admission time.

Source facts, not measured: lookups hold slots during pacing; samples can be up
to 6 hours older than 45-minute peer lists; libtorrent's sampling and ban
limits; that the inspected dumps lack info bytes; that caches can be verified
by hash.

Plausible, unmeasured: that pacing wait explains most of the 6.4-second slot
time; any speedup from a larger scaling factor, decoupled pacing, iterative
lookups, uTP, IPv6 or announce feeding; HTTP cache coverage; passive
harvesting yield; the number of BEP 51 nodes in the DHT.

Without a primary source: any crawler rate other than Bitmagnet's unmeasured
FAQ range.
