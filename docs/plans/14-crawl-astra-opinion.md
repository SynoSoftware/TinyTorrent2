# Independent opinion: fastest practical catalogue acquisition

Research date: 2026-10-25. Source inspection only; no build, launch, crawl or
benchmark was performed. This is a recommendation, not a changed product
contract. Native files were changing during inspection, so findings below name
functions rather than claiming to review the final implementation.

## Objective and recommendation

The owner's clarified objective is the fastest practical growth of **usable,
validated, searchable torrents and files**, while sharing one existing
libtorrent session with the person's downloads. A large discovered-hash count
is an input, not success. Peer-first scheduling, broad inventory, short attempts
and incremental enrichment are ideas to evaluate against that objective, not
requirements that predetermine the answer. Existing archive, exclusion, route,
payload-safety and ownership rules remain in the [catalogue contract](../contracts/indexing.md).

My recommendation is **active BEP 51 exploration with source affinity, prompt
metadata acquisition from fresh peer hints, and a small ordinary-lookup fallback
when that fast path cannot keep useful capacity occupied**. Keep libtorrent's
metadata implementation. Do not require every torrent to pass through a full
DHT lookup before it may obtain a handle, and do not race multiple expensive
paths for every hash.

Prefer these paths, without turning the order into a blocking chain:

1. Already available local metadata goes straight through ordinary admission.
2. A fresh announcing peer can start a guarded metadata handle immediately.
3. A BEP 51 hash retains its responding DHT node. Ask that node for peers with
   one direct `get_peers` request; begin metadata on a useful response.
4. When ready work is insufficient, give a bounded share of otherwise idle
   capacity to ordinary libtorrent discovery/acquisition. That may overlap a
   direct probe; deduplicate by torrent identity and feed later peers into the
   same handle. Under a healthy ready backlog, avoid this duplicate effort.

This is a hypothesis about the best tradeoff, not a measured victory over
strict peer-first. It combines the cheap known-source route with a way to use
capacity that strict gating can leave idle. It needs no second session,
external indexer, custom peer-wire implementation or general task framework.

Final source-only follow-up: a fixed quarter of configured slots for ordinary
fallback is not supported by the protocol evidence and can leave 12 of 16 slots
idle. Prefer ready candidates, then fill remaining configured capacity with
ordinary eligible work. Keep the existing activity timeout and busy-download
limits. Do not add preemption before measuring a real ready-backlog problem;
a quiet handle may be finishing its handshake, and cancellation adds churn.
This is the simpler baseline for observation, not a proven throughput optimum.

## What the primary sources establish

**Discovery.** BEP 51 was designed because passive observation favors many IPs
and incentivizes disruptive node-ID behavior. It returns hashes held by the
responding node, plus contacts near a chosen target; changing the target changes
contacts, not that node's samples. Respect the advertised sampling interval and
visit other nodes during it. Its statement about surveying the DHT in hours is
design intent, not a guarantee of full metadata acquisition. [BEP 51][bep51]

**Peer hints.** BEP 5 separates nodes that store peer records from peers that
participate in a swarm. A sampled node is therefore a useful `get_peers`
destination, not a BitTorrent peer to connect to. An `announce_peer` endpoint
is a fresher direct peer hint. Neither proves current reachability. Incoming
`get_peers` hashes are weaker: the pinned implementation itself emits random
hash queries when refreshing routing buckets. [BEP 5][bep5],
[pinned node.cpp, `send_single_refresh`](../../3rdParty/libtorrent/src/kademlia/node.cpp)

**Metadata.** BEP 9 transfers the info dictionary in 16 KiB blocks. A peer may
reject requests, and a whole dictionary must pass its hash check. Discovery,
connection, extension support and validated metadata are different milestones.
There is no instant readiness oracle. Preserve validated info bytes and useful
provenance; unverified fragments are not searchable torrent descriptions.
[BEP 9][bep9]

