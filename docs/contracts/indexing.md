# Torrent catalogue

The engine owns the optional catalogue of discovered torrent metadata. Search
queries it locally; Torrents owns accepted transfers and Library owns files on
the drive. Collection starts off. Stopping collection preserves saved search,
inspection, Add and local rebuilding.

The approved
[HTML prototype](../../app/prototypes/torrent-indexing-prototype.html) owns the
feature's UI composition, controls, dialogs and interactions. Existing native
WinUI and TableView implement it; sample data and browser algorithms are not
production behavior. The owner's later Settings decision takes precedence over
the prototype's standalone settings dialog: Indexing is a normal Settings
category after Transfers, using the shared category header, icons and rows.
Rule editing and reviewed policy/removal changes remain explicit task dialogs.

## Ownership and storage

`Engine::State` owns one native `Catalogue` and its separate serialized `Store`
worker. The worker owns SQLite work; state owns the existing libtorrent session,
alerts, crawler handles and completions. Queries do not queue behind transfer
checkpoints. The product window uses the existing pipe and never opens
`index.db`. No separate process, session, download owner or database server is
introduced.

Create `index.db` in the engine data directory on first collection enablement.
Opening empty Search alone creates nothing; existing data opens with collection
off. Native SQLite uses the app's packaged `e_sqlite3.dll`, pinned through
`SQLiteRuntimeVersion` and loaded by absolute installed path. Engine-only output
also deploys that runtime.

Archive exact validated info-dictionary bytes, verified full v1/v2 identities
and additional metainfo actually acquired. BEP 9 supplies an info dictionary,
not a complete original `.torrent`. Missing trackers, web seeds and piece layers
remain unknown; preserve available information and its origin. Libtorrent stays
the parser for admission, Add and rebuild.

The archive keeps a metadata-only libtorrent resume serialization for acquired
v2 hash trees, including partial trees that cannot form complete `.torrent`
piece layers. It contains no destination, wanted files or transfer progress.
Load it through libtorrent and verify its exact info bytes against the archive;
build fresh Add parameters from the retained metadata fields. Accepted transfers
contribute their available canonical checkpoint or preview parameters through
the same admission owner, without requesting another save. The catalogue keeps
a metadata snapshot; transfer checkpoints own subsequent download progress.
Keep acquired complete piece layers when a later snapshot is partial; libtorrent
proves completeness by writing and parsing a torrent file. Independent partial
transfer snapshots are not combined into a new hash tree.

Retain every valid original file entry and index, including padding, duplicate
names and empty files; preserve attributes and available v2 file roots. Hiding
padding never renumbers files. A v2 root is a SHA-256 Merkle root, not a flat
checksum. Root plus length identifies known equal file content; names and
torrent hashes cannot establish that identity.

Archive admission precedes replaceable search projection. Projection yields in
bounded file batches, persists progress and publishes only complete entries.
Rebuilding uses the same path over retained metadata, with collection off and
no network. Failures preserve the authoritative archive; repair never deletes
an unreadable archive. Accepted-transfer checkpoints separately own destination,
wanted files, progress and payload operations.

## Collection and availability

### Experimental comparison crawler

The owner's direct-translation comparison preserves the normal libtorrent
crawler and adds an experimental Bitmagnet alternative. `crawler.json` in the
engine data folder selects `algorithm: bitmagnet`; absence keeps `libtorrent`.
Settings shows the engine-reported algorithm in Collection and its collapsed
summary. Selection remains file-based. The original crawler's concurrency rows
remain visible but disabled for the translation, with an explanation.
Activity shows active metadata jobs and peer lookups against the running
translation's effective limits; the scheduler owns those limits. The optional
`dht_crawler`, `metainfo_requester` and `dht_server` objects use the pinned
reference's configuration keys and defaults. `catalogue_status` reports the
selected crawler, resolved configuration and acquisition statistics.

