# Keep up with the person

Status, 9 November 2026: revision 16. Steps 2 and 3 are committed at
`d72c4d5` and met their predictions in every pair of rounds 2 to 4. Step 1 is
redesigned: a table that switches to a different list keeps the rows on screen
and changes what they show. It is implemented in the working tree, and the
peer table switch capture shows a switch at about 40% of its earlier cycles in
every pair (see Round 5). Its first design, rows filled in phases, made the
median click slower in all 15 pairs of rounds 1 to 3 and is removed. The
owner asked for "clear improvements and proof in all fields touched".

In this plan, "today" means the reference build: HEAD `2967cdd`, without
steps 1 to 3.

Purpose: a click on a torrent switches the selection at once and is never lost,
and the map of the selected torrent shows its land before its connections.
Step 1 changes TableView, so it starts from the table's history, as the
[rendering plan](17-rendering-and-selection.md) requires for every TableView
change.

## What the person sees

1. When the peers table switches to another torrent, the rows on screen stay
   and show the new torrent's peers from its first row, with no animation.
   The next click no longer waits for WinUI to build, animate and draw a new
   row for every row on screen. Changes within one torrent's peers, such as a
   sort or peers joining and leaving, animate as today.
2. The map of a newly selected torrent shows its land first, with the country
   shading. Then it shows the arcs with the markers of places that have a
   connection. Then it adds the markers of the other places. Each layer is
   drawn once. The third publication follows the second without any drawing;
   only the markers and the inspection of every place are computed between
   them, and the plan does not measure whether that fits in one frame.
3. A press on a row selects a row, except in the cases Step 3 lists: a row
   that has left the view, and a row that is still invisible as it arrives.
   In the diagnostic run, 5 of 21 presses selected nothing.

The owner's words, in order:

- "click moves cursor immediatelly. after that it does the rest."
- "my requirement is to be able to switch the selection immediatelly", and "in
  the end the UI needs to move a rectangle from old to new... then start
  canceling the old and start working on the new." This replaces the earlier
  request that "a table updated should be async, filled in only when it
  doesn't bother others", which the owner withdrew after Step 1's first design
  failed.
- "we must not fall behind user. keep up with the user!!!! that means drop
  queued work that user doesn't want to see anymore." A pending click cancels
  display work, never a command: "if you're in the middle of executing Apply
  button and I close the dialog, doesn't mean you're canceling apply."
- "if all seeding of peers into tableview finish and we're losing clicks -
  thats a bug".
- "The map draws land first, then connections, then unconnected peers", and
  "map draws only once --- this is a matter of ordering the rendering, not of
  making more work".

Step 1 answers the second request. The remarks on `Body.Changes.Reconcile`
record an older owner requirement that still holds within one list: leaving
rows animate out, arriving rows animate in, "and the table is never empty in
between". A switch to a different list follows the owner's later rulings
instead (see Step 1).

## Evidence

The reference is run `127c126b` of `performance-selection`: a visible window,
native clicks, city detail, a limit of 1,000 arcs, and 21 clicks 150 ms apart.
HEAD `2967cdd` has the same code.

| Endpoint, `torrent-clicks` | Reference |
| --- | --- |
| Click to rendered selection | median 79.7 ms, p95 and worst 546.5 ms |
| Presses that selected nothing | 2 |
| Selected but never observed rendered | 6 |
| UI-thread response (window-message probe) | p95 109.7 ms, worst 431.9 ms |

An elevated native CPU trace of the same run (WPR, symbols from Microsoft) gives
4,836 UI-thread samples, about 1 ms each, inside the UI pauses of 250 ms or
more. The tracer's own stack walks are removed. The numbers overlap, because one
sample counts under each function on its stack.

| Work on the UI thread inside the pauses | Samples |
| --- | ---: |
| Layout forced by a pointer hit test (`CUIElement::HitTestEntry` calls `UpdateLayout`) | 932 |
| Layout in the render tick | 704 |
| List realization (`ModernCollectionBasePanel::RunVirtualization`) | 1,078 |
| ... of which: row prepare and measure (`SetupContainerContentChangingAfterPrepare`) | 657 |
| ... of which: cells measured by `CellsPanel.MeasureOverride` (text 409, data context 203) | 793 |
| Render walk, text glyph textures included (751) | 1,141 |
| Rows drawn while they animate (`LayoutTransitionElement`) | 589 |
| Table change notifications (`Body.View.Apply`) | 19 |
| Garbage collection | 3 |

What this shows:

- A pause is WinUI making and drawing changed rows, not the table's own
  notifications.
- A click pays for any layout that is waiting, because the hit test runs it
  first. So a lower dispatcher priority cannot protect a click. Only a smaller
  pending layout can.
- The list sets each prepared row's content and measures the row, with all of
  its cells, inside the layout that realizes it. WinUI's source shows this:
  `ListViewBase::SetupContainerContentChangingAfterPrepare` sets the content
  and measures at once unless a handler marks the event handled.

Map work in the same run, for the first picture of the selected 1,000-arc
torrent (`selection-map-settlement`): fills 0.26 ms, borders 0.19 ms, glow
890.6 ms, arcs 823.3 ms. Today the land waits about 1.7 s for the arcs,
because one picture publishes all layers together.

## Step 1. A different list switches in place (TableView)

**What the click waits for.** The app already follows the owner's order. A
press moves the selection; in the same handler `Inspector.QueueSubject`
cancels the old torrent's reads (`Invalidate`, which also stops `ApplyPeers`
row by row) and starts the new torrent only after the selection's frame
(`FrameCallback`). The press waits for work already handed to WinUI. During
fast clicking the UI thread waited for messages only 2% of the time. The saved
traces of 9 November (reference run `557407bf`, 12 clicks, 1,364 ms from
press to rendered selection in all) attribute that time:

| Work on the UI thread | ms | Share |
| --- | ---: | ---: |
| Garbage collection, including one 263 ms induced full collection | about 460 | 34% |
| WinUI drawing | about 410 | 30% |
| App code | about 230 | 17% |
| WinUI input, the hit test included | about 110 | 8% |
| Layout | about 50 | 4% |

Inside the long pauses, layout took 829 of 1,902 samples, 401 of them in
`CellsPanel.MeasureOverride` measuring new cells, and drawing rows that animate
took 589. The managed wrappers of new cells' WinUI objects
(`ContentPresenter`, `TextBlock`, `ListViewItem`) report memory pressure
(`GC.AddMemoryPressure`) as they are created, which counts toward the next
collection.

The peers table causes that work. Another torrent's peers share no row with
the shown peers, so `Changes.Flip` removes every row the list holds and inserts
a new one at each position: the list builds a new row for every row on screen
and animates the old rows out while the new ones arrive.

**History since `v0.9.1.2`.** The same switch went back and forth:

