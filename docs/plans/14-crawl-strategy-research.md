# Active DHT crawling: source findings and recommended strategy

Research date: 2026-10-25. This is a recommendation, not a throughput benchmark
or a claim that the proposed pipeline is already implemented. The live
[catalogue contract](../contracts/indexing.md) remains the product authority.
The owner's requirement is the fastest practical growth of validated,
searchable torrents and files while sharing the existing engine and libtorrent
session with downloads. Hash inventory, peer-first scheduling, short attempts
and incremental enrichment are ideas to evaluate against that goal, not
requirements that predetermine the implementation. A growing hash queue alone
does not establish useful catalogue growth.

## Recommendation

Prefer active BEP 51 exploration with source affinity and prompt metadata
acquisition from fresh peer hints. Keep a bounded ordinary libtorrent lookup
fallback when ready candidates cannot fill useful capacity. Source inspection
supports these mechanisms; it does not prove this combination globally fastest.
The [independent opinion](14-crawl-astra-opinion.md) explains the same distinction
between resource efficiency under a ready backlog and wasted capacity when
strict readiness gating has no work to offer.

The work has distinct milestones, not a mandatory blocking chain:
**discover → obtain peers and metadata → validate and retain → publish files.**

Fresh announcing peers can start a guarded metadata handle directly. A sampled
hash can first query its supplying DHT node cheaply. When those paths leave
capacity idle, an ordinary acquisition may begin without waiting for a separate
peer-ready stage; feed late peer hints into that same handle. Under a healthy
ready backlog, avoid duplicating full lookups for cold hashes. Neither hashes
nor endpoints predict instant replies. Choose between these paths by useful
output and shared resource cost, not by the size of the queue they create.

Persisting deduplicated hashes and useful hints can avoid repeated discovery
and support later retries, but it remains a storage/throughput tradeoff. Discovery
should not idle solely because other jobs wait. Keep libtorrent in charge of
peer connections, metadata exchange and validation; no parallel wire
implementation or extra session is justified by this research.

## Verified reference behavior

| Reference | What its implementation actually does | Useful lesson and limitation |
| --- | --- | --- |
| Bitmagnet, `e31b30d` | Separate sampling, triage, get-peers, metadata and persistence channels. At default scaling factor ten, the factory permits 200 concurrent get-peers jobs and 400 metadata jobs; each channel buffers 100 entries. | Separate network stages so metadata latency does not serialize discovery. Those counts belong to a dedicated indexer; they are not measured defaults for this shared-session laptop client. [Factory][factory], [configuration][config]. |
| Bitmagnet | Retains the node that supplied a sampled hash; asks that node for peers before metadata work. Returned contacts feed node discovery. Empty peer responses do not enter its metadata stage. | Preserve the source endpoint. Its audited get-peers operation is one direct request, not a complete recursive lookup for that hash. [Peer lookup][getpeers]. |
| Bitmagnet | Tries returned metadata peers sequentially, with a six-second timeout per request. | It avoids our blind 90-second acquisition-slot occupancy, but sequential peers can still delay a good peer. Its custom direct TCP requester is not a reason to duplicate libtorrent or change global transfer timeouts. [Metadata pipeline][metadata], [request timeout][timeout], [requester][requester]. |
| Bitmagnet | Takes up to 60 sampling candidates each second and rotates its target every ten seconds. It shortens some returned backoffs to 60 seconds. | Broad keyspace exploration is useful. Shortening an advertised interval is not: follow BEP 51. [Sampler][sampler], [crawler][crawler]. |
| Rats Search, `032d297` | Walks DHT targets on a default 100-ms timer. Queues an announcing hash together with its peer endpoint; ten concurrent metadata jobs use a direct-peer fast path where available. | An announcing endpoint is a better readiness hint than a hash alone. Its in-memory recent set and metadata queue are not our durable discovery ledger. [Crawler source][rats], [defaults][ratsh]. |

The reference inventory records their other limitations, including v1-oriented
metadata identities and file-retention differences. None establishes a complete
network catalogue or a suitable million-entry performance guarantee for us.

## Confirmed problems in the inspected TinyTorrent crawler

These findings describe the source before this corrective implementation batch;
line numbers can move as the native owner fixes them.

1. `Engine/Indexing.cpp`, `MaintainIndex`/`Acquire`: acquisition slots are allocated
   before peers are known, then held for up to 90 seconds. The original limit
   is four idle jobs and one busy job. Configurable larger limits can hide more
   latency, but their benefit depends on usable peers and shared connection
   headroom. Allocating every slot blindly and requiring a full peer lookup
   before every handle are both strategies to compare, not established optima.
2. `Engine/Alerts.cpp`, announce handler: retains the hash but discards the
   announcing peer endpoint. `On(dht_sample_infohashes_alert)` similarly drops
   the relation between each hash and the responding DHT node.
