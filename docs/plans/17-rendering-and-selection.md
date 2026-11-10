# Rendering and selection

Status, 9 November 2026. The map handoff from `e723ae6` is implemented and
functionally validated; the owner reports much better pan/zoom. Preserve it. At
`4f069bb` every movable preparation already runs off the UI thread and the game
loop: Inspector's peer capture runs inside its worker, `MapModel.Update` stores a
population reference, picture resources are created and retired on the render
worker, traffic membership and dot input are built in `MapRenderer`, marker
preparation runs in the render pass, `MapWork.Prepare` runs on the pool and
`OnDraw` publishes one displayed value. No further "move it to a worker" task
exists; what remains on those threads is presentation or WinUI-owned work.

Batches 1 to 3 and 5 of the [fix list](#steps) were built and measured in one
round, which showed a defect in the map's rounds; see
[the batch round](#round-of-batches-1-to-3-and-5). The fix and batch 4 were
built and measured on 10 November; see
[the round of batches 1 to 5](#round-of-batches-1-to-5). Each click costs the UI
thread less, but the median click is slower in every pair, because presses wait
longer to be handled. [Batch 6](#batch-6-waste-found-on-10-november) removed
the waste in those waits and was measured the same day; see
[the round of batch 6](#round-of-batch-6). The stalled time, the working set and
the map's first connections fell in every pair. The UI thread is still stalled
for 1.3 to 1.6 s of each 3 s of clicks, in WinUI's own layout and the table's
cell layout. [Batch 7](#batch-7-remaining-waste-on-10-november) removed the
double parse of peer pushes and smaller waste in cell layout and snapshots.
[Batch 8](#batch-8-forced-collections-and-per-update-allocations) found that
the replay's own peer fixture caused WinUI's forced full collections, and
removed smaller per-update allocations. Both batches were measured in
[one round](#round-of-batches-7-and-8): the click medians did not change, and
the worst click fell to 164 ms.
[Batch 9](#batch-9-row-rebinds-map-redraws-and-engine-copies) removed the
map's extra drawings during and after fades, the second managed event of each
row rebind and several engine copies. [Its round](#round-of-batch-9) shows 5 or
6 map drawings in the idle second instead of 54 to 59, and no change in
clicks. [Batch 10](#batch-10-route-preparation-after-a-click) cut the map's
route preparation after a click from 122 to 57 samples of the click phase, and
the map now keeps its last picture while a new subject's rows are unread, as
its contract says; clicks did not change.

Responsiveness is not accepted. At `4f069bb` the worst navigation, marquee and
stationary-dot draw gaps are 322, 266 and 225 ms against a 16.67 ms frame period.
A blocking full collection fills only the stationary gap (210 ms of 225); the
navigation gap holds 41 ms of collection, and the marquee gap and the two worst
clicks hold none. Relocating work did not move those maxima. The
[budget](#budget) splits each endpoint into the work that fills it; the
[steps](#steps) first measure the unknown parts from saved traces, then change
each part until the sum fits the target.

Purpose, in the owner's words: "all I want is this to be as fast as possible."
That means faster input response and smooth animation, shown by before/after
evidence on the same workload. Scope: from a click or an update to the picture
on screen, across the app, the pipe and the engine. The replay feeds the app
from a synthetic peer source, so it never measures the engine or the pipe.
Current behavior is owned by
[architecture](../contracts/architecture.md),
[peer location](../contracts/peer-location.md),
[torrent Inspector](../contracts/ui/torrents.md) and
[TableView](../../lib/TableView/docs/tableview-contract.md). The
[harness guide](../../app/tests/Performance.md) is the measurement entry point.

## Outcome

A torrent or peer selection acknowledges input without awaiting grouping,
projection preparation, geometry, rasterization or a replacement picture. The
map keeps presenting completed content while replacement content is prepared;
new content appears when available, and a completed handoff may contain partial
detail. Completing visual detail cannot delay input or presentation. Selection
semantics and displayed interaction targets stay correct while refinement is
incomplete.

The goal: a torrent click is visibly reflected in the next displayed frame.
Map navigation, stationary dot animation and marquee movement miss no
presentation deadlines and show no uneven jumps. Measure each separately; smooth
dots cannot compensate for stalled geography. Use the actual display refresh
rate; at 60 Hz the frame period is 16.67 ms. This is the direction of the work,
not a pass mark; [acceptance](#acceptance) says how a change is judged.

Native selection, accessibility, drag gestures, unfinished-edit protection and
the retained Inspector remain intact. Native control updates still run on the UI
thread; detached calculation and native map rendering run elsewhere.

## Preserved handoff and settled decisions

- **A frame does not wait for refinement, and refinement is weighed by what it
  redraws.** The game loop presents the available compatible picture and
  advances dots without joining grouping or rendering. The render worker
  publishes useful partial detail through the existing completion handoff,
  then refines it. Blocked grouping leaves navigation over the last completed
  facts; blocked rendering leaves input and presentation over the last
  completed picture. Each drawing draws the whole map, so refinement that asks
  for another drawing costs a whole map for the strokes it changed. Before
  batch 9, fades did this 54 to 59 times in an idle second. Since batch 9, arc
  widths and borders that a drawing's budget refuses wait for a drawing that
  comes anyway or for passes that draw nothing. A zoom's guides, outlines and
  arc strokes still ask for another drawing each time the 6 ms budget runs
  out, until the view is refined.
- Keep the implemented owners and seams: Inspector and PeerMap accept intent on
  the UI thread; `MapWork` groups detached observations on a serialized worker;
  the `MapMotion` render worker owns one mutable renderer, native geometry and
  offscreen rasterization; the `MapMotion` game loop owns adopted completions,
  per-frame viewport, dots and presentation and publishes the one displayed
  inspection/view. One latest request, bounded completions, worker-only native
  lifetime; the UI never joins the worker. A generic scheduler, second device,
  tile cache or alternate representation needs a demonstrated failure first.
- Retain the raster transform, longitude wrapping and one-eighth viewport margin
  at every edge, and the three-target bitmap reuse. Static strokes and sampling
  may scale temporarily; markers, rings and dots keep their DIP sizes.
- **Owner ruling: preserve speed over the country artifact.** The broad gaps and
  overlaps between shaded countries remain visible and are accepted where a
  correction would slow navigation or complicate the renderer. The antialiasing
  coverage correction at `4f069bb` is retained; the gaps are not claimed fixed,
  and no further probe, geometry or handoff redesign is released for them.
- Themed location lights without outlined rings are done at `caad1b0`.
  Marker-layer caching stays deferred.
- Dots during navigation are optional: keep them moving when that preserves
  responsiveness, pause or hide them when it does not, and resume without
  another input when the gesture ends. Stationary dot animation keeps its own
  requirement.
- **Remove work first, then reduce it, then move it.** Work that nobody needs
  is removed. Needed work is made smaller. Needed work that is not UI or
  drawing then leaves the UI and drawing threads, for the owner whose
  responsibility it already is, such as an app worker, a library policy or the
  engine. A move never turns the architecture upside down, for example by
  making a library depend on the app or the engine depend on the UI. Moving
  alone does not make work cheaper: moved work still allocates, a collection
  stops every thread, and WinUI's forced full collection runs on the UI
  thread. At `4f069bb` every movable preparation already ran off the UI thread,
  and the worst gaps stayed at 322, 266 and 225 ms. Plan 18 split the row work
  into pieces, and the median click was slower in all 15 pairs.
- **Owner ruling, 9 November: a TableView change starts from the table's
  history.** TableView work is open. Before designing a change, read every
  TableView commit since the `v0.9.1.2` release
  (`git log v0.9.1.2..HEAD -- lib/TableView`) and the TableView documents,
  `lib/TableView/docs/archive` included, and find each earlier attempt at the
  same change or its opposite. The design names each attempt: what it did, why
  it was removed according to its commit message, and what the new design does
  better. The investigation is done when every commit that changed the code the
  design edits is accounted for. The table's behavior has gone back and forth:
  the 3 November revert to `v0.9.1.2` was made for memory, and it also removed
  two owner rulings on switching to a different list that had nothing to do with
  memory. The next commit then built the opposite behavior.
- Memory growth causes freezes. WinUI asks for a blocking full collection
  after the working set grows by 50 MiB, with a median pause of 162 ms in the
  [budget](#budget), and every collection pauses every thread: 13% of the
  `4f069bb` run, and a third of the click time in the 9 November native trace.
  Allocations and live objects are reduced like any other waste.
- Build, test and capture rules are owned by [testing](../agents/testing.md)
  and [compilation](../agents/compilation.md); this plan adds none.

## Workload

Keep the established stress workload: the frozen 50-torrent fixture with
159,810 known peer rows (`fixture.json` owns the exact counts), 1,000 distinct
places, descending speed sorting, city detail with the 1,000-arc limit, docked
layout, Replay/InputOnly. Do not reduce it to obtain a pass; it is where the
stalls reproduce. Until 9 November replay ran offscreen, where the map draws no
dots during sorting and clicks; it now runs on the other display, so compare
only runs placed the same way ([benchmark](../../app/tests/Performance.md)). Compare the same fixture, profile, configuration, refresh
rate and DPI, against the preserved reference binary.

Distinguish two populations when attributing a cost. Peers represented on the
map are grouped places and arcs, built from detached facts off the UI thread.
Rows exposed to XAML are the peers table's rows, published through TableView on
the UI thread. A cost belongs to one of them; name which.

Since batch 6, `Swarm.Read` looks up the joined peers on its worker, so the
benchmark's switches include the lookups: about 4.6 µs per peer in the peers
capture's 10,000-peer journey. Inspector's `_locating` flag guards only the
public address's lookup. This removes the two whole-population sets per pass of
[issue 578](https://github.com/SynoSoftware/TinyTorrent2/issues/578), which
closes with the commit.

Ordinary use is a separate expectation: hundreds of rows, sometimes thousands,
rarely ten thousand. A correction that helps only at stress size and adds code,
state or memory at ordinary size is not retained.

## Acceptance

A change is judged first by counts of the work it removes, and then by the
harness's response and draw endpoints on the stress workload. Examples of
counts are the drawings in the idle second, the row notices of a click, the
messages parsed and the working-set growth. A count needs one run, does not
vary between runs, and names the code that does the work. The endpoints vary
between runs of one build by more than most changes move them: `new10`'s
median click was 43.6 ms in one round and 27.9 ms in the next. GC duration
explains a stall; it is not an acceptance endpoint, because a GC rule alone
would again leave responsiveness unverified.

The targets below are the goal, not a pass mark. At the stress load every
round ended "not accepted", whatever it changed.

| Endpoint (existing) | Target |
| --- | --- |
| Click-to-XAML completion, per click | p95 within one frame period; worst within three. The interval covers the whole UI turn after the click, including the selected torrent's peer-table publication; the click follow-up attributes what is inside it. |
| Navigation draw gap | p95 within one frame period; worst within three |
| Marquee sample gap | p95 within one frame period; worst within three |
| Stationary dot draw gap | p95 within one frame period; worst within three |
| Command-delivery worst | within three frame periods |
| First selected connections, first aggregate connections, post-click map settlement | not worse than the reference |
| Peak and end private commitment, peak working set | not above the reference |

The three-frame worst allowance is the default, not the outcome. The goal says
no missed deadline, which is one frame; three tolerates the
isolated misses that a full collection currently makes unavoidable, until a
round shows what one frame would cost.

These endpoints cannot certify the physical display. Displayed frame rate and
next-displayed-frame selection stay recorded as unverified until a
presented-frame endpoint exists; they are not a gate, because an unmeasurable
gate kept every round "not accepted" regardless of what changed.

### Deciding a correction

- Before building, the correction names the count or endpoint it predicts to
  change and the direction. A change without a predicted count or endpoint is
  not a performance correction and is judged as code only.
- One matched comparison against the preserved reference binary. Predicted
  count or endpoint improved and no endpoint worse than the reference:
  retained, and the reference moves to it. Predicted count or endpoint not
  improved: not retained, because it did not deliver its intended result.
- Predicted count or endpoint improved but another endpoint worse: one more matched
  pair, and that pair decides. Worse again on that endpoint: not retained.
  Otherwise retained. No third run, no retry for a favorable maximum.
- Corrections already retained stay unless a matched comparison attributes a
  regression to them. A worse single run is not a confirmed regression; it
  prevents an unjustified revert and nothing more.
- Record every adverse observation. Keep UI elapsed time distinct from worker
  time and queueing; never add overlapping intervals. New in-process probes
  need matched diagnostic builds of both revisions; external recording may use
  the preserved binary directly.

## Budget

The targets are the [acceptance](#acceptance) table's: p95 within one frame
(16.7 ms) and worst within three (50 ms). Each endpoint lists today's value,
the work inside its worst gaps, and the change that removes each piece. Values
come from the latest profiled build `4f069bb` (replay, its evidence folder
`artifacts/evidence/ui-profile-map-defined-changes-20261107-4f069bb`, joined
with its `gc-intervals.json`), and for clicks from the native 9 November trace
of plan 18's reference build. "Unknown" marks time no saved record attributes;
step 1 attributes it.

**Collections, which every endpoint shares.** In the 59.5 s `4f069bb` run:

| Kind | Count | Median pause | Longest | Total |
| --- | ---: | ---: | ---: | ---: |
| gen 0 | 109 | 23.5 ms | 76.9 ms | 3.3 s |
| gen 1 | 65 | 37.5 ms | 101.3 ms | 2.6 s |
| Full, requested by WinUI (induced, blocking) | 6 | 162 ms | 218.6 ms | 0.9 s |
| Full, background, large allocations | 19 | 34.8 ms | 49.3 ms | 0.66 s |

A gen 0 pause of 23.5 ms alone breaks the one-frame target, and each requested
full collection breaks the three-frame worst. The requested ones come from
WinUI: `CCoreServices::TrackMemory` is on the UI stack for the 262 ms collection
in the native click trace, and
`ReferenceTrackerHost.IReferenceTrackerHost_DisconnectUnusedReferenceSources` on
the UI stack of another. WinUI asks after the working set grows by 50 MiB
(step 2); what grows it is step 3, and why a gen 0 collection takes 23.5 ms is
step 4.

| Endpoint | Today, p95 / worst | Inside the worst gaps | Change |
| --- | --- | --- | --- |
| Click to rendered selection | native: median 41–100 ms, worst 156–505 ms (plan 18 round 4 reference) | Native trace, 12 clicks, 1,364 ms: collections 34%, WinUI drawing 30%, app code 17%, input 8%, layout 4%; in the long pauses, cell measuring and row building lead. `4f069bb`: the two worst (229 and 193 ms after selection) hold no collection; unknown | Plan 18 Step 1 removes row building on a peers switch; collections below; the rest after step 1 |
| Map navigation draw gap | 54–184 / 208–322 ms | 41 ms of collection at most. The gap coincides with the UI thread not delivering the next view change (worst command gap 332 ms in the same window); in a later managed trace the UI thread spent 60–70% in WinUI frames and 10–17% in `CellsPanel.MeasureOverride`. Unknown for `4f069bb` | After step 1 |
| Marquee sample gap | 142 / 266 ms | No collection. At `75af850`: 234 ms of native WinUI work, `CellsPanel.MeasureOverride` 81 ms forced by `Marquee.Track`'s `TransformToVisual`. Unknown for `4f069bb` | After step 1 |
| Stationary dot draw gap | 42 / 225 ms | 210 ms of one requested full collection; the 55–67 ms gaps each hold a 45–55 ms gen 0 or gen 1 pause. The dots draw on Win2D's own thread, but in C#, and a blocking collection stops every thread that runs .NET code | Collections below. If gaps remain after batches 1 to 3, the dot motion moves to Composition animations, which the system compositor runs outside the app's threads |
| Command delivery | 171 / 325 ms | Same stall as navigation; at `75af850`, `View.Apply` 112 ms in one click and the fixture's own summary feed 150 ms in another | After step 1 |

The owner watched a round 5 run of plan 18 on 9 November: the transfer dots
move jaggedly, and the pan is slow and jagged. Those are the stationary dot and
navigation rows. The owner judges them again live, beside the reference build,
after batches 1 to 3.

## Steps

Each step is done when its result is written into the budget above.

1. **Attribute the unknown gaps from saved traces, without a run.** Run the
   existing interval attribution over `4f069bb`'s `ui.nettrace` for these
   windows, on the UI, game-loop and render-worker threads: clicks 4 and 17
   (`raw-timing.json`), navigation 40,748–41,070 ms, marquee 36,037–36,304 ms,
   stationary 17,762–17,987 ms, command delivery 40,739–41,065 ms. Done when
   every unknown in the table is split by owner, in milliseconds.
2. **What makes WinUI request a full collection: largely known.** WinUI's
   source, read on 7 November: `CCoreServices::TrackMemory` runs in the render
   walk, reads the process working set and, after growth above 50 MiB, asks the
   runtime's tracker host for a collection, which calls `GC.Collect()`, a
   blocking full collection. The traces show the tracker host and
   `TrackMemory` on the UI stack of such collections, at intervals of 2.4 to
   3.5 s while the map opens. Done when the collected trace of step 4 shows
   `TrackMemory`'s caller for one requested collection; the budget then states
   the working-set growth per second each phase may have.
3. **Allocate only what is needed, when it is needed.** The 21 clicks of the
   native reference trace allocated about 82 MB in 3.15 s, mostly on workers:
   the table projection rebuilds its row maps from nothing on every publish
   (about 20 MB), each peer update parses every peer again into new records
   (13 to 24 MB), and the map rebuilds its inspection dictionary on every draw.
   For each owner in that trace and in the `78fb43f` allocation trace, write
   what it allocates per click and per update, what it must allocate, and the
   change that keeps the rest: reuse, update in place, or not at all. Done when
   every owner above 1 MB per phase has that line and the sum gives the
   working-set growth per second against step 2's 50 MiB.

   Each change is judged against the data-oriented form, because it removes the
   churn without the retained object-keyed state that made the reverted rework
   hold 788 bytes per row against 266: a row gets a small integer slot once,
   when it first appears; positions, membership and versions are arrays or bit
   sets indexed by that slot and kept between publishes, which the collector
   does not trace; the writer bumps a row's version or names the changed slots,
   so work follows what changed; a reply is parsed into the existing row,
   comparing bytes before making strings; buffers are sized once and reused.

   **Owner ruling, 9 November: every stage that redoes work an earlier stage
   already did, or makes an object only to drop it, is fixed; the collector is
   the evidence, not the target.** The same holds for WinUI objects that
   code-behind makes per update, row, draw or click instead of keeping one, and
   for copies made per element of an object that could be shared. Today each
   stage of the per-update path receives everything and works out again what
   changed. The fix list, in build order:

   | Batch | Owner and files | Today, per update of one torrent with N peers | Change |
   | --- | --- | --- | --- |
   | 1 | App: `Inspector.ApplyPeers`, `Peer.Record.Parse` (`Shell/Inspector/Peers.cs`, `Torrents/Peer.cs`) | A new record and new strings for all N peers, a fresh endpoint dictionary and set of N | Read each peer's raw value (`JsonMarshal.GetRawUtf8Value`) with `Utf8JsonReader`; find the existing peer through a per-torrent endpoint index kept between replies, looked up without making a string; build a new record only for a changed peer, and a string only when its text changed |
   | 1 | App: splice in `ApplyPeers` | Every reply scans every peer in scope to find this torrent's; a join or leave copies the scope four times, through a list, up to 159,810 peers | Each torrent's peers are kept with the torrent; a join or leave builds the view's list once at its exact size, about 80 KB at the 10,000 rows of the product's largest real use; a view of one torrent uses that torrent's array |
   | 1 | App: `Inspector.Filter` | A new map observation for all N peers and a count of connected and active peers over the whole scope, per reply; a chosen place's rows found again over the whole scope | Counts come from each swarm's pass; a chosen place's rows are found again only when a join, a leave or a location can change them; the map takes the round's updates (batch 3). No released rows are worked out: the person's change of place or scope withdraws the table's source (batch 6) |
   | 1 | App: `Geolocation.Find`, the peers' lookup (`Swarm.Read` since batch 6) | A new location, with its country and city places and their name dictionaries, for every located peer, about 1.2 to 2.3 KB each, kept as long as the peer; two whole-population sets per locating pass, which every join starts | One shared location per place until the location data changes; locate the joined peers, and every kept peer only when the location data changes |
   | 1 | App: `Peer` | Each peer keeps its own port text and client name, and a known peer its own copy of the same few values | Port text made when a row shows it; one string per distinct client name; one shared record per role and source set for known peers |
   | 1 | App: map selection marks (`PeerMap/Selection.cs`) | Recount on every `PeerVersion` change, which every rate change bumps | `PeerVersion` changes only when a location does, the one input of the count |
   | 1 | App: `Peer.Prepare`, `Peer.Apply` | A set, an array and a change record per changed peer, and a new notice object per raised property | A bit mask per change, and one shared notice object per property |
   | 1 | App: `Torrent.Update` | Any changed field raises one notice for the whole row, so each binding of every visible moving torrent runs again and formats about 20 strings per poll | Raise only the properties whose inputs changed |
   | 1 | App: `Inspector.Refresh` | Raises one notice for the whole inspector per poll, per selection click and per peer reply that changes rows, so every binding of the pane runs again | Raise only the properties whose inputs changed |
   | 2 | TableView: `Body.Projection`, `Table` | About six fresh N-sized hash tables, a sort-key map and a full re-sort per publish | The host names changed rows (`RefreshView(changed)`); the projection keeps a key-to-slot map and per-slot arrays between publishes, checks new rows only for duplicates, and moves only rows whose sort key changed |
   | 2 | App: `InspectorPane` | `RowsUpdated` calls `RefreshView()` | Calls `RefreshView(changed)` with batch 1's changed peers |
   | 2 | App: `Peer.Key` | A torrent id and endpoint string per peer, about 136 bytes, only for the table's row identity | The table identifies a peer by reference, because a swarm keeps one peer object per endpoint for as long as the torrent's peers are kept |
   | 3 | Map: `MapRenderer.Inspect`, `MapWork.Prepare` | Rebuilds the destination dictionary every draw; regroups every observation per update | Keeps destinations between passes; adjusts places by the changed observations |
   | 4 | Engine detail collector, `PipeClient`, [pipe contract](../contracts/pipe-api.md) | Collects one torrent per round trip through the engine's single detail reading, and builds a JSON object per peer before writing | The [communication plan](communication-plan.md)'s steps 1 to 3, which the owner asked on 9 November to implement: per-torrent collection, one subscription for the inspector's scope, and keyed delivery of full replies. Its step 4, incremental row application, is batches 1 and 2. Replies stay full: the 15 October review of that plan rejected change lists because the window would become a second authority that a single lost update makes wrong, and the [pipe contract](../contracts/pipe-api.md) asks for a measured payload problem first. The pause on 17 October rested on gain inferred, not measured, so the work starts with that plan's step 1 baseline, which also times the engine's per-peer JSON building. It mainly helps views of many torrents, which today cost one round trip per torrent |
   | 5 | App: `SpeedGraph.Draw` | Three new path geometries with their figures and segments per draw, and a new geometry and text block per tick | Keep the geometries and tick labels, and update them |
   | 5 | App: `PiecesMap` draw | A new pixel array the size of the whole map per draw | One pixel buffer per size, written in place |
   | 5 | TableView: `Header.Strip` clip; row visual states | A new clip geometry per size change; every realized row updates its visual states on any selection change | One geometry whose bounds change; only rows whose selection or hold changed |
   | 5 | App: `FileNode` (`Controls/FileSelection.cs`) | An empty child list for every file | A child list only for folders |

   The 9 November sweeps of `app/src` and `lib/TableView/src`, one for work
   redone and one for copies kept per element, found the rows above. The engine
   also copies each peer's address into a set beside the JSON; batch 4's writer
   ends that copy with the tree. The torrent table's status cell keeps three
   icons per row, one shown, and stays: a row container makes them once and
   reuses them, and each icon carries its own theme brush, which one icon could
   follow only through brushes chosen in code or visual states in every row. The map's drawing keeps its Win2D brushes, geometries and bitmaps
   between frames; its remaining copies (`Arrangement.Present`) belong to
   batch 3. Batch 5 runs after batch 2, which owns the TableView files until
   then.

   Batches 1 to 3 share one build and one round; batch 4 has its own. Batch 2
   starts from the TableView history rule above: `RefreshView(changed)` existed
   before the 3 November revert. Batches 1 to 4 pass changes as
   [one update, end to end](#one-update-end-to-end) describes.
4. **Find the two-sided objects and why a gen 0 collection takes 23.5 ms.**
   An object that WinUI and .NET both hold, a native WinUI or Win2D object and
   its .NET wrapper, can only be freed by a full collection in which both take
   part, and every one is scanned again in each collection. One collected trace
   of the current build with a heap snapshot in the aggregate phase counts
   them, with handles, finalizable objects and surviving bytes, by owner: table
   rows and cells, event subscriptions on WinUI objects, map brushes and
   geometry, and row objects handed to WinUI. Done when each owner is marked as
   needed (rows on screen) or churn (created and dropped per click, per update
   or per draw), and the gen 0 pause is split into those causes.
5. **Write each change with the milliseconds it removes,** from steps 1 to 4,
   and add them up per endpoint. Where the sum cannot reach a target, the plan
   says so and names the reachable value for the owner. The finish date is the
   number of changes left at this point.
6. **Implement the changes in batches,** each batch with one build and one
   matched round under [deciding a correction](#deciding-a-correction).

The selection correction below and plan 18 Step 1 are already in progress and
run alongside steps 1 to 4.

## One update, end to end

The stage that reads a reply knows which peers changed. Every later stage uses
that knowledge and works nothing out again. Today the parser knows it and drops
it, and then the filter, the table and the map each find the changes again over
all rows. That handoff, not any one stage, is why the waste stayed hidden.

The snapshots stay. Each update makes new row lists and peer records and never
changes them afterwards, so worker threads read them without locks. The changes
travel beside the snapshots.

An independent architecture review on 9 November checked this flow against
the code and settled it:

| Boundary | What passes | Owner |
| --- | --- | --- |
| Engine to app | Every peer of the torrent: one complete reply, so a newer reply replaces an older one undelivered. Batch 4 writes it without an object per peer and without the copy `data.update` makes | Engine, `DescribePeers` |
| Reply to swarm, on a worker | `Swarm.Read` looks up the joined peers' locations, and every peer's when the location data changed. It returns the swarm, which is the same object unless a peer joined or left; the changed peers with their new records and changed properties; the joined and left peers; the swarm's connected and active counts, counted in the same pass; and the view's new list when membership changed, built on the worker | `Swarm`, a torrent's peers kept between replies |
| Swarm to view, on the UI thread | Applies the records, which raise only the changed properties of rows on screen. Publishes the swarm and the list. Sums the counts over the scope. Adds the update to the round's list for the map. Gives the table its one delivery. Recomputes a chosen place's rows only when a join, a leave or a location can change them | `Inspector` |
| View to table | One delivery: the list, a new object only on a join or leave, and the changed rows. The table keeps changed rows until it settles, because the host skips deliveries while the layout is map-only or a replacement is pending | TableView, batch 2 |
| View to map | Once per poll round, and per reply during a subject's first round: the subject, the view's list and the round's updates. The map's worker keeps, per peer, the contribution it last added and its groups, in a dictionary keyed by peer. It takes each listed peer off at that contribution and adds it at its current values, and takes off peers that left | `Inspector` gathers the round, `MapModel` hands it over, `MapWork` keeps the state |
| Locations | Each location found is a changed peer on the same path, and the only change that recounts the map's selection marks | `Inspector` |

The swarm keeps peers in the order they first appeared: a join appends and a
leave removes. The engine lists connected peers first, so the reply's order
changes whenever a peer connects or disconnects; following it would rebuild
the table's rows each time and move rows in an unsorted table under the person.

The map keeps its own copy of what it added, not the update an old record,
because the map skips and merges updates: a superseded preparation, a hidden
map and a cancelled worker all drop some. Taking off what it added and adding
the current values gives the right totals however many updates it missed.
Subtraction is exact: the engine sends rates as whole bytes per second.

The map rebuilds from the view's list on a new subject, map detail, language
or geography, when its worker holds no state for the subject, and when the
round's updates name more peers than the view holds. A place's estimated flag,
which an `|=` sets today, becomes a count of unlocated members, so taking a
peer off can clear it.

A dictionary keyed by peer in the map serves better than a slot number shared
by every consumer: the table cannot use app slots, because the library knows
only `peer.Key` and keeps its own key-to-slot map, so the map would be the one
user. It also ends a defect: `Arrangement` indexes membership by position, so
any join or leave regroups every peer.

## Round of batches 1 to 3 and 5

**9 November.** Builds: plan 18's round 5 new build as the reference
(`keep-up-new5_capture`), and new `keep-up-new6_capture` with batches 1 to 3
and 5. Five pairs of plan 18's replay on the benchmark display, the reference
first. The Roslyn compiler server was busy before the reference runs of pairs
1 and 2. Values are the five pairs in order.

| Endpoint | Reference | New | Verdict |
| --- | --- | --- | --- |
| UI-thread ticks from the first to the last press (G) | 5.42, 4.92, 4.31, 4.98, 4.31 | 5.13, 5.54, 5.29, 5.36, 5.06 | worse: higher in 4 pairs |
| Ticks per click, median, common clicks (M) | 73.7, 26.6, 21.1, 25.7, 59.7 | 34.5, 54.7, 28.8, 17.3, 31.9 | mixed: lower in 3 pairs |
| Click to rendered selection, median, common clicks (ms) | 85, 45, 31, 46, 57 | 84, 82, 87, 27, 43 | mixed: higher in 2 pairs |
| `firstSubjectPictureMs` | 242, 194, 147, 248, 131 | 195, 524, 338, 271, 198 | worse: later in 4 pairs |
| `firstConnectionsMs` | 1908, 1915, 2118, 1870, 1748 | 4022, 3600, 3092, 1136, 2972 | worse: later in 4 pairs |
| Marquee gap, worst (ms) | 404, 564, 348, 701, 283 | 1023, 555, 1062, 232, 514 | worse in 3 pairs |
| Stationary map draw gap, worst (ms) | 402, 645, 216, 114, 77 | 115, 710, 116, 262, 106 | worse in 3 pairs |
| Peak private commitment (MiB) | 1092, 1089, 1215, 1132, 1120 | 1094, 1177, 1123, 1245, 1094 | higher in 3 pairs |

The build is not accepted. Its map waited for a poll round to end before
grouping a new view, which made the peers capture fail on this build: a place
the capture expected was missing. That wait can delay the first subject picture
and the first connections, but the round does not prove it is the whole cause,
and it does not explain the higher UI-thread ticks. The fix shows a new view at
once and gives the map each later round's changes when the round ends. The next
build holds it with batch 4, and its round decides batches 1 to 3 and 5; the
capture fixture answers one torrent at a time, so the replay does not exercise
batch 4.

## Round of batches 1 to 5

**10 November.** Builds: the same reference (`keep-up-new5_capture`), and new
`keep-up-new7_capture` with batches 1 to 5 and the fix of the map's rounds. Five
pairs of plan 18's replay on the benchmark display, the reference first, with
no build or benchmark running before any run. The replay does not exercise
batch 4; the [communication plan](communication-plan.md) records its own
measurement. The peers capture passes on this build. Values are the five pairs
in order; "common clicks" are the clicks both builds selected and rendered.

| Endpoint | Reference | New | Verdict |
| --- | --- | --- | --- |
| UI-thread ticks from the first to the last press (G) | 4.75, 5.03, 4.79, 5.13, 5.06 | 5.15, 5.15, 4.80, 4.97, 4.85 | mixed: higher in 3 pairs |
| Ticks per click, median, common clicks (M) | 39.5, 29.8, 41.9, 26.1, 25.7 | 66.5, 23.0, 21.1, 9.9, 22.3 | lower in 4 pairs; over all 82 common clicks 23.0 against 37.7 |
| Ticks per click, p95, all clicks (M) | 783, 452, 398, 518, 538 | 546, 289, 326, 206, 190 | lower in every pair |
| Click to rendered selection, median, common clicks (ms) | 38, 40, 44, 50, 61 | 82, 76, 74, 54, 68 | **higher in every pair** |
| ... of which the press waited to be handled (ms) | 8.8, 25.4, 21.0, 17.8, 18.4 | 31.0, 47.3, 34.8, 21.5, 11.6 | higher in 4 pairs |
| ... from the handled press to the selection (ms) | 3.7, 2.3, 1.7, 2.0, 1.9 | 0.9, 0.8, 0.8, 0.7, 0.7 | lower in every pair |
| ... from the selection to its render (ms) | 18.5, 12.7, 19.8, 12.3, 25.5 | 29.1, 14.5, 16.3, 4.9, 19.5 | mixed: lower in 3 pairs |
| Click to rendered selection, worst, common clicks (ms) | 617, 353, 277, 284, 288 | 280, 222, 203, 153, 243 | lower in every pair |
| UI-thread response in `torrent-clicks`, p95 / worst (ms) | 383/488, 124/279, 116/296, 126/319, 150/271 | 133/384, 129/278, 103/292, 92/359, 104/330 | p95 lower in 4 pairs; worst lower in 3 |
| Selected but never observed rendered | 8, 2, 2, 2, 4 | 1, 1, 2, 1, 2 | not more |
| `firstSubjectPictureMs` | 327, 171, 207, 144, 158 | 242, 245, 137, 133, 236 | mixed: later in 2 pairs |
| `firstConnectionsMs` | 1333, 2083, 1173, 1758, 1564 | 1638, 1436, 1674, 981, 1168 | mixed: later in 2 pairs |
| Marquee gap, worst (ms) | 490, 500, 341, 358, 218 | 539, 394, 312, 387, 626 | mixed: worse in 3 pairs |
| Stationary map draw gap, worst (ms) | 125, 158, 138, 100, 102 | 258, 118, 111, 138, 98 | mixed: worse in 2 pairs |
| Peak private commitment (MiB) | 1308, 1278, 1134, 1275, 1263 | 1213, 1236, 1256, 1268, 1221 | lower in 4 pairs |
| Peak working set (MiB) | 877, 859, 823, 848, 839 | 885, 926, 920, 932, 891 | **higher in every pair**, by 8 to 97 |
| Peer table switches seen during the clicks | 6, 10, 11, 11, 10 | 13, 11, 12, 14, 13 | more in every pair |

Five pairs of the peer table switch capture with the same builds. Each value
is the median of 12 steps, in megacycles on the UI thread.

| Step | Reference | New | Verdict |
| --- | --- | --- | --- |
| Switch, table and map | 92, 93, 95, 93, 93 | 98, 104, 104, 104, 105 | higher in every pair |
| Switch, table alone | 94, 97, 95, 95, 95 | 93, 91, 93, 92, 93 | lower in every pair |
| Switch, sorted table | 89, 91, 91, 90, 91 | 100, 103, 101, 101, 103 | higher in every pair |
| Poll: five peers leave, five join | 113, 116, 114, 114, 113 | 131, 126, 125, 125, 132 | higher in every pair |
| Idle, no change | 32, 36, 34, 35, 35 | 44, 46, 45, 45, 46 | higher in every pair |

What the records show:

- **The steps with the map shown compare different work.** A managed trace of
  one pair of the switch capture shows the new build's map drawing the peers'
  traffic dots in every second of the capture. The reference's map drew only at
  the start, and it prepared no picture in the switch and poll steps, so it did
  not follow the peers the capture gave the inspector. Every step with the map
  shown, idle included, costs the new build about 10 megacycles more; the step
  without the map costs it 2 to 5 less. At idle the new build's UI thread ran
  app code in 2 of 4,275 samples, so the added cycles are WinUI's and Win2D's
  while the dots fly; a managed trace cannot name native frames.
- **The median click waits in the queue; it does not cost more.** From the
  handled press to the selection takes under 1 ms in every pair, against 1.7 to
  3.7 ms. The press waits longer to be handled. One pair recorded each table's
  change notices (`TINYTORRENT_PERFORMANCE_CHANGES=1`). In it, the new build's
  peers table switched 10 times during the clicks, and the reference's raised no
  notice. Each switch to a torrent with a different number of peers raises one
  notice per row added or removed, because WinUI's list has no notice for a
  range and a reset makes the table flash (`Body.View.ClearItems`): 9,000 to
  300 peers raised 8,700 removals in 48 ms, and 300 to 9,000 raised 8,700
  insertions in 86 ms. Every switch started 10 to 110 ms after its own press
  was handled and before the next press, so none was for a torrent the person
  had already left. The next press came while 4 of the 10 switches were writing
  rows, and waited 43, 17, 16 and 10 ms for the rest. The other 17 presses
  waited up to 326 ms with no table notice in the wait; that work is not
  attributed. The three longest of those waits, at the first clicks, show WinUI
  work with cell measuring in a managed trace of another pair.
- **Collections.** In the traced pair, the new build's animation phase had 2
  collections pausing 112 ms in all, against 6 pausing 365 ms. One pair is not
  a round.
- The peak working set is not attributed.

Under [deciding a correction](#deciding-a-correction), the batches are not
accepted as built: the median click is slower and the peak working set higher
in every pair. The click's own work is lower in every measure, so the cost is
in other UI-thread work that runs while the person clicks. A peers switch that
is already writing rows explains a few waits. The work in the other waits is
named in [batch 6](#batch-6-waste-found-on-10-november).

## Batch 6: waste found on 10 November

**Evidence**, from the saved records of `new5` and `new7` and two heap dumps of
`new7` (step 4):

- **The UI thread is stuck for long stretches during the clicks.** In both
  builds it spends over half of the clicks phase in stretches of 30 ms or more:
  1,845 of 3,057 ms in `new7`, and 1,631 of 3,089 ms in `new5`. The replay's
  driver pings the window every 16 ms, and in almost every click wait the
  window answered nothing for 55 to 331 ms.
  - The stretches hold WinUI layout with `CellsPanel` measuring, the peers
    table's row notices, and collections of 26 to 93 ms.
- **Row notices cost time in proportion to the count change.** The peers table
  raises one notice per row it adds or removes:
  - A switch from all 159,810 peers to the 9,000-peer torrent raised 150,810
    removals in 1,506 ms. This is the freeze the owner sees when peers are
    drawn.
  - Showing all peers raised 20,895 to 57,492 insertions per publish, in 187 to
    581 ms each.
  - A marquee selection raised 21,015 insertions in 246 ms.
- **WinUI wraps every row the table inserts.** Inside each insertion notice,
  WinUI reads the row back (`IList.GetAt`) and wraps it for COM
  (`ComWrappers.CreateManagedObjectWrapper`). So every peer the table ever
  inserts stays held by WinUI.
  - Before the clicks: 385,341 GC handles. 159,810 of them are on `Peer`,
    which is every peer. There are also 165,559 managed-object wrappers and
    205,873 finalizable wrapper releasers.
  - After the clicks: 143,542 handles, 27,397 of them on `Peer`.
  - During the clicks, Win2D radial gradient brushes rose from 6,003 to 12,171,
    and geometries from 14,160 to 20,967.
- **The map rebuilds what it drew before.** After the clicks, the map rebuilt
  959 arcs in 662 ms, 387 ms of it glow, for places it had built before the
  clicks. When the next torrent lacks a place, that place's route disposes its
  arc and brushes (`MapRenderer.PrepareRoutes`).

Two independent code reviews found the rest of the list below. The lead
confirmed each item in the code.

**Fix list.** One agent owns each piece's files, and no other agent edits them.

| Piece | Files | Change |
| --- | --- | --- |
| T, table | `lib/TableView/src`, `lib/TableView/docs` | 1. The count change of a list costs what the screen needs. The positions the list can show change in the update. The remaining insertions and removals apply in slices of a few milliseconds, below input priority, and a newer list or view drops the rest. There is no reset; the `Body.View.ClearItems` refusal stands. 2. One reused args object per notice, if WinUI does not keep the args after the event. Check that first. 3. The host can withdraw a source the table has accepted but not applied, and the rows on screen stay. This replaces `Release(rows)` at retarget. "The next list is the person's change" is one call, not a list of rows. 4. `Source.Capture` sizes its copy from the count. 5. For a list that shares no row with the view, `Slots.Commit` swaps in a key map the worker built. 6. One cached set of realized rows, refreshed from `ContainerContentChanging`. 7. The selection is committed, and row visuals are raised, only when their inputs changed. 8. `Projection.Prepare` takes the cancellation that a newer request cancels. 9. `View.GetSelectedRanges` reads the selection, not every row. |
| W, window | `Shell/MainViewModel.cs`, `Shell/MainViewModel/*.cs`, `Shell/MainWindow.cs`, `Shell/MainWindow/Chrome.cs`, `Shell/MainWindow/Actions.cs`, the `MenuItems` file | 1. `CountStatus` is raised only when its text changes. 2. The status bar is laid out only when a shown text changes. 3. The menus are filled only when their inputs change. A menu-bar menu cannot be filled when it opens, because `MenuBarItem` raises no opening event. 4. A selection change refreshes only the commands that read the selection. 5. The map commands refresh only on their own inputs. |
| M, map drawing | `Torrents/MapRenderer.cs`, `Torrents/MapMotion.cs` | 1. A route that leaves keeps its arc, brushes and strokes in a cache keyed by destination, up to the arc limit, and the cache gives them back when the place returns. 2. The dot, mark and halo sprites are made once per device and DPI, not once per picture. |
| C, peer path | `Shared/Engine/PipeClient*.cs`, `Torrents/Swarm.cs`, `Torrents/Peer.cs`, `Shell/Inspector.cs`, `Shell/Inspector/*.cs`, `Shell/InspectorPane.xaml`, `Shell/InspectorPane.xaml.cs`, `Shell/InspectorPane/*.cs`, `Torrents/MapModel.cs`, `Torrents/MapWork.cs`, `Controls/FileSelection.cs` | 1. A stale observation is dropped before it is copied, and a message is copied at most once. 2. One change record per round feeds both the table and the map, with no copies into further sets. 3. The table is told only about rows whose sorted value changed. The changed rows reset on retarget. The Connection column sorts by its raw values, not by display text. 4. `Swarm.Read` locates joined peers on the worker, so a click's cancellation also cancels the lookups, and only the view's peers feed the view. 5. The map is fed once per round. `RefreshMap` serves only view settings, and `MapModel.Update` raises only what changed. 6. A peer cell's tooltip text is made when the tooltip opens, not on every update, and a row holds no `ToolTip` it does not show. 7. `FileSelection` is made only while Files shows. The Files keys are not rebuilt per snapshot. `ArrangePeers` sets only what changed. `MapWork.Prepare` checks cancellation while it regroups. |

A changed peer still gets a new record. The 9 November review chose immutable
records so that the workers read them without locks; see
[one update, end to end](#one-update-end-to-end).

After T and C finish, the lead connects the inspector to T's new table calls,
reviews every diff with the focused review passes and builds once. The lead
then runs the affected tests and one round against `new7`.

**Predicted endpoints**, each lower than `new7` in every pair:
- the median click to rendered selection;
- the UI thread's stalled time during the clicks;
- the peak working set;
- `firstConnectionsMs` after the clicks.

The heap dump after the clicks should hold no more than a few thousand `Peer`
handle targets.

## Round of batch 6

Batch 6 was built on 10 November as `new8` and measured in one round of five
pairs against `new7`, on the benchmark display with no competing load. Before
the round, every TableView test outside the interactive category (198) and
every peers capture journey passed.

**What changed beyond the fix list.**

- **Table.** T also cut the two costs an independent review of `new7` found:
  - Every held row container was rebound and measured after a switch. Now the
    rows on screen are rebound in the update, and the rows below it later.
  - Every inserted row was wrapped for COM. Now a row that arrives off screen is
    announced as the platform's placeholder, null.
- **Map.** The same review's map findings were fixed:
  - `Paint.Core` allocated a closure on every call.
  - `Inspect` made a new target for every route of every picture.
  - Every drawn picture copied the arc routes into an array.
  - A route that left the shown arcs lost its arc and brushes. These now go to
    the same cache as the routes that leave the subject.
- **Peer path.** C item 6 (tooltips) was left as it was, because a tooltip that
  WinUI registers lazily cannot open on the hover that registers it, and
  Narrator reads the hint text from `HelpText`.
- **Connection sort order.** The Connection column now sorts by connection kind
  rather than by its hint sentence: known peers first, then TCP, uTP, I2P and
  HTTP, and outgoing before incoming. The order is now the same in every
  language.

**Results.** Ticks are the UI thread's cycles from a press reaching it to its
rendered selection.

| Endpoint | `new7` | `new8` | Lower in every pair |
| --- | --- | --- | --- |
| Ticks per click, median of 97 common clicks | 24.6 M | 18.0 M | 4 of 5 |
| Press to handled, median of 97 common clicks | 67.7 ms | 41.3 ms | yes |
| Click to rendered selection, median per run | 27 to 33 ms | 14 to 31 ms | 3 of 5 |
| Click to rendered selection, p95 per run | 116 to 176 ms | 78 to 179 ms | 4 of 5 |
| UI thread stalled during about 3 s of clicks | 1,569 to 1,773 ms | 1,295 to 1,603 ms | yes |
| Peak working set | 905 to 934 MiB | 827 to 846 MiB | yes |
| `firstConnectionsMs` after the clicks | 1,110 to 1,805 ms | 131 to 206 ms | yes |

Three of the four predicted endpoints fell in every pair. The median click to
rendered selection fell in three. `new8` also kept up with more switches: the
peers table showed 13 to 17 subject changes, against 12 to 14. Its UI thread
spent about 5% more cycles over the whole click phase.

**Heap.** A dump of `new8` at the same point as `new7`'s first dump holds 6,584
managed-object wrappers and 19,195 dependent handles, against 165,559 and
178,409. That is 0.19 wrappers per live `Peer`, against 1.04. The dump after
the clicks failed, because `new8` finished its clicks before the script's
fixed wait ended.

**What the stalls still hold.** A managed trace of one `new8` replay found 20
stalls, 1,416 ms of 3,018. The samples inside them are about 2.25 ms each:

| Work | `new7` | `new8` |
| --- | --- | --- |
| WinUI native work with no app frame | 466 | 497 |
| Table cell layout (`CellsPanel` measure and arrange) | 157 | 169 |
| Cell fill (`OnDataContextChanged`, `SyncChildren`) | 14 | about 20 |
| Table row notices | 109 | 0 |
| Collections | 9, 353 ms | 2, 35 ms |

The next step is a native trace of the click stalls. The managed sampler cannot
divide WinUI's layout and rendering, and an ETW trace needs the owner's
elevation.

## Batch 7: remaining waste on 10 November

Built on 10 November. Every TableView test outside the interactive category
(198) passed, and every peers capture journey passed before the table change.

- **A peer push is parsed once.** Before, `PipeClient.Read` parsed each
  observation, copied it whole with `Clone`, and `Swarm.Read` read the same
  bytes again; at 9,000 peers the copy reached the large object heap every
  second. Now the receive thread parses the message once into pooled buffers,
  and `Inspector.ApplyObserved` returns them after every reader of the batch,
  the peers worker included, has finished. An observation that is replaced or
  dropped before it applies returns its buffers too.
- **Torrent cell tooltips.** The status, added and completed cells hold one
  `ToolTip` each and change only its content, instead of getting a new tooltip
  object each time the text changes.
- **The cells panel keeps its own list of cells.** `CellsPanel` is the only
  writer of its children, so it keeps them, with their columns, in a list.
  Measure, arrange and cell fill no longer fetch each cell back from WinUI and
  look up its wrapper. In `new8`'s trace those fetches were about 15 of the 245
  cell samples of the click phase.
- **The torrent snapshot reuses its set of ids.** Each snapshot built a new
  set of every torrent id and scanned every torrent and the selection for
  removals. Now it clears one kept set, and scans only when the engine stopped
  listing a torrent.
- **The status bar measures a rate's width only when its digits change.**
  `ReserveDigits` measured six texts on every status layout, which runs on
  every selection change. It now keeps each panel's text with every digit as
  8 and measures again only when that text changes, or after `QueueStatus`
  for a language or text scale change. `NameButton` sets a name and tooltip
  only when they differ, as `SetStatus` does, because each new tooltip text
  made WinUI build a new tooltip. In `new8`'s trace the status bar held 29
  samples of the click phase, 13 of them in `ReserveDigits`.

**Peers capture**, at 10,000 peers: a push that changes no peer took 5.0 ms,
median of 30, against 12.5 ms in the last capture. The first push, which
creates every peer, took 40.5 and 44.9 ms in two captures, against 11.4 and
20.3 ms before. The rise is the location lookups: creation now looks up each
new peer on the worker, where `LocatePeers` did it later, and the same captures
time 10,000 lookups alone at 36.6 and 39.2 ms. Creation without the lookups
takes about 4 to 6 ms.

The [round of batches 7 and 8](#round-of-batches-7-and-8) measured this batch
as `new9`. The build after the status bar change had no warnings.

**What the stalls are.** The UI thread is busy, not waiting. Its cycle counter
in `new8`'s profiled replay shows 2.74 s of CPU in the 3.0 s click phase, and
in each press that waited 30 ms or more it ran for nearly the whole wait.
Two traces show what it runs:

- The managed trace of `new8`: of 1,461 samples in the phase, 948 hold no app
  code. Of the rest, cell layout and fill hold 261, the table's catch-up of a
  count change 93, `ApplyProjection` 54 and the status bar 29.
- The native trace of 8 November, of an earlier build: the render walk holds
  2,300 of 4,836 stall samples, mostly rasterising the text of changed
  `TextBlock`s; layout-transition copies of animating rows 556; and the
  ListView's container preparation 576. Hit testing, 1,022, is almost all
  layout that WinUI runs before it can test.

So the native work is layout and drawing of rows whose content changed, and
the owner's row transitions. Each notice of a count change costs WinUI's
`OnItemsChanged`, reached through the managed `Surface`, which exists for its
automation peer; the number of notices is the change in count.

A sampler without elevation confirmed this on the current build. It suspends
the UI thread about every 14 ms, copies its stack and walks it with DbgHelp.
Of 118 stall samples in one `new9` replay, a forced full collection held 20,
the render walk 27, text measure 18 and layout transitions 5.
[Batch 8](#batch-8-forced-collections-and-per-update-allocations) found the
source of the collection.

**`Field` stays in the cell templates.** `Field` is a managed control, so WinUI
reaches each cell's measure and arrange through .NET. `new8`'s managed trace
puts 57 samples of the click phase under its base `MeasureOverride` and
`ArrangeOverride`. The native samples show what runs below them:
`CTextBlock::MeasureOverride` and `CIconElement::MeasureOverride`, the measure
of the cell's text and icon, which a cell built from native elements also
does. The crossing itself is a few JIT-compiled calls per cell, which neither
sampler separates from that measure. Replacing the control in nine templates
would not remove the measured work, and would change what Narrator reads from
those cells.

## Batch 8: forced collections and per-update allocations

**What forces the full collections.** All 70 samples of the `new9` replay that
hold `GCInterface_Collect` have one stack: `CCoreServices::TrackMemory` in
`NWDrawTree`. Every 51 render walks, WinUI reads the process working set. When
it has grown more than 50 MiB since its last trigger, WinUI calls
`DisconnectUnusedReferenceSources`, and .NET runs a blocking full collection
of about 100 ms on the UI thread. The app cannot turn this off
([microsoft-ui-xaml#12113](https://github.com/microsoft/microsoft-ui-xaml/issues/12113)),
so it can only grow its working set less.

**Most of that growth was the replay's fixture.** A heap dump in the click
phase held 137 MiB of dead large objects. 74 MiB of them were byte arrays: peer
lists that `PerformanceSource.Peers` read into a new array, copied whole with
`Deserialize<JsonElement>`, and parsed into new metadata, about 5.8 MiB for
each fetch of a 3,000-peer torrent. The product reads engine messages into
pooled buffers and copies nothing.

- **The fixture reads like the product.** `PerformanceSource.Peers` parses the
  reply into pooled buffers and returns the `JsonDocument`. `ReadPeers`
  disposes it after `ApplyPeers`, which awaits the worker that reads it.
- **A map group's name closure is made only for a new group.** `CountryOf` and
  `PlaceOf` captured their `location` parameter in a lambda, so C# allocated
  the closure on every call, once for each peer grouped. `NewCountry` and
  `NewPlace` now hold the lambdas.
- **`PrepareRoutes` keeps its working sets.** The previous routes by key and the
  model's keys were a new dictionary and a new set on each update that changed
  the destinations; they are now fields that are cleared and reused.
- **A new peer's endpoint is parsed once.** The `Peer` constructor parsed it
  for the address and port, and `Inspector.Locate` parsed it again for the
  lookup. `Swarm.Read` now parses it once and gives the result to both; the
  lookup takes an `IPAddress`. The replay's fixture looked locations up by
  endpoint text, so this change shows only in the product.
- **Only a `Field` that dims zeros follows its label.** The `Label` callback
  called into .NET on every label change of all 60 fields; 4 of them dim zeros.

The build had no warnings.

## Round of batches 7 and 8

One round of five sets on the benchmark display, each set running `new8` (batch
6), `new9` (batch 7) and `new10` (batch 8), with no competing load except a
compiler process before set 1's `new9` run. Every run passed.

| Click to rendered selection | `new8` | `new9` | `new10` |
| --- | --- | --- | --- |
| Median, 94 common clicks | 38.3 ms | 36.7 ms | 43.6 ms |
| p90 | 139 ms | 111 ms | 106 ms |
| Worst | 413 ms | 265 ms | 164 ms |
| Worst in each set | 139 to 413 ms | 114 to 265 ms | 116 to 164 ms |

Click by click, the medians do not separate. `new10` was faster than `new9` on
44 of 101 clicks and slower on 57, a median of 7.3 ms slower, with quartiles of
-29 and +43 ms; `new9` against `new8` split 47 to 48. The UI thread's cycles
per click differ by less than 1 M between `new9` and `new10`.

A gc-verbose trace of one replay of each build explains the tail:

| Click phase | `new9` | `new10` |
| --- | --- | --- |
| Large object allocations | 40.5 MiB | 5.7 MiB |
| Forced full collections | 1 | 0 |
| Large object allocations, whole run | 599 MiB | 302 MiB |

`new10`'s five forced collections come while the replay builds large peer
views: two while the all-peers map opens with 159,810 peers, one at the
9,000-peer view, one at the churn map and one in connection churn. That memory
holds the views' peers.

## Batch 9: row rebinds, map redraws and engine copies

Built on 10 November with no warnings. Every TableView test outside the
interactive category (198) passed, every peers capture journey passed, and the
details capture passed with the real engine.

- **A row rebind raises one managed event.** `Body.Row` and its `CellsPanel`
  both handled `DataContextChanged`, so each rebind crossed from WinUI into
  .NET twice, with a new event-argument object each time. `Row` now gives the
  new item to `CellsPanel.ShowItem`. In `new8`'s managed trace the two handlers
  held 126 of the 1,574 UI-thread samples of the click phase.
- **Table updates make fewer throwaway objects.** A table without a hierarchy
  shares one empty set for expanded and expandable rows, where each update made
  four new ones. The check for changed visible rows reuses one list, and makes
  the array it publishes only when the rows changed.
- **A fade no longer forces extra redraws.** Arc strokes are rebuilt within a
  6 ms budget per drawing, and a stroke the budget refused asked for a new
  drawing at once. During a fade every step changes many widths, so the map
  drew at every frame: 37 to 59 drawings in the idle second of the last round.
  A width that a fade changed now waits for the fade's next step, which draws
  anyway.
- **The glow is recorded once per fade step.** It was recorded again on every
  drawing while a fade ran. Its kept inputs now hold the fade's progress.
- **Widths left at a fade's end are rebuilt without drawing.** After the two
  changes above, `new11`'s idle second still drew 44 times. A CPU trace of its
  render thread was busy in 467 of 503 samples, 275 drawing cached strokes and
  89 creating them: the budget is a deadline, and recording the unchanged arcs
  used most of it, so each drawing rebuilt a few strokes and drew the whole map
  again. The arcs that the fade's last drawing leaves at an earlier width now
  wait in a queue. Passes that draw nothing rebuild them, as they already did
  for stale border strokes, and the map draws once when the queue is empty.
- **The engine copies less.**
  - A new view's observations no longer wait behind the old view's unsent
    ones. `Detail.Subscribe` withdraws them, because the window drops them;
    the [pipe API contract](../contracts/pipe-api.md) says so.
  - A snapshot is moved into its last subscriber instead of copied.
  - A torrent row reads its hashes and status once.
  - A message reserves its full size before it is written, so a large peer
    list is not copied as its text grows.
  - The problem scan of each turn skips the folder-root work when no root
    hangs, which is the normal case.

**Measured and not changed.**

- **`Field` in the cell templates.** Of the 1,574 click-phase samples, about 2
  are the crossing into .NET. The rest is text measure, which native cells also
  do; see [batch 7](#batch-7-remaining-waste-on-10-november).
- **Small sets, arrays and LINQ objects per click.** The allocation trace of
  the click phase holds 0.0 MiB of them, so removing them cannot be measured.
- **Count-change notices.** Each costs 3 to 4 crossings between .NET and WinUI,
  because the table's list is a .NET object that WinUI calls for its automation
  peer.

**Left out, with the reason.**

- Skipping unchanged peer lists: connected peers' rates change at every
  refresh, so an unchanged list is rare.
- Copying piece bitfields: libtorrent copies them inside `3rdParty`, which
  agents do not change.
- One snapshot for several commands: a command's reply promises a published
  snapshot that holds its result.
- Describing a view once for several kinds of observation: the owner's
  ruling shows each available fact at once, and how often the kinds arrive
  apart is not measured.
- Shrinking pooled buffers: WinUI's forced collection follows working-set
  growth, not size, and shrinking would add work.
- Not copying each new row list in TableView: the copy is the library's
  documented behavior, and it costs about 1 ms per update only at 160,000
  peers.

**Correction to batch 7.** Batch 7 explained its slower first push by location
lookups. That does not hold: the capture's `PeerRows` use `10.x` addresses,
which the location data does not hold, while `lookupMs` times public `87.68.x`
addresses in a separate loop. `createMs` is one sample per capture, and it has
been 9 to 45 ms across captures of the same code, so the capture cannot show
a change of a few milliseconds. Batch 8's single endpoint parse saves about 1
to 2 ms per 10,000 new peers, which source review shows and this capture cannot.

**The selection review failed on every build from `new7` on**, with "Deferred
inspector refresh lost the final selection", and last passed on 30 October.
The failure did not depend on batches 7 to 9; batch 10 found its causes.

`new11` is the batch without the queue of arcs left at an earlier width;
`new12` has every change.

## Round of batch 9

One round of five sets on the benchmark display, each set running `new10`
(batch 8) and `new12` (batch 9), with no competing load except a compiler
process before set 1's `new10` run. Every run passed.

| Map drawings without dots, per set | `new10` | `new12` |
| --- | --- | --- |
| Idle second (`selected-map-baseline`) | 54 to 59 | 5 to 6 |
| Peer sorting | 267 to 322 | 55 to 69 |
| Torrent clicks | 71 to 104 | 67 to 92 |
| Torrent marquee | 40 to 73 | 49 to 55 |

| Click to rendered selection | `new10` | `new12` |
| --- | --- | --- |
| Median, 101 common clicks | 27.9 ms | 43.4 ms |
| p90 | 87.5 ms | 87.3 ms |
| Worst | 223 ms | 189 ms |

Click by click, the builds do not separate. `new12` was faster on 45 of 101
clicks and slower on 56, a median of 2.8 ms slower, with quartiles of -12.2 and
+31.5 ms. The UI thread's cycles per click differ by a median of 0.0 M, so the
map's extra drawings were not work of the click. The pooled medians move more
than the paired clicks: `new10`'s median was 43.6 ms in the last round and is
27.9 ms here.

The peers capture of `new12` passed all 36 journeys. At 10,000 peers a push
that changes no peer took 3.9 ms, median of 30, against 5.0 ms in batch 7.

**What the click phase still does.** A sampled trace of one `new12` replay
holds 1,508 UI-thread samples in the 3.2 s click phase:

| UI-thread work | Samples | Why it stays |
| --- | --- | --- |
| WinUI's input, layout and rendering, no app frame | 960 | WinUI's own work |
| Cell measure and arrange (`CellsPanel`) | 246 | The text and icon measure that native cells also do |
| Count-change notices (`Table.CatchUpSlice`) | 114 | See below |
| Applying a prepared projection (`PrepareView`) | 56 | Changes the rows the person sees |
| Row rebinds (`Row.OnItemChanged`) | 20 | Shows each row's new item |
| Inspector bindings, status bar, replay driver | under 15 each | |

Six collections paused the UI thread for 132 ms in the phase, against 11 and
323 ms in `new8`'s trace.

The count-change notices are the platform's: `IBindableObservableVector` has
no range notice, so a list that changes by 1,000 rows takes 1,000 notices, and
the owner's ruling forbids a Reset of a displayed list. Of the 114 samples, 43
are under WinUI's `OnItemsChanged`. The managed sampler does not split the rest
between the list's native handling and CsWinRT's marshaling of each notice,
which makes 4.9 of the 5.4 MiB of `ComWrappers` objects in batch 8's
allocation trace of the phase. Each notice reaches .NET through `Surface`'s
`OnItemsChanged`, because `Surface` is a managed `ListView` for its automation
peer. The notices run in slices of at most 4 ms below input, so a click waits
for at most one slice. Of the four `ComWrappers` calls each notice makes, the
table's own is the check in `View.Raise` that no listener kept the reused
arguments; the other three marshal the delegate's arguments and the override's
round trip. Holding the arguments' COM pointer instead would save that one
lookup per notice at the cost of a reference the view must release. In
`new17`'s traced replay, source lines put that check at 2 of the click phase's
1,560 UI-thread samples, and the call of WinUI's handler beside it at 32, so
the check stays.

The same trace splits the phase's 122 notice samples by their innermost
managed frame:

| Notice work | Samples |
| --- | --- |
| WinUI's list inside the base `OnItemsChanged` | 45 |
| WinUI's native handler of the notice, inlined into `View.Raise` and `CatchUpSlice` | 54 |
| `Surface`'s round trip as a managed subclass: WinUI's interface query and the override's marshaling | 13 |
| Marshaling the arguments | 7 |
| GC polls | 3 |

The table's own code is not measurable here. The only alternative that removes
work is a list that is not a managed subclass, which would save at most 13
samples and lose `Surface`'s automation peer, so `Surface` stays. A click that
selects a torrent without kept peers must show no rows until its peers arrive,
as the [peer-location contract](../contracts/peer-location.md) says, so the
peers table goes to no rows and then to the new rows. `View.CatchUp` raises
only the notices the list is still owed, so when the new rows arrive before
the removals are announced, only the difference is announced.

All 151 samples in the cells' own measure are one line, WinUI's `child.Measure`
of each cell; the panel adds nothing of its own.

The six collections in `new17`'s click phase are all started by allocation,
three of generation 0 and three of generation 1; WinUI's forced collections
(`DisconnectUnusedReferenceSources`) all fall outside the clicks. `new10`'s
allocation trace puts 40 MiB in the 3 s phase. Its largest app sites are the
peers worker's `Swarm.Read`, 8.6 MiB, which allocates only for peers that join
and finds a held peer by its endpoint text without allocating; the map's
groups for each new subject, about 5 MiB; and the `ComWrappers` object .NET
makes for each notice crossing, 5.4 MiB. Each serves the click. Reusing map
groups between subjects would save at most about an eighth of the phase's
allocation, and a reused group would have to keep its point and its name's
language exactly, so it is not done.

A traced replay of the build with every change of this round (`cpu19`) holds
1,543 UI-thread samples in the click phase: 977 in WinUI with no app frame,
230 in cell measure (202 of them WinUI measuring a cell), 115 in count
notices, 59 applying prepared projections, 54 in cell arrange and 20 in row
rebinds. A selection change measures no cells: a row answers it only in
`Row.UpdateStates`, which changes visual states only when the row's drag or
held state changes, so the measures are of rows whose values changed. About
42 of the 59 projection samples are the notices `View.Apply` raises for the
realized rows; `Slots.Commit` holds 3. Six collections paused the UI thread
for 112 ms.

The map's render thread was busy in 393 of its 1,508 samples: 255 drawing,
including 56 rebuilding strokes for a new subject's arcs, and 122 preparing
routes. Source lines put 99 of those in `PrepareRoutes` itself: 37 adding the
previous routes to the lookup by key, 27 checking whether the destinations are
unchanged, 17 in the loop over destinations and 13 parking arcs that stop
showing. The sorting phase, which updates the same subject's 9,000 peers for
6 s, holds 1 sample of it, and the idle second none, so the cost comes with
the clicks' changes of subject. The lookup lets a new subject's places reuse
the arcs of places the previous subject shared. Drawing reads each route's
displayed strength by key in 1 sample of the phase.

## Batch 10: route preparation after a click

A click shows the new subject with no destinations until its peers arrive, and
its peers then get their locations in batches. Each of those updates changed
the destinations, so `PrepareRoutes` put every route into its lookup by key,
also when the model had no destination to match, and rebuilt a set of every
model key to drop the displayed strengths of routes that had left.

- With no destinations, the routes leave without the lookup.
- A route that leaves drops its own displayed strength. Countries keep theirs
  in a dictionary by code, so their cleanup no longer scans every route's key.
- The routes up to the first changed destination keep their positions, and
  only the rest are matched by key. The check compares the destination objects
  first, because the model reuses each destination that did not change, and a
  place that gets its location is usually listed after the others.

- **The map keeps its last picture while a new subject's rows are unread.**
  The [peer-location contract](../contracts/peer-location.md) asks for this,
  and `Inspector.GiveMap` followed it, but `Inspector.UpdateMap`, which the
  pane's refresh calls after each click, gave the map an empty population and
  so released every arc only to build them again. Both now ask `KeepsLastMap`.

The builds had no warnings. In one traced replay of each build, route
preparation held 122 samples of the click phase in `new12`, 79 after the first
two changes above (`new13`), 68 after the third (`new14`) and 57 with the kept
map (`new15`); rebuilding strokes fell from 53 to 43 samples between `new14` and
`new15`. Single traces differ by about 10 samples from run to run. Every replay
passed, and the peers captures of `new14` and `new15` passed every journey,
including the retained route of "map retained aggregation and live fade".

- **A kept arc returns only to a route that draws it.** `PrepareRoutes` took
  every returning place's kept arc and, when the place did not draw, parked it
  again at once, which also made an unused arc the newest, so the arc limit
  released arcs used more recently first. The shown destinations are now chosen
  before the loop, in a set kept between updates, and only their arcs return.

In `new17`'s traced replay, parking no longer appears in route preparation. Its
41 own samples are matching the previous subject's routes by key, so places
the subjects share keep their arcs (26), and the loop over the new destinations
(14). The replay and every peers capture journey passed.

**Round of batch 10.** Five sets on the benchmark display, each running `new12`
(batch 9) and `new15` (batches 9 and 10), with no competing load. Every run
passed. Click by click the builds do not separate: `new15` was faster on 54 of
103 clicks and slower on 49, a median of 0.4 ms slower, with quartiles of
-34.7 and +28.8 ms, and the UI thread's cycles per click differ by a median of
-1.6 M. The map's drawings per phase overlap between the builds, because these
changes make each update cheaper, not fewer.

**The selection review passes again**, in all six sections and in its peer
refresh and shared-lifetime journeys. It had three faults:

- Its check read the inspector's target after a wait that returns at once and
  one Low-priority layout, before the frame-deferred `QueueSubject` follows the
  selection. It now waits for the final row.
- It set the table's selection while the table prepared the view of the new
  filter, so the table held the selection as pending, the model's selection
  stayed empty, and Properties could not open the inspector. A hidden
  inspector does not follow the selection. The review now applies the
  selection through `SelectTorrent`, as the other reviews do, and fails at once
  when the inspector is hidden.
- **Every engine snapshot queued an inspector read that did nothing.**
  `Inspector.Observe` asks for a read once a second, and for every section but
  Speed the read returned at once because the subscription already served the
  view. While that read waited for its frame, `IsLoading` was true, and the
  inspector's change notice runs just before it, so in the Peers section every
  notice reported loading. `QueueRead` now asks whether the subscription
  serves the view, so a served view queues no frame and reports no loading.

## Batch 11: one halo brush per colour

The second independent review of 10 November found that a new map creates two
gradient brushes per arc in its first drawing. When the 159,810-peer list
opened, the render thread spent 590 samples in
`CanvasRadialGradientBrush.CreateWithStops`, and 2,001 brushes were alive at
1,000 arcs.

- **The glow's halo brush is shared by colour.** The brush depends only on its
  colour, and the map draws at most four colours per palette: download,
  upload, and the grey of each when a selection dims a route. Each arc made
  its own brush. The map now keeps one brush per colour, and the glow sets each
  arc's centre, radius, opacity and scale before it draws the arc. A command
  list keeps a radial brush by value and only references its gradient stops,
  which never change
  ([ID2D1CommandList](https://learn.microsoft.com/windows/win32/api/d2d1_1/nn-d2d1_1-id2d1commandlist)),
  so each arc still gets its own halo.
- **The core brush stays one per arc.** Its stops put the landing fade 30
  pixels from the destination, so they depend on the arc's length on screen.

Built on 10 November with no warnings (`new18`). No capture has counted the
brushes.

**Left out, with the reason.** Batch 9 records the review's engine items and
pooled buffers. The rest:

- The scans that every drawing repeats in `DrawScene` and
  `MapMotion.RenderMap` (the marked routes, the estimated countries, the chosen
  destination and a new `Highlight`): since batch 9 an idle second draws 5 to 6
  times, so each scan passes over about 1,000 routes a few times per second.
- `Swarm.Join` copies only for a scope of several torrents; one torrent's
  view is its swarm's own list. A swarm rebuilds its endpoint index only for a
  reply that adds or removes a peer. The index holds positions, and a leave
  shifts every later position, so an update in place still touches most
  peers. The new swarm also keeps a worker that reads the old one safe. Both
  run on the worker.
- TableView's `Slots.Number` reads each row's key once, only for a list it has
  not numbered; matching rows needs the key. `ClearNext` is one fill of an
  integer array, about 640 KB at 160,000 rows. `Flip` writes each row of a
  different list into the view once and raises no notice per row; the view
  must hold every new row.
- The owner of about 800 MB of native memory at the stress load is not known.
  Only a native heap trace of a running build can name it.

## Round: HEAD against the working tree

On 10 November the owner asked for HEAD against the working tree. HEAD
(`bef7363`) was built in a separate worktree and ran under its own harness,
because its engine speaks pipe version 38. Its replay fixture is the one before
batch 8, which leaves large arrays that the product does not make, so HEAD's
collections are somewhat worse than its product code alone. The working tree
is `new18` (batches 1 to 11). Five pairs on the benchmark display, alternating,
with no competing load. One more pair replaced pair 2, whose working-tree run
stopped before the benchmark: the harness read `ready.json` while the app was
still writing it, and the file ended at byte 8,192.

| Measure, median of five runs | HEAD | Working tree | Working tree better |
| --- | ---: | ---: | ---: |
| Click to rendered selection, median | 66.2 ms | 37.7 ms | 3 of 5 |
| Click to rendered selection, worst | 452 ms | 153 ms | 5 of 5 |
| UI-thread cycles per click, median | 54.8 M | 26.0 M | 4 of 5 |
| All-peers map, first picture | 8,646 ms | 4,610 ms | 4 of 5 |
| All-peers map, UI thread's worst response | 1,012 ms | 399 ms | 5 of 5 |
| Selected torrent's map, first picture | 3,825 ms | 484 ms | 5 of 5 |
| Map after the clicks, first picture | 1,596 ms | 212 ms | 5 of 5 |
| Marquee, worst gap | 388 ms | 202 ms | 5 of 5 |
| Peak private memory | 1,243 MiB | 964 MiB | 5 of 5 |
| Peak working set | 843 MiB | 740 MiB | 5 of 5 |
| Pan back during churn, UI thread's worst response | 224 ms | 287 ms | 0 of 5 |
| Pan back during churn, worst navigation gap | 232 ms | 283 ms | 1 of 5 |

The other docked zoom and pan phases are mixed. The pan back is worse in
every pair. In 4 of the working tree's 5 runs private memory falls during that
phase, by 4 to 11 MiB, so a collection freed memory there; HEAD's falls in 2 of
5. The likely cause is that the lower memory moves WinUI's forced collection,
which follows 50 MiB of working-set growth, into that phase. This is not
proven: a collection trace of one run of each build would show it.

## Committed: map selection preparation

Source review at `4f069bb` found one population-sized UI path left: both map
callers copied the table's selection on the UI thread, `PeerMap` compared every
selected peer and location on every map update, and selecting all 159,810 peers
made those UI operations population-sized. Ordinary torrent clicks do not take
this path, so the standard click comparison cannot validate it.

The correction is committed at `6f5f091`, and its conditions hold:

- Conditions 1 and 2, by source review of the current code. Only the map
  listens to the peers table's selection; `PeerMap.Select` stores the table's
  `Selection.Items`, `PeerMap/Selection.cs` counts on the thread pool, and the
  render thread's `Marks` reads destinations, not peers.
- Condition 3, the map lifecycle capture with its stale-selection tripwire,
  passes all 12 journeys. It had failed since 6 November before reaching the
  tripwire, at "Equivalent peer facts redrew the same map": the rework that
  moved the map's facts to its workers (`fc0916d`, `6528d57`) dropped the
  comparison of a new snapshot's facts with the last, so every new population,
  such as a return to a subject's peers, built a new presentation, and the
  render thread inspected and published the same picture again.
  `MapWork.Prepare` now keeps the last presentation when the new one draws the
  same places with the same facts in the same language
  (`Presentation.Matches`, which compares groups, not peers). The peers
  capture passes all its journeys with it.
- The select-all on the stress fixture. The selection replay now has a
  `peer-select-all` phase: Ctrl+A's `SelectAllFromKeyboard` on all 159,810
  peers, until the map has marked their 1,000 places and drawn them. In its
  traced run the app's map code holds 3 of the phase's 165 UI-thread samples.
  The rest was TableView's commit, which looked up every selected row's view
  position three times and sorted the positions three times:
  `BuildSelectedPacket` ordered the rows by position, `RefreshContainers`
  looked the positions up again into hash sets to find the changed rows, and
  the list's `GetSelectedRanges` reads them once more. The packet's sort now
  hands its positions to `RefreshContainers`, which merges them with the
  previous selection's sorted positions. The phase took 327 ms with 165
  samples before and 225 ms with 114 after; position lookups fell from 43
  samples to 15 and `RefreshContainers` from 38 to 16. The 25 selection tests
  pass. What is left is one pass each: the selection's set, the packet's
  lookup and sort, and the list's own request for the selected ranges, which
  can come after the view has changed and so reads the positions itself.

The conditions were:

1. Source. Both map callers pass the table's immutable selection without a UI
   copy. `PeerMap/Selection.cs` alone counts selected places on the thread pool
   with one active task and one latest pending request, independent of
   grouping. No `Peer` reference enters `MapMotion.Input`;
   `Presentation.Marks` only resolves destinations and estimated regions. The
   UI accepts a result only while the peer generation, selection reference,
   subject, detail and map lifetime still match; clearing publishes empty at
   once; unload never joins the task. TableView, geometry, the renderer and the
   picture handoff are unchanged. The
   [peer-location contract](../contracts/peer-location.md) carries the new
   selection text in the same change.
2. Review. No map-specific selected-peer enumeration remains in UI or
   presentation callbacks, pending work is bounded, and rapid select/clear,
   subject or detail replacement, location mutation during capture, blocked
   grouping and unload are traced.
3. Validation, one collected build. The existing map lifecycle capture passes
   with its stale-selection tripwire: block the worker, replace and clear the
   selection, release, and obsolete marks are rejected. A select-all in the
   peers table on the stress fixture highlights the selected places with no
   peer scan sampled on the UI or game-loop thread.

The guaranteed result is the removal of these synchronous scans. It does not
claim shorter GC pauses or a large-selection latency figure.

## Committed: row keys and sort keys read on the worker

What is established. A blocking full collection on the UI thread overlaps most
of the worst stationary gap: 374 of 381 ms at `75af850` and 210 of 225 ms at
`4f069bb`. At `555bd62` one overlapped 539 of 611 ms of the worst marquee gap;
that run's stationary overlap was not saved.

The trace identifies the collection's managed caller as the .NET
reference-tracker host (`GC/Triggered` under
`ReferenceTrackerHost.IReferenceTrackerHost_DisconnectUnusedReferenceSources`
on the UI thread). Its native trigger is not confirmed; WinUI's working-set
growth heuristic (`CCoreServices::TrackMemory`, 50 MiB) is a source-backed
candidate. The 1,105,232 handles and 200,468 finalizable objects come from GC
163 in the `555bd62` trace, before the runtime bookkeeping correction at
`75af850`; they are a historical sample, not a current inventory, and do not
show that every object is a COM wrapper.

Allocation owners, from the `78fb43f` allocation trace (sampled tick amounts,
not a census), read by owning frame from its raw ticks and by phase in its
preserved [phase grouping](../../artifacts/evidence/ui-profile-alloc-current-20261106-78fb43f/phase-grouping.md).
The journey is 59.3 s; its aggregate phase is 13.6 s with the whole population
loaded.

| Owner | Thread | Sampled allocation over the journey | Standing |
| --- | --- | ---: | --- |
| `Table.CaptureProjection`: the memoized row-key map, built by calling the host's key selector once per row | UI | 184 MiB of entries, 33 MiB of buckets | Moved to the worker by this round. The peers table sets a key, so every capture read 159,810 keys on the UI thread into a map grown from zero capacity. |
| `Schema.SortKey` capture: the sort-key map, built by calling the host's sort selector once per row | UI | 19.5 MiB of entries, 3.8 MiB of buckets | Moved to the worker by this round. |
| `Body.Projection.Prepare`: uniqueness and source-key sets, position map | worker | 198 MiB of set entries, 139 MiB of position entries, 78 MiB of buckets | Sized from their counts by this round, which removes the doubling chains; they never blocked the UI. |
| `Body.View.Apply`: COM-wrapper holder arrays | UI | 51 MiB | The .NET 10 duplicate-bookkeeping defect, removed by the runtime option at `75af850`. |
| `Peer[]` replacement lists in `ApplyPeers` reconciliation | worker | 119 MiB in the aggregate phase | No correction selected. |
| Inspector `Filter` capture | worker | about 24 captures in the aggregate phase, 1.7 per second | One 4.9 MiB value array per capture. The fixture delivers about one reply burst per second, so coalescing captures per burst cannot move an endpoint here. |
| `MapModel.Update` per-peer objects | worker | 209 MiB | Removed at `fc0916d4` and `6528d57`. |
| `Dictionary<DestinationKey, Destination>` rebuilt by `MapRenderer.Inspect` every render pass | render worker | 137 MiB | About 1,000 entries, generation 0, at pass rate during navigation. |

Issues [573](https://github.com/SynoSoftware/TinyTorrent2/issues/573) and
[578](https://github.com/SynoSoftware/TinyTorrent2/issues/578) are therefore
ordinary-use code fixes, judged as code: the per-reply recapture is real when
the engine answers one torrent at a time, and the geolocation sets are real
when geolocation runs, but this fixture shows neither. They ride in the next
build without a predicted endpoint.

The change, committed in `2dec906`,
[issue 579](https://github.com/SynoSoftware/TinyTorrent2/issues/579): the
projection worker reads the host's row key and sort keys itself, once per row,
from the source snapshot the UI captured. `Table.CaptureProjection` captures
only UI-affine state (the source snapshot, displayed rows, realized indices,
hierarchy state and holds) and passes the raw selectors; `Body.Projection`
memoizes the keys and builds the sort comparer, and sizes its collections from
the counts they fill. One UI-thread read stays: a sort or layout request reads
its keys once when it is made, so a request whose selector fails is rejected
before the table changes, as the contract and its tests require; every later
projection of the accepted sort, which is the per-update path, reads on the
worker. Hosts are unchanged. The worker takes detached inputs, a concrete
blocking cost leaves the UI thread, and ownership is clearer, since the worker
now prepares everything from one captured snapshot.

The contract changes with it: key and sort-key selectors run on the worker and
read only state the host publishes safely, never UI objects. Every host
selector was audited: the peers table's key and sort keys read readonly fields
and the volatile record snapshot the map already reads off-thread; torrent and
tracker selectors read plain properties written on the UI thread, where a
stale read sorts a row by its previous value until the next update; file nodes,
Library entries, the sample and the tests read plain or immutable data.

Every build since `2dec906` holds this change, plan 18's round 4 reference
included, so it has no separate round: [step 4](#steps)'s trace of the current
build measures the collections with it.

Issues 573 and 578 against the current code:

- 578 no longer applies, and it is closed: `LocatePeers` and its two
  whole-population sets are gone since `6c6f57b`. `Swarm.Read` looks a peer up
  when it joins, and again only when the geolocation data changes.
- 573's capture for the map is gone: the map takes a round's changes once,
  when the round ends. Its remaining case was a chosen place: every reply that
  changed the rows started its own search of the whole population for that
  place's rows, and every search but the last was discarded. `Inspector.Filter`
  now runs one search at a time; a call during it leaves the search to one
  that follows it with the latest peers and place. The map capture's "peer
  worker cancellation and latest filter" journey, which changes the place
  during a search, and every peers capture journey pass.

## Follow-ups, not steps

Each becomes a task only by the owner's scoped decision, with its own predicted
endpoint and completion condition.

- Presented-frame endpoint (DXGI or DWM present events) in the harness. It is
  the precondition for claiming displayed frame rate or next-frame selection,
  not a work stage.
- Shared-device contention between offscreen rasterization and the game loop's
  presents is a possibility, not a stage; any second device or smaller render
  unit needs it measured first.
- Sure source redundancies without measured savings:
  [575](https://github.com/SynoSoftware/TinyTorrent2/issues/575) (bitmap wraps
  drawn outside the view; it touches the measured draw path, so after the next
  round) and [576](https://github.com/SynoSoftware/TinyTorrent2/issues/576)
  (small per-frame redundancies, including the destination dictionary that
  `MapRenderer.Inspect` rebuilds every pass). Each names its predicted endpoint
  when it joins a build. Issue 574 was closed as invalid: `SequenceEqual` stops at the
  first difference and the unchanged path allocates nothing.
- Code shape: [577](https://github.com/SynoSoftware/TinyTorrent2/issues/577),
  splitting the picture pool, sprites and markers out of `MapMotion`.
- Perceived halo softness of the location lights is visually unverified.

## Deferred TableView findings

Correctness findings from the selection review, outside this round and owned by
TableView (`Table/Selection.cs`, `Body/Surface.cs`, `Table/Reordering.cs`,
`Order.cs`):

- Internal `SelectedItems` reads the public getter, which can return a pending
  future-view request, so accessibility, context menu, drag and invocation can
  consume pending intent as the current selection.
- UI Automation actions use the public setter, which defers during view
  preparation, while pointer selection commits at once.
- A selection callback during projection can queue a correction while
  `_preparingView` is true, and same-version completion need not drain it.
- Repeated `CanStartDrag` queries enumerate the host's order until a second
  queue participant is found; the proposed repair is one cached capability in
  `Order` with a narrow `Table.RefreshOrder()` refresh on every unpublished
  row-change path.

None of these is established as a cause of the measured stalls.

## Historical results

| Record | Content |
| --- | --- |
| [Map handoff functional run](../../artifacts/evidence/Capture-map-handoff-d220f8ff-add3-44be-adfb-cc33004a45fd/captures/review.json) | Eight journeys passed at `e723ae6`. |
| [Replay baseline `b20b923`](../../artifacts/evidence/ui-benchmark-replay-baseline-20261106-b20b923/README.md), [corrected observer `ba64259`](../../artifacts/evidence/ui-benchmark-draw-observer-baseline-20261106-ba64259/README.md) | Frozen references. |
| [Peer snapshot](../../artifacts/evidence/ui-benchmark-peer-snapshot-20261106-2beb6e7/README.md), [scope retention](../../artifacts/evidence/ui-benchmark-scope-retention-20261106-3d2970a/README.md), [notification attribution](../../artifacts/evidence/ui-notification-attribution-20261106-deb3e1e/README.md), [bindable notifications](../../artifacts/evidence/ui-profile-bindable-20261106-222e88d/README.md), [sort capacity](../../artifacts/evidence/ui-profile-sort-capacity-20261106-78fb43f/README.md) | Table-side attribution rounds. |
| [Allocation trace `78fb43f`](../../artifacts/evidence/ui-profile-alloc-current-20261106-78fb43f/README.md) | Allocation owners behind the next round. |
| [Thread work `fc0916d4`](../../artifacts/evidence/ui-profile-map-thread-work-20261106-fc0916d4/README.md) | Relocation demonstrated; response mixed. |
| [Marker preparation `555bd62`](../../artifacts/evidence/ui-profile-marker-preparation-20261106-555bd62/README.md) | Acceptance failed; GC first identified. |
| [Runtime bookkeeping `75af850`](../../artifacts/evidence/ui-profile-runtime-bookkeeping-20261106-75af850/README.md), [delay attribution](../../artifacts/evidence/ui-profile-runtime-bookkeeping-20261106-75af850/plan17-reviewed-delay-findings.md) | Retained; mixed. |
| [Defined changes `4f069bb`](../../artifacts/evidence/ui-profile-map-defined-changes-20261107-4f069bb/README.md), [build](../../artifacts/evidence/ui-build-map-defined-changes-20261107-4f069bb/README.md) | Current reference; mixed. |
| [Country full-screen run](../../artifacts/evidence/ui-country-fullscreen-20261107-4f069bb/comparison.json) | Visual review of the accepted artifact. |
| [Plan before this rewrite](../../artifacts/evidence/plan17-rewrite-20261107/plan-before-rewrite.md) | The full investigation record through 7 November, with the former HTML review beside it. |
