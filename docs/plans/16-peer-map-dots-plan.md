# Moving dots on the peer map

**In progress.** The design below is implemented in the working tree and
described in [peer location](../contracts/peer-location.md#moving-dots); the
checks after the build remain. This plan holds the design that two
independent expert panels and two plan reviews settled, the work that keeps
the map cheap to draw, and the other designs that are just as good, so the
owner can switch later without starting again. When the checks pass, this plan
is deleted.

## What the user sees

Dots travel along the arcs between the user's location and the peers. Download
dots move toward the user in the download colour. Upload dots move away in the
upload colour. The Speed statistic over the map says how much data one dot
stands for, such as "•• 48.0 KiB per dot".

The number of dots follows three common-sense expectations from the owner:

- **Slow traffic shows very few dots, but not none.** A trickle sends a dot
  every second or two; a route that is slower still sends one when its bytes
  add up to a dot.
- **Faster total traffic shows more dots, up to a maximum.** More dots than
  the maximum are noise. The maximum limits noise, not cost: one sprite batch
  of 40 or 200 dots costs about the same, and the cost comes from how long
  frames run.
- **Routes share the dots by bandwidth.** Dots on routes of ordinary length
  move at the same speed on screen, so the dots on a route sit closer together
  the faster the route is: dots per DIP along such a route are proportional to
  its rate. Short and long routes move slower or faster, because each trip
  lasts 0.5–3 seconds: a 20-DIP route at 40 DIPs per second, a 900-DIP route
  on a full-screen map at 300 (point 2).

The arcs keep showing absolute speed through their brightness and width, on
the existing fixed logarithmic scale. The Speed statistic, the tooltip and the
Peers table give the exact numbers. The dots add what the other marks cannot
show: that data moves, in which direction, and how much each route carries
compared with the others.

## Design

1. **Meaning.** One dot stands for the same number of bytes on every route.
   Dots leave evenly, at each route's measured rate. A route shows no dot
   before that many bytes are complete; bytes short of a dot carry over to the
   next one. A dot whose trip would already be over is not shown late.
   Departures have no random gaps, because the rates are averages and random
   gaps would invent timing. Dots launched before a size change keep standing
   for the old size until they land, at most 3 seconds.
2. **Motion.** Each route has one trip time: its length at the fitted overview
   scale ÷ 160 DIPs per second, clamped to 0.5–3 seconds. When the route's arc
   is made, the route stores its length in map degrees; its overview length is
   that × the viewport's fitted scale, computed when needed. Arcs are not
   rebuilt on resize, so storing DIPs would leave the trip time, the
   short-route test and the spacing limit at the old size. A dot's progress is
   its age ÷ the trip time, with no easing. So the 160 DIPs per second hold for routes between about 80 and
   480 overview DIPs, and zoom changes only how fast dots cross the screen,
   never how many are in flight. At high zoom a dot crosses the view in a few
   frames; that is acceptable, because the zoomed view is for inspecting
   places, not traffic.
3. **Short routes.** A route shorter than 20 DIPs at the fitted overview, such
   as a peer in the user's own city or a region of the user's own country,
   carries no dots and is left out of every count below. On such a route a dot
   would never leave the fades at its two ends (point 6), and the spacing
   limit would have no solution. Its arc and marker still show that it is
   active.
4. **Number of dots.** People do not count more than about four dots; beyond
   that they judge how crowded the view looks, so the maximum follows the
   map's size and the spacing along each route:

   - **Density:** `MaxDots = max(1, ⌊canvas area ÷ 10,000 DIP²⌋)`, one dot per
     100 × 100 DIP square: 40 on an 800 × 500 canvas and about 190 on a
     full-screen map. Zoom does not change it; a resize does.
   - **Spacing:** the dots of one direction on one route stay at least 15 DIPs
     apart, about five head widths, so they read as separate dots and not as a
     dashed line. Download and upload dots on one route travel opposite ways,
     so each direction counts on its own. A direction's dots in flight are at
     most its route's overview length ÷ 15 DIPs.

   The total rate of all routes that carry dots decides how many dots should
   be in flight: `wanted = MaxDots × min(1, (total ÷ ceiling)^(1/3))`, and at
   least 1 while anything transfers. The cube root spreads the count evenly:
   from 2% to 100% of the ceiling it changes about four times. The arcs'
   logarithmic scale is not used, because with a 5 MiB/s ceiling it reaches
   half at about 70 KiB/s, so almost every real download would show nearly the
   same number of dots.

   The 10,000 DIP² and 15 DIP values are judgement and are checked on the
   running map.
5. **Dot size.** The smallest value from 1 KiB in the series 1, 1.5, 2, 3, 4, 6,
   8, 12, 16, 24 … (each step ×1.5 or ×1.33, continuing into MiB and GiB) that
   keeps the mean dots in flight, Σ rate × trip ÷ size, at or below
   0.8 × `wanted`, and every direction of every route within its spacing
   limit. Both limits shrink as the size grows and short routes are left out,
   so the search always ends. The 0.8 leaves room for the ±1 dot that even
   departures add to each route, so the hard limit rarely cuts dots. The small
   steps keep the dots in flight within a third of the target; powers of two
   would let a 4% rise in speed halve the dots. The size rises at once and
   falls only after 10 seconds of lower traffic, so the legend does not
   flicker. A resize changes the size at once in both directions, because the
   user caused it.

   With an 800 × 500 canvas (`MaxDots` = 40), trips of about 1.5 seconds, a
   5 MiB/s ceiling and traffic spread over enough routes that no spacing
   limit applies, the targets are:

   | Total traffic | `wanted` | Dots in flight (about) |
   | --- | ---: | ---: |
   | 1 KiB/s | 2.3 | 1.5 |
   | 10 KiB/s | 5 | 3–4 |
   | 100 KiB/s | 11 | 6–9 |
   | 1 MiB/s | 23 | 12–18 |
   | 5 MiB/s or more | 40 | 21–32 |

   No more than `MaxDots` dots are in flight on the whole map. In the rare
   frame where the total would go over, the new dot is dropped, not replayed
   later. There is no
   minimum per route: it would show dots for bytes that never moved and make
   the legend false. A slow route in a busy swarm gets dots rarely, and its
   arc and marker show that it is active.
6. **Mark and fades.** A glowing dot: a 4.5-DIP round head with a soft glow
   out to 7 DIPs across, and a tail that tapers and fades to nothing 8 DIPs
   behind it. The owner chose this bolder mark over a 3-DIP head with a 5-DIP
   tail, which was hard to see beside the city markers. The tail stays at 8
   DIPs because the dots of one direction on a busy route sit 15 DIPs apart,
   and a longer tail would join them into a line. Still, it reads as a dot,
   which matches the legend. In motion, the tail fills the gap between
   frames. It is one white sprite, rotated along the motion and
   tinted with the exact download or upload theme brush, with normal blending.
   The angle comes from a second point a little further along the great
   circle; the longitude difference between the two points is wrapped to
   ±180° (`MapViewport.Wrap`), so the angle does not flip at the map's edge.
   At its own two endpoints a dot is transparent within 3.5 DIPs of the
   marker's centre and at full strength from 10 DIPs, so it looks as if it
   passes under its markers; it crosses other places' markers at full
   strength. Its strength is also multiplied by its arc's landing fade near the
   destination and by the origin fade near the user's location, so converging
   download dots cannot form a bright knot. Each of these fades has one
   function, used by both the arc brushes and the dots. Dots do not take the
   arc layer's 80% cap.
7. **Legend.** "•• 48.0 KiB per dot" ends the label row of the Speed
   statistic, in the shared byte format. It shows whenever animations are on
   and High Contrast is off, while the map shows, also when no data moves and
   before the map has finished loading, so the statistic never changes size
   on its own. That rule belongs to the map control, not to the renderer's
   lifetime. Before the first size is known, it shows the smallest size; when
   traffic stops, it keeps the last size. Its tooltip says what one dot means
   and which colour and direction is which.
8. **Settings.** With Windows animations off or High Contrast on, there are no
   dots and no legend line. The arcs, the Speed numbers and the table still
   show speed.

### The ceiling

People's connections differ by a factor of a thousand, so the count needs the
user's own connection capacity: a fixed ceiling would keep a gigabit line at the maximum
all the time and never let a slow line come near it. The total counts both
directions, so the ceiling does too:

1. When Connection setup has stored both a download and an upload capacity
   above zero (`Editor.CapacityDownload` and `Editor.CapacityUpload`, decimal
   Mbit/s, so × 125,000 for bytes per second), the ceiling is their sum. One
   side alone counts as unknown, as Connection setup itself treats it.
2. Otherwise it is the highest total the map has seen in this engine session,
   never below 1 MiB/s, so a trickle on a fresh session is not shown as a full
   line.
3. When the live total goes above the stored capacity, because that value is
   stale, the peak replaces it.

`MapModel` holds the session peak, because it outlives the map's unload and
reload, which a minimise or a section change causes. `MapDots` reports the
total of the routes that carry dots, because only it knows which routes are
short; `MapModel` keeps the highest one. The peak resets when the engine
session changes, as the peer rows do. `Inspector` gives `MapModel` the
capacity through the existing `UpdateMap` path; `MapRenderer` never reads
settings. The ceiling only grows within a session, as the connection test's
speed meter does. A new peak can still raise it while the user watches; the
cube root and the size steps keep that change small.

Decided: an active speed limit does not lower the ceiling, because limits
switch with the schedule, can be one-sided or per torrent, and would make the
ceiling fall while the user watches; the connection capacity is stable and needs no
rules. Decided: the arcs stay on their fixed 5 MiB/s scale, because no single
route fills a fast line, so a scale on the connection capacity would dim every arc on fast
connections and fade them all whenever the ceiling grows.

### Where the code lives

The dot rules have their own state and inputs: the dots in flight, the size and
its 10-second fall, the carried bytes per route, the ceiling (an input), the
canvas area and the clock. They go in their own type, `MapDots`, in its own
file. `MapMotion` owns one on its game loop thread, gives it plain numbers
(rates, trip times, lengths, area, ceiling, speed and total choices, time) and
draws the positions and tints it returns. `MapRenderer` builds the routes and
rates when the peers change and hands them to `MapMotion`; `MapMotion` reports
the dot size and the total back to the UI thread for the legend and
`MapModel`'s peak.
`MapDots` uses no Win2D, so its arithmetic (the series, the limits, the short
routes) can be read on its own, and a plain test can cover it if one earns its
place under [testing](../agents/testing.md).

## Keeping it fast

While dots move, they draw at the display's frame rate. That is needed for smooth
motion, so the work per frame must be small, the map behind the dots must be
redrawn rarely, and the dots must stop drawing as soon as nothing moves.

### Done

- **Map and dots in one layer on its own thread.** **Owner requirement:**
  the dots keep moving while the UI thread is busy, such as while the Peers
  table fills; the window's acrylic backdrop stays; pan and zoom stay as
  fast as before. The map and every table share the UI thread, so dots drawn
  there froze during any long table work. A transparent Win2D
  `CanvasAnimatedControl` (`MapMotion`) draws the latest picture of the map
  and the dots over it on its game loop thread, at the display's rate while
  anything moves. A first version drew only the dots there, over the map's
  canvas, but a WinUI 3 swap chain shows only the window's backdrop through
  its transparent pixels, so the map under it disappeared (Win2D issue 994).
  The UI thread still prepares and draws the map, into a pooled offscreen
  picture on the control's device, and hands it over with its view; the dots
  are placed in that view, so they stay on their arcs during pan and zoom.
  Win2D's WinUI 3 documentation lists `CanvasAnimatedControl` as
  unsupported, although it runs (Win2D issue 1004).
- **One sprite batch.** All dots are tinted copies of one white sprite, sent to
  the GPU in one batch, like the markers.
- **Stepped arc fade.** The engine sends rates every second, and each update
  starts a 1.6-second fade of arc brightness. Before this change, that fade
  redrew the whole map 30 times a second whenever traffic flowed, with or
  without dots. The fade now moves in steps of one colour level, at most 16
  steps, so it looks the same with few redraws: a 20% rate change takes about
  6 steps instead of 48 frames, and a jump from idle to full speed takes 16.
- **A game loop that sleeps.** The game loop runs only while dots fly or a
  new picture waits. Otherwise it pauses until the next dot leaves, and it
  stays paused when nothing transfers. The map's own timer wakes only for the
  next fade step. Minimising the window or leaving the Peers section unloads
  the map, which stops both.
- **Cheap positions.** Each route keeps its great circle as two vectors, so a
  dot's position is a few sines and cosines, not a path search.
- **Redraw the map and rebuild the glow only when they change.** Before this
  change, every rate update redrew the whole map and rebuilt the glow blur
  about once a second while traffic flowed. The renderer now keeps, per
  destination, everything it draws except strength: its key, whether it has
  an arc, its role kinds (seeds, leechers, unknown), whether it is active,
  and its larger direction, which sets the arc and glow colour; and, per
  estimated country, whether it is active, which sets its shading. This
  arrangement is one of the glow's inputs, and it changes only when that list
  differs from the previous update. When the largest strength change,
  measured against the last drawn strengths, is below one colour level,
  nothing fades or redraws; the drawn strengths stay as they are, so small
  changes add up until one is large enough to draw. A steady transfer with
  small rate changes then redraws nothing.
- **Per-dot values in the sprite.** Fades go into the tint and the angle into
  the transform, so the dots stay one batch.
- **Dots outside the view are skipped** before any fade or angle work.

### Only after a measurement

- Animating the dots on the composition thread with keyframes would wake the
  app only when a dot leaves. Pan and zoom would then have to restart every
  running animation. Build it only if a measurement on the owner's 15 W laptop
  shows that the game loop's frames cost real power.

## Checks after the build

These questions need the running app; reading the code cannot settle them.

1. Does the map show sharp, with no offset or blur, in Light, Dark and at
   125% and 150% scaling, and never black or blank?
2. Do the dots follow their arcs, including across the map's edge and while
   the view pans and zooms, and keep moving while the Peers table fills?
3. Do the Speed statistic and its legend fit in every corner and in
   Spanish, without clipping?
4. Do the starting values (160 DIP/s, 4.5-DIP head, 8-DIP tail, 10-DIP marker
   fade, 10,000 DIP² per dot, 15 DIP spacing) look right? They are judgement,
   not measurement.
5. How much CPU and GPU time does a second of map take with dots, against the
   map before this change, at 1080p and 4K, with steady traffic?
6. Do pan and zoom feel as quick as before? A new picture shows at the game
   loop's next frame, so it can reach the screen one frame later than a
   canvas drawn on the UI thread.

The `peers` capture review draws the map with synthetic peers and can answer
question 3. It counts the dots' frames while choosing a place with up to
10,000 peers busies the UI thread, which answers the frame-rate part of
question 2, and counts the pictures the layer shows during its drags. Its
screenshots leave out the layer; a full-content `PrintWindow` snapshot of the
capture window includes it.

## Other designs that are just as good

If the design does not look right on screen, these can replace one part of it
without changing the rest.

| Part | Alternative | Gain | Cost |
| --- | --- | --- | --- |
| Mark | **Plain round dot**, no tail | Calmest look; matches the legend exactly | At 160 DIP/s a dot moves about 5 DIPs per frame; may look slightly stepped |
| Mark | **Comet** with a tail of about 15 DIPs | Direction shows even more clearly in a still frame | The dots of a busy route, 15 DIPs apart, join into one line; the legend's "••" no longer matches the mark |
| Mark | **Tail length grows with route speed** | Speed shows in shape as well as spacing | A second visual channel for the same number; needs its own scale |
| Layout | **Two lanes**: download and upload dots about 1.5 DIPs on either side of the arc | Opposite dots never pass through each other | Adds noise on a 1–2 DIP arc; crossing dots already read as two-way traffic |
| Encoding | **Streaming dots**: fixed spacing, speed of motion taken from the arcs' logarithmic scale, no legend | Slow peers always show moving dots; the scale never changes; no legend needed | People judge motion speed poorly and it has no unit; every active arc moves all the time |
| Count | **Same number of dots at every speed** (about 32–64), only the size changes | The map looks equally lively at any speed | Slow and fast traffic look alike, and below about 10 KB/s the map is nearly empty |
| Count | **Count on the arcs' logarithmic scale** instead of the cube root | One speed scale for arcs and dots | Almost every real download shows nearly the same number of dots |
| Maximum | **Fixed 40 dots** at every map size, no spacing limit | Simplest; the same look at every size | Crowded on a small map, sparse on a full-screen map; one busy route can fill its arc with beads |
| Ceiling | **Fixed 5 MiB/s** for everyone, as the arcs use | No ceiling to track | A gigabit line stays at the maximum and a slow line never comes near it |
| Ceiling | **Highest total in the engine's day history**, read when the map opens | Knows the connection's speed from the last 24 hours before any traffic is seen | A pipe read and parse of up to 8,640 samples, a ceiling that jumps when the reply arrives, and a history request that must not release the peer reading |
| Ceiling | **An active speed limit lowers the ceiling** | A transfer at its limit shows the maximum | Limits switch with the schedule and can be one-sided or per torrent, so the ceiling would fall while the user watches |
| Arcs | **Arcs on the dots' ceiling** | One scale for arcs and dots | No single route fills a fast line, so every arc dims on fast connections and all arcs fade when the ceiling grows |
| Cost | **Floor of 16 KiB per dot**, one BitTorrent block | At 1 KiB/s the 30 Hz frames run about 9% of the time instead of all the time | Slow traffic shows one dot every 16 seconds or more, which reads as none |
| Cost | **Composition-thread animation** | Fewest wake-ups | Pan and zoom restart every animation |

These were considered and rejected:

- **Dot speed showing ping.** People read motion speed as transfer speed, ping
  mostly follows distance, which the map already shows, and the engine does
  not report it.
- **Marching dashes or light pulses along the arc.** They need the arcs drawn
  again every frame on the UI thread, and they compete with
  the arcs' own brightness.
- **Additive blending.** It glows in Dark but builds the bright knot at the
  user's location, and it washes out to white in Light.
- **A ripple when a dot lands.** At the user's location it would flash
  constantly.
- **Lighter or darker dot colours than the theme brushes.** The transfer
  colours must mean the same thing everywhere in the app.
- **Random gaps between departures.** The rates are averages, so random gaps
  would invent timing the data does not have.
- **A minimum number of dots per route.** It would show dots for bytes that
  never moved and make the legend false.
