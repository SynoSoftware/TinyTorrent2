# Discovery feedback and native UI corrections

## Current batch: useful acquisition throughput

**Current owner request:** show the engine-reported algorithm in Settings so
manual performance comparisons identify which implementation is running. Add a
read-only Collection row and retain it in the collapsed summary; disable the
original-only concurrency rows for the translation and explain why. Selection
remains through the existing file switch. Root owns the coupled managed status,
view and six catalogue edits plus docs and the sole final build. One focused
independent review follows the complete edits; no new presentation tests or
captures are justified. Estimate: one round, about 0.2 hours, low risk; the wire
already carries `crawler`, so the initial label requires no protocol change.

**Additional owner steer:** show effective metadata/peer capacities during
evaluation. Extend Activity with active/max fetching and peer-lookup counts,
taken from the running scheduler. Root owns the small native Statistics/status
extension, managed optional limits, protocol 28 bump and all six translations.
Original crawler denominators remain absent instead of guessed; stopped or
unstarted translations expose no cached capacities. Its metadata admission loop
and reported limit share one calculation. The algorithm-label batch passed
its focused independent review and final app build (14:35:36 UTC); this steer
arrived during that build. Complete the capacity extension before another
focused review and combined final build. Estimate: one additional round,
about 0.2 hours, low risk. No crawler tuning or scheduling change is authorized
by this display work.

The complete extension passed independent correctness, ownership and fresh-read
review (`acquisition_evidence_read`). Corrections preserve the accessible cause
when collection is blocked and put peer lookups in a separate ordinary row,
with a dash while inactive, instead of crowding the Activity value. Final view
hashes: `4f3cc3d2176fe8cc09416cf60673382617284dd2` (XAML) and
`1ede08ae237b6afdcaedda66af8b06b29f6a39f0` (code-behind). Root's combined Release
build passed at 2026-10-26 14:54:26 UTC, logs prefix `20261026-145133`. The engine
beside the app matches the current engine output, SHA256
`A446E7B75FF85746075E3CEC4F662FFD0FEA0DC3EA9E0AED58C0CFAE25F97B8E`.
Reviewed Settings, status and scheduler hashes remain unchanged. The earlier
app failure (`20261026-144052`) coincided with shared-checkout Inspector edits;
root left those files untouched and independently reviewed their changed pair
before retrying. No task-owned app was launched and the owner's data/config
remain unchanged. Only the existing handshake assertion's expected protocol
number changed; no new tests or broad suite were justified. The capacity/label
batch is complete; the lower observed owner rate remains unexplained and
upstream comparison below remains open. Total elapsed was about 0.45 hours,
including the additional steer and the shared build wait/retry.

The existing manual switch remains:
Turn Indexing off, edit `%LOCALAPPDATA%/TinyTorrent/crawler.json` to
`{"algorithm":"bitmagnet"}` or `{"algorithm":"libtorrent"}`, then turn it on.
Absent configuration selects libtorrent. Both use the existing catalogue;
switching requires neither clearing it nor rebuilding search. The original
idle/busy concurrency controls govern only the original crawler. Root previously
finished the matching Release app/engine output for the owner's test:
`artifacts/bin/TinyTorrent/release_win-x64/TinyTorrentUI.exe`.
The owner's configuration remains unchanged.

**Then resume:** prepare a benchmark of the actual upstream Bitmagnet Windows
binary. The owner explicitly clarified that comparison against our translation
does not answer performance against Bitmagnet itself. No upstream executable
has been benchmarked yet; do not present its FAQ range as measured evidence.
`acquisition_evidence_read` owns temp-only preparation under
`V:/temp/plan14-upstream-20261026`, with no launches until root reviews the run
commands. Existing PostgreSQL 18 binaries were found under Program Files; use
an isolated temporary cluster without touching installed services or databases.
Official Windows release archives avoid rebuilding anything in `3rdParty`.

Warmed baseline session `40976` finished cleanly: 81.57 torrents/minute versus
the completed translated crawler's 153.30 newly searchable torrents/minute.
The measured lower target band is reached by the experimental arm; actual
upstream comparison and ordinary-client integration are not complete. Both implementations
are built and independently reviewed. Do not infer a warmed winner from the cold
pair. Historical best five-minute baseline is 74.2 torrents/minute.
TCP-only (59.4/minute) and one-peer acquisition (66.6/minute) supplied no
improvement; one-peer source is reverted. The proposed temporary diagnostic
batch was cancelled and its five source files restored before any build/run.

**Owner decision, settled:** retain the existing crawler for A/B comparison and
implement another crawler as a direct translation of pinned Bitmagnet
`e31b30d7351c269dc801876f92ebc59d31fee545`. Use Bitmagnet's actual configuration
parameters and defaults; do not force this algorithm through our invented
idle/busy knobs. This explicitly authorizes alternative crawler implementations
for comparison. It does not create another database owner, download workflow,
process or libtorrent session. Preserve full accepted metadata and all valid
file entries. Identify source bugs and necessary product/protocol adaptations
explicitly rather than silently describing an approximation as exact parity.

The translated source is integrated and the corrected Release build passed.
Six specialist passes and the consolidated correction round are complete;
`port_paths_resume` finished a clean independent delta over all six passes.
Root owns shared integration and the sole build. Requester SOCKS4 USERID,
TCP_NODELAY and physical proxy-address fallback are fixed, preserving the original
three-second total dial budget. `port_paths_resume` owns independent delta review;
`ab_benchmark` updated only the existing disposable measurement helpers.

The small collection-status correction in `Search/Browser.cs` and
`Shell/MainWindow/Chrome.cs` is complete. `acquisition_evidence_read` finished a
clean independent six-pass review: blocked collection reports unavailable,
suppresses fetching/rate, and exposes its cause through the existing tooltip
and accessible name. Off remains correct after a nonstorage error; search/Add
remain independent. Reviewed hashes are `46b45fce20f06513ffde4bd594721b339ba52c56`
and `f90ac2f1a57cbd1eb9e986fad51523ff08f729af`, respectively. Existing localized
text and layout are reused. Final Release app/engine build passed at
2026-10-26 14:20:16 UTC; logs use prefix `20261026-141630`. The engine beside
the app matches the current engine output, SHA256
`D7D52657BAACCFE4E0D57E3380089E3B1B0E7DB2553B0D7E58CC72145B589599`.
The reviewed file hashes are unchanged. Ordinary computed-state changes justify
no additional capture or UI test. This refreshed binary is for manual testing;
the four completed performance measurements above used the earlier recorded
benchmark binary.

Confirmed corrections in this round: reuse one adapter snapshot for DNS and
binding; validate bootstrap ports before network work; use the session DHT ID
for routing-table origin; preserve producer work under backpressure; start node
forwarding's timeout after triage; use LRU limiter eviction; persist discovery
through one owner; share availability estimation; align decode limits; localize
new deterministic errors in all six engine catalogues. Algorithm switches stop
the outgoing crawler and guard queued baseline admissions. SQLite admission now
yields every eight records, retains refused batches and records the completed
prefix before any storage failure. Healthy pending batches drain after stopping
collection; storage failure stops collection and remains visible.

