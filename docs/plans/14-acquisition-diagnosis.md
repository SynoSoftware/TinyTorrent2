# Metadata acquisition diagnosis

Source inspection and isolated acquisition measurements, 2026-10-25.
The owner wants useful indexed torrents and files per minute. The current
[working plan](14-discovery-feedback.md) owns execution and settled decisions.
Raw traces, completed-run reports, periodic samples and the fixed manifest are
preserved under `artifacts/measurements/catalogue-20261025/`; disposable catalogue
databases are removed after analysis. The interrupted Both-64 arm has a complete
trace but no periodic-sample JSON because the owner stopped it before harness
completion. Its last observed CPU/memory values came from the streamed samples.

## Measured result

### Current transport comparison

The paced, peer-gated, peer-filter candidate on TCP only published 300 new
torrents / 9,705 selectable files in 302.966 seconds: 59.4/minute, peak rolling
minute 85, 17.02 CPU seconds and 40.6 MB peak working set. Exit was clean; no
status errors or false-empty samples occurred. The original fixture, 256 jobs
and shared 200-connection limit match the preceding Both-transport profile
(72.5/minute). Public DHT inventories vary; this supplies no evidence for changing
the transport default. Old-cohort conversion was 3/128 and controls 4/6. Evidence:
`artifacts/measurements/catalogue-20261025/metadata-peer-filter-tcp`.

The next single-variable candidate allows one connection per acquisition,
matching Bitmagnet's sequential endpoint approach. Libtorrent's
`session_impl::try_connect_more_peers` stops at the shared connection cap;
`torrent::want_peers` counts connecting peers against each torrent's limit.
With two connections, 100 hashes can occupy the shared 200 slots while other
admitted jobs' 30-second deadlines already run. One connection may expose more
hashes promptly, but may also delay a useful second endpoint; measured useful
output decides whether to keep it. Existing pipe commands expose no acquisition
connection counters; the current data does not establish how often this cap is
actually full. No dependency or session-wide timeout change follows from this
hypothesis.

The reviewed one-peer candidate completed at 338 new torrents / 20,160 files
in 304.609 seconds: 66.6/minute, peak minute 99, 21.69 CPU seconds and 41.8 MB
peak working set. Exit was clean, no status errors/false-empty samples occurred,
and 237 peer-positive hashes remained ready at the final sample. Conversion was
5/128 old sample hashes and 5/6 controls. Evidence is in
`artifacts/measurements/catalogue-20261025/metadata-one-peer`. It demonstrates
no useful throughput improvement over the preceding two-peer profiles, so the
three-line policy experiment was reverted exactly. Further limit changes need
current-pipeline occupancy and acquisition-stage evidence, rather than assuming
the shared connection cap is the dominant wait.

### Follow-up acquisition profiles

Both runs below use the final reviewed Release candidate (idle 256 jobs,
two peers/job, no idle per-job receive throttle), the same initial manifest and
normal live discovery. They differ only in the existing global connection-limit
setting. The owner's data is untouched. Completed evidence is under
`artifacts/measurements/catalogue-20261025/profile-256-global200` and
`profile-256-global800`, including samples, final publication records and a
cohort summary. The measurement now holds the shared capture lock after an
unrelated capture terminated an earlier attempt; that interrupted attempt is
not scored as a completed run or a production crash.

| Shared connection cap | Seconds including clean shutdown | Newly published torrents | Selectable files in new published torrents | Torrents/minute | Peak rolling minute | CPU seconds at last sample | Peak working set |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 200 | 303.254 | 226 | 11,373 | 44.7 | 71 | 40.44 | 42.9 MB |
| 800 | 304.985 | 124 | 2,966 | 24.4 | 44 | 29.73 | 41.3 MB |

Neither reaches the 100–1,000/minute reference target. More shared connections
did not improve this observation, so production connection defaults remain
unchanged. Public-network variability and different sampled inventories prevent
a causal claim that 800 itself caused the lower result. The fixed old sample
published 3/128 then 2/128; official controls published 4/6 then 2/6. Those same
hashes have aged since the earlier measurements, so their availability is not
controlled across the whole research period. The first profile's roughly
twofold increase over the older 22.2/minute observation is useful evidence,
not a repeatability guarantee.

