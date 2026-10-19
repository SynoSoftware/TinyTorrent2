# Responsive torrent communication

**Owner ruling: this full version is the plan. Do not replace it with a
condensed rewrite or restore an older copy over it. Propose shortening as an
edit that keeps the owner rulings, the findings table, the modules-and-seams
section and the baseline gate, so the owner can see what is removed.**

**Paused.** Stage 1, the reviewed responsiveness fixes, is implemented, and its
rules live in the [pipe API](../contracts/pipe-api.md) and
[engine](../contracts/engine.md) contracts. The next version below is not
scheduled: it is complicated for little gain, and the owner has not decided
whether to implement it. Start it only when the owner asks.

[Architecture](../contracts/architecture.md), [engine](../contracts/engine.md), [pipe API](../contracts/pipe-api.md),
[UI](../contracts/ui.md#committing-edits), and [testing](../agents/testing.md) remain the
authorities. Update their affected sections as the design is implemented. This
plan is not runtime evidence or a second implementation-status backlog.

## Intended behavior

Selecting a torrent shows available facts immediately. Fresh detail and local
peer locations arrive independently in the background. A slow torrent does not
hold up another torrent's details. Settings switches reflect the click while
saves proceed asynchronously. Commands retain their required persistence order.

Immediate means no application-imposed wait for fresh collection, another
request, a lookup, a save, or a polling interval before displaying known facts.
Missing facts remain unknown; successful persistence is reported only after
commit. All prior requirements remain in scope: single-torrent peers first,
then all detail and communication paths, including the Settings delay.

## Source findings and current patch

| Finding | Source | Disposition |
| --- | --- | --- |
| Peer aggregation dropped the callback applying the immediate cached reply. | [Inspector peers](../../app/src/Torrents/Inspector/Peers.cs) | The patch restores it. Preserve this fast display path. |
| The pipe previously waited for each reply before sending another request. | [PipeClient](../../app/src/Shared/Engine/PipeClient.cs), [native Pipe](../../lib/Pipe/Pipe.cpp) | The patch multiplexes requests using existing request IDs and one ordered writer. |
| Every peer reply rebuilds collections over all displayed peers. | [Inspector peers](../../app/src/Torrents/Inspector/Peers.cs) | Fix the repeated merge work before expanding concurrency. |
| Every changed setting previously called full policy application, including display preferences. | [Settings](../../engine/src/Engine/Settings.cpp), [Policy](../../engine/src/Engine/Policy.cpp) | The reviewing agent's fix compares policy groups and skips unaffected work. It is included in the first commit; confirmation still follows the store commit. |
| Partial replies construct complete file JSON separately for each arriving detail kind. | [Detail](../../engine/src/Engine/Detail.cpp), [Torrent](../../engine/src/Torrent.cpp) | Combine publication at the end of the current alert batch, before construction. Implemented in source: `Receive` marks the reading and `Tick` publishes once after the batch. |
| The engine holds one detail collection, and a new context read from the same connection cancels the earlier one. The window therefore reads the all-torrents Peers scope one torrent per round trip, and a refresh of N torrents costs N pipe and libtorrent round trips. | [Detail](../../engine/src/Engine/Detail.cpp), [Inspector peers](../../app/src/Torrents/Inspector/Peers.cs) | Replace this with shared per-torrent collection in the next architecture change. This is the one finding that forces that change. |
| The snapshot reply serializes status refreshed on the engine's periodic clock, while the window polls on its own clock. | [Session](../../engine/src/Engine/Session.cpp), [Commands](../../engine/src/Engine/Commands.cpp), [PipeClient](../../app/src/Shared/Engine/PipeClient.cs) | Keep the immediate reply and push later observations. The two cadences introduce staleness; their constants alone do not bound collection, queueing or UI time. |
| Native delivery and the window each retain one pending detail message. | [Pipe](../../lib/Pipe/Pipe.h), [MainViewModel](../../app/src/Shell/MainViewModel.cs) | Concurrent torrent updates require separate pending entries, or results will overwrite each other. |
| Background file reads share Inspector's demand lifetime and use negative contexts. | [File reads](../../app/src/Shared/Engine/PipeClient/Files.cs) | Move complete-data consumers to independent requests using the same collector. |
| Settings already chooses the clicked value before awaiting the save. | [Settings](../../app/src/Settings/Editor.cs) | Trace input-to-render latency as well as the pipe; do not add a second optimistic state owner. |

The source proves these paths exist, not their share of the reported delay.
The request path itself has no timer: the pipe thread posts each request to
the engine thread, libtorrent answers wake that thread the same way, and the
reply goes straight to the window's receive loop. Measurement must establish
the share of the reported delay, the cost of building and parsing a snapshot
or a file list at the owner's sizes, and the time an all-torrents Peers refresh
takes against torrent count. No runtime measurements were performed for this
change. Country and city lookup happen locally in WinUI, not
through the engine pipe.

**Owner requirement: display available facts immediately, even when they are
not current; fresh facts follow in a later packet and the UI never waits.**
Cheap cache access and short state mutations remain synchronous on their owner.
Slow collection, file I/O and lookup must not occupy that dispatch path. Actions
such as Open may asynchronously await their own complete-data reply; this never
holds the pipe or another request. Successful saves still require persistence.
Do not add an acceptance message unless a caller needs it.

## Next version: scoped detail subscriptions

### Ownership and transport

Keep one duplex named pipe, frame encoding, ordered writer, receiver, request
IDs, and bounded outstanding work. Independent requests complete independently.
The engine message-loop thread remains the state owner; its existing commit
queue orders durable mutations. Concurrent collection means overlapping async
queries, not parallel mutation of engine state or one thread per torrent.

Inspector owns one subscription describing its visible section and torrent
identities. Single selection, multiple selection, and all-torrent peers use
that same path. WinUI supplies identities from its current scope and replaces
the scope when membership changes. An empty or hidden view releases its demand.

The first demand starts immediately. Later refreshes follow the existing window
refresh setting, with one cadence and no duplicate query while a kind is still
outstanding. Hidden or disconnected subscriptions produce no new collection.
Speed continues using the existing engine histories rather than a new store.

The engine starts each later refresh: it re-collects a held subscription at
the window's saved interval and pushes the observations. The window already
tells the engine its interval through settings, the fresh facts then arrive as
pushed packets without a request in front of them, and a scope of many
torrents costs one declaration instead of one request per refresh. With
summaries pushed the same way below, this is the only consistent choice, so it
is not an open decision. The [pipe API](../contracts/pipe-api.md) sentence that no refresh
timer runs for a hidden inspector stays true, because a hidden view releases
its subscription.

### Modules and seams

Terms follow the `/codebase-design` skill. A module is anything with an
interface and an implementation. Its interface is everything a caller must know
to use it correctly, including ordering rules and lifetimes, not only the
signature. A seam is where that interface lives. A module is deep when a lot of
behavior sits behind a small interface.

Today the detail interface is shallow and leaks across three modules. A caller
of `PipeClient` must know the `Consumer` convention, that a file read waits
behind the Inspector's demand, that a background read uses a negative context,
and that a push with `ready` ends the demand. Inspector correlates pushes
itself, and `MainViewModel` keeps its own single pending slot. All of that
exists because the engine's collector holds one `Reading` and cancels a
connection's earlier context read: an internal limit of the engine became part
of the interface every caller learns. The deletion test says the gate in
`PipeClient/Files.cs` earns its keep today, but at the wrong seam; remove the
engine's limit and the gate has nothing left to guard.

The same vocabulary names two further defects. `Consumer` names instances, not
kinds: `Summary`, `Inspector`, `Draft` and `Files` are particular callers, and
`Next` and `BeginDetail` switch on them for per-caller rules, such as one in
flight, last in line, or borrowed demand. The rule a request needs, replace my
unsent read or wait for complete facts, is a property of that request, not of
who sent it. And `Pipe::Connection::Send` branches on the `detail` message type
to keep one undelivered copy, so the transport module knows an application
message kind, and keyed slots would add more such branches there.

Target shape: three deep modules at two seams.

- **Engine collector**, one module in `State` replacing `Reading`. Interface:
  `Demand(torrent, kinds, reader)`, `Release(reader)`, `Invalidate(torrent)`,
  and the alert answers it already receives. Two reader adapters sit at its
  reader seam: a display reader that receives every observation of its scope,
  partial and complete, and a complete-data reader whose reply is sent once
  when its facts are complete. Both adapters exist today, Inspector on one side
  and Open and Library on the other, so the seam is real. Publication after the
  alert batch, generation checks, dropped-alert recovery and the collection
  bound are implementation; no caller learns them.
- **Transport slot**, in `Pipe::Connection` and the window's receive path.
  `Send(message)` stays ordered and bounded. Add `Replace(key, message)`:
  latest wins per key. The key is opaque to the transport and chosen by the
  application, so the transport stops matching on message types. The writer
  today sends keyed detail only when the ordered queue is empty, so a sustained
  stream of replies, such as pipelined file reads, starves observations;
  the new writer sends one keyed observation after each ordered frame, so
  replies stay first without blocking observations. Interleaving can deliver
  a fresh pushed snapshot before an older snapshot reply queued earlier, so
  arrival order no longer proves freshness: the engine stamps every snapshot
  and observation with a per-connection sequence number when it builds it,
  and the window applies a message only when its number is above the last
  applied for that key, the summary or a context, torrent and section.
  Explicit refresh replies carry the same stamp. The window's keyed drain in
  `MainViewModel` is the same interface on the other side.
- **PipeClient**. Its interface shrinks to `Send`, `Read` for one-shot reads
  whose reply arrives when the facts are complete, `Subscribe(scope)` which
  replaces this window's previous subscription, `Release()`, and one
  `Observation` event carrying context, torrent, section, readiness and facts.
  The `Consumer` enum, the file-read gate and negative contexts go.
  `Withdraw` goes only once `Read` takes a cancellation token that withdraws
  an unsent read, because `AddDraft.Close` cancels queued preview reads
  with it today and the next poll must not wait for the previous one; a read
  already sent completes and its caller ignores the outcome, as now. One
  unsent summary read at a time stays, as a property of `RefreshSnapshot`.

Wire interface at the pipe seam: `subscribe {context, section, torrent_ids}`
for display, and `torrent {torrent_id, view}` for complete-data reads, whose
reply is deferred until the facts are complete. Pipelining means that held
reply blocks nothing, which is what the ruling allows. Observations are
`{type:"observation", session_id, context, torrent_id, section, ready, data}`.
Inspector stays the one owner of context currency: it increments the context
and discards observations that do not match; PipeClient does not second-guess
it.

Test surface: the collector is exercised through the wire by the existing
engine checks, with demand, answers, invalidation, removal, dropped alerts and
observations all visible there, and PipeClient through `Subscribe` and the
`Observation` event. No internal seam is added until a second adapter needs it.

### Delivery and identity

Available facts are delivered immediately and applied through the same owner as
fresh updates. Preserve handling of data in the initial reply. Aggregate detail
arrives as independently addressed per-torrent observations, without waiting for
the whole scope or assembling it into one oversized frame.

Reuse session identity and Inspector context as the subscription generation.
Every update identifies its torrent and section. The request ID correlates an
operation; the context decides whether an update still belongs to the view.
No packet acknowledgements, retransmission, replay log, or second transport is
needed for this behavior.

Selection updates presentation and replaces the subscription without awaiting
old work or its release. Keep known rows for identities still in scope. Retained
presentation for a recently viewed torrent can display before a new reply.
Apply subsequent results only to the current session, context, torrent and view.

### Shared per-torrent collection

Replace the global active `Reading` and other-torrent queue with collections
keyed by durable torrent identity. Each owns requested detail kinds, available
answers, generation, and waiting readers. Reuse the existing torrent query
records so only one query for a torrent/detail kind is outstanding.

Display subscriptions and complete-data consumers use this same collector.
Background file reads and actions such as Open await the complete facts they
need through ordinary request IDs; they do not borrow Inspector's context.
Remove the negative-context convention and global Inspector/background-file gate
as callers migrate. Keep one implementation of validity and collection rules.

Reply from available facts before admitting any fresh query. A display with no
retained facts receives unknown values immediately. Collection capacity never
delays that reply or the local presentation change.

Collect multiple torrents within a bounded batch, prioritizing newly visible
demand while allowing background work to progress. Start with eight in-flight
fresh torrent collections as a provisional internal bound, then tune from evidence.
Release capacity when collection finishes; retained subscriptions do not occupy it.
The bound exists because libtorrent's alert queue holds 2000 alerts by default
and drops the rest; a scope of hundreds of torrents queried at once can
overflow it, and `RecoverDetail` then fails the affected reads. This is not a
user setting or a claim of optimal concurrency. One slow collection must not
stop other admitted collections or unrelated commands and summaries.

An arriving fact updates its torrent and marks its publication pending. At the
end of the current alert batch, publish each changed observation once. Complete
callers finish only when their required facts satisfy the existing validity
rules. Unknown, known-empty and complete remain different states.

Keep generation checks at this owner: answers predating an invalidating edit
cannot become current facts. Removal settles that torrent's readers while
others continue. Dropped-alert recovery reconciles or fails affected reads;
increasing concurrency must not create waits that can never finish.

### Bounded retention and publication

Retain requested detail kinds rather than all peers, files and pieces for every
torrent. Release collections without readers. Keep the existing bounded display
retention in Inspector; do not add another cache service or durable torrent
database. Inspect retained bytes as well as entry counts before tuning limits.

Replace the single pending detail slot at both endpoints with entries keyed by
context, torrent identity and section, behind the key-agnostic `Replace`
described under [modules and seams](#modules-and-seams). A newer observation
replaces only the older observation for that key. Each observation contains the currently available
facts for its section, so replacing it does not require replaying field patches.

Drain keys in arrival order. Include keyed publication in an explicit bound on
pending bytes and messages. The existing single detail slot bypasses the ordered
output limit, so a new keyed map does not inherit that protection. Scope size
alone is not a queue budget. Never silently truncate rows or discard another
torrent's only update. One entry per key means the pending count cannot
exceed the scope, so the budget is bytes per subscription. When a
subscription's pending bytes exceed it, the engine ends that subscription with
an explicit `overloaded` observation, without blocking; the view shows that
failure and subscribes again only when its scope changes. Disconnecting is
not the response to a scope that is too large, because the window would
subscribe to the same scope on reconnect and loop while interrupting unrelated
commands; it remains the response to a reader that stops reading, through the
existing ordered-output limit. Replies and control messages keep their room
ahead of observations. Clear
obsolete subscription entries when the context is replaced or disconnected.

Queue one UI dispatcher drain for pending observations and apply the newest
current update for each key. Split a drain across dispatcher turns only when a
measured all-torrents scope blocks input for a visible time. Changing scope
discards old detail, not unrelated completions.

### Incremental presentation

Inspector owns peer groups by torrent and stable rows keyed by
`(torrent_id, endpoint)`. Match a torrent's `p` peers in O(p) work. Update
existing objects for telemetry changes and alter membership only where needed.
Do not rebuild a dictionary of all `P` peers for every torrent's answer.

Combine aggregate membership, summary, sorting and map updates once per scheduled
UI drain where required. Follow the [TableView update contract](../../lib/TableView/docs/tableview-contract.md#53-source-identity-and-update-contract)
to retain focus, selection and scrolling. A large drain must yield to input;
coalescing should reduce repeated work rather than delay first useful content.

General, Files, Trackers and Pieces apply known fields immediately without
erasing known values when a partial observation lacks them. File/tracker drafts
remain in their current owners. Display availability never authorizes a file
operation that requires confirmed paths or priorities.

Location lookup stays in WinUI's Geolocation owner. It runs in the background,
updates only retained peer identities and the matching dataset version, and
joins the next table/map update. Peer rows never await a location batch.

### Settings feedback and status freshness

Keep `Setting` as the sole owner of input, pending intent, confirmation and
failure. Later clicks remain available during a save, and a reply settles only
the submitted input. Navigation follows the existing immediate Settings rules.

Trace show-dialog and one engine-affecting preference through click, synchronous
notifications, binding/layout, enqueue, frame write, engine dispatch, commit,
reply and first rendered frame. Narrow repeated notifications or move blocking
work only where the trace identifies it. Separate visual latency from save time.

Return already known summary facts immediately, then deliver fresh snapshots
as coalesced pushes through the existing snapshot representation and application
owner. Initial replies and pushes carry the sequence stamp described under
[modules and seams](#modules-and-seams), so an older cached reply cannot
overwrite a newer observation even when the writer interleaves them. These
pushes replace routine polling; keep explicit refresh and reconnect reads where
needed. The selected cadence governs recurring telemetry collection, never
initial replies or command feedback. While a connection test is active the
engine pushes once per second whatever the saved interval, which replaces the
window's one-second polling that the [pipe API](../contracts/pipe-api.md#connection-measurement)
requires during the test, so phase changes and the hold countdown stay
responsive. One slow request cannot stop these independent updates.

Post command-triggered status queries directly through the existing status-query
owner, guarded against an outstanding query. A command that arrives while a
query is outstanding sets a pending mark, and the handler issues one follow-up
query when the outstanding answer arrives, because that answer can predate the
command and the feedback would otherwise wait for the next periodic refresh.
Setting `statusPending` alone can wait until the next tick. Pass only `query_name`, as the current routine query
does: `query_save_path` distinguishes explicit detail answers in the alert
handler and would bypass routine summary updates. Clear outstanding state when
the answer arrives or the relevant alert is dropped.

At alert-batch end, publish partial detail only for reads still held. Mark a
collection changed only after storing a current answer; invalidation clears the
mark and ending a collection destroys it. These rules prevent duplicate final
answers and empty partials after invalidation. No timer delays partial delivery.

## Implementation order for the next version

1. Establish the baseline and diagnostic points; record the exact executable
   and revision before making timing claims. Preserve the first commit's fast
   display paths throughout the change. The deciding number for steps 2 and 3
   is the all-torrents Peers refresh time against torrent count: it grows with
   one round trip per torrent by construction, and the baseline shows at what
   count it stops fitting the refresh interval on the owner's machine.
2. Implement per-torrent collection, scoped demand, and keyed pending delivery
   together across both endpoints. Do not enable concurrent torrent publication
   while either endpoint still has one detail slot.
3. Migrate Inspector and complete-data consumers, then remove the global reading
   queue, sequential aggregate loop, obsolete gates, and replaced context paths.
4. Make row application incremental, combine UI work, add snapshot pushes,
   and finish the Settings corrections an authorized trace identifies. The
   summary delivery change is independent of steps 2 and 3 and can land first.
5. Update the active contracts and wire representation with the implementation.
   Advance the protocol version from its then-current value in both executables
   and the existing harness; ship the matching pair.
6. Review the final diff and perform the narrowly scoped validation below only
   when authorized. Record remaining limitations rather than claiming zero delay.

## Evidence and acceptance

Use existing fixtures and diagnostics. Add behavioral coverage only for a
concrete unguarded failure under the testing policy; do not create a new test
framework, use sleeps to demonstrate concurrency, or assert source text.

| Scenario | Required result |
| --- | --- |
| Hold request A; send independent B. | B completes and is routed correctly while A remains pending. |
| Request a summary while its cached facts are old. | The cached reply displays immediately; the later observation reaches the window without waiting for its next periodic poll. |
| Delay one torrent's detail; read another torrent and a summary. | Available facts display and independent work completes. |
| Two torrents update before the UI drains; one updates again. | Both torrents survive delivery, with the newest observation for the repeated key. |
| Switch A → B → A with replies and lookups pending. | Retained rows display immediately; old contexts cannot alter the current view or loading state. |
| Deliver partial, empty, failed and complete observations. | Known facts remain visible; empty/failure affects its own demand; complete-data actions never consume incomplete facts. |
| Share a collection between Inspector and a complete-data caller. | Both receive their required facts; releasing Inspector does not cancel the other caller. |
| Edit/remove a torrent or drop an alert during collection. | Obsolete facts do not apply; affected readers settle; other torrents continue. |
| Flip a setting repeatedly and delay/refuse an earlier save. | Latest input remains responsive; refusal belongs to the submitted input; success still follows commit. |

When authorized, measure input-to-first-frame, enqueue-to-write,
write-to-initial-data, collection-to-publication, and UI application separately.
Use monotonic elapsed time within each process and correlate identities; do not
subtract unrelated clock origins. Report median, p95, p99, worst case, workload
size, retained bytes, and revision.

Cover one selected torrent, several, all torrents, warm/cold facts, and an
unrelated slow operation. The small per-torrent latency fixture alone cannot
establish large peer-list, large file-list or WinUI rendering cost. Review that
input can appear on the next available frame without waiting for IPC, and that
synchronous UI work does not prevent that frame.

The baseline needs four numbers that no existing fixture records: the
all-torrents Peers refresh time at ten, one hundred and the owner's torrent
count; snapshot build, write and parse time at that count; `DescribeFiles` time
for the largest file list in the owner's library; and how often the three
answers of a Files or Pieces read arrive in one alert batch, which decides
whether partial publication ever sends anything. `engine/tests/Latency.ps1`
measures single requests under peer load and needs these added. The engine
thread builds every cached reply synchronously today; split an expensive
describe or frame only if the `DescribeFiles` number requires it.

Build affected targets and use the prescribed output location only when the
owner requests validation. Use an optimized build for timing as required by the
repository. Keep all unperformed compilation, native interaction and timing
checks explicitly unverified.