| Version | A switch to a different list | Why it ended |
| --- | --- | --- |
| `v0.9.1.2`, 31 October | One removal and one arrival per row for the whole list, animated. | Replaced by the rework. |
| `42a815f`, 1 November | The same notifications, animation off. **Owner ruling: the table never flashes.** The list's own removals finish before its delayed arrivals, so an animated switch shows no rows in between. | The revert below. |
| `ce8f6b6`, 3 November | Also scrolls to the first row. **Owner ruling: a different list starts at the top**, because the new rows at the old offset show "an arbitrary middle of a list they have not seen". It tracked every row each frame laid out. | The revert below. |
| `25da2fe`, 3 November | Revert of all of TableView to `v0.9.1.2`. | Made for memory: the rework held about 788 bytes per row against 266. The two rulings above cost no memory per row; they left with the files that held them. |
| `65c1473`, 3 November, today | `Flip`: rows on screen animate out and in, other positions change without a notification. | Builds every row on screen, the work the next click waits for. Neither ruling came back. |

**What changes.** `Changes.Flip` puts the new list's row at every position
without a notification and raises only the change in count, at the end. The
containers the list holds stay; `Table.RebindRows` gives each the row now at
its position, so its cells rebind instead of being built. Nothing animates
except rows at the end that join or leave. Then `Table.ShowDifferentList`
scrolls to the first row unless the host's `ScrollTo` waits for this view, and,
when the old list had selected rows, makes the list read every container's
selection again through one range call.
Two lookups that matched rows by identity now go by position, because after a
switch the list's own record of a container's row is a row of the old list:
`ShowCurrent`, which corrects a container the list fills later, and the
automation peer's row (`Table.ShownRow`). No state is added per row; the
switch allocates less than `Flip` did.

This is better than each earlier attempt: unlike `42a815f` it builds no row,
unlike `ce8f6b6` it tracks nothing per frame to start at the top, and unlike
today it neither builds nor animates.

**Done when:**

- The source review shows that a switch to a different list raises only the
  change in count, that every container shows the view's row at its index
  afterwards, and that selection highlight and automation follow the shown row.
- The TableView tests pass, with the reconcile test for a different list
  changed to the new count of notifications.
- The peer table switch capture shows fewer UI-thread cycles per switch in
  every pair of five, and round 5 shows no endpoint worse in every pair.

### Tried first: rows filled in phases, removed

**What it did.** A row that the list realized while a view change waited for
its layout (`Table._layoutPending`) showed its first cell at once. Its other
cells stayed collapsed, without the row, until the table filled the row. A
waiting row reported the height of a filled row, so rows did not move when they
filled. Two designs filled the rows:

- Rounds 1 and 2 filled each waiting row from the list's phase-1 callback
  (`RegisterUpdateCallback`). The WinUI source shows that the list runs that
  build work inside the UI tick, until the time since the end of the previous
  frame passes the list's budget: 40 ms by default
  (`BUDGET_MANAGER_DEFAULT_LIMIT`). A press that arrives during that tick waits
  for all of it.
- Round 3 filled one row per Low-priority dispatcher item and laid that row out
  in the same item (`UpdateLayout`), skipping rows whose containers the list
  had recycled.

**What it measured.** Values are ranges over the five pairs of each round.

| Endpoint | Round 1 | Round 2 | Round 3 |
| --- | --- | --- | --- |
| Click to rendered selection, median, reference | 41–75 ms | 44–64 ms | 36–41 ms |
| The same, new | 78–115 ms | 54–73 ms | 45–85 ms |
| UI frames per second from the release to the arcs, new against reference | not computed | 10–21 against 10–18 | 25–31 against 14–18 |
| The selected map's glow drawing, new against reference | not computed | mixed | 828–1,077 against 356–491 ms in 4 of 5 pairs |
| `firstConnectionsMs` | later in 4 of 5 pairs | later in 3 of 5 pairs | later in every pair, by 271 to 1,151 ms |

The median click was slower in all 15 pairs. In ticks, which are the UI
thread's own CPU cycles (`QueryThreadCycleTime`) and which other programs on
the laptop do not change, round 3 shows where the time went: a click's own work
from the press to its rendered selection was mixed (median more in 3 of 5
pairs, worst less in 4 of 5), but the UI thread spent more cycles on the 21
clicks in 4 of 5 pairs, 4.42 to 4.90 G against 3.36 to 4.86 G.

**Why it failed.** Splitting the row work does not make it smaller. During fast
clicking the UI thread is busy most of the time in both builds: in round 1,
2.0 to 2.9 s of the 3.15 s of clicks ran in frames longer than two refreshes.
So the pieces ran where the next press landed, and each piece added its own
layout pass or frame. In round 3, one row per frame also kept the window
drawing about twice as many frames for the 1 to 2 s after the clicks, and in 4
of 5 pairs the map's glow drawing took about twice as long, so the arcs came
later. That link is not proven: in round 4, without Step 1, the frame rate
matched the reference, and the glow drawing still took 430 to 1,430 ms in both
builds (see Round 4).

**What is left.** Only a smaller row makes the click faster. The table's own
row code is already small: `CellsPanel` creates one `ContentPresenter` per
visible column once, sets its content when the row's item changes, and measures
each cell once. The cost is WinUI inflating each cell's `DataTemplate`, binding
it and measuring its text. Making that smaller changes how the table builds a
cell, for every host. The rendering plan excludes that redesign ("no
publication redesign or broad table investigation"), so it needs the owner's
decision before any work.

**Removed with it:** its TableView code, its paragraph in contract section 5.3,
and its row in the implementation map. Section 5.3's reference to the reference
host's diagnostics now names section J (`ProbeQuietPlacementAsync` in
`lib/TableView/sample/Jobs/JobsPage/Diagnostics.cs`), because section R no
longer exists.


## Step 2. The map draws land, then connections, then unconnected places

This applies only to the first picture of a new subject, which is the
selected torrent or peer scope. A later picture of the same subject publishes
whole, as today. Otherwise every poll would make arcs disappear and come back.

**Preparation.** `MapRenderer.Prepare` builds arc geometry for exactly the
routes it chooses to show (the active routes with the highest rates, up to the
arc limit). So the state that today reads `route.Arc is not null` can read that
set instead:

- `_arranged` and `_arrangement`, which are the glow cache key;
- the origin opacity;
- the fade's change, steps and start time.

That state then stays in the first part of `Prepare` with the same values, and
the land phase draws the country shading with a fresh fade. Only arc geometry,
traffic and route bounds need the arcs. They move to a second part that runs
before the connections phase. The non-phased path runs both parts back to back,
as today.

The fade lasts 1.6 s, and each fade step is a later render pass. When a
torrent's arcs and glow take longer than that (1.7 s in the reference run),
the next pass finds the fade finished. A country that both torrents share then
keeps its starting shading until that pass and takes its new shading without a
visible fade. Today that torrent behaves the same way, because the same draw
time passes before its next pass. The difference is that the land of the new
torrent is on screen during that time, instead of the old torrent's picture.