The next source question is metadata-slot occupancy. Cold fallback filled all
256 jobs by five seconds, while the known-peer backlog reached 178 by 60 seconds
in the global-200 arm. Bitmagnet starts metadata requests only after a positive
peer lookup. A peer-gated candidate is tracked in the working plan; its
measurement completed as follows.

| Peer-gated profile, global cap 200 | Seconds including clean shutdown | Newly published torrents | Selectable files | Torrents/minute | Peak rolling minute | CPU seconds at last sample | Peak working set |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Unbatched frontier updates | 305.118 | 292 | 17,753 | 57.4 | 86 | 99.02 | 49.4 MB |
| Batched frontier updates | 303.468 | 287 | 23,393 | 56.7 | 106 | 57.05 | 48.1 MB |

The fixed old sample published 30/128 and controls 2/6. The higher live output
still misses the reference target and uses substantially more CPU. Status reads
at 41.74 and 124.43 seconds falsely returned successful empty catalogues before
recovering. Source establishes a concrete cause: the 64-job Store queue can
refuse bursts of individual peer/failure writes, and the status adapter encoded
that refusal as empty success. The next correction batches those writes through
existing maintenance and returns status failures honestly. Its performance is
unmeasured until the next run. Evidence for this run is under
`artifacts/measurements/catalogue-20261025/peer-gated-unbatched`.

The reviewed batching correction completed with clean Exit, no status errors
and no false-empty status samples. It published 29/128 old sample hashes and
2/6 controls. Overall torrent throughput is essentially unchanged in these
sequential observations; the useful improvements are lower CPU cost and removal
of the demonstrated queue/status defect. File-count differences are sensitive to
the torrents encountered. Its peak minute reached 106, but the five-minute
average remains below the reference target. Evidence is in `peer-gated-batched`.
A five-minute existing-binary continuation from this disposable catalogue
published 266 new torrents / 7,569 selectable files in 308.629 seconds:
51.7/minute, peak minute 83, 76.28 CPU seconds and 49.5 MB peak working set.
It completed cleanly with no status errors or false-empty samples. The ready
backlog grew from 1,026 to 1,897 while acquisition stayed mostly at capacity.
The low average is therefore not explained solely by a cold-start ramp. This
is a continuation, not a matched fresh-manifest algorithm comparison; evidence
is in `peer-gated-batched-warm`. The useful-rate goal remains unmet.

Supplier-only probing (same original fixture, 15-second probe deadline and 20
starts/tick) published 261 new torrents / 15,517 files in 303.780 seconds:
51.6/minute, peak minute 72, 15.06 CPU seconds and 36.9 MB peak working set.
Exit was clean, with no status errors or false-empty samples. Old cohort
publication was 6/128 and controls 3/6. This trades per-hash recursive recovery
for much lower observed resource use, without establishing a speed improvement.
Metadata acquisition was mostly below its 256 capacity and ready stayed zero.
Evidence is in `supplier-only-15s-start20`.

Bitmagnet's `internal/protocol/dht/server/factory.go` applies per-IP pacing to
all outbound queries: one/second, burst four, with a four-second query timeout
after waiting for the limiter. Its 200 workers are not 200 simultaneous requests
to one supplier. Our 20/tick selector can take many hashes from one sampled
supplier. Libtorrent's default DOS blocker can block after 50 packets in ten
seconds, including responses received locally. Admission must spread suppliers
before increasing starts; the 15-second full-lookup-oriented deadline also
limits dead-supplier turnover. This is a source-supported next hypothesis, not
a measured explanation of all earlier stalls.

Supplier-diverse admission, direct deadlines four seconds and up to 50 starts
per tick published 377 new torrents / 26,444 files in 304.675 seconds:
74.2/minute, peak minute 99, 23.95 CPU seconds and 42.2 MB peak working set.
The schema migration retained the starting seven torrents / 51 files. Exit was
clean with no status errors or false-empty samples; old sample publication was
6/128 and controls 2/6. Ready grew to 534 while metadata was mostly full.
Evidence is in `supplier-diverse-4s-start50`. These follow-up fixtures reuse
saved endpoint hints, including control hints; they are not fresh DHT-only
control arms. The reference target remains unmet. An existing-binary global-800
comparison tests the remaining connection-budget question for this changed
peer-positive workload; it changes no product or owner setting.