The first final build found one unavailable ABI1 method, `add_dht_router`;
the complete log contained that sole error. The bootstrap adapter now uses
the supported asynchronous `add_dht_node` (ping then routing admission), with
clean focused source review before the successful rebuild. Logs are in
`artifacts/compilation/logs/20261026-115825-Engine.log` and the following build
record. The current measured binary is
`artifacts/bin/Engine/Release/TinyTorrent.exe`; do not use the earlier app output
to test this new alternative.

The earlier benchmark session `4128` disappeared without a saved completed
result. Do not count its partial output. Replacement session `79664` and paired
libtorrent session `23727` both completed cleanly. The longer-warmup translated
run `67405` and matching libtorrent arm `40976` also completed:
600-second warmup, 600-second collection, 75-second drain,
same binary and starting fixture. Do not compare warmed port throughput with
the baseline's cold window or select a winner from unmatched windows.
The earlier cold pair uses one-minute warmup, ten-minute collection and a separately measured
75-second publication drain. The
fixture `V:/temp/plan14-profile-20261025/live-base-20261026.db` has seven retained
baseline torrents, no pending hashes and 3,188 cached nodes. SHA-256:
`645044bd2f3d3bd7a682cb4ed29980dbf3bde452ab1d4b8ffc5e436235f6bb14`.
It is not a fixed acquisition cohort; the paired baseline will use an identical
copy, and comparisons exclude the existing seven records.

Completed Bitmagnet evidence is in
`artifacts/measurements/catalogue-20261025/bitmagnet-live-default-20261026/`.
Final SQL counts 687 newly searchable torrents / 52,924 files during 600.012
seconds: **68.70 torrents/minute**, 5,292.29 files/minute. Publication drain adds
132 torrents / 6,284 files, giving 819 / 59,208 overall. All received valid
metadata is published after drain: 820 SHA1-valid dictionaries, one rejected by
the common parser, 819 admitted/published. No storage error or publication after
the drain. Collection CPU is 40.0625 seconds; peak working set 49,430,528 bytes.
The final approximately five-minute window produces 574 publications in
298.312 seconds (115.45/minute), demonstrating a cold ramp rather than proving
long-term sustained target performance. Do not replace the full-window rate
with this warmer subwindow or count drain as active collection.

Paired baseline evidence is in
`artifacts/measurements/catalogue-20261025/libtorrent-live-default-20261026/`.
It publishes 642 new torrents / 53,741 files in 600.088 seconds: **64.19
torrents/minute**, 5,373.31 files/minute. Warmup contributes 82 torrents and
drain another two, giving 726 / 55,799 overall. Collection CPU is 64.75 seconds;
peak working set 51,089,408 bytes. Both arms used the same executable and exact
base fixture, with their documented distinct resource budgets. The translation
is about 7% faster by collection torrent count and uses about 38% less CPU,
but returns slightly fewer files. One sequential public-DHT pair cannot
establish a decisive algorithm advantage or the sustained competition target.
Neither full-window collection rate reaches 100/minute.

The completed warmed port evidence is in
`artifacts/measurements/catalogue-20261025/bitmagnet-live-warmed-20261026/`.
After 600 seconds of warmup, it publishes **1,533 torrents / 73,849 files** in
600.015 seconds: **153.30 torrents/minute**, 7,384.72 files/minute. The entire
twenty-minute active run publishes 2,830 / 136,575 (141.50 torrents/minute),
and the separate 75-second drain adds 125 / 2,766. All 2,955 SHA1-valid
dictionaries pass the common parser, archive and full search publication;
there is no storage error or publication after drain. Collection CPU is 73.625
seconds; peak working set is 57,208,832 bytes. This establishes a measured rate
inside the lower portion of the published 100–1,000/minute range on this run.
It does not prove 1,000/minute, an advantage over a warmed baseline, or production
adoption under the client's transfer-resource policy. The matching baseline
measurement below addresses the first comparison only.

Matching warmed baseline evidence is in
`artifacts/measurements/catalogue-20261025/libtorrent-live-warmed-20261026/`.
It publishes **816 torrents / 86,424 files** in 600.228 seconds: **81.57
torrents/minute**, 8,639.12 files/minute. Its active startup-plus-warmup run
publishes 1,630 / 153,665, with another 14 / 3,458 published in drain, giving
1,644 / 157,123 overall. Collection CPU is 92.78125 seconds; peak working set
53,227,520 bytes. No publication follows the drain. Thus our translation gives
about 88% more searchable torrents but about 15% fewer searchable files in this
warmed pair, with about 21% less collection CPU. Public cohorts and independent
worker budgets differ, so do not call that a causal or universal algorithm win.
It remains a comparison of OUR TWO implementations, not a run of upstream
Bitmagnet. All four benchmark processes have exited cleanly.

Baseline has a visible acquisition plateau around seconds 218–379, with retained
totals nearly unchanged while fetch slots remain occupied. This is separate
from an earlier large-file publication delay. Focused source diagnosis found a
concrete weakness: `Alerts.cpp:263` extends the 30-second acquisition deadline
from `torrent_status.total_download`, which includes all protocol traffic,
not only unique metadata progress. No absolute deadline bounds that extension.
This path can retain unproductive jobs; existing samples do not prove it caused
this plateau. Normal active-transfer queueing is not the cause of crawler
admission: Guard clears auto_managed and Acquire clears paused. Do not silently
change the baseline during this comparison or claim the suspected cause proven.

Acquisition evidence at collection end: 28,463 sampled hash observations,
20,155 selected candidates, 19,926 peer queries, 5,387 positive peer responses,
5,376 metadata jobs, 14,558 completed endpoint attempts, 3,082 established
connections, 1,167 metadata-capable peers and 820 received dictionaries.
11,476 endpoint failures occurred at Connect. Peak metadata jobs were 131 of
400, peak probing 200. Total limiter waiting was 9.26 seconds across completed
attempts. These are cumulative stage counters, not a fixed cohort; unfinished
attempts are absent and fetching includes received work waiting for persistence.

Focused independent source-fidelity follow-up (`acquisition_evidence_read`)
confirmed the examined pipeline's queues, deadlines, sequential peer attempts,
batching and pacing, with no new unintended defect established. The disclosed
BEP51 change can materially affect warmed supplier reuse: upstream
`internal/dhtcrawler/sample_infohashes.go:62` changes productive advertised
intervals above 300 seconds to 60; the native port honors the full interval.
Startup also differs: shared-session live contacts plus 128 persisted seed
contacts replace upstream's initially empty table/direct bootstrap ping path.
The 128 seeds do not cap subsequent routing-table growth. Shared libtorrent
direct requests have no ordinary traversal-admission or upload-quota send ceiling
in the inspected source. This is an adapted translation, not an execution of
unmodified upstream Bitmagnet.

Fresh official Ubuntu controls are in
`V:/temp/plan14-profile-20261025/controls-20261026/manifest.json` (SHA256
`3FDF04285EAC8CA7D4BFAA103D4BF2155AFD95BB9C56C564959B6CC2DC443FA1`).
All four HTTP metadata downloads succeeded, and root independently recomputed
raw-info SHA1/SHA256 hashes. Public peer metadata availability remains unverified.