**Publications.** The render worker leases one picture and keeps the lease's
own reference until the last phase or a cancellation. Each publication adds a
reference with the existing `Retain`, as the rate-only republish does. Each
layer is drawn once.

1. **Land.** The worker draws the bitmap target, which holds the fills,
   country shading and borders. It closes that drawing session, then
   publishes:
   - `Scene = null`, so no dots fly and `ShowsConnections` is false;
   - no markers;
   - an inspection with a new, empty list of places;
   - a flag that tells the game loop not to draw the arcs target.
2. **Connections.** The worker builds the missing arc geometry, then traffic
   and route bounds, then draws the arcs target (glow and cores). The game loop
   does not read that target before this publication, so the worker never
   draws a target that the game loop is drawing. The worker publishes the
   scene, the markers of places with a connection (`Group.Connections > 0`),
   and an inspection that holds a new list of those places.
3. **Unconnected places.** The worker publishes the same picture with the
   markers and an inspection list extended by the other places. Each phase
   computes the markers of the places it shows; the plan does not measure
   that time.

Each phase's inspection has its own list of places. So `DisplayChanged` fires
once per phase, and the accessible description and hit testing follow what is
shown. The map keeps `_appliedInput` unset until the last phase, so a pass
cancelled between phases is drawn again. The refinement passes
(`_rebuildPending` and `_again`), route reuse by destination key, glow caching
and the reuse of pictures through the free pool do not change.

**Country shading is land.** The shading for countries that have only
unconnected peers is drawn in the land phase. Drawing it later would draw the
fills twice, and the owner ruled that the map draws once.

**A new map traces its whole land first.** After round 1, the owner watched a
run and saw the arcs before the map. When a map opens, it has no country
outlines yet. Each drawing traced outlines only within the renderer's 6 ms
rebuild allowance, so in round 1 the land took 14 to 24 drawings, about 6 to
12 ms of tracing each, while the arcs were complete after the first two.
Tracing the whole world took 119 to 307 ms in total, and each of those
drawings drew the 1,000 arcs again. Now
`MapRenderer.TraceLand` traces every country that has no outline yet before the
land is drawn, without the allowance, so the first picture shows the whole
land. Retracing after a zoom keeps the allowance, because a complete coarser
outline stays on screen meanwhile. New country fills keep the allowance too:
in round 1, all of them took at most 3.3 ms in one drawing.

**Cancellation.** A new subject already cancels the old render request
(`MapMotion.Changed` compares `Model.Subject`). The map takes the new subject
when the next torrent's rows arrive; until then it keeps its last picture
(`Inspector.Filter`, and `InspectorPane.RefreshMap`, which waits while
`IsReplacing`). Between phases the worker checks the request's cancellation.
So when the next rows arrive before the old torrent's connections are drawn,
the old land stays on screen until the next subject's land replaces it. When
they arrive later, the old connections are published first.

**Contract.** [Peer location](../contracts/peer-location.md) now describes the
three publications of a subject's first picture, and keeps "one immutable
result" for each publication. The render order under "Arc dimming" gives the
layers within one frame, which this plan does not change.

**Done when:**

- The source review shows that each target is drawn once per picture, and that
  the game loop never reads a target before the publication that completes it.
- The review shows that a picture returns to the free pool exactly once, after
  the game loop and the worker are both done with it: after three
  publications, and after a cancellation that follows one or two.
- The review shows that a pass cancelled between phases is drawn again, and
  that `DisplayChanged` fires once per phase.
- When the map opens in `inspector-all-map-open`, the work record shows one
  drawing that traces the land, not 14 to 24.

## Step 3. Find and fix the lost presses

A lost press raises no `SelectionChanged` for its torrent, because the harness
counts a selection that `MainViewModel.Select` later refuses as selected. So
the table dropped it. In the capture build, the harness reads the table's
gesture state by reflection at two moments:

- just after the table handles the press;
- just before it handles the release, from a handler on an element inside the
  list.

That separates four cases:

- the press armed no gesture;
- a press applied at once that selected nothing;
- a deferred press that `CancelGesture` ended before its release, for example
  through a host `Selection` assignment;
- a release that dispatched nothing.

The harness also records what each press landed on (`PressedOn`): a row, a row
animating out, whose container has no index, or no row.

One diagnostic run of the reference build, before the new build, records which
case occurs. The new build fixes that cause at its owner, because the owner
ruled that losing clicks is a bug. The new build keeps the record, so its runs
show the count.

**What the diagnostic run found** (reference build, run `c2e0866f`). 5 of 21
presses selected nothing. None of them is one of the four cases above;
`PressedOn` found the causes:

- The first click's work held the UI thread for about 500 ms while the next
  clicks waited in the input queue. In that time the torrent list re-sorted.
  The table keeps the pointed row in place, but it knew only the pointer
  position from before the queued moves, so six of the seven rows moved.
- 3 presses landed on rows animating out. Their containers have no index any
  more, so `Table.HitTest` refused them.
- 2 presses landed where the arriving rows did not take hit tests yet, so the
  hit test saw empty row surface. One of them cleared the selection.

**Fix.** `Table.HitTest` targets the row the list has laid out under the press
when the table handles it. A row animating out is still visible where the
person pressed, so it targets the row it shows, where that row now is. A press
that reaches the surface takes the row the list has laid out under it, which
is a row animating in. Contract section 13 states the rule.

A press that waited in the input queue while the list changed its rows is
applied to the new layout, so it can target a row that no frame had shown when
the press was sent. The current design cannot avoid that, because the table
knows only the layout at the time it handles the press and keeps no record of
earlier frames.
A Win32 list view has the same limit: it hit-tests a queued click against its
current layout. In the capture build, each press records when the last frame that showed a view change of the
torrent table was committed, or the time of the press when a view change waits
for its first frame. The report counts the presses sent before that time plus
two refreshes, which allows for the compositor to put a committed frame on
screen.

**Decision: a press on a row that is still invisible as it arrives does
nothing.** When the list changes its rows, the platform's row transitions fade
the leaving rows out, then keep each arriving row invisible for 300 ms before
it fades in. The hit test does not find an arriving row: in round 1 it found
none at presses sent up to 550 ms after the view change was committed. In
round 1, 18 of the new build's 105 presses landed on an arriving row. They
came in runs of clicks 150 ms apart after one re-sort, so a person who clicks
at that pace meets the same case.

`Table.HitTest` ignores such a press when the table handles it before the
frame that lays out the view change, or within 300 ms after it: it selects
nothing and clears nothing. After the 300 ms it selects the arriving row. A
press in the invisible 300 ms landed on a place that looked empty, which is
not a click on a row the person saw. Selecting that row would give the person
a torrent they did not choose and start its display work. A press on a row
that is fading in selects it, because the person can see that row. In round
1, this rule would have ignored about 7 to 9 of the 105 presses.

The platform does not report whether an arriving row is still invisible. The
table records when the frame that laid out the view change ran (`OnLaidOut`),
and the 300 ms is the platform's observed timing that the `_layoutPending`
remarks record. The rule has three costs:

