# Disk discovery and torrent file tracking

Design plan, 2026-10-16. Implementation and performance measurements are still
to come. The [Library](../contracts/library.md), [engine](../contracts/engine.md), [database](../contracts/database.md), and
[pipe API](../contracts/pipe-api.md) contracts own current behavior; update them alongside
implementation.

## North star

**Library quickly shows the person's actual video, music and subtitle files on
selected local NTFS volumes and keeps their locations current with little memory.
Deleted files disappear from normal results. Optional torrent tracking follows
files moved outside the app, while missing payload is never downloaded again
without permission. Preserve valid identification and metadata; keep the interface
responsive and the torrent engine unelevated.**

Two independent options start **off**: **Discover media for Library** and
**Track moved torrent files**. Ordinary torrent use with both off never requests
elevation. Settings exposes both; the first Library visit may offer them once
without blocking saved browsing, with both choices explicit. Remember a declined
offer. Explain their different startup behavior and the optional full verification
of moved files in that same setup surface; quick checking is the default. Describe
tracking as automatically reusing matching files found elsewhere when their old
location is missing, so consent covers reuse rather than an unknowable move history.

Library-only discovery starts the helper when Library actually needs it. Tracking
starts it with the engine, even without a product window, and enabling tracking
during a session activates it then. Each new engine session with tracking enabled
can therefore request Windows administrator permission once. Both features reuse
that process until the engine quits. Neither elevates the engine or the UI.
Starting the helper does not start a tracking search: it waits idle until the
engine notices missing files and asks where those particular files are.

Default to currently available fixed NTFS volumes; removable NTFS volumes require
explicit selection. One shared volume scope belongs to engine settings, so
headless tracking does not depend on `library.db`; the UI edits that same choice.
The default scope follows volume descriptors as they become available; an explicit
empty selection means none. Scope membership never authorizes journal creation.
No folder-selection workflow is required. Searching the saved catalogue remains immediately usable
while discovery catches up. Known absence always wins over cached membership.
Removing a torrent does not remove the disk source's contribution to files that
remain on disk.

The helper answers **which files match this selection, and what changed?**
It examines filesystem metadata internally, filters before IPC, and sends only
matching files and changes to previously matching files. It never opens media
streams for content or sends a client an inventory of unrelated files. Library
selects only video, music and subtitles. The engine can additionally select exact
names/sizes from missing torrent payload for a finite locating job. Neither
selection accepts `*.*`; tracking never broadens Library's media filter or builds
an all-files index. Reading metadata to find matches does not mean retaining all
of it. This is not general desktop search or an arbitrary SQL interface.

Success means complete matching locations within the stated NTFS scope,
incremental updates while connected, bounded memory and transfer, preserved
user choices, and no accepted removals from interrupted work. Actual latency
and resource use must be measured; Everything's speed is not a promise.

## Owners and lifetime

| Owner | Responsibility and reason |
| --- | --- |
| Native disk helper | Reads NTFS metadata, reconstructs paths, filters and tracks changes; prepares a missing journal only on explicit enablement. Its small interface contains administrator privileges. |
| C# disk source | Builds queries, schedules work, and submits complete changes through the existing database owner. |
| Existing Library/file-facts/subtitle owners | Identify media, read contents as the user, associate captions, search, and present results. Each existing rule keeps one owner. |
| Existing database owner | Owns migrations, source membership, locations, transactions and cleanup; the helper never opens `library.db`. |
| Existing engine | Owns helper activation, tracking settings/queries, candidate validation, adoption and missing-payload holds. It remains unelevated and owns no Library database or media catalogue. |
| Installer | Ships and updates the helper executable in the protected application directory; it installs no service. |

Use the agreed small C++ Windows executable, `TinyTorrentFileFinder.exe`, with the engine's
existing native toolchain. It links the same native pipe implementation and
calls Windows filesystem interfaces directly, without libtorrent, SQLite,
WinUI or .NET. C would obstruct direct reuse; Rust would introduce a toolchain
and interoperability seam without a demonstrated benefit here. Retained records
and buffers determine the main memory cost. Define its
name in `Directory.Build.props` with the existing executable names.
This proposes a narrow extension to the current two-process/C# Library ruling:
native code acquires filesystem facts; Library policy and persistence stay in
C#. It does not move Library into either native executable.

**Owner ruling: no Windows service. Elevate the helper when requested, keep that
process running to avoid repeated prompts, and release discovery memory while
unused.** The engine is the single activation owner. Startup tracking and the
UI's `StartDiscovery` request call the same implementation: reconnect first,
otherwise launch through Windows `runas`. Coalesce requests into one launch/UAC
interaction. The UI connects directly to the helper after engine activation;
it never starts a competing process. A declined UAC request leaves saved browsing
and torrents usable and preserves both settings. Suppress automatic attempts for
that engine session until explicit Retry; refresh, typing and page navigation
cannot prompt again. The next engine startup can make its normal tracking attempt.

Leaving Library, closing the window, or disabling discovery does not terminate
the elevated process. **The helper belongs to the running engine and exits when
that engine exits.** It waits on a validated engine process handle, so engine
failure also ends it; it is not tied to the shorter-lived UI process. A new
engine session or replacement after a helper crash requires elevation again.
Report an unexpected helper loss and offer retry; do not launch a prompt loop.
A file manifest cannot make one approval permanent. Never save administrator
credentials, disable UAC, or substitute a scheduled task for the rejected service.

Each selected volume has a Library query only while that feature is enabled and
Library is active. Leaving Library, disabling it or closing the window cancels
those queries and disconnects the UI's finder connection. Tracking uses a separate
engine connection and opens finite exact-name/size queries only after noticing
missing files. It closes them when that locating job finishes or is cancelled;
disabling tracking also cancels its pending jobs. There is no continuous torrent
watch set, background all-files index or search on torrent addition/completion.
Both use one query implementation. The helper
retains matching file names and only their ancestor directories. Closing a query
releases its graph, buffers and volume handles; disconnect closes all that
connection's queries. With neither consumer active it performs no enumeration or journal
polling and retains no file index. The resident process and its local pipe are
all that remain. No helper catalogue or journal checkpoint is persisted.

Reopening discovery starts a fresh metadata baseline. Library presents its saved
searchable catalogue immediately, under the existing ready-when-open ruling;
discovery never substitutes a preparing page. Background progress belongs in
the existing work presentation. Cached rows are last observations, not claims
of current presence; automatic file reads wait for current confirmation. A file
deleted while discovery was idle can briefly remain until reconciliation, just
as a deletion between journal polls can. Once observed missing it stays hidden
until positive presence evidence returns. Existing Open failure handling covers
the observation gap.

This trades catch-up work for low idle memory and no second persistent index.
A journal cursor alone cannot resume discovery without the matching identities
and ancestry that give its events meaning. Warm queries retain that state only
while their consumer uses them. A tracking job releases its matches on completion;
the enabled setting alone retains none. Measure catch-up cost before revisiting retention;
minutes of recurring work would fail the fast, light product goal even when the
saved catalogue makes the page open quickly.

## Files and platform scope

Discovery supports local NTFS 3.0/3.1 through its metadata and USN journal only.
FAT/exFAT, ReFS, network shares, other NTFS versions and locked volumes are not
scanned by another method. There is no folder search or directory-walking
fallback. A usable journal is required to open a query; explicit drive enablement
can create a missing journal through the narrow setup operation below. Ordinary
queries do not create journals, and there is no journal resize/delete operation.
Reparse targets are not followed, and cloud placeholders are not
hydrated. Matching names may be listed without their contents being available.
Hidden/system attributes alone do not exclude a matching file.

`FileName` supplies the existing video/audio extension sets and `SubtitleFile`
supplies supported captions (currently `.srt`, `.ass`, `.ssa`, `.vtt`, `.sub`).
Expose those existing sets through their owners and use them in their current
predicates and the query builder; do not create another classification list.
The helper receives the normalized union and knows nothing about movies,
episodes, music tags, subtitle languages or associations.

An extension identifies a candidate, not a completed download. TinyTorrent's
unfinished suffix does not match; a path or known physical file that a current
torrent reports unfinished remains ineligible through the disk source too.
Outside managed torrents, a final filename is the available completion evidence.
The helper cannot establish another application's download state.

All matching hard-link locations count. A physical file with `data.bin` and
`Film.mkv` names must yield the latter; two matching names yield two Library
locations. DOS-only short names do not create extra locations. Junctions and
symbolic links are not traversed to manufacture aliases.

Matching names are candidates, not automatic Library membership. In particular,
the helper retains matching names and ancestry inside the Windows Recycle Bin;
Library excludes those locations through its eligibility rule below. This keeps
folder restoration incremental without teaching the helper a second query policy.

## One pipe architecture

All connections use the existing four-byte little-endian UTF-8 JSON framing,
16 MiB frame limit, request IDs, typed error envelope, asynchronous receive,
single writer, outstanding-request bounds and version/session greeting.
The UI connects directly to the engine and finder endpoints; the engine connects
to the same finder endpoint for tracking. Each has independent
connection state. No second RPC system, codec, scheduler or recovery path.

Extract the existing native transport from its `Engine.h` dependency into
`lib/Pipe` and link it into both executables; do not copy `Pipe.cpp`. Its client
and server roles share framing, correlation, queues and failure handling. Extract
the managed connection machinery once and reuse it for both clients. Engine
activation, `Consumer`, Inspector contexts, negative file contexts and the
engine's file-read gate stay in its adapter until the collector actually changes.
Those rules do not belong in the finder client.

