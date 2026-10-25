# Torrent indexing and search

Status: UI approved; candidate implementation plan, not yet implemented.
Owner decisions below are settled. The schema, search layout,
capacity mechanism, tuning values and implementation sequence remain proposals
to validate before implementation, not choices settled by reference research.

**Outcome:** Search → Find file → Download file, or inspect a source torrent and
open its Add dialog. Both use the existing Add workflow.

Read the [Rats Search and Bitmagnet inventory](14-torrent-references.md) for the
eight-area comparison, inspected revisions, source evidence and documented
plans. [Libtorrent capabilities](../contracts/libtorrent.md) records what our
pinned dependency supplies. Those references own research detail; this document
owns the proposed TinyTorrent behavior.

## Settled decisions

- Torrents remains the main workspace and owns transfers. Library remains the
  on-drive collection. Search is a separate, always-reachable workspace.
- Indexing starts off. Settings owns enablement and options; the first-use
  empty view also exposes the same switch so collection can start there.
  Both switches show On/Off and explain that discovery builds a searchable
  catalogue of torrent files.
  Disabling collection preserves search, inspection, downloads and local rebuild.
- **Owner ruling: the engine creates and exclusively owns its indexing
  database.** Use its existing session, pipe, settings and download owners.
- **Owner ruling: use SQLite, with no separately installed database engine or
  server.** Add native SQLite support to the engine and ship the required runtime
  with TinyTorrent. The WinUI app already uses SQLite; the engine does not yet.
- Build a general crawler: BEP 51 discovers public-DHT samples, BEP 5 locates
  peers, BEP 9 obtains metadata, and BEP 33 supplies optional swarm estimates.
  The catalogue contains discovered torrents, with no completeness promise.
- Preserve validated metadata for offline rebuilding, every valid file entry
  and original file index, and available v2 file roots.
- **Owner ruling: search prioritizes responsiveness.** Bounded scans and limited
  results are acceptable with honest coverage, counts and ordering. Exhaustive
  work is required only where its benefit justifies the cost.
- File search is file-first and collapsed initially. Merge occurrences with the
  same known file-content identity even when their names differ; expand a file
  to inspect its torrent sources. Metadata/index imports remain deferred.
- Download file chooses the source with the highest usable seed estimate, then
  peer estimate, when preparing the displayed result/action. The action binds
  that source occurrence and adds only its original file index with the
  configured destination and ordinary defaults. Download torrent opens the
  existing Add dialog for that source. Both use the existing Add owner.
- **Owner ruling: bans are rule-based exclusions.** Configurable title and
  file/path rules decide admission. Inspect previously unknown metadata once;
  on rejection discard it and its file list, retaining only compact rejection
  evidence that prevents another fetch while the ban applies. Lifting a ban or
  changing its rules permits reconsideration and metadata retrieval again.
  Preview removals of retained catalogue entries before applying them. The
  storage budget accounts for rejection evidence and index/database overhead.
  Bitmagnet and Rats Search are research references, not classification-language
  or architecture requirements.
- **Owner ruling: the storage budget triggers automatic stale-entry eviction.**
  Remove entries not seen for the configurable interval, oldest first. If no
  eligible entries remain, keep collecting and allow growth above the target.
  Eviction does not ban a torrent or use seed estimates as a measure of value.

## Research conclusions for the specification

The inventory owns the detailed findings. Use this map when finalizing the
affected behavior; reference throughput and concurrency are not our targets.

