# Rats Search and Bitmagnet: comparative feature inventory

Research snapshot: **2026-10-25**. This document inventories reference products
and prioritizes useful product capabilities. It does not select a TinyTorrent
schema, algorithm, storage policy or architecture, and does not authorize
implementation. The [torrent catalogue contract](../contracts/indexing.md) owns
TinyTorrent behavior and incorporates the relevant findings and owner
clarifications.

## Evidence and versions

**Implemented** means inspected source contains the behavior; it does not mean
we executed the application. **Partial** identifies a real interface whose
effect is narrower than its label. **Documented** identifies a documentation
claim not independently established in implementation. **Planned** identifies
an explicitly proposed capability. **Not established** is limited to the
owners inspected, rather than proof that no related code exists anywhere.
No binaries, crawlers, builds, benchmarks or dependency installations were run.

| Project | Inspected version and important boundaries |
| --- | --- |
| Rats Search | Latest release **v2.3.5**, published 2026-10-07, commit **8415a208025d3827f2f380f3bfe530557a10eaba**. Inspected master **032d297a62e895dd253d6df9095a6f8068f49f91** adds only its changelog. This is the current **Qt/C++ and Manticore** application, not the old Electron/Sphinx implementation under legacy/. Its bundled librats revision is **84884e78018aaad975b62d62b507f20ee527765b**. [Release][r-release], [commit][r-commit], [current application][r-readme]. |
| Bitmagnet | Latest stable release **v0.10.0**, published 2025-03-02, release commit **4d4042a**. Latest prerelease **v0.10.1-beta.1**, published 2026-05-21, points to the inspected main commit **e31b30d7351c269dc801876f92ebc59d31fee545**, eleven commits ahead of stable. Torznab profiles, streamed blocked-filter storage, classifier concurrency changes, a query parser fix and the latest TMDB fix are available in that beta, rather than stable v0.10.0. Detailed source findings below describe this pinned commit; a beta tag does not establish runtime verification. Documentation calls the product alpha. [Stable release][b-release], [beta release][b-beta], [revision comparison][b-compare], [project status][b-home]. |

The most important distinction is product purpose: **Rats combines a desktop
search application, peer catalogue exchange and a torrent client; Bitmagnet
concentrates on a self-hosted searchable catalogue, enrichment and indexer
integrations.** Neither currently delivers the complete file-first experience
we want without important qualifications.

## 1. DHT discovery

The four BEPs have different jobs. [BEP 5][bep5] locates peers for a known
infohash. [BEP 9][bep9], negotiated through BEP 10, obtains the info dictionary
from a peer. [BEP 51][bep51] obtains samples of hashes held by DHT nodes.
[BEP 33][bep33] obtains approximate swarm counts through Bloom filters. None
provides filename or keyword search over the network.

| Capability | Rats Search | Bitmagnet |
| --- | --- | --- |
| Protocol support actually used | **Implemented:** BEP 5 and BEP 9/10. Pinned librats also implements BEP 32 address-family negotiation and BEP 42 node-ID handling. Its audited KRPC surface has ping, find_node, get_peers and announce_peer, without BEP 51 sampling or BEP 33 scrape filters. [KRPC][r-krpc], [DHT node][r-node], [metadata handling][r-bt]. | **Implemented:** BEP 5, BEP 9/10, BEP 51 and outbound BEP 33 requests. Its DHT responder handles the basic queries and sampling, but does not produce BEP 33 filters in get_peers responses. Thus its BEP 33 support here is estimate retrieval, not a complete scrape responder. [Client adapter][b-client], [responder][b-responder], [metadata requester][b-requester]. |
| How hashes are discovered | An **announce-based spider**: walks random DHT targets, uses neighbor IDs and collects incoming announce hashes with the announcing peer endpoint. Separate Rats-peer search and replication can supply catalogue descriptions. [Crawler][r-crawl], [DHT node][r-node], [peer API][r-peer]. | A **sampling crawler**: bootstrap routers, discover nodes around rotating targets, sample eligible nodes, triage hashes, request peers, obtain metadata and persist. Incoming requests also contribute node contacts; announce handling is not shown as a second direct metadata acquisition pipeline. [Crawler][b-crawl], [sampling][b-sample], [responder][b-responder]. |
| Peer discovery for metadata | Can contact the announcing peer directly; otherwise librats performs DHT peer discovery. Peer catalogue descriptions are another source, distinct from payload peers. [Crawler][r-crawl], [metadata helper][r-btsub]. | Queries the node associated with a sample, then tries returned peers sequentially until metadata succeeds. The audited get-peers stage does not recursively follow returned nodes to resolve that same hash. [Peer lookup][b-getpeers], [metadata pipeline][b-request]. |
| Scheduling and resource bounds | Walk timer defaults to 100 ms; at most ten metadata fetches are active. An in-memory recent-hash set is capped at 10,000 with partial eviction, and indexed hashes are checked before fetching. The metadata enqueue path has no queue-size guard. No durable per-hash acquisition retry ledger was established. [Crawler and defaults][r-crawl], [bounds][r-crawlh]. | Bounded pipeline concurrency/buffers scale with a default factor of ten; peer-lookup concurrency is twenty times that factor and metadata request concurrency forty times it (200/400 jobs at default). Both channels buffer 100 entries at default; buffer capacity is distinct from concurrency. DB triage avoids fetching complete known entries and a transient encountered-hash filter suppresses repeats. Processing has a durable job queue; failed metadata acquisition itself does not have an equivalent persisted per-hash retry ledger. [Factory][b-factory], [triage][b-triage], [request pipeline][b-request]. |
| BEP 51 scheduling qualification | Not applicable to the inspected spider. | Samples up to 60 eligible nodes per second around a target rotated every ten seconds. It records returned intervals, **but shortens intervals above 300 seconds to 60 seconds when new hashes were found**. This does not fully respect BEP 51's requested backoff; it is a reference limitation, not a behavior to assume desirable. [Sampling][b-sample], [BEP 51][bep51]. |
| IPv6 and coverage | IPv4/IPv6 discovery exists in the dependency; actual end-to-end crawler coverage on a host was not exercised. Discovery depends on reachable routes, announcements and metadata peers. [DHT discovery][r-dhtdisc]. | Structures decode IPv6 contacts, but the outbound adapter consumes IPv4 nodes and metadata dialing uses tcp4. Full IPv6 crawler coverage and BEP 42 behavior were not established. [Adapter][b-client], [requester][b-requester]. |

