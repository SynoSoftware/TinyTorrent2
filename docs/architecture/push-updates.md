# Change-driven refresh

Parked concept, not scheduled. Owner decision (2026-10-08): the benefit is
about half a second less staleness at the default interval and less idle pipe
traffic, while the engine samples and builds the snapshot every second either
way. That does not pay for a protocol change and a window refresh rewrite.
Evaluate again when a measurement shows the open window costs noticeable CPU
or battery while idle, or when the refresh-interval setting is to be removed
for its own reasons.

It is separate from the inspector detail-read change in
[detail-reads.md](detail-reads.md) and builds on it: it needs that change's
explicit inspector demand (`context`, `release`) and its pushed `detail`
message.

## Problem

For periodic updates, the window uses a timer. Every refresh interval it requests
the summary snapshot, and the inspector requests its open section. The person
sees three things because of that:

- Changes already observed by the engine can wait up to one refresh interval
  to appear. Command replies already trigger a separate snapshot request.
- Settings has a "refresh interval" control whose only purpose is to make the
  polling cheaper. It changes nothing about how often the engine samples.
- An idle library is refreshed as often as a busy one.

## Facts the design rests on

- The engine already samples libtorrent once a second (`statusInterval` in
  Session.cpp) with `post_torrent_updates`, and libtorrent answers with only
  the torrents whose status changed. The engine therefore already has
  change-driven data at one-second granularity. The window is the last hop,
  and it pulls.
- `Snapshot()` in Session.cpp builds every row plus settings, limits, activity
  and connection-test state in one JSON value. A row is a few hundred bytes.
- The window's `PipeClient` runs one `PeriodicTimer` at the configured
  interval and reads `snapshot` on each tick. The inspector reads its section
  with a `context` on each tick; the engine answers at once and pushes one
  `detail` result when the section's queries finish.
- The pipe keeps one latest-wins slot per connection for `detail`. Replies and
  control messages go first. The reliable output queue closes the connection
  after four unsent messages, so pushed updates must never enter it.
- `refresh_interval` exists on both sides: a window preference and an engine
  setting (Settings.cpp), 1,000 to 10,000 ms. The protocol says it changes
  window demand only.
- `engine/tests/Engine.ps1`, `Checks.ps1`, `Transfer.cpp` and
  `app/tests/Capture.ps1` send `snapshot` requests.

## Design

- **D1. The engine's one-second tick is the only clock.** The window has no
  refresh timer. Nothing in the engine samples faster or slower because a
  window is open; the tick already runs for policy and history.
- **D2. The list is a subscription.** A `snapshot` request with `follow: true`
  answers as today and registers the connection. On every later tick whose
  snapshot differs from the last one sent to that connection, the engine pushes
  `{type: "snapshot", ...}` with the same body as the reply. Disconnect ends
  the subscription. A plain `snapshot` request keeps its pull semantics, so
  tools and reconnects are unchanged.
- **D3. Push only what changed, compared whole.** The engine keeps, per
  subscribed connection, the serialized snapshot it last sent and compares the
  new one with it. One string per connection, a few kilobytes. Field-level
  diffs and replay logs stay out, as protocol.md already requires; the
  comparison is a string compare on data the engine builds anyway. Without this
  rule an idle library would be pushed every second for nothing.
- **D4. One latest-wins slot per message type per connection.** The `detail`
  slot in `Pipe::Connection` becomes a map from type to message. Replies and
  controls still go first. A slow window receives fewer updates, never a closed
  connection. This is the bound the protocol asks for: at most one undelivered
  update per kind per connection.
- **D5. The inspector refreshes while its demand exists.** The current change
  registers a section's demand by `context` and withdraws it by `release` or by
  a section change. With that in place, the engine re-runs the registered
  section's queries on each tick, through the existing `Query`, `Receive` and
  `ContinueReading`, and pushes a `detail` result when the described JSON
  differs from the last one pushed for that context. The window stops reading
  the section on a timer; its one read per section change stays. The watch
  timer rejected in detail-reads.md was rejected because demand was implicit
  and expired between reads. With explicit release that reason no longer
  holds, and detail-reads.md records the change.
- **D6. The Speed section follows the same rule.** While its context is
  registered, the engine pushes the five-minute `history` body on each tick
  that added a sample, about 13 KB. The day range stays on request. One shape
  for all sections is simpler than a per-sample message; measure before adding
  one.
- **D7. Commands keep immediate feedback.** After a command reply the window
  requests one `snapshot` as it does today, because a reply and then a tick is
  up to one second of a stale list. The engine's push on the next tick then
  carries nothing new for that connection and is skipped by D3.
- **D8. The refresh-interval setting goes.** Window preference, engine setting,
  the Settings control, its strings and the capture fixture are removed. The
  connection test's one-second override disappears with it, because one second
  is now the only cadence. A control that changes nothing must not stay.
- **D9. Protocol version rises** and the hello check on both sides follows.
  Nothing else in the protocol changes shape: `snapshot`, `history` and
  `detail` bodies are the ones that exist.

## Rejected alternatives

| Alternative | Reason |
|---|---|
| A fixed one-second timer in the window | Still polling: one interval of latency after every change and the same request traffic when idle. Removes the setting without the benefit. |
| Push every tick without comparing | Idle libraries would wake the window every second to redraw nothing. The compare costs a string of a few kilobytes. |
| Field-level patches or a replay log | protocol.md forbids them; a whole snapshot is small and coherent by construction. |
| Engine tick faster than one second while a window is open | No evidence anyone needs it; libtorrent's own statistics are per second. Reconsider only with a measurement. |

## Delivery

Build once per stage, Debug x64, and only when the stage's evidence needs it.

1. **Pipe and list push (engine).** Generalise the slot (D4), add `follow`
   (D2) and the per-connection compare (D3), bump the version (D9). Evidence:
   `Engine.ps1` passes with the new version; a pipe client that subscribes
   receives a `snapshot` push when a torrent is paused and none while idle.
2. **Window list from pushes.** Remove the refresh timer; subscribe on
   connect; keep one `snapshot` after commands (D7) and on reconnect.
   Evidence: the capture review of the main window matches today's images.
3. **Inspector and Speed on the tick (D5, D6).** Engine re-queries while a
   context is registered; window stops its per-tick read. Evidence: open Peers
   on a transferring torrent and see it update without any `torrent` request
   after the first; close the pane and see the queries stop in the engine log.
4. **Remove the setting (D8)** on both sides, including strings and fixtures.
   Evidence: capture review of the Settings page; `Checks.ps1` unaffected.
5. **Measure once in Release**: engine CPU with a 1,000-torrent idle library
   and the window open, which should be indistinguishable from the window
   closed; and update latency from a pause command to the redrawn row, which
   should be the command round trip plus one tick at most.
