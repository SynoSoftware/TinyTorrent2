# Retry and reset

Part of the [search protection contract](../search-protection.md). Read its
shared ownership, identity, admission, pacing and persistence rules first.

Reset and operation-drain changes also follow the [IMDb reset ordering](local.md#imdb-datasets-and-local-answers),
because a dataset command must drain before Library's execution gates are taken.

**Owner ruling: an individual item has one Fetch entry, labelled Force fetch
when locally blocked. Reset all belongs only in Settings, never a context menu.**
The label exposes the override before the click; there are not two competing
commands, and a failed Fetch followed by another confirmation is not required.
The command captures the item, provider and query. Changed input uses ordinary
admission, and a newly discovered block is explained rather than silently
overridden.

The subtitle choice dialog labels its retry **Search again**, so the action
describes the person's task. It keeps the same scoped override of local
protection and explains the current block; supplier waits are never overridden.

Search feedback covers that captured query. Selecting a candidate identifies
its detail or subtitle-download resource; a hold on that resource is shown at
the selection action, which offers its own scoped Force fetch. A different
candidate never inherits permission to override the previous candidate's hold.

The existing [feedback placement](../ui/feedback.md#feedback-placement) owner shows
why an item is blocked. A remembered miss is informational, not an error;
a temporary failure names the cause and retry time. The same short reason is
available with the command through its description and accessibility text.
Force fetch permits one fresh attempt for that item, not a provider-wide reset.
A provider-imposed wait shows its deadline and has no force override; credentials
that need correction offer Settings. Repeated submission is disabled while pending.

A subtitle batch uses a scope selector and one Find button. Library's
batch instead groups videos by the provider's last answer, as described below.
Both reuse the native interaction pattern, not a new generic batch framework.
The three subtitle scopes have nested counts, for example a selection with one
request per item and 10 eligible, 5 locally blocked and 10 already satisfied
items:

| Scope | Example count | Requested work |
| --- | --- | --- |
| New | 10 | Missing work without local suppression; remote waits still apply. |
| Missing data | 15 | New plus locally blocked missing work; explicitly retry the blocked requests once. |
| All selected | 25 | Missing work plus a fresh lookup for already satisfied items. |

New describes missing work without a local block, not newly added torrents.
Missing data explicitly includes blocked items. All selected includes refresh
of successful data. This behavior is explained beside the selector; there is no
reset step or confirmation. Feature owners first classify requests, then count
each selected movie with at least one request in that scope. The counts are
nested, but differences are not always counts of blocked movies: a movie can have
both an eligible language and a blocked language. New executes only eligible
languages; Missing also executes its blocked unresolved languages; All includes
satisfied languages. For EN exact plus ES blocked, Missing counts one movie and
requests only ES. Pending duplicate work is not accepted again.

Remote waits and setup failures do not change those scope memberships; the batch
shows why included work cannot run yet instead of silently dropping it from the
count. Unsupported inputs and explicitly cleared Library entries are excluded
under their existing rules. Counts and execution derive from this one feature
classification, are rechecked at execution, and report changed eligibility or
waits. Equivalent requests are grouped so each is refreshed once per batch.
Saved data stays if refresh fails.

Library also projects resource holds discovered for the current file evidence
in this session. The attempt records the required question and joins its live
RequestPolicy hold, so another successful request releases it immediately.
After reopening, dependencies such as episode resources are discovered again
during resolution; initial counts cannot include an undiscovered dependency,
but persisted protection still refuses its HTTP request at execution.

Library defines missing through its identification/data rules, not whether every
optional field has a value. Subtitles defines the corresponding unresolved work
through its language and match rules. Its **No exact match** includes likely
subtitles already downloaded; missing is not equated with no `.srt` file. The
middle subtitle scope includes new and blocked unresolved searches while keeping
its feature-specific wording. A movie counts once even when several wanted
languages need work. The separate Exact match only / Best match / alternatives
choice stays. All repeats supplier searches; it does not authorize overwriting
subtitle files, re-downloading the same reference or replacing user choices. The
existing publication and ownership rules still decide what can be saved.

For Library All, the already accepted identification is refreshed through its
provider; it is not rematched and an explicit choice is not replaced. A
different match is never applied silently. Library's batch groups videos by the
chosen provider's last answer: Not checked yet uses New's protection, Not found
last time uses Missing's, and Already checked uses All's. For a video another
provider identified, the answer adds to the same IMDb title and never replaces
the identification.

Subtitle batch admission is scope-aware: New respects local protection and the
cap; Missing and All explicitly include and retry locally blocked work. Capped
requests are not excluded from every scope.

Settings > Plugins owns **Reset search protection**. Reset all clears all local
blocks and the associated cap/backoff history across Library and Subtitles; a
provider-specific reset applies the same operation to that provider. Reset starts
no search and deletes no movie information, subtitles or user decisions.

| Action | Changes | Preserves |
| --- | --- | --- |
| Force fetch; blocked work included by Missing or All | One fresh invocation bypasses the relevant local request hold and existing cap; it may make one early probe through a pre-existing local access backoff. The shared hold stays recorded until the probe's result updates it. | Good data, last valid answer if retry fails, unrelated history, remote holds and pacing. |
| Reset search protection (provider/supplier or all, in Settings) | Transactionally clear the scope's local request suppression, transient backoff and retained cap counts; invalidate dependent attempts and completed-search reuse for blocked unresolved requests. Do not enqueue a search or grant fresh intent to later work. | Saved candidates and metadata, user decisions, files, credentials, unrelated history, access-needs-attention state, remote holds and pacing usage. |

Fresh intent belongs to the captured request, never a global or permanent force
mode. It persists with already-durable accepted subtitle jobs, so reopening
before execution does not lose the person's Missing/All request. Library keeps
its session-only batch lifetime. The intent ends when that requested lookup
answers, fails or is stopped; waiting for admission does not spend it. It lives
within existing accepted-work storage, not another queue.

An early probe admits only the authorized logical lookup while the shared
backoff stays recorded, including the pages and authentication calls needed to
complete that lookup. The old hold cannot block its own continuation. At most
one such probe is allowed per access scope for a command, not once per movie.
Later failures/waits apply immediately, including between pages or login and
search, and supersede any older queued override. A stopped or failed fresh
attempt cannot pass its override to ordinary retries. A new Force command
supersedes an older failed access recheck. Actual success or failure updates the
shared hold; force preserves its failure streak so another failure does not
restart at the shortest delay.

Equivalent files share protection, so resetting one request can make others
eligible. Reset does not invoke Fetch/Find or start a new batch. Existing
automatic settings and already accepted jobs keep their normal scheduling, so
accepted work can resume after its local hold is cleared. Reset never enables a
disabled feature, creates subtitle jobs for old torrents or searches every
installed provider. Explicit clears and satisfied jobs remain intact; only
suppressed unfinished work becomes eligible.

The reset hint in Settings reads: “Allow previously skipped searches again.
Keeps movie information and subtitles. Does not add searches. Previously queued
work may resume.” Enabled automatic work still follows its normal scheduling.
Remote waits show their retry time; a force request during one reports that wait
without sending. Setup failures offer settings or a supported recheck. Repeat
reset submission is disabled while pending, failures are reported, and
eligibility refreshes after commit. Text uses the existing English/Spanish
catalogues and keyboard paths; there is no per-file flood of toasts and no new
diagnostics page.

Reset's coordination:

1. Features serializes resets with feature lifetime changes and pauses
   admission at the affected feature owners.
2. Active operations are cancelled and drained, including their outcome/cache
   writes. Execution gates are obtained in fixed order: Library, then Subtitles
   if both apply. Features never acquire each other's gate, and policy recording
   never calls back into them.
3. In one Database transaction, the selected policy facts and dependent feature
   attempts are cleared. Each feature invalidates completed-search reuse for the
   affected blocked unresolved requests, preserving candidate records and
   successful metadata. Otherwise the next Fetch could reuse the same rejected
   candidate set and stop again without asking the provider. Not every successful
   query in the reset scope is invalidated. Library suppression attempts are
   removed. For subtitle jobs, suppression-derived NoMatch/capped-complete state
   is cleared and only work still authorized by existing manual or automatic
   eligibility reopens. Stage evidence, Exact/Likely match facts, output
   references, choices and unrelated jobs stay. A finished historical search is
   not renewed manual authorization.
4. Gates are released, state refreshes and previously accepted work resumes. The
   cleared scope does not become a search queue. If drain or commit fails,
   committed history is retained, work resumes from it and the reset failure is
   reported.

Library distinguishes Stop, which ends its batch, from the temporary pause/drain
that reset and dataset installation use; pause/drain keeps interrupted but
still-valid queued intent. Subtitle reset preserves membership and file
ownership; it only reopens the necessary search stage. No UI or database lock is
held while draining the network. Existing operation timeouts bound the drain.
This avoids both lost work and stale completions without a generation/version
protocol.