Neither catalogue represents every torrent. Public-DHT sampling or observation
misses private/nonparticipating swarms, unreachable metadata and hashes not
encountered during the crawl. Bitmagnet explicitly says its crawler finds
hashes by chance and is not an arbitrary-hash lookup API. Its advertised crawl
rates are illustrative, hardware-dependent documentation claims, not measured
results from this investigation. [Bitmagnet FAQ][b-faq]

## 2. Metadata

| Capability | Rats Search | Bitmagnet |
| --- | --- | --- |
| Acquisition and validation | Negotiates ut_metadata, assembles 16 KiB blocks and checks the completed dictionary's SHA-1 against the requested hash. Pinned dependency limit is **4 MiB**. Parser checks v1 piece/length structure and safe path components. [Torrent metadata][r-bt], [parser][r-info], [constants][r-types]. | Negotiates ut_metadata, verifies peer handshake identity/extension support, retrieves blocks and verifies dictionary SHA-1. Metadata must be positive and **below 10 MiB**; default peer timeout is six seconds. [Requester][b-requester], [parser][b-parse], [request configuration][b-requestconfig]. |
| What reaches the catalogue | Hash, name, size, piece length, discovery time/address and returned file paths/sizes; later category, votes, descriptions and swarm observations. File records have path and size rather than explicit original index or attributes. [Conversion][r-convert], [domain record][r-domain]. | Twenty-byte hash, name, total size, private flag, timestamps, file status/count and source association. Stored multi-file rows retain original zero-based index, path and size. A single-file torrent uses the torrent's name/size/status without a separate file row. [Persistence][b-persist], [file model][b-filemodel]. |
| File completeness | Accepted file lists are stored as aggregate path/size documents. Default maxFiles=0 imposes no policy limit; a configured maximum rejects the **whole torrent**, rather than retaining its first files. This is still an extracted description, not preservation of every original torrent field. [Files storage][r-repo], [filters][r-filter], [configuration][r-config]. | Default save_files_threshold=100 retains **only the first 100 entries in original torrent order**, while keeping total count and an over-threshold marker. Later filenames are unavailable to search/classification. Raising the threshold may cause retrieval on rediscovery; reprocessing alone cannot recover omitted entries. [Persistence][b-persist], [triage][b-triage]. |
| Additional exclusions | Configurable collection policy can reject content based on size, count, title or detected type. [Filter policy][r-filter]. | Acquisition also rejects names shorter than eight bytes, payload below 1,024 bytes and invalid UTF-8/NUL in names/paths. These include **policy exclusions of potentially valid torrents**, not just protocol validity checks. [Checker][b-checker], [metadata pipeline][b-request]. |
| Retained metadata versus complete .torrent | Catalogue/portable dump does **not** retain raw info bytes. UI .torrent export uses an existing cache or live metadata, otherwise fetches metadata again, and wraps info bytes with known trackers. [Torrent exporter][r-texport], [engine][r-engine]. | Raw info bytes and outer .torrent fields are not archived. Optional save_pieces, off by default, stores v1 piece hashes and piece length; extracted fields plus those hashes do not guarantee reconstruction of the original info dictionary. [Persistence][b-persist], [pieces storage][b-pieces]. |
| v2 and file identity | Audited parser/identity is v1 SHA-1 oriented and requires v1 pieces/layout. Full v2 hashes, piece layers and per-file SHA-256 roots are not represented. Hybrid torrents may work through their v1 side. [Parser][r-info], [types][r-types]. | Fixed twenty-byte identity, SHA-1 validation and v1 file extraction. No stored v2 file roots or piece layers; pure-v2 catalogue retrieval is not implemented in this path. Homepage lists v2 as speculative. [Parser][b-parse], [file model][b-filemodel], [roadmap][b-home]. |

**BEP 9 metadata is the info dictionary, not a complete .torrent file.** In
particular, trackers and other outer fields are not supplied by that exchange.
Retaining a searchable file description, retaining exact info bytes and
retaining a complete torrent are three different capabilities. [BEP 9][bep9]

One source-level Rats caveat deserves separation from product claims: its
metadata helper adds a normal torrent and later removes it; metadata completion
can promote the torrent into downloading before that removal. Consequently the
helper's metadata-only description does not establish strict payload/disk
isolation. This was not reproduced at runtime. [Helper][r-btsub],
[completion/promotion][r-bt]

