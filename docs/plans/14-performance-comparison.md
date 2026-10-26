# Catalogue performance: comparative code review

2026-10-25. Review only: no builds, benchmarks, installations or TinyTorrent
implementation changes. Reference checkouts were created at the owner's request
under `3rdParty/references/`. TinyTorrent remains on `main`.

## Verdict

This review describes the pre-correction source hashes below. The follow-up
acquisition candidate and its empirical acceptance gate are tracked in
[the current batch](14-discovery-feedback.md#current-batch-useful-acquisition-throughput);
the old limits quoted here are evidence of that review, not current settings.

TinyTorrent has a credible local, file-first catalogue implementation, but its
observed collection rate is not yet competitive with Bitmagnet's reported range.
The strongest source-backed opportunities concern acquisition latency hiding,
active-transfer resource ownership, foreground query scheduling and redundant
archive bytes. Replacing SQLite or adding more hash discovery does not follow
from the evidence.

Our best observed run published 92 torrents / 7,215 selectable files in 248.59
seconds: about 22.2 torrents/minute. Bitmagnet [documents 100–1,000/minute][b-faq];
that is a project claim, not a same-machine benchmark. No trustworthy current
Rats Search throughput measurement was established. At a constant 22.2/minute,
one million new torrents takes about 31 days; duplicate rediscovery can slow
growth further. The [acquisition measurements](14-acquisition-diagnosis.md)
separate fixed-cohort results, public-network variability and Debug overhead.

## Inspected source

| Product | Revision | Acquisition model |
| --- | --- | --- |
| TinyTorrent | Current reviewed working tree, base `763e82f` | One libtorrent session; 32 idle / 2 busy jobs by default, configurable 1–64; ready hints first, then cold fallback; independent peer probes; durable pending/retry records. |
| Bitmagnet | `e31b30d7351c269dc801876f92ebc59d31fee545` | Default 200 peer-lookup workers and 400 metadata hash jobs; positive peer responses only reach metadata; sequential direct TCP endpoint attempts. |
| Rats Search | `032d297a62e895dd253d6df9095a6f8068f49f91` | Announce-based spider; direct announcing-peer fast path; ten metadata jobs; 100-ms metadata queue dispatch timer. |

Bitmagnet's 100-entry channels are buffers, not its worker limits. Its six-second
deadline covers one endpoint; a hash can try several endpoints sequentially.
Rats' ten jobs and TinyTorrent's 64 jobs therefore cannot be ranked by their
numbers alone. Sources: [Bitmagnet factory][b-factory], [endpoint loop][b-loop],
[requester][b-requester], [Rats crawler][r-crawler], [limits][r-limits].

## Ranked findings

### 1. Acquisition has two independent limits beyond the number of hash jobs

**Confirmed mechanism; improvement size unproven.** TinyTorrent limits each idle
metadata torrent to 64 KiB/s, dropping to 16 KiB/s when busy
(`engine/src/Engine/Indexing.cpp:512,674`). These limits become libtorrent torrent
bandwidth channels, which participate in socket receive quotas
(`3rdParty/libtorrent/src/torrent.cpp:9817–9852`,
`peer_connection.cpp:5726–5780,5985,6192`); metadata does not bypass the limit
merely because it is protocol traffic. A large info dictionary therefore cannot
use the owner's available connection freely.

The pinned libtorrent metadata plugin additionally allows **at most two
outstanding metadata blocks per peer**, refilling after replies/ticks
(`3rdParty/libtorrent/src/ut_metadata.cpp:398–414`). Bitmagnet requests every
16-KiB block before reading the responses
([requestAllPieces][b-pieces], lines 266–285). With larger metadata and higher
RTT, this is a material difference in the amount of work in flight even after
a healthy peer has connected. It is not explained by a DHT protocol bandwidth
ceiling.

**Counterevidence:** most random candidates fail before valid metadata; successful
sample medians are already approximately 2.5–3 seconds. TCP-only did not improve
the measured cohort. Thus neither the bandwidth limit nor request window is
proven to dominate the whole 22/minute result. Bitmagnet's aggressive request
burst also has different peer/network costs.

**Highest-value next question:** distinguish connection/handshake waiting from
post-first-block receive time on successful larger metadata. Evaluate the
existing idle per-torrent limit separately from hash concurrency. Do not shorten
the whole acquisition to six seconds or change global transport, and do not
rebuild/change the dependency just to imitate Bitmagnet's request window.

### 2. The busy concurrency setting limits new starts, not existing work

**Confirmed execution path.** At idle-to-busy transition, `MaintainIndex` lowers
every existing handle's peer limit from eight to four and receive limit from
64 to 16 KiB/s, but does not reduce the outstanding job count. It subsequently
stops starting jobs above the configured busy limit
(`engine/src/Engine/Indexing.cpp:507–513,598–609`). Sixty-four existing acquisitions
can therefore coexist with active transfers even when the busy setting is two,
until work completes or stalls. Receive progress can extend their deadlines.

The shared 200-connection limit bounds the whole session; it does not reserve
those connections for accepted transfers. This matters before increasing the
idle ceiling: the 64-slot trace already reached 119 half-open peers. Neither
Bitmagnet's standalone crawler nor Rats' ten-job default establishes acceptable
sharing behavior for our client.

**Recommendation:** make the effect of busy concurrency explicit and protect
accepted-transfer capacity before widening idle work. Preserve useful progress;
the review does not prescribe a new preemption scheduler. Controlled transfer
impact remains unmeasured, so the present mechanism is not evidence that the
busy count is enforced immediately.

### 3. A 75-ms scan budget is not a 75-ms Search response guarantee

**Confirmed scheduling exposure; large-scale impact unmeasured.** Foreground
Search, detail requests, admission, node persistence, maintenance and cleanup all
use the same FIFO `indexWork` worker
(`Indexing.cpp:391,461,1043`; `engine/src/Store.cpp:62–68,110`). Projection yields
in bounded slices, but `Catalogue::Admit` first materializes every file and
evaluates rules before completing its archive transaction
(`engine/src/Catalogue.cpp:1091–1225`). A very large metadata admission or queued
maintenance work can delay a query before its scan budget even starts.

Search also has separate candidate-preflight and ordered-scan budget scopes
(`Catalogue.cpp:1543,1606`), plus the UI's 150-ms input debounce. Trigram narrowing,
ordered keyset continuation and bounded results are good choices, but elapsed
end-to-end latency includes all these stages and queue wait.

Bitmagnet separates ingestion from searchable-content processing, batches writes,
and can run alternative SQL plans concurrently with cancellation
([query owner][b-query], lines 38–83,145 onward). It also intentionally delays
visibility through processing/cache; that is not an instant-search target to copy.
Rats uses a dedicated full-text daemon, which is a different resource model,
not proof that changing TinyTorrent's embedded database is necessary.

**Recommendation:** protect foreground response time at the existing store
owner, particularly around large admissions, and judge first useful result
latency while collecting. The earlier million-torrent/zero-file SQL seek result
does not establish performance with ten million filenames or concurrent ingest.

### 4. The archive stores the info dictionary at least twice

**Confirmed redundant storage.** `Catalogue::Admit` puts the same metadata in
`snapshot.ti`, serializes resume data, then stores both the exact `info` blob and
that resume blob (`Catalogue.cpp:1189,1218–1223`). Pinned libtorrent's
`write_resume_data` writes `atp.ti->info_section()` into the resume dictionary
unconditionally when `ti` exists (`3rdParty/libtorrent/src/write_resume_data.cpp:122–125`).
Available original metainfo can contain another copy. This costs space and write
bytes for every accepted torrent, especially metadata dominated by v1 piece
hashes. It helps explain why comparing our disk bytes/torrent with Bitmagnet's
80-GB/ten-million guidance without matching preservation policies is misleading.

Bitmagnet normally omits pieces and retains only the first 100 files
([persistence][b-persist], lines 174 onward); Rats stores extracted descriptions.
Their smaller records do not satisfy our full rebuild/download-preservation
requirements.

**Recommendation:** retain one canonical copy of immutable info bytes while
preserving the acquired v2 hash-tree/resume facts and compatibility with existing
archives. This is a concrete space-efficiency opportunity, not a reason to drop
files, piece hashes or offline rebuilding. Current measured admission was fast;
the duplication is not established as today's network conversion bottleneck.

## Search comparison: useful differences, not a speed ranking

TinyTorrent applies the selected global order before its bounded result page and
rechecks each individual occurrence against one predicate. Its 20,000-visit,
75-ms budgets and 1,000-row UI ceiling disclose limited coverage rather than
establishing exhaustive counts. These are appropriate responsiveness safeguards.

Rats' filename search performs Manticore `MATCH` and **LIMIT before parent
filtering**, then sorts only that selected hit batch for seeders/size
([repository][r-repo], lines 438–502). Its aggregate file document can match terms
from different files. Copying that path would regress our global-sort and
same-file requirements even if it appears fast on a small result window.

Bitmagnet supports relevance ranking and budgeted estimated counts, but filename
terms contribute to a parent torrent's search vector rather than independent
verified file hits. Its pipeline retains fewer fields and has processing/cache
latency. We cannot infer which engine returns useful matches faster without
equivalent corpora, predicates, order and hardware.

## What to keep

### Functional priorities

The [full feature inventory](14-torrent-references.md) remains the authority for
reference-product capabilities. For the existing client, the useful comparison
is what a person can finish:

| Task | TinyTorrent | Reference advantage or limitation |
| --- | --- | --- |
| Find a file and download selected siblings or its whole torrent | Original indices and preserved metadata feed the existing Add workflow. | Rats tracks file selection locally but its backend downloads all files (`src/services/download_service.cpp:390–406`); Bitmagnet hands off whole-torrent magnets. Preserve our stronger integrated workflow. |
| Obtain a useful catalogue quickly | Active public-DHT discovery and durable retries exist; measured metadata conversion is below the competition's reported range. | Bitmagnet overlaps many endpoint requests. Rats also exchanges existing catalogues, which ordinary BitTorrent peers cannot provide. Acquisition speed is the immediate gap; imports remain future scope. |
| Understand collection progress | Previously one flat footer mixed totals, shown rows and queued work. | Bitmagnet's health/throughput reporting makes productive work easier to distinguish from activity. The current batch separates indexed totals, useful publications/second and activity with icons. |
| Reuse a successful search | Query grammar exists, but examples and remembered searches are absent. | Rats persists up to 100 recent queries and favorites. Short query help is a useful optional improvement; history is secondary to usable results. |
| Trust freshness | Results deliberately retain their publication scope until refreshed; Inspector availability does not automatically refresh after an observation. | New-result awareness and a dated availability refresh are concrete later improvements. They must preserve selection and never block Add. |
| Discover media through an automation ecosystem | General file lookup is the current purpose. | Bitmagnet's enrichment, media facets and Torznab integration serve that different workflow; they do not justify expanding this batch. |

The Inspector already holds parent context and source choice. Improve its
discoverability and freshness before adding another workspace or download flow.
Performance rankings beyond the recorded cohort are unproven: no equivalent
Rats or Bitmagnet run on this hardware was performed.

### Existing strengths

Keep the single engine/session, payload guard, ready-first acquisition, persistent
deduplication/retry frontier, exact rule-rejection reasons, full file preservation,
local rebuilding, indexed keysets and truthful bounded search. Bitmagnet's broader
latency hiding and Rats' fresh announcing-peer affinity are the useful comparisons;
TinyTorrent already has the latter path and does not lack active discovery.
The immediate product priority remains useful published torrents and files per
minute without making accepted transfers or local Search wait.

## Review record

Reviewed acquisition, admission/store queue, metadata protocol and principal
search execution paths in the three source revisions above. The report
distinguishes confirmed mechanisms from unmeasured performance hypotheses.
No code fixes, benchmarks, tests or compilation were performed. Reference
repositories remain read-only checkouts for the owner's requested future review.

TinyTorrent and pinned dependency content hashes for a focused follow-up:

| File | Git content hash |
| --- | --- |
| `engine/src/Engine/Indexing.cpp` | `2f4efa9a273b03840c4ea2f938e5303c080e9a75` |
| `engine/src/Catalogue.cpp` | `d9109cca35c9044926589e17df70130c9a733911` |
| `engine/src/Store.cpp` | `370f857e6adb90d7519fc635930c5c92c2071189` |
| `engine/inc/Store.h` | `205a8888e57a08d0f18bc42e47f672c95ba18884` |
| `engine/inc/Engine/State.h` | `e0171676c2c6e3c7d6d8928dace958d3ca96b406` |
| `engine/src/Engine/Policy.cpp` | `ae457aa0cbebef51d967f3cafc2dc6db36d1a9da` |
| `app/src/Search/Browser.cs` | `dec9eb30b42b980cd9779a545b6a14442e635e45` |
| `3rdParty/libtorrent/src/ut_metadata.cpp` | `8deb3d53cc4c9bda450960953aaa5c777af89001` |
| `3rdParty/libtorrent/src/peer_connection.cpp` | `b84993212cd424869506871e74a3f56f62aa52c6` |
| `3rdParty/libtorrent/src/torrent.cpp` | `50b2601e5834e620522dc2a2cac38a608129f942` |
| `3rdParty/libtorrent/src/write_resume_data.cpp` | `b1a539c3a6129aaeeecdeba1bc2709e19a7d6a7d` |

[b-faq]: https://bitmagnet.io/faq.html
[b-factory]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/dhtcrawler/factory.go#L87
[b-loop]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/dhtcrawler/request_meta_info.go#L44
[b-requester]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/protocol/metainfo/metainforequester/requester.go#L88
[b-pieces]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/protocol/metainfo/metainforequester/requester.go#L266
[b-persist]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/dhtcrawler/persist.go#L174
[b-query]: https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/database/query/query.go#L38
[r-crawler]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/net/crawler.cpp#L185
[r-limits]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/net/crawler.h#L94
[r-repo]: https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/src/data/torrent_repository.cpp#L438