Public peers complete the translated handshake/all-piece exchange, and normal
stop drains received batches, as the completed arm establishes. Both native arms
now have paired cold and warmed measurements. Runtime questions still requiring
evidence are actual upstream performance and ordinary-client policy integration.
No UI capture,
installer run, payload transfer test or broad regression suite is required for
those questions. No task-owned engine/build process was running before the build.

Alternative selection is an explicit `crawler.json` in the engine data folder:
`{"algorithm":"bitmagnet"}` selects original defaults; absence preserves the
existing crawler. Optional `dht_crawler`, `metainfo_requester` and `dht_server`
objects use the reference keys. `catalogue_status` returns the selected crawler,
resolved configuration, adapter differences and acquisition counters. This is
the comparison interface, not a new UI settings workflow. Original unused keys
remain identified as unused: pinned Bitmagnet ignores its reseed interval and
key-mutex setting; shared transport owns the DHT port, and common retention
preserves all files and exact info bytes instead of applying threshold/piece
discarding. Plain TCP4 cannot implement Required encryption, uTP-only or
IPv6-only policy; configured rate caps currently make the alternative unavailable
rather than bypassing them. Confirmed proxy/adapter changes cancel old work.

**Experimental comparison scope:** the reference arm retains Bitmagnet's
independent metadata worker/socket budget, not libtorrent's transfer connection
cap or start-rate setting. The baseline remains the normal product default;
there is no UI selection for the reference arm. Resolved configuration reports
both budgets. Compare actual defaults first and report resource differences;
that result alone cannot establish a causal algorithm advantage. Production
adoption/shared connection admission is a later decision informed by the result,
not an undisclosed change to normal transfer policy. Route privacy still applies.

The port preserves original worker/channel sizes, batching, endpoint deadlines,
per-IP pacing and the pinned BoomFilters stable filter. Known malformed-piece,
duplicate-piece, short-final-piece and extension-ID bugs are corrected. Other
documented adapters are libtorrent's shared DHT transactions, advertised BEP51
backoff, actor scheduling, SQLite/full retention and flushing received metadata
on stop. Every outbound transport still uses the confirmed route.

After consolidated fixes and focused follow-up, compile once and run the paired
measurement. Default observation is one-minute warmup plus ten-minute collection
per arm, with publication drain measured separately. Helpers retain exact binary,
configuration and input hashes and use publication watermarks/final SQL to count
new searchable identities and files. Existing historical controls are aged;
fresh healthy controls and a matched candidate cohort still need preparation.
No target or relative-speed claim is established by source parity. Neither a
peak minute nor a paper comparison proves the requested sustained speed.

**Owner decisions, settled:** match the competition's useful indexing rate;
Bitmagnet's published 100–1,000 torrents/minute is a reference target, not a
measurement on this machine. Pause further storage-budget and exclusion-rule
optimization. Finish the requested totals/rate presentation and collapsed
Settings summaries, then measure the acquisition correction. Root builds once
after source work and independent review; no development builds.

The owner's running Debug engine was queried read-only twice, 25 seconds apart:
148 torrents / 4,723 files stayed unchanged, while pending hashes rose from
60,063 to 60,997 and sampled observations from 17,973 to 18,933. Its CPU consumed
about 27.9 seconds during that interval. Metadata acquisition is stalled in that
observation; the UI is faithfully displaying engine totals. Previous matched
Release measurements establish substantial Debug overhead but do not establish
that overhead as the only cause of this stall.

Current work has one native implementer for acquisition policy/rate and root for
UI, translations, contracts and the sole build. The candidate permits 256 idle
jobs with two connections each, removes their idle bandwidth throttle, and
promptly yields excess jobs to the configured busy limit. The existing global
connection policy still applies. Preserve explicit saved concurrency settings,
the payload guard, normal transfers, ready-first selection and stall/progress
rules. Benchmark the same cohort before claiming an improvement; this remains
an unproven candidate, not a completed speed target.

UI completion means visible indexed torrent/file totals, a separately grouped
60-second rate of newly searchable torrents, and compact activity with detailed
probe/queue facts available in the tooltip. Indexing Settings uses the existing
`SettingsSection.Summary` path for Collection, Storage, Exclusion rules and
Maintenance, so the useful facts remain visible when collapsed. No new settings
or navigation are needed. All shipped languages receive the new text.

Expected remaining work: one source/review/build round and a matched acquisition
measurement, roughly 30–60 minutes; a failed throughput hypothesis requires
another evidence-led correction, not relabelling the goal complete. Material
risks are connection pressure, useful-job cancellation on a busy transition and
publication-rate accuracy. Verify those paths in source before the build, then
use the final optimized output for the throughput question. Capture only if
source review leaves a concrete fit question, and protect the owner's live app.

Source review completed for the candidate and presentation. The native reviewer
traced acquisition/cancellation/recovery, saved settings, per-handle policy,
capacity after async selection and publication/rebuild counting with no finding.
The UI reviewer found selection disappearing during background work; selection
now stays with catalogue totals, independently of the shown-match group. Focused
follow-up is clean. Catalogue integrity exposed duplicate notification keys
already present in the dirty resource files, which would throw in `Strings.Read`;
only identical duplicate copies were removed. All six JSON catalogues pass
duplicate-key and changed-placeholder validation. No capture is required to
reconfirm the shared collapsed-summary control. Final combined Release build is
running; a separate live capture instance temporarily owns the pipe and must
finish before the isolated acquisition run.

The combined Release build passed at
`artifacts/bin/TinyTorrent/release_win-x64`. A separate capture interrupted the
first acquisition attempt at 67 seconds; it is not an engine-crash finding or
a completed benchmark. Subsequent measurements hold the existing capture lock.
The completed 256-job / global-200 run published 226 new torrents and 11,373
selectable files in 303.254 seconds: 44.7 torrents/minute, peak rolling minute
71, 40.44 CPU seconds and 42.9 MB peak working set. Exit was clean. Random cohort
publication was 3/128 and official controls 4/6; these hashes have aged since the
earlier runs, so conversion is not a controlled comparison across that interval.
The existing-binary global-800 arm was slower and supplies no evidence for
raising production connection defaults. The target remains unmet.

**Next candidate:** follow Bitmagnet's positive-peer gate. `Due` admits only
hashes with peer endpoints, and disables automatic DHT on those metadata
handles. Cold hashes remain in the existing durable `Probe` path. Idle probes
allow 200 leases and 20 starts per tick; busy limits stay 8 and 2. Preserve valid
late peer responses even after a probe lease expires. Keep the source-node to
normal-DHT fallback and metadata deadline unchanged in this comparison. An
application probe deadline does not cancel libtorrent's underlying traversal;
the measured outcome must justify the wider probe allowance. No new session,
requester, setting or storage mechanism is introduced. The native implementer
owns `Catalogue.cpp` and `Indexing.cpp`; root owns documentation, integration
and the sole post-review combined build. Estimate one further round, 20–35
minutes. Publish new searchable torrents/files, resource cost and the difference
from the measured 44.7/minute baseline; idle metadata slots are acceptable only
if useful throughput benefits.