## 3. Search

| Capability | Rats Search | Bitmagnet |
| --- | --- | --- |
| Torrent versus filename search | Separate torrent and file queries exist. File search matches retained paths and returns **parent torrents with matching-path snippets**. Native GUI merges local name hits, file matches and streamed peer hits by torrent hash. [Search service][r-search], [GUI][r-window], [result model][r-resultmodel]. | General local search includes torrent names/hashes, retained file terms and enriched content. File terms receive lower weight. Results are **torrent-content records**, not individual matching-file rows. [Search vector][b-tsv], [torrent path terms][b-torrents]. |
| Query syntax | App escapes Manticore Boolean, phrase and proximity operators, intentionally leaving asterisk unescaped. Torrent index has prefix expansion with minimum prefix length three; file index lacks the same explicit prefix configuration. Do not attribute the whole Manticore grammar, arbitrary substring matching or Everything syntax to the app. No inline ext:/path:/size: parser was established. [Query escaping][r-query], [index configuration][r-manticore]. | Documented and implemented token search supports implicit AND, quoted phrases, dot for ordered following, pipe OR, exclamation negation, parentheses and suffix asterisk prefix matching. No leading/infix wildcard. Case/punctuation/Unicode normalization and forgiving incomplete-input parsing are provided. [Search syntax][b-searchdocs], [query implementation][b-query]. |
| Filters | Structured content type, safe search, inclusive parent-torrent size and file-count bounds. These are not per-file length/extension filters. API file search and peer requests require more than two characters; the local GUI path has a different guard. [Search service][r-search], [API router][r-api]. | Content type, catalogue source, tags, file type, language, genre, video resolution/source; release year is exposed in API. Some facets have AND/OR/NOT logic. Audited public search input does not supply numeric size/seed-count range filters. [Search schema][b-searchschema], [UI search controller][b-controller]. |
| Ordering and ranking | Torrent queries order before pagination; accepted fields include seeders, leechers, name, size, files, added and completed. File queries **limit MATCH rows first**, apply parent filters afterward and then sort the surviving page for seeders/size only. GUI header sorting concerns accumulated results, not a globally ranked distributed result set. [Repository][r-repo], [result model][r-resultmodel]. | PostgreSQL relevance ranking and explicit ordering by relevance, published/updated date, size, file count, seeders/leechers or name; API also supports hash. UI chooses relevance for text queries and publication order otherwise. Counts/facets may be estimates, marked accordingly. [Ordering][b-order], [controller][b-controller], [query engine][b-query]. |
| What a filename match guarantees | All sibling paths share one searchable document. Different query terms can come from different files; snippets are collected afterward without same-file verification. It is **not** guaranteed that one file satisfies the entire query. [File document/query][r-repo]. | Filename terms contribute to one parent search vector, together with name/content terms. A matched torrent does not identify a single file satisfying the entire query. Separate paginated Files API filters by parent hashes and ordering, without a filename query input. [Search vector][b-tsv], [files schema][b-filesschema]. |
| Presentation and remembered searches | Table with file-match decoration, peer provenance, details panel and hierarchical files. Top/recent, persistent recent-search history, favorites and activity feed are implemented. History keeps 100 recent case-insensitively deduplicated queries with time/count and clear/disable controls. [GUI][r-window], [details][r-details], [history][r-history]. | Table and expandable details show original and identified titles, size, dates, availability, quality/type chips, source/hash, tags, files and classification. TMDB details can add posters, overview, ratings, languages, genres and IDs. Permalinks and bulk magnet/hash copy are implemented; saved searches/custom feeds remain planned. [Results][b-results], [details][b-details], [release][b-release], [roadmap][b-home]. |

Neither reference establishes Everything-like instant arbitrary filename/path
search at our intended scale. No latency benchmark was run. Bitmagnet also
has a processing queue and a documented ten-minute search cache, with explicit
refresh, between ingestion and visible results. [Reprocessing][b-reprocess],
[FAQ][b-faq]

## 4. Downloads

| Capability | Rats Search | Bitmagnet |
| --- | --- | --- |
| Complete torrents | Integrated client accepts hash/magnet/.torrent, destination choice, pause/resume/remove, progress/speed/peer reporting and optional removal on completion. Created torrents can seed. Saved transfer/session state is distinct from the search catalogue. [Downloads][r-download], [engine][r-engine], [torrent creation][r-create]. | Does not download payload. Opens whole-torrent magnets through an external client and supports copying hashes/magnets, including bulk actions. [Magnet construction][b-torrents], [bulk actions][b-bulk]. |
| Select one or several files | **Partial interface only:** UI checkboxes and API change remembered selected flags. DownloadService explicitly says librats still downloads all files. No priority/selection call constrains payload. API documentation also describes index arrays while implementation treats arrays as booleans. [Selection implementation][r-download], [file widget][r-filesui], [API description][r-apidocs]. | File table is inspection-only. No selected-file download action or selection-bearing magnet generation is implemented in the audited UI/API. Selection can be done later in the external client, outside Bitmagnet. [File table][b-fileui], [magnet construction][b-torrents]. |
| Existing-client handoff | Native open/copy magnet and .torrent export. [Torrent menu][r-menu], [exporter][r-texport]. | Magnet handler and Torznab indexer integration with Prowlarr/Sonarr/Radarr and other compatible consumers. This is Bitmagnet supplying search results, not importing all of Prowlarr's search sources. Direct client integration is planned. [Servarr guide][b-servarr], [Torznab conversion][b-torznab], [roadmap][b-home]. |
| Alternative sources and playback | Peer descriptions, DHT metadata and known trackers provide alternative acquisition paths. Completed-content Open opens a directory. No implemented player/streaming workflow or exact identical-file alternatives across different torrents was established. [Engine][r-engine], [downloads UI][r-downloadsui]. | Media identity can associate related releases with the same content. This is not exact identical-file detection or combining payload across torrents. In-place seeding and direct download integrations are speculative. [Content results][b-details], [roadmap][b-home]. |