| Verified reference limitation | Requirement owner below |
| --- | --- |
| Opportunistic discovery does not establish complete network coverage. | [Crawler](#crawler-and-shared-session) |
| Extracted descriptions and truncated file lists cannot recover original metadata. | [Metadata and rebuild](#metadata-and-rebuild) |
| Parent-level matching can combine sibling filenames; limited results or local ordering need an explicit scope. | [Search](#search) |
| Rats' selection flags do not constrain downloaded payload. | [Add and large file lists](#add-and-large-file-lists) |
| Swarm observations can be missing, stale or disconnected from displayed values. | [Availability](#availability) |
| Neither reference supplies precise rule-based ban/lift behavior with discarded metadata. | [Retention](#retention) |
| Reference size controls do not define our age-based budget policy. | [Storage and capacity](#storage-and-capacity) |

History, richer filters/provenance, later import/export, tags and external
integrations remain optional improvements. Distributed search, peer database
sharing, votes/feed, rich media enrichment and an operations dashboard are not
added to the initial scope by this research.

## Candidate implementation sequence

Use these stages as a validation outline while finalizing the design. Their
completion criteria describe finished behavior, not a test run at each stage.
They do not authorize starting implementation or fix the candidate mechanisms
below.

**Owner ruling: implement the final paths directly and validate them together.**
Resolve shared storage, query and Add contracts before their callers depend on
them. Avoid intermediate implementations that a later stage replaces; reviewing
or testing that disposable code spends effort without protecting the delivered
feature. Stage 6 owns the validation scope. An earlier measurement or build earns
its cost only when its answer blocks the next implementation decision.

### 1. Establish engine storage and the search representation

Implement [storage](#storage-and-capacity) and the
[metadata archive](#metadata-and-rebuild). Verify the bundled SQLite tokenizer
and native engine deployment before relying on either. Build one projection
path for ingestion and local rebuild, with the [retention evaluator](#retention)
at admission before an archive is committed.

Choose one [search representation](#search), informed by Gozler's compact
scanning techniques and SQLite's actual capabilities. Start with the simplest
layout that meets the required matching and bounded-work behavior; additional
indexes or name/folder sharing need a concrete space or query benefit. If a
choice cannot be resolved from source and dependency documentation, measure only
that question before building its dependent code.

**Done:** the engine owns durable accepted metadata, compact rejection evidence
and one rebuildable search projection. Ingestion and rebuild use the same final
implementation; rejected metadata never enters the archive. Native SQLite
deployment and the selected layout are resolved before dependent work.

### 2. Add general discovery and guarded metadata acquisition

Implement [crawler ownership](#crawler-and-shared-session), persistent
deduplication/retry, deadlines and resource admission. Extend the existing
hybrid-conflict handler for crawler handles. Consult remembered bans before
metadata acquisition and the current rules again before retaining the result.
Include [initial ingestion of existing transfers](#crawler-and-shared-session)
through that same admission path.

**Done:** discovery and retry are persistent and bounded; acquisition uses the
existing metadata-only guard and respects accepted-transfer ownership, including
newly learned hybrid aliases. Already-available transfer metadata can populate
the catalogue without network retrieval.

### 3. Connect local search, Inspector and existing Add

Implement [search](#search), [presentation](#presentation-and-contract-updates) and
[Add integration](#add-and-large-file-lists), including bounded preview files and
compact file choices. Update both pipe codecs and their version together.

**Done:** with indexing off and a saved database, a person can find a file,
inspect its parent, select files across pages and confirm the existing Add
workflow. Whole-torrent Add exchanges no complete file list. Coverage, counts
and ordering stay truthful across result pages; obsolete queries cannot replace newer
results; reconnect preserves the ordinary draft/duplicate semantics.
Equal-content aliases keep a stable displayed/action source within the query's
eligible source scope; estimate updates do not silently change its filename.

### 4. Add independent BEP 33 observations

Implement [availability](#availability) using bounded shared-session requests.

**Done:** correct union estimates carry their observation times; missing, stale,
legacy, invalid or saturated responses do not block metadata retrieval, search
or Add. Indexing off stops automatic observations and retains dated results.

### 5. Expose retention rules and reviewed removal

Expose [retention](#retention) through the existing settings owner and a read-only
removal preview. Reuse the admission evaluator and rejection records established
with storage/acquisition rather than adding a second exclusion path.

**Done:** unchanged rules suppress repeated unwanted metadata fetches; lifting
a ban or changing its rules permits metadata retrieval again. Catalogue clearing
and search rebuilding preserve still-applicable bans. Cancelled/stale previews
remove nothing; confirmation affects only the reviewed scope. Accepted entries
retain every valid file, and removal never reaches transfers or payload.

### 6. Review and verify the integrated feature

Complete the repository's focused source passes and the owner's final review on
the finished implementation. Collect corrections before one planned integrated
build, then reuse its output for the checks below and app inspection under
[testing](../agents/testing.md) and [compilation](../agents/compilation.md).
Repeat only a check whose evidence a correction changes; a small edit alone is
not a reason to run tests. Temporary code and discarded prototypes need no
validation work.

The following are the minimum behavioral checks, not a request for a new test
method or harness per row. Reuse existing coverage and fixtures first. Add or
expand a test only for the stated failure that those checks do not already
catch, at the cheapest layer that can expose it. Record that gap for every new
test; omit redundant examples, platform tests and presentation assertions.

| Check on the final implementation | Failure and justification |
| --- | --- |
| Retain representative v1/v2/hybrid metadata, reopen the store and rebuild offline; preserve identities, original file indices/paths and available roots. Exercise the index owner's failed-write handling using existing failure support where available. | A parser can accept correct metadata while our persistence or rebuild loses it, making saved results or downloads wrong. Neither libtorrent validation nor SQLite transactions guard our serialization and recovery decisions. |
| Pass a query-matching occurrence through Search's compact choices to existing Add, including equal-content aliases and a hidden-padding index gap. Keep the bound source when estimates change. | The new caller or pipe representation can select another source, filename or file index even though existing Add works correctly. Extend existing Add/pipe checks for this boundary instead of retesting the download engine. |
| Exercise candidate verification and a deliberately exhausted search budget on a small known catalogue. | Candidate narrowing can omit a required query branch or return a false match; an incomplete empty scan can be reported as no matches. These are our matcher/planner and coverage decisions, which SQLite and the compiler do not validate. |
| Reject metadata, remember its rule reason across reopening, suppress another fetch, then release that reason. Include another already-recorded reason that still applies. | A broken policy lifecycle either repeatedly fetches excluded content or leaves it permanently suppressed after the rule is removed. Discarded metadata cannot answer future rule matches; test known release/eligibility, not predicted acceptance. |
| At the budget, evict an old entry while preserving a recently rediscovered one; with no eligible entries, allow growth. Reusable freed pages count toward recovered capacity. | Our destructive age/capacity decision can erase fresh entries, over-evict or stop collection contrary to the owner's policy. SQLite handles deletion, not eligibility or the meaning of the budget. |
| Check BEP 33's published Bloom-filter vector and reuse overlapping responses in the aggregate. | Wrong bit interpretation or adding node estimates produces plausible but inflated swarm counts. Our aggregation code needs this independent expected result; dependency parsing does not guard that mathematics. |

Inspect the completed Search → Find file → Inspect source → Download choices
journey once, including initial ingestion from an existing transfer and search
with indexing off. Use isolated data and existing inspection tools. Check the
changed controls' keyboard use and constrained layout; expand theme/language
inspection only where changed styling or text creates a specific risk. The
ordinary review covers unaffected Torrents and Library behavior.

Use one representative catalogue workload to measure useful first-page latency
and total storage footprint. Aim for ordinary warm searches within 100 ms;
increase scale only to answer an unresolved performance or capacity question.
Fixed million-/ten-million-row matrices and competing backend implementations
are not required. Query-plan inspection is for an identified slow access path.

**Done:** the final review's material findings are resolved, the justified checks
and app journey have recorded outcomes, and any remaining evidence gap is stated.
Restricted real-payload Transfer checks and installer release work retain their
separate authorization requirements.

## Storage and capacity

Create `index.db` under `Engine::State::directory`, alongside engine settings
and checkpoints. Create it on first indexing enablement; opening empty Search
alone creates nothing. An existing database
opens independently of the switch.

One concrete native indexing owner holds its SQLite connection, schema and
sequential migrations, archive/projection SQL, queries, retries and capacity.
Engine state owns its lifetime and libtorrent interactions. WinUI uses the
existing pipe and never opens the database.

Keep this a deep, concrete module: its interface exposes settings/status,
query pages, catalogue/file details, retained metadata for Add, and reviewed
retention operations. Parsing, candidate planning, SQL, retries and publication
stay private. The existing engine loop supplies session events and resource
pressure; existing Add receives prepared metadata. A generic repository,
pluggable search backend or per-phase service would expose implementation
choices without serving another caller.

Reuse Store's serialized worker/wake/completion pattern on an index-owned
instance: searches must not queue behind transfer checkpoints or block their
Store. Use short transactions, with parsing, network waits and response
serialization outside them. Give foreground queries turns between ingestion
batches. Keep optional index work within the engine's existing Exit deadline;
interrupted transactions recover through SQLite on reopen.

[Features.cs](../../app/src/Shell/Features.cs) and
[Database.cs](../../app/src/Shared/Storage/Database.cs) confirm that `library.db`
belongs to the window. Leave that file and ownership intact. Reuse its
transaction/versioning/recovery conventions, not its C# implementation.

[Engine.vcxproj](../../engine/src/Engine.vcxproj) currently has no SQLite
dependency. Adding the indexing database therefore includes adding native
SQLite access and runtime deployment to the engine. The C# database owner and
WinUI process are not dependencies of engine database access.

[The app](../../app/src/TinyTorrent.csproj) already references Microsoft.Data.Sqlite
and SQLitePCLRaw.bundle_e_sqlite3. Sharing that packaged native `e_sqlite3` runtime
is a deployment candidate, not an existing engine capability or an additional
owner ruling. Verify its native C interface, architecture, tokenizer support and
output location for the engine. Make runtime availability explicit for engine-only
builds/checks, app output and installer payloads, without requiring WinUI to run;
use one dependency/version authority for a shared runtime. Resolve any DLL by its
absolute installed path and call/free through the same runtime.
Do not change or rebuild `3rdParty/` for this integration.

The machine's system `winsqlite3.dll` supports FTS5, but that observation does
not establish features across supported Windows versions. Use the bundled
runtime consistently.

Use 1 GiB as the initial candidate storage budget, with its final default
supported by measurements rather than a predicted torrent count. Settings
exposes budget, actual use and the configurable not-seen interval. Measure the
whole footprint: database pages, metadata, projections, FTS, free pages,
rejection records, journals and index-owned scratch. The budget is a cleanup
target, not a byte-perfect ceiling; use SQLite's standard filesystem/recovery
and conservative working headroom, without custom VFS machinery.

At/above budget, remove retained entries whose last actual discovery predates
the configured interval, oldest first. For locally ingested entries never seen
through discovery, use their catalogue admission time as the age baseline.
Remove until usage is below target or no eligible entries remain. Then continue
indexing, including above budget if all
remaining entries are recent. A lower budget triggers the same cleanup policy;
it does not authorize deleting recent entries or stopping for the target alone.
Age eligibility, not popularity, determines automatic removal. The interval's
initial default remains a product candidate to validate; configurability and
the cleanup behavior are settled.

Run cleanup in bounded batches through the index owner's existing removal path,
as part of enabled collection, rechecking age before deletion so a new discovery
can preserve the entry. This
automatic storage policy is distinct from previewed content-rule cleanup and
Clear catalogue. Eviction removes archive/projections without a ban; a fresh
rediscovery can enter the ordinary deduplicated acquisition path again. It does
not delete payload, checkpoints or metadata held by an existing Add/transfer.

Reuse freed SQLite pages first. Measure active pages as well as file allocation;
recovered reusable pages satisfy the cleanup target even before the file shrinks,
so the same allocated free space must not trigger further eviction. Incremental
vacuum may return pages in bounded maintenance;
show actual allocation and estimated reclaimable space distinctly. A read or
failed vacuum is not evidence that another entry must be removed.

Keep short transactions, full commit durability and bounded query scratch. The
candidate serialized connection uses rollback journaling; WAL is not required
for concurrent readers in that execution model. SQLite's
[`max_page_count`](https://www.sqlite.org/pragma.html#pragma_max_page_count)
limits only the main file, and
[`journal_size_limit`](https://www.sqlite.org/pragma.html#pragma_journal_size_limit)
does not bound peak journal usage. Do not tie a hard page cap to this soft budget
or block permitted growth. Actual disk-full/storage errors stop affected writes
and defer collection with a usable error; preserve committed data and reads.

Clear catalogue remains a confirmed removal of archives/projections rather than
deletion of the database file: save indexing off and drain/cancel collection,
preserving applicable rejection records. Search remains reachable when empty.

## Database model

Use the [product vocabulary](../../CONTEXT.md#torrent-search). A catalogue entry
is not an accepted Torrent and never uses an accepted addition's TorrentId.
Validated info bytes are immutable; additional acquired metainfo retains its
origin. Search projections are replaceable, swarm observations become stale,
and pending work ends or retries. Remembered rejections outlive discarded
metadata. Keep these lifetimes distinct without imposing a table count or a
generic job framework; physical separation earns its place through ownership,
query cost or recovery needs.
The following candidate relations describe responsibilities, not a finalized
schema or an alternative metadata format:

| Relation | Key and responsibility |
| --- | --- |
| `catalogue` | Internal integer catalogue key, unique nullable full v1/v2 BLOB hashes, admission time and nullable discovery times. Validated identities remain independent of managed transfers. |
| `metadata` | One row per catalogue key containing exact info bytes and additional metadata actually obtained. Cold data read for rebuild, retention evaluation and Add, not copied into search rows. |
| `pending` | Unique unresolved lookup key, next eligible attempt, failure/backoff and observation times; a completed admission removes it transactionally. |
| `nodes` | Typed DHT endpoint, node identity and next sampling eligibility; a bounded frontier, not catalogue membership. |
| `estimates` | One latest usable observation and optional retry timing per catalogue key; missing counts remain null. |
| `names` | One exact spelling per name ID, its normalized matching text and extension. Repeated names across entries share this row without merging file identity. |
| `folders` | Per-entry parent/name links; parent null means a root, not an invented sentinel. |
| `files` | Primary key `(catalogue_id, file_index)`, name/parent links, size, attributes and optional v2 root. Rebuildable from retained metadata. |
| `summaries` | A complete searchable entry's title/name reference, totals, projection version and monotonically ordered publication marker. Insert after its file projection is complete. |
| `rejections` | Compact known lookup/hash evidence, matched-rule context, reason and decision time. Remembers a rule-based ban independently of discarded catalogue metadata and permits reconsideration when lifted or its rules change. |

Use SQLite AUTOINCREMENT for catalogue and publication identities whose
watermarks must not reuse committed values; ordinary rowid allocation can reuse
deleted high values. Enforce hash/root lengths, unique full identities and
parent/entry foreign keys
at ingestion. A nullable v1 or v2 hash denotes absence; a pending 20-byte lookup
key is not mislabeled a verified v1 identity. Preserve the identity row during
local rebuild; clear/rebuild recreates derived name/folder/file/summary data.
Use full verified content hashes in catalogue Add references, so an old UI
reference cannot select different content if an internal row number is reused.

Keep the archive BLOBs in a cold rowid table initially. Although large BLOBs can
favor external files, one transactional SQLite archive avoids a second file
recovery/retention mechanism; measure large-metadata cost before adding one.
Evaluate WITHOUT ROWID for compact composite-key file/frontier records, keeping
ordinary INTEGER PRIMARY KEY rows where rowid or incremental BLOB access is
useful. [SQLite's guidance](https://www.sqlite.org/withoutrowid.html) makes this
a layout choice to measure, not a rule for every table.

Index lookup/retry keys, verified hashes (including truncated-v2 lookup), parent
links, last-discovered time for age eviction, name-to-file/folder references and
measured size/order access paths.
A B-tree over a name does not by itself accelerate arbitrary substring search.
Keep FTS and its
name rows coherent in their owning transaction. Sequential schema migrations
preserve accepted metadata and applicable rejection evidence. Projection version
changes rebuild derived data instead of deleting their source.

## Metadata and rebuild

The exact validated bencoded info dictionary is the durable source of truth.
Preserve verified full v1/v2 hashes and discovered lookup-key relationships.
BEP 9 transfers that dictionary, not the original complete `.torrent`.
Retain additional fields, tracker/web-seed information and v2 piece layers
actually obtained, recording their origin/completeness; absent information
remains unknown.

The pinned libtorrent exposes immutable bytes through
`torrent_info::info_section()`, original ordering through `layout()`, and
available roots through `layout().root()`. Its
[loader](../../3rdParty/libtorrent/src/load_torrent.cpp) accepts absent v2 piece
layers and validates layers that are present. Both Add preparation and rebuild
use libtorrent, the existing metadata parser.
Decode retained dictionaries under their acquisition bounds: the session permits
metadata up to 30 MiB, while the standalone file loader defaults to 10,000,000
bytes and a lower decode depth. Default file-loader settings must not reject
metadata already validated and retained through BEP 9. Use the bounded
`from_info_section` path for that dictionary, preserving the original incoming
file path's safety policy.

Represent every indexed file by catalogue entry plus its original file index.
Retain original path, length, attributes, padding/symlink distinctions and an
optional v2 root. Presentation may hide padding while retaining its index.
Duplicate filenames and zero-length files remain valid entries. Preserve raw
metadata when libtorrent sanitizes a download path. Respect its untrusted-input
limits and report refusal; introduce no application file-count cutoff or silent
truncation.

A v2 root is a SHA-256 Merkle root, not a flat SHA-256 file checksum. Retain it
and file length as the known file-content identity for grouping, independent of
name or parent torrent. Preserve every source catalogue/file identity beneath
the group. Ordinary v1 piece hashes and torrent info hashes are not per-file
identities; entries without a known file root remain separate. Empty files
without a root also retain their source identities. Use the retained v2 roots
already supplied by metadata rather than fetching payload to calculate hashes.

Evaluate the current retention rules before archiving metadata. On rejection,
retain only compact rejection evidence and release the fetched dictionary/file
list, including index-owned temporary copies. The rejected torrent is not a
catalogue entry and has no searchable file projection. On acceptance, commit the
archive before reporting
it retained or releasing its last useful reference. Build replaceable
file/name/search projections in bounded batches, persisting progress. Publish
the catalogue entry as fully searchable when all
entries are represented.

Local rebuild uses that same projection code over retained metadata, with
indexing off and the network unavailable. Reuse freed database space instead of
copying the whole catalogue. Persist progress; present rebuilding/incomplete
coverage distinctly from no matches. Insufficient space preserves the archive.
Search repair never deletes an unreadable authoritative archive. Rebuilding
search does not require discovery or another BEP 9 download.

Active-transfer checkpoints remain authoritative for destination, chosen files,
pieces and progress. Preserve available metadata/hash evidence, but initialize
a new Add without crawler transfer state. Missing v2 piece layers can be
obtained through normal peer hash exchange; they do not prevent rebuilding
names, file indices, lengths and roots locally.

## Crawler and shared session

Use the session created by
[State::Start](../../engine/src/Engine/Session.cpp). Begin after its confirmed
route is ready. Seed a paced BEP 51 walk from live DHT contacts, spread targets
across the 160-bit keyspace and follow returned nodes. Persist traversal progress
and per-node eligibility; honor returned sample intervals. Bound requests and
the pending frontier. Retire expired contacts while retaining accepted metadata.

Treat sampled 20-byte keys as discovery evidence: some are truncated v2 hashes.
The pinned
[torrent::set_metadata](../../3rdParty/libtorrent/src/torrent.cpp) validates that
case and reveals the full hashes. Merge aliases only after validated metadata.
Pending, in-flight and archived deduplication share one admission path.
Passive DHT observations enter it while indexing is enabled.

When collection starts with indexing enabled, including engine startup,
enumerate existing accepted transfers in bounded batches and submit all locally
available validated metadata through that same
deduplication, exclusion and archive/projection path. This immediately populates
the catalogue without DHT requests, peer connections or a second metadata fetch.
While enabled, admit metadata that subsequently becomes available to those
transfers through the same path. Keep their handles, file choices and download
state under the existing transfer owner. Local ingestion is not a DHT sighting:
leave Last seen unknown until actual discovery. The storage policy above owns
age eligibility for these entries.

Refresh a known entry's last-discovered time from actual discovery even when no
metadata fetch is needed; deduplication must not lose that observation. Coalesce
these updates in bounded batches before age-based eviction decisions. Metadata
loads, searches, inspections and availability checks do not count as discovery.

Before creating a handle, check accepted torrents, previews, pending additions
and removals. Harvest existing metadata or await its owner. Crawler-owned
handles use the established
[Guard](../../engine/src/Engine/Additions.cpp) before acquisition; this prevents
payload download/creation. After retention remove only the crawler handle,
without delete-data. A racing Add waits for its removal and enters ordinary
preview/duplicate handling.

Prechecks cannot prevent a hybrid alias discovered later. Libtorrent can pause
both handles. Extend the existing conflict owner in
[Alerts.cpp](../../engine/src/Engine/Alerts.cpp), which currently releases
previews, to release crawler handles too, retain `alert.metadata`, record
verified aliases, clear the incidental error and reapply the accepted owner's
intent. Keep the existing policy when both handles belong to accepted transfers.

Persist metadata-job admission before acquisition and failure/backoff before
retry. Jobs have finite deadlines and bounded concurrency. Reconcile lost alerts
and removal completion so slots are released. A timeout is a failed observation,
not proof of a dead torrent.

Yield network and disk work to active transfers, verification and moves through
admission, bounded connections/bandwidth and short database batches.
Peer-class priority alone does not prioritize unlimited-rate connections.
Crawling obeys the existing pause, DHT, proxy/adapter and shutdown policies and
never uses a direct-route fallback. Search stays local under those holds.
Crawler handles never become visible transfers or Library membership.

## Availability

Use the shared session's `dht_direct_request()` for BEP 33
`get_peers` requests with `scrape=1`. Schedule bounded work independently,
favoring current inspection/visible entries over optional background refresh.

Merge valid Bloom filters before estimating seed/downloader populations;
adding per-node counts double-counts overlapping observations. Store the latest
usable estimate, observation time and response coverage. Treat missing, stale,
legacy, invalid and saturated replies explicitly as uncertainty. Display a dated
**estimated swarm**, with no claim about a specific file's availability.

A failed check preserves the last usable estimate and its original observation
time; it must not become a measured zero or make an old estimate look fresh.
Distinguish observation source and latest attempt outcome without retaining a
history of swarm checks. Presentation reads the current observation rather than
a second independently refreshed copy in the search projection.

Metadata collection, searching and Add never wait for availability checks.
Indexing off stops automatic collection/scrapes and keeps the saved observations.
Protocol references: [BEP 33](https://www.bittorrent.org/beps/bep_0033.html),
[BEP 51](https://www.bittorrent.org/beps/bep_0051.html).

## Search

Return useful verified matches promptly. Bound candidate visits, scan time,
memory and output with internal budgets; a row limit alone does not bound the
work of counting or sorting. Prefer cheap indexed narrowing and useful match
order. Stop rather than consume disproportionate resources proving completeness.
This limits query coverage, not match correctness or retained metadata coverage.

Replies distinguish exhausted scope from a work/result limit or cancellation.
Show how many matches were found; claim an exact total only when established
cheaply or when a justified operation explicitly needs it. A limited result can
say `200 found · search limited`; an empty limited scan does not establish no
matches. Counts described as estimates or lower bounds must be labelled as such;
no estimation subsystem is required. Refining the query or requesting more may
continue bounded work. Exact identity checks and reviewed removal still provide
the evidence their operations require; ordinary searching need not pay that cost.

One engine-owned search interface accepts typed query, target, sort and page
intent. Organize its implementation as Gozler does, without a class per phase:

1. Parse the text and each structured filter into one predicate model. Space
   means AND, `|` OR, `!` exclusion and `*`/`?` whole-target wildcards; quotes
   preserve phrases. As in Gozler, `a|b c` means `(a OR b) AND c`. Initial
   functions are `ext:`, `size:`, `path:` and `torrent:`. Parse filters separately
   before combining them, so an unfinished quote/operator in text cannot absorb
   a filter. Paths belong to torrent metadata, never the current Windows folder.
2. Compile once per query: normalize text consistently with ingestion, compile
   wildcards and order cheap numeric/identity checks before expensive path work.
   Preserve original spellings; normalize Unicode form and case without silently
   removing accents. Each triggering matched occurrence must satisfy the query
   against its own path/parent torrent; sibling files cannot manufacture a hit.
3. Select safe candidates, then verify with that compiled predicate. An
   exhaustive result requires a candidate superset; bounded exploration may stop
   earlier when it reports limited coverage.
   A required positive literal can narrow candidates; an OR arm, negation or
   ancestor-path match cannot justify claiming completeness from a filename-only
   shortcut that loses hits.
   Expand matched folder components through parent links, caching work per
   folder/query rather than rebuilding every full path. A literal spanning path
   components cannot narrow a single name with grams crossing the separator;
   use path-aware candidates or the same bounded fallback. Short/broad terms scan
   shared names within the same executor, with cancellation, not another matcher.
4. Order the found matches deterministically and materialize only the requested
   rows, using catalogue/file identity as stable tie-breaks. An index may provide
   global ordering cheaply; otherwise describe the order as applying to the
   found subset. Sort and count requests must respect the same work budgets.

The starting candidate is external-content FTS5 over `names.folded`, sharing its
rowid and keeping raw text in `names` rather than copying content into FTS. A
compact trigram index with `detail=none` stores candidates, not phrases or relevance.
Build bound MATCH expressions from quoted three-character grams of mandatory
literals; verify their ordering/adjacency in the canonical matcher. Longer MATCH
tokens are unsupported with this detail setting. Never pass user query syntax
directly to MATCH or assume LIKE with ESCAPE uses the trigram index.
[SQLite's trigram rules](https://www.sqlite.org/fts5.html#the_trigram_tokenizer)
own these restrictions. Use case-sensitive tokenization on already-normalized
text to avoid a second folding authority. Retain one chosen layout. No relevance
scoring, term positions or semantic classification
is required for filename search.

Relevant Gozler techniques are demonstrated in
[search.rs](../../3rdParty/goz/crates/gozler-core/src/search.rs),
[store.rs](../../3rdParty/goz/crates/gozler-core/src/index/store.rs),
[engine.rs](../../3rdParty/goz/crates/gozler-core/src/query/engine.rs),
[paths.rs](../../3rdParty/goz/crates/gozler-core/src/query/paths.rs) and
[rows.rs](../../3rdParty/goz/crates/gozler-core/src/query/rows.rs): interned names,
required-literal filtering, cheap predicates first, shared path components,
limited selection before sorting and row materialization on demand.
Adapt these techniques to disk-backed metadata; they do not require Gozler's
service or a second complete resident catalogue.

Pipe reads return bounded result/detail pages within the existing frame and
deadline. Keep one latest pending query and cancel obsolete work. Capture a
completed-publication watermark for the query; later ingestion appears on a
refresh instead of repeatedly invalidating a user paging through results.
Clear, rebuild, removal or identity reconciliation invalidates affected queries.
Counts, ordering and pages describe the same scope and its coverage. No database
transaction survives while the user scrolls. Use seek cursors where an indexed
order supports them, or bounded scan continuation; do not enumerate all matches
merely to construct a page. [SQLite's scrolling-window example](https://www.sqlite.org/rowvalue.html#scrolling_window_queries)
explains increasing OFFSET costs. Ordinary ordering uses immutable metadata; swarm observations
can refresh separately without moving rows under the person.

Actual TableView behavior is a constraint:
[Source.Capture](../../lib/TableView/src/Body/Source.cs) enumerates its full
source on the UI thread, and
[Sorting.cs](../../lib/TableView/src/Table/Sorting.cs) sorts that snapshot.
Supply bounded batches with the found range and coverage through the existing
TableView scrolling; add no Previous/Next pagination controls or replacement
virtualizer. Show a total only when established. A sort choice
requests engine ordering within the declared scope; partial results must not
imply the globally first/best matches. Disable misleading page-local sortable
headers. Existing TableView stays intact.
File search supplies bounded content groups with bounded batches of torrent
sources as their direct children, collapsed initially. Any matching occurrence
can make its content group a hit; include known equal-content sources under it
even when their filenames differ. Explicit parent-torrent restrictions continue
to constrain the sources. Never combine terms from different files to create
a match. Root counts and ordering refer to distinct file-content results; source
counts and pages refer to that group's retained occurrences. Indicate limited
coverage separately for groups and sources when either lookup stops early.
Choose a deterministic matching occurrence for the root filename and filename
order within the declared query scope. Prepare the default download source
separately, binding it and its eligible source scope as described under Add
below. When its original name/path differs from the displayed matched alias,
disclose the actual download source/path in the existing action tooltip/Inspector.
Prepare each finite
hierarchy before giving it to TableView; expansion does not start asynchronous
work inside the control.

## Add and large file lists

Extend the existing preview source representation with a bound catalogue/file
occurrence and query/action context. Resolve retained metadata inside the engine, then converge with
external-source preparation before duplicate handling, destination validation,
choices and confirmation. Keep
[AddDraft](../../app/src/Torrents/AddDraft.cs), the shared FileSelection and
[Add](../contracts/engine/addition.md) as their existing authorities.

Bound Add itself:
[Describe](../../engine/src/Engine/Previews.cpp) currently serializes every file,
and AddDraft loads that array and submits a dense priorities vector. Range
preview/detail files as well as Search Inspector files. Keep default wanted
choice plus original-index/range overrides in the existing file-choice owner;
expand to libtorrent priorities only inside the engine's validated Add path.
Use this compact contract for ordinary and catalogue Add, replacing the dense
wire form. Whole-torrent Add requires no full file-list exchange.

Choices survive paging and refer to original indices, including gaps for hidden
padding. Keep a multi-file choice within one parent torrent. Existing duplicate
merge/Show in Torrents, cancellation, disconnect and uncertain-confirmation
behavior remain authoritative. Send catalogue identity and choices through the
pipe, not metadata blobs, temporary exported files or a direct-download path.

The main table offers Download file and Download torrent. When preparing a
result/action, rank eligible sources by usable seeds, then peers, with stable
catalogue/file identities breaking ties. Unknown estimates sort after known
ones; if all are unknown, use the stable order without blocking Add. Eligibility
retains the query's explicit parent-torrent restrictions and captured scope;
filename-mismatching aliases may still be equal-content sources as above.

Bind the chosen default's verified catalogue/file identity, original index and
original filename/path to the result action. Keep the matching representative's
filename/order separate from this source choice. The action tooltip/Inspector
names the actual download occurrence even when its path differs from that alias.
Estimate updates do not silently switch the bound default or change the root's
name/order; refreshing the result can prepare a new highest-estimate default.

At acceptance validate the bound occurrence, content identity and source scope,
then select only its original file index with the configured folder and ordinary
defaults through existing Add's automatic acceptance. If the source/scope is
unavailable, report/refresh through the existing outcome path instead of silently
choosing another occurrence. Keep ordinary layout/original-path behavior; do not
rename the chosen source to another alias. No Add dialog or new download owner
is introduced for file download. Whole-torrent download opens existing Add for
the same bound source with the ordinary all-files choice. An explicitly chosen
expanded source binds that occurrence instead.

## Retention

Use one finite collection of exclusion rules from the authoritative prototype
editor. Conditions within one rule are AND; independent rules are OR. A rule
matches when all its configured torrent conditions and its optional quantified
file condition hold. Any matching rule rejects the whole torrent; if none
matches, accept it with all valid files. Extension include/exclude comparisons
are fields inside that rule, not a separate global whitelist policy. Use the
literal/wildcard primitives already needed for search, without a classifier
language or another evaluator. Validate fields/ranges before saving.

Search filters narrow visible results while retaining the metadata. Retention
rules reject whole torrents and discard their metadata; persistent per-file
hiding is a separate capability outside this scope.

Rules may combine file name/path, extension and per-file size conditions. For
example, reject a torrent containing an MP3 file larger than 30 MiB. Every file
condition in one rule must match the same file; an MP3 and an unrelated large
video cannot satisfy that rule together. A matching exclusion rule rejects the
whole torrent, keeping unwanted metadata out of the catalogue.

Use the prototype's finite rule editor: torrent title, total size and file
count, plus file name/path, extension and size with Any/Every/No matching file.
Evaluate the combined file conditions on one non-padding entry before applying
that quantifier; Every requires at least one such entry. Text exclusions and
numeric comparisons remain fields, not a general expression language. Validate
empty rules, ranges and explicit byte units before their existing Apply flow.

The index owner evaluates verified metadata at admission, reconsideration and
removal preview through the same policy implementation. A lookup key alone
does not establish names, size or extensions; first-time decisions may require
metadata acquisition. Availability estimates do not determine retention.
Capacity and network waits are deferred work, not rule exclusions.

Keep policy configuration in the existing engine settings owner, rather than
duplicating editable rules in SQLite. Recheck current rules and remembered bans
before retaining a completed network result. Rules express the person's content
criteria; metadata names and inferred types do not establish distribution rights.

**Admission and bans:** a rule can exclude a title containing `xxx` or any
torrent containing a file under an unwanted path such as `tmp`. A new DHT lookup
key supplies neither title nor paths, so the first encounter normally requires
metadata retrieval. Evaluate it before catalogue admission. A rejected torrent
keeps no raw dictionary, title/file list or search projection; release fetched
metadata and any index-owned temporary copies. Retain only a compact rejection
record with known identity/lookup evidence, matched-rule context, reason and
decision time. Hashes are internal recognition keys, not something the person
must enter to create a ban.

Check that remembered decision before subsequent metadata work: **ban active →
skip retrieval; ban lifted → allow retrieval again**. Reconsider affected
decisions when rules change, and evaluate current rules after a permitted fetch.
With rejected metadata discarded, that re-evaluation may need another download.
Indexing off still prevents automatic retrieval; lifting bans does not start a
mass refetch. Search rebuild and catalogue clearing preserve applicable bans.
These rules govern indexing, not existing transfers or manually supplied Add
sources.

Before applying an added, edited or removed rule, preview only its effect on
remembered bans. Show distinct currently suppressed records, those still
suppressed by another recorded reason, and those released for retrieval, then
Apply or Cancel. Removing/changing a rule releases its attributed reasons;
another already-recorded match to an unchanged active rule keeps suppression.
Count overlaps once. For example, 300,000 banned becomes 200,000 still banned
and 100,000 eligible for reconsideration after removing their last known reason.

These counts concern known reasons and retrieval eligibility, not predictions
of future acceptance. Added/edited predicates create no new evidence about
discarded metadata. When a released hash is rediscovered, fetch and evaluate
current rules; it may be rejected again for a newly found reason. No retained
file lists, eager metadata fetch or additional prediction mechanism is needed
for preview. Cancel changes neither rules nor decisions. No excluded-ID list or
manual hash entry is part of the UI. Reviewing retained catalogue entries is
the separate removal preview below; editing rules does not silently delete them.

Remembered bans must identify the rejected content precisely and retain enough
rule context to explain and reconsider the decision. Bitmagnet's approximate,
forgetful blocked filter does not provide those semantics. The exact record
layout and policy-change bookkeeping remain candidate implementation details.
Rejection records count toward the storage budget and have a lifetime distinct
from accepted catalogue rows, so removing an archive cannot accidentally lift
its applicable ban. They cannot restore metadata deliberately discarded.

**Removal preview:** saving rules controls new admissions and leaves existing
entries intact. Edit/save the rules first, then review existing entries under
that saved policy without further writes. Unsaved-policy overrides are outside
the initial scope. Show affected entries and files, per-entry reasons,
bounded result pages and estimated retained-data space, not payload bytes.
The same evaluator applies only after confirmation. A cancelled preview changes
nothing. Freeze the policy and a monotonic archive watermark so ongoing crawling
cannot add unreviewed entries to the removal; a changed policy, database reset
or identity reconciliation requires another preview. Keep internal catalogue
IDs non-reused within a database; retain no long SQLite transaction or complete
in-memory removal list while the person decides.

Apply confirmed removal in short batches, atomically preserving applicable
rejection evidence with each removed archive/projection. Report actual
completion or partial cancellation. Existing Add previews already holding
metadata, accepted torrents, resume checkpoints and payload remain untouched.
Applying rules to existing entries and clearing the catalogue use this same
preview/removal owner.
Content-rule cleanup and Clear catalogue remain previewed; automatic age-based
storage eviction follows the separately configured policy above.

The [comparative inventory](14-torrent-references.md#6-storage-retention-exclusions-and-deduplication)
owns the verified reference behavior: Rats' preview is count-only and rejection
memory is transient; Bitmagnet has a broad classifier and approximate blocking,
without precise rule context or a verified cleanup preview. Neither behavior is
an implementation specification for our smaller admission/removal workflow.

## Presentation and contract updates

**Owner ruling: the HTML is authoritative for this feature's UI.** Use the
[torrent-indexing prototype](../../app/prototypes/torrent-indexing-prototype.html)
for layout, controls, labels, dialogs (including the exclusion-rule editor) and
interactions, alongside its [notes](../../app/prototypes/torrent-indexing-prototype.md).
Implement that UI with existing native WinUI owners and TableView, rather than
embedding HTML or designing a parallel screen from this prose. This plan owns
engine, persistence and correctness semantics; mock algorithms and fixture
counts are not implementation evidence.

Keep the authoritative HTML in sync before production UI work. Its above-budget
scenario reflects the age-based budget/growth policy. Partial-result coverage
still needs its current semantics. Revise that example in the prototype rather than
inventing different dialogs or controls during native implementation.

Reuse the existing table, InspectorPane, title-bar search, dialogs and navigation.
Keep the torrent-page composition: results table, Filters on the left and
Inspector on the bottom. General keeps its Summary and Properties composition,
showing retained metadata and dated swarm estimates without transfer-only facts.
Files uses the existing file browser; choices prepare Add instead of changing
an accepted torrent. No separate General or Files layout is required.
File-search results show one collapsed row per known file-content identity, with
full filename, size, source count, the best source's dated seed/peer estimates and
Download file/Download torrent actions in the main table. Equal known file hashes
and lengths merge different names; equal names alone never merge files. Expansion
shows one-line torrent-source rows without repeating the file name or path
underneath. The original file path remains in the tooltip and torrent Files view.
Selecting a torrent source inspects it in the existing General composition.
Inspector > Files browses every file in that torrent and keeps the existing
multi-file choice workflow, with one Download button at the bottom right.
Do not duplicate Add and Download for the same choices. Whole-torrent Download
opens existing Add. Torrent
search still returns torrents. Keep result rows concise rather than adding
repeated explanatory disclaimers.

Selecting a file result shows its own Inspector, not an empty prompt to select a
torrent. General shows known size, source and alias counts, plus a TableView of
torrent name, torrent size, file count, seeds, peers and Inspect for every source.
Observation times have their own Observed column; Last seen records actual DHT
discovery separately. Torrent names stay on one line. Show one row per verified torrent
identity even when different display names refer to it; retain those names as
aliases. Use the engine's existing full v1/v2 identity reconciliation rather
than names or truncated DHT keys. Keep file and torrent hashes out of the results
and source tables; their role here is internal identity. Aliases lists known filenames with Search and
Copy actions. Search uses
the chosen alias through the existing query owner and retains the file identity.
Selecting a source continues to use the existing torrent General/Files
presentation. Keep utilities tied to available metadata rather than inventing
statistics. Counts retain the search coverage semantics above.

The main table has separate File and Torrent action columns so torrent-download
buttons align in root and child rows. Each is the neutral one-word Download
button with the shared leading Lucide icon and a tooltip naming its scope and
source, under the existing button rulings. Source rows leave the File action
cell empty; missing actions never shift the Torrent column. Header and row
content share the same alignment and geometry.
Use TableView's column-header sorting. Put result and selection counts in the
status bar; add no title/count row or standalone sort control above the table.

Reuse TableView multi-selection. File, Search and the result context menu expose
Download selected files. Deduplicate selected content, bind its preferred source
when preparing the command, then group original file indices by source torrent
through existing Add's automatic acceptance, without a series of Add dialogs.

Expose Search through View, global page selection and Ctrl+3. Preserve the
Torrents/Library direct switch; from Search it returns to Torrents. Extend
[Navigation](../../app/src/Shell/Navigation.cs) and the two-way dispatch in
[Finding.cs](../../app/src/Shell/MainViewModel/Finding.cs), menus, Inspector and
saved presentation explicitly. Keep each workspace's query and selection
independent, and retain existing departure/draft guards.

Settings exposes indexing, budget/usage, the not-seen interval, finite retention fields and explicit
Clear catalogue/Rebuild search actions. Search reports concise status. Rules and
remembered rejection reasons explain why content is excluded and when retrieval
is allowed again. The rule editor previews excluded counts before Apply; Search
can link to it but has no individual hash-ban dialog or banned-ID browser.
Filters and Inspector
visibility stay in the application's View menu; add no Search-specific
configuration toolbar.
Existing dialogs and tables handle review/removal and the rule-based ban
lifecycle; add no classification page.
Distinguish disabled collection, a resource wait, no database, rebuilding,
storage failure and no matches. Above-budget storage alone does not stop
collection. Use the shared status-bar work presenter: short label, percentage,
progress below and adjacent Cancel. Report outcomes through existing
notifications; add no appearing banner or surface that shifts the workspace.
Storage errors name the actual cause and offer applicable recovery: Open folder,
Repair for a repairable condition, and confirmed Delete/Clear only while storage
is accessible. Rebuild damaged search projections from retained metadata; never
delete an unreadable authoritative archive as a repair shortcut. Do not present
a generic Retry as the only way out.
Update every shipped language catalogue.

During implementation update the affected architecture, engine, database, pipe
and UI contracts and add one indexing contract for this feature's live rules.
Current architecture excludes online discovery; its new engine-owned exception
does not change Library's exclusions or C# ownership. Keep Library's database
contract separate from engine indexing ownership. When work is complete, move
live rules into those owners and delete this plan.