The peer-gated candidate is source-complete and frozen for independent delta
review. Its async probe callback also checks the current busy policy before
starting work selected while idle. The wider allowance counts new hash probes,
not individual UDP requests. The completed global-800 run actually published
124 new torrents / 2,966 files in 304.985 seconds (24.4/minute), with 29.73 CPU
seconds, 41.3 MB peak working set and clean Exit. Both completed profile arms
are archived, including publication records and fixed manifest; full results
are in [the acquisition report](14-acquisition-diagnosis.md#follow-up-acquisition-profiles).

Independent delta review caught crawler-only DHT flags leaking through live
handle adoption into Preview and Add. The acquisition-to-preview handover now
restores ordinary DHT/LSD, receive and connection/PEX policy while retaining
the payload guard. This is the existing handover path, with no second download
workflow. The three-file correction is frozen for the same reviewer before
the next build.

The peer-gated build passed and its full measurement completed with clean Exit:
292 newly published torrents / 17,753 selectable files in 305.118 seconds,
57.4/minute, peak rolling minute 86, 99.02 CPU seconds and 49.4 MB peak working
set. The old sample published 30/128 and controls 2/6. Evidence is archived under
`artifacts/measurements/catalogue-20261025/peer-gated-unbatched`.
The speed target remains unmet.

This run exposed a concrete queue-pressure defect: status reads at 41.74 and
124.43 seconds returned Success with zero totals and no error, then recovered.
`Store` rejects a job above 64 queued jobs with `Overloaded` and an empty
detail; the status adapter converted that failure into an empty successful
status. A failed read must return the existing typed pipe failure and preserve
the UI's previous state. Individual peer-hint writes and four per-hash failure
paths can each enqueue a job per response/expiry; 200 probe expirations can
overflow the worker in one burst.

**Next correction:** coalesce peer hints and retry updates at the existing
engine owner, replacing the old announcements buffer rather than adding a
parallel implementation. Raw incoming-announcement hashes remain in discovery;
ordinary peer replies only update existing pending records. The existing
maintenance job persists batched failures before fresh hints using prepared
updates and transactions. Keep useful updates if scheduling itself refuses the
batch. Root owns docs/build; native implementer owns State, Alerts, Additions,
Indexing and Catalogue header/source. This adds one correction/review/build and
matched measurement round, about 20–30 minutes. Verify that normal probing no
longer produces false-zero successful status or drops productive archive work;
do not raise the worker queue cap to conceal the pressure.

The six-file batching correction is source-complete. All three peer-response
paths use one injection/coalescing owner; all four timeout/add/recovery failure
paths accumulate retry updates. Maintenance drains one bounded peer map and the
discovery/failure buffers. Only refused scheduling restores the batch, with newer
peer hints winning; SQL failures are not replayed because earlier sub-batches
can already have committed. The status adapter returns the existing typed
failure. Three implementer source passes are complete; the independent reviewer
is tracing the frozen correction before the combined Release build. The remaining
runtime questions are useful throughput, resource use and whether normal probing
still overloads catalogue work; collapsed Settings summaries need no new capture.

Independent delta review is clean: all six frozen file hashes match, ordinary
peer/failure updates cannot resurrect accepted or rejected hashes, refused work
restores only under the current version/enabled/lifetime gates, and the window
preserves last totals on typed status failure. The sole combined Release build
has started for the matched public-network throughput and overload questions.

The combined Release build passed at `artifacts/bin/TinyTorrent/release_win-x64`.
The batched-frontier measurement is running on the same disposable base,
256 idle acquisitions, global connection cap 200 and normal live discovery,
holding the shared capture lock. No owner catalogue/settings are changed.

The batched run finished with clean Exit: 287 new published torrents / 23,393
selectable files in 303.468 seconds, 56.7/minute overall, peak rolling minute
106, 57.05 CPU seconds and 48.1 MB peak working set. No status read failed or
returned false-empty success. This fixes the demonstrated queue/status defect
and lowers observed CPU cost, but does not establish better average throughput.
Evidence is archived in `peer-gated-batched`. A five-minute continuation using
the same binary and a copy of this disposable catalogue now measures sustained
throughput from its 1,026 ready hashes, separately from the cold-start average.
This adds about five minutes, no implementation/build round. A peak alone does
not satisfy the speed goal.

The warmed continuation also completed cleanly without status errors: 266 new
published torrents / 7,569 files in 308.629 seconds, 51.7/minute, peak minute 83,
76.28 CPU seconds and 49.5 MB peak working set. Ready hashes rose from 1,026 to
1,897 with acquisitions mostly full. Startup ramp alone does not explain the
remaining shortfall. Evidence is in `peer-gated-batched-warm`.

**Current correction:** follow Bitmagnet's single supplying-node peer lookup.
A sampled hash whose supplying node returns no peers or times out remains
durable and receives existing retry backoff; it no longer launches a recursive
libtorrent traversal immediately. Source-less hashes, including controls, keep
ordinary peer discovery. Remove the now-unneeded probe traversal state, retain
valid late positive replies and leave metadata limits, transport, deadline,
retry schema and session ownership unchanged. The current fallback starts
traversals at both negative responses and supplier deadlines, and an application
deadline does not cancel them. Lower shared UDP/work pressure is a hypothesis,
not a diagnosed router fault. Implementer owns only `State.h` and `Indexing.cpp`;
root owns docs/build/measurement. One focused review/build and same-manifest
comparison, about 15–25 minutes. Defer the more complex nonprovider peer plugin
until this smaller change has a measured result.

Supplier-only source work and three focused passes are complete. Independent
delta review is clean; frozen `State.h` and `Indexing.cpp` hashes match. Negative
direct responses and deadlines enter the existing batched retry path, source-less
lookups and late ordinary positives remain intact, and no acquisition/payload
or transfer handover path changed. Root's combined Release build has started;
the runtime question is published throughput/resource use on the same fresh
fixture, with no other algorithm change in this arm.

The supplier-only combined Release build passed. The matched fresh-process run
is active on the original fixture with the same 256/200 job/connection settings.

Supplier-only completed cleanly: 261 new published torrents / 15,517 files in
303.780 seconds, 51.6/minute, peak minute 72, 15.06 CPU seconds and 36.9 MB peak
working set. No status errors/false-zero responses. The ready queue stayed zero
and metadata capacity was often unused. Evidence is in
`supplier-only-15s-start20`. Its major measured benefit is resource cost, not speed.

**Next batch:** supplier-diverse admission before leasing, up to 50 starts per
idle tick, existing 200 concurrent probes, four-second direct supplier deadline
and unchanged 15-second source-less deadline. Bitmagnet paces all outbound DHT
queries per IP (one/second, burst four) and uses a four-second query deadline;
our current same-supplier batches can trip remote/local libtorrent DOS limits.
Select at most one due hash per supplier IP per tick, preserving excess hashes'
eligibility. Do not implement a general rate controller or peer plugin. Catalogue
selection must avoid full-backlog grouping/scans at scale; settle its bounded
indexed selection before implementation. Root owns docs/build/evidence; native
implementer owns Catalogue header/source, State and Indexing. This adds a focused
source/review/build/measurement round, about 20–35 minutes. Underlying direct
RPC observers can live 15 seconds after a four-second application deadline,
but do not retransmit; acknowledge late-response loss rather than claim cancellation.

Selection is settled before edits: one covering pending index and an in-memory
supplier-address cursor at the catalogue owner. Bounded index range seeks skip
an entire IP's hashes, then retrieve at most its first due hash. Visit at most
200 IPs per call, wrap once, and never choose one IP twice in that call. A
separate bounded source-less range shares capacity and borrows unused slots;
alternate its priority only for a one-slot call so neither lane starves. Lease
only the selected hashes. No full-backlog GROUP BY/window pass, supplier table,
token controller or new public interface. Index-only schema revision 4 preserves
archive data. With many distinct future-only suppliers, bounded visitation trades
immediate selection for eventual round-robin service; do not claim exhaustive
selection. Native implementation owns the four files previously listed.

The supplier-diverse correction actually needed edits only in Catalogue and
Indexing sources, with no public interface/header additions. Three focused
passes and independent delta review are clean. Packaged SQLite 3.53.4 EXPLAIN
shows all three query shapes using `SEARCH ... USING COVERING INDEX pending_probe`
with the expression/source/eligibility bounds; none scans or uses a temporary
sort. Cursor and lane preference advance only after transaction commit. The
combined Release build is running for the matched throughput/resource question.

That combined Release build passed. The fresh-fixture run is active; its initial
status retained the baseline seven torrents / 51 files after the additive schema
revision. No routine test suite or UI capture was added.

Supplier-diverse completed cleanly: 377 newly published torrents / 26,444 files
in 304.675 seconds, 74.2/minute, peak minute 99, 23.95 CPU seconds and 42.2 MB
peak working set. No status errors or false-empty samples. Evidence is in
`supplier-diverse-4s-start50`. Metadata filled its capacity and ready hashes grew
to 534, so supply is no longer the demonstrated empty-queue constraint. The
reference target remains unmet. One existing-binary global-800 arm now tests the
remaining connection-budget question with this peer-positive/paced algorithm;
the old global-800 arm used cold fallback and does not settle this changed mix.
No product default or owner setting changes. This adds five minutes, no build.

The global-800 arm completed cleanly: 359 new published torrents / 32,077 files
in 304.380 seconds, 70.8/minute, peak minute 117, 23.66 CPU seconds and 41.4 MB
peak working set. No status errors or false-empty samples. Evidence is in
`supplier-diverse-global800`. It supplies no reason to raise shared defaults.

**Current batch:** install a crawler-only libtorrent peer policy through each
acquisition's `params.extensions`, not the session. Release peers lacking BEP 10,
enabled `ut_metadata`, or positive advertised `metadata_size`; these cannot
currently serve metadata. Keep libtorrent's requester/validation, transport,
whole-hash deadline and shared limits. Normal disconnect severity suffices:
libtorrent records `last_connected` and enforces its existing reconnect delay,
so an incapable endpoint does not immediately occupy the slot again. Do not
copy Bitmagnet's arbitrary metadata-size ceiling.

One shared atomic flag isolates the actor/network-thread policy and disables
it before the existing acquisition-to-Preview handover. Crawler handles pending
removal remain crawler-owned; no retirement machinery is needed at every
teardown branch. No State pointer crosses into the plugin. Implementer owns only
State, Indexing and Previews; root owns docs/build/measurement. Three focused
passes and the same independent reviewer precede the single combined Release
build and original-fixture global-200 comparison. Estimate 20–30 minutes.

The three-file peer filter is source-complete. Three focused passes and the
independent behavioral review are clean; final follow-up also checked the
`crawlerOwned` name and BEP 10 wire-bit comment against the corrected hashes.
No teardown registry or extra retirement branches were added. The public
libtorrent hook/normal-disconnect/reconnect paths were verified against pinned
source. Root's combined Release build has started for the original-fixture
global-200 throughput comparison; no runtime improvement is claimed yet.

The peer-policy combined Release build passed. The original-fixture global-200
comparison is running on that output, holding the shared capture lock.

The peer-policy run completed cleanly: 369 new published torrents / 24,360 files
in 305.349 seconds, 72.5/minute, peak minute 116, 25.23 CPU seconds and 41.9 MB
peak working set. No status errors/false-empty samples. Old sample publication
was 5/128 and controls 5/6; public-network variability prevents attributing the
control improvement to the policy alone. Evidence is in `metadata-peer-filter`.
There is no demonstrated overall throughput gain over the 74.2/minute paced
baseline. The target remains open; do not call a peak minute completion.

Inspection of final pending leases in the paced baseline and peer-filter run
found no old unfailed, peer-empty leases past their 120-second eligibility.
This supplies no evidence that indefinitely extended acquisitions dominate these
runs; do not add a hard acquisition lifetime on that hypothesis. Ready work still
accumulates with metadata full. The next narrow question is endpoint breadth:
one connection per acquisition (as in Bitmagnet's sequential endpoint loop)
versus the current two competing for the shared cap. No change to that policy
has been made. Avoid another blind increase in global connections or a new
requester. All processes/checks from these completed runs have exited.

Latest owner UI steer is complete: all four Indexing expanders retain shared
collapsed summaries; localized totals/rate/compact footer are reviewed and in
the current Release output. The speed goal remains separate and active. Keep
the small original fixture/manifest/helper until that goal closes; completed
run databases can be removed after their archived evidence is checked.

Completed run databases have been removed after archiving publication records,
samples and summaries. Process inspection confirms no measurement engine remains.

The next goal turn revalidated the reviewed native hashes and successful Release
manifest before starting an existing-binary TCP-only comparison, idle 256/global
200, on the same original fixture. Only the disposable engine's transport changes;
the owner's saved settings and production default remain unchanged. The question
is whether current acquisition scheduling benefits from avoiding uTP-first
connections; the earlier TCP result used an older 64-job pipeline. This costs one
five-minute measurement, with no compilation. The native implementer concurrently
checks connection allocation and endpoint breadth read-only; no product edits
occur during the run. Previous goal work completed the UI request and supplied
measurement evidence, but did not reach the throughput objective.

TCP completed with 300 new torrents / 9,705 files in 302.966 seconds,
59.4/minute, peak minute 85, 17.02 CPU seconds and 40.6 MB peak working set.
Exit and catalogue status were clean. This does not support changing transport.
The next batch is one per-hash connection, keeping the peer filter and all other
policies fixed. One implementer owns the two native files; root updates contracts
and builds after focused source review. Budget: one small review/build round and
one five-minute acquisition comparison, approximately 15 minutes. Accept a useful
rate improvement, otherwise remove the candidate; reaching 100/minute remains
unproven, and a single peak minute cannot satisfy that target.

The one-connection delta is frozen and independently reviewed clean. Its only
native changes are one `Acquisition::connections` constant and the creation and
policy-refresh callers. Preview adoption still restores ordinary user limits;
busy bandwidth and the payload guard are unchanged. Source hashes:
`State.h` = `89894e4d8edf5a9b3d8a9640cb5a2b355ffbf34a`,
`Indexing.cpp` = `68eadcca77bfff6e72c9d010de3b4b97135b88df`.
Root started the sole optimized engine build for the remaining runtime question:
does this increase newly searchable torrent/file output on the same fixture?

That Release engine build passed, then the one-peer arm published 338 new
torrents / 20,160 selectable files in 304.609 seconds: 66.6/minute, peak minute
99, 21.69 CPU seconds and 41.8 MB peak working set. Exit and status were clean;
old cohort conversion was 5/128 and controls 5/6. Evidence is archived under
`artifacts/measurements/catalogue-20261025/metadata-one-peer`. The experiment
did not improve useful output, so its three native lines and contract change
were reverted. Original reviewed hashes are restored exactly; the earlier
combined Release application still matches them. The newer engine-only output
is an experimental one-peer binary and must not be launched as current source.
This round cost two five-minute measurements and one 39-second build, within
the stated round estimate. It rules out these changes as supported improvements;
it neither establishes the dominant current wait nor reaches the goal.
The original fixture and helper remain for the active goal; the owner catalogue,
other running apps and capture instances were not removed. A lead correction:
Bitmagnet's outbound-query limiter should have been traced before the broad
concurrency trials; discovering it later added avoidable measurement rounds.

## Earlier execution record

Settled owner requests, in order:

1. Show discovery progress directly on Search, separating displayed rows from
   indexed torrent/file totals and active/queued metadata work.
2. Work on main. Completed: main fast-forwarded to the existing two feature
   commits and checkout switched without changing uncommitted work.
3. Make idle/busy metadata concurrency configurable in Indexing Settings.
4. Replace the caption's binary workspace switch with a native ComboBox for
   Torrents, Library and Search, preserving navigation guards and shortcuts.
   **Owner correction:** use a native dropdown button and explicit menu choices;
   changing a field selection must not drive workspace navigation.
5. Diagnose slow collection against the million-entry goal without claiming a
   screenshot's row count measures throughput.
6. Restore removal previews to the compact approved prototype and established
   native dialogs: one summary, affected entries, meaningful paging, no empty
   table or repeated scope counts. Apply Impeccable layout against existing UI.
7. Require confirmation before rebuilding saved search projections.
8. Align Indexing Settings controls vertically using established Settings widths.
9. Search must remain responsive at one million torrents / ten million files;
   bound displayed results and optimize candidate queries and status reads.
10. Sort the whole matching search set before returning the first X rows.
11. Research the fastest practical crawling strategy, including an independent
    Astra opinion. **Owner correction:** broad hash capture, peer probing and
    short attempts are ideas to evaluate, not a prescribed pipeline. Optimize
    useful indexed metadata throughput; do not inflate a hash count as proof.

Root owns app UI, translations, integration and the sole final build. Native
implementer owns catalogue/crawler/query and typed wire contracts. TableView
implementer owns explicit external sorting. Two research agents, including
Astra, own their reports. Expanded search/crawler work adds a native batch and
focused review before the integrated final checks; query semantics, migrations
and scheduling are the material risks. Compile after implementation and review.

Implemented and reviewed app corrections: Search footer includes
indexed totals and active/queued retrieval; native three-workspace dropdown;
idle/busy fetch settings (defaults 32/2, range 1–64); shared confirmation before
Rebuild/Repair; compact removal preview without an empty fixed table; aligned
Settings editor/unit slots and action widths. Translations updated in six
catalogues. Search requests debounce for 150 ms, coalesce behind one in-flight
query, and retain a hard 1,000-row display bound. TableView external sort mode
leaves row order to the database. Combined compilation and focused native
capture passed; the live-crawl shutdown issue below keeps the batch open.

Confirmed source findings: current footer uses Rows.Count; status already
supplies indexed torrent/file totals, acquisitions and pending hashes. Existing
five-second visible-page polling and footer tooltip/accessibility owners can
publish these without a new transport. Metadata concurrency is fixed at 4/1;
Due clamps batches at 16. Projection processes one torrent per one-second tick,
even for tiny torrents. Node storage caps at 2048 and can reject fresh candidates
while every retained node cools down; preserve requested BEP 51 backoff.

Verification: review paths and ownership first; catalogue integrity for changed
translations. Runtime checks must answer a remaining question about the native
selector, preview fitting or changed crawler scheduling; reuse existing checks
and final shared output, with no per-edit compilation.

Native implementation is source-complete. It preserves sampled-node affinity
and announce peers, overlaps bounded probes with metadata acquisition, keeps
ordinary libtorrent fallback when ready peers are scarce, batches node writes,
removes arbitrary pending-hash truncation and publishes multiple small entries
per projection slice. Status totals are maintained transactionally. Search adds
trigram candidates, ordered indexes/keysets and bounded resumable file groups;
schema migration preserves the archive. These mechanisms are not a measured
claim of maximum throughput. The packaged SQLite 3.53.4 runtime accepted an
in-memory FTS5 trigram table without compilation or user-data access.

The six source reviews completed. Corrections verified: torrent-target
title/total-size matching, preserving bounded search continuation while crawl
totals grow, equal translated action widths, and removal of redundant/unused
indexes. Packaged SQLite EXPLAIN exposed candidate expansion choosing the whole
root index and rank keysets rescanning equal-value cohorts. Forced candidate
join order and sequential equal-value/range seeks correct these without extra
schema state. A one-million-torrent, zero-file in-memory SQL fixture measured
the deep continuation at about 1.4 ms instead of 70–120 ms; this is not a
full-product or ten-million-file benchmark.
Six translated catalogues passed JSON, duplicate-key and changed-placeholder
integrity checks.

SQL corrections passed the independent six-pass focused delta. The final narrow
scheduler correction removes the unmeasured quarter-cap on ordinary fallback:
fill configured capacity ready-first, then ordinary eligible work. Astra's second
opinion favors this simpler baseline over idle slots or new preemption machinery.
Count existing pending admissions against outstanding work so slow storage does
not accumulate queued metadata. The independent scheduler delta is clean,
including shared later-peer delivery to existing acquisitions. The first final
combined capture build passed. Use
`artifacts/lanes/2/bin/TinyTorrent/debug_win-x64_capture` for all final checks;
the matching capture launcher uses `-ArtifactsPath artifacts/lanes/2`.
CatalogueChecks passed in 13.18 seconds, including historical migration and
global search ordering; small-fixture first query was 7.34 ms. Native catalogue
capture passed in both scenes. Inspected compact empty/populated removal
dialogs and aligned Settings; fixtures closed without applying removals/rules.

Live verification is not yet passing. A first 150-second fresh run stayed at
zero nodes. Independent UDP pings and the engine's own ping/find_node proved
working routing in later short probes. The unchanged binary then learned nodes
at 30 seconds and published metadata by 50 seconds: 3 torrents / 14 files at
90 seconds, unchanged at 140, while 16 acquisitions and 584 ready candidates
remained. That run returned a nonzero shutdown code, hidden by the check's
generic final-save message; its temporary store was removed before diagnosis.
No source fix or network attribution is justified yet. A bounded 70-second
reproduction now preserves the exit code and log on failure. Native owner
traced the meanings: 1 is abrupt save/teardown, 2 is watchdog, other values may
be crashes. The plan remains open until this concrete failure is resolved or
identified precisely. Do not rebuild unchanged source.

The 70-second repeat exited cleanly (3 torrents / 34 files). The original
150-second scenario then reproduced ExitCode 2: the desktop watchdog. Its log
reached `saved` about two seconds after Exit, so final-save failure is not the
diagnosis. Preserved store:
`V:/temp/plan14-live-473f1e09-1303-4481-9190-50fe747ebdcc`.
Read-only inspection found consistent totals, no journal and the last database
write at Exit request. The actor may be waiting for workers or blocked in
libtorrent teardown. Existing output cannot distinguish those; the refined
installed-SDK filename search found no CDB. The compile exception's question was
stated to the owner before temporary `[DEBUG-plan14-exit]` phase markers were
authorized in the existing shutdown log. Native owner edits only those traces;
independent reviewer checks them before one engine-only diagnostic build.
Remove the markers with the eventual correction and review affected paths.

The diagnostic run isolated the watchdog to teardown: at eight seconds all
workers were idle, the log had flushed, and the engine reached `Finish()` before
the watchdog fired during `session.reset()`. Changing the exit code would mask
the existing lifetime requirement and is rejected. Stop DHT asynchronously at
shutdown entry so crawl cancellation overlaps checkpointing; retain the same
save guards and ten-second watchdog. Native owner implements; independent paths
review checks the change before compilation.

Useful throughput remains unproven: fresh 150-second runs returned between zero
and eight validated torrents despite a ready backlog and sixteen acquisitions.
The next diagnostic question is where occupied slots spend their time: lifecycle
transitions, connecting, connected without receiving, or receiving without
metadata completion. Existing state-update alerts supply this evidence without
new requests. Collect compact temporary acquisition traces in the same narrow
diagnostic build, then remove them before the final matching app/engine build.
Do not raise limits or add scheduling machinery without that evidence.

The compact trace established two source defects: expiration could beat a queued
receive observation (request 48 gained over 22 KiB before removal), and late DHT
peer hints used `connect_peer` defaults falsely advertising uTP/hole-punching.
Native owner corrected expiration through one outstanding asynchronous fresh
status request, including dropped-alert recovery, and matched libtorrent's own
DHT peer-source/empty-capability semantics. Independent paths review is clean.

The same diagnostic binary at 32 idle slots reached 16 torrents / 2,097 files at
140 seconds, versus 3 / 30 in the adjacent 16-slot run. Earlier 16-slot runs
varied from zero to eight torrents. This supports a 32-slot initial default on
this connection, not a universal speed claim; busy remains two and existing saved
settings retain their values. TCP-only at sixteen slots exited cleanly; both
transport runs hit the watchdog after saving. Earlier DHT cancellation reduced
save latency but did not alone resolve the join. The corrected peer flags must
now be checked with ordinary both-transport policy. Remove temporary traces,
review the final delta and make one matching app/engine build for that check.

## Current resumption point

**Owner priority correction:** defer the ten-second Exit investigation. Deliver
high-value indexing throughput first on the owner's 400 Mbit connection. The
measured 3–7 torrents/minute is inadequate; a busy crawler is not success.
Root and the retained native implementer/Astra researcher now investigate
acquisition throughput. Source is frozen until a causal change is established.
The saved shutdown dump permits later offline work without new live reruns.

The final combined build passed at
`artifacts/bin/TinyTorrent/debug_win-x64_capture`. Earlier CatalogueChecks and
native bilingual captures remain valid: the later delta changes only crawler
expiry, peer hints, its idle default and shutdown. Do not rerun unrelated checks.
All source reviews are clean, and no temporary diagnostics remain in production.

Normal both-transport crawling on this final build published seven torrents /
51 files by 140 seconds but still exited with watchdog code 2. Log: requested
20:04:34, checkpoint/saved 20:04:43. Its disposable store is
`V:/temp/plan14-live-79b066b7-d07a-484c-84d0-b54e717c2622`.
Earlier TCP-only on the diagnostic binary exited cleanly, but this alone does
not establish the final join's cause. Do not relabel the watchdog outcome.

Native source is frozen. Root is capturing only the next disposable engine's
shutdown thread stacks through the existing Windows DbgEng runtime, without
installing a debugger or compiling. This distinguishes final-document I/O,
the synchronous `session->is_paused()` call and final session teardown. Scripts:
`V:/temp/plan14-stack.py` and `V:/temp/plan14-live-crawl.ps1 -ExitStacks`.
Stack output: `V:/temp/plan14-shutdown-stacks.log`; saved minidump:
`V:/temp/plan14-shutdown.dmp`. It confirms owner thread in session destruction
joining the libtorrent network thread, sampled in its undead-peer cleanup tick.
Storage workers were idle. Further Exit work is deferred by the owner.
Clean owned processes and disposable stores when finished. Exit remains a
recorded deferred issue; it does not block the owner's acquisition priority.
Full ten-million-file performance and a fastest crawling profile remain unmeasured.

## Acquisition conversion investigation

Settled owner direction: keep DHT and the existing architecture. Compare useful
indexed torrents/minute, searchable files/minute and resource cost. Wait for the
requested analyses before selecting or combining algorithm changes. A growing
hash queue establishes discovery activity, not metadata acquisition success.

Two new read-only agents own independent reports: `acquisition_bottleneck`
traces the seven-stage cohort and missing instrumentation in
`14-acquisition-diagnosis.md`; `bitmagnet_acquisition` audits current primary
sources and the reported throughput range in `14-bitmagnet-acquisition.md`.
Root owns integration and any eventual combined build. Expected first round:
source analysis, then a decision about the smallest measurement; no production
algorithm changes or compilation during that round.

Confirmed against pinned libtorrent source: `torrent_peer.cpp` initializes
`supports_utp` true; `torrent.cpp` chooses uTP when policy permits it and
`peer_connection.cpp` falls back to TCP after failure. Empty DHT hint capability
flags therefore do not establish TCP-first behavior. Whether this delay explains
our low conversion rate remains a hypothesis requiring a matched workload.
The pinned uTP stream fails an unconfirmed endpoint at its first timeout,
initialized from `utp_connect_timeout` (3,000 ms by default). The separate
15-second `peer_connect_timeout` does not establish a fixed 15-second uTP
fallback delay; measure the actual connection events.

Both read-only reports are complete. Bitmagnet's inspected factory actually
permits 200 peer-lookup and 400 metadata jobs at default scaling, each buffering
100 entries; the earlier 100/100 interpretation confused buffer capacity with
concurrency and is corrected in both reference reports. Its metadata jobs are
peer-positive, direct TCP and sequential endpoint attempts (3-second dial,
6-second whole endpoint deadline). These are comparisons, not chosen settings.

The diagnostic compile exception answers one blocked question: where does the
cohort lose throughput between peers, transport, protocol, metadata and storage?
Existing output cannot expose those stages. `acquisition_bottleneck` owns one
temporary opt-in `TT_CATALOGUE_TRACE=1` source batch in State and existing
Indexing/Alerts/Session/Additions/Previews owners. It adds filtered libtorrent
events and admission outcomes, with no algorithm change. Root owns the cloned
128-hash sample, public controls, harness and sole diagnostic build after review.
`bitmagnet_acquisition` prepares six official candidate controls, then reviews
the stable diagnostic delta independently. Official provenance does not establish
current swarm health; acquire only their hashes through the real guarded path.

Diagnostic source review is clean after correcting handshake completion to the
exact incoming `connection ready` event. Native peer events copy their data on
the actor; admission data returns through the existing worker completion. The
opt-in log expands its normal 32-line buffer/1-MiB rotation to 16,384 lines/64 MiB;
monotonic sequence gaps and dropped alerts invalidate exact stage counts.
Existing DHT counters add load/observer evidence without new requests or tuning.
The 8,000-byte DHT quota gates incoming requests, not outgoing lookup sends
(`dht_tracker.cpp` explicitly permits those), so it is not a proven lookup cap.

Root prepared `V:/temp/plan14-cohort-20261025/base.db` and `manifest.json`:
128 seed-20261025 random hashes from 7,821 pending rows, plus six official Ubuntu
hash-only controls. Baseline contains seven retained torrents / 51 selectable
files. Normal discovery continues; report cohort conversion separately from
unrelated growth. Harness and analyzer are `V:/temp/plan14-cohort.ps1` and
`V:/temp/plan14-cohort-report.py`. Same reviewer checked the source corrections
and harness contracts; one engine-only diagnostic build is now justified.

The Debug diagnostic build passed. Its completed five-minute cohort run retained
ten new torrents / 181 selectable files overall. Of 128 sampled hashes, 124 were
selected, 92 produced a peer response, 45 showed transport establishment, 22
advertised metadata support and five were retained. All six official controls
found peers; two were retained. No trace sequence gaps or dropped alerts occurred.
Initial hinted-peer attempts can precede the add alert and escape handle
association: connection-attempt counts are lower bounds, not an exact funnel.
Timeout records request removal; three attempts delivered valid metadata just
afterward and were salvaged. Count those as successes, not terminal failures.

Debug add completion took a median roughly five seconds while observed alert
consumption took about one millisecond. Session replies arrived in bursts. This
locates backlog upstream of the actor, not in archive admission. The owner was
told that Debug cannot establish production throughput; choosing it for the
performance comparison was a lead error. The same reviewed source then compiled
in Release, using the existing prebuilt dependency. An identical five-minute
Release cohort is running before any algorithm change. No source inputs changed
between builds. Early Release add completion is about four milliseconds; final
conversion and resource comparisons remain pending.

## Paused by owner

Owner requested a pause during the 64-slot comparison. Root gracefully stopped
only that disposable engine and preserved its log/database; treat the run as
interrupted, not a completed five-minute benchmark. The owner subsequently
resumed the investigation.

Completed Release-32 baseline:
`V:/temp/plan14-cohort-20261025/run-both-82d13953-13b4-40d7-b603-a93884951999`.
At 298.18 seconds: 60 new torrents / 955 selectable files overall, 43.30 CPU
seconds and about 37.8 MB peak working set. All six controls succeeded; sampled
cohort retained 19 of 124 selected hashes, versus five in Debug. Trace sequences
are complete and no alerts dropped. Initial hinted connection-attempt counts
remain lower bounds. Algorithm and limits were unchanged between build modes.

Interrupted Release-64 evidence:
`V:/temp/plan14-cohort-20261025/run-both-8863740a-378b-46fd-b6cd-294c2245ed1c`.
The completed analyzer output exists for Debug and Release-32. Temporary
diagnostics remain opt-in in source pending the acquisition investigation;
no production algorithm has been selected or changed. Both agent reports are
complete. On resume, assess the interrupted evidence before rerunning anything.
Release diagnostic binary:
`artifacts/bin/Engine/Release/TinyTorrent.exe`. No engine remains running from
this measurement; the owner's data and settings were not used.

## Resumed acquisition comparison

The interrupted 64-slot trace is complete through its explicit collection stop:
26,822 records, no sequence gaps or dropped alerts, 92 validated and retained
new torrents. Its last 246.27-second status shows 7,215 new selectable files;
large individual torrents make this file rate particularly variable. The fixed
sample retained 18 of 128 selected hashes; five of six controls succeeded.
At the same 246.27-second trace window, the 32-slot run had 47 validations versus
92 at 64 slots. These sequential public-network observations demonstrate useful
overlap, not a reproducible twofold speedup or better candidate conversion.

Independent source/evidence reviewer `acquisition_next_step` confirmed that over
90% of starts already have peer hints. Hinted success remains approximately
17–18%; storage is not the demonstrated bottleneck. Keep the existing ceiling
and deadline: raising width further approaches the shared connection budget,
and healthy controls sometimes begin receiving metadata near the stall deadline.
The busy policy reduces existing handles' resource limits, but does not
immediately shrink their count to the busy setting; higher idle widths need
transfer-priority evidence before adoption.

The existing-binary TCP-only/64 comparison completed: 94 new torrents / 5,215
retained selectable files in its last 296.14-second status, with 39.67 CPU seconds
and 38.2 MB peak working set. It converted 14 sampled hashes and five controls.
At 246.27 seconds it had 72 validations versus Both-64's 92. This supplies no
evidence for changing the shared production transport. Its trace also has no
sequence gaps or dropped alerts; Exit completed cleanly. Final worker-drained
published counts, stage counts, latency, resources and interpretation are in
[the acquisition report](14-acquisition-diagnosis.md#measured-result).

`acquisition_next_step` removed the temporary cohort instrumentation in State,
Indexing, Alerts, Session, Additions, Previews and Log. Production scheduling,
limits and admission stay intact. Independent `acquisition_cleanup_review`
completed correctness, ownership and fresh-read passes with no findings.
Normal logging and alert categories are restored; no temporary debug mechanism
ships. Root's final combined Release build passed after that review:
`artifacts/bin/TinyTorrent/release_win-x64`. The
first sandbox attempt could not open the build lock; the same authorized build
was retried with filesystem access, without changing source or toolchain.
This is one cleanup/review/build round, estimated 15–25 minutes. No routine
suite or repeat UI capture is justified by diagnostic-only removal.

The independent cleanup review's seven content hashes still match the final
source after compilation. `git diff --check` passes. Raw cohort evidence and
the manifest are retained under `artifacts/measurements/catalogue-20261025/`;
the disposable catalogue databases are removed after analysis. No measurement
engine or build remains running. This continuation used one five-minute
existing-binary comparison and one final combined build; it made no production
algorithm/default change. Use Release for performance assessment: the demonstrated
Debug network-thread overhead invalidates Debug as a production speed baseline.
The earlier Debug measurement was a lead mistake, corrected by matched Release
measurements. Deferred Exit refinements and full ten-million-file/controlled
active-transfer measurements remain explicitly unverified, rather than silently
added as mandatory tests or claimed complete.