**Swarm counts.** BEP 33 estimates populations through Bloom-filter unions. It
does not identify which peer will send metadata, and actively scraping every
new hash adds work before the outcome we want. Reuse observations already
obtained, and keep extra scrapes out of the metadata critical path. [BEP 33][bep33]

**Bitmagnet.** The inspected revision keeps separate pipeline channels and the
node-to-hash association. Its peer stage asks that node directly; metadata tries
returned peers sequentially. This supports the affinity fast path, not a claim
that sequential peers or its concurrency values are optimal here. Its sampler
also shortens some requested intervals, which conflicts with the BEP 51 rule
and should not be copied. [Peer requests][bmpeers], [metadata][bmmetadata],
[sampler][bmsample], [pipeline][bmpipeline]

Bitmagnet's custom requester dials IPv4 TCP and requests every metadata block
after handshakes. This is a different transport and scheduling implementation
from our shared libtorrent session; its per-peer deadlines cannot be copied as
equivalent handle deadlines. [Requester][bmrequester]

**Rats Search.** The inspected crawler queues the announcing peer with the hash
and uses its direct-peer metadata method when available, otherwise a DHT method.
It suppresses known hashes and runs multiple acquisitions. This independently
supports fresh-peer preference. Its recent set and metadata queue are memory
structures, not a demonstrated durable million-entry catalogue strategy, and
the inspected file alone does not prove the underlying spider or metadata
implementation's throughput. [Crawler, revision 032d297][rats]

## Pinned libtorrent: capabilities and important waits

The actual dependency pin is the SynoSoftware revision
`464f12919bee03de42e56fe1c891a9d1772bc913`; use
[libtorrent.pin](../../3rdParty/libtorrent.pin) and the checked-in source as the
authority, rather than an upstream version label alone.

| Operation | Verified behavior | Consequence |
| --- | --- | --- |
| `dht_get_peers(hash)` | Starts iterative searches on the session's DHT nodes. Peer responses invoke a callback whenever `values` is present. The public call exposes neither terminal completion nor per-call cancellation/cookie. | A 15-second application lease is not a 15-second network operation. Success or lease expiry cannot establish that all its requests ended. |
| `dht_direct_request(endpoint, request, cookie)` | One directed RPC with response-or-timeout callback on the normal path. Its observer marks completion once. Public documentation explicitly says it bypasses the IP filter. | Good source-affinity probe with exact correlation. Apply the established endpoint policy before dispatch; this is not permission to bypass the person's filter. |
| Direct-request exceptional paths | Session code returns if DHT is absent; tracker code can find no matching address family; observer allocation can fail. | Still need bounded owner-side recovery. Do not rely on the documented completion alert under every teardown/resource failure. |
| DHT RPC timeout | Pinned RPC manager uses a one-second short timeout and 15-second full timeout. | Short timeout permits traversal progress; it does not cancel the RPC. A custom earlier application cutoff does not reclaim its network resources. |
| `dht_sample_infohashes` | One directed request, not a full traversal. Successful replies deliver samples and contacts. | Explicitly track/rate-limit dispatches; count responses separately from starts. |
| Metadata peers | `add_torrent_params.peers` seeds endpoints; `connect_peer` adds more. `ut_metadata` owns block allocation, retries and validation. | Race a small number of peers through the same handle; avoid a parallel BEP 9 implementation. |
| Session peer scheduler | `try_connect_more_peers` shares the connection-start budget across torrents and checks the global connection ceiling. | More crawler handles can compete with downloads without increasing total starts. Handle count alone does not bound peer pressure. |

