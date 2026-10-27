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
alerts, crawler and completions. Queries do not queue behind transfer
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
While unpublished entries remain, batches run back to back, so publishing keeps
up with collection; each batch is short, so a queued search still answers
quickly.
Rebuilding uses the same path over retained metadata, with collection off and
no network. Failures preserve the authoritative archive; repair never deletes
an unreadable archive. Accepted-transfer checkpoints separately own destination,
wanted files, progress and payload operations.

## Collection and availability

### Crawler and configuration

The engine uses the Bitmagnet translation pinned to
`e31b30d7351c269dc801876f92ebc59d31fee545`. Collection uses the existing
session's DHT transport, one Catalogue/SQLite owner and existing Add. BEP 51
samples public-DHT hashes, BEP 5 locates peers and BEP 9 retrieves metadata.
Sampling cannot discover every torrent: this is a discovered catalogue, not the
complete network. Metadata acquisition creates no libtorrent torrent handles.

Optional `crawler.json` in the engine data folder overrides the reference
configuration; absence uses its defaults. There is no algorithm selector in
configuration, status or Settings. The `dht_crawler`, `metainfo_requester` and
`dht_server` objects retain the pinned reference's keys. Resolved configuration identifies unused reference
keys: the session owns the DHT socket, and reference file/piece-discard settings
never weaken full accepted metadata and file retention. `catalogue_status`
reports resolved configuration and acquisition statistics.

The requester uses plain IPv4 TCP, endpoint deadlines, per-IP pacing and
independent metadata concurrency. Libtorrent's transfer connection cap and
outgoing-start limit do not constrain these requests. Required encryption,
uTP-only, IPv6-only and effective session rate caps leave collection unavailable.
Confirmed proxy/adapter routing and cancellation remain mandatory. Collection
follows pause, DHT, route and Exit policy; closing the product window leaves
enabled collection running.

Bootstrap from persisted nodes and the session's live routing tables, using
the actual local IPv4 DHT identity reported asynchronously by libtorrent.
Sampling, peer lookups, metadata retrieval and projection have separate bounded
work slices. Honor BEP 51 advertised backoff. Before metadata retrieval, triage
filters indexed and remembered-rejected identities; metadata establishes
verified identities and aliases. A truncated v2 lookup is not a verified v1
identity. Per-node query pacing delays a query without occupying one of its
concurrency slots.

The session node's incoming DHT traffic adds two routes beside sampling. An
`announce_peer` supplies a peer that was reachable moments ago, so retrieval
starts from it without a peer lookup; a repeat while that hash is still
fetching or awaiting storage starts no second retrieval. Incoming `get_peers`
requests rank hashes by request count, and hashes requested at least twice get
a full DHT lookup, most-requested first, with a bounded number at once. Both
routes see only traffic that reaches this node; `requests_heard` and
`announces_heard` in `crawler_stats` count it.

### Rats Search exchange

While the crawler runs, the engine also receives catalogue records from Rats
Search peers over Rats' own protocol, through the crawler's route. TinyTorrent
implements the receiving side itself and links no librats. The protocol lives
in `engine/inc/Rats/` and `engine/src/Rats/` (namespace `tt::rats`): `Session`
is the handshake and messages without sockets, `Snapshot` reads a `.ratsdb`
file while it arrives, and `Connection` runs one session over a `Dialer`
connection. `Engine/Rats.cpp` owns scheduling and storage. The
[Rats wire protocol](../plans/14-rats-wire-protocol.md) gives the bytes.

- Peers come from a DHT lookup for the Rats key every ten minutes, from
  announces of that key to this node, and from the optional `rats.peers` list
  of IPv4 `address:port` entries in `crawler.json`. DHT addresses in private
  or reserved ranges are ignored, and one that fails before the handshake is
  forgotten, so junk addresses cannot crowd out real peers; listed addresses
  are kept. The engine connects to at most two peers at once, because a Rats
  node serves only ten, and tries an address again after an hour.
- A peer that shares its index gets a snapshot request when neither its key
  nor its address was asked in the last seven days. A peer can change its key
  but not its address, and the request itself counts, so a broken or hostile
  snapshot is not downloaded every hour. Any other peer, or one that refuses,
  gets one batch of up to ten random records.
- Only an answer to what was asked extends the 60-second wait, and a
  connection ends after two minutes, or thirty while a snapshot is requested,
  so a peer cannot hold one of the two places. One connection stores at most
  250,000 records, so one peer cannot fill the catalogue.
- The engine acknowledges snapshot bytes only after their records are stored.
  The sender stops at 4 MiB unacknowledged, so that bounds memory and no
  temporary file is written. Decoding runs in short slices on the catalogue
  worker, so a highly compressed snapshot cannot delay Search. A full
  catalogue cancels the transfer.
- Titles and file paths are cleaned the way libtorrent cleans torrent names,
  removing direction overrides, invisible characters and invalid text.
- `catalogue_status.rats` reports the peer addresses known, open connections
  and records stored this run.

A received record becomes an **unverified entry**: a catalogue row and its
files with no retained metadata. It is published at once and carries
`verified: false` in hits, sources and detail. Its name, size and files are the
peer's claim, and its file positions are not torrent file indices.

- A record is skipped when the catalogue holds or has rejected its hash. A
  record an exclusion rule matches is skipped without remembering a rejection,
  because a claim must not ban the real torrent.
- Peer-claimed seeds and peers are stored only with the peer's own scrape time,
  because the time a record arrives says nothing about the swarm.
- Triage treats an unverified entry as missing, so the crawler can still fetch
  its metadata. Admission then replaces the claim with the verified name, size
  and files, and evaluates the rules against the real metadata.
- Rebuild keeps unverified entries and their files. Detail has no extras for
  them and treats them as private, so no public scrape runs for a torrent
  whose privacy is unknown.

Persist sampled hashes and their responding DHT nodes, plus incoming DHT hash
observations. Saved pending hashes are observations awaiting rediscovery;
startup reloads nodes but does not replay those hashes as runnable acquisition
jobs. Retain existing pending data and schema history. The live scheduler's
queue and the database's pending-observation count describe different scopes.

SQLite admission yields between small batches; refused scheduling retains the
received bytes. Healthy received batches drain when collection stops. Storage
failure preserves the unprocessed suffix, stops collection and reports an error.
Failed status reads preserve the window's last totals. Settings shows active
metadata jobs and peer lookups against their actual admission limits, without
treating an enabled switch as proof of successful collection. Search exposes
indexed totals, newly searchable torrents per second and compact acquisition
activity; discovery details remain in its tooltip and accessible name. Raw hash
observations do not establish searchable throughput.

When collection starts, including engine startup with indexing enabled, admit
all locally available accepted-transfer metadata through the same deduplication
and exclusion path. Also admit metadata those transfers subsequently obtain.
This uses no DHT request or duplicate fetch and takes no ownership of their
handles or download choices. Libtorrent remains the parser for crawler admission
and ordinary transfer metadata; previews and accepted transfers keep their
existing handle ownership and payload safeguards.

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
Search marks an unverified entry. `catalogue_prepare` refuses one with
`metadata_unavailable`, so every Download action for it opens ordinary Add by
hash, which fetches the real metadata and shows the verified file list.

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

Import and export of catalogue files, distributed search, classification
languages, persistent file hiding and manual hash-ban entry are outside the
initial feature.

Rebuild and Repair require an explicit confirmation explaining their local disk
and CPU cost. Removal previews use one torrent/file summary and a compact bounded
list; an empty preview shows no list or paging. Paged previews state the full
confirmed scope once and never infer displayed ranges from an assumed page size.
