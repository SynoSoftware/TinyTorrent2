# Search protection

This contract covers remote calls from TMDB, Wikidata, OMDb, MDBList,
OpenSubtitles, SubDL, SubSource, IMDb dataset downloads and DB-IP city data
downloads. IMDb movie searches read local data under the
[local-data rules](search-protection/local.md).

## Read for the change

Read the shared ownership, identity, admission, pacing and persistence rules
here when changing search protection. These references carry the same authority
and cover the branches that need additional rules:

| Change | Reference |
| --- | --- |
| Force fetch, batch scopes, Retry, Reset or operation draining | [Retry and reset](search-protection/reset.md) |
| IMDb local reads, provider capability, dataset installation/deletion or local answer invalidation | [IMDb datasets and local answers](search-protection/local.md) |

Reset and operation-drain changes also need the IMDb reset ordering in the
local-data reference, because a dataset command can otherwise deadlock with
Library's execution gates.

## Decision and scope

One concrete, window-lifetime `Shared.Network.RequestPolicy` owns durable
request suppression, access waits, pacing and their reset semantics. Library
owns identification and metadata reuse. Subtitles owns matching, acquisition
and file publication. Adapters own endpoint parameters and response decoding.

A valid missing answer is remembered by the question asked, across files and
restarts. Temporary failures have shared backoff; they are never missing-data
answers. **Force fetch** requests a fresh answer despite local suppression.
**Reset search protection** clears the selected history without deleting useful
data or user decisions. Reset and requesting data are separate actions; reset
does not enqueue a search. Remote quotas remain separate.

Success has four observable conditions:

- Equivalent files reuse an answer, including an empty one, without another
  remote request.
- An outage causes one recovery attempt per applicable backoff, not one per
  movie. Unaffected access paths remain usable.
- Every local hold has a visible retry/reset path, and every timed wait can end.
- Reset cannot lose unrelated accepted work, erase successful data, or let an
  old completion restore cleared protection.

Known misses do not poll. The app's automatic-work eligibility, startup delay,
subtitle completion stages, network route and file-publication rules apply
unchanged. The five-search cap for remote searches and its shared setting stay.
Reaching the cap creates a local block; the Force/Missing/All actions provide
explicit recovery.

## Ownership and interface

| Responsibility | Owner | Reason |
| --- | --- | --- |
| Metadata workflow, matching and successful caches | Library | Remote facts still need identification and user-decision rules. |
| Subtitle workflow, matching, successful candidates and files | Subtitles | Candidates still need language, release and destination rules. |
| Suppression, access recovery, pacing and reset semantics | RequestPolicy | Every adapter needs the same protection decisions. |
| Request parameters, parsing and provider-specific quota facts | Adapter | Remote contracts differ; common policy does not. |
| HTTP connections, confirmed route, transport cancellation and reply classification | ProviderHttp, including its streaming Download path | Protection must not create a second transport or route authority, and the same reply must give the same failure and hold in every feature. |
| IMDb download/import and replaceable data | Imdb.Dataset; Library coordinates lookup drain | A dataset update must preserve the installed file until a complete replacement is ready. |
| Schema initialization and transaction execution | Database | Feature data and protection need one commit authority. |
| Construction, lifetime and cross-feature reset coordination | [Features](../../app/src/Shell/Features.cs), a host that is neither feature | Neither feature owns the other's lifetime. |

```mermaid
flowchart TD
    UI["Automatic work / Fetch / Find / Retry"] --> Feature["Library or Subtitles workflow"]
    Feature --> Cache["Feature-owned successful data"]
    Feature --> Policy["RequestPolicy"]
    Feature --> Adapter["Selected adapter"]
    Adapter --> Send["Common outbound path"]
    Send --> Policy
    Send --> Transport["ProviderHttp Send / Download"]
    Policy --> DB["Existing Database"]
```

The policy reads eligibility, records classified outcomes, reserves outbound
allowance and resets local history. Requests, intents and outcomes have names;
an empty list or a combination of `force`, `limited` and `answered` flags never
communicates refusal.

