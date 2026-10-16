# Library and Subtitles database

`Shared.Storage.Database` owns `library.db` in the engine-selected data directory.
It is a service in the product window process, with one connection and one
serialized transaction path. Library and Subtitles share this database. SQLite
TEMP tables are connection-local working state, not a second saved database.
The engine remains the authority for torrents and their files and never opens
this database.

## Ownership and initialization

`Shared/Storage/Database/Schema.cs` is the authoritative initial schema, including
tables, indexes, views, defaults and temporary working tables. `Database.Open`
registers path comparison, enables foreign keys, reads `PRAGMA user_version`,
and initializes the schema transactionally before exposing the connection to
any feature. A new database receives the current schema revision in the same
transaction as its schema. Version 0 initializes only an empty schema; existing unversioned storage
is incompatible. The temporary projection is recreated for every connection.
Unsupported versions fail without changing their data.

**Owner ruling: the schema at revision 9 is the baseline that saved databases
hold. Never change it destructively.** A schema change that only edits the
schema text and raises the version makes every saved Library database
incompatible, and the person loses saved metadata, choices and subtitle
records. Every change after revision 9 is instead a sequential migration from
the schema as it stands, which upgrades a saved database and preserves its
data. Database owns their execution and `PRAGMA user_version`; domain owners
define their required schema changes. Migrations run transactionally during
this same initialization path, before Library or Subtitles can access the
database. There is no second upgrade mechanism in a domain or in startup wiring.

Schema revision is independent of the application version. Development
databases before revision 9 remain intact until the owner resets them; that
reset loses saved metadata and choices.

Library owns identification, provider reuse, file facts and search queries.
Subtitles owns accepted work, supplier policy and subtitle-file operations.
Their SQL executes through `Database.Run`; network requests and file reads run
outside it. `Database/Files.cs` owns the shared reconciliation, cleanup and
search invalidation SQL so membership changes commit consistently across both
domains. `FileSources` applies source facts through that transaction boundary;
`TorrentSource` obtains those facts from coherent engine replies. Application
wiring supplies these owners, never schema fragments.

Library's Store saves an answered search and its reusable candidates in one
transaction. Each downloaded resource and its request outcome commit together
before a description reads its next resource. Completing a description saves
its choices, and applying a match saves its resource references; neither writes
the responses again, because reuse must not make old data count as freshly
downloaded. Responses and choices survive a rejected match. Once the provider
has answered, these saves finish even if the requesting context is cancelled;
cancellation still prevents that obsolete context from applying a match.

One connection needs no concurrent-reader WAL setup, pooled connections, ORM or
generic repository. Each short operation commits or rolls back as a whole.
The managed SQLite command timeout bounds retries for external locks to five
seconds. No performance claims are implied without measurement.

## Durable state

| Tables | Reason to persist and lifetime |
| --- | --- |
| `contributions`, `source_files`, `locations` | Last accepted file lists and one identity per shared location let Library reopen without collecting every torrent again. A contribution records its accepted name and stamp, including a successfully empty list. Confirmed withdrawal prunes it. |
| `source_state`, `known_torrents` | Distinguish first connection from a genuinely empty collection and remember which additions are already known for automatic subtitle eligibility. Confirmed withdrawal removes an origin. They contain no saved file catalogue. |
| `identifications` | Accepted provider matches and explicit user decisions, including an explicit clear. They belong to source-file identity, survive moves, and end with confirmed withdrawal. Ordinary unsuccessful attempts are not decisions. |
| `video_information`, `video_references` | Parsed metadata used by current identifications and links to its provider responses. Unreferenced metadata is pruned; deleting it cascades its response links. |
| `title_facts` | Ratings per site, directors, age rating and box office, shared by every provider's metadata that names the same IMDb ID. A row no metadata names is pruned. |
| `video_records`, `video_choices` | Reusable provider responses with retrieval time and saved candidates for later searches. They outlive a file's membership under the saved-provider-data ruling; ordinary cleanup does not discard responses or learned candidates and spend the allowance again. Provider freshness policy remains with the provider. |
| `request_settings`, `request_counts`, `request_blocks` | The shared search cap, completed search charges and classified local holds. RequestPolicy owns their meaning; restarts and torrent removal retain them. |
| `request_holds`, `request_usage` | Access holds, failure streaks and outbound reservations. Reset clears local backoff while preserving remote waits and pacing. |
| `video_searches`, `subtitle_searches` | Feature-owned completed query answers and completion times, separate from local matching. A fresh batch reuses answers completed after its acceptance. Reset invalidates rejected or empty query reuse while retaining successful facts and candidates. |
| `file_facts` | Successful reads of file dates and media properties, including a successful empty result, so the window need not repeat filesystem reads. Shared contributors inherit them; confirmed withdrawal removes them. Failed reads remain session work. |
| `library_settings`, `subtitle_settings` | Typed singleton settings. Subtitle credentials are protected for the Windows user. The saved automatic extras count remains part of the subtitle contract. Each domain validates readable saved settings; RequestPolicy owns allowance deadlines. |
| `provider_keys` | The person's own API key for a Library provider, protected for the Windows user. It replaces the key built into the release until the person removes it. A key that cannot be decrypted is removed, so the provider uses its built-in key again. |
| `subtitle_outputs` | Actual subtitle path, language, created/found ownership and supplier reference. These facts distinguish files the app created from files it merely found. |
| `subtitle_output_sources` | Associations of actual subtitle outputs with source files and the movie location they served. A failed move retains old ownership without claiming a subtitle at the new location. Deleting an output cascades its associations. |
| `subtitle_jobs` | Accepted work, evidence stage, retry, outcome and match history for an output/language. Cancellation stops work without erasing its history. Jobs and outputs are removed when no accepted source remains. |
| `subtitle_job_sources` | Accepted jobs' source files, independent of output ownership. Jobs and search history follow the movie even when a caption is missing, found-file discovery replaces its associations, or a created caption cannot move. Deleting a job cascades its associations. |
| `subtitle_scans` | Completed one-time discovery stamps per source file and path. Explicit rescan bypasses these; ordinary refresh does not repeatedly walk folders. |

