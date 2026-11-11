# Rendering and selection

Status, 11 November 2026. The map handoff from `e723ae6` is implemented and
functionally validated; the owner reports much better pan/zoom. Preserve it. At
`4f069bb` every movable preparation already runs off the UI thread and the game
loop: Inspector's peer capture runs inside its worker, `MapModel.Update` stores a
population reference, picture resources are created and retired on the render
worker, traffic membership and dot input are built in `MapRenderer`, marker
preparation runs in the render pass, `MapWork.Prepare` runs on the pool and
`OnDraw` publishes one displayed value. No further "move it to a worker" task
exists; what remains on those threads is presentation or WinUI-owned work.

The batches of the earlier fix list (1 to 12) are built and measured. Their
findings and the changes that were not made are in
[Questions already investigated](#questions-already-investigated), and their
rules are in the contracts.

The work is open.
[What still interrupts the person](#what-still-interrupts-the-person) lists
it, and the [hard lines](#hard-lines-that-waste-memory-and-cpu) list the waste
that the architecture causes.

Purpose, in the owner's words: "all I want is this to be as fast as possible."
On 11 November the owner made it precise: "we're not looking here for faster
execution but for less jagged movement and more responsive interface". On the
same day the owner rejected the fixed limits at the stress load that this plan
had carried, which agents had written: "my targets were never miliseconds at
160k peers. my targets were smooth and immediate feedback of the user
interface.... that is not impossible to achieve because I'm not asking to
render what you cannot., I'm asking to start rendering immediatelly what I ask,
best effort basis. I also ask to stop wasting memory and cpu because of
architecture that draws hard lines when it shoudn't". So a change is judged by
what the person feels: how soon the UI answers an input, whether what was asked
for starts to show at once, and whether motion stays even while the content
fills in. Execution time and memory count as causes of those, and as waste.
The plan promises no time by which the stress load's 159,810 peers are
complete. Scope: from a click or an update to the picture
on screen, across the app, the pipe and the engine. The replay feeds the app
from a synthetic peer source, so it never measures the engine or the pipe.
Current behavior is owned by
[architecture](../contracts/architecture.md),
[peer location](../contracts/peer-location.md),
[torrent Inspector](../contracts/ui/torrents.md) and
[TableView](../../lib/TableView/docs/tableview-contract.md). The
[harness guide](../../app/tests/Performance.md) is the measurement entry point.

## Goal

The person's own input is answered at once. What they asked for starts to show
at once and fills in as it gets ready. Nothing that fills in stops the next
input or the motion on screen.

1. **Feedback first.** A press, click, selection, drag, wheel step, key or
   scroll shows its own answer in the next frame the display shows: the row
   shows selected, the marquee follows the pointer, the map moves under the
   hand. The answer waits for nothing that the input asks to be loaded,
   grouped, sorted, laid out or drawn.
2. **Start at once, best effort.** What the person asked for starts to appear
   with whatever is ready: the kept rows of a torrent seen before, the land
   before the arcs, part of a list before the whole. It then fills in. A
   torrent or peer selection does not await grouping, projection preparation,
   geometry, rasterization or a replacement picture. The map keeps presenting
   completed content while replacement content is prepared, and a completed
   handoff may contain partial detail. The plan promises that the start does
   not wait for the whole. It does not promise a time by which a large content
   is complete.
3. **Smooth while it fills.** While content fills in and updates arrive, moving
   content keeps moving evenly: map pan and zoom, the peer dots, row animations
   and the marquee. The next input is answered as the first one was. Completing
   visual detail cannot delay input or presentation, and selection semantics
   and displayed interaction targets stay correct while refinement is
   incomplete.
4. **No waste from hard lines.** No owner works out again what another owner
   already knows, and none does work that no later owner uses. The work of a
   path is counted as a whole, from the input or the update to the picture,
   across every owner on it.
   [Hard lines](#hard-lines-that-waste-memory-and-cpu) names the ones found.

The goal applies at every size. The stress load is where the failures show; it
is not a size at which a time is promised.
[How a change is judged](#how-a-change-is-judged) says how each point is
measured.

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
  for another drawing costs a whole map for the strokes it changed. Until 10
  November, fades did this 54 to 59 times in an idle second. Since then, arc
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
  and the worst gaps stayed at 322, 266 and 225 ms. An attempt to split the
  table's row work into pieces made the median click slower in all 15 pairs (see
  the questions below).
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
  [record of 7 November](#record-of-7-november-what-filled-the-worst-gaps),
  and every collection pauses every thread: 13% of the
  `4f069bb` run, and a third of the click time in the 9 November native trace.
  Allocations and live objects are reduced like any other waste.
- Build, test and capture rules are owned by [testing](../agents/testing.md)
  and [compilation](../agents/compilation.md); this plan adds none.

## Questions already investigated

Read this list before you investigate a cause, so that nobody repeats the
work. It starts with the pan-back review of 10 November, and the groups after
the tables answer the questions of the batches before it. "Review" means an independent agent
session that read saved runs and traces. Add each new answer here.

**Map**

- *Why does the map freeze during pans while the UI thread is free?* The
  render worker draws on the game loop's `CanvasDevice`, and Win2D's
  `CanvasSwapChain` takes that device's Direct2D factory lock to start each
  frame and to present it (`CreateDrawingSession` and
  `PresentWithSyncInterval`). A long Direct2D call on the worker therefore holds
  the game loop's next frame: in 83 of 99 such freezes the game loop drew
  nothing. Render-thread input cannot help these freezes, because the newer
  view was already in `MapMotion`. Review and source check, 10 November;
  [batch 12](#batch-12-pan-effects-notices-on-screen-only-and-two-findings).
  Open: which worker step holds the lock. The capture records step durations,
  not their start times.
- *Do the UI probes show every UI-thread wait?* No. A probe is a sent message,
  and Windows delivers it before posted work, so a posted replay step can wait
  20 to 90 ms while the probes answer at once. Review, 10 November.
- *Why did the glow cost about 7 ms in each pan drawing?* The view is one of
  its inputs, so a pan recorded it again at each step. Fixed in batch 12:
  0.1 ms median per pan drawing.
- *Why did no peer dots fly in the pan back?* The dot size fell only after
  10 s of lower traffic, and a new subject kept the old subject's size. Review,
  10 November; fixed in batch 12; see
  [the pan back during churn](#pan-back-during-churn).
- *What else would shorten pan and zoom freezes?* A review on 10 November
  read the saved runs of `bfc54dc` (20.7 s frozen of 49.9 s of pans and zooms:
  13.8 s with the UI thread busy, 4.7 s tied to the worker's drawings) and
  proposed five changes. None is built or measured yet.
  1. Hold the tables' new sort order while a map gesture is active, and apply
     it once when the gesture rests (`Peers.RefreshView` and
     `Torrents.RefreshView`). This removes the new-order blocks from the
     gesture, which are the 13.8 s. It changes what the person sees, so it is
     the owner's decision.
  2. During a pan, draw a new picture only when the view nears the edge of the
     last one's margin, and once more at rest, as the glow does since batch 12.
     In 73.2% of pan frames the view was within half the margin of its
     picture. Predicted: about 12 pictures per pan phase instead of 30 to 59.
  3. Draw each picture for the view expected at its publish: the view's speed
     times the last drawing's time. At publish a picture was a median 0.10
     margins behind the view (worst 1.28), and in zoom-out the view was wider
     than the picture in 77.9% of frames.
  4. Arc guide refinement takes about 20 times its 6 ms budget. Drawings that
     refine 30 or more guides take 131 to 175 ms, against 41 ms. Each refined
     route disposes its gradient brush (`Route.Release`), and the same drawing
     builds it again with no budget check, although the brush depends only on
     the end points, colour and scale (checked in code). A zoom discards the
     refined guides at the next power of two, so refining while the scale
     changes is wasted.
  5. The render worker runs at below-normal priority (`MapMotion.cs:516`)
     while it holds the Direct2D lock the game loop needs, and Windows locks
     do not raise a holder's priority. Code only; no run shows it.

  Checked and dropped by the same review: keeping the game loop awake between
  pan frames (a picture published during a UI block was drawn after a median
  of 7.7 ms, as with a free UI thread), and sorting arcs by brush (each arc has
  its own gradient, so Direct2D cannot batch them).
- *What are the whole-process pauses?* Every pause over 64 ms in the traced run
  `cpu19` was a blocking full collection that WinUI requested (71 to 153 ms).
  Collections that allocation started stayed at or under 64 ms. The heap held
  40 MiB in generation 2 and 164 MiB of large objects. Review, 10 November.
  *What holds the large objects, and what makes private memory climb?* (`new22`,
  11 November: an allocation trace of one replay, `mem22`, and a heap snapshot
  12 s after the window was ready.) The managed heap reaches 245 MiB while the
  all-peers map opens and then stays at 205 to 250 MiB, so the later climb of 3
  to 4 MiB/s in private memory is native. The live large objects (180 MB) have
  no single owner: 45 MB of free fragments, 28 MB of byte buffers, 23 MB each of
  `int` and object arrays, 21 MB for the map's contributions of 160,000 peers, 13
  MB in 20 kept `Body.Changes.Edit` arrays and 8 MB of the location cache. While
  the map opens, 38 MiB of small temporaries come from COM marshalling in
  `Body.View.Raise` for the table's count-change notices. Open: what native
  memory grows; it needs a native heap trace, which needs elevation.

**Tables**

- *What does a new sort order cost the UI thread?* From the native trace of
  8 November (blocks 2 to 3 times longer than now): WinUI draws each row in a
  transition again from nothing (31.9%), draws the other rows (19.8%) and lays
  out (31.5%, of it 19.3% filling a container for each inserted position).
  Text drawn into textures is 25.8%, and the colour flag glyph is 6.3%.
  `Changes.Reconcile` is under 2 ms. The list has no move, so a moved row is
  built a second time. No change in `Reconcile` gets under that floor. Review,
  10 November. Candidates: the flag as a shared picture, fewer pieces of text
  per row (each needs proof that it looks the same), or a body based on
  `ItemsRepeater` with a transition provider. The last one is a major change,
  is not measured and waits for the owner's decision.
- *Why is the first sort of the all-peers list slow?* No row on screen keeps
  its place, so every row the list holds leaves and a new one arrives: about
  twice the work of a later sort. Half of the 316 ms block is
  `CellsPanel.MeasureOverride`; the sort itself runs on the worker. Batch 12
  lowered its worst UI response from 417 to 231 ms.
- *What does the list do with its containers below the screen after a sort?*
  In a 60-row test it dropped them in the layout after the sort and made them
  again, with the right rows and selection, within 1.5 s. Batch 12.
- *Why do both tables sometimes show a new order in one frame?* Chance. Each
  table schedules its own settle, and the benchmark's peer interval is 1 s. No
  mechanism is needed. Review, 10 November.
- *Does a new order cost less when only the containers on screen get notices?*
  Only for the first sort of the all-peers list (295 to 156 ms). Pans froze
  32.1% of the moving time against 24.8%, torrent-table blocks grew from 143
  to 228 ms, and 15 extra UI blocks appeared; see
  [the round of batch 12](#round-of-batch-12).
- *Is the queued rebind of containers off screen (`FlushRebind`) a UI block?*
  No. In a traced run of `new20` (61 s) it had 15 samples, about 30 ms in all,
  and none inside a UI block of 100 ms or more. The blocks that end near no
  new order (34 in `new19`'s runs against 3 in `new18`'s) are 71% WinUI with no
  app frame and 22% cell measure and arrange, with almost no row content
  change. Batch 12.
- *Can a `Field` cell be plain text?* No. It draws an icon and highlighted
  text. Batch 12.
- *Can a new sort order reuse the rows as a switch to another torrent does?*
  The owner declined, because the animation shows where each row went. See
  [the owner's answers](#the-owners-answers-to-the-pan-back-review).

**Clicks and the UI thread**

- *Is the UI thread waiting during the click stalls?* No, it is busy. `new8`'s
  cycle counter shows 2.74 s of CPU in its 3.0 s click phase, and in each press
  that waited 30 ms or more it ran for nearly the whole wait. The managed trace
  of `new8` holds 1,461 samples in the phase, 948 with no app code; of the rest,
  cell layout and fill hold 261, the table's catch-up of a count change 93,
  `ApplyProjection` 54 and the status bar 29. The native trace of 8 November (an
  earlier build) puts 2,300 of 4,836 stall samples in the render walk, mostly
  rasterising the text of changed `TextBlock`s, 556 in layout-transition copies
  of animating rows, 576 in the list's container preparation and 1,022 in hit
  testing, which is almost all layout that WinUI runs before it can test. An
  unelevated sampler on `new9` (118 stall samples): forced full collection 20,
  render walk 27, text measure 18, layout transitions 5. So the native work is
  layout and drawing of rows whose content changed, and the owner's row
  transitions. Saved traces, 10 November.
- *What does a click phase hold now?* `cpu19` (the build with every change of
  10 November) holds 1,543 UI-thread samples in the click phase: 977 in WinUI
  with no app frame, 230 in cell measure (202 of them WinUI measuring a cell),
  115 in count notices, 59 applying prepared projections, 54 in cell arrange and
  20 in row rebinds. All 151 samples in the cells' own measure are one line,
  WinUI's `child.Measure` of each cell; the panel adds nothing. A selection
  change measures no cells: a row answers it only in `Row.UpdateStates`, which
  changes visual states only when the row's drag or held state changes. About 42
  of the 59 projection samples are the notices `View.Apply` raises for the
  realized rows; `Slots.Commit` holds 3. Six collections paused the UI thread
  for 112 ms.
- *Can the count-change notices be made cheaper?* No, they are the platform's.
  `IBindableObservableVector` has no range notice, so a list that changes by
  1,000 rows takes 1,000 notices, and the owner's ruling forbids a Reset of a
  displayed list. Each notice reaches .NET through `Surface`, a managed
  `ListView` subclass that exists for its automation peer, and makes four
  `ComWrappers` calls. The table's own is the check in `View.Raise` that no
  listener kept the reused arguments: 2 of 1,560 samples in `new17`'s trace,
  against 32 for WinUI's handler beside it, so it stays. The other three
  marshal the delegate's arguments and the override's round trip. `new17`'s
  122 notice samples split by their innermost managed frame: WinUI's list inside
  the base `OnItemsChanged` 45, WinUI's native handler inlined into `View.Raise`
  and `CatchUpSlice` 54, `Surface`'s round trip 13, marshalling the arguments 7,
  GC polls 3. The only alternative that removes work is a list that is not a
  managed subclass, which saves at most 13 samples and loses the automation
  peer, so `Surface` stays. Holding the arguments' COM pointer would save one
  lookup per notice at the cost of a reference the view must release; not done.
  Notices run in slices of at most 4 ms below input, so a click waits for one
  slice at most. `View.CatchUp` raises only the notices the list is still owed,
  so a click that selects a torrent without kept peers, which goes to no rows
  and then to the new rows, announces only the difference when the new rows
  arrive before the removals. Review of 10 November.
- *Why did the table's row notices keep every peer alive, and what did they
  cost?* WinUI reads each inserted row back (`IList.GetAt`) and wraps it for COM
  (`ComWrappers.CreateManagedObjectWrapper`), so every peer the table ever
  inserted stayed held. `new7` held 385,341 GC handles before the clicks,
  159,810 of them on `Peer`, with 165,559 managed-object wrappers and 205,873
  finalizable wrapper releasers; after the clicks 143,542 handles, 27,397 on
  `Peer`. A switch from all 159,810 peers to a 9,000-peer torrent raised 150,810
  removals in 1,506 ms, showing all peers raised 20,895 to 57,492 insertions per
  publish in 187 to 581 ms each, and a marquee selection raised 21,015 in 246 ms.
  Since a row that arrives off screen is announced as null (TableView contract
  5.3), a dump of `new8` at the same point holds 6,584 managed-object wrappers
  and 19,195 dependent handles against 165,559 and 178,409: 0.19 wrappers per
  live `Peer` against 1.04. Batch 6, 10 November.
- *Why was the median click slower while each click cost the UI thread less?* The
  press waited longer to be handled, behind UI-thread stalls. In `new7` against
  `new5` the handled press to its selection took under 1 ms against 1.7 to
  3.7 ms, and the wait before the handling was longer. Removing the waste in
  those stalls (`new8`) lowered the median press wait from 68 to 41 ms over 97
  common clicks and the stalled time from 1.6 to 1.8 s to 1.3 to 1.6 s of each
  3 s of clicks, lower in every pair; what remains is WinUI's own layout and the
  table's cell layout after each switch.
- *Can the row work of a switch be split into pieces that fill later?* No. The
  first design of an in-place switch filled rows in phases, and the median click
  was slower in all 15 pairs of three rounds. During fast clicking the UI thread
  is busy most of the time, so each piece ran where the next press landed and
  added its own layout pass; filling one row per Low-priority item also kept the
  window drawing about twice as many frames for 1 to 2 s after the clicks. It was
  removed. A different list now switches in place (TableView contract 5.3), and
  five pairs of the peer table switch capture show a switch at about 40% of its
  earlier cycles (median 140 against 349 megacycles) and its busiest frame at
  less than half. Rounds 1 to 3 and the switch capture of
  the keep-up plan (plan 18), 9 November. That plan is closed and deleted;
  `git show bfceaa2:docs/plans/18-keep-up-with-the-person.md` shows its text.

**Allocation and engine copies**

- *What does the click phase allocate, and who owns it?* `new10`'s allocation
  trace puts 40 MiB in the 3 s phase. The largest app sites are the peers
  worker's `Swarm.Read`, 8.6 MiB, which allocates only for peers that join and
  finds a held peer by its endpoint text without allocating; the map's groups
  for each new subject, about 5 MiB; and the `ComWrappers` object that .NET
  makes for each notice crossing, 5.4 MiB. Each serves the click. Reusing map
  groups between subjects would save at most about an eighth of the phase's
  allocation, and a reused group would have to keep its point and its name's
  language exactly, so it is not done. Small sets, arrays and LINQ objects per
  click hold 0.0 MiB, so removing them cannot be measured. Of the six
  collections in `new17`'s click phase, all started by allocation (three of
  generation 0, three of generation 1), none is WinUI's forced collection.
- *Where does the map's render thread spend a click phase?* In `cpu19`'s trace
  it is busy in 393 of its 1,508 samples: 255 drawing, 56 of them rebuilding
  strokes for a new subject's arcs, and 122 preparing routes. Source lines put
  99 of those in `PrepareRoutes`: 37 adding the previous routes to the lookup by
  key, 27 checking whether the destinations are unchanged, 17 in the loop over
  destinations and 13 parking arcs that stop showing. The sorting phase holds 1
  sample of it and the idle second none, so the cost comes with a change of
  subject; the lookup lets a new subject's places reuse the arcs of places the
  previous subject shared. Route preparation then fell from 122 samples
  (`new12`) to 79, 68 and 57 (`new15`) and to 41 in `new17`, where parking no
  longer appears; the 41 are matching the previous subject's routes by key (26)
  and the loop over the new destinations (14). Single traces differ by about 10
  samples. Batch 10.
- *Does the map need its glow brush per arc?* Not the halo's. It depends only
  on its colour, and the map draws at most four colours per palette, so the map
  keeps one brush per colour and sets each arc's centre, radius, opacity and
  scale before it draws the arc; a command list keeps a radial brush by value
  and references only its gradient stops. The core brush stays one per arc,
  because its stops put the landing fade 30 pixels from the destination and so
  depend on the arc's length on screen. A new map made 2,001 brushes alive at
  1,000 arcs and spent 590 render-thread samples in
  `CanvasRadialGradientBrush.CreateWithStops` when the 159,810-peer list opened.
  No capture has counted the brushes after the change. Batch 11 (`new18`).
- *Why was batch 7's first push slower, and does the capture show it?* The cause
  given at the time, location lookups, does not hold: the capture's `PeerRows`
  use `10.x` addresses, which the location data does not hold, while `lookupMs`
  times public `87.68.x` addresses in a separate loop. `createMs` is one sample
  per capture and has been 9 to 45 ms across captures of the same code, so the
  capture cannot show a change of a few milliseconds. Parsing a new peer's
  endpoint once saves about 1 to 2 ms per 10,000 new peers, which source review
  shows and the capture cannot.
- *Which copies and scans were left in place?* Skipping unchanged peer lists:
  connected peers' rates change at every refresh, so an unchanged list is rare.
  Copying piece bitfields: libtorrent copies them inside `3rdParty`, which
  agents do not change. One snapshot for several commands: a command's reply
  promises a published snapshot that holds its result. Describing a view once for
  several kinds of observation: the owner's ruling shows each available fact at
  once, and how often the kinds arrive apart is not measured. Shrinking pooled
  buffers: WinUI's forced collection follows working-set growth, not size, and
  shrinking would add work. Not copying each new row list in TableView: the copy
  is the library's documented behavior and costs about 1 ms per update only at
  160,000 peers. That answer weighed its time only; its memory is open again
  as [hard line 1](#hard-lines-that-waste-memory-and-cpu). The scans that every drawing repeats in `DrawScene` and
  `MapMotion.RenderMap` (the marked routes, the estimated countries, the chosen
  destination and a new `Highlight`): since the fades stopped forcing drawings,
  an idle second draws 5 to 6 times, so each scan passes over about 1,000 routes
  a few times per second. `Swarm.Join` copies only for a scope of several
  torrents, and a swarm rebuilds its endpoint index only for a reply that adds or
  removes a peer; the index holds positions, so a leave shifts every later
  position and an update in place still touches most peers, and the new swarm
  keeps a worker that reads the old one safe. TableView's `Slots.Number` reads
  each row's key once, only for a list it has not numbered; `ClearNext` is one
  fill of an integer array, about 640 KB at 160,000 rows; `Flip` writes each row
  of a different list into the view once and raises no notice per row. Batches 9
  and 11.
- *What owns about 800 MB of native memory at the stress load?* Not known. Only
  a native heap trace of a running build can name it. Batch 11.
- *Does `Field` cost more than a native cell?* Of 1,574 click-phase samples,
  about 2 are the crossing into .NET. `Field` is a managed control, so WinUI
  reaches each cell's measure and arrange through .NET: 57 samples sit under its
  base `MeasureOverride` and `ArrangeOverride`. Below them the native samples
  show `CTextBlock::MeasureOverride` and `CIconElement::MeasureOverride`, the
  measure of the cell's text and icon, which a cell built from native elements
  also does. Replacing the control in nine templates would not remove the
  measured work and would change what Narrator reads from those cells. Batches 7
  and 9.

**Decisions of the click-first work, not to change**

- *Should the old display freeze at the click?* No. A new map subject cancels the
  old render, but freezing the old display at the click would need its own state
  in the table, the map and the inspector, because more than seven paths
  restart work for the old torrent.
- *Should publications run at Low dispatcher priority?* No. The table's
  notifications cost 19 samples of 4,836 in the stalls, and a hit test runs any
  waiting layout before the click, so a lower priority cannot protect a click.
  Only a smaller pending layout can.
- *Should `Torrent` raise per-property change notices?* It does now: `Raise`
  names the properties whose input fields changed, through a table of inputs per
  notice (`Torrent.cs`). The click work left it alone, because in the reference
  trace the whole-row notices came from the harness's summary feed
  (`ApplySummaries`, 198 samples), not from a click, and many bound properties
  are computed from several fields, so per-property notices need a dependency
  map. `RefreshText` still raises one notice for the whole row, for a language
  change.

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

`Swarm.Read` looks up the joined peers on its worker, so the benchmark's
switches include the lookups: about 4.6 µs per peer in the peers capture's
10,000-peer journey. Inspector's `_locating` flag guards only the public
address's lookup. This removed the two whole-population sets per pass of
[issue 578](https://github.com/SynoSoftware/TinyTorrent2/issues/578), which is
closed.

The fixture answers one torrent at a time when all peers open
(`Inspector/Peers.cs`), so `inspector-all-map-open` joins and publishes a
growing list about 50 times, where the product joins once per batch of
replies. In the 10 November traces `Swarm.Join` allocated 31 to 32 MiB of
`Peer[]` there. Pairs of builds still compare fairly, but that phase's time and
memory do not describe what the product does when all peers open.

Ordinary use is a separate expectation: hundreds of rows, sometimes thousands,
rarely ten thousand. A correction that helps only at stress size and adds code,
state or memory at ordinary size is not retained.

## How a change is judged

No measure has a limit in milliseconds at the stress load. Until 11 November
this plan carried such limits: p95 within one frame and worst within three,
for clicks, navigation, the marquee and the dots. Every round against them
ended "not accepted", whatever it changed, so they told nobody whether a
change was good. No further round runs against them.

A change is judged by three things, in this order:

1. **A count of the work it removes.** Examples are the drawings in the idle
   second, the row notices of a click, the messages parsed, the bytes one
   update allocates and the working-set growth. A count needs one run, does
   not vary between runs, and names the code that does the work.
2. **One round of five matched pairs** against the preserved reference binary,
   for the phases the change touches. The change stays when no measure is
   worse in five of five pairs. The measures vary between runs of one build by
   more than most changes move them: `new10`'s median click was 43.6 ms in one
   round and 27.9 ms in the next. So a mixed result is explained from the code
   and the saved records, and is not run again.
   [Testing](../agents/testing.md#verification-must-answer-a-question) decides
   whether a change needs a round at all.
3. **The owner's look on screen,** beside the reference build, for every
   change to motion or to what an input shows. No measure shows whether motion
   looks even.

The harness already records these measures. Each is read with the point of
the [goal](#goal) that it shows.

| Goal point | Measure | What it shows, and what it misses |
| --- | --- | --- |
| Feedback first | Click to selection, and click to XAML completion, per click | How late the person's own click shows. The second covers the whole UI turn after the click, including the selected torrent's peer-table update. |
| Feedback first; smooth while it fills | UI response per phase: the delay of a message sent to the window about every 16 ms, p95 and worst. The count of UI blocks of 100 ms or more. Command delivery, worst | Whether the UI thread lets input through while work continues. Windows delivers a sent message before posted work, so the probe misses waits of posted work. |
| Start at once | First picture of each map opening | When the picture with the requested connections is drawn. For a large content this is its completion, which has no promised time. The saved runs hold the time of each published picture but not its subject, so they cannot say when the first part of the new content showed. Until they can, the owner's look judges this point. |
| Smooth while it fills | The share of the moving time with the map frozen, split by UI thread busy or free; navigation updates per second and the worst navigation gap; the marquee sample gap; the stationary dot draw gap; the length of a table's new-order block | A freeze is 50 ms or more between two drawn views that differ. |
| No waste | The counts of point 1 above; peak and end private memory and peak working set; forced full collections per run | What the work costs. GC duration explains a stall; it is not judged for itself. |

These measures cannot certify the physical display. Displayed frame rate and
next-displayed-frame selection stay recorded as unverified until a
presented-frame endpoint exists.

The plan ends when
[what still interrupts the person](#what-still-interrupts-the-person) holds no
entry that the app can change, and the owner has looked at ordinary use and at
the stress replay and accepts both. Each entry that stays names the platform
limit that keeps it.

Before the plan is deleted, two kinds of its content are in the contract that
owns them, because a rule or a fact that only a plan holds is deleted with it,
and the next design then works it out again or builds the opposite:

- every owner ruling and owner answer recorded here;
- the platform facts that a later design depends on. They are under
  [questions already investigated](#questions-already-investigated), the
  rounds and [hard lines](#hard-lines-that-waste-memory-and-cpu): a WinUI list
  takes one notice for each row and none for a range; one table update is one
  UI block of layout; a removed row container is not reused until its
  animation ends; a hit test runs any waiting layout first; WinUI forces a
  blocking full collection after 50 MiB of working-set growth; the render
  worker and the swap chain share one Direct2D lock; and which input WinUI
  reads off the UI thread.

### Deciding a correction

- Before building, the correction names the goal point it serves, the count or
  measure it predicts to change, and the direction. A change without a
  predicted count or measure is not a performance correction and is judged as
  code only.
- Predicted count or measure improved, and no measure worse in five of five
  pairs: retained, and the reference moves to it. Predicted count or measure
  not improved: not retained, because it did not deliver its intended result.
- Predicted count or measure improved, but another measure worse in five of
  five pairs: find the cause in the code and the saved records before any new
  run. The correction stays when the cause is fixed, or when the owner accepts
  the trade. No retry for a favorable maximum.
- A measure that is worse in fewer than five pairs is recorded as borderline
  and is not run again.
- Corrections already retained stay unless a matched round attributes a
  regression to them. A worse single run is not a confirmed regression; it
  prevents an unjustified revert and nothing more.
- Record every adverse observation. Keep UI elapsed time distinct from worker
  time and queueing; never add overlapping intervals. New in-process probes
  need matched diagnostic builds of both revisions; external recording may use
  the preserved binary directly.

## What still interrupts the person

This is the plan's working list. Each entry is something the person sees at
the stress load, with its latest recorded size, its cause and what changes it.
An entry leaves the list when its change is retained. An entry that the app
cannot change stays and names the platform limit.

1. **A pan or zoom stops while a table shows a new sort order.** Size: the map
   is frozen with the UI thread busy for 22.9% of the moving time
   ([third round](#third-round-the-border-stall)). One new-order block is a
   median of 98 ms for the torrent table and 106 ms for the peer table with
   the notices bounded to the cache, against 132 and 135 ms without
   ([fifth round](#fifth-round-notices-bounded-to-the-cache)). Cause: one
   table update is one UI block that lays out the rows and starts their
   animations. The map gets its view through the UI thread, so during that
   block the map has no new view. The owner keeps the row animation. Change:
   the bounded notices were dropped (fifth round). Reading the map's input
   off the UI thread is hard line 4 below. What then stays in the block is
   WinUI's layout and drawing of the rows that changed.
2. **Going from all peers to one torrent's peers blocks the UI, and that
   torrent's map opens later.** Size: worst UI response 230 ms when the
   selected torrent's map opens with the bounded notices, against 160 ms
   without. This block and the first sort of all peers take 342 to 421 ms
   together with the bounded notices, against 455 to 522 ms without (fifth
   round). Cause and change: hard line 2 below.
3. **The first sort of the all-peers list blocks the UI.** Size: 316 ms in the
   traced run of batch 12. Cause: no row on screen keeps its place, so every
   row the list holds leaves and a new one arrives, and half of the block is
   cell measure. Change: none selected. The candidates are in the table
   questions above; one of them, a body based on `ItemsRepeater`, waits for
   the owner's decision.
4. **Everything stops for 0.1 to 0.2 s several times in a run: the UI, the map
   and the dots.** Size: WinUI's forced full collection, 71 to 153 ms each in
   the traced run `cpu19`, five of them in `new10`'s run, 2.4 to 3.5 s apart
   while the map opens. Cause: WinUI asks
   .NET for a blocking full collection each time the working set has grown by
   50 MiB ([what forces them](#what-forces-winuis-full-collections)), and a
   blocking collection stops every thread that runs .NET code, the map's game
   loop included. The app cannot turn this off. Change: the working set grows
   less, which is the [hard lines](#hard-lines-that-waste-memory-and-cpu) and
   the other waste listed with them.
5. **The dots jump by about 50 ms.** Size: at `4f069bb` each dot gap of 55 to
   67 ms held a gen 0 or gen 1 pause of 45 to 55 ms, and the median gen 0
   pause was 23.5 ms. In `cpu19` every collection that allocation started
   stayed at or under 64 ms. Cause: not known. The candidate is objects that
   WinUI and .NET both hold (step 7). Change: after the cause is known. One
   idea is not designed: the dot motion as Composition animations, which the
   system compositor runs outside the app's threads.
6. **A pan or zoom stops while the UI thread is free.** Size: 2.0% of the
   moving time since the border fix (third round). Cause: the render worker
   draws on the game loop's device and holds its Direct2D lock during a long
   call. Change: fewer drawings during a gesture, hard line 3 below.
7. **The worst click of the click phase waits 137 to 259 ms.** In all ten runs
   of round 32 (`7b34517` against `20ea1bc`; `compare32.md` in the session
   scratchpad) it is one UI block in the first 0.55 s of the phase, and the
   block ends when the torrent table goes from 29 to 39 row containers. Cause:
   not established. Change: after one traced run of that phase (step 7).
8. **The marquee stalled** at `4f069bb`: sample gap p95 142 ms, worst 266 ms.
   This plan holds no later marquee figure. Before any work on it, read the
   marquee phases of the latest saved runs.

### Record of 7 November: what filled the worst gaps

The two tables show where the time was at `4f069bb`. The rounds recorded
below changed most of these paths, so the tables do not show where the time is
now. Values come from the profiled build `4f069bb` (replay, its evidence folder
`artifacts/evidence/ui-profile-map-defined-changes-20261107-4f069bb`, joined
with its `gc-intervals.json`), and for clicks from the native 9 November trace
of the click benchmark's reference build (run `557407bf`). "Unknown" marks
time that no saved record attributes.

**Collections, which every endpoint shares.** In the 59.5 s `4f069bb` run:

| Kind | Count | Median pause | Longest | Total |
| --- | ---: | ---: | ---: | ---: |
| gen 0 | 109 | 23.5 ms | 76.9 ms | 3.3 s |
| gen 1 | 65 | 37.5 ms | 101.3 ms | 2.6 s |
| Full, requested by WinUI (induced, blocking) | 6 | 162 ms | 218.6 ms | 0.9 s |
| Full, background, large allocations | 19 | 34.8 ms | 49.3 ms | 0.66 s |

A gen 0 pause of 23.5 ms is longer than one frame of a 60 Hz display, so it
shows as a jump in moving content, and each requested full collection stops
everything for 0.1 to 0.2 s. The requested ones come from
WinUI: `CCoreServices::TrackMemory` is on the UI stack for the 262 ms collection
in the native click trace, and
`ReferenceTrackerHost.IReferenceTrackerHost_DisconnectUnusedReferenceSources` on
the UI stack of another. WinUI asks after the working set grows by 50 MiB
([what forces WinUI's full collections](#what-forces-winuis-full-collections)).
What grows it is in the [hard lines](#hard-lines-that-waste-memory-and-cpu),
and why a gen 0 collection takes 23.5 ms is step 7.

| Endpoint | 7 November, p95 / worst | Inside the worst gaps |
| --- | --- | --- |
| Click to rendered selection | native: median 41–100 ms, worst 156–505 ms (round 4 of the 9 November click comparison) | Native trace, 12 clicks, 1,364 ms: collections 34%, WinUI drawing 30%, app code 17%, input 8%, layout 4%; in the long pauses, cell measuring and row building lead. `4f069bb`: the two worst (229 and 193 ms after selection) hold no collection; unknown |
| Map navigation draw gap | 54–184 / 208–322 ms | 41 ms of collection at most. The gap coincides with the UI thread not delivering the next view change (worst command gap 332 ms in the same window); in a later managed trace the UI thread spent 60–70% in WinUI frames and 10–17% in `CellsPanel.MeasureOverride`. Unknown for `4f069bb` |
| Marquee sample gap | 142 / 266 ms | No collection. At `75af850`: 234 ms of native WinUI work, `CellsPanel.MeasureOverride` 81 ms forced by `Marquee.Track`'s `TransformToVisual`. Unknown for `4f069bb` |
| Stationary dot draw gap | 42 / 225 ms | 210 ms of one requested full collection; the 55–67 ms gaps each hold a 45–55 ms gen 0 or gen 1 pause. The dots draw on Win2D's own thread, but in C#, and a blocking collection stops every thread that runs .NET code |
| Command delivery | 171 / 325 ms | Same stall as navigation; at `75af850`, `View.Apply` 112 ms in one click and the fixture's own summary feed 150 ms in another |

The owner watched a run of the click comparison on 9 November: the transfer dots
move jaggedly, and the pan is slow and jagged. Those are the stationary dot and
navigation rows. The owner judges them again live, beside the reference build,
after the next change to them.

## Hard lines that waste memory and CPU

The owner, 11 November: "the architecutre was designed to have one owner, one
implementation, one responsibility - that could have obscured the big picture
and caused a lot of unnecesary work of the whole system."

A hard line is a boundary between two owners that lets too little through.
Each owner does its own work completely and correctly. It does not know what
the owner before it already worked out, or what the owner after it will use.
So the same result is worked out again on the other side of the line, or work
is done that no later owner uses. The waste does not show inside one owner.
Each contract describes one owner, and a review of one owner stops at the
line: this plan once left the table's copy of every new row list in place
because the copy "is the library's documented behavior". The waste shows when
one path is followed from end to end.

The plan keeps one owner for each rule, because two copies of a rule drift
apart. It changes what crosses the line: the owner that knows a fact passes
it, and the next owner uses it and does not work it out again. Two lines
already let the fact through. The peers pane names the rows whose values
changed (`Table.RefreshView(changed)`), so a sort reads only those rows' keys
again. And the map's grouping gets the shown zoom since `20ea1bc`; until then
it built an arc for every destination, however close two of them were on
screen ([close destinations plan](20-close-destinations.md)).

**Owner ruling, 9 November: every stage that redoes work an earlier stage
already did, or makes an object only to drop it, is fixed; the collector is
the evidence, not the target.** The same holds for WinUI objects that
code-behind makes per update, row, draw or click instead of keeping one, and
for copies made per element of an object that could be shared. The batches
built from this ruling are recorded in the questions above, and the contracts
hold their rules. Before them, the 21 clicks of the native reference trace
allocated about 82 MB in 3.15 s, mostly on workers: about 20 MB for the
table's row maps, built from nothing on every publish, and 13 to 24 MB for
parsing every peer again into new records on each update.

Each change is judged against the data-oriented form, because it removes the
churn without the retained object-keyed state that made the reverted rework
hold 788 bytes per row against 266: a row gets a small integer slot once,
when it first appears; positions, membership and versions are arrays or bit
sets indexed by that slot and kept between publishes, which the collector
does not trace; the writer bumps a row's version or names the changed slots,
so work follows what changed; a reply is parsed into the existing row,
comparing bytes before making strings; buffers are sized once and reused.

**One path from end to end: a peer update, from libtorrent to the peers
table.** The same update also goes to the map; that path has its table after
hard line 4.

| Owner | What it gets | What it does | Work that follows the size of the list |
| --- | --- | --- | --- |
| libtorrent | A query | Returns the torrent's full peer list | The list. Not ours to change |
| Engine and pipe | The full list | Sends each torrent's full list, by owner ruling | Writing and parsing the list |
| Peers worker, `Swarm.Read` | The full list | Matches every listed peer against the rows it keeps, and makes the lists of the peers that joined, left and changed | One match for each listed peer. Needed, because the wire carries no changes |
| Peers worker, `Swarm.Join` | The swarms of the view | After a join or a leave in a view of several torrents, makes one new array of all peers | A copy of the whole list |
| Inspector and peers pane | The joined, left and changed peers | Names the changed rows to the table (`RefreshView(changed)`). After a join or a leave, sets the whole new list | None of its own |
| Table, `Source.Set` | The whole list | Copies every row reference, on the UI thread | A second copy of the whole list |
| Table, `Changes.Prepare` and `Slots.Commit` | Its copy | Compares the whole new order with the shown one, which finds the joins and leaves again, and makes a new position index | A comparison and an index of the whole list |
| Table to WinUI | The changes | One notice for each changed position the list holds, and one for each row of a change in count | None. It follows the screen and the change |
| WinUI | The notices | Lays out and draws the rows that changed, in one UI block | None. It follows the screen |

The joins and leaves are known at `Swarm.Read`. After it, the whole list
crosses two lines, is copied twice, and is compared to find the same joins and
leaves a second time.

1. **Between the peers worker and the table: a whole list for every join or
   leave.** The table above shows it. In one replay of the 10 November traces
   `Swarm.Join`, `Source.Set`, `Changes.Prepare` and `Slots.Commit` allocated
   31 to 32 MiB, 28 MiB, 18 to 21 MiB and 16.5 MiB of large objects, before
   `0a9df1e` reserved the row map. The fixture answers one torrent at a time
   when all peers open
   ([workload](#workload)), so these sizes overstate the product there. The
   person pays through entry 4 of
   [what still interrupts the person](#what-still-interrupts-the-person),
   because this memory is working-set growth. How many forced collections it
   causes is not measured. The table has compared whole lists since its first
   commit, and `db6ec1d` made "assign a new list for each change" the app's
   pattern. The owner's model on 11 November is a collection that the table
   follows: "tableview is displaying peers of a torrent which are in an
   observableconnection. if we switch the torrent we swich the observable
   collection." Open: a design in which the peers pane passes the joins and
   leaves it already has, as it passes the changed rows. It changes TableView
   contract 5.3, so it starts from the table's history (ruling of 9 November
   in the settled decisions) and needs the owner's decision before it is
   built (step 6).
2. **Between the peers pane and the table: the table guessed whether it was
   given another list.** The
   pane knows that the person chose another torrent. The table was not told,
   so it compared rows. A list that shares rows with the shown one, such as
   one torrent's peers after all peers, was animated as a change of the same
   list: every row the list held left with an animation, and the list built
   other containers at once (fifth round). The owner, 11 November: "we
   established that there's no need to animate when we change the datasource
   so we flip it in place. u must not handle animation out of tableview".
   Change: the host withdraws the shown list (`Table.Withdraw()`) before it
   sets another one, and the table puts the new rows into the containers it
   holds, without animation. It was written with the pane's part and dropped
   on 11 November (fifth round): where the two lists share rows, a held
   container keeps the old row as WinUI's item, so two containers can answer
   for one row, and every step of a multiple selection jumped to the top.
3. **Between the map's render worker and its game loop: more pictures than
   frames.** The worker owns the picture and the game loop owns the frame, and
   the count of pictures does not follow the count of frames. Each drawing
   draws the whole map. A pan that the latest
   picture still covers draws nothing new until the view rests (fourth
   round). During a zoom every step has another scale, and the renderer
   leaves a drawing to the rest only while the picture's scale still looks
   the same (`MapRenderer.Postpone`), so a zoom draws as many pictures as the
   worker can make. After `20ea1bc` made each drawing cheaper, the worker drew
   152 to 172 pictures in a 2 s zoom-in phase in four of five runs, against 68
   to 82 before (round 32; 66 in the fifth run, which a long UI block
   disturbed). The map showed 117 to 122 frames in each of those phases,
   before and after, with 45 to 46 new views per second, so the added pictures
   did not become added frames. The second round saw the same effect: cheaper
   drawings, more of them, fewer game-loop frames and more freezes with the UI
   thread free. Open: a design that draws less during a zoom (step 5).
4. **Between the UI thread and the map's game loop: the map's view is read by
   an owner that is busy.** A pan or a
   zoom is the person's input to the map, and the map's own threads draw it.
   The input is read on the UI thread, so a busy UI thread freezes a map that
   could draw: 22.9% of the moving time (third round). WinUI's own scrolling
   reads touch, pen, touchpad and wheel input off the UI thread, but not a
   mouse drag. A mouse drag now reaches the game loop directly
   ([sixth round](#sixth-round-the-game-loop-follows-a-mouse-drag)); a zoom
   still goes through the UI thread.

The three other paths, followed in the code on 11 November (step 4). In the
map, "all" means every place or destination, about 1,000 in the stress
fixture, not every peer. The saved runs hold no time for the map worker's
steps, so the map's entries are not sized.

**A peer update to the map's picture.**

| Owner | What it gets | What it does | Work that follows all places |
| --- | --- | --- | --- |
| Inspector, `ApplyPeers` and `GiveMap` | Each torrent's reply | Gives the map the round's replies once, at the round's end | None. It passes the changes |
| Map worker, `Arrangement.Update` | The replies | Applies each change, join and leave to its groups | None. It follows the changes |
| Map worker, `Arrange` and `Merge` | Its groups | Looks up the regions of every group and walks every group to rebuild the placement list. Projects every placement again and rebuilds its index | Every group and every placement, though `Update` knows which groups changed |
| Map worker, `Present` and `Matches` | Its destinations | Makes a new dictionary of every country, a new list of every destination and a new index by key. Compares all of it with the last presentation, and drops it when nothing differs | Three new collections and one comparison of everything, to learn what `Arrange` and `Merge` knew |
| Render worker, `RenderMap` and `PrepareRoutes` | The presentation | Builds the set of marked destinations. Sorts every active destination by rate to choose the arcs. Sets every route's strength. Builds an array of every route to see whether the arrangement changed | Every destination and country, for every presentation |
| Render worker, `PrepareArcs`, `Inspect`, `PrepareMarkers` | The routes | New traffic for every arc. A new inspection with a target for every route. A marker for every destination, compared with the kept ones | Every arc, route and destination. The markers' check by reference fails, because `Present` always makes a new list |
| Game loop | The picture and the traffic | Shows the picture. Walks every lane when the traffic is new | Every lane |

**A wheel step on the map to the picture.**

| Owner | What it gets | What it does | Work that follows all places |
| --- | --- | --- | --- |
| `PeerMap`, wheel and viewport | The wheel event | Sets the new view, sends it to the map's threads and restarts the 250 ms rest timer | None |
| Render worker, for each picture | The view | Draws the whole map (hard line 3). Builds a new inspection with a target for every route, though the model did not change | The whole picture, and an array of every route, also for pictures that no frame shows |
| Render worker, at its own rest | Its clock | Draws once more 250 ms after its last view, and refines what the motion left | The whole picture |
| `PeerMap.Rest`, the UI's timer | Its clock | Asks the map worker to group again at the shown zoom, 250 ms after the UI's last view change | None |
| Map worker, `Merge`, `Present`, `Matches` | The new shown scale | Merges every placement. Makes a whole presentation and drops it when no destination changed. It no longer places every group again, because only `Merge` reads the shown scale (seventh round) | Every placement, at every rest |

**A click on a torrent to its rows and its map.**

| Owner | What it gets | What it does | Work that follows the whole |
| --- | --- | --- | --- |
| Torrents table and `MainViewModel` | The press | Selects the row and hands the selection on | None. It follows the selection |
| Inspector, `QueueSubject` and `Retarget` | The selection | Invalidates, waits one frame, clears, invalidates again and refreshes twice | None |
| Inspector, `ReadPeers` | The next frame | A torrent seen before: joins its kept swarm on a worker, shows its kept rows, then subscribes. A torrent not seen: subscribes at once | None for the lists. One worker hop for a single kept swarm, whose join returns that swarm's own list |
| Peers worker, `Swarm.Read` | The first reply | A torrent not seen: a row for every peer. A torrent seen: one match for each peer | Every peer of the new torrent, needed for the first rows |
| Table, `Source.Set` and `Changes.Prepare` | The new list | Copies the new list. Copies the old view and scans it for a shared row, to learn that the list is another one | The new list, and the old list: 159,810 rows when the person leaves all peers |
| Map worker, `Arrangement.Update` | The new subject | Groups every peer of the torrent from nothing, also for a torrent it showed before | Every peer of the new torrent |
| Render worker, `DrawPhases` | The presentation | Publishes the land, then the arcs, then all places | Every destination, twice for the inspection |

5. **Between the map's grouping and its readers: a whole presentation for
   every change.** `Arrangement.Update` applies only the changes. After it,
   `Arrange`, `Merge`, `Present` and `Matches` each pass over every group,
   placement or destination, and the render worker does the same for routes,
   traffic, inspection and markers. `Arrange` and `Merge` know which groups
   they copied and which destinations they made new. That fact does not cross,
   so `Matches` and every reader work it out again. Open.
6. **Between the render worker and the picture's readers: an inspection for
   every picture.** An inspection holds a target for every route. During a
   zoom the model does not change, and the worker still builds one for each
   of the 152 to 172 pictures of hard line 3. Open with step 5.
7. **Between the inspector and the map's worker: the map keeps one subject.**
   The inspector keeps the swarms of torrents seen before and shows their
   rows at once. The map's worker keeps the groups of one subject, so a
   return to a torrent groups every peer of it again. Open.
8. **Between the UI's rest timer and the render worker's rest: two clocks for
   one rest.** The worker draws its rest picture and refines it. The UI's
   timer then asks for a new grouping, which walks every group although only
   the shown scale changed. When a destination changes, the worker draws the
   whole map again. The order of the two drawings in time is not confirmed.
   The [close destinations plan](20-close-destinations.md) keeps the two
   clocks apart by design, so the change is that plan's to decide.

Some lines stay where they are. The engine sends each torrent's full peer
list, by owner ruling
([pipe API](../contracts/pipe-api.md#snapshots-and-detail)); `Swarm.Read` pays
for it with one match for each listed peer, on a worker. WinUI's lines are not
ours to move: its list takes one notice for each row and has none for a range,
and it asks for a full collection after 50 MiB of growth. The first rows of a
torrent not seen before need a row for every peer, and the first picture of a
subject that the map's worker does not hold needs every peer grouped.

The review left four candidates that are not confirmed: pushes of the old
subscription that are parsed and dropped between a click and the new
subscription; the second publication of a first picture, replaced before a
frame shows it; a second rebuild of the table's view when the pane calls
`RefreshView()` for a new subject; and a hit test of every route after each
wheel step. The readers' reports are `map-paths.md` and `click-path.md` in
the reviewing session's scratchpad.

Waste under the ruling of 9 November that is not a hard line:

- **The inspector repeats two calls on a click, and both stay.** It
  invalidates in `QueueSubject` and again in `Retarget` one frame later. The
  second call decides the release with the new target, so only its
  cancellation of a read that never started is a repeat. It refreshes in
  `Show` and again in `FollowSelection`, after `IsReplacing` may have changed;
  the repeat costs one pass over the inspector's commands. A third item
  stays open: a torrent seen before joins its one kept swarm on a worker,
  where `Swarm.Join` returns that swarm's own list, and its rows and its
  subscription wait for that hop. A `ShowSwarms` that used a worker only for
  several swarms was built and taken out again on 11 November
  ([seventh round](#seventh-round-the-batch-of-11-november)).

- **Collections that grow by doubling** make copies only to drop them. In one
  heap dump of the replay, 40 of the 57 arrays of 1 MiB or more were such dead
  copies: 72.5 MiB. The table's row map is reserved since `0a9df1e`. The map's
  contributions (`MapModel`) are reserved since the seventh round: the growth
  of their dictionary had allocated 38 to 40 MiB of large objects in one
  replay.
- **Native memory with no named owner.** Private memory rises by 178 to
  225 MiB within 1 s when the first arcs appear, and about 195 to 219 MiB of
  it is committed pages that hold only zeros. `CanvasDevice.Trim()` does not
  release it. One run of a build that called it when a map had settled and
  after each click (`new27`, 11 November; runs in `mem27runs.txt` in the
  session scratchpad) held 745 MiB after the all-peers map settled, against
  763 to 794 MiB in five runs without it. The calls during the clicks freed
  about 57 MiB, and the run still ended at 903 MiB, against 885 to 918 MiB.
  The memory is not measured again since `20ea1bc`, which lowered peak private
  memory from 903.5 to 714.9 MiB (round 32). Only a native heap trace can name
  the owner, and that needs elevation.

## Steps

The order is: what is built, then what the owner asked for, then the paths
not yet followed and the hard lines that need a design, then the causes that
are not known. Each step is
done when its result is written into
[what still interrupts the person](#what-still-interrupts-the-person).

1. **The table changes that were built are dropped.** The notices bounded to
   the cache and the different-list rule of hard line 2 failed review on 11
   November ([fifth round](#fifth-round-notices-bounded-to-the-cache)). A new
   design starts only when the owner asks for one.
2. **Prototype the map's input off the UI thread** (hard line 4). The owner
   asked for it on 11 November. Done for a mouse drag: the owner has seen a
   pan during table re-sorts
   ([sixth round](#sixth-round-the-game-loop-follows-a-mouse-drag)). Open: the
   slower rendered selection that round found, and whether a zoom needs the
   same path.
3. **Record what the start shows.** Capture builds add the subject to the
   record of each published picture, so a saved run says when the first
   picture of the requested content appeared. It joins the next build and
   needs none of its own.
4. **Follow the other paths from end to end,** across every owner on them, as
   the table of one peer update does under
   [hard lines](#hard-lines-that-waste-memory-and-cpu). The paths are: a peer
   update to the map's picture; a click on a torrent to its rows and its map;
   a wheel step on the map to the picture. For each owner on a path, write
   what it gets, what it works out, and what the owner before it already
   knew. It is a review of the code and the saved records and needs no build.
   Done when each path has its table, and each piece of work that is done
   twice, or that no later owner uses, is on the hard-lines list or has a
   reason to stay. Done on 11 November for what the code confirms: the three
   tables and hard lines 5 to 8. Open: the four candidates listed there as
   not confirmed, and a size for the map worker's steps.
5. **Draw less during a zoom** (hard line 3). First the design, with the
   number of pictures it predicts for a 2 s zoom phase; then one batch.
6. **The whole-list hard line** (hard line 1). First the table's history, as
   the ruling of 9 November requires; then a design for the owner's decision.
   Nothing is built before that decision.
7. **One traced run for the causes that are not known.** A collected trace of
   the current build with a heap snapshot in the aggregate phase answers two
   questions. The first is why a gen 0 collection takes 23.5 ms. An object
   that WinUI and .NET both hold, a native WinUI or Win2D object and its .NET
   wrapper, can only be freed by a full collection in which both take part,
   and every one is scanned again in each collection. The trace counts them,
   with handles, finalizable objects and surviving bytes, by owner: table rows
   and cells, event subscriptions on WinUI objects, map brushes and geometry,
   and row objects handed to WinUI. Each owner is then marked as needed (rows
   on screen) or churn (created and dropped per click, per update or per
   draw). The second question is what fills the first UI block of the click
   phase (entry 7 of the list). Naming the native memory needs an elevated
   trace, which needs the owner's permission.
8. **The map's whole presentation for every change** (hard lines 5 to 7), as
   later work: `Arrange` and `Merge` sized by the groups that changed;
   `Present` and `Matches` replaced by the fact that nothing changed; one
   inspection kept while the model stands; the groups of a torrent shown
   before kept. The session of the close destinations plan judged the first
   two not small. None is sized, so the traced run of step 7 first shows the
   map worker's share.
9. **Implement the changes in batches,** each batch with one build and one
   matched round under [deciding a correction](#deciding-a-correction).

## What forces WinUI's full collections

All 70 samples of the `new9` replay that hold `GCInterface_Collect` have one
stack: `CCoreServices::TrackMemory` in `NWDrawTree`. Every 51 render walks,
WinUI reads the process working set. When it has grown more than 50 MiB since
its last trigger, WinUI calls `DisconnectUnusedReferenceSources`, and .NET runs
a blocking full collection of about 100 ms on the UI thread. The app cannot
turn this off
([microsoft-ui-xaml#12113](https://github.com/microsoft/microsoft-ui-xaml/issues/12113)),
so it can only grow its working set less.

Most of that growth was the replay's fixture: a heap dump in the click phase
held 137 MiB of dead large objects, 74 MiB of them byte arrays of peer lists
that the fixture read into new arrays and copied. The fixture now reads like the
product ([benchmark](../../app/tests/Performance.md)). In
the click phase of `new10` large object allocations fell from 40.5 to 5.7 MiB
and forced full collections from 1 to 0, against `new9`; over the whole run
large object allocations fell from 599 to 302 MiB. `new10`'s five remaining
forced collections come while the replay builds large peer views: two while the
all-peers map opens with 159,810 peers, one at the 9,000-peer view, one at the
churn map and one in connection churn. That memory holds the views' peers.

## Pan back during churn

On 10 November the owner asked for HEAD (`bef7363`) against the working tree
(`new18`, batches 1 to 11), five pairs on the benchmark display, alternating,
with no competing load. The working tree was better on clicks, memory and the
maps' first pictures. It was worse at the pan back during churn: the UI thread's
worst response was 287 ms against 224 ms (worse in 5 of 5 pairs) and the worst
navigation gap 283 against 232 ms (worse in 4 of 5).

The other docked zoom and pan phases are mixed. The pan back's worst response
is worse in every pair, but its typical response is better: p95 15.4 to
21.6 ms against 24.6 to 40.0 ms in 4 of 5 pairs. The fifth working-tree run
was disturbed; its p95 was 274.7 ms.

An independent review of the saved runs found what the worst response is. A
zoom or pan phase lasts 2 seconds, and its longest freeze is the UI-thread
block that ends in the frame where a table shows a new sort order: the peer
table every 1 s in the benchmark, the torrent table about every 3 s. The
render thread keeps drawing during the block, so it is not a collection. The
block is not longer in the working tree: over the churn phases, a median of
179 ms for the peer table against 206 ms at HEAD, and 170 against 201 ms for
the torrent table. The working tree reaches the pan back earlier in the
source's ticks, so a torrent-table block falls inside it in 4 of 5 runs,
against 1 of 5 at HEAD. The block itself is the defect in both builds: the
list is told about a new order with a Remove and an Insert notice for each
row it holds, and the frame after the notices measures every visible cell
again.

The peer dots showed in 73% of the pan back's draws in two HEAD runs and in
none of the working tree's. A second review found the cause: the dot size.
`MapDots` lowers the size only after 10 s of lower traffic, and a new map
subject kept the size of the subject before it while every route started again
from zero bytes. After a change from a busy selection to a quieter one, each
dot stood for the busy selection's bytes, so almost no dot flew for 10 s. In
all 5 working-tree runs the first dot of the docked phases came 10.1 to 10.7 s
after the peer table last changed to another torrent's list, inside the pan
west. HEAD's map followed a smaller marquee selection (91 to 192 routes against
627 to 833), so its size was likely smaller and its first dot came after 6.3 to
8.0 s; the records hold no dot size, so this step is inferred. Batch 12 makes
the first traffic after a new subject take its own size at once.

## The owner's answers to the pan-back review

- **A new sort order keeps its row animation.** The review proposed putting a
  new order into the existing rows when most visible rows change, as a switch
  to another torrent does. The owner declined: "the animations is there for a
  purpose. it is acceptable to flip it when you change the torrent, but not
  when you switch the sort order because the animation has a purpose and your
  proposal invalidates the purpose of the animation". Its cost is therefore
  reduced only by what the animation does not need.
- **Pan and zoom draw without the full effects.** The owner: "on pan or zoom
  we don't need full effects. the full effects can appear when we calmed
  down."
- **A cell shows plain text instead of `Field` only where that is proven to
  look the same.** The owner rejected a guess as a hack.
- **The cache rows beyond the screen get no notice.** Approved.
- **Pan input on the render thread is measured before it is built.**
- **The first sort of the all-peers list is investigated.**

## Batch 12: pan effects, notices on screen only, and two findings

- **The glow moves with the map while the view moves.** The glow was recorded
  again whenever its inputs changed, and the view is one of them, so a pan
  recorded it at every step: 481 of 527 pan drawings, 7.3 ms median. While the
  view moves, `MapRenderer` now draws the glow it recorded last, moved from its
  recorded view to the current one, and keeps its strengths and strokes. When
  the view has not changed for a quarter second, the next drawing records the
  full glow again.
- **Only the containers on screen get notices.** `Changes.Reconcile` tells the
  list of the positions it holds between the panel's first and last visible
  index. A container it holds off screen takes its new row through
  `Table.RebindRows` after the layout that shows the screen, or at once on a
  scroll, and reads its selection again. The Jobs sample's reversal reports its
  disagreements after it settles.
- **A new map subject takes its own dot size at once.** `MapDots.Clear` keeps
  the last size for the legend, and the first traffic after it now applies its
  own size at once, as a new area, speed or total does. The 10-second rule
  still keeps the legend steady within one subject.
- **`Field` is not plain text.** Each `Field` cell draws a `FontIcon` and a
  `TextBlock` with a `TextHighlighter` (`TinyTorrentInlineFieldStyle`), and the
  Download, Upload and City cells set a glyph. Plain text would change what the
  cell shows, so the cells keep `Field`, as the owner's condition requires.
- **Where the pans freeze, from the saved runs of `new18`.** A freeze is 50 ms
  or more between two drawn views that differ. The map showed an old view in
  41.6% of the moving time: 167 freezes in 25 phases. In 27.7% of the moving
  time (67 freezes, about 200 ms each) the UI thread was busy for at least half
  the freeze, mostly in the tables' new-order blocks. Pan input on the render
  thread would remove those. In about 14% (100 freezes, about 70 ms each) the
  UI thread was free. HEAD's runs: 49.4% and 37.2%.

  An independent review of the free-UI freezes found that they are real: a
  median of 7 replay steps reached `Navigate` on time inside each one. In 83 of
  99 the game loop drew nothing. In 4.7 s of their 6.8 s a newer view was
  already in `MapMotion`, so render-thread input cannot shorten them. The game
  loop draws about 10 times less often in the middle of the worker's pass than
  near its publish, and longer passes give more freezes: pan east drew 8
  freezes with a 183 ms pass and 1 with a 48 ms pass. The worker draws on the
  game loop's `CanvasDevice`, and Win2D's `CanvasSwapChain` takes that device's
  Direct2D factory lock (`ID2D1Multithread`) to start each frame and to present
  it. So a long Direct2D call on the worker holds the game loop's next frame.
  The capture records no time at which each worker step starts, so the step
  that holds the lock is not named yet. In the other 2.1 s the replay's steps
  reached `Navigate` 20 to 90 ms late while the UI probes answered at once: a
  probe is a sent message, which Windows delivers before posted work, so the
  probes miss waits of posted work.
- **The first sort of the all-peers list.** In the traced run the block lasted
  316 ms. Half of the UI thread's samples were in `CellsPanel.MeasureOverride`,
  36% to 38% were in WinUI with no app frame, and the rest built new row
  elements and their bindings. `ApplyProjection` took tens of ms at most and
  `CaptureSort` 5 to 10 ms; the sort itself runs on the worker. No row on
  screen keeps its place in the first sort, so every row the list holds leaves
  and a new one arrives: about twice the work of a later sort. Only a native
  trace, which needs elevation, can split WinUI's share.

In a 60-row test list the list itself dropped its containers below the screen
in the layout after a sort, and made them again with the right rows and
selection within 1.5 s. So off screen, `RebindRows` matters for the containers
the list keeps, such as a focused row's. No test reaches that case: a test list
keeps no container below the screen through a sort.

Built on 10 November with no warnings (`new19`). The TableView reconcile,
sorting and selection tests passed.

### Round of batch 12

Five pairs of `new18` (`bfc54dc`) against `new19`:

- The glow took 0.1 ms per pan and zoom drawing instead of 7.5 ms (median),
  and the arcs 16 ms instead of 38 ms. Freezes with the UI thread free fell
  from 11.4% to 3.1% of the moving time.
- `new19` crashed at exit in 3 of 5 runs: the queued rebind of containers off
  screen (`FlushRebind`) read the list after the window had closed. It now
  returns when the table is unloaded, as every other queued table callback
  does. `new20` then ran 6 times without a crash.
- Freezes with the UI thread busy rose from 26.1% to 38.4% of the moving time.

To isolate the notices on screen only, five pairs ran `new20` with that change
switched off (`Shown()` returned null) against `new20`, both with the dot fix:

| Measure | Off | On |
| --- | ---: | ---: |
| Moving time with the map frozen | 24.8% | 32.1% |
| Torrent-table new-order block, median | 143 ms | 228 ms |
| Peer-table new-order block, median | 168 ms | 137 ms |
| UI blocks near no new order | 3 | 15 |
| First sort of the all-peers list, worst UI response | 295 ms | 156 ms |
| Zoom-in, worst navigation gap | 206 ms | 278 ms |

The change halves the first big sort, but it makes pans and the torrent table
worse. With it off, a switch to another torrent rebinds every row at once,
which is slower than before batch 12, so the click results of this round do
not judge it.

The first dot now flies 5.7 to 6.1 s after the marquee starts, in zoom-out, in
all 10 runs, against 12.1 to 12.8 s, in pan-west, before the dot fix.

### Second round: the table source at HEAD against the notices on screen only

On 11 November the owner asked for the measurement again. `new21b` has the
TableView source exactly at `HEAD`; `new21` has the notices on screen only.
Both carry three map changes: a refined guide keeps its core brush, guides are
refined only when the view rests, and a parked route releases its strokes.
One `new21` run stopped with "The final peer table did not publish": the peer
table did not show the heavy torrent's list within 20 s.

| Measure, pans and zooms | `new21b` | `new21` |
| --- | ---: | ---: |
| Moving time with the map frozen | 34.2% | 39.2% |
| of it with the UI thread busy | 17.9% | 26.8% |
| Torrent-table new-order block, median | 127 ms | 171 ms |
| UI blocks near no new order | 2 | 8 |

The notices on screen only made movement more jagged in both rounds. The
review that proposed them is asked to trace why, through the table code and
WinUI's source.

The map changes made each drawing shorter, and the worker drew more of them:
against `new20`, the arcs step fell from a median of about 14.6 ms (p90 about
55 ms) to about 10.2 ms (p90 15 to 21 ms), and the drawings rose from 29 to 31
to 41 to 46 per second. The game loop then drew 49 frames per second instead
of 55, and the map froze more with the UI thread free. A cheaper drawing only
helps when the worker also draws less often during a pan: proposal 2 of the
pan review. The review that proposed it is asked for the design. Private
memory still grew by 3.0 to 4.3 MiB/s, against 4.4 to 6.0, so the parked
strokes are not the main cause of the climb.

### Third round: the border stall

The other review traced the freezes with the UI thread free to one drawing
step. The guides no longer used the 6 ms rebuild budget during a zoom, so the
fills traced every visible country again at each new outline detail, and the
borders then made a stroke for every new piece with no budget at all
(`e723ae6` had removed that budget when drawing moved to the worker). One such
drawing held the device the game loop presents with for 0.6 to 2.8 s. The fix,
committed as `605838d`: while the view moves, outlines keep their detail and
are traced once at rest, as guides are; a piece's first stroke takes the
budget; and the map reports ready only after the work left for its rest is
drawn.

`new22` (the fix) against `new21`, five pairs, all ten runs finished. Logs:
`round22.log` in the session scratchpad, with `stepmax22.ps1`, `panfreeze.ps1`
and `loopframes.ps1`.

| Measure, pans and zooms | `new21` | `new22` |
| --- | ---: | ---: |
| Longest borders step, per run | 746 to 1,799 ms | 0.7 to 5.6 ms |
| Longest fills step, per run | 32 to 143 ms | 1.4 to 16.9 ms |
| Moving time with the map frozen | 37.9% (149 freezes) | 24.9% (79 freezes) |
| of it with the UI thread free | 12.5% | 2.0% |
| Game-loop frames with a new view | 36.3/s | 43.8/s |
| Worker drawings | 34.9/s | 53.9/s |

What remains is 22.9% of the moving time frozen while the UI thread is busy,
mostly with table re-sorts. The owner first approved holding the peers table's
re-sort during a map gesture, then withdrew it: "I think we shound't hold it
right now and we should investigate how can winui3 dredraw smooth animation in
2 table during this heavy load yet we can't pan properly".

*Why do the tables animate smoothly while a pan freezes?* (Review of WinUI's
source, 11 November; nothing run.) Row animations are independent animations:
the UI thread starts them once and the compositor plays them
(`DoubleAnimation.cpp:121`, `timemgr.cpp:346` in `3rdParty/microsoft-ui-xaml`).
One table update is one UI block of about 165 ms that lays out the rows and
starts their animations; in that block no code reads the mouse, so the map has
no new view. WinUI's own scrolling takes touch, pen, touchpad and wheel input
off the UI thread (`ScrollPresenter.cpp:4582-4634`, `2798`), but not a mouse
drag. For swap chain content the platform's way is an input source made off the
UI thread (`HWCompNodeWinRT.cpp:3517`), the render-thread input item of the pan
review. Open, because `InputPointerSource` is closed source: a mouse drag that
leaves the map, input to the controls above the map, and click, double-click,
menu, hover, cursor and focus on raw pointer events. On 11 November the owner
asked for a prototype that answers those questions.

### Fourth round: arcs that drew the map again

After `605838d` a pan still drew a new picture at almost every frame. Capture
builds now record what made each drawing, and nearly every one was asked for
by the drawing before it. An arc whose stroke was made at another scale asked
for another drawing when the 6 ms budget had run out. The arcs draw last, so
with 1,000 arcs every drawing ran out before the later ones, and none was ever
rebuilt. The fix, committed as `a1cb18b`: such an arc keeps its stroke and
waits in the restyle queue, which passes that draw nothing rebuild. With that
loop gone, a pan that the latest picture still covers draws nothing new until
the view rests.

`new25` (the fix) against `new24`, five pairs, all ten runs finished. Logs:
`round25.log` and `compare25.md` in the session scratchpad, with `causes.ps1`.

| Measure | `new24` | `new25` |
| --- | ---: | ---: |
| Drawings in each pan of pair 1 | 69 to 134 | 24 to 29 |
| of them asked for by the drawing before | all but 2 | 0 to 5 |
| Zoom-out drawings, per pair | 61 to 89 | 54 to 74 |
| No-selection animation, worst UI response (median) | 311 ms | 158 ms |

No measure was worse in all five pairs. The zoom-out's UI-thread measures were
worse in 4 of 5 (navigation updates 41.1 against 37.1 per second). The worker
drew less there, and no path from its work to the UI thread was found, so this
is read as noise. The pan drawings that remain are activity fade steps, about
10 a second.

### Fifth round: notices bounded to the cache

Counters in the table showed why the notices on screen only made the torrent
table slower. The list kept every container it was never told about, 35 for
10 rows on screen. So each new order refilled about 24 rows off screen, at
about 3 ms a row. Now a held position farther from the screen than the cache
reaches, `ceiling(CacheLength × rows on screen)` rows, is told again, and the
list drops it. Only the rows within the cache are refilled without a notice.

**Dropped, 11 November.** An independent review against the WinUI list
source, and a second trace by another session, found that refilling a
container without a notice breaks the owner's ruling that the table never
flashes and never breaks the list's animation:

- The positions on screen stay fixed while the list applies the told joins and
  leaves, so each net leave at or above the screen moves a quiet cache
  container onto the screen. It shows the row it had until the rebind at Low
  priority. A row on screen can also play a leave and an arrival for a move
  that did not happen. A peers table sorted by rate does this when a visible
  peer disconnects.
- WinUI records the item each container was prepared for, and a refill does
  not change that record. `ContainerFromItem` can then return a container that
  shows another row, so Narrator and UI Automation can read or select the
  wrong row. The different-list rule of hard line 2 has the same defect where
  the two lists share rows.
- With rows selected, every update re-read the whole selection, and the cache
  estimate was twice the panel's real cache.

These follow from refilling WinUI's containers without telling WinUI, so a
fix is a new design, not a correction. The table's lookups by item,
`Table.ShownRow` and `Table.ContainerItem`, hold only while no item a
container was prepared for is also the current row at another position; a
new design keeps that true. The code was taken out of the tree.

`new26` (bounded) against `new26h` (TableView source at `HEAD`), same map code,
five pairs, all ten runs finished. Logs: `round26.log`, `compare26.md` and
`disconnect26.txt` in the session scratchpad.

| Measure | `new26h` | `new26` |
| --- | ---: | ---: |
| Torrent-table new-order block, median (5 of 5 pairs better) | 132 ms | 98 ms |
| Peer-table new-order block, median (5 of 5 pairs better) | 135 ms | 106 ms |
| UI blocks over 100 ms near no new order | 2 | 0 |
| Click to selection, median (5 of 5 better) | 22.7 ms | 10.5 ms |
| Opening the selected torrent's map, worst UI response (5 of 5 worse) | 160 ms | 230 ms |
| Its first picture (5 of 5 worse) | 294 ms | 380 ms |

The torrent table now holds 14 containers after the first orders, as at
`HEAD`. Also worse in 5 of 5: the first picture after the selection settles
(489 against 519 ms) and the worst UI response when the churn map opens (103
against 125 ms). Round 21's saved runs show the same slower opening with the
unbounded notices, so the notices on screen only cause it, not the bound.

The opening is slower because the peer table builds its row containers later,
not because it does more. Going from all peers to one torrent's peers removes
the 16 rows the list holds, with an animation. A removed container cannot be
reused until its animation ends, so the list needs 16 others at once. With the
table source at `HEAD`, the first re-sort of all peers had already built 16
spare containers; with the bounded notices it built 6, so the opening builds
the other 10 (about 85 ms of cell measure). In all five pairs the two blocks
together were shorter with the bounded notices: 342 to 421 ms against 455 to
522 ms. The review proposed that a change of subject that keeps none of the
rows the list holds replaces the list in one step, as a switch to another
torrent's peers already does. The owner then gave the rule for every change of
the data source, which is
[hard line 2](#hard-lines-that-waste-memory-and-cpu).

### Sixth round: the game loop follows a mouse drag

While a mouse drag is active, the map's game loop reads the cursor at each
frame and moves the view itself, so a pan no longer waits for the UI thread
(hard line 4, for a mouse drag only). The owner tried it by hand under the
replay's load and judged it "works outstanding. follows perfectly".

`nf1` (the drag change, committed as `689ab23`) against `nf1h` (`HEAD` with
the same harness), five
pairs, all ten runs finished. Every pan of `nf1` reports that the game loop
followed the drag. Logs: `round33.log` and `compare33.md` in the coordinating
session's scratchpad.

| Measure | `nf1h` | `nf1` |
| --- | ---: | ---: |
| Pan east, worst gap between drawn views (5 of 5 better) | 187 ms | 48 ms |
| Pan back, worst gap (5 of 5 better) | 150 ms | 65 ms |
| Pan west, worst gap (5 of 5 better) | 116 ms | 31 ms |
| Pans, drawn views per second (5 of 5 better each) | 50 to 52 | 58 to 60 |
| New views drawn while the UI thread is blocked, pans and zooms | 1.4/s | 28.3/s |
| Click to rendered selection, median (5 of 5 worse) | 33.6 ms | 45.5 ms |

Zooms still reach the map through the UI thread, so the views drawn while it
is blocked come from the pans. The slower rendered selection was not
reproduced, so it is closed
([seventh round](#seventh-round-the-batch-of-11-november)). Pair 4 differed by
0.1 ms, the other four by 7 to 23 ms.

### Seventh round: the batch of 11 November

Two rounds ran against one reference, `nfc-ref`: `689ab23` with the close
destinations plan's wait at a rest.

**Round 34** tested four changes in one build: the rest merge
(`MapWork.Prepare` merges alone when only the shown scale changed), a reserve
for the map's contributions, a `ShowSwarms` that joined one kept swarm in the
click's own UI turn, and a correction to the order of two calls in
`MapMotion`. The worst wait of a replayed step for the UI thread rose from 193
to 267 ms in 5 of 5 pairs, and the zoom-in's worst UI response from 139 to
252 ms in 4 of 5. The UI thread did no more work in total; the same work came
in larger pieces: two UI blocks of 240 ms or more in every run, against one in
five runs. With one kept swarm, the peers, the filter, the map and the pane's
refresh ran in the click's turn, before the selected row's frame, and the UI
cycles of a click rose from 55 to 138 megacycles at the 90th percentile,
pooled over about 100 clicks. Both UI-thread changes were taken out of the
tree. Logs: `round34.log` and `compare34.md` in the coordinating session's
scratchpad.

**The click render after `689ab23` is not slower.** `nfc-ref` holds the drag
code of `nf1`, byte for byte, and its median click render was 34.9 ms in round
34, against 33.6 ms for `nf1h` and 45.5 ms for `nf1` in the sixth round.
Pooled over about 100 clicks per build, `nfc-ref` had the shortest wait from
selection to rendered frame of the four builds: 30 ms at the 90th percentile,
against 62 ms for `nf1h`. So the correction in `MapMotion` had nothing to
correct.

**Round 35** tested the rest merge and the reserve alone (`batch2`). Another
session's scripts loaded one core during pairs 1 and 2, so pairs 6 and 7
replaced them. Logs: `round35.log` and `compare35.md`.

| Measure | `nfc-ref` | `batch2` |
| --- | ---: | ---: |
| Worst wait of a replayed step for the UI thread (5 of 5 better) | 219 ms | 206 ms |
| Torrent clicks, worst UI response (4 of 5 better) | 212 ms | 189 ms |
| Connection churn, worst UI response (4 of 5 better) | 179 ms | 148 ms |
| Zoom-in, worst UI response (2 better, 3 worse) | 160 ms | 150 ms |
| Peak private memory (2 better, 3 worse) | 728 MiB | 713 MiB |
| Settlement after the clicks, worst UI response (5 of 5 worse) | 33 ms | 147 ms |
| Pan east, worst navigation gap (5 of 5 worse) | 37 ms | 65 ms |
| Idle map, draws per second (5 of 5 worse) | 60.2 | 59.9 |

The three measures that are worse in 5 of 5 pairs are edges of their phases,
by the saved runs:

- **Settlement.** A UI block of 90 to 250 ms comes about 400 to 500 ms after
  the last click in both builds: 124, 140, 245 and 121 ms in four reference
  runs, and 197, 154, 89, 147 and 125 ms in the batch. The phase ends when the
  map reports ready. In the reference it ended before that block in three
  runs; in the batch it ended after it in four. Over the clicks and the
  settlement together, the worst UI response is better in the batch in 4 of 5
  pairs. Borderline behind it: the game loop's frames show the selected
  torrent's land a median of 332 ms after the last click, against 295 ms
  (later in 4 of 5 pairs).
- **Pan east.** The report's gap includes the time from the last drawn view
  to the phase's end, and the harness sets that end on the UI thread. A UI
  block held the end 55 to 89 ms after the last view in four batch runs, and
  63 ms in one reference run. Between two drawn views the longest gap was 17
  to 30 ms in both builds.
- **Idle map.** One frame in a phase of 3 s. The same reference binary
  measured 59.7 in round 34, where the build with the same two changes
  measured 60.1.

In round 34 the same measures pointed the other way with the same two changes
in the build: the settlement was better in 5 of 5 pairs and the pan east in 4
of 5. Neither change runs on the UI thread. The rest merge runs only after the
shown zoom changed, and the reserve does nothing once the dictionary is large.
The arcs step that took 892 and 897 ms in two zoom-outs of round 34's batch
took 7 to 25 ms in every batch run of round 35. The rest merge and the reserve
are retained, and the reference moves to them.

## Allocation owners still open

Sampled allocation owners that no correction has addressed, from the `78fb43f`
allocation trace (sampled tick amounts, not a census), read by owning frame and
by phase in its preserved
[phase grouping](../../artifacts/evidence/ui-profile-alloc-current-20261106-78fb43f/phase-grouping.md).
The journey is 59.3 s; its aggregate phase is 13.6 s with the whole population
loaded.

| Owner | Thread | Sampled allocation over the journey | Standing |
| --- | --- | ---: | --- |
| `Peer[]` replacement lists in `ApplyPeers` reconciliation | worker | 119 MiB in the aggregate phase | No correction selected. |
| Inspector `Filter` capture | worker | about 24 captures in the aggregate phase, 1.7 per second | One 4.9 MiB value array per capture. The fixture delivers about one reply burst per second, so coalescing captures per burst cannot move an endpoint here. [Issue 573](https://github.com/SynoSoftware/TinyTorrent2/issues/573), which asked for it, is closed: the map takes a round's changes once, and a chosen place's search runs one at a time. |
| `Dictionary<DestinationKey, Destination>` rebuilt by `MapRenderer.Inspect` every render pass | render worker | 137 MiB | About 1,000 entries, generation 0, at pass rate during navigation. [Issue 576](https://github.com/SynoSoftware/TinyTorrent2/issues/576). |

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
- At rest after a zoom, outlines are retraced in slices of the 6 ms budget, and
  each unfinished slice draws a whole picture: 45 pictures in one churn phase of
  round 25 (`again rebuild Trace`). Running the slices in passes that draw
  nothing, as the arcs now are, and drawing once at the end would remove them.
  It changes the contract's rule that the retrace keeps the allowance.
- Merging map destinations that lie close together at the shown zoom has its
  own [plan](20-close-destinations.md), decided by the owner on 11 November.
  It changes the peer location contract, which today merges only an estimated
  group's places, in fitted-map space, and groups cities with coordinates only
  at identical positions.

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
| [Defined changes `4f069bb`](../../artifacts/evidence/ui-profile-map-defined-changes-20261107-4f069bb/README.md), [build](../../artifacts/evidence/ui-build-map-defined-changes-20261107-4f069bb/README.md) | Current reference; mixed. |
| [Country full-screen run](../../artifacts/evidence/ui-country-fullscreen-20261107-4f069bb/comparison.json) | Visual review of the accepted artifact. |