- The table compares the time it handles the press, not the time the press was
  sent. A press sent just before the change reached the screen, aimed at the
  old row, is ignored: 1 press in round 1. A press sent while the row was
  invisible that waits in the input queue past the 300 ms selects the row.
- The time is that of the last view change. A row that arrived in an earlier
  change and is already fading in is ignored while a later change is within
  its 300 ms.
- If the platform changes its timing, the rule drifts: toward ignoring presses
  on visible rows if the delay gets shorter, toward selecting unseen rows if
  it gets longer.

**Done when:**

- The source review shows that neither found case drops a press any more,
  except the presses on rows that are still invisible as they arrive.
- No press in the new build's runs selects nothing, except presses on no row
  within 300 ms of a view change; the report counts those.
- The new build's report gives the number of presses sent before a view
  change of the torrent table reached the screen and handled after it, by what
  each press landed on.

## Not in this plan

- **No freeze at the click.** A new map subject cancels the old render
  (Step 2). Freezing the old display at the click
  would need its own state in the table, the map and the inspector, because
  more than seven paths restart work for the old torrent.
- **No Low dispatcher priority for publications.** The notifications cost 19
  samples, and a hit test runs any waiting layout before the click.
- **No per-property change notifications on torrents.** `Torrent.Notify`
  raises `PropertyChanged(string.Empty)`. In the reference trace, those
  notifications come from the harness's summary feed (`ApplySummaries`, 198
  samples), not from a click. Many bound
  properties are computed from several fields, so per-property notifications
  need a dependency map, and that work belongs to its own change.
- **Row transitions within one list do not change.** The owner requires that
  rows leaving animate out and rows arriving animate in. Only a switch to a
  different list stops animating (Step 1).
- **A click during an inspector save is still refused**
  (`MainViewModel.Select` checks `Inspector.IsPending`), so item 3 of "What
  the person sees" does not hold then. A save takes one engine round trip, and the benchmark never saves.

## Proof

**Builds.** Two builds, both of the capture Release configuration:

- The reference: HEAD plus only the harness additions below.
- The new build: the reference plus steps 2 and 3. Rounds 1 to 3 also held
  Step 1.

Plan 17 requires matched builds for an in-process probe. Keep the reference
binary in an evidence folder before the second build overwrites the output.

**Harness additions** (capture build only):

- The per-press gesture record of step 3, with what each press landed on.
- When the last frame that showed a view change of the torrent table was
  drawn, recorded with each press. The reference build lacks this record,
  which step 3 added after the reference was built, so its count is reported
  for the new build only. Plan 17 requires matched builds for an in-process
  probe, because a probe's cost would bias one side's endpoints. That reason
  does not hold for this probe: it is one reflection read of a field per frame
  during the clicks and one per press, about 210 reads per run. A read took
  80 to 100 ns in a standalone loop, so even at 10 µs a read the probe adds
  about 2 ms to a click phase whose endpoints are tens of milliseconds per
  click.
- `firstSubjectPictureMs` in the `heavy detail after clicks` outcome. The
  map's draw handler stamps it on the game loop thread, on the first draw whose
  picture shows the subject object that selecting the heavy torrent created
  (`Inspector.Show` creates a new `PeerSubject`), with all of its peers. The
  map presents on its own thread, so a stamp on the UI thread's frame would
  add a delay the person does not see. A
  picture of the heavy torrent from an earlier click has another subject
  object, so it cannot match. It is
  counted from `released`, like `firstConnectionsMs`. It does not wait for the
  peers table, which `Connections` waits for before it polls. The outcome also
  records whether selecting the heavy torrent changed the subject. When the
  last click had already selected it, the endpoint is reported as not
  measured, because its map could already be on screen.

**Runs.** One round of five pairs, alternating reference and new. Each run is
`Capture.ps1 -Review performance-selection -Configuration Release -MapDetail
cities -ArcLimit 1000 -InputOnly -Replay`, on the display other than the main
one. The owner ruled on 9 November that a benchmark never takes the owner's
keyboard, mouse or focus; native clicks need all three. The same day one pair
of each placement compared the two Step 1 builds: on the other display, replay
showed the same map stalls, map pictures, dots and memory as native clicks, but
about half of a click's UI-thread cost, because it skips the pointer path and
the hit test. So the click endpoints below measure the work after a press
reaches the table, and the two press endpoints that need a hit test, lost
presses and presses handled after a view change, are checked by source review.
Round 5 began with native clicks; its pair 1 is replaced by a replayed pair.
Before each run, record whether a build or another benchmark was running. A
difference counts only when all five pairs agree in direction. A mixed result
is reported as mixed and not run again.

**Ticks first.** The owner asked for the benchmark in ticks: the UI thread's
own CPU cycles (`QueryThreadCycleTime`), which other programs on the laptop do
not change. The harness records them with each press (`pressCycles`) and at
its rendered selection (`renderCycles`). They measure the UI work of the
clicks. Milliseconds measure what the person waits for, which also holds
waiting for other work and work on other threads, such as the map's render
worker.

Round 4's predictions, for steps 2 and 3 without Step 1:

| Endpoint | Prediction |
| --- | --- |
| UI-thread cycles per click, from the press to its rendered selection, median and worst | not more |
| UI-thread cycles from the first to the last press | not more |
| Click to rendered selection, median, p95 and worst | not slower |
| UI-thread response in `torrent-clicks`, p95 and worst | not slower |
| Presses that selected nothing | 0, apart from presses on rows still invisible as they arrive |
| Presses lost on a leaving row; selections cleared by a press | 0 |
| Selected but never observed rendered | not more |
| `firstSubjectPictureMs` in the `heavy detail after clicks` outcome | far below the reference, which draws no land before the arcs |
| Presses sent before a view change they were handled after | counted, new build only |
| `firstConnectionsMs` | not later |
| Drawings that trace land when the map opens (`inspector-all-map-open`) | 1 in the new build |
| Marquee sample gap; map draw gaps | not worse |
| Peak and end private commitment; peak working set | not higher |

"Not more", "not slower" and "not later" fail only when all five pairs are
worse. If an endpoint misses its prediction, its cause is found from the code
and the saved records first, as round 3's late arcs were; a correction then
gets one more round.

Round 5's predictions, for Step 1 against round 4's new build
(`V:\artifacts\TinyTorrent2\evidence\keep-up-new4_capture`, commit `d72c4d5`)
as the reference. Neither build adds a harness record, so one build is needed.
Each of the 21 clicks switches the peers table to another torrent, and today
each switch builds and animates every row on screen, so the work the step
removes is in every click:

