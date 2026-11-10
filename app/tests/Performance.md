# UI performance benchmark

Build with `pwsh -NoProfile -File .\Build.ps1 -App -Capture -Release`, then run:

```powershell
pwsh -NoProfile -File .\app\tests\Capture.ps1 -Review performance-map -Configuration Release -BinaryDirectory '<printed build output>' -MapDetail countries -Replay -InputOnly
pwsh -NoProfile -File .\app\tests\Capture.ps1 -Review performance-map -Configuration Release -BinaryDirectory '<printed build output>' -MapDetail cities -Replay -InputOnly
pwsh -NoProfile -File .\app\tests\Capture.ps1 -Review performance-map -Configuration Release -BinaryDirectory '<printed build output>' -MapDetail cities -ArcLimit 1000 -Replay -InputOnly
```

Repeat the three profiles with `-Review performance-selection` for docked maps,
selection and marquee. `-Replay -InputOnly` runs the existing scenarios in a
1920×1080 window on a display other than the main one, without activating it,
taking images or sending desktop input, so the owner keeps the main display,
keyboard, mouse and focus. With no other display the window is offscreen. Offscreen,
the 9 November comparison of one pair drew no map dots during sorting and clicks
(63–91% of draws on a display) and opened the selected torrent's map in 3.7–3.9 s
(0.5–1.9 s on a display), so compare only runs placed the same way; a binary
built before replay chose the display opens offscreen. The launcher
rejects pre-replay binaries before starting their engine or window. The preserved pre-reboot binary remains historical evidence; build the
unchanged production version with this harness for a new matched baseline.

The external source process emits the same 120 Hz motion schedule and 150 ms row
selection schedule through a fixture-owned pipe. The window queues these commands
on its UI context without pausing the sender while a command runs. Reports retain
emission, application and XAML completion separately; late commands are applied in
order rather than skipped. Pan and zoom call the existing map viewport/zoom owners;
selection and marquee call the table's existing commitment and coverage owners.
They bypass native delivery, hit-testing, pointer capture and focus. This measures
command-to-render behavior, not native click latency or foreground performance:
in the 9 November comparison a replayed click cost the UI thread a median of
20–29 megacycles against 54–64 for a native click, and no replayed press can
land on a row that moves away.
The external one-second peer source, summaries, sorting and churn stay unchanged.
No replay result establishes displayed FPS or physical next-frame acceptance.

For notification attribution only, set `TINYTORRENT_PERFORMANCE_CHANGES=1` in
the launching process before one `-Replay -InputOnly` run, then remove it.
`timing.json` records `traceChanges` and aggregate `changes`: table, requested
subject IDs, source-object identity, before/after counts, notification counts
and first/last completed-notification QPC. These batches flush through the UI
queue; they are not exact `ApplyProjection` boundaries or native CPU timings.
They omit quiet replacements and the first notification's native work. The
requested subject can differ from the retained rows being replaced. Counters
add per-notification observation overhead, so use them for attribution, not an
uninstrumented performance comparison. They are disabled by default.

`TINYTORRENT_PERFORMANCE_SCOPES=1` instead selects a focused, offscreen
`performance-selection -Replay -InputOnly` variant and enables these counters.
After the unchanged all-peer setup, it selects 14, then 15, then 14 torrents,
waiting for each scope to finish before the next. It records common Peer
identity retention, missing/outside rows and notification counts. It uses the
same 50-torrent inventory and city/arc settings, without starting the timed
click/navigation/churn schedule or live rate source. The report labels this
`peer-scope`; it is not a standard-profile response or animation comparison.
Remove the environment variable after the run.

The native desktop-input procedure described below is retained for its existing
evidence and safety guards. **Do not run it on the owner's desktop.** `-InputOnly`
alone still sends global input. PresentMon 2.3.1 records ETW presentation events
passively; it does not supply benchmark gestures. Recorder-only work is separate
from `Capture.ps1`, uses existing ETW rights only, and permits no further UAC,
elevation or permission changes. Do not capture screenshots or video.