Protection integrates at the common Library lookup/resource-read path and the
common subtitle search/download path. Endpoint adapters report facts and cannot
invent their own admission checks, retry delays or resets. The outbound path
applies the policy before every call, including calls inside one adapter
operation. `Supplier.Operation` carries the account and validity check through
`Supplier.Send`; the allowance checks belong to the policy, and the validity
check runs before each send, including redirects and login. Authentication
controls which credentials are sent, not whether pacing applies to an
unauthenticated download. Library's `SaveSearch` and `SaveResponse` commit
related data together and finish after caller cancellation; protection extends
those paths and adds no parallel operation or persistence layer.

`ProviderHttp` classifies every reply on the common outbound path once, as
[answers, holds and recovery](#answers-holds-and-recovery) lists. Anything but
a success records its hold through RequestPolicy and throws a
`ReplyException`. A `ReplyException.Held` names its `ReplyFailure` and carries
that hold; `Missing`, `Expired` and `Route` hold nothing. Library, Subtitles
and the data downloads each turn it into their own failure in one switch, and
the failure's retry time is the hold's. An adapter states its
provider's facts on the `ProviderCall`: whether a 3xx is a redirect it
follows, whether a refused session is one it renews, whether a 404 names the
requested resource, and a quota that its reply reports in its own way.

Library serializes execution through `VideoLookup._access`; Subtitles uses
`Acquisition._operation`, including account checks. Each workflow keeps its
serialization because it also protects decisions and publication. IMDb reads
use Library's execution owner too, but no network policy. A shared executor,
second scheduler, generic cache callbacks, shared-task cancellation machinery,
a new project, an interface per class, a generic repository or an event bus
would duplicate that responsibility and are unnecessary.

Feature stores record reusable answers and request outcomes in one
`Database.Run` transaction. RequestPolicy owns its SQL; its internal recording
operation participates through the current connection. There is no nested
`Database.Run`, network I/O in a transaction, or database access in adapters.
The policy provides eligibility and due times; feature workers own queue order
and wakeups. Presentation reads the same policy as execution.

The shared [AccountDialog](../../app/src/Shared/AccountDialog.cs), plugin access
declarations and `Shared.Credentials` stay the owners of credentials. The dialog
edits; Library or Subtitles saves/checks; RequestPolicy owns protection. There
are no provider-specific editors and no second credential store. Library has no
account-check operation.

## Request identity and access scope

Request identity answers: **have we obtained this answer for this evidence?**
It contains the catalogue/supplier, operation, effective parameters, relevant
language and any established subtitle evidence stage that permits another try.
It is one bounded, canonical representation built from the same prepared input
as the outbound request, never parsed or normalized independently in two places.

- Library search includes effective query and language for localized providers.
  Description identity names the movie, series or individual episode resource.
- Subtitle hash search includes hash, size and language. Filename/title search
  includes the parameters that supplier actually receives, retaining release
  distinctions it uses.
- For suppliers without hashes, early and completed subtitle evidence have
  distinct suppression identities: the completion rule permits one later title
  search even with unchanged remote parameters. Equivalent copies share the
  completed-evidence answer. Earlier successful candidates remain reusable where
  they satisfy the matching rules.
- Catalogue identity distinguishes providers. OMDb and MDBList do not share
  answers merely because they describe the same IMDb movie; local IMDb data
  cannot satisfy a remote provider's missing search or resource.
- Local paths and torrent identities do not distinguish otherwise equivalent
  questions. An adapter that sends a filename includes that effective filename,
  not its directory. Account-dependent content includes its account scope.

Access scope answers: **which calls does this failure or allowance affect?**
Adapters identify the applicable service, key, account or endpoint scope and
request/download allowance. Every relevant hold applies. IMDb dataset downloads
have their own access scope, independent of local movie reads. A wait scoped to
downloads, such as OpenSubtitles' body deadlines, holds downloads only. A
search-format failure cannot be cleared by successful login.

Key-scoped allowances use the effective built-in or personal key; account-scoped
allowances use a stable remote account identity where available. Changing a
password must not silently refill a known account quota. Credential changes
invalidate authentication failure for the replaced credentials, not an unrelated
quota. Only opaque credential identities are persisted, never secrets in history
or logs. Switching back to a previously used identity restores its unexpired
holds. Other installations' consumption of a shared key is unknowable locally;
remote replies remain authoritative.

Saved-account and draft-account Check use the same admission and accounting as
other calls, with the captured effective credentials. Check is an explicit access
recheck, not a movie search: it neither consumes the per-movie cap nor creates
content-negative history, even when a supplier verifies access with a fixed title.
Its usage and remote holds persist independently of saving the draft. Cancelling
the editor discards credentials and presentation results, not observed remote
limits. A successful check clears only the access failure it actually disproves.

A displayed item maps to its current structured request identities at the feature
owner. Scoped retry uses those identities; Settings reset uses their catalogue or
supplier. History is never selected by substring matching opaque keys. Library's
provider chooser stays: each actionable provider choice uses its own protection
state, never another provider's block or an aggregate force state on the parent.

## Answers, holds and recovery

These are different facts, not one catch-all status enum. A feature may have
good saved information while a fresh request is waiting or has failed.

| Fact | Ordinary behavior | What ends it |
| --- | --- | --- |
| Valid answer | Reuse feature-owned data, including absent optional fields. | Explicit refresh or the adapter's applicable freshness rule. |
| Valid empty answer / missing resource | Remember the exact request in RequestPolicy; no polling. | A fresh user request, reset or newly eligible evidence. |
| No acceptable match | Keep candidates and the feature's matching decision; do not label the remote data absent. | Changed evidence/options or user selection/fresh search. |
| Temporary transport/server failure | Shared access backoff; pending work waits. | A relevant due attempt succeeds, or the user requests an early local probe. |
| Remote quota/rate wait | Hold only the affected calls until their deadline. | Deadline and subsequent remote response. Local reset cannot replenish it. |
| Access needs attention | Stop the affected path without polling. | Correct setup, supported verification, or an explicit recheck. |
| Unusable individual record | Hold that request, preserving good data. | Retry/reset or a targeted correction when its adapter is fixed. |
| Malformed protocol/page format | Back off the affected operation, preventing every movie from failing separately. | A relevant successful retry or adapter correction. |
| Cancellation/local ineligibility | No missing answer. | Later work becomes eligible. |

Only a validated remote response establishes missing data. HTTP 200 is
insufficient; errors and challenges inside its body are classified, and an
error inside a successful HTTP response holds the provider instead of creating a
failed record for every movie. A 404/410 is a missing item only when the adapter
knows it identifies the requested resource rather than a broken endpoint.
Unsupported input and local filtering never create a remote miss.

`ProviderHttp` reads every reply to Library, Subtitles and the IMDb and city
data downloads with the same rules:

| Reply | Failure | Hold |
| --- | --- | --- |
| 2xx, or 3xx when the call follows redirects itself | None | None, unless it also reports a used-up allowance |
| 429, `X-RateLimit-Remaining: 0` or a quota the adapter reads, such as a subtitle supplier's 402 | Wait | Remote wait on the allowance scope, or on every operation of the access; an adapter's quota names its own scope |
| 401 or 403 to a request with an authentication scope, which every Library request and supplier API request has | Refused, or Expired for a session the caller renews | Access needs attention on the authentication scope; Expired holds nothing |
| 404 or 410 when the call names one resource | Missing | None |
| Any other status, a transport failure or a timeout | Unavailable | Backoff on the operation |
| A reply over its size limit, or JSON that Library cannot parse | Invalid | Backoff on the operation |
| A proxy or adapter that cannot carry the request | Route | None |

A wait that the status or headers report is recorded even when the body cannot
be read. Retry-After, or else `X-RateLimit-Reset` in Unix seconds or as
a date, gives the wait or backoff its requested time.

The last valid answer is stored separately from later failed attempts. A timeout
during Force fetch must neither replace good metadata nor erase a remembered miss
and reopen automatic searching. An explicit refresh reports its failure even
when older information is displayed. A cache hit cannot clear an access failure,
and success on one operation cannot clear an unrelated hold.

Only RequestPolicy owns remote-negative history. Feature caches keep no second
independent missing flag. Matching decisions and local file failures remain
feature facts. A missing subtitle download reference affects that reference, not
the movie's entire subtitle search.

## Execution and request pacing

Within its serialized workflow, the feature:

1. Revalidates current membership/settings, then rereads cache and protection.
2. Reuses an answer or reports the hold. Fresh intent applies to the requested
   search/resource; unchanged successful dependencies remain reusable.
3. Invokes the adapter. Before every outbound call, the common send path checks
   operation validity and atomically reserves applicable pacing allowance.
4. Records any remote wait/failure before another call can start, including the
   next page. Known remote holds are retained for the captured access identity
   even if the requesting file/context has since been cancelled.
5. Commits reusable data and its classified request outcome. It applies a match
   or publishes a file only after the existing current-target check.
6. Releases execution so later duplicates read the committed answer or hold.

No second request runs concurrently through the same feature owner. Repeated
submissions of an already-pending user command are not queued. A cancelled
operation releases execution; a later caller can retry if no answer was
received. There is no shared result task whose cancellation another caller owns.

A long wait returns a reason and due time to the worker; it does not retain an
execution gate while idle. Queues skip held work when another eligible access
path can run. Explicit work gets priority between operations through feature
scheduling. On wake, the policy is rechecked: a remembered due time is not
permission.

Every outbound call is paced: search pages, detail reads, login, allowed
redirects, download-link requests and file downloads. Quota charges apply only to
endpoints covered by that supplier's allowance; a CDN read is not automatically
another API charge. A reservation is kept once a send may have begun, including
cancellation or timeout. An uncertain allowance-spending operation is not
replayed blindly; it reuses its acquired reference or follows the supplier's
supported recovery.

Pacing uses the serialized execution, adapter-declared rate windows, and a fixed
local minimum of one second between starts per access scope. This is a
conservative policy, not a measured optimum or a supplier rate claim. The
bounded usage that long quota windows need persists, including SubSource's
declared windows. In-session pacing uses monotonic elapsed time; saved deadlines
use UTC.

Temporary failures use a 30-second exponential backoff with small jitter and a
maximum of one hour including jitter. The streak and due time persist at access
scope. The next eligible real request is the probe; there is no extra
health-check call. Its outcome is recorded before another job is admitted.
Pending work recovers automatically; known misses create no timer.

A relevant remote deadline takes precedence over an earlier local retry. Both
Retry-After forms and provider-specific quota facts are parsed, and wait headers
are recorded even when the response body is unreadable. HTTP 429 does not
prescribe the service's quota scope; see [HTTP 429](https://www.rfc-editor.org/rfc/rfc6585.html#section-4)
and [Retry-After](https://www.rfc-editor.org/rfc/rfc9110.html#name-retry-after).
The policy caps a newly received deadline at 24 hours from receipt, so an
excessive remote value cannot prevent requests indefinitely. The cap applies to
newly recorded deadlines; it does not rewrite existing stored holds. Without a
usable deadline, the supplier's documented reset rule applies, or a one-hour
fallback followed by one probe. Missing headers never create permanent waits.
These policy constants are not user settings.

The streaming download path, which the IMDb and city data downloads use, has
the same outbound admission and reply classification, and records status and
wait headers before reading the body or starting the next transfer. A remote
wait refuses the next Download until it ends, and the download's failure names
its time. Remote transfer failures are classified there; local file writes,
import failures and dataset SQLite errors remain Dataset recovery and create no
remote service backoff. The path keeps its idle timeout and streaming writes: it
does not buffer a large dataset or impose the JSON path's whole-request timeout.
A dataset 404 is a download failure, never evidence that a particular movie is
absent.

## Persistence and failure boundaries

Protection uses the existing Database, schema authority and transaction path,
as the [database contract](database.md) records. `Database/Schema.cs` defines
storage, `Database.Run` executes transactions, and `Database/Files.cs` owns
shared membership reconciliation, cleanup and snapshot invalidation. Protection
keeps only:

- Current request suppression and its checked time; no append-only attempt log.
- Access holds, failure streaks and deadlines with their actual scopes.
- Bounded pacing usage required by declared windows.
- Existing cap counts.
- Completion times on reusable answers, so one accepted refresh is shared by
  equivalent batch items and survives a queued subtitle job reopening.

Current source membership stays temporary engine-derived facts; protection
history never creates membership or accepted work. Protection is independent of
torrent withdrawal, so removing and re-adding a movie does not refill its remote
allowance. Reusable replies are saved after cancellation but apply to files only
through the current-membership/decision check. Reset invalidates affected
presentation snapshots in its commit and keeps the established rollback/reopen
and fail-closed recovery paths. Protection adds no second connection, schema
initializer, in-memory membership catalogue or reset store. IMDb's separately
owned replaceable file is the documented exception, not another owner of
`library.db`.

Successful remote-answer caches stay with their feature. Complete search
identity is recorded alongside candidates: Library's learned choices alone cannot
prove that a specific query was answered. Subtitle caches retain stable
references and matching facts, not expired download URLs or subtitle payloads.
Provider freshness/retention rules apply; history cleanup never silently renews
traffic.

Completed intermediate resource reads are saved, so a missing episode does not
discard an already-read series description. A partially completed search is
never an authoritative empty answer or complete candidate set, and contradictory
later pages cannot replace a complete cached answer. Partial pages are kept only
where the adapter/cache representation supports them; there is no generic
pagination-resume framework.

Actual remote replies can outlive the requesting context: their reusable answer
and access facts persist for the captured identity, but an obsolete file decision
is never applied. Reset drains those writes before its clearing transaction.
Cancellation before a valid answer creates no miss. A crash after a remote reply
but before local commit can cause a repeat; HTTP and SQLite cannot commit
atomically, and uncertain requests never become permanent negative answers.

If protection cannot be read or saved, new remote work pauses and existing
storage recovery applies. The app never silently runs unprotected. Local features
remain usable where their data is readable. A confirmed route change cancels
obsolete traffic and releases local transient backoff, preserving content history
and remote account holds. Shutdown, database recovery and reset use the app's
existing lifetime authority.

## The existing search cap

The [shared search limit](architecture.md#third-party-provider-access) keeps its
default of five and its scope and counting semantics: Library catalogue/text;
subtitle supplier/title/year/season/episodes/language; completed workflows count,
including misses. Reaching the cap marks the request locally blocked, so the
individual command becomes Force fetch. That command and the batch Missing and
All scopes authorize one fresh attempt despite the cap; New and automatic work
respect it. Subtitle counting does not charge each hash/title sub-search.

RequestPolicy owns the cap and features consult it after cache reuse. Charges
are recorded with completed remote lookup outcomes; cache hits, rejected
admission and failed network attempts do not count. The cap supplements request
reuse and access recovery; it is not a second implementation of either, and no
Library-to-Subtitles setting callback exists.

## Review checks

Policy decisions alone cannot prove serialization or publication, so checks
exercise feature execution as well. These failures are the review criteria for
changes to this contract:

- Duplicate files and reopen repeat an empty lookup; changed language/resource
  or completed subtitle evidence is wrongly suppressed.
- Filtered candidates, malformed/partial responses or cancellation become a
  false miss; a failed retry erases good data or the previous valid miss.
- An outage retries once per queued movie; successful login clears an unrelated
  search/download hold; quota switching/reopen or discarding a checked draft
  loses a known deadline or changes the saved account.
- Reset during a delayed reply restores old suppression, drops unrelated queued
  work or publishes to a removed file; resetting history starts a new search.
- Force fetch bypasses a newly reported failure, or internal pagination/login/
  download calls bypass pacing; ordinary Fetch silently overrides a hold or a
  retry applies to a different item/query than its feedback named.
- Batch counts exclude blocked items from Missing/All, count languages as movies,
  or force the same shared request repeatedly; subtitle refresh overwrites a file
  or loses the distinction between a likely and an exact match.
- A queued subtitle refresh loses its authorization on reopen, an early probe
  admits unrelated work, or Library All skips already identified entries.
- Nonempty candidates yield no acceptable match, then reset and ordinary Fetch
  reuse that same completed search instead of sending one fresh query; retained
  candidate facts and subtitle files must survive this reset.
- Local IMDb empty/ambiguous answers or cap history stop a new dataset from
  resolving an unfinished movie; installation loses queued Library work. Local
  reads send HTTP or incur remote-policy charges. Dataset download waits make
  installed-data reads unusable, or reset/cancellation loses the installed file or
  restarts a download.
- IMDb damage reaches Library's incompatible-storage recovery and offers to
  delete user decisions; that failure stays classified at its dataset owner.

The smallest affected WinUI journey is reviewed for reset discovery,
pending/failure feedback, keyboard access and English/Spanish text.