Local evidence: [session declarations](../../3rdParty/libtorrent/include/libtorrent/session_handle.hpp),
[session dispatch](../../3rdParty/libtorrent/src/session_impl.cpp),
[get-peers callbacks](../../3rdParty/libtorrent/src/kademlia/get_peers.cpp),
[DHT dispatch](../../3rdParty/libtorrent/src/kademlia/dht_tracker.cpp),
[node dispatch](../../3rdParty/libtorrent/src/kademlia/node.cpp),
[direct observer](../../3rdParty/libtorrent/include/libtorrent/kademlia/direct_request.hpp),
[RPC timeout](../../3rdParty/libtorrent/src/kademlia/rpc_manager.cpp),
[metadata implementation](../../3rdParty/libtorrent/src/ut_metadata.cpp).

Pinned defaults include 30 connection starts/second, 200 connections, 15-second
peer connection timeout, ten-second handshake timeout and 30 MiB maximum
metadata. These are library defaults, **not a measurement of effective product
settings**. Changing global timeouts to accelerate the crawler also changes
downloads; exhaust the per-crawler opportunities first.
[Settings defaults](../../3rdParty/libtorrent/src/settings_pack.cpp)

## Alternatives and when they win

| Approach | Expected advantage | Cost / failure path | Judgment |
| --- | --- | --- | --- |
| Passive announce hints only | Fresh peer endpoint, little additional DHT traffic | Depends on incoming traffic, reachability and the portion of keyspace served | Keep as a valuable supplement; not the main broad discovery strategy. |
| Active BEP 51 plus source-node probe | One response can supply many candidate hashes; peer query starts at a node known to store that hash | Node or peers can have disappeared; samples can repeat | Best initial fast path for broad discovery. |
| Full `dht_get_peers` before every handle | No connection slots spent until peers appear; benefits from libtorrent traversal | Extra routing work despite known source; no terminal/cancel event; strict gating can idle metadata | Useful bounded fallback, not the mandatory path. |
| Handle immediately for every hash | Overlaps lookup and handle lifecycle; no separate readiness wait | Cold/dead hashes occupy slots and consume DHT/peer budgets; floods can hurt downloads | Small opportunistic share when ready work is scarce; poor default under saturation. |
| Direct probe plus immediate handle for every hash | Lowest possible latency for some candidates | Duplicates work and can lower aggregate useful throughput | Measure selective overlap, not unconditional racing. |
| Custom iterative direct-request DHT walker | Explicit per-RPC correlation and application-controlled fan-out | Reimplements routing/termination policy; source filtering, IPv6 and cancellation bookkeeping grow | Do not add before measuring a real limitation of the direct fast path plus libtorrent fallback. |
| More handles / automatic concurrency controller | Can hide latency if connection and database headroom exists | Can merely queue inside the same session, consume memory and steal download starts | First expose evidence and compare fixed profiles. No automatic controller justified yet. |

Under a large ready backlog, peer-first is resource efficient: there is no
reason to give a scarce handle to a cold hash. Under an empty ready backlog,
strict peer-first can waste usable capacity. That distinction is the reason
for the recommendation; it is not a preference for more pipeline machinery.

## Findings in the current implementation and proposal

Inspected [Indexing.cpp](../../engine/src/Engine/Indexing.cpp),
[Catalogue.cpp](../../engine/src/Catalogue.cpp), the
[existing research](14-crawl-strategy-research.md) and
[feedback plan](14-discovery-feedback.md) after developing the protocol strategy.

The edits already address several obvious bottlenecks: metadata limits become
configurable; projection can process several small torrents per invocation;
database status uses maintained totals; broad hash inventory is no longer
deleted merely at 100,000; announce peers are retained. These are credible
improvements, but no source inspection proves the resulting rate.

The material remaining questions are:

1. `On(dht_sample_infohashes_alert)` persists samples and nodes separately and
   loses which node supplied each hash. `MaintainIndex` then launches full
   `dht_get_peers` searches. Preserve that relation to enable the cheaper route.
2. Probe records expire after 15 seconds or the first peer batch. The traversal
   can outlive either event. Thus the visible probe count is not the real count
   of active DHT network work. Rate-limit fallback starts independently and do
   not describe expiring a lease as cancellation.