The visible 1920×1080 journey uses 50 disposable torrents: 40 have 50–1,000
leechers, seven have 1,000–3,000, and three have 3,000; each has twice as many
seeds. The fixture contains approximately 160,000 known peer rows (the exact
count is saved in `ready.json`), with 5,000 connection slots globally. One heavy
torrent has 3,000 connections; the remaining 2,000 are distributed across the
other torrents. Disconnected known peers have zero rates. No payload or
peer traffic supplies the data. The standard capture launcher owns isolation
and cleanup; it replaces development instances before using its disposable store.
All three runs use the same 1,000 distinct populated-place coordinates from
the bundled country geometry. Countries are chosen first, then locations are
taken round-robin from their populated places, spreading the workload across
countries and within them. `locations.json` preserves every coordinate and
`ready.json` records the count per country. The fixture supplies city data without downloading a geolocation
database. Country and city runs first use the default 200-arc limit; a third
city run raises it to 1,000. Each run records its requested detail and limit,
and the actual retained arcs in the drawn scene.
The report also counts retained routes whose bounds intersect the viewport;
this is a conservative visibility estimate, distinct from the cap and from
individual overlapping strokes.
The overview reports source locations, prepared destinations before the cap,
and retained arcs separately. Stationary white location markers include known,
disconnected peers and are independent of the arc cap. The report counts all
prepared location markers and those drawn in the viewport separately from moving
transfer dots. White location dots sit beneath arcs; moving transfer dots sit
above them. Arcs and their moving dots convey transfer activity.
Roughly 500–700 arcs from 1,000 locations is the
owner's expected collapse behavior at this resolution, not a target the fixture
enforces. Current city grouping merges exact coordinates only; nearby-location
merging remains a product finding, separate from frame pacing.

The journey opens the global map with Ctrl+Shift+M, pans at 1× for one second
each way, zooms at the download origin to 128× and back for two seconds each,
then centers at that origin at 16×. It pans 30° east, returns to the origin,
and pans 30° west, taking two seconds per leg. It measures stationary 1×
animation for three seconds in that global-map visit.
It clicks seven physical visible row slots three times at
150 ms intervals with a half-height Peers inspector split equally between map
and table, then draws a two-second constant-speed marquee. Before clicking, it
clears the torrent selection, measures the docked map's appearance with all
known peers, and samples its animation for three seconds. It then selects one
torrent and waits for its connections to be drawn before starting timed clicks.
The ready condition requires the current peer rows and a drawn picture with
connections for that same inventory. Worker quiescence and moving dots are not
readiness requirements; a one-second selected-map baseline lets native row
transitions settle before clicks. In the fresh docked journey, the live source
starts after that baseline; the initial all-peers animation uses fixed rates.
This separates initial readiness from the measured live sorting and churn.
The full-screen source starts after its first ready map. Startup and steady
animation are separate measurements.
Screenshots preserve both starting states and the map before the marquee.
Escape or losing foreground focus stops input.
The zoom phases record whether the requested endpoint was actually reached;
a missed endpoint remains a failure in the results. Zoom-out starts from a
separate, untimed 128× setup so a failed zoom-in cannot shorten that test.
Wheel deltas processed by the map are recorded alongside the external sender's
timestamps, separating input delivery from what the application handles.
Every motion phase also records visible dot sprites and the progress of the
same dot across consecutive draws. Dots can advance, remain visible without
advancing, or be absent; missing dots at high zoom can be outside the viewport.
Animation during navigation is observed, not required to pass. Draw gaps and
navigation pacing remain separate from dot visibility and progress.

Timed mouse input runs in a separate process at 120 Hz, using the same native
input source as the capture build. Application GC pauses cannot pause that
sender. Plans and actual delivery timestamps are saved with each journey;
selection and rendering timestamps stay in the app.
The external process also produces synthetic peer snapshots once per second.
They enter the app through the ordinary parsing, reconciliation and filtering
path. Generation time and source-to-UI receipt delay are reported separately.
Both tables sort by descending download speed. Torrent summary rates change
every three seconds, replacing approximately three visible rows per update;
peer rates change every second, replacing approximately half the visible rows
in the dedicated heavy-torrent sorting case.
A seeded permutation changes the ranks of retained and incoming rows throughout
the viewport. Retention preserves identities, not positions. Visible membership
samples record departures and rank changes instead of assuming the sort worked.
Clicks visit seven physical row slots while sorting continues. The benchmark
records which torrent each press actually hit, matching its input timestamp
to the external sender's calibrated uptime. Already-selected clicks are counted
separately from selection switches.
Each source tick produces the same rates and permutation across runs. Timed
phases follow asynchronous readiness and screenshots, so their exact tick and
row identities can differ; use the saved source/timing records when comparing.