The important positive lesson is convenient handoff from an inspected result.
Neither project proves working **search file → select that file and siblings →
download only that selection** as one integrated experience.

## 5. Availability

| Capability | Rats Search | Bitmagnet |
| --- | --- | --- |
| Measurement | Despite scrape naming, app sends **UDP tracker announces**, numwant=0, to eight public trackers. It takes the response with the greatest seed count and that response's leechers; it does not sum populations. No BEP 33 implementation was established in the pinned DHT path. [Swarm scraper][r-swarm]. | **BEP 33** get_peers scrape requests obtain seed/peer Bloom filters. Missing filters mean no estimate. Current storage retains numeric counts rather than an archive of all scrape filters; no cross-node Bloom union was established in this path. [DHT client][b-client], [scrape stage][b-scrape], [source persistence][b-persist]. |
| Refresh | New indexed entries and opening details request observations; API can request one. Success cooldown is five minutes and failure quiet period one minute, with bounded background/interactive queues. A cooldown is **not** a guaranteed periodic refresh interval. [Tracker owner][r-trackers], [details][r-details], [bounds][r-swarmh]. | Complete torrents are scraped again when **rediscovered** and their estimate is missing or older than the default 30 days. This is not scheduled refresh of every stored hash or an on-demand arbitrary-hash health API. [Triage][b-triage], [defaults][b-config]. |
| Data independence | Availability enrichment follows indexing; a successful lookup updates counts and check time. Stored count defaults are zero, so zero does not reliably distinguish unknown from measured empty. [Tracker owner][r-trackers], [torrent record][r-domain]. | Metadata is saved before availability scraping; classification is delayed a minute to allow counts to arrive. Missing availability does not discard saved metadata. Source counts are nullable/timestamped; UI can show unknown. [Persistence][b-persist], [results UI][b-results]. |
| Limits and stale values | Public trackers may not know a swarm and can fail or disagree. A best tracker response is not proof that any selected file is downloadable. [Swarm scraper][r-swarm]. | FAQ explicitly calls BEP 33 a rough indicator and says actual downloadability requires trying the torrent. Source helpers take the maximum known count across sources, rather than a sum. [FAQ][b-faq], [source helpers][b-torrents]. |

One Bitmagnet data-flow limitation: source refresh updates source rows, while
search rows copy counts during processing. The refresh path does not enqueue
reprocessing, so displayed/sorted listing counts can remain older than source
values. Torznab reads hydrated source counts. This is a source-supported
observation, not a reproduced runtime failure. [Persistence][b-persist],
[processor][b-process], [Torznab][b-torznab]

## 6. Storage, retention, exclusions and deduplication

| Capability | Rats Search | Bitmagnet |
| --- | --- | --- |
| Database and schema | Bundled **Manticore searchd**, run as an app-managed separate process and accessed through local MySQL protocol. Tables include torrents, aggregate file documents, version, store and feed. It does not require the user to install a separate database server manually, but it is not embedded SQLite. [Manticore owner][r-manticore], [database access][r-db]. | A separately running **PostgreSQL** database powers storage, full-text search and jobs. Tables separate torrents/files/pieces, sources, hints, content/search records, tags, external content/collections, queues and blocked filters. [Installation][b-install], [initial schema][b-schema], [search migration][b-tsvmigration]. |
| Deduplication | Full normalized infohash checks, including checks around derived numeric document IDs. Existing entries may gain missing files/higher votes. This deduplicates torrents, not identical files across torrents. [Repository][r-repo], [indexing owner][r-indexing]. | Infohash identity with conflict-handled inserts/upserts; source/hash and file hash/index/path identities; external content identity can group related releases. This is not content-root deduplication of files. [Schema][b-schema], [persistence][b-persist]. |
| Acceptance/exclusion rules | Finite rules: optional max file count, torrent size bounds, adult name/category filter, case-insensitive include/exclude title regex and allowed content types. New entries are rejected as a whole, with a reason. Existing-entry merge can bypass new-entry filtering. [Policy][r-filter], [indexing owner][r-indexing]. | Customizable **YAML/CEL classifier** can examine title, retained filenames, extensions, size, type and other facts, then classify, tag or delete. Core banned keywords and configured type/XXX deletion are implemented; this is a general workflow language rather than a few simple collection filters. [Classifier guide][b-classifier], [core classifier][b-core]. |
| Preview before cleanup | **Implemented:** Settings Check Torrents performs a dry-run count and Clean Torrents removes matches. API accepts temporary filter overrides without saving them first. Preview is a count, not a frozen list of reviewed entries or a per-entry explanation report. [Settings][r-settings], [cleanup router][r-api]. | Single/bulk deletion and classifier deletion exist. A cleanup dry-run/affected-record preview was **not established** in the inspected UI/config/schema. [Delete resolver][b-mutation], [details UI][b-details]. |
| Remembering rejected torrents | Policy returns/logs a rejection reason, but no durable rejected-hash record was established. Remove/cleanup deletes rows without a tombstone. Recent in-memory crawler suppression does not guarantee future metadata fetches are avoided. [Indexing owner][r-indexing], [crawler][r-crawl]. | Acquisition/classifier/manual deletion paths feed a persisted **stable Bloom filter** used before fetching. It remembers blocked hashes approximately, without retained rejected metadata. It can have false positives and forget entries; records do not distinguish matched policy/reason or support precise rule-change reconsideration. [Blocking][b-block], [triage][b-triage], [filter wrapper][b-stable], [underlying filter semantics][stable-upstream]. |
| Overall catalogue size | No aggregate hard catalogue storage cap or automatic quota eviction was established in the audited storage/settings/ingestion owners. Log budget and database-transfer free-space checks are different controls. [Settings][r-settings], [configuration][r-config], [sync][r-sync]. | No aggregate hard storage cap was established. Main controls are file-list threshold, omitted pieces and classifier deletion. Documentation's rough disk-size guidance is not an enforced limit. [Configuration][b-configdocs], [FAQ][b-faq]. |