That global-800 arm published 359 new torrents / 32,077 files in 304.380 seconds:
70.8/minute, peak minute 117, 23.66 CPU seconds and 41.4 MB peak working set,
with clean Exit and no status errors/false-empty samples. It supplies no evidence
for raising connection defaults. Evidence is in `supplier-diverse-global800`.
Next address the confirmed metadata-slot waste from connected peers that cannot
serve metadata, rather than raising width or replacing the requester.

The crawler peer-capability filter published 369 new torrents / 24,360 files in
305.349 seconds: 72.5/minute, peak minute 116, 25.23 CPU seconds and 41.9 MB peak
working set. Exit was clean; status errors and false-empty samples remained
absent. Sample publication was 5/128 and controls 5/6. This does not establish
an overall throughput improvement; the control difference is also subject to
public-network variability. Evidence is in `metadata-peer-filter`.
Final lease inspection supplies no evidence of peer-empty unfailed acquisitions
remaining beyond their saved 120-second eligibility, so no absolute-lifetime
mechanism is justified by these runs. The useful-rate goal remains open.

### Earlier matched build-mode and transport measurements

Release removes the demonstrated multi-second add-callback delay in Debug.
Broader overlapping acquisition improves observed output; switching the shared
session to TCP-only does not establish an improvement. Keep ready-first
selection, the existing configurable limits, transport policy and stall deadline.
No new requester, session, storage algorithm or discovery architecture is justified
by these observations.

The same reviewed diagnostic source was built in Debug and Release using the
existing dependencies. Each run starts from seven retained torrents / 51 files,
128 seed-20261025 randomly sampled pending hashes and six official Ubuntu
hash-only controls. Normal discovery stays on; the fixed sample is reported
separately from newly discovered work. The controls' info dictionaries were not
admitted or supplied to acquisitions. Only metadata is acquired, with the normal
payload guard. The owner's database, settings and transfers are untouched.

| Profile | Observed seconds | New retained torrents | New selectable files | Torrents/min | CPU seconds | Peak working set |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Debug, Both, 32 | 296.79 | 10 | 181 | 2.0 | 325.28 | 58.8 MB |
| Release, Both, 32 | 298.18 | 60 | 955 | 12.1 | 43.30 | 37.8 MB |
| Release, Both, 64, interrupted by owner | 246.27 | 92 | 7,215 | 22.4 | 36.52 | about 39.9 MB |
| Release, TCP, 64 | 296.14 | 94 | 5,215 | 19.0 | 39.67 | 38.2 MB |

These are last periodic status samples, not final database totals: in-flight
metadata can finish between the last sample and collection stopping. TCP's last
sample also has publication still pending. File counts include retained
selectable entries; searchable publication can lag an archive admission.
Large individual torrents make file rates particularly variable.
The 64-slot Both run was deliberately stopped, not completed for five minutes.
After collection stops and queued publication drains, all retained entries are
searchable: Both-32 ends with 64 new torrents / 965 files in 303.09 seconds,
Both-64 with 92 / 7,215 in 248.59 seconds, and TCP-64 with 96 / 5,234 in
301.02 seconds. Final published rates are respectively 12.7 / 22.2 / 19.1
torrents and 191 / 1,741 / 1,043 files per minute. CPU and memory above come
from the periodic observations, rather than a sample after process destruction.
Sequential public-network runs do not prove a repeatable speedup. At the same
246.27-second trace window, validations are 47 / 92 / 72 for Release Both-32 /
Both-64 / TCP-64 respectively; every validation was accepted by storage.

### Fixed random cohort

Counts below are distinct hashes except explicitly labelled connection events.
Existing peer hints can precede the observation window, so the observed BEP 5
response row is not a prerequisite for every later row. Attempt counts are lower
bounds: hinted connections can start before the add alert associates their handle.