3. `Catalogue.cpp`, `Discover`: deletes pending rows beyond 100,000. That bounds
   durable knowledge rather than active work. Removing that cutoff can avoid
   losing useful candidates; keeping all hashes has a storage cost. Evaluate
   that tradeoff against usable metadata throughput rather than treating broad
   inventory as the owner's required end state.
4. `Catalogue.cpp`, `SaveNode`: a 2,048-node cap can reject all fresh candidates
   when retained nodes are cooling down. Deleting cooling records indiscriminately
   would forget required sampling intervals; preserve those deadlines separately
   from any bounded resident frontier, or retain the node records in SQLite.
5. `MaintainIndex` samples two nodes every two seconds idle, every eight seconds
   busy. This is deliberately slow exploration, independent of actual sample
   yield or metadata throughput.
6. `Catalogue.cpp`, `ProjectPending`: processes one torrent per invocation;
   one-second maintenance therefore publishes at most about one torrent per
   second even when each torrent has a single file. Let small entries share
   the existing bounded file/time budget while retaining resumable projection.
7. `Catalogue.cpp`, `ReadStatus`: repeated exact counts and a sum scan the growing
   catalogue. The stats intended to prove progress must not become a full-database
   scan every few seconds. Maintain inexpensive counts at their authoritative
   mutation points, with migration/reconciliation when needed.

Local owners: [crawler](../../engine/src/Engine/Indexing.cpp),
[alerts](../../engine/src/Engine/Alerts.cpp),
[catalogue](../../engine/src/Catalogue.cpp).

## What the existing libtorrent can do

The dependency pin is the SynoSoftware revision
`464f12919bee03de42e56fe1c891a9d1772bc913`, recorded in
`3rdParty/libtorrent.pin`. Its version header reports 2.1.2.0 and upstream
revision `a610c6ef4`; that label alone does not identify the actual dependency.
Current online API docs show 2.1.0, so the pin and checked-in source decide
behavior.

- `session.dht_get_peers(hash)` works without creating a torrent handle. It
  posts peer batches through `dht_get_peers_reply_alert`. This enables peer
  discovery before expensive metadata jobs. [Session API][session].
- That alert is **not a terminal lookup event**. Pinned
  `src/kademlia/get_peers.cpp:34–69,94–96` calls its peer callback for `values`
  responses; `src/session_impl.cpp:6414–6417` turns those callbacks into alerts.
  A lookup can produce multiple batches, and no values does not establish that
  an empty completion alert will arrive. There is no per-call cancellation or
  request cookie on `dht_get_peers`. Bound starts as well as application leases.
- `dht_direct_request(endpoint, request, cookie)` has one response-or-timeout
  alert per call. It is suitable for trying the sample-providing node directly,
  and the existing BEP 33 owner already uses it. Its documentation explicitly
  says it bypasses the session IP filter: apply the existing endpoint policy
  before sending crawler probes. [Session API][session], [alert contract][alerts].
- Pinned `src/kademlia/rpc_manager.cpp:363–364` has a one-second short timeout
  and a 15-second full RPC timeout. Expiring an application probe earlier
  does not cancel the libtorrent request. Reject late callbacks by request
  identity and avoid replenishing unbounded hidden requests behind a small UI
  counter.
- `add_torrent_params.peers` supplies known peer endpoints when adding a handle;
  `torrent_handle.connect_peer` can add later endpoints. A ready-peer job can
  avoid another full DHT lookup. Preserve normal IP/route restrictions and the
  existing payload guard. Pinned headers: `add_torrent_params.hpp` and
  `torrent_handle.hpp`.
- Pinned default peer connection timeout is 15 seconds, handshake timeout ten
  seconds, global connection-start rate 30/second and connection limit 200
  (`src/settings_pack.cpp:246–256,315`). More crawler jobs cannot bypass these
  shared limits, and changing them globally would affect accepted downloads.
  A 30-second first metadata attempt is one initial experiment rather than an
  established optimum. Starting that deadline before asynchronous add and
  connection scheduling consumes part of it before a peer can answer; removing
  a handle also loses its partial metadata blocks. Compare useful completions
  and repeated work before adopting a shorter deadline. Do not copy a
  six-second custom-TCP deadline into the shared session as an equivalent wait.
- Libtorrent's metadata plugin already spreads block work across peers. Pinned
  `src/ut_metadata.cpp:396–415,463–497` keeps up to two requests outstanding per
  peer and permits re-requesting a block after three seconds. Keep this owner;
  a new metadata wire implementation would duplicate difficult validation.

## Alternatives to compare

| Strategy | When it helps | Why it can lose |
| --- | --- | --- |
| Strict peer-first | Plenty of fresh ready candidates; scarce connection capacity | A slow peer stage can leave metadata capacity idle, and an extra lookup can add latency despite a useful hint. |
| Immediate guarded handle for every hash | Few candidates, otherwise idle resources; overlaps ordinary discovery and handle setup | Dead hashes monopolize slots, consume shared connection/DHT budgets and delay better candidates. |
| Source-affinity fast path with limited ordinary fallback | Broad sampling plus occasional gaps in ready work | Requires deduplication and explicit start limits; it still needs measured comparison with the simpler alternatives. |
| Direct probe and immediate handle for every hash | May minimize latency for an individual lucky hash | Duplicates network work and can reduce aggregate validated metadata throughput. |