3. `Due` currently requires peers; `Acquire` disables handle DHT. This makes
   readiness a strict gate. It should be an explicit comparison candidate, not
   treated as established fastest behavior.
4. `Acquire` starts a fixed 30-second deadline before asynchronous add and
   connection scheduling. Removing a handle loses `ut_metadata`'s in-memory
   partial blocks. Repeated attempts can repeatedly discard useful progress.
   The existing `progress_ppm` field is not a metadata-block progress signal:
   `ut_metadata` does not update it. Do not build stall detection on that field.
5. Slot replenishment and readiness dispatch currently pass through one-second
   maintenance. Once network successes become fast, that adds avoidable idle
   time. Request a bounded dispatch after relevant completions, keeping one
   scheduling owner and the periodic timer for recovery. Measure whether this
   matters before introducing another loop.

A short initial attempt is reasonable; **shorter is not monotonically better**.
Connection queueing, handshakes and larger info dictionaries all consume time.
The current per-torrent byte limits make a large-dictionary cutoff especially
important to measure. The native implementer proposed using existing status
updates to extend the deadline after at least 16 KiB of additional received
bytes. Pinned `torrent.cpp` sets `total_download` from payload plus protocol
bytes, and guarded crawler handles have no wanted payload. This is a reasonable
minimal **receive-activity heuristic**, not verified metadata progress. Accumulate
bytes since the last threshold crossing; resetting the baseline on every status
sample would fail to recognize slower steady receipt. Ordinary keepalives do
not satisfy that threshold, although sufficiently noisy protocol traffic can
extend occupancy. Measure success and timeout ages before adding more machinery.
A small allowance for longer later attempts is another comparison candidate;
neither requires partial-block serialization or a dependency modification.

Similarly, retaining every hash can be cheap and improves restart behavior, but
it does not make the crawler faster by itself. Persist in batches, maintain
bounded in-memory work, and measure ingestion/selection costs. Do not force a
disk commit round-trip into every immediate peer connection if an already
batched durable path can preserve the same ownership guarantees. Any relaxation
of the current persistence contract is an owner decision, not assumed here.
Broad retention and fair retry are coverage policies with throughput costs;
they must not turn a million stale hashes into priority over fresh viable work.

## Concrete integration order

1. Keep the existing engine module as network/work owner and `Catalogue` as the
   SQLite owner. Preserve ordinary admission, exact archive bytes, identity
   validation, rule reasons and payload guard. Add no scheduler interface or
   second metadata owner.
2. Retain a bounded number of fresh hints with their source kind and observation
   time: actual peer versus DHT source node. Prefer the sample's direct source
   before starting full traversal. Extend the existing correlated direct-request
   handling so peer probes and scrapes cannot mistake each other's replies.
3. Fill available metadata capacity promptly from fresh peers. When that supply
   dries up, allow limited ordinary discovery/acquisition in the same session.
   Prevent duplicate handles, and attach late peer hints to the current owner.
4. Bound start rate as well as resident requests, handles, peers and queued
   storage work. A cooling node is not eligible merely because rediscovered.
   Reduce new crawler work as downloads/checking/moves consume resources; a
   lower future handle limit alone does not shrink already-running jobs.
5. Compare fixed profiles and deadline choices with the measurements below.
   Only then decide whether more concurrency, selective races, longer retries
   or more database work is worth their cost.

## What would establish that it is faster

The decisive counters are **new validated torrents admitted**, **new torrents
published**, **new files published**, and elapsed time. Show recent rates and
time since the last publication, separately from inventory size and active
jobs. Count duplicates, exclusions and failures separately; they are work but
not growth. Files/second alone rewards giant torrents, while torrents/second
alone rewards tiny ones, so report both with metadata-size/file-count mix.

For a bounded observation, record new distinct hashes by source, direct probes
started/responded/positive/timed out, fallback starts, ready backlog, handle
occupancy, connection attempts, metadata success ages, failures, projection lag,
database queue latency, memory and impact on a user's active transfer. Distinct
hash counts require insertion results, not adding the length of sample arrays.