| Stage | Release Both-32 | Release Both-64 | Release TCP-64 |
| --- | ---: | ---: | ---: |
| Discovered hashes supplied | 128 | 128 | 128 |
| Selected for retrieval | 124 | 128 | 127 |
| Observed positive BEP 5 peer response | 92 | 64 | 63 |
| Observed outgoing connection attempts, lower bound | 273 | 255 | 106 |
| Hashes with an established transport | 47 | 39 | 30 |
| Hashes advertising nonzero `ut_metadata` | 34 | 29 | 19 |
| Validated metadata | 19 | 18 | 14 |
| Retained metadata | 19 | 18 | 14 |
| Retained / selected | 15.3% | 14.1% | 11.0% |
| Retained selectable files | 430 | 398 | 306 |
| Stall-removal requests | 105 | 110 | 113 |

Unselected or interrupted work is censored, not a failed acquisition. A stall
record requests removal; it is not necessarily a terminal failure. In Debug,
three valid responses arrived 173–200 ms after removal was requested and were
salvaged. Success takes precedence when classifying those attempts.

All six controls found peers in each Release run. Both-32 retained 6/6;
Both-64 and TCP-64 retained 5/6 each. Thus the real acquisition path can retrieve
healthy public metadata, while most randomly discovered hashes fail to provide
it within an attempt. This does not prove that every failed swarm is dead.

### Latency, pressure and failure interpretation

For the random sample, median request-to-add was 4 / 5 / 5 ms, and median
request-to-transport was 224 / 355 / 319 ms. Conditional successful metadata
latencies were 3.03 / 2.62 / 2.48 seconds. Those success samples contain fewer
than 20 entries each; a tail percentile would be misleading. Transport p90 was
3.97 / 26.05 / 23.69 seconds; handshake p90 was 19.49 / 26.34 / 23.91 seconds.
Each distribution is conditional on reaching that stage, not the latency of all
discovered hashes. Controls sometimes started receiving metadata near the
30-second stall deadline; Bitmagnet's six-second per-endpoint timeout is not a
safe whole-acquisition replacement.

Observed acquisition concurrency peaks at 32 / 64 / 64. Periodic status averages
are 30.4 for Both-32 and 61.5 for TCP-64; the interrupted Both-64 run has no saved
periodic-sample series. Connected-peer peaks are 20 / 20 / 21, with half-open
peaks 59 / 119 / 109. These are independent maxima, not simultaneous sums.
The configured shared cap is 200 with 30 outgoing starts/second. Storage
admission in Both-32 took a median zero, maximum two milliseconds, with no
rejections or storage errors. Neither the ordinary active-download queue nor
archive admission explains the low random-hash conversion.

Connection and protocol timeouts dominate observed disconnect errors, followed
by refused/reset/closed endpoints. For example, Both-64 logged 953 Windows
10060 and 490 libtorrent `timed_out` disconnects. These are events, including
repeated endpoint attempts, not 1,443 distinct failed torrents. Libtorrent
`torrent_aborted` records our removals; `upload_upload_connection` can follow
successful metadata and the payload guard's finished state. Neither should be
counted automatically as an acquisition defect. No metadata validation or
storage failure was observed in these Release runs.

Over 90% of acquisitions already begin with peer hints. Hinted attempts completed
at approximately 17–18% in the Both runs; the larger width changes useful
throughput rather than candidate quality. Archived info dictionaries grew by
about 5.30 MB in the completed Both-32 run and 7.45 MB in Both-64, approximately
0.02–0.03 MB/second of retained metadata. This is neither network payload
throughput nor a measurement of the connection's bandwidth ceiling.

All three Release traces have complete sequence numbers, no dropped alerts and
an explicit collection stop after starts. Full debug peer logging is temporary
measurement scaffolding and is removed after the comparison. Existing Search
and Settings keep the production indexed/fetching/probing/queued statistics.

Remaining limits: no full ten-million-file benchmark, no maximum-throughput
claim and no controlled active-transfer impact measurement. A higher idle
ceiling needs that resource-priority evidence: busy transitions lower existing
handles' limits, but do not immediately shrink their count to the busy setting.
Exit refinements remain deferred by the owner.

## Initial finding before the cohort