Bitmagnet's classifier deletion has a concrete source caveat: when a processing
batch produces no successful content rows, the processor returns before its
delete/block persistence path. A batch containing only delete outcomes therefore
does not establish unconditional deletion as the documentation suggests. Mixed
batches can reach that path. [Processor][b-process], [persistence][b-processpersist]

**Owner clarification:** [exclusion and cleanup](../contracts/indexing.md#exclusion-and-cleanup)
defines bans as remembered rule-based rejections after metadata is discarded,
with retrieval permitted again when lifted. This comparison uses that meaning,
rather than a manual infohash-entry workflow. The catalogue contract owns the full
admission, reconsideration and removal requirements.

Rats is the better reference for a small understandable rule surface and cleanup
preview. Bitmagnet is the reference for suppressing later acquisition after a
rejection. Neither supplies this whole clarified behavior with precise reasons,
ban lifting and policy reconsideration.

## 7. Index management

| Capability | Rats Search | Bitmagnet |
| --- | --- | --- |
| Start/stop independently of search | Runtime indexer setting starts/stops spider timers, leaving shared search/client/P2P available. It does not establish cancellation of every in-flight fetch. Indexer defaults on; headless mode has a force-spider option. [Application][r-app], [configuration][r-config]. | HTTP search, job processing and crawler workers can be selected independently at startup; omit crawler to search the retained catalogue. No GUI/API crawler start/stop mutation was established. These worker roles do not imply mandatory separate OS services. [Installation/CLI][b-install], [query schema][b-queryschema]. |
| Import torrent descriptions | .torrent import indexes through shared detection/filtering without automatically starting a download. Creation can index and seed local content. [API router][r-api], [creation][r-create]. | /import accepts newline-delimited JSON catalogue records with hash/name/size/source and optional media hints. It is **not binary .torrent ingestion**, and Item has no files/pieces/root fields. Files may be added after later DHT discovery. [Importer][b-importer], [import guide][b-importdocs]. |
| Export, transfer and merge | GUI/API portable **.ratsdb** export/import, merge, cancellation and resumable imports. Compressed framed records include descriptions/file lists/votes, not raw info dictionaries. Whole-database pull from sharing peers, reusable snapshots and resumable peer transfers are implemented. [Exporter][r-exportdb], [importer][r-importdb], [sync][r-sync]. | Documented pg_dump data export and psql restore/merge into a same-version migrated database. This is an operational database procedure, not a UI export workflow. Guide's selected-table export is explicitly partial and omits pieces, blocked filters and queue state. [Backup/merge guide][b-backup], [pieces schema][b-pieces], [blocking storage][b-block]. |
| Rebuild without rediscovery | Restoring an existing .ratsdb can recreate its exported searchable descriptions. Rebuilding a peer snapshot just rebuilds a served dump. No general lossless raw-metadata-to-search rebuild command was established; missing original metadata cannot be reconstructed from catalogue rows alone. [Dump format][r-dump], [snapshot][r-snapshot], [torrent exporter][r-texport]. | Individual/batch reprocessing regenerates classification/search rows from stored torrent facts, files, hints and sources. External APIs can be disabled; already known matches can be retained. It cannot recover omitted filenames, raw info bytes or v2 roots. [Reprocess guide][b-reprocess], [processor][b-process]. |
| Persistence/retries | Catalogue/config/history/favorites/transfer state persist. Crawler recent-hash memory is transient. Whole-index operation checkpoints and transfer resume are separate capabilities from a persistent crawl retry frontier. [Configuration][r-config], [sync][r-sync], [crawler][r-crawl]. | PostgreSQL catalogue and processing jobs persist. Queue retry/failure state supports processing, while metadata acquisition failure simply returns in the audited crawl stage. [Metadata stage][b-request], [queue schema][b-queueschema]. |
| Statistics and configuration | GUI status and API report torrent/file/size and peer statistics. JSON configuration, storage/network/filter controls and a headless console mode exist. Peer database sharing and random replication default on in current source, despite README's opt-in wording; REST API defaults off. [Configuration][r-config], [application][r-app], [API][r-api]. | Health indicator, worker status, queue/throughput dashboard, pending/retried/failed jobs, reprocess enqueue and queue purge. YAML/environment/CLI config inspection; Prometheus/status/profiling endpoints and optional Grafana tooling. Queue purge is not catalogue deletion. [Queue schema][b-queueschema], [configuration][b-configdocs], [observability][b-observe]. |

Neither reference's recovery features alone satisfy preservation of **all valid
file entries and sufficient acquired metadata for our full offline rebuild and
download-preparation requirements**. Bitmagnet can rebuild its deliberately
reduced searchable view; Rats can restore its exported descriptions. Those are
useful capabilities, with different preservation boundaries.

## 8. Other useful capabilities

| Area | Rats Search | Bitmagnet |
| --- | --- | --- |
| Classification | Built-in extension/type detection weighted by file size, with adult/category word lists. Finite logic, no exposed classifier programming language. [Classifier][r-classifier]. | Rich media recognition: content type, video resolution/source/codec/modifier, language, episodes and release group, plus TMDB canonical content, posters, descriptions, ratings and collections. YAML/CEL workflows can be extended/replaced and attach custom tags. Classification is inference, not proof of content or distribution rights. [Core][b-core], [classifier guide][b-classifier]. |
| Distributed search and sharing | Actual torrent/file keyword requests to connected Rats peers, streamed replies and provenance. Opening a remote result can fetch descriptive/file data from its offering peer, with conditional DHT fallback. Separate automatic small random replication and whole-database pull exist. There is no universal global ranking or completeness guarantee. [Peer API][r-peer], [replication][r-replication], [GUI][r-window]. | No live federation/distributed keyword search implementation established. Shared PostgreSQL or periodically merged databases are not federation. Pooling/federation is a roadmap idea. [Roadmap][b-home], [backup guide][b-backup]. |
| Social and descriptive additions | Favorites, votes, top lists and activity feed; tracker-site association can retrieve descriptions/posters from Nyaa/Rutracker with limits/cooldowns. Votes and peer claims do not authenticate payload. [Voting][r-votes], [feed][r-feed], [site scraper][r-sites]. | External TMDB ratings concern the identified film/show, rather than torrent-quality votes. Tags and external identities aid browsing. Community reports/reputation were not established in current schemas/UI. [Details][b-details], [schemas][b-queryschema]. |
| APIs and integrations | Shared dotted ApiRouter methods through REST/WebSocket cover search/history/details, filters/cleanup, torrent creation/import, downloads, database operations, votes/feed/config/stats/peers and tracker checks. GUI also uses the router internally. REST search is not automatically a broadcast distributed search. [Router][r-api], [API documentation][r-apidocs]. | GraphQL/GraphiQL covers search/files/tags, metrics/health/workers, delete/reprocess and queue operations. Torznab provides general/media-specific search for compatible consumers. Configurable Torznab profiles are available in **v0.10.1-beta.1**, not stable v0.10.0. [Endpoints][b-endpoints], [query schema][b-queryschema], [mutations][b-mutation], [beta release][b-beta]. |
| Native/operational conveniences | Cross-platform native Qt UI, tray behavior, language/theme/font choices, update checks and headless operation. Noise-encrypted Rats peer transport, mDNS, peer exchange/reconnection and NAT/relay mechanisms are wired. Library GossipSub capability should not be mistaken for a proven app feature. [Transport][r-transport], [configuration][r-config], [README][r-readme]. | Responsive embedded web UI, themes/translations, permalinks and detailed operations dashboard are released. Native payload downloading, in-place seeding, v2 support, bidirectional Prowlarr ingestion, saved searches/custom feeds, direct client integration and authentication/API keys/access levels remain roadmap/speculative items, not implemented checklist entries. [Release][b-release], [roadmap][b-home]. |

Two additional distinctions keep the inventories honest. Rats API documentation
promises broader peer-to-DHT file backfill than one existing-local-row branch
actually performs. Bitmagnet's homepage describes only a single GraphQL search
query, while the current schemas implement considerably more. Source-supported
capabilities above take precedence over either stale description.
[Rats router][r-api], [Bitmagnet schema][b-queryschema]

## What each does particularly well

**Rats Search** is the stronger reference for a native discovery/search utility:
one result with parent context, local history/favorites, convenient handoff,
small collection filters, cleanup counts and portable/resumable catalogue
exchange. Its peer network expands what a local user can find. Its main
limitations for our goal are partial file-selection downloading, parent-level
file matching, page-local filtering/order, lossy metadata storage and weak
durable rejection/retry semantics.

**Bitmagnet** is the stronger reference for an enriched catalogue: active BEP 51
sampling, BEP 33 estimates, expressive token queries, facets, content identity,
source provenance, reprocessing, Torznab and transparent job/health reporting.
Its main limitations for our goal are truncated file coverage, no independent
matching-file results or selective downloader, lossy/v1-oriented metadata and a
classifier/operations surface substantially larger than our core experience.
Its blocked-hash memory also does not preserve precise rule decisions.

These strengths are source-supported product observations, not comparative
performance benchmarks or recommendations to copy their storage/architecture.

## Most valuable capabilities for TinyTorrent

These priorities concern **user-visible functionality and preservation
requirements**, not mechanisms. Existing workspace, engine ownership, SQLite
and Add-workflow decisions remain settled; this research does not redesign them.

### Essential for the requested experience

1. **Real local file and torrent search.** Clearly select the result kind. A
   file result identifies its parent torrent and a file satisfies the whole
   file query; separate sibling paths must not manufacture a match. The
   [search contract](../contracts/indexing.md#search-and-add) prioritizes useful
   matches within bounded work, with explicit coverage and truthful count/order
   claims rather than mandatory exhaustive results.
2. **Inspect and download the intended files.** Inspect the parent and siblings,
   choose one/several files or the complete torrent, and carry that exact choice
   into the existing Add workflow. Merely remembering checkboxes is insufficient.
3. **Complete retained file coverage and recoverable metadata.** Preserve every
   valid entry and original torrent file index, the metadata needed to prepare
   downloads and rebuild search locally, and v2 roots when available. Distinguish
   acquired BEP 9 bytes from a complete .torrent. Both references demonstrate why
   a convenient searchable description is not enough.
4. **Search remains useful with indexing stopped.** Catalogue inspection and
   download preparation remain available. Crawling is optional and its resource
   use must respect active transfers, without making every search a network task.
5. **Rule-based admission and remembered bans.** Evaluate requested title and
   file/path exclusions before retaining a torrent; discard rejected metadata,
   remember enough to avoid repeated retrieval, and allow retrieval again when
   the ban is lifted. Show the matching reason. Preview effects before applying
   rules to existing retained records. No manual infohash-entry workflow is part
   of this requirement, and no classification language is needed.
6. **Understandable space and activity controls.** Show storage use and whether
   indexing is active, paused or failing; enforce the requested overall storage
   budget including search/database overhead. Provide useful progress without
   requiring an operations dashboard.
7. **Honest discovery and availability.** Describe discovered torrents, not a
   complete-network catalogue. Availability can be unknown/stale and must never
   block search or downloading. The already requested BEP 33 is useful context,
   not proof of seeders or selected-file availability.

### Optional improvements, ordered by likely usefulness

| Capability | Value and scope |
| --- | --- |
| Search conveniences | Extension/type/size narrowing, remembered queries, copy magnet/hash and useful provenance/check dates can remove repeated work. Advanced operators are useful only when their meaning stays understandable. This inventory selects no grammar. |
| Later imports, exports and catalogue transfer | Rats shows substantial value in portable/resumable exchange; Bitmagnet shows source-labelled imports and reprocessing. Keep import as the previously deferred capability, rather than enlarging the first release. Preservation now should not depend on implementing an importer now. |
| Refresh availability when it helps a decision | Better freshness on inspected results could help compare torrents. Tracker observations may complement BEP 33. Neither needs to be a prerequisite for retrieval or a permanent full-catalogue polling operation. Scheduling remains open. |
| Tags and lightweight inferred types | Useful for browsing and understandable exclusions. Rich TMDB identification, posters, genre/language facets and classification tuning are later enhancements, not requirements for filename search. |
| External API/Torznab integration | Useful if people want another application to search this catalogue. It is independent of the essential in-client Search-to-Add experience. |
| Identical-file alternatives across torrents | Potentially valuable later; preserve v2 file roots now as already requested. Media-title grouping or matching filenames does not establish identical file content. No grouping implementation is proposed here. |

Distributed peer search, whole-peer database sharing, votes/feed ranking,
programmable classification and a large administration stack have lower initial
fit. They add network behavior, trust decisions or workflows without completing
the central experience: **Search → find file → inspect parent torrent → download
selected files**. Their existence in a reference is not a reason to add them.

## Primary references

Code links pin the inspected revisions. Documentation links identify the
current published pages, whose discrepancies are called out above.

[bep5]: https://www.bittorrent.org/beps/bep_0005.html
[bep9]: https://www.bittorrent.org/beps/bep_0009.html
[bep33]: https://www.bittorrent.org/beps/bep_0033.html
[bep51]: https://www.bittorrent.org/beps/bep_0051.html
[r-release]: https://github.com/librats/rats-search/releases/tag/v2.3.5
[r-commit]: https://github.com/librats/rats-search/commit/032d297a62e895dd253d6df9095a6f8068f49f91
[r-readme]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/README.md
[r-crawl]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/net/crawler.cpp
[r-crawlh]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/net/crawler.h
[r-krpc]: https://github.com/librats/librats/blob/84884e78018aaad975b62d62b507f20ee527765b/src/librats/dht/krpc.h
[r-node]: https://github.com/librats/librats/blob/84884e78018aaad975b62d62b507f20ee527765b/src/librats/dht/node.cpp
[r-dhtdisc]: https://github.com/librats/librats/blob/84884e78018aaad975b62d62b507f20ee527765b/src/librats/subsystems/dht_discovery.cpp
[r-bt]: https://github.com/librats/librats/blob/84884e78018aaad975b62d62b507f20ee527765b/src/librats/bittorrent/torrent.cpp
[r-info]: https://github.com/librats/librats/blob/84884e78018aaad975b62d62b507f20ee527765b/src/librats/bittorrent/torrent_info.cpp
[r-types]: https://github.com/librats/librats/blob/84884e78018aaad975b62d62b507f20ee527765b/src/librats/bittorrent/types.h
[r-btsub]: https://github.com/librats/librats/blob/84884e78018aaad975b62d62b507f20ee527765b/src/librats/subsystems/bittorrent.cpp
[r-convert]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/net/librats_convert.cpp
[r-domain]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/domain/torrent.h
[r-repo]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/data/torrent_repository.cpp
[r-query]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/data/query.cpp
[r-search]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/services/search_service.cpp
[r-manticore]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/data/manticore.cpp
[r-db]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/data/database.cpp
[r-api]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/rest/api_router.cpp
[r-apidocs]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/docs/API.md
[r-window]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/ui/mainwindow.cpp
[r-resultmodel]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/ui/searchresultmodel.cpp
[r-details]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/ui/torrentdetailspanel.cpp
[r-history]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/app/search_history_store.cpp
[r-filesui]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/ui/torrentfileswidget.cpp
[r-menu]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/ui/torrentmenu.cpp
[r-download]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/services/download_service.cpp
[r-downloadsui]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/ui/downloadswidget.cpp
[r-engine]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/net/torrent_engine.cpp
[r-create]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/services/torrent_creator.cpp
[r-texport]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/services/torrent_exporter.cpp
[r-swarm]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/net/swarm_scraper.cpp
[r-swarmh]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/net/swarm_scraper.h
[r-trackers]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/services/tracker_service.cpp
[r-filter]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/services/filter_policy.cpp
[r-indexing]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/services/indexing_service.cpp
[r-settings]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/ui/settingsdialog.cpp
[r-config]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/app/config_store.cpp
[r-app]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/app/application.cpp
[r-sync]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/services/database_sync_service.cpp
[r-exportdb]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/services/database_exporter.cpp
[r-importdb]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/services/database_importer.cpp
[r-dump]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/services/database_dump.h
[r-snapshot]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/services/database_snapshot.cpp
[r-classifier]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/domain/content_classifier.cpp
[r-peer]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/peer/peer_api.cpp
[r-replication]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/services/replication_service.cpp
[r-votes]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/services/voting_service.cpp
[r-feed]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/services/feed_service.cpp
[r-sites]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/net/tracker_site_scraper.cpp
[r-transport]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/net/p2p_transport.cpp
[b-release]: https://github.com/bitmagnet-io/bitmagnet/releases/tag/v0.10.0
[b-beta]: https://github.com/bitmagnet-io/bitmagnet/releases/tag/v0.10.1-beta.1
[b-compare]: https://github.com/bitmagnet-io/bitmagnet/compare/v0.10.0...e31b30d7351c269dc801876f92ebc59d31fee545
[b-home]: https://bitmagnet.io/
[b-faq]: https://bitmagnet.io/faq.html
[b-client]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/protocol/dht/client/server_adapter.go
[b-responder]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/protocol/dht/responder/responder.go
[b-crawl]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/dhtcrawler/crawler.go
[b-sample]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/dhtcrawler/sample_infohashes.go
[b-getpeers]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/dhtcrawler/get_peers.go
[b-request]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/dhtcrawler/request_meta_info.go
[b-factory]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/dhtcrawler/factory.go
[b-triage]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/dhtcrawler/infohash_triage.go
[b-requester]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/protocol/metainfo/metainforequester/requester.go
[b-requestconfig]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/protocol/metainfo/metainforequester/config.go
[b-parse]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/protocol/metainfo/parse.go
[b-checker]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/protocol/metainfo/banning/checker.go
[b-persist]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/dhtcrawler/persist.go
[b-filemodel]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/model/torrent_files.gen.go
[b-pieces]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/migrations/00013_torrent_pieces.sql
[b-tsv]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/model/torrent_contents.go
[b-torrents]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/model/torrents.go
[b-searchdocs]: https://bitmagnet.io/guides/text-search.html
[b-query]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/database/query/query.go
[b-searchschema]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/graphql/schema/torrent_content.graphqls
[b-controller]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/webui/src/app/torrents/torrents-search.controller.ts
[b-order]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/database/search/order_torrent_content.go
[b-filesschema]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/graphql/schema/torrent_files.graphqls
[b-fileui]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/webui/src/app/torrents/torrent-files-table.component.html
[b-results]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/webui/src/app/torrents/torrents-table.component.html
[b-details]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/webui/src/app/torrents/torrent-content.component.html
[b-bulk]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/webui/src/app/torrents/torrents-bulk-actions.component.ts
[b-servarr]: https://bitmagnet.io/guides/servarr-integration.html
[b-torznab]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/torznab/adapter/search_result.go
[b-scrape]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/dhtcrawler/scrape.go
[b-config]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/dhtcrawler/config.go
[b-schema]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/migrations/00001_init.sql
[b-tsvmigration]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/migrations/00006_tsv.sql
[b-classifier]: https://bitmagnet.io/guides/classifier.html
[b-core]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/classifier/classifier.core.yml
[b-mutation]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/gql/resolvers/mutation.resolvers.go
[b-block]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/blocking/manager.go
[b-stable]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/bloom/stable.go
[stable-upstream]: https://github.com/tylertreat/BoomFilters/blob/master/stable.go
[b-process]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/processor/processor.go
[b-processpersist]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/processor/persist.go
[b-install]: https://bitmagnet.io/setup/installation.html
[b-importer]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/importer/importer.go
[b-importdocs]: https://bitmagnet.io/guides/import.html
[b-backup]: https://bitmagnet.io/guides/backup-restore-merge.html
[b-reprocess]: https://bitmagnet.io/guides/reprocess-reclassify.html
[b-queueschema]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/graphql/schema/queue.graphqls
[b-queryschema]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/graphql/schema/query.graphqls
[b-configdocs]: https://bitmagnet.io/setup/configuration.html
[b-observe]: https://bitmagnet.io/guides/observability-telemetry.html
[b-endpoints]: https://bitmagnet.io/guides/endpoints.html