File identity uses the engine's addition identity and file index. Paths coalesce
current shared locations but do not resurrect removed identities. Membership
cleanup retains associations while a present origin's file list is pending;
absence from an incomplete read is not withdrawal. Explicit identifications,
successful file facts, subtitle ownership and accepted jobs are shared with
overlapping contributors before a departing contributor is pruned. A path alone
never recreates an association after its last source leaves.

## Reconstructed state

| Temporary tables and views | Reason to exist |
| --- | --- |
| `file_reads` | Current engine summary, file-list confirmation and scheduling. A reconnect clears confirmation; a command only schedules every list for another read while confirmed lists stay in use. Process-relative retry times never survive the connection. |
| `present`, `received` | Transaction scratch sets for replacing complete origin and file lists without treating partial reads as deletion. |
| `new_origins` | Current-session subtitle eligibility, derived while reconciling durable known-origin history. |
| `collected_files`, `pending_origins`, `current_files` | Shared membership and file-read readiness rules, including path settlement. |
| `video_attempts`, `fact_attempts` | In-flight work and session failures/misses. A blocked video attempt names its required request; batch projection joins the current RequestPolicy hold instead of copying it. Reopening clears attempts; durable remote history belongs to RequestPolicy. Withdrawn locations are pruned. |
| `subtitle_languages` | Languages actually associated with a movie's current location, derived from ownership and current source facts. |
| `library_entries`, `library_rows`, `library_state`, `library_matches` | Joined display/search data, a rebuildable row snapshot, its validity and the current match set. Actual changes invalidate it in the same transaction as their writes; confirming an unchanged list does not. |

The former TEMP-only catalogue structurally excluded rows not supplied by the
current connection. Retaining last-known lists deliberately replaces that
guarantee: `collected_files` admits only current confirmation, `pending_origins`
protects unconfirmed associations during cleanup, and `current_files.readable`
requires current confirmation, completion and settled paths. Choices and
ownership shared across a location join only confirmed files, because a cached
file can still name a path its torrent has left. Display completion and
subtitle languages use cached files; they do not authorize file work.

Media bytes, transfer progress history, selection, scroll position, network
clients and active cancellation tokens do not belong in this database. Search
validity is explicit state; no reader changes behavior because a table happens
to exist. No feature creates or alters production tables during a query.

## Lifecycle and recovery

Startup constructs one Database and attaches its domain owners. The first use
initializes storage, then coherent source reconciliation establishes membership
before feature work becomes actionable. The first source list is a baseline,
even if empty, so a new database does not automatically search an old collection.
Subsequent unfinished additions remain eligible across a window restart.

Disconnect pauses engine-dependent work while cached search, inspection and local
Open remain available. Reconnection reconciles the complete summary before
publishing surviving cached rows. Successful reads replace one entire torrent
list per transaction; failed reads retain its cache without confirming it.
Unchanged confirmation only updates TEMP state and does not invalidate search.
Confirmed source removal and domain cleanup are one
transaction, whether enrichment and automatic subtitles are enabled or not.

Closing, deletion and connection replacement first stop and drain source,
Library and subtitle workers. Queued callbacks check their originating owner.
Window close also drains accepted settings saves before disposing those owners;
a refused close resumes feature synchronization.
The old Database is permanently disposed before a replacement is attached, so
late work cannot reopen the replacement file. Read-only recovery follows this
same path because reopening a connection discards every TEMP table. A failed
rollback also requires coordinated reopening; the unusable connection rejects
further operations and the original failure remains visible. A normal transient
failure retries through the existing owner; it does not secretly replace the
connection or clear saved decisions.

Incompatible or damaged storage remains untouched until the person confirms
deletion through the existing UI. Deletion failure remains visible and retryable.
Successful deletion creates fresh owners through normal startup. Rebuilding a
search snapshot never requires deleting durable data.

Filesystem operations and SQLite cannot share one transaction. Subtitle saving
and moves retain the existing [best-effort file contract](subtitles.md#saving-and-following-the-movie):
a crash or database failure after a file operation can leave a file for later
discovery. Discovery does not guess created ownership or delete a found file.
Post-publication bookkeeping applies the same membership cleanup as source
reconciliation, so it cannot restore associations already withdrawn by the
engine's source list.