The available evidence locates most lost throughput before metadata completion,
but does not yet distinguish unavailable swarms from poor connection or metadata
exchange behavior. Increasing discovery alone does not address that gap.

The retained diagnostic trace `V:/temp/plan14-crawl-16.log` contains 64 acquisition
starts, three metadata successes, 45 explicit timeouts and 16 attempts interrupted
by Exit. Among its 48 terminal outcomes, metadata success is 6.25%; dividing by
all 64 starts gives 4.69% but treats censored work as failures. Forty starts never
show a sampled transport-connected peer. The successful attempts took 19, 24 and
30 seconds from start. Carried-forward status samples peak at 45 connections
including connecting peers, and eleven transport-connected peers. These are
sampled crawler values, not exact session peaks or handshake counts.

This older trace predates the fresh-status expiry correction. It cannot measure
that correction or establish the final build's failure distribution. The final
build's reported seven torrents / 51 files in 140 seconds establishes poor growth,
but does not expose the missing stages below. Existing status output cannot
produce a truthful seven-stage conversion funnel by itself.

## What is verified in the current source

* Discovery and metadata acquisition are separate. BEP 51 supplies sampled hashes;
  BEP 5 finds endpoints for a particular hash; BEP 9 requests that torrent's info
  dictionary from a peer. The standard protocols do not ask a torrent peer for
  its complete catalogue of torrent metadata. [Collection owner][collection].
* `MaintainIndex` allocates ready-peer work first, then ordinary eligible work.
  `Acquire` starts a 30-second stall deadline before `async_add_torrent` completes.
  Adding, removing and pending admissions count against capacity. Queue and remove
  latency therefore consume a slot; the trace needs to measure them. [Dispatch][dispatch].
* `Guard` clears `auto_managed`, and acquisition clears `paused`. These metadata
  handles bypass libtorrent's ordinary active-download queue. Its peer eligibility
  includes `downloading_metadata`; unwanted payload does not stop metadata
  retrieval. There is no source evidence of a one-active-download serialization
  bug. [Guard][guard], [libtorrent eligibility][wantpeers].
* The existing global connection limit and outgoing connection rate apply to the
  shared session. Defaults are 200 and 30 new connections/second. Per-acquisition
  limits are eight idle or four busy peers, and 64 or 16 KiB/second. The global
  defaults are not necessarily the owner's saved settings. At 64 jobs, eight
  connections each can exceed a 200-connection session budget; at 32, the
  potential 256 also exceeds it. That arithmetic does not prove saturation in
  the observed run. [Defaults][defaults], [policy][policy], [dispatch][dispatch].
* Libtorrent's connection boost starts several known peers promptly; its ordinary
  connection loop shares starts across eligible torrents. There is no hidden
  requirement to finish one torrent before connecting to another. [Boost][boost],
  [shared connection loop][connectionloop].
* Empty DHT capability flags do not force TCP. A newly created peer optimistically
  has `supports_utp=true`; both-transport policy initially chooses uTP and falls
  back to TCP after a failed uTP connection. However, **15 seconds is not the
  normal silent-uTP fallback delay**: the uTP stream starts with its own
  `utp_connect_timeout`, default 3,000 ms, and an unconfirmed socket fails at its
  first timeout. The generic `peer_connect_timeout` defaults to 15 seconds, but
  quoting it alone overstates this hypothesis. Actual scheduling and transport
  latency still need measurement. [Peer default][peerdefault], [transport choice][transport],
  [fallback][fallback], [uTP timer][utptimer], [uTP expiry][utpexpiry], [timeouts][timeouts].
* A direct source-node peer probe gets up to 15 seconds, followed by a stock
  lookup window up to 15 seconds. These probes run independently from metadata
  slots. Database leases prevent simultaneous selection of the same pending row;
  ready endpoints restore immediate eligibility. After probe expiry, the existing
  stock DHT traversal can continue and late replies can still help a handle.
  Measure direct-positive and fallback-positive rates separately. [Probe owner][dispatch],
  [reply handling][replies], [pending selection][pending].
* Metadata success invokes archive admission, removes the network handle, and
  later projects files through bounded 1,024-file / 75-ms slices. This is no
  longer a one-torrent-per-second publication path. No current trace demonstrates
  storage as the dominant bottleneck. [Admission caller][ingest], [projection][projection].

