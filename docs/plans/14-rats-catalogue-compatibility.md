# Rats catalogue compatibility

Research and controlled experiment, 2026-10-26. This follows
[multiple torrents per peer contact](14-peer-metadata-research.md). It identifies
an existing protocol to investigate and how TinyTorrent would implement it; it
authorizes no product implementation. The initial source research ran no
applications; the subsequent controlled experiment and its limits are recorded
at the end.

## Conclusion and release evidence

Rats already offers both small batches of searchable torrent/file records and
whole-index transfers over a persistent cooperating-peer connection. A new
TinyTorrent-specific protocol is unnecessary to investigate this capability.
The controlled experiment below confirms a Rats-compatible session, a
`randomTorrents` response and a full snapshot on one connection.

This is implemented in released source, not merely proposed in documentation:

- Local Rats checkout: `032d297a62e895dd253d6df9095a6f8068f49f91`.
- [Published v2.3.5](https://github.com/librats/rats-search/releases/tag/v2.3.5):
  released 2026-10-07, non-prerelease, with Windows ZIP/setup, Linux and macOS
  assets. The tag resolves to `8415a208025d3827f2f380f3bfe530557a10eaba`.
- The official [comparison API](https://api.github.com/repos/librats/rats-search/compare/8415a208025d3827f2f380f3bfe530557a10eaba...032d297a62e895dd253d6df9095a6f8068f49f91)
  reports one subsequent commit changing only `CHANGELOG.md`. The
  [release tree](https://api.github.com/repos/librats/rats-search/git/trees/8415a208025d3827f2f380f3bfe530557a10eaba?recursive=1)
  and local `git ls-tree` agree on transport blob `bd4bb01e`, dump `5739b419`,
  database sync `22b4a380`, and librats submodule
  `84884e78018aaad975b62d62b507f20ee527765b`. The release's raw sync source was
  also retrieved directly. The local checkout is shallow; absent older Git
  objects do not establish absence of a feature in that release.

These source checks establish released code and published assets. The later
experiment establishes controlled binary interoperability; reachable public
sharing peers remain unmeasured.

## Interoperable sequence

### 1. Find and authenticate the correct peers

Rats sets librats `NodeConfig.protocol = "rats-search/4"` and
`Security::Noise`. The node uses self-certifying Curve25519 identities and
`Noise_XX_25519_ChaChaPoly_SHA256`; the application protocol identifier is bound
into the handshake. Using a TinyTorrent identifier would put the client in a
different network. There is no negotiation that automatically joins older
incompatible Rats protocol identities.

Its default DHT discovery key is the node protocol string. The rendezvous hash
is the **20-byte SHA-1 of the UTF-8/ASCII bytes `rats-search/4`**. Discovery uses
Mainline `get_peers`/`announce_peer` for that hash; those returned endpoints are
Rats peers, not metadata sources for a torrent named by that hash. Default
search cadence is 30 seconds, announce cadence 10 minutes with immediate first
announce. Default bootstrap nodes are `router.bittorrent.com:6881`,
`dht.transmissionbt.com:6881`, `router.utorrent.com:6881`,
`dht.libtorrent.org:25401`, and `dht.aelitis.com:6881`. Rats defaults its peer
listener to 4444 and its DHT port to 6881; discovered ports take precedence.

The transport offers reliable UDP and TCP, preferring UDP with TCP fallback
after 1.2 seconds. DHT discovery, mDNS, reconnection, mesh peer exchange and NAT
helpers are separately attached subsystems. A bare librats node can instead
`connect(host, port)`; it does not require its own DHT or BitTorrent downloader.
Consequently, reusing TinyTorrent's existing DHT to look up the rendezvous hash
and passing returned endpoints to librats is **source-supported in principle**,
but has not been tested. It avoids assuming a second DHT is necessary.

Sources: local `src/net/p2p_transport.cpp:301` under
`3rdParty/references/rats-search/`,
[pinned discovery:34](https://github.com/librats/librats/blob/84884e78018aaad975b62d62b507f20ee527765b/src/librats/subsystems/dht_discovery.cpp#L34),
[discovery configuration:43](https://github.com/librats/librats/blob/84884e78018aaad975b62d62b507f20ee527765b/src/librats/subsystems/dht_discovery.h#L43),
[node API:135](https://github.com/librats/librats/blob/84884e78018aaad975b62d62b507f20ee527765b/src/librats/node/node.h#L135),
[node configuration](https://github.com/librats/librats/blob/84884e78018aaad975b62d62b507f20ee527765b/src/librats/node/config.h),
[bootstrap defaults:362](https://github.com/librats/librats/blob/84884e78018aaad975b62d62b507f20ee527765b/src/librats/dht/node.cpp#L362).

### 2. Exchange application capabilities

After the peer-connected event, send typed message `client_info` with
`clientVersion`, `torrents`, `files`, `totalSize`, `peersConnected`,
`connectedAt`, and `databaseSharing`. The last flag advertises whole-index
serving; absent means false. A receiving-only spike should advertise its own
actual capability, not claim to serve an index. Peers can refresh this message
when sharing changes; `clientVersion` is informational, not a substitute for
the protocol identity or capability flag.

Typed messages are librats `MessageType::Typed`: a big-endian u16 type-name
length, type-name bytes, then JSON bytes. Use upstream `MessageJson::on/send`
rather than reconstructing its encrypted framing.

Sources: local `src/services/peer_registry.cpp:50`, `src/domain/peer.h:12`;
[typed encoder:34](https://github.com/librats/librats/blob/84884e78018aaad975b62d62b507f20ee527765b/src/librats/subsystems/message_json.cpp#L34).

### 3. Request small batches or a complete snapshot

| Request | Response and scope | Gates and limits |
| --- | --- | --- |
| `randomTorrents` with `{"limit":5}` | `randomTorrents_response` with `{"torrents":[...]}`; records include file lists and scraped extras. | Serving uses `p2pReplicationServer`, default true. Requested limit defaults to 5 and is clamped to 1–10. Selection requires `seeders > 0` and excludes the XXX category (`src/data/torrent_repository.cpp:536`). Random batches can repeat; there is no exhaustive cursor or delta guarantee. |
| `torrent` with `{"hash":"<40 hex>","files":true}` | `torrent_response` is one record with file list. | Missing hashes receive no reply. This retrieves catalogue fields, not BEP 9 bytes. |
| `feed` with `{}` | `feed_response` contains feed entries/file lists, date and size. | Feed selection is a subset, not the entire catalogue. |
| `databaseRequest` with `{"session":1}` | Negotiates a `.ratsdb` snapshot, then a file offer. | `databaseSharing`, default true; nonempty catalogue, file-transfer support, three concurrent serves, and 30-second repeat-request spacing. Refusals may provide `reason` and `retryAfter`. |

Rats' background random replication requests five records per peer: an initial
five-second timer, then adaptive 10–60-second intervals. Those are its own
policies, not proof of our achievable rate. Whole-index exchange transfers a
**complete compressed snapshot**, not just hashes absent locally.

For a snapshot, retain `(peerId, session)` for the outstanding request:

1. `databaseRequest_response` echoes `session` and `accepted`. If accepted but
   `ready:false`, the peer is building a snapshot; `databaseProgress` carries
   `session`, `processed`, `total`. Silence, not elapsed preparation work,
   drives its timeout.
2. A ready response carries `ready:true`, `torrents`, `bytes`, `snapshot`
   (generation filename), and `format:1`. It can be a second response after
   preparation.
3. The sender calls `sendFile(peerId, snapshotPath)`. The file offer has a
   sender-assigned transfer ID, not the application session ID. Rats accepts
   only while awaiting a file from the requested peer and no transfer is
   already active, then correlates progress/completion by `(peerId, transferId)`.
   The code tolerates the offer arriving before the JSON reply; do not assume
   stronger session correlation than the wire provides.
4. On completion, validate the dump and merge it. For resumption, a later
   request can include `have:{snapshot,bytes}`. Resume only the matching peer,
   generation and size; the file-transfer accept carries the actual offset.
   Otherwise restart. `databaseCancel:{session}` cancels preparation/serving.

Reference waits: 60 seconds for an answer; three minutes of preparation silence
or waiting for an offer; preparation heartbeat every ten seconds. Rats sets
file-transfer idle timeout to five minutes and offer timeout to ten minutes.
They describe current interoperability behavior, not proposed TinyTorrent UI
settings.

Sources: local `src/peer/peer_api.cpp:186`, `:207`, `:225`;
`src/services/replication_service.cpp:12`; `src/services/database_sync_service.cpp:600`,
`:651`, `:748`, `:848`, `:1066`, `:1173`;
[released peer API](https://github.com/librats/rats-search/blob/8415a208025d3827f2f380f3bfe530557a10eaba/src/peer/peer_api.cpp#L225),
[released database exchange](https://github.com/librats/rats-search/blob/8415a208025d3827f2f380f3bfe530557a10eaba/src/services/database_sync_service.cpp#L600).

### 4. Receive the file on the same peer transport

Librats `FileTransfer` attaches before node startup. `send_file`, `on_offer`,
`accept`/`accept_resume`, `reject` and progress/completion callbacks own transfer
mechanics. Control and data use `MessageType::FileChunk` on the existing peer
session; there is no new torrent handshake per record.

Its big-endian payload opcodes are OFFER (1: transfer ID, flags, total, name,
file manifest), RESPONSE (2: ID, accepted, optional start offset), CHUNK (3: ID,
file index, byte offset, bytes), FILE_END (4: ID, file index, SHA-256), PROGRESS
(5), COMPLETE (6), CANCEL (7), PAUSE (8), RESUME (9). Defaults are 64 KiB chunks,
4 MiB unacknowledged window and 256 KiB progress acknowledgements. Whole-file
SHA-256 is checked before delivering the temporary file. It proves transfer
integrity, not the truth of the torrent records inside.

[Pinned transfer header:41](https://github.com/librats/librats/blob/84884e78018aaad975b62d62b507f20ee527765b/src/librats/subsystems/file_transfer.h#L41)
owns exact field layouts and API; `:387` sends through the peer transport.

## Snapshot encoding and admission semantics

`.ratsdb` format 1 is independent of the Manticore database:

```text
"RATSDB\0" (7 bytes)
u32 version
u32 headerBytes + UTF-8 JSON header
repeated: u32 frameBytes + qCompress(JSONL, level 6)
u32 zero
u32 footerBytes + UTF-8 JSON footer
```

All outer integers are big-endian. The header contains `format:"rats-search-db"`,
`version`, producing `client`/`peerId`, ISO creation time, estimated `torrents`,
and `compression:"zlib"`. Each frame normally contains 500 torrent JSON lines.
The footer contains exact declared `torrents`, `files`, `totalSize`. Qt's
`qCompress` wraps zlib data in an additional four-byte big-endian uncompressed
size hint; plain zlib decompression of the entire frame is incorrect.

The reference reader checks magic, supported version and stored block lengths
against 64 MiB, decompresses each frame, and deserializes nonempty JSON objects.
Missing footer marks truncation; Rats can retain complete preceding frames.
Its compressed-block cap does **not** cap expanded bytes: `qUncompress` can
grow beyond its size hint. A TinyTorrent decoder must bound decompressed work
and validate JSON records rather than copying that assumption for untrusted
peer input. Dump completion, JSON validity, footer reconciliation and torrent
verification are separate facts; a successful file hash establishes none of
the latter three.

Sources: local `src/services/database_dump.h:19`, `database_dump.cpp:16`, `:109`,
`:198`, `:252`; [released format implementation](https://github.com/librats/rats-search/blob/8415a208025d3827f2f380f3bfe530557a10eaba/src/services/database_dump.cpp),
[Qt's documented wrapper and allocation behavior](https://doc.qt.io/qt-6/qbytearray.html#qUncompress).

Records contain a v1-style 40-hex hash, title, size, file count, piece length,
timestamps, categories, tracker seed/leech/completion claims, votes, optional
scraped `info`, and `files_list:[{path,size}]`. They contain **no original info
bytes, piece-hash string, original torrent file indices/padding, v2 identities
or v2 file roots**. The `info` field holds scraped extras, not torrent info.
Rats validates hash syntax/name and deduplicates by hash. For existing records,
its import keeps the local row, fills an absent file list and takes higher vote
counts; filters/classification apply to new rows. It does not authenticate that
the claimed file list belongs to the claimed hash.

TinyTorrent's `Catalogue::Admit` requires verified `torrent_info`/exact info
bytes (`engine/src/Catalogue.cpp:1097`), and file selection relies on original
layout indices, size and optional content-root validation
(`engine/src/Engine/Indexing.cpp:896`).
Imported array ordinal therefore cannot become an Add file index. A compatible
future importer must preserve the distinction between **peer-claimed search
records** and verified metadata, then resolve a selected hash through the
existing metadata/Add owner before selecting files. This gap needs an explicit
product/admission decision; bulk records cannot simply be passed to Admit.

Sources: local `src/domain/torrent.h:23`, `torrent_codec.cpp:29`,
`src/services/indexing_service.cpp:92`; [released codec](https://github.com/librats/rats-search/blob/8415a208025d3827f2f380f3bfe530557a10eaba/src/domain/torrent_codec.cpp#L29).

## Implementation direction

Owner direction, 2026-10-26: the product writes its own receive-only Rats
client over TCP. The released Rats source and pinned librats are the protocol
reference; a released Rats app is the test peer. Experiments that only measure
Rats peers and their records may use librats tooling as the receiver.

- **Routing.** Every outbound connection uses the saved proxy and adapter, and
  the engine's `MetadataRequester` already dials TCP that way. The inspected
  librats `NodeConfig` exposes a listener bind address and transport choices,
  but no proxy or bandwidth-limit setting; a bind address does not route
  outgoing sockets.
- **Scope.** The engine's existing DHT looks up the rendezvous hash. The client
  needs TCP, the Noise XX handshake, typed-message and file-chunk framing,
  `client_info`, `randomTorrents`, `databaseRequest` and a bounded `.ratsdb`
  reader. librats' DHT, reliable UDP, NAT, mDNS, reconnection and mesh exchange
  stay out.
- **Dependencies.** The bundled OpenSSL supplies the Noise primitives (X25519,
  ChaCha20-Poly1305, SHA-256), so `3rdParty` gains nothing.

The price is owning the protocol code:

- The Noise handshake is security code. Build it from OpenSSL primitives,
  follow the Noise specification and the Rats code exactly, and prove it
  against a real Rats peer, not only against itself.
- Rats' source is its only specification. Pin the client to one released
  version, because a new protocol identity such as `rats-search/5` ends
  interoperability.
- Rats prefers reliable UDP and falls back to TCP after 1.2 seconds; TCP also
  passes through SOCKS proxies, which rarely carry UDP. The first peer test
  confirms that Rats peers serve a TCP-only client.

Unverified: whether librats accepts externally created sockets. That would
remove only the routing reason; the scope and dependency reasons stand.

## Footprint, licences and next evidence

Measured source footprint at the pinned librats commit: its CMake base library
list contains **182 tracked C/C++ source/header files, 1,714,874 bytes (1.64 MiB)**,
including 75 compilation units totaling 1,118,045 bytes, plus two generated
headers. This is with search/download and distributed-storage features off and
C bindings off; leaving the default C bindings on adds two files/60,586 bytes.
The full repository has 566 tracked file blobs totaling 6,038,354 bytes
(5.76 MiB), including tests, examples and language/platform bindings. Method:
sum GitHub blob sizes for paths in the pinned CMake `LIBRARY_SOURCES` base
list; optional append lists are excluded. The base target still lists DHT,
NAT and other subsystems even if our node does not attach them. These are
source bytes, not linked executable size, installed size or memory; those
remain unmeasured and depend on linkage/build configuration.
[Pinned tree sizes](https://api.github.com/repos/librats/librats/git/trees/84884e78018aaad975b62d62b507f20ee527765b?recursive=1),
[CMake source list:148](https://github.com/librats/librats/blob/84884e78018aaad975b62d62b507f20ee527765b/CMakeLists.txt#L148).

Rats and librats are MIT; reused notices must remain. Bundled crypto also has
MIT and BSD-3-Clause notices, including Curve25519-donna. These are compatible
reuse candidates, subject to retaining the actual dependency notices.
[Pinned CMake options/dependencies](https://github.com/librats/librats/blob/84884e78018aaad975b62d62b507f20ee527765b/CMakeLists.txt#L87),
[librats licence](https://github.com/librats/librats/blob/84884e78018aaad975b62d62b507f20ee527765b/LICENSE),
[crypto notices](https://github.com/librats/librats/tree/84884e78018aaad975b62d62b507f20ee527765b/licenses),
[Rats licence](https://github.com/librats/rats-search/blob/8415a208025d3827f2f380f3bfe530557a10eaba/LICENSE).

The experiment scope was:

1. Use an actual controlled v2.3.5 peer and a minimal receiving session with its
   real protocol identity. Exchange `client_info`, request 1–10 random records,
   and record their fields/duplicates. This proves connection and message
   interoperability without database transfer or production schema changes.
2. Request one explicitly sharing peer's snapshot; verify file hash, bounded
   frame decoding, complete/truncated outcome and unique torrent/file counts.
   Measure bytes, elapsed time, CPU/memory and unique useful records per minute.
3. Resolve a small chosen subset through our existing metadata owner and compare
   claimed paths/sizes with verified layout. This measures how much later
   acquisition remains and whether filename results can safely reach Add.

The interoperability portions of steps 1 and 2 were authorized and completed
below; CPU, memory and meaningful sustained throughput were not measured.
Step 3 remains a possible later check, outside the requested batch-and-snapshot experiment. Public-peer
reachability/adoption, novel data per peer, correctness of public records,
internet throughput, production dependency footprint and policy-compatible
routing remain unmeasured.

## Controlled experiment: passed

One independent C++ receiver used the released librats 3.0.3 static library's
public C API to connect over loopback TCP to the portable released Rats Search
v2.3.5 application (self-reported `2.3.5.1`). It used `rats-search/4`, Noise,
JSON messaging and file transfer, without enabling DHT, BitTorrent or a database
in the receiver. This was library-to-released-application interoperability,
not two Rats applications or TinyTorrent production integration; it does not
change the implementation direction above.

The controlled peer held 12 synthetic torrents with three file entries each.
The receiver requested `randomTorrents:{limit:5}`, waited for its response,
then requested `databaseRequest:{session:1}` on the same connection.

| Observation | Result |
| --- | --- |
| Established peer connections | 1 |
| Batch | 5 distinct torrents, 15 file entries |
| Snapshot | 12 distinct torrents, 36 file entries; 991 bytes |
| File offers / completion | 1 / success |
| Received data | Byte-identical to the sender's snapshot |
| Record validation | Hash sets, names, sizes, file counts and every path/size matched the fixture |
| Format validation | Magic/version, bounded qCompress/zlib frame decode, complete footer and totals matched |
| Receiver process interval | 0.2428 seconds, including startup and shutdown; not an internet throughput benchmark |

Snapshot SHA-256:
`4de94f4d63ed107e850dc41abf7b137224ce88a66b5a380d6ac27febc4b292d3`.
The library reported successful verified file delivery; independent byte
comparison with the sender and format/record checks established catalogue
contents beyond the receiver's exit status.

Official GitHub assets were SHA-256 checked before use:

- `RatsSearch-Windows-x64-v2.3.5.zip`:
  `503bfb00eca6aa54f75212cb795b5dc1d7fe47018aa3e09bc08614fe61609d07`.
- `librats-windows-x64-3.0.3.zip`:
  `5b338800b054805a4791e66b73075a83a57cef71168de7454ea7b59044052da3`.

The library package ships a static library and an unsuitable generic client,
not a DLL or a Rats catalogue client. A disposable receiver was compiled and
linked against that unchanged library; TinyTorrent and dependencies were not
built. Its executable was 1,013,248 bytes, a standalone experiment host size,
not a measured increase to TinyTorrent. The release headers omit generated
`librats/util/features.h`; a host-local empty header selected static linkage
for the C API without changing the archive or rebuilding the library.

Two setup findings are retained so a repeat does not rediscover them. The first
control request failed with HTTP 400 before exchange; JSON POST parameters
worked for the Windows fixture path. An initial zero-seed fixture transferred
the whole snapshot but returned an empty random batch: the source's positive-
seed predicate explains this. The successful fixture used explicit synthetic
`seeders:1` claims, not measured public availability. No upstream code changed.

Both instances used disposable data. The receiver exited, the controlled Rats
process was stopped, its owned Manticore daemon stopped successfully, and Rats'
previous saved data-directory value was restored. Process inspection found no
remaining Rats, receiver or searchd process. Temporary peer databases were
removed after preserving the evidence.

Raw result, messages, snapshot, logs and receiver/controller sources are in
`artifacts/experiments/rats-compatibility-20261026/evidence/`. The result JSON
SHA-256 is `177845641a0c6515ef9e79290c51901d54a4ebbfd60f938f13cca9a3a49906f9`.
This proves the existing protocol can deliver many torrent/file records over
one connection. It does not establish reachable public sharers, novel-data
yield, resumability under interruption, production routing or public speed.
