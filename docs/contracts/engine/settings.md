# Engine settings

Part of the [engine contract](../engine.md). Read its shared state, edit and
persistence rules before the sections here that the change affects.

## Network settings

Use libtorrent's port mapping with UPnP and NAT-PMP enabled by default, one
on/off setting, and a configurable listen port. The engine applies those
choices; it does not implement another router client. Mapping success is not
proof of public reachability or firewall permission.

The encryption setting shows people that TinyTorrent encrypts, and lets them
choose how strictly. Each choice sets libtorrent's encryption policy:

- Preferred, the default, encrypts the data with every peer that supports it:
  `pe_enabled`, `pe_both` and `prefer_rc4`. Without `prefer_rc4`, the side
  that accepts a connection chooses plaintext, so only the handshake is
  encrypted.
- Required connects only to peers that encrypt: `pe_forced` and `pe_rc4`.
- Allowed is the libtorrent default: `pe_enabled` and `pe_both` without
  `prefer_rc4`.
- Disabled uses plain connections only: `pe_disabled`.

The proxy setting sends peer and tracker connections through a SOCKS5,
SOCKS4 or HTTP proxy. Its type, address, port, user name and password save
together, and a proxy without its address or port is refused, because it
would stop every connection. settings.json holds the password encrypted with
DPAPI for the current Windows user, so a copy of the file does not reveal it.
The engine encrypts a password only when a settings command changes it, and
every later save writes that encrypted text. A failed encryption therefore
refuses only that command, with `encryption_failed`, and never blocks another
save or loses the saved password.
The settings reply and the snapshot carry the password as plain text, because
only the same logon session can open the pipe. While a proxy is in use,
libtorrent accepts no incoming connections, so the engine also stops port
mapping.

libtorrent applies encryption and the proxy only to new connections, so a
change of either pauses and resumes the session, which reconnects every peer.
libtorrent cannot tell a failing proxy from peers that are offline, so the
engine checks the proxy itself each time it applies one: it connects, signs in
and reports the outcome in the snapshot. The `check_proxy` command runs the
same check on values that are not saved.

Proxy checks use the confirmed adapter for name resolution and the TCP connection,
including checks of draft proxy credentials. A missing adapter reports the proxy
as unreachable without opening a connection. A confirmed proxy or adapter change,
or loss of that adapter, cancels earlier checks before they can send credentials;
the automatic check runs again when the selected route becomes available.

The network adapter setting, by default any adapter, limits torrent
traffic to one adapter, such as a VPN, through libtorrent's listen and outgoing
interface settings. While that adapter is absent, no torrent traffic flows and
the window and the tray tooltip say why, so traffic never leaks onto another
adapter.