## Cohort evidence and missing observations

Use one acquisition request ID plus lookup hash throughout. Retry attempts have
new request IDs; aggregate both distinct hashes and attempts. Record monotonic
times and terminal outcomes, including cancellation, rather than silently
counting unfinished attempts as failures.

| Stage | Existing evidence | Minimal additional observation |
| --- | --- | --- |
| Discovered | Pending inventory and raw sampled counter | Count new distinct pending inserts separately from repeated samples; retain source type in the diagnostic cohort manifest. |
| Selected | Historical start trace; current active gauge | Record selection, add completion/error and removal completion, hint count and direct/fallback origin. |
| BEP 5 returned peers | Current ready gauge; endpoint reply handlers | Per-hash first peer response, unique endpoint count and direct/stock origin; distinguish empty response, timeout and late response. |
| Connections attempted / established | Historical sampled connection and peer gauges | Filter libtorrent's existing outgoing attempt and `connection_established` events for cohort handles, including transport and endpoint. |
| Peer supports metadata | Not present in current public status | Filter existing incoming `extended_handshake` peer-log events; record advertised nonzero `m.ut_metadata` and positive `metadata_size` separately. |
| Metadata received / validated | `metadata_received_alert`; current metadata failures are only displayed for previews | Record request-to-first-data, received metadata data messages, success time and `metadata_failed_alert` error; preserve separate parse/hash failures. |
| Retained / published | Indexed totals and archive/projection state | Record `Catalogue::Admission` result (accepted or excluded), duplicate/enrichment distinction, storage error and complete-publication time/files. |

Two instrumentation traps matter:

1. In this pinned source, the outgoing `peer_connect_alert` is emitted immediately
   after `async_connect`, **before** transport establishment. Its header's success
   wording does not match that implementation. Count it as an outgoing attempt.
   `peer_log_alert::connection_established` is emitted from the successful
   completion callback. [Attempt][attempt], [established][established].
2. `torrent_status::num_peers` subtracts connecting peers but can still include
   peers waiting for the BitTorrent handshake. `supports_extensions` means BEP 10,
   not specifically BEP 9. Metadata support requires the extended handshake's
   `ut_metadata` advertisement. [Status gauge][gauge], [metadata handshake][handshake].

Existing dependency exports in `3rdParty/Debug/libtorrent.definitions` and
`3rdParty/Release/libtorrent.definitions` do not disable logging; the dependency
build's logging option defaults on. These existing peer-log events avoid adding
another metadata requester or peer-plugin implementation. The current engine
alert mask does not enable peer logs or connect alerts. Temporarily enable their
categories for the isolated diagnostic run, filter cohort events before writing,
and count dropped alerts. Keep only async-connect, establishment, handshake,
metadata message and failure events; writing every socket event would distort
the workload. No dependency rebuild is needed. [Alert mask][mask], [event sources][peerlogs].

The current ingestion lambda discards the `Catalogue::Admission` return value.
Consequently a successful storage task does not mean a new retained torrent:
an exclusion or enrichment can also succeed. Observe that existing return value
for the cohort instead of inferring retained count from metadata successes.
[Admission result][admission], [caller][ingest].

## Measurement design (executed above)

The question justifying diagnostic instrumentation is: **does most time and loss
occur obtaining usable peers, opening transports, completing protocol handshakes,
receiving valid metadata, or retaining it?** Source and the existing binary cannot
answer that question. Implement and review the compact observations together
before the lead's single diagnostic compilation. No algorithm change belongs in
that build; it establishes the baseline.

Use a copied temporary catalogue and the existing payload guard. Pin a manifest
of 128 randomly selected distinct pending hashes from the saved backlog, with a
recorded random seed and original hints. Do not choose only hashes that already
have peers: that would remove the suspected failure from the sample. Keep normal
discovery running, but label cohort events so unrelated growth cannot inflate
its success rate. Compare cohorts at equal configured concurrency, connection
budget, route, encryption and metadata limits.