After those cases, a separate churn phase retains 4,000 permanent connections
and rotates 1,000 slots at 250 disconnect/connect swaps per second. New peers
pass through Connecting and Handshake before Connected. Established peers
scheduled for closure remain connected until their slot rotates. Consequently
the steady churn state has 4,500 established, 250 connecting and 250 handshaking
peers, with 250 established peers scheduled to disconnect. These are synthetic
UI states, not real sockets or a claim about a Windows connection limit.
The selected heavy torrent stays open while this load continues through docked
zoom and pan cases. An external WM_NULL round-trip probe checks window-thread
availability about every 16 ms with a one-second timeout. Its p95/worst delay
and timeouts expose UI-thread stalls; they do not establish pixel latency.
`Capture.ps1` owns the workload and saves its counts in `fixture.json` for the
app to consume. The complete workload remains loaded without scrolling through
all 50 torrents.

Supply Intel PresentMon 2.3.1 through `-PresentMonPath`, or put it at
`artifacts/tools/PresentMon-2.3.1-x64.exe`. The runner writes `performance.md`,
`performance.json`, the raw display trace, stimulus timestamps, map picture
updates and renderer operation elapsed times into the capture evidence directory. ETW
permissions may require elevation; add `-ElevatedRecorder` to elevate only
PresentMon through the Windows UAC prompt. The app launches at normal privilege
after that prompt is accepted, so human confirmation does not consume its setup
timeout. The capture launcher holds the exclusive capture lock and permits only
this checkout's diagnostic window; the recorder filters its executable name.
Recording ends when the target exits or its 120-second capture window completes.
Its CSV may appear only after the map's first
present. `-InputOnly` validates the same scenarios without a recorder or UAC;
it produces diagnostic timings and no displayed-FPS verdict. A failed recorder
run remains failed and waits for its safety timer to finish cleanup, without
requesting another elevation.
Use `-Review performance-selection -InputOnly -Configuration Release` to repeat
only the docked-map, selection and marquee cases without elevation or replaying
the unchanged full-screen journeys. Render-only reports identify that limitation.
Use `-Review performance-map` for the full-screen cases alone; add `-InputOnly`
for draw timings without a display recorder. Both modes use the full 50-torrent fixture.
The full journey stops if returning to the docked map leaves it blank. The focused
mode measures a fresh docked view; retain the transition failure as a separate finding.
Scenario-ready time includes current peer rows; first-render time separately
records when the connections become visible.
Docked-map startup records `map-draw-ui-poll`: the UI polls the last drawn
picture's subject, complete peer inventory and retained routes while checking
that the map is loaded, visible and has revealed its canvas. The independent
map can draw
without another XAML render callback. Earlier binaries sampled this endpoint
only at XAML completion; those startup times require a matched reference before
comparison with the corrected observer. Global startup retains its separately
labelled `xaml-completion` observation. Both are diagnostic upper bounds after
submitted drawing, not displayed-pixel timing. Click and marquee endpoints are
unchanged.
The report includes UI-process private bytes and working set, sampled externally
throughout each case and click pass. It records baseline, peak, end and growth
in MiB and saves raw `memory.json` samples. Private memory includes native and
managed allocations and the synthetic fixture; GPU VRAM and other processes
are outside this measurement. A rising value calls for investigation, not a
leak verdict from a single run.
Regenerate a report without replaying input:

```powershell
pwsh -NoProfile -File .\app\tests\Performance.ps1 -Directory '<captures directory>' -Analyze
```

Add `-InputOnly` when analyzing a run without a display trace. A failed run
can be analyzed for its completed phases; the report retains its failure.

Read displayed FPS per surface, 1% lows, p95/worst frame holds and estimated
missed refreshes together. Changed views from the map draw event are counted
separately so moving dots cannot conceal stalled navigation. Selection-event latency, XAML render
completion and OS input-to-display observations have distinct endpoints; the
last does not identify selection-highlight pixels. Marquee geometry is sampled
at render completion. The JSON preserves raw samples and measurement limits.