Speed limits and a second pair, alternative limits, use libtorrent's session
rate limits. Both pairs start unlimited, so selecting either before configuring
it does not unexpectedly throttle transfers. The interface owns the
[speed-mode choices](../ui/settings.md#speed-limits).
A fixed choice saves `schedule_enabled: false` with `limit_mode`; Weekly schedule
enables the saved periods. Turning limits off keeps the caps the person typed.
Outside scheduled periods, the schedule applies standard limits. Both pairs include LAN and loopback peers: global means all torrent
traffic, with no undisclosed local-network exemption. The engine assigns every
peer socket type to libtorrent's global peer class while retaining its other
class defaults. This makes the displayed limits apply to local transfers too.
The deliberately small tray menu contains only its immediate session
controls.

Weekly periods repeat in local time with Monday numbered zero. Equal start and
end times span a full day beginning at that time; an earlier end spans midnight.
Pause wins over alternative limits on overlap. Manual Pause all remains saved
and authoritative. Explicit Resume during a scheduled pause bypasses that pause
until the next schedule-mode change. Editing periods without changing the current
scheduled mode preserves that pause exemption. A fixed limit choice disables
scheduling until the person selects Weekly schedule again; it does not expire
at a period boundary.
A limit choice never resumes paused transfers. An absent selected adapter
still blocks transfers. The temporary pause exemption is not saved or
replayed after restart. The saved limit choice remains the manual default
when scheduling is disabled. Snapshots report the applied mode and caps, their
controlling origin and the current pause reason together, so the window does not infer
effective policy from saved settings or an unfinished Settings edit. The pause
reason names a pause that Resume all lifts first, the person's and then the
schedule's, so the adapter is the reason only when nothing else pauses
transfers, and the window then offers Settings instead of Resume.

Queue limits start at libtorrent's defaults: three downloads, five seeds and
200 connections. Zero means unlimited in these controls. Ratio and seeding-time
limits start disabled; reaching either pauses through the saved intent owner.
Automatic seed stopping waits until completion work has settled, because pausing
between the final flush or move and finish-time verification can prevent that
verification from running. Verification after the final-folder move also satisfies
finish-time verification; it does not schedule a second check of the same files.
A final-folder move refused with an error requiring the person does not exempt
a torrent still seeding at its original folder: no completion work can advance
until that error is resolved, so its seed limits continue to apply.
Elapsed-time limits request asynchronous status updates for idle seeds, because
libtorrent does not publish changes to elapsed time alone. Each seed has at most
one request awaiting an answer during normal operation, with at most 64 across
the session. Answers release slots for the rest of the round immediately, so a
large library neither floods the alert queue nor waits a timer interval between
batches. A dropped status alert releases pending requests for retry on the next
round. Replies update the ordinary torrent status,
so policy reads one authority without holding up window commands.
The ratio denominator is the greater of downloaded and verified bytes; seeding
time excludes paused time. Explicit Resume or Force of completed content saves a
per-torrent exemption, so it does not pause again immediately or after restart.

## Connection measurement

The engine owns a temporary connection test independently of the window's
refresh interval. It suspends the session through the existing pause policy;
it never rewrites individual torrent intent or the saved Pause all choice.
Before measuring, session statistics must report no connected or half-open
peers and unchanged payload counters for one second. Stopping is bounded to
ten seconds so a session that cannot become quiet is restored instead of
measured under load.

Measurement runs off the engine thread against Cloudflare's public HTTP
endpoints, the owner's choice, through Windows WinHTTP, without a package,
JavaScript or WebView. The [network route](../architecture.md#network-route)
governs it; WinHTTP cannot enforce a configured proxy or adapter, so either one
leaves testing unavailable. Before the person selects Test, the page discloses
the provider, IP and data use, its privacy link, the temporary torrent pause,
the test length, and that data use depends on connection speed. Cloudflare's
open-source SDK documents the endpoints, but that is no compatibility guarantee
for a native client; there is no second provider or silent fallback.

Download and upload run in turn for a fixed time: five seconds each way for
Quick and fifteen for Longer. Only time ends a direction, so traffic grows with
the connection's speed; at 1 Gbps, Longer moves about 1.9 GB each way. Four
HTTPS connections repeat 10 MB requests back to back, so the line is not idle
between requests and a limit on one connection does not cap the result. The
rate is payload bytes divided by elapsed time after a two-second warm-up,
measured with a monotonic clock. The warm-up absorbs connection set-up and an
ISP's short burst above the sustained speed. Upload counts the bytes the socket
accepted. A direction that moves no bytes after the warm-up fails visibly. The
owner chose this over Cloudflare's own aggregation, the 90th percentile of
single requests within a small data budget, which reports bursts above the
sustained speed on download and the idle gaps between requests on upload;
Ookla, fast.com and M-Lab also measure for a fixed time.

Requests use WinHTTP's asynchronous mode, so cancellation and the direction
deadline close a pending request after its initiating function returns. The
worker keeps its buffers and callback context until WinHTTP reports handle
closure, so cancellation cannot race an in-progress call or free memory a
callback still uses
([WinHTTP concurrency](https://learn.microsoft.com/en-us/windows/win32/winhttp/concurrency-in-winhttp)).
Elapsed time freezes when each direction ends, including cancellation and
failure. Payload counts exclude protocol overhead, so billed traffic is higher,
and results do not promise torrent speed.

Download and upload share a 45-second deadline. A successful test retains suspension for
three minutes; a successful retest starts that hold again without releasing
the existing suspension. Draft edits do not extend it. Navigation, Apply, page
cancellation, test cancellation, failure, expiry, disconnect and shutdown release
only this operation's restriction. The current pause policy decides whether
transfers can resume, preserving independent manual, schedule and adapter pauses.

Restoration checks the session's pause state before reporting completion. A
failure remains visible; it must not claim that transfers resumed. Results are
transient measured capacities, never automatically applied settings. The
connection owns the operation, so another caller cannot release or replace it.

## libtorrent settings

The engine keeps the defaults of the pinned libtorrent release, v2.1.2, because
they are tuned and a changed value can slow transfers without a visible
benefit. The Settings page exposes the approved [product settings](../ui/settings.md),
including network protocol choices, queue and seeding policy,
and the memory and checking controls in [Disk write caching](#disk-write-caching).
The [wire representation](../pipe-api.md#wire-representation) owns their fields and ranges.
The disk backend stays at its
default ([Disk write caching](#disk-write-caching)). The engine changes four
more defaults:

- `user_agent` is `TinyTorrent/<version>`, and `peer_fingerprint` is
  `lt::generate_fingerprint("TY", ...)` with the three-part product version.
  The incrementing build number remains in `user_agent`; the compact fingerprint's
  single-character version fields cannot represent an unrestricted build count.
  Trackers and peers then see TinyTorrent instead of a generic libtorrent client, and a private
  tracker that admits only known clients can admit it by name.
- `dht_bootstrap_nodes` lists `dht.libtorrent.org:25401`,
  `dht.transmissionbt.com:6881` and `router.bittorrent.com:6881`. The default
  names only the first, so when that host is unreachable DHT cannot start and a
  magnet link without trackers never gets its metadata.
- `active_dht_limit`, `active_lsd_limit` and `active_tracker_limit` are `-1`,
  as `active_limit` already is. Running torrents are auto-managed, so the
  default limits would stop all but 88 of them announcing on DHT.
- `active_checking` is the largest `int`, so libtorrent starts every check in
  its queue and
  [drive turns](files.md#drive-turns) decide which ones run. libtorrent pauses every
  check when the limit is 0 or less.

## Disk write caching

Use the selected libtorrent release's default Windows disk backend and write
policy, leaving `session_params::disk_io_constructor` unset. In the reviewed
2.1.2 release that is pread with write-through, which `disk_write_mode` does not
configure. Libtorrent 2.1 has no configurable disk-cache size. Advanced settings
instead exposes `max_queued_disk_bytes` as Disk write buffer, saved as
`disk_buffer_mib`: 1–1024 MiB, initially libtorrent's 100 MiB default, for each
drive letter or server in use. Lowering
this threshold can reduce memory for writes waiting on a slow disk by applying
backpressure to peers; it may reduce download speed. It is neither a strict
allocation ceiling nor a limit on the engine's total memory or Windows' cache.
An imported `disk_cache_mb` value does not change this setting.

Advanced also exposes three controls for verification and large seeding libraries:

- Checking memory (`checking_memory_mib`): 1–1024 MiB, default 4. This sets
  `checking_mem_usage` in 16 KiB blocks (64 blocks per MiB). It targets the
  outstanding reads per checking torrent, not all checking memory: libtorrent
  keeps at least two pieces outstanding per hashing thread, so large pieces
  can exceed the target.
- Checking threads (`hashing_threads`): 1–64, default 1, per running check.
  libtorrent gets this value unchanged and gives each drive letter or server
  this many threads. The drive turns run one check at a time on a drive
  letter or server, also when a folder links to another drive, so each
  running check has these threads to itself. A single thread
  suits sequential hard-drive access; extra threads allow parallel
  verification on faster storage at the cost of CPU and memory. Download-time
  hashing retains the regular disk threads.
- Open-file limit (`file_pool_size`): 1–10000, default 40, for each drive
  letter or server in use. A larger pool reduces reopen work across many
  active files but retains more operating-system resources.

Defaults match the pinned libtorrent release. Changes use the existing settings
commit and session update, including after restart. Windows working-set trimming,
disk backend selection and cache bypasses are not controls:
they add paging or change storage behavior without an established benefit for
this backend. A Windows working-set limit would not cap allocations or the
separate WinUI process.

The default is the starting point, not a claim of lower memory use. Take the
first real workload measurement required by [testing](../../agents/testing.md#resource-checks),
and compare mmap only if it shows a disk or memory problem mmap could plausibly
improve.