Add six public controls selected from current official Ubuntu, Debian and Fedora
torrent listings. Record the exact source URL and info hash in the manifest.
Give acquisitions **only the hashes**, not their downloaded info dictionaries,
because admitting those dictionaries would bypass BEP 9. First test controls
through DHT like ordinary sampled work. For any failing control, a separate
known-peer arm can feed a freshly tracker-confirmed endpoint into the same
acquisition path; this separates failed DHT lookup from failed metadata exchange.
An official torrent is only a prospective healthy control until a current peer
actually serves valid metadata. [Ubuntu][ubuntu], [Debian][debian], [Fedora][fedora].

Observe for five minutes, with a bounded drain period for work already started.
Report censored attempts separately. Summarize cohort conversion counts, distinct
successful hashes, p50/p90/p95 stage latency where sample size supports them,
time-weighted active/connecting/metadata-capable counts, failure reasons, new
retained torrents/minute, published files/minute, metadata bytes, CPU, working set
and database growth. Three successes support individual latency values, not
credible percentile distributions.

Choose the next change from this result:

* Healthy controls fail after peer discovery: correct the shared transport or
  protocol path before changing candidate selection.
* Healthy controls succeed while random hashes mostly have no responding peers:
  improve selection from fresh positive hints; a larger hash inventory alone
  cannot supply live metadata peers.
* Connections or metadata peers wait despite spare slots and peer hints: correct
  the measured admission/scheduling delay at its existing owner.
* Admission/publication dominates: correct that measured storage path.

If the transport events show material avoidable uTP-first latency, compare Both
and TCP on fresh clones of the **same manifest** using the existing binary's
settings. This affects only the disposable engine. Hold every other setting
fixed; compare retained output and connection cost, not merely dial latency.
An adjacent run with different random hashes cannot identify that effect.
Repeat the same workload after the smallest supported correction. Preserve the
single production session and metadata owner. Only evaluate active-transfer
impact for a candidate that improves idle throughput, with an explicitly
controlled transfer rather than changing the owner's live jobs.

[collection]: ../../engine/src/Engine/Indexing.cpp#L728
[dispatch]: ../../engine/src/Engine/Indexing.cpp#L445
[guard]: ../../engine/src/Engine/Additions.cpp#L9
[wantpeers]: ../../3rdParty/libtorrent/src/torrent.cpp#L8787
[defaults]: ../../engine/inc/Engine/State.h#L196
[policy]: ../../engine/src/Engine/Policy.cpp#L391
[boost]: ../../3rdParty/libtorrent/src/torrent.cpp#L4052
[connectionloop]: ../../3rdParty/libtorrent/src/session_impl.cpp#L4494
[peerdefault]: ../../3rdParty/libtorrent/src/torrent_peer.cpp#L157
[transport]: ../../3rdParty/libtorrent/src/torrent.cpp#L8058
[fallback]: ../../3rdParty/libtorrent/src/peer_connection.cpp#L4194
[utptimer]: ../../3rdParty/libtorrent/src/utp_stream.cpp#L141
[utpexpiry]: ../../3rdParty/libtorrent/src/utp_stream.cpp#L3421
[timeouts]: ../../3rdParty/libtorrent/src/settings_pack.cpp#L246
[replies]: ../../engine/src/Engine/Indexing.cpp#L846
[pending]: ../../engine/src/Catalogue.cpp#L1257
[ingest]: ../../engine/src/Engine/Indexing.cpp#L374
[projection]: ../../engine/src/Catalogue.cpp#L1864
[attempt]: ../../3rdParty/libtorrent/src/peer_connection.cpp#L445
[established]: ../../3rdParty/libtorrent/src/peer_connection.cpp#L6347
[gauge]: ../../3rdParty/libtorrent/src/torrent.cpp#L12527
[handshake]: ../../3rdParty/libtorrent/src/ut_metadata.cpp#L182
[mask]: ../../engine/src/Engine/Session.cpp#L150
[peerlogs]: ../../3rdParty/libtorrent/src/bt_peer_connection.cpp#L2030
[admission]: ../../engine/src/Catalogue.cpp#L1091
[ubuntu]: https://ubuntu.com/download/alternative-downloads
[debian]: https://www.debian.org/CD/torrent-cd/
[fedora]: https://fedoraproject.org/torrents/