| Endpoint | Prediction |
| --- | --- |
| UI-thread ticks from the first to the last press | lower in every pair |
| UI-thread ticks per click, median, over the clicks both builds selected | lower in every pair |
| UI-thread ticks per click, worst | not more |
| Click to rendered selection, median and worst | lower; the reference's median varied from 67 to 112 ms between round 4's runs, more than the expected gain, so mixed is possible |
| UI-thread response in `torrent-clicks`, p95 and worst | not slower |
| Presses that selected nothing; presses lost on a leaving row; selections cleared by a press | not more |
| `firstSubjectPictureMs`, `firstConnectionsMs` | not later |
| Marquee sample gap; map draw gaps | not worse |
| Peak and end private commitment; peak working set | not higher |

**Where runs go after round 1.** The owner asked for the benchmark window on
the other monitor. The capture app now opens a visible performance run on a
display other than the main one when one holds the 1920x1080 client area
(`MainWindow.PerformanceDisplay`). The reference binary predates that, so the
round's runner moves its window to the same display after `ready.json` and
before input starts. Both displays run at 100% scale and 60 Hz, but the other
display may be driven by another graphics adapter, so round 2's values compare
within its pairs, not with round 1's. A run starts only after 45 s without
input from the owner, because the benchmark takes the mouse and stops when
another window takes the foreground; a run the owner interrupts is repeated.
Two traced runs stopped that way after round 1.

**Round 1, 9 November.** Builds: reference
`V:\artifacts\TinyTorrent2\evidence\keep-up-reference_capture`, new
`V:\artifacts\TinyTorrent2\evidence\keep-up-new_capture`. No build, benchmark
or other app used CPU before any run. Values are the five pairs in order.

