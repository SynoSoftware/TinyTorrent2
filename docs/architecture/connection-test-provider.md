# Connection test provider research

Initial research checked 2026-10-08. The selected implementation below records
the subsequent decision; runtime evidence is in the
[settings implementation record](../settings-implementation.md).

## Selected implementation

The owner selected Cloudflare's public HTTP endpoints for the small native
integration. TinyTorrent uses Windows WinHTTP, without a package, JavaScript,
or WebView. Windows automatic proxy configuration applies; a configured
torrent proxy or bound adapter disables this operation instead of bypassing
the person's torrent route. The page discloses the provider, IP/data use,
privacy link, temporary torrent pause, and approximate traffic ceiling before
the person selects Test.
[WinHttpOpen automatic proxy](https://learn.microsoft.com/en-us/windows/win32/api/winhttp/nf-winhttp-winhttpopen)

Download and upload run sequentially, using completed HTTPS requests and a
monotonic clock. Sample payload starts at 64 KiB and grows to 10 MB per
request, with at most 128 MiB per direction and ten seconds of requests per
direction. Incomplete requests produce no saved result. The engine also
bounds stopping to ten seconds and measurement to 45 seconds. Cancellation
closes the active WinHTTP request. These are payload bounds, so protocol
overhead can increase billed traffic. Results estimate HTTP capacity; they
are neither torrent speed nor Cloudflare's browser SDK statistics.

Before HTTP traffic starts, libtorrent session statistics must show no
connected or half-open peers and unchanged sent/received peer payload bytes
over a full second. Temporary suspension is a separate runtime pause reason
at the existing policy owner. Successful measurement holds it for three
minutes for retesting; departure, Apply, cancellation, failure, timeout,
disconnect and shutdown release it without changing saved torrent intent or
other pause reasons. Restoration is confirmed against the session pause flag,
with a visible failure if it cannot be confirmed in five seconds. The existing
snapshot consumer uses a one-second cadence during the operation and hold.

The provider endpoints are documented by its official open-source SDK, but
that documentation is not a native service compatibility guarantee. This
limitation remains explicit; there is no second provider or silent fallback.

## Initial comparison

Use M-Lab ndt7 for a native implementation when documented permission to use a
public measurement service is required. M-Lab explicitly permits custom desktop
and commercial clients without an API key. Its public data requirement is a
material product tradeoff: disclose it before the user starts a test. The
service is best effort; failure must leave existing speed limits intact.
[M-Lab developer requirements](https://www.measurementlab.net/develop/)

Cloudflare has the smaller HTTP transport and better privacy fit, but the
documentation reviewed describes a browser SDK and its endpoints, rather than
a supported native API with explicit service-use permission. The open-source
client licence does not itself license the hosted service. If the owner chooses
Cloudflare, establish that native endpoint use is permitted before shipping;
do not add both providers or an automatic fallback.

## Comparison

| Concern | Cloudflare | M-Lab ndt7 |
| --- | --- | --- |
| Integration | Browser JavaScript SDK; documented GET/POST endpoints | Documented custom desktop clients; HTTPS discovery plus WSS |
| Upload evidence | Complete POST and response timing | Server reports bytes actually received and elapsed time |
| Extra SDK features | Latency, jitter, loaded latency, packet loss, AIM scores | Application goodput; optional server TCP statistics |
| Privacy | IP received, truncated; anonymized measurement information shared | IP, time and measurement data publicly retained indefinitely |

The Cloudflare SDK depends on browser PerformanceResourceTiming, supports
custom measurement sequences and cancellation, and documents
`https://speed.cloudflare.com/__down` and `https://speed.cloudflare.com/__up`.
Packet loss now requires the integrator's own TURN configuration. A native
speed-only implementation needs none of the extra SDK features and should not
embed a browser merely to obtain two rates.
[Cloudflare SDK](https://github.com/cloudflare/speedtest/blob/main/README.md)

Cloudflare's current speed-test notice says it receives the connecting IP,
derives city/country and ASN, truncates IPv4 to /24 and IPv6 to /48, and shares
anonymized measurement information with partners without sharing the IP.
Present a provider/privacy link and traffic warning before starting; the
reviewed SDK documentation does not state a separate native consent contract.
[Cloudflare speed-test privacy notice](https://speed.cloudflare.com/)

M-Lab requires active initiation with informed consent and links to applicable
policies. It provides no exemption from public results. Its developer guidance
recommends no more than four daily tests for integrations and describes a
40-test daily client rate limit. An on-demand test avoids scheduling machinery.
[M-Lab developer requirements](https://www.measurementlab.net/develop/)

M-Lab's current privacy policy also excludes users younger than 16. The notice
must clearly explain public, indefinite retention of the IP, test time and
results; merely naming M-Lab is insufficient to communicate that consequence.
[M-Lab privacy policy](https://www.measurementlab.net/privacy/)

## Native ndt7 mechanics

1. GET `https://locate.measurementlab.net/v2/nearest/ndt/ndt7`.
   Read `results[].urls`, choosing the complete `wss:///ndt/v7/download` and
   `wss:///ndt/v7/upload` URLs from one result. Preserve their query parameters,
   especially `access_token`; never hardcode a measurement server.
   [Locate v2](https://www.measurementlab.net/develop/locate-v2/)
2. Run download and upload sequentially on separate WSS connections. Negotiate
   `Sec-WebSocket-Protocol: net.measurementlab.ndt.v7`; check the selected
   protocol. Count received binary payload for download. Send random binary
   messages for upload, initially 8 KiB. Receive JSON measurements concurrently.
3. Compute upload from the last server `AppInfo.NumBytes` and
   `AppInfo.ElapsedTime` (microseconds): bytes per second is
   `NumBytes * 1,000,000 / ElapsedTime`. Download uses locally received payload
   and monotonic elapsed time. Missing server upload evidence means no upload
   result; locally queued writes are not proof of delivery.
4. Permit WebSocket message sizes through 16 MiB without allocating the whole
   message: consume fragments incrementally. Handle close and ping/pong. Tests
   normally last at most ten seconds after handshake; allow a thirteen-second
   hard stop. Use a bounded 5–10 second handshake timeout.
   [ndt7 specification](https://github.com/m-lab/ndt-server/blob/master/spec/ndt7-protocol.md)

Windows already supplies WinHTTP WebSockets. Mark an HTTPS GET with
`WINHTTP_OPTION_UPGRADE_TO_WEB_SOCKET`, send/receive the handshake, require
HTTP 101, then call `WinHttpWebSocketCompleteUpgrade`. The platform requires
`Winhttp.lib`, not another runtime.
[Microsoft WinHTTP upgrade API](https://learn.microsoft.com/en-us/windows/win32/api/winhttp/nf-winhttp-winhttpwebsocketcompleteupgrade)

Proposed product bounds, not provider requirements: one run at a time, a
45-second overall deadline, and 100 MiB payload per direction. Cancel closes
the active request/socket. Reaching the byte ceiling ends that direction;
report a bounded estimate rather than implying full-line capacity. Download
may overshoot at the transport level because data is already in flight; a
payload ceiling is not an exact billed-traffic ceiling. Fixed small tests can
underestimate fast connections; neither provider measures torrent swarm speed
or guarantees an ISP subscription rate.

## Cloudflare HTTP alternative

The current official source issues GET `__down?bytes=N` and POST
`__up?bytes=N` with an N-byte body. It consumes the response before recording
the sample. Its upload denominator is request-start to response-start, so a
native implementation must wait for a successful server response after the
entire body, rather than timing buffer writes. The source reads `Server-Timing`
for download/latency adjustment; blindly subtracting that duration from upload
would diverge from the current implementation. This endpoint does not provide
the explicit received-byte measurement available in ndt7.
[Cloudflare bandwidth implementation](https://github.com/cloudflare/speedtest/blob/main/src/engines/BandwidthEngine/BandwidthEngine.ts)

A native HTTP proposal would use reusable HTTPS connections, full successful
transfers, a monotonic clock, download byte verification, and explicit body
length. Bound its adaptive sample ladder by the same time/traffic limits;
discard failed or incomplete transfers. This is a custom estimate, not a claim
of equivalence with Cloudflare's browser SDK statistics.

Native WinHTTP probes on 2026-10-08 confirmed download samples through 4 MiB,
but the 16 MiB request received HTTP 403. The documented 10 MB sample succeeds
for both download and upload; the implementation caps each request at that
tier. This is a tested request size, not a guarantee about other sizes or
future provider availability.