The source-affinity fast path with fallback is the recommended first comparison
candidate. It uses otherwise idle capacity without requiring an unconditional
race for every hash. Existing references establish that the underlying paths
work; they do not establish their relative throughput in this client.

## Initial profiles to compare

These are proposed starting values, not established optima. Measure their yield
on this machine and connection before claiming sustained rates.

| Stage | Initial idle / busy profile | Rule |
| --- | --- | --- |
| Sampling | 4–8 / 1 requests per second | Traverse distinct eligible nodes, retain returned contacts, honor node backoff. |
| Peer probing | 32 / 4 active jobs | Prefer the supplying node; bound ordinary lookup starts independently of application leases. |
| Metadata | 16 / 2 active jobs, configurable | Prefer fresh peer hints; allow a limited ordinary-acquisition share when ready work is insufficient. Bound total peer pressure. |
| First metadata attempt | Compare the existing deadline with around 30 seconds | Judge completions and lost partial work, not fast timeout counts. Keep later retries for difficult swarms. |
| Persistence/publication | Small transactions, bounded file/time batches | Compare the benefit/cost of retaining hash hints; keep search and transfer state responsive. |

Retain only a bounded number of peer endpoints per hash and give them a freshness
time; all endpoints are untrusted hints. When ready work exists, it takes precedence, but
reserve regular probe opportunities for old eligible hashes so a stream of new
announcements cannot starve the backlog. Deduplicate before both probing and
metadata acquisition. A fresh peer hint can justify earlier reconsideration;
mere rediscovery of the same hash must not continuously erase failure backoff.
Remember rule-based rejections exactly and re-evaluate only under the agreed
rule-release lifecycle.

BEP 51's target changes returned contacts, not the returned hash sample. Repeated
requests to a cooling node do not produce better coverage. Explore more distinct
nodes rather than trying to force more hashes from the same node. Keep proper
node IDs; neither Rats' spider-ID behavior nor Bitmagnet's shortened intervals
should be copied. [BEP 51][bep51].

There is no oracle for which unseen torrent will reply instantly. Fresh
announcements, successful direct peer discovery and recent endpoint success are
evidence that makes a request more promising; only a verified metadata response
establishes success. A DHT sample says that a node held peer records for that
hash, not that those peers remain reachable or support BEP 9.

## Visible progress

Show current indexed torrents/files separately from discovered hashes awaiting
metadata, peer probes, and metadata acquisitions. Label those as jobs, never
threads. An enabled switch is not proof of work. Show the actual waiting reason
when route, DHT, pause or storage prevents work. A compact status can read:
`1,240 torrents · 18,600 files · 24 probing · 16 fetching · 82,000 queued`.
Use the existing footer tooltip/accessibility owner for detail that cannot fit.

A short recent completions rate and last-success age would distinguish active
but unproductive probing from useful growth; they are optional derived facts,
not additional durable torrent state. Do not put approximate swarm seeds in
place of acquisition progress. Do not rescan millions of rows to refresh stats.

## Evidence required before performance claims

Source inspection establishes the source-level ceilings and available mechanisms,
not a winning strategy or the fastest numbers.
One focused final fresh-store observation can report hashes discovered, probes
started/positive/expired, metadata successes/timeouts, queue growth and resource
use over a fixed interval. Compare strict peer-first, source-affinity with
ordinary fallback, and selective overlap under comparable resource budgets;
record network variability. Count new validated torrents and published files
as the outcome, separately from discovered hashes and attempted jobs. A
million-torrent target also requires measured SQLite
admission, projection and local-search behavior at that scale; a higher network
rate alone cannot establish it.

Bitmagnet's FAQ advertises roughly 100–1,000 torrents/minute depending on hardware
and network, and says duplicates slow growth. This is its documentation claim,
not our benchmark or a promised rate. [FAQ][faq]. No crawler, payload transfer,
build, installation or runtime benchmark was run for this research.

[factory]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/dhtcrawler/factory.go
[config]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/dhtcrawler/config.go
[getpeers]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/dhtcrawler/get_peers.go
[metadata]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/dhtcrawler/request_meta_info.go
[timeout]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/protocol/metainfo/metainforequester/config.go
[requester]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/protocol/metainfo/metainforequester/requester.go
[sampler]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/dhtcrawler/sample_infohashes.go
[crawler]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/dhtcrawler/crawler.go
[rats]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/net/crawler.cpp
[ratsh]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/net/crawler.h
[bep51]: https://www.bittorrent.org/beps/bep_0051.html
[session]: https://libtorrent.org/reference-Session.html
[alerts]: https://libtorrent.org/reference-Alerts.html#dht-direct-response-alert
[faq]: https://bitmagnet.io/faq.html
