# Responsive torrent communication

**Owner ruling: this full version is the plan. Do not replace it with a
condensed rewrite or restore an older copy over it. Propose shortening as an
edit that keeps the owner rulings, the findings table, the modules-and-seams
section and the baseline gate, so the owner can see what is removed.**

**In progress.** Stage 1, the reviewed responsiveness fixes, is implemented,
and its rules live in the [pipe API](../contracts/pipe-api.md) and
[engine](../contracts/engine.md) contracts. The next version below is built as
protocol 39 in bfc54dc, and the contracts hold its rules. The Settings delay
is closed (see [Settings feedback](#settings-feedback)). Two items are open:

- The cost of a Files observation on a large file list (see the baseline
  below).
- A Peers view without one target releases its subscription before it
  subscribes again when its context is invalidated, which a contract sentence
  and a comment do not describe (see [delivery and identity](#delivery-and-identity)).

The paragraphs below that the departures override stay, so the owner can see
what changed. On 11 November the built sections were removed from this plan,
because their rules now live in the [pipe API](../contracts/pipe-api.md#snapshots-and-detail)
and [engine](../contracts/engine.md) contracts: "Shared per-torrent
collection", the retention paragraph of "Bounded retention and publication",
"Incremental presentation", the status-freshness paragraphs of "Settings
feedback and status freshness", the paragraph on refreshes the engine starts,
and implementation steps 2 to 5. `git show bfc54dc:docs/plans/communication-plan.md`
shows them. The engine contract's rules for collections and for the status
query arrived in the same change as this shortening, so the two are committed
together.

**Step 1 baseline, 9 November.** Engine of protocol 38 (the build before
batch 4), `engine/tests/Latency.ps1 -Samples 40` under a load of 117 live peer
connections over 120 torrents, Release, on the owner's laptop. Run
`Latency-e7c6918e`. The all-torrents Peers refresh, one round trip per
torrent: 10 torrents median 24 ms (worst 143), 100 torrents 211 ms (worst
240), 120 torrents 210 ms (worst 228). At about 2 ms per torrent it would
outgrow the default one-second refresh interval near 500 torrents. The
120-torrent snapshot is 105,492 bytes, arrives in a median 5.7 ms and parses in
1.7 ms. A Files read got its three answers in one alert batch 77 times of 80,
and a Pieces read 74 of 80, so partial publication seldom sends anything.
`DescribeFiles` now returns only the file count. The per-file cost moved into
each Files observation: the engine thread copies the file priorities and
progress, and the trees worker builds a tree of every file with its path, at
every refresh (`engine/src/Engine/Detail.cpp`, `Respond`). That cost is
unmeasured, because the load torrents hold one file each. `engine/tests/Checks.ps1
-Check FileWindow` builds a torrent of 10,921 files and sends this read through
`Send-Timed`, which returns its milliseconds; the check discards them today.
Run against a Release engine (`-EnginePath`), it gives the number. The owner
names the size of the largest list only if it is far above 10,921 files. The
trees worker also serves `tree` requests, so the time of one build also
bounds how long a `tree` request can wait behind it.

**After protocol 39, 10 November.** The same script and load on the same
laptop, two runs, `Latency-dd83bae1` and `Latency-1a5f6fcb`. The all-torrents
Peers refresh, from the subscription to every torrent's ready observation:

| Torrents | Protocol 38, median (range) | Protocol 39, first run | Second run |
| ---: | --- | --- | --- |
| 10 | 24 ms (19–143) | 20 ms (15–124) | 17 ms (11–57) |
| 100 | 211 ms (190–240) | 129 ms (115–183) | 144 ms (107–333) |
| 120 | 210 ms (198–228) | 151 ms (137–169) | 164 ms (135–385) |

In the first run every refresh of 100 or 120 torrents is faster than every
refresh of protocol 38. The second run's slowest refreshes came while its
snapshots also arrived two to six times slower than in the other runs, up to
37 ms against 5 to 11; batch 4 only made that path cheaper, so the laptop was
busy then. The script parses each frame in PowerShell: the subscription's
reply arrives a median 0.5 to 0.6 ms after the first observation. At two
frames per torrent that parse is most of the 120-torrent time, so the engine's
own time per torrent is lower than the table shows and is not isolated. In the
second run, whose script starts its clock at the request as protocol 38's did,
one torrent's first and complete observations take a median 1.5–1.7 and
2.4–2.8 ms across the five views, against 1.4–1.9 and 2.3–2.9 for protocol 38.
A Files read got its answers in one alert batch 80 times of 80, and a Pieces
read 78 of 80.

**Departures from the design below.** Protocol 39 built it with these three
differences:

- No `overloaded` byte budget. Each torrent has one pending entry, so the
  scope bounds the pending messages, and ending a large subscription would
  hide every torrent's peers instead of showing what is known.
- No per-key sequence number on observations. Observations travel only
  through the keyed slot, keyed by context and torrent, so a newer one for a
  key always follows an older one; snapshots keep their existing `sequence`.
- The `Consumer` enum stays: it still gives each view its one unsent read, and
  replacing it touches the summary and draft readers, which the collector and
  keyed delivery do not need. `Withdraw(Consumer)` therefore stays too, and
  `Read` takes no cancellation token. The sentence under [modules and
  seams](#modules-and-seams) that removes `Withdraw` describes no planned work.

Three more differences in names and shapes, which the contracts own:

- The collector is a set of `Engine::State` functions in
  `engine/src/Engine/Detail.cpp`: `Subscribe`, `ReadDetail`, `CollectDetail`,
  `ReleaseReading(connection)` and `Invalidate`. Its `Demand` means "collect
  this torrent before the refresh of torrents already shown", not the
  `Demand(torrent, kinds, reader)` below.
- An observation is a reply, with `ok` and `data` or `error`, plus `type`,
  `session_id`, `context`, `torrent_id` and `section`. `ready` is inside
  `data`.
- The window's receive thread drops an observation of another context before
  the inspector sees it (`PipeClient.Keeps`), as the pipe API says. The
  inspector still owns the context.

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

Protocol 39 built every disposition below except the Settings row, whose two
named causes are handled while its symptom is not recorded.

| Finding | Source | Disposition |
| --- | --- | --- |
| Peer aggregation dropped the callback applying the immediate cached reply. | [Inspector peers](../../app/src/Shell/Inspector/Peers.cs) | The patch restores it. Preserve this fast display path. |
| The pipe previously waited for each reply before sending another request. | [PipeClient](../../app/src/Shared/Engine/PipeClient.cs), [native Pipe](../../engine/src/Pipe.cpp) | The patch multiplexes requests using existing request IDs and one ordered writer. |
| Every peer reply rebuilds collections over all displayed peers. | [Inspector peers](../../app/src/Shell/Inspector/Peers.cs) | Fix the repeated merge work before expanding concurrency. |
| Every changed setting previously called full policy application, including display preferences. | [Settings](../../engine/src/Engine/Settings.cpp), [Policy](../../engine/src/Engine/Policy.cpp) | The reviewing agent's fix compares policy groups and skips unaffected work. It is included in the first commit; confirmation still follows the store commit. |
| Partial replies construct complete file JSON separately for each arriving detail kind. | [Detail](../../engine/src/Engine/Detail.cpp), [Torrent](../../engine/src/Torrent.cpp) | Combine publication at the end of the current alert batch, before construction. Implemented in source: `Receive` marks the reading and `Tick` publishes once after the batch. |
| The engine holds one detail collection, and a new context read from the same connection cancels the earlier one. The window therefore reads the all-torrents Peers scope one torrent per round trip, and a refresh of N torrents costs N pipe and libtorrent round trips. | [Detail](../../engine/src/Engine/Detail.cpp), [Inspector peers](../../app/src/Shell/Inspector/Peers.cs) | Replace this with shared per-torrent collection in the next architecture change. This is the one finding that forces that change. |
| The snapshot reply serializes status refreshed on the engine's periodic clock, while the window polls on its own clock. | [Session](../../engine/src/Engine/Session.cpp), [Commands](../../engine/src/Engine/Commands.cpp), [PipeClient](../../app/src/Shared/Engine/PipeClient.cs) | Keep the immediate reply and push later observations. The two cadences introduce staleness; their constants alone do not bound collection, queueing or UI time. |
| Native delivery and the window each retain one pending detail message. | [Pipe](../../engine/inc/Pipe.h), [MainViewModel](../../app/src/Shell/MainViewModel.cs) | Concurrent torrent updates require separate pending entries, or results will overwrite each other. |
| Background file reads share Inspector's demand lifetime and use negative contexts. | [File reads](../../app/src/Shared/Engine/PipeClient/Files.cs) | Move complete-data consumers to independent requests using the same collector. |
| Settings already chooses the clicked value before awaiting the save. | [Settings](../../app/src/Settings/Editor.cs) | Trace input-to-render latency as well as the pipe; do not add a second optimistic state owner. |

The source proves these paths exist, not their share of the reported delay.
The request path itself has no timer: the pipe thread posts each request to
the engine thread, libtorrent answers wake that thread the same way, and the
reply goes straight to the window's receive loop. Measurement must establish
the share of the reported delay, the cost of building and parsing a snapshot
or a file list at the owner's sizes, and the time an all-torrents Peers refresh
takes against torrent count. The baseline and the measurement after protocol
39 answer the snapshot cost and the refresh time; the Files observation cost
and the Settings delay remain unmeasured. Country and city lookup happen locally in WinUI, not
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

Test surface: `engine/tests/Checks.ps1` covers complete `torrent` reads and
removal through the wire. No check sends a subscription, asserts an
observation or drops an alert; `engine/tests/Latency.ps1` subscribes and
measures, and dropped-alert recovery is verified by reading only. No internal
seam is added until a second adapter needs it.

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

**Open: a Peers view without one target releases before it subscribes again
when its context is invalidated.** In the all-torrents Peers view and a
multiple selection's Peers view, every command reply and every change of
selection or section invalidates the context. `Inspector.Invalidate` then
sends a release at once, and the next subscription follows a frame later. A
change of the scope's torrents, such as a torrent whose peer count reaches
zero, subscribes again without a release, and the contract is right for that
path. After a release, the engine ends that scope's collections before the
new subscription arrives, so the first observations carry no peers, and observations of the old
context that were already queued are still sent. Nothing visible changes: the
window keeps its rows when an observation has no peers, and a display
subscription needs facts newer than its own start either way. The release
saves work, because every command reply resubscribes the scope, settings and
layout saves included, and without it the engine would rebuild every shown
torrent's retained peer list for a view that has not changed. Keep the code.
Correct the [pipe API](../contracts/pipe-api.md#snapshots-and-detail)
sentences that a new subscription keeps the collected facts and withdraws the
earlier one's unsent observations, so they name this exception and its
trigger, and the comment above `Inspector.Release`, which says that a later
subscription often replaces the release unsent. The alternative, releasing
only when no subscription follows, makes one path for every scope at the cost
of that rebuild, which is unmeasured beyond the 117 peers of the `Latency.ps1`
load.

### Keyed publication

Replace the single pending detail slot at both endpoints with entries keyed by
context, torrent identity and section, behind the key-agnostic `Replace`
described under [modules and seams](#modules-and-seams). A newer observation
replaces only the older observation for that key. Each observation contains the currently available
facts for its section, so replacing it does not require replaying field patches.

Peer diffs are applied in the window, not sent on the wire. Each observation
carries one torrent's full peer list. A worker matches it against the kept
rows (`Swarm.Read`), and the UI thread changes only the peers that joined,
left or changed. A join or a leave also makes the worker build the view's
whole list again (`Swarm.Join`), and the table copies every row reference of
that list on the UI thread (`Source.Set`). The wire stays full lists because every delivery step
replaces or drops whole messages: the engine's keyed slot, the withdrawal of
an earlier subscription's messages, the receive thread's context check and the
window's drain. A diff would need a baseline per subscriber and a resync after
each of those drops; even "send nothing when unchanged" loses a change when it
replaces an unsent full list in the keyed slot. libtorrent returns the full
list on every query, so the engine pays for the list either way, and no
measurement shows that its bytes cost anything the person sees: the load of
`Latency.ps1` holds 117 peers. Jagged movement in the owner's all-torrents Peers view, or a
torrent near libtorrent's limit of 3000 known peers whose observation costs
visible engine or parse time, would change this. The first lever then is to
send the slowly changing list of known, unconnected peers less often.

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
discards old detail, not unrelated completions. No split is planned: a worker
reads and matches the peer lists, and the UI thread applies only the changes,
plus one copy of the view's row references after a join or a leave.
The capture fixture applies the all-torrents scope one torrent at a time, so
its all-torrents figures overstate the work per refresh and do not measure
this path.

### Settings feedback

Keep `Setting` as the sole owner of input, pending intent, confirmation and
failure. Later clicks remain available during a save, and a reply settles only
the submitted input. Navigation follows the existing immediate Settings rules.

Closed on 11 November. No symptom is recorded: no switch, no duration and no
build, and the owner knows of no delay. Both causes the findings name are
handled: `Setting.Toggle` shows the click before it awaits the save, and the
engine reapplies only the policy groups that changed
(`engine/src/Engine/Policy.cpp`). A switch that the owner later finds slow
opens the item again; then trace that switch only, as follows.

Trace that switch through click, synchronous
notifications, binding/layout, enqueue, frame write, engine dispatch, commit,
reply and first rendered frame. Narrow repeated notifications or move blocking
work only where the trace identifies it. Separate visual latency from save time.

## Implementation order for the next version

1. Establish the baseline and diagnostic points; record the exact executable
   and revision before making timing claims. Preserve the first commit's fast
   display paths throughout the change. The deciding number for the per-torrent collection
   is the all-torrents Peers refresh time against torrent count: it grows with
   one round trip per torrent by construction, and the baseline shows at what
   count it stops fitting the refresh interval on the owner's machine.
   Done on 9 and 10 November (see the baseline above), except the Files
   observation cost.
2. Settings feedback: closed, with no symptom (see
   [Settings feedback](#settings-feedback)).
3. Correct the pipe API sentences and the comment named under [delivery and
   identity](#delivery-and-identity).
4. Review the final diff and perform the narrowly scoped validation below only
   when authorized. Record remaining limitations rather than claiming zero delay.

## Evidence and acceptance

Use existing fixtures and diagnostics. Add behavioral coverage only for a
concrete unguarded failure under the testing policy; do not create a new test
framework, use sleeps to demonstrate concurrency, or assert source text.

Protocol 39 built every row below. The status column names the code that
enforces each row, as read at a1cb18b on 11 November.

| Scenario | Required result | Status |
| --- | --- | --- |
| Hold request A; send independent B. | B completes and is routed correctly while A remains pending. | Built: `PipeClient.RunConnection` and the engine's request IDs. |
| Request a summary while its cached facts are old. | The cached reply displays immediately; the later observation reaches the window without waiting for its next periodic poll. | Built: summary pushes and the summary key in `Pipe::Connection::Replace`. |
| Delay one torrent's detail; read another torrent and a summary. | Available facts display and independent work completes. | Built: one collection per torrent, admitted by `Admit`. |
| Two torrents update before the UI drains; one updates again. | Both torrents survive delivery, with the newest observation for the repeated key. | Built: the keyed slots in `Pipe.cpp` and `MainViewModel`. |
| Switch A → B → A with replies and lookups pending. | Retained rows display immediately; old contexts cannot alter the current view or loading state. | Built: the context checks in `Inspector.Receive` and `PipeClient.Keeps`, and the kept swarms. Aggregate views are the open exception under [delivery and identity](#delivery-and-identity). |
| Deliver partial, empty, failed and complete observations. | Known facts remain visible; empty/failure affects its own demand; complete-data actions never consume incomplete facts. | Built: `Inspector.Apply` and `ContinueCollection`. |
| Share a collection between Inspector and a complete-data caller. | Both receive their required facts; releasing Inspector does not cancel the other caller. | Built: `ReleaseReading` keeps reads without a context. |
| Edit/remove a torrent or drop an alert during collection. | Obsolete facts do not apply; affected readers settle; other torrents continue. | Built: `Invalidate`, `FailReading` and `RecoverDetail`. No check drops an alert, so dropped-alert recovery is verified by reading only. |
| Flip a setting repeatedly and delay/refuse an earlier save. | Latest input remains responsive; refusal belongs to the submitted input; success still follows commit. | Built: `Setting.Toggle`; `app/tests/SettingChecks.ps1` checks who owns a save. |

**Status, 11 November.** The segment measurement and the coverage list in the
next two paragraphs are not planned: the [pipe API](../contracts/pipe-api.md#snapshots-and-detail)
holds the latency targets, `Latency.ps1` measured the engine side, and no open
question needs the segments. Measure them only for a symptom the owner names.

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

Status of this gate: `Latency.ps1` now records the first, second and fourth
numbers, and the baseline and the measurement after protocol 39 give them. The
third has moved: `DescribeFiles` returns only the file count, so the number to
take is the cost of a Files observation on a large file list, as the
baseline describes. Split that work only if the number requires it.

Build affected targets and use the prescribed output location only when the
owner requests validation. Use an optimized build for timing as required by the
repository. Keep all unperformed compilation, native interaction and timing
checks explicitly unverified.