Use one build definition for the protocol version and bump it for the changed
greeting. The common greeting identifies `endpoint` (`engine` or `finder`),
`version`, and `session_id`; engine-specific greeting data remains with that
endpoint. Activate the helper from the protected application installation,
never an executable path advertised by a pipe peer. Elevation and peer identity
belong to endpoint activation; framing and request handling remain shared.

The only delivery generalization selected from the
[communication plan](communication-plan.md#modules-and-seams) is its
`Replace(key, message)` interface in place of hard-coded `detail`/`snapshot`
slots. This removes engine message knowledge from the reused transport while
preserving its writer fairness and the engine's existing two observation keys.
Ordinary `Send` remains ordered and lossless. The helper uses one replaceable
observation, volume-list invalidation. All discovery batches are ordinary replies;
dropping a batch would lose files or deletions.

The expensive shared per-torrent collector/scoped-subscription work remains
behind the existing measurement gate. This feature needs transport extraction,
not that rewrite. Preserve existing pushed summaries, sequence rejection and
fair delivery rather than implementing them again.

## Interface and DTOs

Five typed asynchronous operations use the existing request/reply envelope:
four for discovery and one for explicit journal setup.
Client methods also take the usual cancellation token; it is not a wire field.
DTO properties serialize as snake_case. Only the following commands are accepted.

| Operation | Command | Input | Successful output |
| --- | --- | --- | --- |
| `Volumes()` | `volumes` | none | `Volume[]` |
| `CreateJournal(VolumeIdentity volume)` | `create_journal` | `VolumeIdentity` | refreshed `Volume` |
| `Open(Query query)` | `open` | `Query` | `Opened` |
| `Read(string queryId)` | `read` | `query_id` | `Readout` |
| `Close(string queryId)` | `close` | `query_id` | empty object |

```text
Volume
  volume_id: string
  label: string?
  paths: string[]
  kind: fixed | removable | other
  state: VolumeState

VolumeState =
  { kind: available, serial: string }
  { kind: journal_missing, serial: string }
  { kind: journal_deleting, serial: string }
  { kind: journal_unavailable, serial: string }
  { kind: unsupported, file_system: string? }
  { kind: locked }
  { kind: unavailable }

VolumeIdentity
  volume_id: string
  serial: string

Query
  volume_id: string
  serial: string
  selection: Selection

Selection =
  { kind: extensions, extensions: string[] }
  { kind: files, files: FileMatch[] }

FileMatch
  name: string
  size: int64

Opened
  query_id: string

Entry
  file_id: string
  paths: string[]
  size: int64?
  modified: int64?
  stamp: string?
  content: local | placeholder | unknown

Readout =
  { kind: working, examined: int64, matched: int64 }
  { kind: batch, entries: Entry[], removed: string[] }
  { kind: complete }
```

These are named DTOs and tagged unions, not loose JSON or an inheritance
framework. Each union arm has exactly its stated fields. Reject missing fields,
invalid values and impossible combinations at the protocol seam.

`Volumes` reads a descriptor snapshot maintained by background Windows discovery,
not a synchronous probe of every disk in pipe dispatch. Known volumes whose
metadata is still being probed are `unavailable` until an update; do not present
an unfinished inventory as a successful empty machine. Pipe readiness follows
the initial volume-identity enumeration; slow per-volume metadata stays outside
dispatch.

`volume_id` is the opaque Windows volume GUID identity returned by `Volumes`,
not a drive letter. `serial` identifies the verified NTFS incarnation. A locked
volume can be listed without inventing a serial that cannot be read. `Open`
requires the available volume's current serial; its worker rechecks it before
accepting any records. A reformat
therefore cannot inherit the old incarnation's source identities. `paths` on
`Volume` lists current mount paths; an available unmounted NTFS volume may have
none, and its volume GUID path remains its location root.

`query_id` is opaque and bound to its connection and helper session.
`serial` and `file_id` are fixed-width hexadecimal unsigned identifiers.
`file_id` includes the complete NTFS reference, including the reuse sequence.
Physical source identity is `(volume_id, serial, file_id)`; a path is a location,
not file identity. Never truncate these values to a torrent integer index.

`extensions` is a nonempty normalized set of dot-prefixed literal suffixes,
sorted and deduplicated by the C# query builder. Each is a dot plus lowercase
ASCII letters/digits, at most 32 characters; at most 128 suffixes. The helper
validates this representation and compares suffixes ordinally without case.
No wildcards, roots, SQL, regex or content predicates. Changing the filter closes
the query and opens a fresh baseline.

The `files` selection is a nonempty, deduplicated set of exact basename/logical
size pairs, not paths or wildcards. Names contain no separator, device prefix,
NUL or traversal; compare ordinally without case while preserving returned path
spelling. Sizes are nonnegative. Bound the set by the common frame and allocation
budgets; reject an oversized query rather than silently omitting targets. A
matching name with unknown metadata size remains a candidate with null size,
not an inferred nonmatch. The engine resolves that uncertainty before adoption.
Both selections use the same metadata reader, sparse graph and cycle lifecycle;
only their membership predicate differs.

`Entry.paths` is the complete, nonempty set of matching volume-relative long
names for that physical file. Preserve spelling, including case distinctions;
deduplicate exact names only. Each path has no leading separator, traversal,
device prefix or NUL. It does not include nonmatching aliases. Windows cannot
open a path through a name that is not one path component, such as a name with
`:` that a Linux NTFS driver created, so such a path is not a location; it
never fails the drive. Receiving an entry replaces the physical file's entire
matching location set, so deleting one hard link removes just that location.
`removed` contains file IDs that now have no matching location. It can name a
file whose entry the client never received, which the client ignores, so the
helper keeps no record of what it delivered. Within a batch an ID occurs once,
in either array; later batches may correct an earlier observation.

`size` is logical bytes, `modified` is UTC Unix milliseconds. Null in these and
in `stamp` means the value could not be obtained, for example because Windows
refused to open the file, not zero. `stamp` is an opaque stable version
combining journal identity and file USN, encoded as two fixed-width hexadecimal
values separated by a colon. It survives reconnect while both values are
unchanged, and changes when the journal is replaced. Persist it with file facts,
not as a resume token. Query/session identity separately rejects late workers.
Obtain current attributes/USN through documented metadata interfaces; a journal
event timestamp is not a modification time.

`content` describes whether content access would need recall: `local` is a
known local ordinary file, `placeholder` requires provider recall, and `unknown`
covers unresolved attributes or unsupported reparse semantics. Derive this
without following reparse targets or opening data streams. Automatic media and
caption readers require `local` plus current source confirmation and ordinary
user access. Listing a cloud name must not itself download its contents; an
explicit user Open can invoke the normal Windows/provider behavior.

### Drive enablement

NTFS does not guarantee a journal already exists. When the person enables
discovery for selected drives, explain that a missing Windows change journal
will be created and remain on the drive. The settings flow calls
`CreateJournal` only for that explicit action on a `journal_missing` volume.
The helper validates the enumerated volume and incarnation again on its worker,
queries the journal immediately before creation, and returns its refreshed
descriptor. An already available journal is an idempotent success; deletion in
progress, an unreadable journal or a changed volume is a refusal. Reconnect and
background discovery never recreate a journal removed by an administrator.

Use `FSCTL_CREATE_USN_JOURNAL` with a provisional 64 MiB maximum-size target and
8 MiB allocation delta. These control NTFS disk-journal trimming, not helper RAM
or a hard disk-space cap: Windows can temporarily exceed the target plus delta.
Existing journals are used as found, with no intentional resizing. The Windows
operation is create-or-modify, so an external administrator creating a journal
between the final absence check and the call remains a race; do not claim atomic
create-only behavior or add volume locking to handle it.
[Windows journal lifecycle](https://learn.microsoft.com/en-us/windows/win32/fileio/creating-modifying-and-deleting-a-change-journal),
[creation semantics](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_create_usn_journal).

Setup runs off pipe dispatch through the existing request/failure path. An
uncertain reply is followed by `Volumes`, never automatic replay of the mutation;
the person can explicitly retry if setup is still required. `journal_deleting`
waits for Windows to finish and then refreshes the descriptor. An absent or
unusable journal leaves discovery unavailable with the setup action where
appropriate; it never triggers a folder scan. Disabling discovery or uninstalling
leaves the journal alone because Windows and other applications may use it.

### Query lifecycle

`Open` validates the DTO/admission, creates a worker and replies promptly;
all volume opening, serial verification and metadata I/O run in that worker.
Allow one query per
volume per connection; duplicate opens return `busy` rather than replacing work.
The helper enumerates the available volumes, which bounds that connection's
possible queries without an arbitrary volume-group rotation and repeated scans.
Only one `Read` may be outstanding per query; the existing shared connection
limits still apply. The memory budget also applies across all clients/queries.

The first cycle is a full baseline. `Read` returns a bounded `batch`, `complete`,
or `working` within one second. Pipe dispatch never waits on disk I/O. A worker
may take much longer without triggering the existing 15-second pipe timeout.
Library stages each batch in its database before asking for the next; the engine
uses the bounded transient candidate state described under adoption below.
`working` is progress, never membership evidence.

At `complete`, Library atomically accepts the staged filesystem cycle and
schedules affected torrent reports from the resulting known absence. Only after
database acceptance succeeds may it issue another `Read`, which implicitly starts the next
finite journal cycle on the same retained graph. No wire acknowledgement,
public USN checkpoint, sequence protocol or subscription machinery is needed.
The source waits five seconds between completed cycles when caught up, rather
than spinning on empty results. It schedules selected volumes fairly. The engine
instead accepts one completed baseline for its locating job and closes the query;
it does not start ongoing journal polling for torrent tracking.

`Close` is idempotent and cancels the worker, settles a pending read as
`cancelled`, and releases state. IDs never survive reconnect. A terminal worker
failure remains failed until close; a partially mutated graph cannot continue
after its cycle was discarded. Failed database acceptance likewise closes the
query; the next attempt begins a baseline. Journal loss returns `reset_required`
and follows the same path.

Cancellation before sending has no server effect. For a known query, ordinary
stop uses `Close`. Cancellation after an `Open`/`Read` was sent but before its
outcome is known closes the finder connection through the common failure path.
Never retry an uncertain `Read`: it may already have consumed a batch. Closing
the connection releases any unknown allocated query and clears confirmation for
its disk sources; the torrent connection is independent.

`{type:"volumes_changed"}` invalidates the volume list; it carries no membership
decision. Requery `Volumes`, refresh mount paths, and schedule available selected
volumes. A disconnected volume is unavailable, not an empty successful baseline.

### Errors

Use the existing `ok:false` envelope with stable codes and diagnostic detail.
The UI localizes codes through its existing owner. No error means an empty disk.

| Code | Caller action |
| --- | --- |
| `invalid_request` | Correct the DTO/filter/unknown query; do not retry unchanged. |
| `access_denied` | Show the setup or authorization failure. |
| `unsupported` | Explain the unsupported filesystem/record version. |
| `unavailable` | Keep cached membership; await volume availability. |
| `journal_unavailable` | Refresh `Volumes`; offer explicit setup only if missing, or explain why the journal cannot currently be used. |
| `reset_required` | Close and start a new baseline; retain user choices and cached rows. |
| `busy` | Respect the per-volume/query/read bounds. |
| `cancelled` | Discard the unfinished cycle. |
| `overloaded` | Retain accepted data and report the resource limit; no retry loop. |
| `setup_failed` | Report that the journal could not be prepared; requery its state before an explicit retry. |
| `read_failed` | Discard this cycle; report that complete discovery could not be established. |
| `response_too_large` | Report the unrepresentable record; never truncate names. |

Serial changes during work produce `reset_required`; a new query must use the
refreshed volume descriptor. Version/framing errors use the shared disconnect
path. Invalid requests do not poison unrelated queries; a worker failure ends
only its own query. Connection loss closes all queries on that connection.

## Metadata acquisition and incremental work

The selected reader uses MFT records plus the USN journal. `FSCTL_ENUM_USN_DATA`
alone is insufficient for a promise of every matching hard-link location: its
record gives a name, not every alias or a link count that makes early extension
filtering safe. A baseline lists every file with `FSCTL_QUERY_FILE_LAYOUT`,
which returns each file's complete reference, every name with its parent and
DOS flag, its attributes, its unnamed data stream's size, its times and its
USN. The first step 1 implementation enumerated MFT record segments instead and
then opened every matching file for its facts. On a 2.2-million-segment system
drive with 91,930 matching files, that baseline took 25 seconds with a warm
cache, 17 of them in the opens, and 102 seconds cold. The listing-based
baseline takes 8–13 seconds warm and 18 seconds right after Windows' file
cache is emptied, and opens only files whose listed facts are incomplete. Single records still
come from `FSCTL_GET_NTFS_FILE_RECORD`, whose documented downward traversal can
return an earlier record than requested, so the reader checks the returned
identity and preserves the complete reference including its sequence.
[Microsoft file layout](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_query_file_layout),
[Microsoft record retrieval](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ni-winioctl-fsctl_get_ntfs_file_record),
[USN record fields](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-usn_record_v2).

Read every relevant `$FILE_NAME` attribute, including extension records linked
to their base reference. Include long/POSIX names and exclude DOS-only aliases.
Validate supported record layouts, lengths, parent/base references and record
integrity; never use reserved offsets as a guessed hard-link count. Retain
matching file identities, their small facts and each matching basename/parent
reference. Resolve their parent chains on demand through the same metadata
reader and memoize only those ancestors. Discard unrelated directory records
as well as unrelated file names during enumeration; do not build an all-directory
map and keep it as a convenient shortcut.
Raw records may incidentally contain small resident file contents; never
interpret, retain or export those bytes. This reads filesystem records, not a
recursive folder listing or media streams. It introduces neither a driver nor
a raw disk extent/run-list parser.
[Microsoft MFT structure](https://learn.microsoft.com/en-us/windows/win32/devnotes/master-file-table),
[file names](https://learn.microsoft.com/en-us/windows/win32/devnotes/file-name),
[record headers](https://learn.microsoft.com/en-us/windows/win32/devnotes/file-record-segment-header).

This is more parsing than the USN enumeration shortcut, but matching alternate
names make it necessary. The first implementation gate must measure the cold
cost before committing to production. If it is too slow, optimize this reader
behind the same interface; do not ship missing aliases or invent another pipe.

For a baseline:

1. Verify volume incarnation and journal identity; capture starting cursor J0
   before listing the files.
2. Enumerate metadata and construct the matching-name/sparse-ancestry graph,
   keyed by full references including reuse sequences. Resolve complete parent
   chains for baseline matches before capturing the ending journal boundary.
   Stage bounded result batches; unresolved ancestry cannot be treated as absence.
3. Capture a finite journal boundary J1 and reconcile changes from J0 through J1.
   Re-resolve changed identities and required ancestors, not just events with a
   matching reported suffix: names/attributes can move between MFT segments while
   the baseline is incomplete. Use current metadata rather than applying historic
   rename strings over newer observations. Correct earlier staged entries and
   validate missing bases/parents after reconciliation.
4. Check that journal identity/history remained valid. Return `complete` only
   when membership/path observations are reconciled and no required event is
   unresolved. The cursor stays internal to this open query.

For each later cycle, capture another finite journal boundary and process up to
it. Continuous writes do not force chasing an ever-moving end. All create,
rename and hard-link-change identities must be examined for current names even
when the journal reports a nonmatching alias. Retained matching files must be
refreshed for relevant changes regardless of the event's reported extension.
Definitive disappearance removes an identity; ambiguous read errors do not.

Resolve a changed physical file's complete current alias set, using the same
metadata reader and documented hard-link enumeration where appropriate. Check
identity and USN around observations that need to agree; retry bounded concurrent
mutation, otherwise fail the cycle. Do not derive deletion from access denied
or a sharing failure. Optional attributes can be unknown; unresolved membership
or ancestry prevents accepting a complete cycle.

Directory rename/move affecting a retained ancestor updates its node and
reconstructs all matching descendant paths, resolving any new parent chain.
An untracked directory cannot affect an existing matching path, because all
ancestors of every retained alias are represented. A new/moved matching alias
resolves and adds its required ancestors. Ignore untracked directory events
only after this graph is complete; the baseline still reconciles changed
identities that could repair incomplete enumeration. Settle matching-file changes
before deciding that a missing ancestor is a failure: moved/deleted children can
explain it. After a successfully reconciled cycle, discard ancestor nodes no
longer reachable from its final matching aliases, never while staging still
depends on them. Hard-link changes
refresh the affected file, not the entire volume. Unrelated directory activity
must not grow the graph or force a baseline; no directory rows cross IPC.

The result is a reconciled live observation, not a VSS snapshot. Reads may see
state newer than the captured journal end; subsequent cycles revisit those
events. Never let an older queued record overwrite a newer resolved observation.
A changed journal identity, missing history, or unresolvable metadata ends the
query instead of advancing beyond uncertainty.

## Memory and scheduling

The helper keeps **matching physical files, their matching basenames/parent
IDs, and only their ancestor directories**. It does not keep unrelated file or
directory names, media bytes, subtitle contents, decoded JSON history, or full
path strings for every node. Small retained file facts let a folder move emit
correct replacements without reopening every descendant for its attributes.
Full paths are assembled only for bounded outgoing batches.

The following is an engineering estimate, not a measurement. For a compact
native graph with ordinary name lengths, allow about 120–200 bytes per retained
ancestor and 160–320 bytes per matching location, including small file facts and
index overhead. More/longer names and allocator overhead increase those figures;
hard links count as locations. The directory count is **ancestors of matches**,
not all directories on the drive.

| Retained ancestors | Matching locations | Estimated retained graph |
| ---: | ---: | ---: |
| 500 | 2,000 | 0.4–0.7 MiB |
| 2,000 | 10,000 | 1.8–3.5 MiB |
| 10,000 | 100,000 | 16–32 MiB |
| 100,000 | 500,000 | 88–172 MiB |

Allow roughly another 4–8 MiB of ordinary active parsing/batch workspace for one
query, plus executable/runtime/OS overhead. Multiple prefetched query batches,
unusually large alias sets and allocator behavior increase peak use; the budget
below covers them. A provisional whole-process target for the first two examples
is roughly 10–30 MiB, to verify in implementation, not a measured guarantee.
Library's existing database/UI memory is separate.

Counts sum across open queries. Terabytes of video do not themselves increase
memory: ten huge videos need roughly ten filename records. A million unrelated
source-code directories are examined but do not remain in this index. Sparse
ancestry adds metadata lookups for required parents; that modest I/O/memory
tradeoff is selected because retaining every directory is unnecessary.

Keep the small graph while discovery is enabled and Library is active, or for
the duration of a finite engine locating job.
Release baseline scratch space and consumed batch buffers promptly; idle updates
do not retain the baseline's peak workspace. Leaving Library, closing the window
or disabling discovery releases its query memory and handles. A concurrent engine
job releases its own state when it ends. Both leave the elevated process available.
**Typing a Library search uses SQLite; it neither allocates this
index nor rescans the disk.** Verify
retained allocations, process private bytes and working set separately.

For a helper used ten minutes a day, the other hours retain no graph, pending
JSON, worker backlog or disk polling. A provisional idle target is a few MiB of
private memory (budget 2–8 MiB until measured in a release build), separate from
shared executable pages and Windows working-set accounting. Repeated open/close
cycles must return private committed memory near the measured pre-query baseline;
destroying objects while an allocator retains the peak is insufficient. Adjust
query allocation ownership if measurement shows retention; do not hide it with
working-set trimming. Resident does not mean reserving the active-work budget.
Tracking only a few missing filenames normally retains a much smaller graph than
a media query, though parsing workspace still applies and common names can have
many matches. Neither enabling tracking nor adding torrents allocates a drive index.

Start with a **128 MiB process-wide budget for discovery allocations**,
including all graphs, working buffers and queues. It is a ceiling, not a
reservation or expected use, and is not a hard process-working-set limit.
Allocate only for actual retained records and in-flight work. Admit allocations against the
aggregate budget; failure ends the affected query with `overloaded`, releases
its state and preserves accepted Library data. Do not repeatedly evict/rebuild
active queries, silently omit files, or introduce disk spilling/another database.
No memory slider is needed in the first product UI.

| Choice | Benefit | Cost |
| --- | --- | --- |
| Raise the budget | Larger directory/media collections fit without failing. | Higher possible RAM pressure and paging; it does not speed a graph that already fits. |
| Lower the budget | Tighter maximum competition with the UI and engine. | Large volumes can fail discovery; it cannot make required records disappear. |
| Retain the graph between cycles (selected) | Folder moves and file changes stay incremental. | Retains the graph's RAM while Library is active. |
| Release it sooner | Returns that RAM. | Requires another baseline before complete updates; do this at query close, not every poll. |

Keep one produced batch ahead of each consumer. Start with 1,024 changed
identities or 1 MiB encoded JSON, whichever comes first. A single complete alias
set may use a larger batch up to the common frame limit; exceeding that fails
explicitly. Use the common 16 MiB frame limit for requests too, including bounded
exact-name selections; reject oversized requests from the length prefix before
allocating/decoding. The engine's existing frame bound remains unchanged.
Account for accepted frame/JSON allocations too, so invalid requests cannot
bypass the helper budget. These are bounds on one transport, not another codec.

Start with one baseline worker across the helper and a separate bounded journal
worker for warm queries, each with one outstanding I/O. The baseline yields
between bounded record groups; warm queries rotate between bounded journal
groups. Pipe dispatch and `Close` remain independent of disk waits. Use Windows
background I/O/CPU priority and cancellable I/O where supported. Do not promise
instant kernel cancellation; release pending work without holding dispatch
locks. This bounds competition on the owner's laptop while allowing an active
volume to keep up during another volume's baseline.

Memory buys retained ancestry and membership, not faster UI searches: those
already query Library's SQLite store. It also cannot fix a slow per-record syscall,
blocked disk, expensive DB acceptance or UI-thread work. Measure each separately.

## Applying results in Library

The current `FileSources.Reconcile` withdraws origins absent from the torrent
summary, and current file identity is a torrent integer index. Correct those
owners once for source-scoped reconciliation and typed source identity; do not
add a parallel ingestion/store path. A disk origin is a volume incarnation.
Torrents keep their addition identity/file index. Neither source withdraws the
other's contribution. Migrate the existing location owner's case-insensitive
path uniqueness so distinct directory entries survive, including case-distinct
aliases of the same physical file. Coalesce torrent/disk contributions only when
they are established to refer to the same actual location; string case folding
alone is not that evidence.

Schema changes are preserving forward migrations through the single `Database`
owner. Persist accepted file facts/locations there; the shared selected volumes
belong to engine settings and the media filter comes from existing classification
owners. Persist no query IDs, helper graphs, cursors or confirmation. Disk confirmation remains
temporary and tied to the accepted query generation: clear it on query close or
replacement, disconnect, unavailability, filter change or failed reconciliation.
Leaving Library therefore invalidates confirmation while retaining cached
browsing. Restore confirmation only through
successful current acceptance, never because cached rows exist.

The shared source owner resolves the physical identity of current unfinished
torrent files using bounded metadata-only opens under the ordinary user token,
on source changes/confirmation, not by opening every discovered disk file.
Known unfinished identity suppresses every matching alias. A definitely absent
file has no identity to suppress. On ambiguous identity/access failure, retain
the exclusion of its known unfinished paths and any established aliases, report
the affected failure, and retry on source changes. Accept unrelated filesystem
observations. An alias with an unknown identity cannot be proved related; the
product accepts that narrow uncertainty instead of hiding a whole drive. It
does not grant permission to read the known unfinished locations.

Stage each finite cycle in SQLite, in short transactions with no authority over
published membership. Later entries replace earlier staged versions by physical
identity. At `complete`, accept the final staged changes atomically through the
existing reconciliation/cleanup owner, including covered known absence.
The first cycle also withdraws old disk
contributions not seen in the baseline; later cycles apply only their changes.
Only that acceptance publishes new membership and enables current file work;
the saved catalogue remains browsable during catch-up.
Interrupted or failed staging is discarded without deleting saved choices,
locations or subtitle associations. Keep staging out of the C# object heap.
Unknown size/time remains unknown in the shared source/store representation,
or is obtained by its ordinary metadata reader; never substitute zero for null.

Use set-based reconciliation and publish after commit; do not call expensive
per-file cleanup/presentation while receiving batches. Measure the final
transaction with a large source: bounded wire batches do not guarantee a short
database acceptance. Keep database work off the UI thread and adapt the existing
owner if needed, rather than inventing a second writer.

Whole-volume discovery can produce many candidates, including application
videos and sounds. Keep the existing automatic video-information setting and
per-movie search protection for every eligible video; disk origin does not
silently make it manual-only. Filename search works before enrichment. Change
the existing provider and file-facts owners to retrieve bounded pending batches
from SQLite, because both currently materialize their entire pending populations.
Recheck current eligibility before each read/request and preserve cancellation
and fair scheduling. There is no second work queue, arbitrary folder exclusion,
minimum duration or guessed personal-media filter.

One existing ambiguous suffix is `.ts`, shared by transport-stream video and
TypeScript. Keep such candidates searchable by filename, but admit them to
automatic movie identification only when the existing ordinary-user file-facts
reader supplies positive video evidence. Empty properties or a failed handler
mean unknown, not nonmedia; manual identification remains available. Apply this
eligibility rule in the existing owner, independently of data-source origin.
No container parser or preliminary content pass is added, and waiting for
evidence is not a provider attempt or a saved no-match result.

Stable physical identity preserves explicit identification across moves and
baseline rebuilds. A changed stamp invalidates facts across every matching alias
and overlapping contributor to that location. File-reading workers capture the
current source generation/stamp and may save only if still current. Existing
path/size checks alone cannot reject a stale read after a same-size edit;
reconciliation must also prevent an old torrent contribution from restoring
invalidated facts. A new baseline generation alone does not discard user choices.

Confirmed deselection withdraws that source through normal cleanup. Physical
disconnection does not. Remounting refreshes location roots; never let a reused
drive letter open a different volume's cached files.

### Actual files, not stale catalogue rows

Source membership and physical presence answer different questions. A torrent
can still own a manifest entry after its payload was deleted. The shared source/
location owner records physical observations; the Library query owner uses that
one eligibility predicate across rows, search, counts, facets, actions and automatic
file/subtitle work. A completed torrent list alone cannot override known absence.

Windows Recycle Bin locations are ineligible, even though their files still
exist and may retain a media suffix. Recognize the per-volume root
`$Recycle.Bin` subtree in the shared location owner; an ordinary nested folder
with that name is not the volume's bin. Keep this exclusion in one place for
every source. Retain the helper's physical identity and candidate membership
while it still reports the recycled file, but do not show, open, identify or
automatically read that location. Normal source cleanup applies when it actually
disappears. Restoring a file/folder makes its outside-bin locations eligible
again under current confirmation and preserves valid choices. Other hard links
outside the bin remain eligible throughout. No recycle-bin browser or recovery
operation is added. [Windows Shell file management](https://learn.microsoft.com/en-us/windows/win32/shell/manage).

An accepted baseline reconciles **all existing Library locations covered by the
verified volume incarnation and exact extension filter**, including torrent-only
locations the disk source has never previously reported. An accepted later cycle
replaces alias sets and removes the locations that disappeared. Apply the final
cycle state atomically, so a replacement file at an old path is not confused
with a late deletion of its predecessor.

Record known absence only for covered locations, with their source/physical
identity binding and volume incarnation. Keep that fact while a live source
still contributes the location; ordinary cleanup removes orphaned observations.
Disconnect, query failure, disabled discovery or deselection are not positive
evidence and cannot resurrect a known-missing entry. A current physical
observation can restore a location; a new file ID at the same path is a new file
and cannot inherit the old movie's identification merely through the path.

| Situation | Library behavior |
| --- | --- |
| Confirmed deletion of the only location | Remove it from normal Files/video/music results and search automatically, even if a torrent still lists it. Retain the torrent itself. |
| File or containing folder is recycled | Exclude its bin locations from every normal result and background read; report the missing original torrent path. Restoration can restore eligible locations; only the engine's validated recovery/adoption path can resolve a torrent hold. |
| One hard-link location disappears | Remove that location; keep other confirmed matching locations. A grouped movie remains while it has an eligible video location. |
| File moves | Update its location under the same physical identity and preserve valid choices; remove the old path. |
| Reopen after files changed while idle | Show the saved searchable catalogue immediately, excluding known absence. Reconcile promptly and refresh automatically; current file work waits for confirmation. Temporary stale observations are possible until catch-up completes. |
| Drive disconnects or cannot be checked | Clear current confirmation and retain saved data. A known disconnected or locked drive is unavailable; exclude its locations while unavailable. A transient query failure alone neither proves disappearance nor withdraws the saved catalogue. |
| Subtitle disappears | Remove its file location and update associations/language counts through the existing owner. |

This extends saved-list browsing for volumes covered by disk discovery. The
previous [file-access limitation](../contracts/library/files.md#file-access-and-unavailable-entries)
left stale paths until Open failed; completed filesystem reconciliation now
removes them automatically. The observation gap remains explicit. Torrent-only/
uncovered sources retain their existing policy. No history browser or new
per-keystroke existence check is added.

After each accepted cycle, invalidate and refresh the active Library query
through its existing owner without waiting for the person to type or refresh.
Five seconds is the initial journal polling cadence, not a promised maximum
deletion latency; I/O, acceptance and rendering also take time. Measure
deletion-to-row-removal and cold-start-to-current-results as product outcomes.
Fast search must not be obtained by calling stale cached rows current files.

For app-initiated deletion, only a definitive per-location outcome from the
existing file-operation owner establishes absence. Accepting a delete request,
removing a torrent, partial cleanup or failing to launch a player does not.
Feed definite outcomes to the same presence owner; no second deletion policy.

## Missing torrent files require an explicit choice

The engine has a general `Error` status and transient disk errors, but no durable
missing-payload hold. `SetIntent` currently clears those errors and `ApplyIntent`
can resume transfers. The existing `verifyFiles` flag describes post-move
verification, not permission to download again. Reuse the status and the engine's
single policy owner; add typed `FilesMissing` and `FilesUnverified` reasons and
one saved hold independent of Paused/Resumed/Forced intent.

This new automatic presence guarantee and volume-binding migration cover local
NTFS payload, including known torrent files outside the media extension filter.
Existing engine destinations on other filesystems or network shares retain
their current storage/error behavior; they do not become subject to an NTFS
identity requirement or acquire a discovery fallback. Discovery itself remains
strictly NTFS metadata plus its journal.

`FilesMissing` means previously present, selected payload is now definitely
absent. `FilesUnverified` means recovery could not establish prior payload state,
such as a legacy installation with unreadable resume data. Do not call this
deletion. Both prevent payload transfers until recovery resolves them. Opted-in
tracking authorizes validated reuse of existing files, never repair downloads. An offline
drive, denied access or failed probe is an availability/error outcome, never
missing-file evidence; keep transfers blocked while it cannot be checked.

### Evidence, detection and the transfer gate

Persist compact groups of expected local NTFS file indices with torrent facts:
files actually observed on disk or settled complete, never every entry in a new
manifest. Bind
each group to its actual resolved local volume incarnation (`VolumeIdentity`).
Normally there is one group; remapped files or mounted subtrees on another
volume add a group, without duplicating paths. Capture the binding when recording
presence, and update it with the existing settled name/move transaction. Keep
this evidence independently of libtorrent's changing piece map, so a recheck
cannot erase the only record that a file previously existed. Batch changes through
the existing ordered settings writer; commit newly completed-file expectations
before advertising completion. Migrate older torrents from readable pre-check
resume evidence using saved priorities and actual name mappings. A piece spanning
a skipped file may live in libtorrent's partfile; that alone does not prove the
skipped file's path ever existed. If evidence for local NTFS payload is unavailable,
use the unverified hold and
allow metadata retrieval with zero payload priorities until recovery is resolved.
An absent legacy field means unknown history; a saved empty group list means no
tracked NTFS files have yet been recorded as expected. It does not prove that
none ever existed, and says nothing about payload on
other storage targets. New additions save that empty list explicitly.
Legacy evidence without a volume binding cannot infer its former volume from
today's drive letter. A present file can establish its current binding; absence
without a prior binding remains unverified rather than proving deletion.

Reconcile trustworthy pre-check resume/progress evidence for current torrents
too, not just legacy migration, and record observed materialization at ordinary
pause/checkpoint boundaries. If prior payload is indicated but cannot be resolved,
use the unverified hold. A file created and deleted before any evidence was
retained cannot be distinguished from a never-created file. Do not promise that
the expected set detects every intervening deletion or add per-piece history.

Expectations follow the engine's actual name/move mappings and immutable metadata
indices; they do not contain a duplicate list of paths or pieces. Pad files and
never-created files do not become expected from the manifest. Keep the history
of previously present skipped files, but only currently wanted payload can cause
a transfer hold. Selecting a skipped file again passes the same presence gate.

Use the existing payload worker to check known paths at restore, before releasing
transfers, on relevant native file errors, and in bounded periodic batches while
the engine runs, including with the window closed or discovery disabled. Start
with a 60-second sweep target, round-robin
across torrents, and measure the cost and actual coverage interval. This work
checks known torrent paths; it does not enumerate directories or build another
disk index. Definite file-operation outcomes and the Library reports below enter
the same owner. Validate the current mapping/generation on return from the worker;
an engine-owned rename or move must not be mistaken for deletion. Missing an
unrelated hard-link alias does not mean the torrent's own path is missing.

Definitive absence requires the current path to resolve onto the saved volume
incarnation. Probe the relevant existing parent/mount root without enumerating
folders. An offline original volume, reused drive letter or replaced mount stays
unavailable, not `FilesMissing`, even if another volume is now mounted there.
An existing NTFS binding remains authoritative even if the replacement uses a
different filesystem. Only an explicit settled location change can leave this
scope; restart or remount cannot discard the binding by checking today's
filesystem type first.
Do not bind that replacement automatically or create payload there; the existing
explicit location-change operation must settle it. These are ordinary-user
metadata checks of known torrent locations, not another discovery mechanism.

Before startup checking or `Verify`, install a **check-only transfer gate**.
Libtorrent can treat absent files as missing pieces without reporting a disk
error, and `force_recheck` normally permits downloading after checking. Its
[`stop_when_ready` flag](https://libtorrent.org/reference-Core.html#torrent_flags_t)
prevents the completion-alert-to-pause race. Supply it in restore parameters
when metadata is available. Metadata-less restoration instead uses zero payload
priorities and `default_dont_download`: metadata acquisition itself counts as
ready, so `stop_when_ready` would stop it. Once metadata arrives, install the
check-only gate before applying wanted priorities. For an existing torrent,
hold `ApplyIntent`, pause and disable auto-management,
transition to checking, arm the flag, then permit checking through ordered
operations in one libtorrent-thread callback, before an I/O completion can
interleave. Arming it while already transferring consumes it
immediately; leaving the torrent permanently paused prevents checking altogether.
Confirm this ordering against the pinned implementation before coding it.

The engine inspects the check result before releasing the gate. All paths that
can start transfers, including naming completion, automatic recovery, scheduler,
Resume, Force and Resume all, respect the same hold. On confirmed loss, stop
transfers immediately and persist the hold before acknowledging it. A failed
save keeps the live hold and reports persistence failure; it cannot claim durable
success. Restore the hold before a torrent can transfer after restart.
Ordinary presence checks never dismiss an existing recovery hold; its resolution
belongs to the explicit recovery actions or the opted-in adoption path below.

Automatic detection is asynchronous. Five-second helper cycles and the headless
sweep do not guarantee zero bytes between an external deletion and its detection;
an already issued OS/libtorrent write can also finish. The enforceable rule is
**no automatic network repair once loss is established**, including through
recheck and restart. Validated adoption may restore prior transfer intent using
existing files. Do not claim a polling design intercepts every write.
No filesystem watcher, storage replacement or second privileged engine is selected.

### Engine interface and DTOs

These four commands extend the existing engine endpoint and its typed adapters.
They do not add torrent operations to the finder endpoint or change framing.

| Operation | Command | Input | Successful output |
| --- | --- | --- | --- |
| `StartDiscovery(DiscoveryStart start)` | `start_discovery` | `DiscoveryStart` | empty object after activation is scheduled or joined; readiness follows through engine state |
| `ReportMissing(MissingFiles files)` | `report_missing_files` | `MissingFiles` | empty object after a durable hold is accepted; stale reports are harmless |
| `FindFiles(Recovery recovery)` | `find_files` | `Recovery` | empty object after a locating job for the current missing hold is scheduled or joined |
| `DownloadFiles(Recovery recovery)` | `download_files` | `Recovery` | empty object after authorization commits |

```text
Existing engine settings additions
  discover_media: bool = false
  track_moves: bool = false
  move_check: quick | full = quick
  volume_scope: VolumeScope = { kind: fixed }

VolumeScope =
  { kind: fixed }
  { kind: selected, volumes: VolumeIdentity[] }

DiscoveryStart
  kind: automatic | retry

DiscoveryState =
  { kind: inactive }
  { kind: starting }
  { kind: ready, process: ProcessIdentity }
  { kind: unavailable, reason: elevation_declined | start_failed | helper_lost }

ProcessIdentity
  pid: uint32
  created: string

MissingFiles
  torrent_id: string
  files: MissingFile[]

MissingFile
  index: int32
  path: string

Recovery
  torrent_id: string
  hold_id: string

ExpectedFiles
  volume: VolumeIdentity?
  indices: int32[]

RootIdentity
  volume: VolumeIdentity
  file_id: string

PayloadHold =
  { kind: missing, hold_id: string, files: int32[] }
  { kind: unverified, hold_id: string }
```

These settings use the existing settings read/update path, not new commands.
`fixed` selects currently available fixed NTFS volumes, re-evaluated as descriptors
update; `selected` uses the explicit volume incarnations and accepts an empty list.
Both consumers resolve this same scope. A slow volume probe does not permanently
exclude a drive through one-time default initialization. Present the default as
“Fixed NTFS drives”; choosing drives saves the explicit selection. Newly observed
missing journals still require the explicit setup action. A locating job captures
the currently available scope when it starts, as described below.

`DiscoveryState` accompanies the existing engine summary/settings state. Activation
runs outside pipe dispatch: a person may take longer than the pipe timeout to
answer UAC, so the command acknowledges scheduling, not readiness. `automatic`
respects session suppression after a declined/failed launch; `retry` is an explicit
user action. At least one discovery/tracking option must be enabled. Coalesce
concurrent requests, including engine startup; the UI uses the ready process
identity to authenticate its direct helper connection. `created` is the exact
Windows process creation `FILETIME`, encoded as fixed-width unsigned hexadecimal,
not a rounded wall-clock timestamp. Helper loss invalidates readiness and all
queries. Settings and activation state do not introduce a second helper launcher.

`ExpectedFiles[]` and the optional `RootIdentity` are saved engine evidence;
the latter identifies the current content directory for targeted move detection.
They do not duplicate paths already owned by torrent mappings. Null volume in
expected-file evidence represents legacy unbound evidence and cannot establish
deletion on a reused mount. These saved facts do not cross the UI pipe.
`PayloadHold` is saved engine state. An optional hold is also exposed as a typed
field on the existing row/detail DTOs; its absence means no recovery hold. Use
the existing durable torrent addition ID, not an info hash or a reused UI row
number. Indices are deduplicated metadata indices; `MissingFiles.files` has
1–1,024 entries and larger reports are split within common frame limits. `path`
is an exact current-path guard, not permission to inspect an arbitrary location.
The engine resolves its own mapping and independently establishes absence on its
payload worker. Stale reports and already durably held files are idempotent successes;
an in-flight or failed hold save must join or retry the existing commit and
cannot acknowledge success until it commits. A live hold alone is insufficient.
Unavailable/failed probes are failures, never empty successful checks.
When tracking is enabled, a new missing episode also schedules its finite locating
job. Reporting never waits for a volume baseline or hash check. Library supplies
no candidate DTO and owns no matching/adoption policy. Progress and outcome use
the existing engine operation/row path; duplicate reports never start another job.
`FindFiles` is the explicit Retry action for that torrent's current missing hold.
It requires tracking enabled, rejects a stale hold ID, and joins an existing job
instead of starting a duplicate. If the helper is unavailable, it may explicitly
retry activation through the same owner; a declined prompt leaves the hold.
The reply never waits for UAC, a baseline or hashing. This command changes no
download authorization. An unverified hold uses explicit location/Verify recovery
instead of pretending the prior payload is known.

Reuse the engine's error envelope: `invalid_request` for malformed reports,
`unavailable`/`files_busy` for unresolved current paths, `storage_failed` or
`overloaded` for unsuccessful persistence, and `recovery_required` for a stale
recovery choice. Refresh the authoritative row before offering that choice again;
never automatically replay an uncertain `download_files` request.

The hold ID is opaque, persisted and changes for each new episode or enlarged
affected set. A stale recovery action is refused and the client refreshes the
row. An unverified hold stays unverified until the whole uncertainty is resolved;
one absent file cannot narrow it prematurely. The engine's row and file-detail
completion fields must stop advertising confirmed missing payload as complete,
even when libtorrent's cached progress still says 100 percent.

Show “Download missing files” for a missing hold and “Download files” for an
unverified hold. The command preserves the person's wanted/skipped selection,
checks the current hold, and commits explicit repair authorization and resumed
intent before allowing payload transfers. Reconcile libtorrent's cached piece
ownership through the same check-only operation before releasing the transfer
gate: clearing the error and resuming a cached 100-percent torrent is not a
repair. The reply acknowledges committed authorization, not completed checking
or download. For a missing hold it removes expectations
only for the selected missing files authorized in that episode; they become
expected again when materialized. Keep skipped-file history. Neither a late
deletion report nor generic Resume can clear or immediately recreate the same
authorized hold. If further loss is found, retain the hold with a new ID and
require a current choice. Normal session pause and scheduling still apply.
Individual Resume/Force on a held torrent returns `recovery_required`; Resume all
can resume eligible torrents while leaving these held. Pausing remains allowed.

Cheap known-path checks and normal fast-resume validation remain the ordinary
path; a presence sweep does not hash every payload. `Verify` remains the existing
command, running through the check-only gate.
Partial restoration keeps the hold. If all selected required files are restored
and verify, clearing the hold also commits Paused intent (Stopped in the UI): verifying starts
neither downloading nor seeding. A recheck without an existing hold may retain
normal intent only after confirming it found no loss requiring recovery.
Remove torrent remains available; it does not request a download.

### Following files moved outside TinyTorrent

The user also wants the engine to use torrent files at their new location,
including on another drive, with optional rehashing. Treat this as **adoption**:
update the torrent's location to use files already there. It never copies,
overwrites or moves remaining files. The engine's existing payload-operation
owner makes that decision; neither the helper nor Library changes torrent paths.

The initial automatic case is one coherent complete wanted-payload layout at one
new root, with no unresolved partfile dependency. Derive the
new root from the engine's actual relative names and validate all required
backing files there, including nonmedia files, using known paths without folder
enumeration. Conflicting roots, individually renamed/scattered files and incomplete
external copies use the existing explicit location
recovery. Do not guess a new layout or leave claimed pieces backed by an absent
skipped-file partfile. This scope provides the ordinary completed-movie/folder
case without creating a second file-management system. Build a locating job from
the missing expected files and existing immutable torrent metadata; do not add
historical completion state or a second manifest for a continuous watch set.

**Track moved torrent files** is independent of Library and off by default.
When off, neither automatic adoption nor content-directory following runs;
ordinary missing-file protection still applies. When on, a newly established
missing episode schedules one locating job under the same payload owner. It can
also try held torrents once when tracking is newly enabled, or after an explicit
retry/location choice. An unchanged missing path on every presence sweep, an
engine restart or an unchanged Library report does not start another search.
Schedule from the transition into a new durable missing episode, not from reading
an existing hold. Duplicate observations join the current work; restored holds
and interrupted attempts offer explicit retry. No saved attempt ledger is needed.
A newer missing episode cancels stale work; turning tracking off cancels it too.

For a same-volume folder move, save its content-directory `RootIdentity` when
observed and update it at settled engine-owned location/name changes. At the
start of a locating job, resolve that directory with ordinary-user
`OpenFileById` and obtain its current path. The volume hint can be a handle to
any accessible file on the volume; no raw-volume access or elevated engine is
needed. Validate incarnation and the derived payload paths before adoption.
Access failure remains unavailable. A file ID plus one surviving hard-link path
does not prove that the torrent's own link moved and alone cannot select an
adoption destination. The general candidate query and checks below also apply
to same-volume moves into a different folder. A surviving hard link can qualify
like another existing matching copy; opted-in reuse does not claim proof of a
historical move. This needs no saved per-file identity catalogue or restriction
that only whole-folder moves on the same volume can work. Recycle Bin paths are
never destinations.
[Known-identity lookup](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-openfilebyid).

If known-directory resolution supplies no usable destination, the engine asks
the helper directly: open a `files` selection containing exact basename/expected
size pairs for the missing files, on the selected available NTFS volumes. Across
drives the physical identity changes, so filenames and sizes find candidates;
they do not prove ownership. Include known nonmedia torrent filenames only in
this finite selection. Never send `*.*`, broaden Library's extension set or retain
other payload names in anticipation of a future loss. The metadata baseline may
examine the volume's other records, discarding nonmatches immediately.

One engine-owned, bounded transient map accepts candidate replacements/removals
by volume incarnation and file ID. Apply every batch, including corrections;
incomplete cycles cannot establish absence or uniqueness. Finish the baseline
for every available volume selected when the job starts before choosing a root;
failure of a requested volume leaves the job unresolved. Close each query after
acceptance. Unavailable/unselected volumes are outside this observation, so never
claim global uniqueness. Mount/selection changes require a fresh explicit attempt,
not a live watch. Do not silently truncate candidates to the first match. Bound
engine candidate allocations to 16 MiB initially; overflow leaves the hold and
offers an explicit destination. This state is separate from the helper's budget
and is released at job end; it is not another catalogue or SQLite database.

Derive possible roots from the engine's actual relative layout, then validate
all required known paths, volume identities, local-content availability and sizes
as the ordinary user. Recycle Bin and reparse/recall locations are ineligible.
Multiple plausible complete roots require the person's choice. Recheck identity,
layout and current hold/mapping before settlement; a completed query is an
observation, not a lock on those files. A matching movie alone does not establish
that the other required files moved. No match, ambiguity or failure leaves the
hold and offers Retry or the existing explicit location recovery, without an
automatic scan loop. Searching only after loss cannot promise immediate move
detection; the known-path detection interval and finite baseline both contribute.

The engine-owned **Moved-file checking** setting offers **Quick** by default and
optional **Full** verification. Capture the mode for each adoption operation.
Quick checks up to three distinct complete pieces near the beginning, middle
and end of every nonempty wanted file, deduplicating pieces shared by files.
Small files may touch fewer pieces. Use the torrent's existing hashes, not new
4 KiB fingerprints or a fingerprint store. A passing sample supports trusting
the remaining existing piece claims; it does not establish that every byte was
verified. Full checks all required payload. Both modes require a unique coherent
layout and all claimed backing storage; neither permits missing files, ambiguous
ownership or automatic redownloading after a mismatch.

Quick additionally requires the live torrent's complete wanted-piece ownership
to remain intact. A missing-file recheck can already have cleared those claims;
`reset_save_path_unchecked` does not restore them, and sampling does not prove
all pieces. If that precondition is lost, keep the hold and offer Full. Do not
resume an incomplete piece map, silently run Full, invent piece-history storage
or remove/re-add a torrent in seed mode to bypass the requirement. Full can
establish complete ownership through the existing gated check.

Run bounded sample work through the existing payload/native-handle seam, with
one outstanding libtorrent `disk_interface::async_hash` call. Its v1 result
hashes the entire piece, including data in adjacent files and padding; a slice
inside one file is insufficient. For v2, combine its SHA-256 block hashes using
libtorrent's Merkle primitives and compare against an authenticated piece-layer
hash or the file root, respecting short final pieces, small-file tree width and
zero-leaf padding. Obtain bounded expected hashes/proofs from the current torrent;
do not copy every file's piece layers. Missing authenticated hashes, unavailable
required bytes or unsupported checking leave Quick unresolved and offer Full,
never metadata-only acceptance. Do not call piece-completion callbacks such as
`on_blocks_hashed`: they can change piece ownership and verify adjacent pieces.
[Pinned disk hashing](../../3rdParty/libtorrent/include/libtorrent/disk_interface.hpp),
[Merkle primitives](../../3rdParty/libtorrent/include/libtorrent/aux_/merkle.hpp),
[v2 hash structure](https://www.bittorrent.org/beps/bep_0052.html).

Validate sample selection and checking against v1 cross-file pieces, v2 short
files/final pieces, hybrid torrents, missing proofs and mismatches; a sample pass
must neither claim full verification nor release an unresolved recovery hold.

Reuse the existing move owner with an explicit adoption operation. Pinned
libtorrent supplies `reset_save_path` for Full and `reset_save_path_unchecked`
for Quick's gated location reset before its sample checks. The existing
`SaveMove` always sets `verifyFiles=true`, so its finalization must honor the
adoption operation's captured verification requirement. Automatic adoption must
never enter `dont_replace`, physically move files, or delete placeholders.
Use the existing shared-file holds, disk-release callback, ordered persistence
and interruption recovery; do not call unchanged `StartMove` and assume it is
adoption-only. Persist its adoption purpose, destination and captured verification
requirement before acknowledging acceptance, so restart cannot fall through to
a physical move or unchecked transfer. Check the current mapping again before
commit. Hashing runs as operation work, never inside a pipe reply's deadline.
Keep the prior authoritative download location until adoption succeeds. A temporary
libtorrent path reset for checking is not that commit. If checking fails or is
cancelled, restore the prior live mapping under the gate, or leave the operation
unresolved and block repair until restoration settles. A later Download files
must never write into an automatically proposed destination that was rejected.
The existing move finalizer commits the destination before verification, so that
ordering cannot be reused unchanged for adoption.

Preserve the person's intent: a Stopped torrent stays Stopped, and an otherwise
eligible torrent can use the adopted files after successful settlement. Tracking
opt-in is standing permission for this validated reuse. Persist the missing hold
promptly before locating; an accepted adoption captures its ID and affected set.
After validation accounts for all missing wanted payload and confirms complete
piece ownership, settle the new location and resolve only that matching
location-related hold in the same ordered commit. An enlarged/replaced hold,
unverified prior history or another blocking condition cannot be cleared by a
stale adoption. Hold transfers throughout validation. Failure keeps ordinary
missing/unverified protection and never grants repair permission. This differs
from an explicit Verify of restored files, which settles to Stopped as above.

### One deletion path across Library and engine

After helper `complete`, derive final covered absence from the staged cycle.
Accept that observation atomically in Library, then promptly report each affected
live torrent's own path through the engine client. Engine validation stops
transfers and acknowledges only after a hold is durable. Its tracking owner can
then locate candidates independently of Library. Filesystem presence
publication does not wait for engine acknowledgement; a failed report or torrent
save does not hide unrelated files or delay removal of a confirmed deleted row.
Report the affected failure without claiming durable protection succeeded;
accepting a report is not a claim that locating or verification has finished.

Known absence already persists while its torrent contribution exists. Derive
reports from it on reconciliation and engine reconnect; retry failed reports
with the existing bounded work scheduling, without replaying successful ones
on every empty discovery poll. The engine's authoritative hold and current
mapping decide whether another report is needed. This uses no separately stored
delivery queue or cross-process transaction. Independent engine presence gates
and restart checks cover interruptions before delivery; the UI is not the sole
protection against redownload. Replayed reports are harmless. A hard-link
deletion affects only torrents whose actual paths disappeared; preserve the
existing shared-file owner when identifying affected torrents. App-owned deletion
uses the same engine hold for any surviving affected torrent; removed torrent
rows need no new state. Engine protection also covers known local NTFS payload
types outside the helper's media filter, without expanding Library's discovery scope.

This deliberately refines the engine contract's Resume-clears-disk-error rule:
ordinary errors keep their current recovery, while these durable reasons require
their named choices. The existing C# Library policy remains in C#; native code
owns only its torrent transfer policy and filesystem acquisition.

## Subtitle association

Subtitles are first-class Files candidates even when unassociated. Feed the
queried paths into the existing subtitle matching/language owner. For disk
videos the matching scope is the video's containing folder plus the established
`Subs`/`Subtitles` locations beneath it, never an entire volume origin. Apply the
existing matching and sole-video policy only inside that local scope; keep
ambiguous captions unattached. This prevents `English.srt` elsewhere on a drive
from being attached to its only movie.

Found captions retain `created=false`; discovery does not grant ownership to
move/delete them. A move or removal updates associations through that same owner.
Torrent-only installations keep their current bounded local discovery input,
sharing matching policy. Disk discovery itself starts no network acquisition,
parses no captions in the privileged process and does not enroll every disk
video in torrent-specific subtitle jobs.

## Privilege and installation

The helper has a `requireAdministrator` manifest and rejects unelevated startup.
The engine uses `ShellExecuteEx` with `runas` and retains the returned process handle
for initial connection verification. Windows supplies consent or administrator
credential entry; TinyTorrent never collects those credentials. A manifest
requests elevation rather than bypassing it.
[Windows elevation](https://learn.microsoft.com/en-us/windows/win32/secbp/running-with-administrator-privileges),
[launch semantics](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-shellexecuteinfow).

Bootstrap arguments identify the launching engine by its actual PID and process
creation time. Before readiness, the helper opens that engine,
validates its expected installed image, creation time and Windows session, and
derives the original logon SID from the engine's token. Hold that exact process
handle and exit when it signals. This handles engine exit/crash without a new
shutdown command and avoids depending on a product window surviving UAC.
Alternate-admin consent must not switch Library to the administrator's profile.

Use a local-only `TinyTorrent.Finder.<logon SID>.<engine PID>` pipe through the
shared implementation. Grant the original logon SID individual client data
rights, reserving pipe-instance creation and ACL changes to the elevated server.
Preserve first-instance claiming and remote rejection. Do not use generic write
access, which also grants instance creation; adapt the shared client's requested
access rights once. Authenticate clients from Windows, not request fields.
[Microsoft pipe security](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights).

Both clients verify the actual pipe server PID, creation time and expected
executable image using Windows process queries, including on reconnect. The
engine binds its first connection to the process returned by launch; the UI
binds to the engine's ready `ProcessIdentity`. For alternate-admin use the helper
grants the original logon SID only `PROCESS_QUERY_LIMITED_INFORMATION` on its
process object. Use `QueryFullProcessImageName`; the existing `EngineProcess.Executable` path
that tolerates inaccessible elevated `MainModule` information is insufficient.
Grant no process mutation, handle duplication, memory access or token access.
Unverifiable identity means discovery unavailable, not weaker validation.
[Process rights](https://learn.microsoft.com/en-us/windows/win32/procthread/process-security-and-access-rights),
[image queries](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-queryfullprocessimagenamew).

The approved helper discovers names/attributes outside ordinary listing ACLs,
but grants no elevated content access. All media opens, reads and subtitle
operations stay under the ordinary user's identity. Its sole filesystem mutation
is explicit missing-journal preparation. Requests name only enumerated volumes,
never arbitrary devices, output paths or commands. It performs no network work.

The installer protects the binary and its native dependencies in the application
directory. Update/uninstall stops the engine and waits for its helper to exit
before replacement. There is no service registration, privileged autostart,
self-update or shutdown RPC. Uninstall never removes media, Library data or
Windows journals.

## Why this is the selected shape

The helper is a deep module: Windows metadata, journal reconciliation, paths,
aliases and resource control sit behind four discovery operations and one
explicit setup operation. The caller supplies an extension set or a finite set
of missing filenames/sizes, stages changes and accepts completed cycles. It does not
manage MFT structure, journal cursors or raw disk handles.

Filtering saves transfer and retained file memory; keeping only required ancestry
makes warm updates cheap. Complete alias sets avoid a second path-diff protocol.
Pulling batches supplies backpressure through existing requests. Retaining the
graph only while connected removes saved-index migration/recovery work. SQLite
remains the single Library authority, and the native/managed transports each
have one implementation used by both endpoints.

Physical presence determines Library eligibility; source membership cannot
resurrect a deleted movie. The engine owns the durable recovery hold, so closing
the window or restarting cannot turn a known deletion into a download. These
rules serve the same purpose as fast discovery: show files the person actually
has and respect their choice to remove them.

This is the least machinery selected to meet the behavior, not a claim of
unmeasured speed. The main cost is a metadata baseline on reconnect. The main
implementation risk is the complete MFT reader; measure and prove it before
spending effort on installation or speculative pipe optimization.

For example, use the existing envelope:

```json
{
  "request_id": 1,
  "command": "open",
  "query": {
    "volume_id": "<returned by volumes>",
    "serial": "0123456789abcdef",
    "selection": {
      "kind": "extensions",
      "extensions": [".ass", ".flac", ".mkv", ".mp3", ".srt"]
    }
  }
}
```

The sample abbreviates the classification owners' full sets. The reply contains
`data:{query_id:"..."}`. Repeated `read` requests yield `working`, `batch`, then
`complete` through ordinary `ok:true,data` replies. Accept the database cycle,
wait for the next refresh, and read again. `close` ends the query. Nothing here
changes the engine's command envelope or introduces another pipe architecture.

## Implementation order and acceptance evidence

1. Prove the metadata reader on disposable NTFS fixtures: ordinary names,
   nonmatching first name with matching hard link, alias removal, multiple
   extension records, DOS names, reused references, directory moves, same-size
   edits, concurrent mutation, journal wrap/reset and malformed records. Measure
   cold enumeration separately from candidate and sparse-ancestor metadata reads.
   Stop if it cannot
   establish complete names or acceptable startup cost; do not hide a folder walk.
2. Extract the existing common native/managed transport and adapt the engine.
   Preserve concurrency, writer fairness, sequence rejection, bounds and engine
   lifecycle; compare its existing responsiveness baseline before/after.
3. Implement the helper interface, retained graphs and budgets. Verify slow disk
   work cannot block dispatch/cancellation, uncertain replies close safely,
   memory exhaustion preserves accepted data, and no unrelated records cross IPC.
   Missing journals offer explicit preparation, deleting/unreadable journals do
   not, and an existing journal's settings stay untouched. Query/reconnect never
   creates a journal and no failure selects a folder-walking implementation.
4. Add the engine's expected-file evidence, durable hold and check-only gate.
   Verify known deletion, new/uncreated files, skipped/reselected files, missing
   resume data, stale reports/recovery IDs, failed saves and shared physical files.
   A missing/replaced volume or reused drive letter is not deletion and cannot
   redirect an automatic download onto the replacement volume. Preserve bindings
   through settled moves, including remapped or mounted-subtree locations.
   Restore, Verify, Force, Resume all, scheduling and naming completion must not
   authorize repair. Check detection with the window closed, discovery disabled
   and payload outside the media filter, plus restart with a saved hold;
   explicit repair preserves selection, and successful restoration/Verify stays
   Stopped. Validate the libtorrent flag ordering at the actual transfer seam.
   Exercise coherent external folder moves on the same and another NTFS volume,
   Quick/Full checks, mismatches, duplicate candidates, recycled files,
   surviving hard links, partial/partfile-backed payload and stale observations.
   Quick cannot release transfers after a prior check cleared complete ownership.
   Adoption changes location only, preserves intent and unrelated holds, and
   resolves only its captured missing episode after successful settlement.
   Failed verification cannot start repair. Tracking-disabled performs no
   adoption or root-ID following; repeated absence/restart never loops baselines.
   A finite engine query includes only the missing names/sizes, consumes complete
   cycles before choosing a root and releases candidate memory on every outcome.
5. Migrate the single source/database owner. Verify atomic baseline/delta
   acceptance, abandoned staging, source isolation, overlapping torrents,
   same-size stale-worker rejection, preserved choices and volume incarnations.
   Validate case-distinct locations, subtitle local scope, cloud recall guards,
   and unfinished-torrent exclusion through a differently named hard link.
   Recycling either a file or its containing folder hides the bin candidates;
   restoring the folder becomes visible incrementally, and an outside hard link
   stays eligible without exposing recycled payload or bin metadata as media.
   A torrent-only ghost disappears after the first covered baseline, a later
   deleted location disappears without another search, stale torrent replies
   cannot restore it, and saved browsing remains available while a baseline
   catches up. Engine-report failure does not block valid filesystem publication;
   durable known absence supplies retries after interruption without claiming
   successful protection before acknowledgement. Unresolved unfinished-file
   identity excludes known paths without hiding unrelated files. Large candidate
   sets keep provider/file-facts batches bounded; ambiguous `.ts` evidence never
   turns unknown properties into nonmedia or a provider no-match.
6. Integrate the two opt-ins, engine-owned activation, caller/server authorization
   and ordinary-user file actions. Verify both-off use and a remembered decline
   never request elevation; Library-only starts lazily, tracking starts with the
   engine but performs no search until needed. UAC cancellation does not cause
   repeated prompts, simultaneous callers share one launch, and slow consent
   cannot time out a pipe request. Alternate-admin
   launch and reconnect retain the original user's Library. Leaving Library or
   closing the window releases its query resources but reuses the approved process;
   engine exit/crash ends it without a shutdown command. Update active architecture, Library,
   engine, database, protocol and wire-representation contracts to describe this
   one design.
7. Measure immediate saved browsing separately from catch-up-to-current-results,
   deletion-to-row-removal, steady update
   latency, syscall/encoding/pipe/
   database/UI costs, retained bytes, private bytes and working set. Compare
   torrent-command and input latency with discovery off/on, plus headless
   known-path checking cost and deletion detection time. Use measured retained
   ancestor and matching-location counts to replace the memory estimates. Exercise
   repeated page visits and a large query followed by long idle: committed memory
   returns near the process-only baseline, workers stop, and handles are released.
   Do not accept minutes of recurring discovery or measurable interactive stalls.
   If the reader cannot meet that goal, stop downstream work and improve it before
   returning any measured retention tradeoff to the owner. Do not silently retain
   an idle graph, add persistence or adopt the deferred per-torrent collector.

Use the [testing](../agents/testing.md) and [compilation](../agents/compilation.md) policies during
implementation. This design task requires no build, elevation, disk
modification or scan of the person's media.

## Review record

Self-review narrowed the interface to extensions and source changes and kept
one transport. The first adversarial pass rejected single-name hard-link
coverage, blanket namespace rebuilds, partial authoritative writes, durable
confirmation, stale shared facts, volume-wide caption matching and ambiguous
cancellation. This revision corrects those findings and removes public
checkpoints/cold resume, which reduced both DTOs and recovery rules.

The memory review further removed all-directory retention: only matching files
and required ancestors remain, with on-demand allocation, a 128 MiB ceiling and
separate estimates for graph, working buffers and process overhead. The reviewer
agrees this preserves completeness with no new interface or transport. Its
additional ancestor lookups belong in the same first performance gate.

The owner's later tracking clarification adds finite exact-name/size selection
through the same query operations. It removes continuous torrent watch sets,
addition/completion-triggered baselines and any all-files index. Starting the
elevated helper alone allocates none of them. The engine asks only after loss,
keeps candidates temporarily, and releases query state when that job ends.

The presence review made known absence authoritative over torrent manifest
membership. The engine review found that transient disk errors and post-check
pausing do not prevent automatic repair; the design persists expected-file
evidence and a recovery hold and gates checking before transfer. Periodic engine
checks cover the window-closed case without another index. Polling latency and
the limits of prior-existence evidence remain explicit.

The initial adversarial reviewer confirmed agreement on 2026-10-16. At the
owner's request, a fresh independent reviewer then challenged the saved plan
without that review history. That pass corrected Recycle Bin eligibility,
explicit missing-journal setup, saved NTFS volume bindings, engine-lifetime
presence checks and acknowledgement only after a recovery hold actually commits.
It also kept existing non-NTFS engine targets outside the new NTFS identity
requirement, with no discovery fallback.

Both reviewers then answered the `grill-with-docs` interview independently as
the affected users and maintainers. The parent judged their advice against the
whole product and existing owner rulings. The decision tree was: useful results
and automatic work; immediate browsing and catch-up; filesystem truth versus
torrent protection; recovery evidence; then opt-in/elevation, idle memory,
engine lifetime and the shared interface.

| Question | Accepted decision and reason |
| --- | --- |
| Must every saved row wait for a new baseline? | No. Preserve immediate cached browsing, reconcile promptly and never resurrect known absence. A recurring blank Library conflicts with its established purpose. |
| Must a torrent-report failure hide the volume? | No. Publish valid filesystem observations and retry reports from saved absence. Hiding unrelated files provides no additional engine protection. |
| Should disk files bypass automatic identification? | No. Preserve the owner's automatic-work policy, with bounded existing worker batches and evidence for ambiguous suffixes. |
| Does recovery need a second index or every-startup hashing? | No. Keep expected-file evidence, known-path checks and a durable engine hold; acknowledge evidence gaps. |
| Does avoiding repeated UAC require a service or retained graph? | No. Library discovery starts lazily; optional movement tracking starts the helper with the engine but searches only after loss. The process stays alive and releases unused query memory. |
| Does the new lifetime need another API or transport? | No. A handle to the owning engine ends the helper; `Close` releases queries. Keep the five helper operations and the one shared pipe implementation. |
| Can externally moved torrent files be reused? | Yes, for a coherent complete layout. The engine requests exact missing filenames/sizes directly, checks candidates using Quick or Full, and reuses its move owner for adoption only. Library supplies absence, not candidate policy. |
| Does tracking require every filename to remain indexed? | No. A finite query discards nonmatches during metadata enumeration and releases matching records at job end. Repeated absence does not schedule repeated searches. |
| Must reuse prove that a move happened? | No. Standing opt-in permits validated existing copies, including surviving hard links. Identity lookup alone is insufficient; the same candidate/layout checks apply. Do not add a per-file identity catalogue or reject ordinary same-volume file moves. |

The earlier engine-first publication and hidden-until-baseline decisions are
superseded by this product review. The owner's later lifetime and opt-in rulings
replace service deployment. No separate ADR duplicates this plan. Final saved-text
review covered the finite-query rewrite, engine activation/settings, Quick's
intact-piece requirement and restoration of the prior destination after rejected
adoption. Both reviewers accepted those corrections and the existing-copy reuse
simplification on 2026-10-16. Metadata completeness/cost, measured memory,
alternate-admin launch/reconnect and libtorrent transfer-gate ordering remain
implementation validation gates. Design agreement is not runtime evidence.