Only the selected scheduler runs. Both use the existing session's DHT transport,
one Catalogue/SQLite owner, full accepted metadata/file retention and existing
Add. The reference requester uses plain IPv4 TCP, its original endpoint deadlines,
per-IP pacing and independent metadata concurrency; libtorrent's transfer socket
cap and outgoing-start limit do not constrain this experimental arm. Report both
budgets in comparisons. Required encryption, uTP-only, IPv6-only and configured
rate caps leave the reference arm unavailable. Confirmed proxy/adapter routing
and cancellation remain mandatory. The original declared DHT port is recorded
as unused because the existing session owns its socket.

The reference's file threshold and piece-discarding settings do not weaken shared
retention. Its two unused configuration keys remain identified as unused, and
BEP51 advertised backoff is honored. SQLite admission yields between small
batches; refused scheduling retains the received bytes. Healthy pending work
drains when collection stops. Storage failure preserves its unprocessed suffix,
stops collection and reports an error. Benchmark outcomes are recorded in the
[active acquisition plan](../plans/14-discovery-feedback.md#current-batch-useful-acquisition-throughput).
Experimental selection does not replace the normal crawler.

### Normal crawler

Use the existing session and confirmed route. BEP 51 samples public-DHT hashes,
BEP 5 locates peers and BEP 9 retrieves metadata. Sampling cannot discover every
torrent: this is a discovered catalogue, not the complete network.
Bootstrap the sampler from the existing session's routing tables, using local
node IDs reported by asynchronous DHT statistics; libtorrent's live-node query
requires an actual local ID. Refresh the candidate pool periodically while
collection is eligible. Settings shows current metadata acquisitions and pending
hashes without treating an enabled switch as proof of successful collection.
Search exposes indexed totals, newly searchable torrents per second and compact
acquisition activity; peer-probing and pending counts remain in its tooltip.
Idle and busy metadata-fetch limits are configurable in Indexing Settings, with
defaults 256 and 2, ranges 1–256 and 1–64 respectively. Explicit saved settings
remain authoritative. Idle acquisition uses two connections per torrent and no
per-torrent receive throttle; busy acquisition uses one and 16 KiB/s. The shared
session connection and bandwidth policies still apply. Reducing capacity yields
excess jobs, preserving those with the most receive progress; queued admissions
consume capacity. Yielding leaves the existing lease to expire without failure
backoff, and late valid metadata remains eligible for admission.

Crawler-only peer extensions promptly release peers without BEP 10, enabled
metadata exchange or a positive advertised metadata size. Libtorrent still owns
metadata requests and validation. Normal disconnect semantics retain its peer
reconnect delay; inability to serve now is not a torrent ban or corruption.
Disable this peer policy before a crawler handle becomes a user Preview,
without lifting the payload guard or changing normal transfer policy.

Retain a sampled hash's responding node and fresh announcing-peer endpoints.
Peer hints use libtorrent's DHT source semantics without inventing transport
capabilities. Libtorrent's configured transport policy chooses the connection;
empty hint flags do not disable its optimistic uTP-first behavior.
Probe known source nodes directly while metadata work continues. A negative or
timed-out supplier lookup retains the hash with retry backoff; it does not start
a recursive lookup immediately. Hashes without a source node use ordinary
libtorrent peer discovery. Only hashes with peer
endpoints enter metadata acquisition; cold hashes remain in durable peer
discovery rather than occupying metadata slots. Disable automatic DHT on those
metadata handles so the probe owner performs each cold lookup. Idle collection
permits 200 hash-probe leases and starts up to 50 new probes per maintenance
cycle; busy collection permits eight and starts up to two. Select at most one
due hash per supplying IP in a cycle, using bounded round-robin index seeks.
Source-less hashes share capacity and borrow unused supplier slots. Direct
supplier deadlines are four seconds; source-less traversal deadlines are 15.
These count hash
probes, not individual network requests: libtorrent traversals can outlive an
application lease. Useful late peer replies update existing pending hashes,
without recreating indexed or excluded entries. Pending archive admission
also consumes outstanding-work capacity, so slow disk writes cannot build an
unbounded metadata backlog. Coalesce peer hints and retry failures at the engine
owner and persist them in the existing maintenance job, after discoveries and
before projection. Apply failures before fresh hints so a new positive response
can make a hash eligible again. Ordinary peer replies update pending records
only. Refused maintenance scheduling restores the batch for a later attempt;
an SQL failure is reported rather than replaying possibly committed backoff.
Failed status reads return a pipe failure, preserving the window's last totals.
Sampling, peer probes, metadata retrieval and projection have separate bounded
work slices, so a slow
torrent does not serialize collection. Batch node persistence and publish
multiple small torrents within one projection slice. Retrieval deadlines yield
stalled work while allowing active receives to continue. Before expiring an
attempt, obtain fresh asynchronous status so queued receive observations cannot
cause a progressing attempt to be removed. Received protocol bytes
are only a progress heuristic, not proof that metadata will complete. These
choices target validated searchable growth; raw hash observations do not prove
throughput, and the catalogue never claims complete DHT coverage.

Bound node traversal, requests, metadata jobs and deadlines. Persist sampling
eligibility, pending leases and retries before network work; honor requested
sample intervals across restart and rediscovery. Deduplicate pending, acquired
and remembered-rejected identities before fetching. A truncated v2 lookup is
not a verified v1 identity; merge aliases only after metadata establishes them.

When collection starts, including engine startup with indexing enabled, admit
all locally available accepted-transfer metadata through the same deduplication
and exclusion path. Also admit metadata those transfers subsequently obtain.
This uses no DHT request or duplicate fetch and takes no ownership of their
handles or download choices. Check transfers, previews, additions and removals
before creating a crawler handle; reuse metadata or await its existing owner.

Crawler handles use the existing payload guard before acquisition and are
removed without delete-data. They never appear in Torrents or Library. A preview
or crawler yields to an accepted transfer on a newly learned hybrid conflict,
preserving the transfer's intent. Collection yields resources to active
downloads, verification and moves and follows pause, DHT, route and Exit policy.
Closing the product window leaves enabled collection running.

BEP 33 observations are independent, best effort and dated. Union valid Bloom
filters before estimating populations; summing node estimates double-counts
overlap. Missing, stale, invalid or saturated observations remain uncertain.
Failed attempts preserve the previous usable estimate and its original time.
These are estimated swarms, never guaranteed seeds or file availability.
Metadata retrieval, search and Add never wait for them. Collection off stops
automatic observations and retains dated results.
Locally admitted private torrents remain searchable but never trigger public
DHT scrapes; the archive's private flag supplies this guard.

## Search and Add

Search offers Files and Torrents. Its query and selection remain independent of
the Torrents and Library workspaces.
Results and Inspector tables use TableView's automatic width fill. Numeric,
date and action columns retain their fitted minimum width; names consume spare
width and retain a readable minimum. General uses the existing torrent-properties Section, Field, Strip and
FieldColumns presentation and semantic icons. First use shows the discovery
heading, explanation and inline On/Off control, without a results header or
additional settings button. No matches offers Clear for the query and filters.
Files initially shows collapsed known-content groups; unrooted files remain
separate. A matched filename identifies its parent torrent. Expansion exposes
equal-content occurrences, including other names, inside the query's captured
publication scope and parent restrictions.

One engine predicate owns matching. Space means AND, `|` OR, `!` exclusion and
`*`/`?` whole-target wildcards; quotes preserve phrases. `a|b c` means
`(a OR b) AND c`. Initial fields are `ext:`, `size:`, `path:` and `torrent:`.
Structured filters are parsed separately. Normalize Unicode and case consistently
without stripping accents; preserve original spelling. Each occurrence must
satisfy the query itself; sibling files cannot manufacture a match.

Responsiveness takes priority over exhaustive counts. Bound
scan work, elapsed time, memory and output. Verify returned matches and disclose
limited coverage. An incomplete empty scan does not prove no matches. Counts
describe the established scope; exact totals appear only when known. Apply the
selected sort to the matching search set before returning its first rows;
sorting each displayed batch is insufficient. Refining a query or requesting
more can continue bounded work. Search coalesces superseded input, permits one
query in flight and displays at most 1,000 results.

Normalized name, size and swarm-order indexes support ordered keyset traversal.
A trigram index narrows supported positive file-text conditions; the shared
predicate still verifies matches. Selective complete candidate sets can be
sorted directly; broad or unsupported queries continue bounded ordered scans.
Supply finite batches to TableView using its external sort mode; its normal
local sorting cannot order the whole catalogue. Superseded replies
cannot overwrite a newer query or selection. No database transaction stays open
while a person scrolls or reviews a dialog.

A group displays its lexicographically first matching filename, with stable
torrent/file ties. Separately bind its default
download occurrence when preparing the action: highest usable seeds, then peers,
with stable identity ties. Unknown estimates never block a choice. Disclose the
actual source and original path when they differ from the matched alias;
estimate updates never silently switch that bound occurrence.

Download file uses that source and original index through existing Add's
automatic acceptance. Multiple choices share the existing file-choice owner.
Download torrent opens ordinary Add for the bound source. Explicit source
selection binds that occurrence. Revalidate identity and root/length where
applicable; report an unavailable source instead of choosing another silently.

Ordinary and catalogue Add share paged preview files and compact choices:
default priority plus original-index overrides. Choices survive pages, hidden
padding, duplicate handling and preview recovery. Whole-torrent Add needs no
full file-list exchange. Metadata export, invented URI schemes and a second
download command do not substitute for Add.

## Exclusion and cleanup

One finite exclusion evaluator serves admission and reviewed cleanup. Conditions
inside a rule are AND; rules are OR. Torrent fields cover title, total size and
file count. Optional file fields cover name, path, extension list and size,
applied to the same non-padding file with Any, Every or No matching files.
Every requires a nonempty set. Extension tokens match exact filename suffixes,
including compound extensions: `ts` does not match `mts`.

Any matching rule rejects the whole torrent before archiving. Discard its
metadata and file list, retaining compact identity and recorded rule reasons.
Unchanged applicable reasons suppress fetching. Removing or changing a rule
releases its reasons unless another already-recorded applicable reason remains.
Rediscovery then retrieves metadata and evaluates current rules. Future matches
cannot be predicted from discarded metadata.

Rule preview reports known suppressed, still suppressed and released counts;
released means eligible for reconsideration, not accepted. Saving rules affects
future admissions. Removing retained entries needs a separate read-only preview
under the saved policy, with affected torrent examples, reasons and explicit
full scope. Freeze policy and archive watermark; later ingestion is outside
confirmation. Clear has a distinct confirmed scope and preserves applicable
bans. Removal never reaches payload, checkpoints or metadata already held by Add.

The budget is a cleanup target, not a strict cap. Account for metadata, search,
rejections, SQLite overhead, journals and scratch. At budget, evict entries not
seen for the configurable interval, oldest first. Actual discovery refreshes age
even when metadata is known; search, inspection, metadata reads and availability
checks do not. For locally admitted entries never discovered through DHT, use
admission time as the age baseline and leave Last seen unknown.

Recheck age before removal. Reusable freed pages satisfy recovered capacity
before physical shrinking. If no stale entries remain, keep collecting above
the target. Eviction is not a ban and never judges value by popularity. Defaults
are 1024 MiB and 4320 hours; Settings offers Hours/Days/Weeks. Actual storage
failure defers affected writes while preserving committed data and usable reads.

Import/export, distributed search, classification languages, persistent file
hiding and manual hash-ban entry are outside the initial feature.

Rebuild and Repair require an explicit confirmation explaining their local disk
and CPU cost. Removal previews use one torrent/file summary and a compact bounded
list; an empty preview shows no list or paging. Paged previews state the full
confirmed scope once and never infer displayed ranges from an assumed page size.