| Endpoint | Reference | New | Verdict |
| --- | --- | --- | --- |
| Presses that selected nothing | 5, 3, 6, 6, 4 | 0, 0, 0, 0, 0 | met; 18 of the 26 recovered presses landed on an arriving row (Step 3's decision: round 1 selected them all) |
| Selections cleared by a press | 1, 1, 1, 1, 2 | 0, 0, 0, 0, 0 | improved |
| `firstSubjectPictureMs` | 1811, 1180, 1297, 1168, 1059 | 228, 148, 266, 257, 239 | met |
| Marquee gap, p95 and worst | p95 156 to 207, worst 222 to 881 | p95 108 to 148, worst 127 to 336 | lower in every pair |
| Docked navigation gap, worst | 266 to 396 | 197 to 256 | lower in every pair |
| Click to rendered selection, median | 41 to 75 | 78 to 115 | **higher in every pair** |
| Click to rendered selection, p95 and worst | | | mixed: missed |
| Selected but never observed rendered | 3, 2, 1, 2, 1 | 2, 2, 1, 0, 0 | mixed: missed (lower in 3 pairs, equal in 2) |
| UI-thread response in `torrent-clicks`, p95 and worst | | | mixed: missed |
| `firstConnectionsMs` | 1822, 1201, 1311, 1174, 1071 | 1159, 1834, 1369, 1243, 1701 | mixed: later in 4 pairs, by up to 633 ms |
| Map animation gap; memory | | | mixed |
| Presses sent before a view change they were handled after | not recorded | 3, 2, 2, 2, 1: 8 on leaving rows, 1 on no row, 1 on a row | counted |
| Presses on no row, given the arriving row | lost: 15 in total | 3, 4, 3, 4, 4 | the rule Step 3 decided after round 1 ignores those within 300 ms |

Among the click endpoints, the median rise is the one counted difference. It
is not a selection effect. Over the 70 clicks that both builds selected,
pooled across the five pairs, the median from click to rendered selection rose
from 62.6 to 93.3 ms, and the UI thread's cycles from press to rendered
selection from 35.2 to 58.2 megacycles. The press reached the UI thread later
(median 31.3 to 49.3 ms), and the selected frame rendered later after the
selection event (14.3 to 31.7 ms). These pooled figures locate where the time
went, but per pair they are not counted differences: the cycles were higher
in 4 pairs and lower in 1, and the arrival time was mixed. During the 3.15 s
of clicks, both builds spent most of the time in UI frames longer than two
refreshes: 2.0 to 2.9 s in the reference, 2.6 to 2.9 s in the new build. The
new build published 47 to 90 map pictures against 25 to 61.

**What the trace shows.** One more pair ran with an elevated WPR trace (CPU
samples, context switches, .NET events): reference run `557407bf`, new run
`4eeb67c4`. It does not show more UI-thread work per click:

- During the clicks, the new build's UI thread used less CPU: 2,409 samples
  against 2,917. Layout fell from 869 to 127 samples, and the list's build
  phases rose from 9 to 127, as Step 1 intends.
- Per click, the UI thread took as long in both builds: from the press to its
  arrival 40 ms against 41 ms on average, and from the selection to its
  rendered frame 64 ms against 65 ms. The new build selected 18 clicks, the
  reference 12. The new build's median was higher (100.7 against 79.2 ms) and
  its 90th percentile lower (115 against 157 ms).
- The UI thread waited for garbage collections longer in the new build: about
  545 ms against 113 ms. The extra allocation came from the 9,000-peer torrent
  that was selected before the clicks. The inspector parsed its peer updates
  (16.9 MB against 8.5 MB), and the map drew its 1,000 arcs again and again
  (8.5 MB against 1.8 MB for the map's inspections).

That torrent stays in the inspector until the inspector follows the
selection. The inspector follows through `FrameCallback`, which runs its work
at Low dispatcher priority after the next frame, so only when no other work is
queued. During the clicks, the UI thread waited for messages only 2% of the
time in the reference and 6% in the new build. In round 1, sampled about every
100 ms, the inspector left the heavy torrent 42, 712, 189, 195 and 98 ms into
the clicks in the reference, and 281, 953, 178, 286 and 1,326 ms in the new
build: later in 4 of 5 pairs, so mixed. Until then, the map keeps drawing the
heavy torrent. Both builds draw it about 42 times a second while it receives
updates (91 to 280 drawings in the 6 s of `peer-speed-sorting`, in both
builds). That repeated drawing, not the phased first picture, is why the new
build published more pictures during the clicks: 3 to 6 of its drawings there
were first pictures.

So the trace shows no added UI-thread work per click, and no correction
followed it. Round 2 points at how Step 1 spreads the work instead. Round 2 still differs from round 1: Step 3's decision now
ignores a press on an arriving row, as the reference loses it, and 18 of round
1's 26 recovered presses were such presses. The Low-priority inspector follow
and the map's repeated drawing exist in the reference and are outside this
plan.

**Round 2, 9 November.** Builds: the same reference, and new
`V:\artifacts\TinyTorrent2\evidence\keep-up-new2_capture`, which adds
`TraceLand`, Step 3's decision and the display choice. Every run ran on the
other display after 45 s without input; none was interrupted, and no build,
benchmark or other app used CPU before any run. Values are the five pairs in
order.

| Endpoint | Reference | New | Verdict |
| --- | --- | --- | --- |
| Click to rendered selection, median | 50.5, 46.4, 64.3, 43.7, 50.4 | 64.4, 54.0, 67.4, 73.2, 66.0 | **higher in every pair**, as in round 1 |
| Click to rendered selection, p95 | 192, 231, 233, 252, 293 | 202, 219, 178, 149, 191 | mixed: lower in 4 pairs |
| Click to rendered selection, worst | 192, 231, 233, 252, 293 | 202, 219, 178, 235, 191 | mixed: lower in 4 pairs |
| Presses that selected nothing | 2, 5, 5, 5, 5 | 2, 2, 1, 1, 1 | met: each landed on an arriving row, 2 to 170 ms after the view change |
| Presses lost on a leaving row | 1, 1, 2, 3, 2 | 0, 0, 0, 0, 0 | improved |
| Selections cleared by a press | 1, 2, 1, 1, 1 | 0, 0, 0, 0, 0 | improved |
| Selected but never observed rendered | 1, 0, 0, 2, 2 | 1, 0, 1, 0, 1 | mixed: missed |
| UI-thread response in `torrent-clicks`, p95 | 45, 90, 124, 110, 108 | 70, 67, 76, 68, 76 | mixed: lower in 4 pairs |
| UI-thread response in `torrent-clicks`, worst | 97, 159, 165, 394, 215 | 133, 77, 114, 110, 187 | mixed: lower in 4 pairs |
| `firstSubjectPictureMs` | 1368, 1587, 1333, 1233, 1724 | 400, 452, 323, 432, 410 | met |
| `firstConnectionsMs` | 1376, 1593, 1344, 1242, 1738 | 1153, 1289, 1731, 1274, 1815 | mixed: later in 3 pairs |
| Drawings that trace land when the map opens | 10, 7, 9, 17, 23 | 1, 1, 1, 1, 1 | met; the whole world took 56 to 108 ms |
| Marquee gap, p95 | 293, 261, 173, 226, 221 | 222, 122, 90, 113, 140 | lower in every pair |
| Marquee gap, worst | 450, 449, 507, 331, 359 | 323, 192, 414, 169, 163 | lower in every pair |
| Docked navigation gap, worst | 604, 548, 546, 640, 596 | 601, 548, 594, 616, 582 | mixed |
| Map animation gap; memory | | | mixed |

The table counts 3 more presses in the new build as not selected (2 in pair 1,
1 in pair 2). Each of them selected its row. Their pointer time was 5.5 to
5.6 ms from their click, outside the harness's 5 ms matching window, so they
are left out of the click endpoints.

**Why the median rose.** Over the 69 clicks that both builds selected, pooled
across the pairs:

- The UI thread used the same cycles from press to rendered selection: median
  31.7 against 30.3 megacycles.
- The selection rendered as fast after its selection event: median 12.8
  against 12.9 ms.
- The press reached the UI thread later: median 28.5 against 41.1 ms.

So a click does not cost the UI thread more. It waits longer before the UI
thread reads it. During the clicks, the new build spent more time in frames
longer than two refreshes in 4 of 5 pairs (2,445 to 2,748 ms against 1,706 to
2,475 ms of 3.15 s), as in round 1. The most likely reading is that Step 1
spreads the row work over more frames. A press then lands more often in a
frame that is running, but waits for less of it, which matches a higher median
with a lower p95 and worst. This reading is not proven: a trace of the frames
that presses wait for would test it. The inspector again left the heavy
torrent later in 4 of 5 pairs (659, 842, 438, 486 and 796 ms against 339, 313,
182, 1,125 and 576 ms). It does not explain the median: in pair 4 it left
earlier, and the median rose most.

**Result of both rounds.** Met in every pair: the map's first picture of the
selected torrent, the land drawn in one drawing, no selection cleared by a
press, the marquee gaps, and presses lost only on rows that are still
invisible as they arrive. Not met: the median click is slower in all 10 pairs,
and the p95, worst and UI-thread response are lower in only 4 of 5 pairs each
round. Step 1 meets its tail prediction in most pairs but costs the typical
click 10 to 30 ms.

**Correction after round 2.** Two facts from rounds 1 and 2, read from the
harness records of all 20 runs:

- The frame a press landed in ran longer in the new build: median 75 against
  45 ms in round 2, 79 against 61 ms in round 1. A press waits for that frame
  to end.
- The UI thread's own CPU during the clicks was higher in all 10 pairs: median
  1.60 against 1.31 G cycles per second in round 2, 1.68 against 1.40 in
  round 1. The UI thread's cycles from press to rendered selection were equal,
  so the added work ran between clicks.

One more pair ran with `dotnet-trace`'s managed sampler and runtime events,
which need no elevation and walk no kernel stacks: reference run `4634a070`,
new run `5e03bdc1`. It showed the same regression: press arrival median 25.4
against 37.0 ms, UI-thread CPU 1.49 against 1.71 G cycles per second. On the
UI thread during the clicks, one sample is about 2 ms:

| UI-thread samples during the clicks | Reference | New |
| --- | ---: | ---: |
| Row cells measured or arranged, or filled by Step 1 | 90 | 283 |
| View publishing (`Table.PrepareView`) | 168 | 73 |
| The same cell work in the last 50 ms before the next click | 17 | 111 |

So the row work grew by about 10 ms per click, and it ran throughout the
150 ms between clicks, where the next press lands. The cause is where Step 1
filled rows: in the list's build phases, which run inside the UI tick for up to
the list's 40 ms budget. For round 3, Step 1 was changed to fill one row per
Low-priority dispatcher item and lay that row out in the same item. The waiting
rule, the first cell, the row height, and steps 2 and 3 did not change.

**Round 3, 9 November.** Builds: the same reference, and new
`V:\artifacts\TinyTorrent2\evidence\keep-up-new3_capture`, which is round 2's
new build with the corrected Step 1. Every run ran on the other display after
45 s without input; none was interrupted, and no build, benchmark or other app
used CPU before any run. Values are the five pairs in order.

| Endpoint | Reference | New | Verdict |
| --- | --- | --- | --- |
| Click to rendered selection, median | 36.4, 39.9, 40.6, 36.5, 38.6 | 44.9, 63.7, 52.7, 85.3, 61.6 | **higher in every pair** |
| Click to rendered selection, p95 | 282, 273, 138, 270, 253 | 161, 119, 180, 224, 204 | mixed: lower in 4 pairs |
| Click to rendered selection, worst | 282, 273, 138, 270, 253 | 161, 131, 180, 224, 258 | mixed: lower in 3 pairs |
| Presses that selected nothing | 5, 5, 5, 5, 3 | 1, 1, 1, 2, 1 | met: each landed on an arriving row, 42 to 185 ms after the view change |
| Presses lost on a leaving row | 2, 1, 2, 2, 1 | 0, 0, 0, 0, 0 | improved |
| Selections cleared by a press | 1, 1, 1, 1, 2 | 0, 0, 0, 0, 0 | improved |
| Selected but never observed rendered | 1, 2, 3, 0, 1 | 1, 0, 1, 0, 0 | mixed: lower in 3 pairs, equal in 2 |
| UI-thread response in `torrent-clicks`, p95 | 77, 182, 131, 94, 45 | 71, 43, 41, 62, 61 | mixed: lower in 4 pairs |
| UI-thread response in `torrent-clicks`, worst | 257, 224, 209, 208, 112 | 120, 147, 180, 135, 134 | mixed: lower in 4 pairs |
| `firstSubjectPictureMs` | 1123, 1057, 1588, 1352, 1310 | 669, 307, 303, 526, 456 | met |
| `firstConnectionsMs` | 1131, 1067, 1593, 1362, 1323 | 2282, 1338, 2318, 2064, 2105 | **later in every pair**, by 271 to 1,151 ms |
| Drawings that trace land when the map opens | 21, 18, 14, 20, 16 | 1, 1, 1, 1, 1 | met |
| Marquee gap, p95 | 99, 152, 135, 151, 313 | 204, 93, 115, 134, 94 | mixed: lower in 4 pairs |
| Marquee gap, worst | 187, 316, 272, 327, 588 | 272, 253, 219, 155, 157 | mixed: lower in 4 pairs |
| Docked navigation gap, worst | 626, 609, 617, 600, 606 | 583, 619, 607, 607, 568 | mixed |
| Map animation gap; memory | | | mixed |

What the correction changed, from the harness records of round 3:

- The UI thread's own CPU during the clicks: median 1.49 against 1.38 G
  cycles per second, a smaller excess than round 2's 1.60 against 1.31.
- The frame a press landed in: median 56 against 48 ms, against round 2's 75
  against 45 ms.
- The press still reached the UI thread later: median 32.8 against 23.9 ms.
  Over the 72 clicks that both builds selected, the median from click to
  rendered selection was 61.6 against 38.4 ms.

So filling at Low priority removed part of Step 1's cost to the click, not all
of it. It also made `firstConnectionsMs` later in every pair, where round 2 was
mixed. The inspector did not leave the heavy torrent later (earlier in 3
pairs), so the delay comes after the clicks, while the heavy torrent's peers
table fills and its map draws the arcs. The saved records point at a cause:
from the release to the arcs, the window drew 25 to 31 frames per second
against 14 to 18, one filled row per frame, and the selected map's one glow
drawing took 828 to 1,077 ms against 356 to 491 ms in 4 of 5 pairs. Round 2's
frame rate and glow time matched the reference. Round 4 shows the glow time
varying as much without Step 1, so this cause is not proven.

**Result of three rounds.** Met in every pair of rounds 2 and 3: the map's
first picture of the selected torrent, the land in one drawing, no selection
cleared by a press, no press lost on a leaving row, and presses lost only on
rows that are still invisible as they arrive. Not met: the median click is
slower in all 15 pairs, with either way of filling rows; p95, worst and UI-thread
response are lower in 3 or 4 of 5 pairs; and with Low-priority filling,
`firstConnectionsMs` is later in every pair. Step 1 is therefore removed (see
Step 1).

**Round 4, 9 November.** Builds: the same reference, and new
`V:\artifacts\TinyTorrent2\evidence\keep-up-new4_capture`: steps 2 and 3
without Step 1, commit `d72c4d5`. Every run ran on the other display after 45 s
without input. One reference run was interrupted by input and repeated. Before
pair 4's reference run, another `dotnet` process used CPU. Values are the five
pairs in order; the reference values vary more than in round 3 (median click 41
to 100 ms against 36 to 41).

The new build selects presses that the reference loses, and those presses land
while the rows change, the costliest moments. So the click endpoints are also
given over the clicks that both builds selected (6 to 13 per pair, 48 in all).

| Endpoint | Reference | New | Verdict |
| --- | --- | --- | --- |
| Ticks per click, press to rendered selection, median, all clicks (M) | 28.5, 31.1, 39.8, 43.7, 34.2 | 27.1, 40.9, 37.7, 69.2, 42.8 | mixed: more in 3 pairs |
| Ticks per click, worst, all clicks (M) | 160, 91, 279, 377, 292 | 340, 519, 649, 392, 517 | **more in every pair**; the worst click of pairs 1 and 2 is one the reference lost |
| Ticks per click, median, common clicks (M) | 35.9, 31.1, 39.3, 43.7, 36.1 | 27.1, 40.9, 37.2, 31.4, 41.8 | mixed: more in 2 pairs |
| Ticks per click, worst, common clicks (M) | 160, 38, 279, 131, 292 | 90, 147, 633, 392, 517 | mixed: more in 4 pairs |
| UI-thread ticks from the first to the last press (G) | 4.34, 4.03, 5.45, 4.91, 4.75 | 4.89, 4.82, 4.64, 4.35, 5.85 | mixed: more in 3 pairs |
| Click to rendered selection, median, all clicks | 58.0, 40.6, 78.3, 48.7, 99.7 | 77.0, 67.2, 70.1, 93.7, 111.9 | mixed: higher in 4 pairs |
| Click to rendered selection, median, common clicks | 62.5, 40.6, 73.6, 44.0, 101.7 | 77.0, 65.2, 70.1, 93.7, 111.9 | mixed: higher in 4 pairs |
| Click to rendered selection, worst, all clicks | 169, 156, 505, 308, 222 | 384, 285, 351, 239, 413 | mixed: higher in 3 pairs |
| UI-thread response in `torrent-clicks`, p95 | 52, 93, 201, 198, 152 | 129, 171, 220, 134, 261 | mixed: higher in 4 pairs |
| UI-thread response in `torrent-clicks`, worst | 225, 259, 320, 314, 292 | 249, 246, 377, 169, 302 | mixed: higher in 3 pairs |
| Presses that selected nothing | 5, 6, 6, 6, 4 | 0, 0, 0, 2, 0 | met: one landed on an arriving row 121 ms after the view change, and one matched no click |
| Presses lost on a leaving row | 2, 2, 4, 3, 1 | 0, 0, 0, 0, 0 | met |
| Selections cleared by a press | 1, 1, 1, 1, 2 | 0, 0, 0, 0, 0 | met |
| Selected but never observed rendered | 5, 7, 8, 11, 9 | 5, 4, 7, 7, 9 | met: not more |
| `firstSubjectPictureMs` | 1495, 1267, 3646, 1697, 1258 | 126, 1017, 126, 778, 847 | met: lower in every pair |
| `firstConnectionsMs` | 1498, 1283, 3675, 1893, 1266 | 1588, 3384, 1609, 1838, 2652 | met: mixed, later in 3 pairs |
| The selected map's glow drawing, ms | 468, 482, 1430, 556, 430 | 432, 1189, 564, 559, 1061 | mixed |
| Drawings that trace land when the map opens | 21, 16, 18, 27, 24 | 1, 1, 1, 1, 1 | met |
| Marquee gap, p95 | 153, 176, 352, 156, 247 | 149, 154, 278, 196, 78 | met: lower in 4 pairs |
| Marquee gap, worst | 216, 705, 619, 470, 321 | 274, 325, 418, 221, 335 | met: mixed |
| Docked navigation gap, worst | 307, 370, 516, 560, 586 | 270, 305, 520, 542, 611 | met: mixed |
| Stationary dot gap, worst | 87, 388, 127, 158, 72 | 117, 118, 111, 101, 122 | met: mixed |
| Peak private commitment, MiB | 1291, 1164, 1177, 1119, 1288 | 1079, 1288, 1076, 1201, 1197 | met: mixed |

**Result.** Steps 2 and 3 improve what they set out to improve, in every pair:
the selected torrent's land shows first, the land traces in one drawing, no
press on a leaving row is lost, no press clears the selection, and only
presses on rows still invisible as they arrive select nothing. Every other
endpoint is mixed, so none is worse by the rule above, except the worst ticks
per click over all clicks. That one counts presses the reference never handled;
over the clicks both builds selected it is mixed. The click itself is not
faster, because the step that aimed at it is removed.

**Round 5, 9 November.** Builds: round 4's new build as the reference, and new
`V:\artifacts\TinyTorrent2\evidence\keep-up-new5_capture` with Step 1. Every
run replayed its input on the other display (see Runs), with no build or other
benchmark running before it. Values are the five pairs in order.

| Endpoint | Reference | New | Verdict |
| --- | --- | --- | --- |
| UI-thread ticks from the first to the last press (G) | 4.29, 4.49, 4.84, 4.48, 4.63 | 4.27, 4.84, 4.42, 4.26, 4.57 | missed: lower in 4 pairs |
| Ticks per click, median, common clicks (M) | 29.1, 17.4, 34.2, 31.8, 29.7 | 17.2, 88.4, 19.7, 34.3, 34.6 | missed: lower in 2 pairs |
| Ticks per click, worst, all clicks (M) | 609, 499, 640, 555, 546 | 413, 734, 334, 499, 562 | met: more in 2 pairs |
| Click to rendered selection, median, common clicks (ms) | 58, 19, 64, 42, 56 | 38, 58, 30, 59, 81 | mixed: higher in 3 pairs |
| Click to rendered selection, worst, common clicks (ms) | 229, 59, 369, 285, 322 | 181, 430, 194, 255, 356 | mixed: higher in 2 pairs |
| UI-thread response in `torrent-clicks`, p95 / worst (ms) | 168/290, 42/237, 233/315, 140/342, 185/237 | 109/253, 276/409, 91/223, 149/238, 272/309 | met: slower in 3 and 2 pairs |
| Selected but never observed rendered | 2, 1, 4, 2, 3 | 3, 6, 1, 1, 2 | met: more in 2 pairs |
| `firstSubjectPictureMs` | 108, 135, 375, 658, 312 | 215, 108, 636, 265, 309 | met: later in 2 pairs |
| `firstConnectionsMs` | 1190, 1667, 1517, 2241, 2098 | 1765, 1553, 2594, 2101, 1280 | met: later in 2 pairs |
| Marquee gap, worst (ms) | 303, 444, 595, 277, 277 | 344, 255, 433, 229, 875 | met: worse in 2 pairs |
| Stationary map draw gap, worst (ms) | 135, 142, 117, 87, 278 | 166, 410, 95, 228, 92 | met: worse in 3 pairs |
| Peak private commitment (MiB) | 1133, 1237, 1275, 1201, 1147 | 1143, 1139, 1186, 1373, 1240 | met: higher in 3 pairs |
| Peer table switches seen during the clicks | 10, 17, 9, 13, 13 | 14, 5, 14, 10, 8 | — |

The presses lost on a leaving row and the presses handled after a view change
need a hit test, which replay does not make; both were 0 in every run.

**Cause of the miss.** The predictions assumed that each of the 21 clicks
switches the peers table. The records show otherwise: of the 20 presses that
select another torrent, the table was seen switching 5 to 17 times, because the
inspector merges selections while the UI thread is busy. Step 1 saves its work
per switch, and the number of switches differs between runs, so the per-click
ticks mix clicks that switched with clicks that did not. Pair 2's new run, with
the fewest switches, has the highest median. The click benchmark therefore
cannot show Step 1's effect. The peer table switch capture (`Capture.ps1
-Review peer-switch`) switches the table between two 200-peer torrents twelve
times per measure and counts UI-thread cycles, so it measures Step 1's work
alone; five pairs of it decide Step 1.

**Peer table switch, 9 November.** Five pairs of `-Review peer-switch` with the
same two builds, the reference first in each pair, without input. Each value is
the median of 12 steps, in megacycles on the UI thread; the five pairs in order.

| Step | Reference | New | Verdict |
| --- | --- | --- | --- |
| Switch, table and map | 363, 363, 319, 322, 349 | 140, 115, 145, 140, 105 | met: lower in every pair |
| Busiest frame of a switch | 119, 130, 122, 125, 126 | 55, 49, 61, 68, 43 | met: lower in every pair |
| Switch, table alone | 591, 348, 670, 720, 336 | 141, 121, 114, 178, 113 | met: lower in every pair |
| Switch, sorted table | 472, 330, 389, 688, 370 | 140, 108, 114, 121, 134 | met: lower in every pair |
| Poll: five peers leave, five join | 154, 113, 132, 173, 149 | 169, 148, 169, 174, 122 | higher in 4 pairs |
| Idle, no change | 55, 69, 44, 40, 46 | 60, 40, 53, 58, 50 | higher in 4 pairs |

Step 1 is met: a switch costs about 40% of its earlier cycles (median 140
against 349), and its busiest frame less than half. The poll is higher in four
pairs. So is idle, which does no work, and another session's compiler and tests
ran just before the new runs of pairs 3 and 4, so this round cannot tell a
poll cost from the load on the second run of each pair. Batch 2 of the
[rendering plan](17-rendering-and-selection.md) replaces the table's poll path,
and its round measures the poll again.

**The rendering plan's batch round, 10 November.**
[That round](17-rendering-and-selection.md#round-of-batches-1-to-5) used this
round's new build as its reference. It cannot measure the poll again: its
reference's map did not follow the peers the capture gave the inspector, while
the new build's map follows them and animates their traffic. The median click
is slower in every pair, while the press itself is handled faster in every
pair. In the one pair that recorded the tables' changes, 4 of 21 presses
waited behind a peers table switch that was already writing its rows, for
example 8,700 removals in 48 ms. The work in the other waits is not
attributed.

**The rendering plan's batch 6 round, 10 November.**
[That round](17-rendering-and-selection.md#round-of-batch-6) measured `new8`
against `new7`. The median press waits 41 ms instead of 68 ms before the UI
thread handles it. The UI thread is stalled for 1.3 to 1.6 s of each 3 s of
clicks instead of 1.6 to 1.8 s, and lower in every pair. The table's row
notices are gone from the stalls. What remains is WinUI's own layout and the
table's cell layout after each switch.
