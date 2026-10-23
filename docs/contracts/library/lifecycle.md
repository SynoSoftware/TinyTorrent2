# Library lifecycle

Part of the [Library contract](../library.md). Read its shared ownership,
membership and SQLite rules before the sections here that the change affects.

## Performance and implementation constraints

**Owner ruling: Library is ready when the person opens it.** The page never
shows a loading or preparing state, because a person who opens Library came to
find a file now. Initialization uses existing torrent facts and local SQLite
reads, never a scan of download folders. C# ownership alone does not prove it fast. If
building the data takes long or needs minutes of scanning, the feature
is badly designed or badly implemented. That is a defect to fix, not a state to
show the person.

Fast file finding is the feature's primary requirement. Moving slow work to a
background thread is insufficient if it still competes for the same CPU, disk,
database connection or command queue. Keep discovery, enrichment, search and
presentation costs separate enough that the optional work cannot hold up the
user's next action. This does not call for a new scheduling framework.

Keep blocking database and network work off the engine message loop and the
UI dispatcher, following existing completion and shutdown ownership. Bound
background requests and pending reads so rapid typing and enrichment cannot
build an unbounded work queue. Source facts and cached video information are
the inputs; total disk capacity and unrelated folders do not affect the work.

### Work lifecycle

- Engine startup restores torrent work only. It does not open SQLite, construct
  Library entries or start provider work, whether or not a window will open.
- During normal window initialization, the C# torrent adapter reads the complete
  accepted-torrent set and needed file facts through the existing pipe. It
  reconciles current contributions, including those with no finished files,
  before deleting stale facts or admitting enrichment. A disconnect or partial
  read is never evidence that missing contributions were removed.
- Reconcile the initial summary before querying the retained catalogue. Closing
  the window normally leaves the engine running, so reopening uses its existing
  summary. A cold engine start already holds the window in its engine-loading
  state; using the same path avoids briefly showing withdrawn origins.
- Publish cached rows once, then each committed change as its read lands;
  Browser paces those refreshes. Confirming an identical list updates only
  temporary confirmation and publishes nothing. Without a cache, files appear
  as each torrent is read. Read stored facts in sets, not once per row. No
  payload read or provider request is a prerequisite for filename search.
- Publish search results before the local IMDb rating lookup. A row keeps the
  rating its unchanged entry already received, so a later search neither blanks
  nor re-sorts it; only rows whose rating differs change, and the table settles
  its sort once for that batch under TableView's existing pointed-row hold.
- C# synchronization compares source snapshots and updates affected torrents and
  files through existing coherent reads; do not ask for every torrent's file
  progress on each summary tick. Source retrieval stays bounded and allows
  commands through. The existing pipe reads one torrent's files at a time and
  supplies no file-list revision. Aggregate progress does not identify every
  file change; synchronization must cover those changes.
- SQLite has one C# connection owner and short serialized database jobs, shared
  by Library and subtitles. Searches retain only the active and latest pending
  query; writes and source updates must still make progress during rapid typing.
  Membership, decision changes and reads use that same ordered path; workers
  return proposed facts for acceptance there. Network requests and Windows
  property reads never occupy that worker. Applying a worker's result rechecks
  the contribution, source entry, filename evidence and latest decision.
  Withdrawal is ordered after already accepted writes and rejects later stale
  results. A pending edit is not reported as saved before it commits. Workers
  never mutate live entries; only presentation rows cross to the dispatcher.
- Report a shared source failure once per failure episode, through the app's
  error surface. Suppression clears only after the source is ready again, so
  ordinary snapshot retries do not repeat the announcement. The existing
  source gate suppresses failures from obsolete or disposed work. Library
  keeps its own property and provider failures and shows unavailable while the
  shared source cannot serve either feature.
- Hidden Library pages cancel queries/detail reads and stop rendering and
  sorting. Source synchronization and automatic downloads may continue while the
  window remains open. Keep only current row/navigation state, no query history.
- Closing the window cancels provider/property work, finishes short accepted
  durable edits through the normal close guard, and releases the database,
  projection and rows. Do not keep a hidden process or wait for network timeout.
  The next window reconstructs current membership before resuming saved work.
