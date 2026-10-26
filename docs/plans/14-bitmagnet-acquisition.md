# Bitmagnet metadata acquisition: focused comparison

The research below preceded the owner's direct-translation A/B implementation.
[Current execution and verification](14-discovery-feedback.md#current-batch-useful-acquisition-throughput)
supersedes its proposed next experiment; source facts remain pinned to the
revision below.

Inspected 2026-10-25. Research only: no production edits, build, crawler launch
or benchmark. The GitHub API resolved current `main` to
`e31b30d7351c269dc801876f92ebc59d31fee545`, the same revision used by the
[earlier inventory](14-torrent-references.md). This is the beta/main source,
not evidence that stable v0.10.0 has every later feature.

## Finding that changes the comparison

**The earlier reports understated Bitmagnet's concurrency.** Its channel
constructor takes **capacity first, concurrency second**. At the default
scaling factor of ten, get-peers permits **200 active jobs**, and metadata
permits **400 active hash jobs**; each has a 100-entry channel buffer.
Sampling permits 100 active jobs. These are limits, not measurements of actual
occupancy or distinct live connections. Each metadata job tries peers
sequentially, so it has at most one endpoint request executing at a time.
The semaphore remains held while downstream sends wait, providing bounded
backpressure. [Factory][factory], [channel implementation][channel],
[default scaling][config], [metadata stage][metadata].

This was an interpretation error in the previous research, not a source-version
change. TinyTorrent's observed 32 or owner-configured 64 acquisition slots cannot
be compared with a supposed 100-slot Bitmagnet default. Neither number establishes
an appropriate shared-session setting for this client.

## Implemented acquisition behavior

| Stage | Verified Bitmagnet behavior | Significance for TinyTorrent |
| --- | --- | --- |
| Sampling | Selects up to 60 eligible nodes per second. A sampled hash retains its supplying node. The target changes every ten seconds. It sometimes shortens requested sampling intervals. [Sampler][sampler], [pipeline][pipeline]. | The source relationship is useful and is already retained here. Honor BEP 51 intervals; more sampling alone does not explain our poor conversion with an existing large queue. |
| Triage | Deduplicates a batch, checks blocked hashes and queries existing records. Missing/incomplete metadata goes to peer lookup; complete entries instead need only an availability refresh where appropriate. [Triage][triage]. | Avoid repeat metadata work. This does not establish a durable failed-acquisition retry queue. |
| Peer lookup | Sends one get-peers request to the supplying node. Returned nodes feed general discovery, rather than recursively resolving this hash in that stage. A response without peers does not enter metadata acquisition. [Peer lookup][peers]. | Bitmagnet spends metadata capacity on peer-positive candidates. Our ordinary fallback may occupy a slot before it has any peer. Compare this mix rather than assuming every occupied slot is productive. |
| Endpoint acquisition | Uses direct IPv4 TCP. The dialer has a three-second timeout. One six-second context/deadline covers dial, BitTorrent handshake, extension negotiation, receipt and validation for that endpoint. It requests all metadata pieces, then reads them. [Requester][requester], [request defaults][requestconfig], [requester factory][requestfactory]. | Bitmagnet has rapid endpoint turnover without an initial uTP attempt. Six seconds is not an equivalent timeout for our whole libtorrent handle, which may still be discovering and scheduling peers. |
| Other peers | Tries peers sequentially until one succeeds; all failures exhaust the list. A hash job can therefore last multiple six-second attempts, plus limiter/downstream waits. [Metadata stage][metadata]. | Its behavior is not parallel racing, nor a six-second maximum for the entire hash. Copying that description would produce the wrong scheduler. |
| Per-host pacing | Before dialing, waits on a limiter keyed by peer IP: two starts per second, burst four, with a bounded expiring limiter cache. [Limiter][limiter], [limiter implementation][keyed], [requester factory][requestfactory]. | High overall breadth still respects repeated requests to one host. There is no evidence of a single global start-rate cap in these inspected metadata layers. |
| Persistence | Batches accepted metadata, writes torrents/files/source records transactionally and queues classification. Availability scraping follows persistence. Classification is delayed a minute to allow estimates to arrive. [Persistence][persist]. | Acquisition, retention and visible search publication need separate timings. Bitmagnet's catalogue growth is not an immediate UI-row count. |

The requester checks handshake identity/extension support and parses the
completed info dictionary. Metadata must be positive and below 10 MiB.
This is a torrent-specific BEP 9 exchange, not a peer's entire catalogue.
Its TCP-only implementation cannot establish that TCP wins for every host or
that copying its requester is necessary. [Requester][requester].

## What its throughput claim actually establishes

The FAQ reports **100–1,000 torrents per minute**, depending on hardware,
network and scaling factor, and warns that duplicates slow growth. It supplies
neither a reproducible workload nor latency/failure distributions for that
range. It also distinguishes newly persisted torrents from rediscovered source
updates. It is evidence that useful DHT indexing can substantially exceed our
current rate, not a benchmark prediction for Windows, this network or complete
file retention. [FAQ][faq].

Bitmagnet defaults to retaining the first 100 file entries and not storing
piece hashes. TinyTorrent must retain all valid files and the immutable info
dictionary. This changes storage work and catalogue usefulness; compare
searchable files per minute as well as torrent counts. [Defaults][config],
[actual file loop][persist].

## Observability worth adopting

Bitmagnet exports metadata request success/error counters, active-request gauge
and a histogram **only for successful request durations**. The collector sits
inside the per-IP limiter, so limiter waiting is excluded. Its coarse failure
counter does not itself identify dialing versus handshake versus unsupported
metadata failures. Sampled logging limits repeated error output. The persistence
counter is incremented after successful transaction completion; its Torrent
value counts that batch's upserted models, not a database-returned count of
newly inserted identities. [Collector][collector], [requester factory][requestfactory],
[persistence][persist].

Our requested cohort should go further only where needed to answer the current
bottleneck: stage outcomes, failed as well as successful latency, ready versus
ordinary admissions, connection transport, metadata-capable peers, validated
metadata and actual new retained/published records. A connected socket is not
proof that a BitTorrent or metadata handshake succeeded.

## Smallest useful next comparison

Current source already supplies source affinity, late peers, bounded overlapping
probes, ready-first acquisition and ordinary fallback. Preserve the existing
libtorrent session and metadata implementation. Trace the requested cohort
before changing these policies.

1. Run known healthy public torrent controls through the same metadata-only
   acquisition path, alongside a fixed randomly discovered cohort. Record stage
   conversion and latency under unchanged connection/concurrency settings.
2. If connection establishment dominates, compare the existing Both and TCP-only
   transport profiles on cloned disposable stores and the same cohort. This
   answers a question with the existing runtime setting; it is not a proposed
   permanent global transport change for the person's downloads.
3. If slots mostly wait without usable peers while ready work queues, compare
   admission/turnover under the same total peer pressure. Bitmagnet suggests
   broad cheap attempts against known endpoints; it does not prove that its
   400-job budget should be copied.
4. Apply the smallest correction supported by those results and repeat the same
   workload. Report new retained torrents/minute, published files/minute,
   metadata bytes, CPU/memory and transfer responsiveness.

**Transport caveat confirmed in the pinned local libtorrent:**
`src/torrent_peer.cpp` initially assumes uTP support; `src/torrent.cpp` selects
uTP under Both; `peer_connection::connect_failed` retries TCP after uTP failure.
But `utp_stream.cpp` initializes its timeout from `utp_connect_timeout`
(3,000 ms by default) and fails an unconfirmed socket on its first timeout.
The 15-second `peer_connect_timeout` therefore does **not** prove that every
silent uTP attempt delays TCP by fifteen seconds. Empty capability flags also
do not force TCP. Measure actual transport transitions before assigning this
as the dominant cause.

This investigation identifies a substantially broader and faster-turnover
reference pipeline. It cannot establish whether our present deficit comes
principally from candidate quality, connection establishment, metadata support,
scheduling or retention. The cohort measurement resolves that choice.

[factory]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/dhtcrawler/factory.go
[channel]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/concurrency/buffered_concurrent_channel.go
[config]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/dhtcrawler/config.go
[metadata]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/dhtcrawler/request_meta_info.go
[sampler]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/dhtcrawler/sample_infohashes.go
[pipeline]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/dhtcrawler/crawler.go
[triage]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/dhtcrawler/infohash_triage.go
[peers]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/dhtcrawler/get_peers.go
[requester]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/protocol/metainfo/metainforequester/requester.go
[requestconfig]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/protocol/metainfo/metainforequester/config.go
[requestfactory]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/protocol/metainfo/metainforequester/factory.go
[limiter]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/protocol/metainfo/metainforequester/limiter.go
[keyed]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/concurrency/keyed_limiter.go
[collector]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/protocol/metainfo/metainforequester/prometheus_collector.go
[persist]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/dhtcrawler/persist.go
[faq]: https://bitmagnet.io/faq.html