Compare the same overall resource budget for strict peer-first, source-affinity
fast path with fallback, and selective overlap. Alternate comparable time
windows because the public network changes. A single short lucky run cannot
establish an optimum. Measure cold discovery and a warmed catalogue separately:
duplicate rate and retry mix change as the catalogue grows.

For one million torrents and ten million files, network speed is only half the
claim. Check database admission/publication throughput, indexed eligibility
selection, retained archive size and search latency at representative scale.
Do not call a row-limited screenshot a scale test. At an observed sustained
rate `r` new torrents/second, the arithmetic time to one million is
`1,000,000 / r` seconds; the extrapolation is valid only while duplication,
failure mix and storage cost remain comparable.

No numerical throughput promise follows from this research. The strongest
immediate recommendation is to remove demonstrable wasted work and prevent
idle capacity, then choose the fastest settings from committed output rates.

## Final dispatch recommendation before the first observation

The native revision inspected after this report added source affinity, ordinary
handle fallback and the receive-activity deadline. Its proposed fallback cap of
one quarter of configured metadata slots has no primary-source basis as an
optimum. With no ready peers it can leave twelve of sixteen permitted slots
idle. For the first observation, prefer the simpler policy: select ready work
first, then fill remaining configured capacity with eligible ordinary work.
Here, "bounded fallback" means the existing metadata and peer budgets, not a
second arbitrary percentage.

Do not add cold-job preemption before that observation. Less than 16 KiB received
does not prove that a job is useless; a connection or handshake may be about to
finish, and cancelling it adds churn and another lifetime transition. Keep the
receive-activity timeout, per-handle limits and busy policy. Measure ready-work
waiting time when every handle is occupied; introduce reserved capacity or
preemption only if that actual failure outweighs the lost acquisition work.
This selects a minimal experimental baseline, not a proven fastest policy.

## Follow-up measurements

A disposable 150-second run at sixteen slots recorded 64 attempts: three
metadata successes, 45 timeouts and sixteen attempts interrupted by Exit. Forty
attempts never showed an established peer. Carried-forward changed-status
observations peaked at 45 crawler connections and eleven established peers;
these are sampled crawler values, not exact session-wide peaks.

Request 48 received over 22 KiB during its final five seconds, but expiration
ran before the updated status arrived. The correction samples fresh status
asynchronously before expiring an attempt. Shorter blind deadlines would also
have lost successful requests that took approximately nineteen, twenty-four and
thirty seconds.

An adjacent run on the same binary at 32 slots reached sixteen searchable
torrents and 2,097 files by 140 seconds, versus three torrents and thirty files
at sixteen slots. Other sixteen-slot runs varied from zero to eight torrents.
This supports trying 32 as the idle default; it does not establish a universal
speedup or optimal concurrency. The configurable busy limit remains two.

Source inspection also found that `connect_peer` defaults advertised uTP and
hole-punching capabilities absent from DHT hints. Supplying the DHT source with
empty capability flags matches libtorrent's own DHT callback and preserves the
configured transport policy. Runtime validation of the combined corrections
belongs to the lead's final build, not this source recommendation.

[bep5]: https://www.bittorrent.org/beps/bep_0005.html
[bep9]: https://www.bittorrent.org/beps/bep_0009.html
[bep33]: https://www.bittorrent.org/beps/bep_0033.html
[bep51]: https://www.bittorrent.org/beps/bep_0051.html
[bmpeers]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/dhtcrawler/get_peers.go
[bmmetadata]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/dhtcrawler/request_meta_info.go
[bmrequester]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/protocol/metainfo/metainforequester/requester.go
[bmsample]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/dhtcrawler/sample_infohashes.go
[bmpipeline]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/dhtcrawler/crawler.go
[rats]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/net/crawler.cpp