- With no source changes or pending work, Library has no periodic folder checks
  or cache refresh loop. Provider-required expiry is handled off the query path.

Use a managed SQLite binding in the existing C# application. SQLite's native
runtime dependency belongs to that process alone. The engine project removes
its SQLite compile/link references when its old Library implementation is
removed; installed dependencies in `3rdParty/` remain untouched. This work
neither adds a managed runtime to the engine nor shares a database across
processes.

One connection, schema and transaction owner serves Library and subtitles because
both use this C# database. Keep their domain decisions with their respective
owners. No ORM, generic repository, second index service, pooled connection per
query or automatic WAL requirement is needed. Add derived indexes only for
measured queries; a trigram index must still preserve short-query behavior.
Manual corrections and subtitle outcomes survive projection rebuilding. Never
delete the database to repair a disposable search index.

### Cost and user benefit

The following work is retained because it serves an agreed user task. Its cost
must be paid at the indicated time, rather than repeatedly while typing.

| Work | Cost and why it is needed | Limit on that cost |
| --- | --- | --- |
| Keep file entries | Saved accepted file lists and temporary confirmation; required for immediate saved-list browsing and safe file work. | Reconcile the summary before publication, then confirm or replace lists in the background, with no folder access or per-keystroke engine enumeration. |
| SQLite cache and identification writes | C# dependency size and local disk I/O; prevents repeated downloads and preserves corrections. | Read in sets, save coherent changes, reconcile removal when connected, and keep maintenance off the first-table path. |
| Early provider enrichment | Network traffic, parsing and small database writes; enables genre, actor and subject search before download completion. | Enabled explicitly, one request in flight initially, cached records reused, shared requests deduplicated, no unused-series harvesting. |
| Cast, synopsis and keyword search | More text to store and match; directly required by actor and subject searches. | Keep only useful fields, normalize on change and match distinct shared records once per query rather than once per file. |
| One- and two-character matching | Can require examining active text because a trigram index cannot answer every short query; necessary for results from the first character. | Examine current entries and referenced titles only and cancel obsolete queries. |
| Combining shared file locations | Path comparison and a membership lookup; prevents duplicate rows for cross-seeded files. | Reconcile when a contribution changes using existing path rules, without file hashing or pairwise comparisons of every file on each query. |
| Complete results and sorting | Summary serialization, row state and ordering; users must find every matching file and use the existing table. | Compact summaries, selected details only, existing virtualization and one sorting owner; optimize transport only when measured. |
| Details card | One local detail read and visible text layout; lets users choose and open the right file. | Fetch only the current selection, discard obsolete reads, and do not instantiate details for hidden rows. |
| Pluggable data-source interface | A small integration seam; explicitly requested to support future origins of files. | One torrent adapter, no runtime plugin loader, extra process or source polling framework. |

Continuous Library per-file progress, proactive disk validation, artwork,
unreferenced provider refreshes and speculative search features do not earn
their recurring cost. They are excluded rather than merely assigned a lower
background priority. On-demand Windows opening can still wait on the target
drive or associated application; that latency is not part of local search and
must not occupy the database worker or UI dispatcher.

### Measurement gates — cancelled

**Owner ruling, 2026-10-09:** the numerical search, first-open, memory and
startup gates, both large-collection benchmarks, baseline comparisons,
profiling and transfer measurements are **cancelled for this delivery**.
They were delaying the feature. Existing failed measurements remain failed;
no numerical performance claim is made.

Keep the implementation constraints above: complete results, bounded work,
local search independent of enrichment, and no resident feature work after
window closure. Cancellation does not require a TableView redesign, a saved
stale file catalogue or new engine communication.

## Verification

Follow [testing](../../agents/testing.md).
Comprehensive checklists and agent interaction runs are **cancelled**. Reuse
existing valid evidence and review the settled change once. A new check must
address a concrete failure introduced or exposed by that change, especially
data loss, stale writes or incorrect file operations; it is not a coverage
exercise. The approved prototype remains the presentation reference.
