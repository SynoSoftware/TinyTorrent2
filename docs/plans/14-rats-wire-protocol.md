# Rats wire protocol

Research, 2026-10-26. This is the byte-level wire protocol a receive-only
TinyTorrent client must speak to a Rats Search v2.3.5 peer over one TCP
connection. It builds on [Rats catalogue compatibility](14-rats-catalogue-compatibility.md)
and does not repeat it. It is research for the planned client. It authorizes no
implementation, no network contact and no change to any product contract.

## Sources and how to read the citations

Every fact below comes from source text. Nothing was run, built or sent to a
peer for this document.

| Tag | Meaning |
| --- | --- |
| `L path:line` | librats at the pinned commit `84884e78018aaad975b62d62b507f20ee527765b`, file `src/librats/<path>`. Read from `raw.githubusercontent.com` at that commit. |
| `R path:line` | Rats Search checkout `3rdParty/references/rats-search/src/<path>`. Its HEAD is `032d297a`, and `git ls-tree HEAD src/librats` there gives the same pinned commit `84884e78`. The existing research shows that v2.3.5 differs from HEAD only in `CHANGELOG.md`. |
| `E file:line` | The passing experiment in `artifacts/experiments/rats-compatibility-20261026/evidence/`. |
| `H` | The released librats 3.0.3 headers in `artifacts/experiments/rats-compatibility-20261026/librats/windows/include/librats/`. |

Rats Search v2.3.5 links the pinned commit, not 3.0.3. The experiment receiver
used 3.0.3 and still worked against v2.3.5 (`E events.txt:1-6`). The pinned
sources are therefore the specification, and the 3.0.3 headers are a
cross-check (section "Pinned sources against released headers").

All multi-byte integers on this wire are big-endian, except two places that
the text marks: the Noise nonce counter and the ChaCha20-Poly1305 length block
are little-endian.

## The layer stack in one table

A connection uses these layers in this order.

| # | Layer | What it is | Section |
| --- | --- | --- | --- |
| 1 | Rendezvous | Mainline DHT lookup of SHA-1 of `rats-search/4` gives `ip:port` of Rats peers. | Layer 1 |
| 2 | TCP link | Plain TCP. No preamble. A stream of length-prefixed blocks. | Layer 2 |
| 3 | Noise handshake | Standard `Noise_XX_25519_ChaChaPoly_SHA256`, three blocks, empty payloads, prologue bound to `rats-search/4`. | Layer 3 |
| 4 | Encrypted messages | Every later block is one AEAD-sealed inner message `[type u8][flags u8][channel u16][payload]`. | Layer 4 |
| 5 | Unsolicited traffic and keep-alive | What the server sends first. No ping is needed. | Layer 5 |
| 6 | Typed JSON messages | Message type 7: `[name_len u16][name][JSON]`. | Layer 6 |
| 7 | File transfer | Message type 4: nine binary opcodes. | Layer 7 |

## Layer 1: DHT rendezvous

A Rats node announces itself on the Mainline DHT under one 20-byte info hash.

| Item | Value | Source |
| --- | --- | --- |
| Key string | the node protocol, `rats-search/4` | `R net/p2p_transport.cpp:309`, `L subsystems/dht_discovery.cpp:53-55` |
| Info hash | SHA-1 of the 13 ASCII bytes, no terminator | `L subsystems/dht_discovery.cpp:34-40` |
| Info hash value | `278903a7e96d10ceec7ddefb8b9bcdd36305346c` | computed from the rule above |
| Evidence in the released app | its log prints `announce 278903a7` | `E server-console.log:168` |
| Port in `announce_peer` | the node's own listen port, sent explicitly (`implied_port` is set only if that number equals the DHT port) | `L subsystems/dht_discovery.cpp:178`, `L dht/dht.cpp:273-293` |
| Default listen port | 4444 (setting `p2pPort`) | `R app/config_store.cpp:59` |
| Port if 4444 is busy | the node tries `port+1` up to `port+4` and announces the one it bound | `L node/node.cpp:142-150`, `L node/node.cpp:252-254` |
| Cadence | search every 30 s, announce every 10 min, first announce at once | `L subsystems/dht_discovery.h:51-56`, `L subsystems/dht_discovery.cpp:161-182` |

The announced port accepts inbound TCP. A node listens on TCP and UDP on the
same port number, and the released app printed `tcp+udp` (`L node/node.cpp:191-212`,
`L node/node.cpp:255-258`, `E server-console.log:157`). Nothing in the Rats
config turns TCP off. The accepted socket is handled by `L transport/reactor.cpp:429-451`.

Two limits of this rendezvous:

- The announced number is the local listen port. It is not a UPnP-mapped
  external port (`L subsystems/dht_discovery.cpp:178`). A returned endpoint
  behind a NAT without a matching forward will not accept a connection.
  The 3.0.3 header documents a different rule (`H subsystems/dht_discovery.h:19-34`),
  which v2.3.5 does not use.
- A Rats node refuses inbound peers when it already holds `max_peers`
  connections. The default is 10, and the Rats settings clamp it to a minimum of
  10 (`R app/config_store.cpp:17-18`, `R app/config_store.cpp:234-237`,
  `R net/p2p_transport.cpp:303`). The refusal looks like a TCP connect that
  succeeds and is then closed. It happens at accept, before any handshake
  (`L transport/reactor.cpp:442-445`, `L node/node.cpp:565-571`), or just after the
  handshake (`L node/node.cpp:597-604`).

## Layer 2: TCP link framing

There is no preamble, magic number or version byte. The first bytes the client
sends are the first handshake block (`L transport/tcp_link.cpp:19-35`). The
server sets `TCP_NODELAY` (`L transport/reactor.cpp:482`); the client may do
the same.

The stream is a sequence of blocks (`L wire/frame.h:67-69`, `L wire/frame.cpp:12`,
`L wire/frame.cpp:31-33`).

| Offset | Size | Field | Rule |
| --- | --- | --- | --- |
| 0 | 4 | `length` | u32 big-endian. Counts only the body. |
| 4 | `length` | `body` | Opaque at this layer. |

- Largest accepted body: 64 MiB = 67,108,864 bytes. A larger `length` closes the
  connection with a protocol error (`L transport/connection.cpp:283`).
- A sender refuses a message when `4 + payload + 16 > 64 MiB`
  (`L transport/connection.cpp:62-67`).
- During the handshake the body is a raw Noise message. After it, the body is
  one sealed message (Layer 4).
- During the handshake the receiver also rejects any block above 65,535 bytes
  (`L security/noise_security.cpp:93-98`). The three real handshake blocks are
  far smaller.

## Layer 3: the security handshake

### What it is

It is standard `Noise_XX_25519_ChaChaPoly_SHA256` from noiseprotocol.org
revision 34. The Noise name string is exactly 32 bytes
(`L crypto/noise.h:35`). The transport is Noise-secured and has no other
security layer (`R net/p2p_transport.cpp:311`, `L node/config.h:94-95`).

| Item | Value | Source |
| --- | --- | --- |
| Protocol name | `Noise_XX_25519_ChaChaPoly_SHA256` (32 bytes, so `h` is the name padded with zeros, which here means no padding) | `L crypto/noise.h:35`, `L crypto/noise.cpp:171-187` |
| Initial values | `h = name`, `ck = h` | `L crypto/noise.cpp:174-184` |
| Prologue | the 14 bytes `librats-proto` + `0x1F`, then the protocol string `rats-search/4`. Total 27 bytes. There is no length prefix, although a comment in `noise_security.h` says "length-prefixed". | `L security/handshaker.h:33-35`, `L security/noise_security.cpp:118`, `L security/noise_security.cpp:62` |
| Prologue hex | `6c6962726174732d70726f746f1f726174732d7365617263682f34` | computed |
| After `MixHash(prologue)` | `h = 3504e772716da005443cd024336a34e500bdb6486fb079961ea5021a826cc323` | computed: `SHA256(name || prologue)` |
| Prologue is always mixed | yes, even when empty | `L crypto/noise.cpp:389-396` |
| Handshake payloads | empty in all three messages. No identity key, no signature, no extra data. | `L crypto/noise.cpp:495-509`, `L crypto/noise.cpp:600-613` |
| Static key | 32-byte X25519. A node may persist it in `identity.key`; the client may use any key. | `L node/node.cpp:47-70` |
| Peer ID | `SHA-256(remote static public key)`, 32 bytes. Nothing else is hashed in. The ID is not sent on the wire. | `L peer/peer_id.cpp:22-26`, `L security/noise_security.cpp:78` |

The prologue binds the protocol. A peer with another protocol string gets a
failed MAC in message 2 or 3, not a clean error (`L node/config.h:97-103`).

### The three blocks

The client is the initiator (`->` is client to server).

| # | Direction | Block length (prefix bytes) | Body | Tokens |
| --- | --- | --- | --- | --- |
| 1 | `->` | 32 (`00 00 00 20`) | `e` (32) | `e` |
| 2 | `<-` | 96 (`00 00 00 60`) | `e` (32), ENC(`s`) (32+16), ENC(empty) (16) | `e, ee, s, es` |
| 3 | `->` | 64 (`00 00 00 40`) | ENC(`s`) (32+16), ENC(empty) (16) | `s, se` |

Sources: `L crypto/noise.cpp:441-467` (initiator writes), `L crypto/noise.cpp:469-492`
(responder writes), `L crypto/noise.cpp:542-568` (initiator reads),
`L crypto/noise.cpp:570-597` (responder reads).

Block 1 has no key yet, so its empty payload adds no bytes. The server
accepts exactly this: after `e` the rest of the body is the empty payload.

### Step by step for the initiator

Notation: `H(x)` is SHA-256. `MixHash(d)` is `h = H(h || d)`. `MixKey(d)` is
`ck, k = HKDF2(ck, d)` and sets the cipher key `k` with nonce counter `n = 0`.
`EncAndHash(p)` is `c = AEAD(k, n, ad = h, p)` then `n += 1` then `MixHash(c)`.
With no key yet, `c = p`. `DecAndHash(c)` uses `ad = h` as it was before
`MixHash(c)`.

1. Setup. `h = name`, `ck = h`. `MixHash(prologue)`.
2. Block 1. Make ephemeral `e`. `MixHash(e.pub)`. Send `e.pub`. Then
   `EncAndHash(empty)` with no key. This still runs `MixHash` over zero bytes,
   so `h = H(h)`. Skipping it breaks the handshake hash.
3. Read block 2 (must be at least 96 bytes). `re = body[0:32]`. `MixHash(re)`.
   `MixKey(DH(e, re))` (`ee`). `rs = DecAndHash(body[32:80])`. `MixKey(DH(e, rs))`
   (`es`). `DecAndHash(body[80:96])` must give an empty payload (tag check).
4. Block 3. `EncAndHash(s.pub)` gives 48 bytes. `MixKey(DH(s, re))` (`se`).
   `EncAndHash(empty)` gives 16 bytes. Send the 64 bytes.
5. Split. `HKDF2(ck, empty)` gives `k1, k2`. The initiator sends with `k1` and
   receives with `k2`. Both nonce counters start at 0. (`L crypto/noise.cpp:255-269`,
   `L crypto/noise.cpp:621-643`.)

The handshake nonce use is easy to get wrong because `MixKey` resets `n` and
`EncAndHash` increments it:

| Step | Key from | Nonce `n` used |
| --- | --- | --- |
| Block 2, ENC(`s`) | `ee` | 0 |
| Block 2, ENC(empty) | `es` | 0 |
| Block 3, ENC(`s`) | `es` (same key, second use) | 1 |
| Block 3, ENC(empty) | `se` | 0 |

The DH results are raw X25519 outputs (RFC 7748). `es` for the initiator is
`DH(e, rs)`. `se` for the initiator is `DH(s, re)`.

### Primitive definitions

| Primitive | Definition in the pinned source | Source |
| --- | --- | --- |
| AEAD | ChaCha20-Poly1305 as in RFC 8439: Poly1305 key from block 0, data from block 1, tag over `AD`, pad16, ciphertext, pad16, `len(AD)` and `len(CT)` as two little-endian u64. Tag is 16 bytes, appended. | `L crypto/chachapoly.c:68-123`, `:125-196` |
| Nonce (12 bytes) | `00 00 00 00` followed by the counter `n` as little-endian u64 | `L crypto/noise.cpp:55-65`, `L crypto/chachapoly.c:11-12` |
| HMAC | HMAC-SHA256 | `L crypto/hkdf.c:21-70` |
| HKDF2 | `temp = HMAC(ck, ikm)`; `out1 = HMAC(temp, 0x01)`; `out2 = HMAC(temp, out1 || 0x02)`. Same as RFC 5869 with empty info. With empty `ikm` (split), `temp = HMAC(ck, "")`. | `L crypto/hkdf.c:219-239`, `L crypto/noise.cpp:189-201`, `:255-269` |
| Hash | SHA-256 | `L crypto/noise.cpp:203-212` |
| DH | Curve25519 (X25519) | `L crypto/noise.cpp:306-315` |

### Deviations from Noise revision 34

The handshake itself follows the specification. I found these differences, all
in the transport phase or in what is left out:

1. Transport messages may be up to 64 MiB. The specification limits every Noise
   message to 65,535 bytes. Only handshake blocks are held to 65,535
   (`L security/noise_security.cpp:93-98`; transport: `L transport/connection.cpp:62-67`).
2. No rekey. There is no rekey call and no `n = 2^64 - 1` guard
   (`L crypto/noise.cpp:737-763`; a search of the pinned `src/` for `rekey`
   finds nothing).
3. Transport AEAD uses empty associated data and an implicit counter nonce. The
   nonce is not sent. Blocks must be opened in order (standard Noise behavior,
   noted because it makes a dropped block fatal).
4. A failed AEAD check after the handshake closes the connection
   (`L transport/connection.cpp:298`).
5. No check that a DH output is all zeros. This is a librats omission and does
   not change the bytes on the wire. A client should still treat an all-zero DH
   result as a failed handshake.
6. The comment "length-prefixed name+version" next to the prologue
   (`L security/noise_security.h:34`) is wrong. The code adds a 14-byte tag and no
   length (see the prologue row above).

Primitives the pinned handshake does not use: Ed25519, BLAKE2 (BLAKE2s appears
only in the UDP transport, `L transport/udp_mux.cpp:3`), SHA-512, SHA-1 (only for
the DHT key), Poly1305 outside the AEAD.

## Layer 4: encrypted messages

After the split every block body is `AEAD(k_send, n, ad = empty, inner)`. The
body length is `len(inner) + 16`. Each block uses one nonce, and the counter
rises by one per block in each direction (`L transport/connection.cpp:94-104`,
`L transport/connection.cpp:294-305`).

`inner` is a 4-byte header and the payload (`L wire/frame.h:20-23`,
`L wire/frame.cpp:47-73`):

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| 0 | 1 | `type` | message type, table below |
| 1 | 1 | `flags` | always 0 in everything read; receivers ignore it |
| 2 | 2 | `channel` | u16 big-endian; 0 for every type except 1 (`App`) |
| 4 | n | `payload` | depends on `type` |

A decrypted message shorter than 4 bytes closes the connection
(`L transport/connection.cpp:300-301`). An unknown `type`, or a type with no
handler, is logged and ignored (`L node/node.cpp:654-666`). It does not close
the connection. A client should do the same.

Message types in the pinned source (`L wire/frame.h:39-50`):

| `type` | Name | Use for this client |
| --- | --- | --- |
| 1 | App | not used by Rats |
| 2 | Control | server sends an identify message; client may ignore it |
| 3 | Gossip | not attached in Rats |
| 4 | FileChunk | Layer 7: the snapshot file transfer |
| 5 | Ping | not attached in Rats; ignore |
| 6 | Storage | server sends a sync request and live entries; ignore |
| 7 | Typed | Layer 6: JSON requests and answers |
| 8 | Pex | server sends a peer-exchange request; ignore |
| 9 | Punch | hole punching; ignore |
| 10 | Relay | relay; ignore |

The released 3.0.3 header adds 11 (`Media`) and 12 (`Rendezvous`)
(`H wire/frame.h:51-52`); the Rats v2.3.5 server never sends them.

Worked example, `randomTorrents` with `{"limit":5}`:

| Part | Bytes |
| --- | --- |
| Inner header | `07 00 00 00` |
| Name length | `00 0e` |
| Name | `72 61 6e 64 6f 6d 54 6f 72 72 65 6e 74 73` |
| JSON | `7b 22 6c 69 6d 69 74 22 3a 35 7d` |
| Inner total | 31 bytes |
| Sealed body | 31 + 16 = 47 bytes |
| Block on the wire | `00 00 00 2f` followed by the 47 bytes |

## Layer 5: after the handshake

### State of the server when the handshake ends

The server calls its "peer connected" handlers when its Noise handshake
completes (`L node/node.cpp:573-652`). It does not wait for any message from
the client. Application messages can flow in both directions at once.
The client may send its first typed message right after block 3.

### What the server sends first

Rats Search attaches these subsystems (`R net/p2p_transport.cpp:321-430`; the
CMake forces storage and the search features on, `R CMakeLists.txt:145-146`).
On connect the server sends the following unasked. The client must read and
drop all of it.

| Order | Type | Payload | Source |
| --- | --- | --- | --- |
| a | 2 Control | identify (below) | `L node/node.cpp:650-652`, `:727-752` |
| b | 8 Pex | `01 00 00 20`: version 1, request, up to 32 peers | `L subsystems/peer_exchange.cpp:63-70`, `:72-73` |
| c | 6 Storage | `02`: sync request | `L storage/storage.cpp:1004-1018`, `:1141-1148` |
| d | 7 Typed | `client_info` | `R services/peer_registry.cpp:50-54` |
| e | 7 Typed | `feed` with `{"localSize":N,"localFeedDate":ms}` | `R peer/peer_api.cpp:415-418` |
| f | 7 Typed | `randomTorrents` with `{"limit":5}` | `R peer/peer_api.cpp:421-422` |

Later, unasked: `randomTorrents` from the server every 10 to 60 s
with `{"limit":5,"version":"2.0"}` (`R services/replication_service.cpp:12-18`,
`:62-63`), `torrentAnnounce`, and live Storage entries (type 6, first byte `01`)
(`L storage/storage.cpp:1098-1139`).

The client does not have to answer any of these. I found no code that disconnects a
peer for ignoring them (the pinned subsystems contain no peer-disconnect call,
and the Rats app has none outside shutdown).

The client must not send a Storage sync request (type 6, payload `02`). The server
would answer with its whole key-value store (`L storage/storage.cpp:931-941`).

The client must keep reading for the whole life of the connection. The server's
send queue closes the connection as a slow consumer when it holds more than
8 MiB of unsent data and tries to send again (`L transport/connection.cpp:83-92`,
`L transport/connection.h:93`). The Rats app logs such a drop and names storage
snapshots as one cause (`R net/p2p_transport.cpp:132-143`).

### Identify (type 2)

Layout, for a client that wants to parse or send it (`L node/identify.cpp:57-128`):

| Field | Size | Notes |
| --- | --- | --- |
| version | 1 | must be `1` |
| listen_port | 2 | the sender's listen port; 0 if it has none |
| count | 1 | 0 to 32 advertised addresses |
| address | 1 + 4 or 16 + 2 | `ip_len` (4 or 16), IP bytes, port; repeated `count` times |
| observed | 1 (+ 4 or 16 + 2) | `ip_len` 0 means none |
| transports | 1 | bit 0 TCP, bit 1 UDP; 0 means "not said"; optional trailing byte |

No code path that I read requires the client to send identify. If a client sends
one, `listen_port = 0` and `count = 0` give the server no dialable address
(`L node/node.cpp:754-794`). The smallest valid message is
`01 00 00 00 00 00`. Whether a real server accepts a client that never sends it
was not tested (see "Unverified").

### Keep-alive and ping

None is needed. Rats Search does not attach the ping service
(`R net/p2p_transport.cpp:321-430`; the service is `L subsystems/ping_service.cpp`).
If a type 5 message ever arrives, its payload is `[tag u8][token 8 bytes]`
with tag 0 = ping, 1 = pong, and a pong echoes the token
(`L subsystems/ping_service.cpp:62-84`).

There is no idle timeout on TCP. The only idle timeout in the pinned sources
is 45 s on the UDP stream (`L transport/udp_stream.h:252`,
`L transport/udp_stream.cpp:1092-1125`). The server's periodic maintenance only
shrinks idle receive buffers and never closes a connection
(`L transport/connection.h:211`, `L transport/reactor.h:222`).

### What closes a connection (server side)

| Cause | Detail | Source |
| --- | --- | --- |
| Handshake not finished 15 s after accept | closed as handshake failed | `L transport/reactor.h:217`, `L transport/reactor.cpp:499-509` |
| Block length above 64 MiB | protocol error | `L transport/connection.cpp:283` |
| AEAD check fails, or inner message under 4 bytes | protocol error | `L transport/connection.cpp:298-301` |
| Handshake MAC fails (wrong protocol string, wrong keys) | handshake failed | `L security/noise_security.cpp:93-105` |
| Server holds `max_peers` (default and minimum 10) | closed at accept or after the handshake | see Layer 1 |
| Client does not read, so the server queue passes 8 MiB | slow consumer | `L transport/connection.cpp:83-92` |
| Same peer ID connects again (same role, same wire) | the older connection is replaced and closed | `L peer/peer_table.cpp:66-67`, `:81-85`, `L node/node.cpp:621-627` |

File-transfer timeouts (Layer 7) end the transfer, not the connection.

## Layer 6: typed JSON messages

### Encoding

Frame `type` 7, `flags` 0, `channel` 0. Payload (`L subsystems/message_json.cpp:34-43`,
`:73-110`):

| Offset | Size | Field |
| --- | --- | --- |
| 0 | 2 | `name_len`, u16 big-endian |
| 2 | `name_len` | message name, ASCII, no terminator |
| 2 + `name_len` | rest | compact JSON text, UTF-8, one value |

- Malformed JSON, an unknown name, or a short payload is logged and dropped.
  The connection stays open.
- The server sees the sender as the Noise-authenticated peer ID, not as any
  field in the JSON.
- Numbers: Rats writes integers as JSON numbers without a fraction
  (`E events.txt:3`). Read them as 64-bit integers where the field says so.

### Messages the client sends

| Name | JSON | Server behavior | Source |
| --- | --- | --- | --- |
| `client_info` | `{"clientVersion":"...","torrents":0,"files":0,"totalSize":0,"peersConnected":0,"connectedAt":0,"databaseSharing":false}` | Stored by peer ID. Every key is optional and read with a default. Not needed by any other handler. | `R domain/peer.h:24-51`, `R services/peer_registry.cpp:62-81` |
| `randomTorrents` | `{"limit":5}` | Replies with up to `limit` records. `limit` defaults to 5 and is clamped to 1..10. Other keys are ignored. | `R peer/peer_api.cpp:225-241` |
| `databaseRequest` | `{"session":1}`, optionally with `"have":{"snapshot":"<name>","bytes":<n>}` | Starts the snapshot negotiation below. | `R services/database_sync_service.cpp:626-638`, `:752-835` |
| `databaseCancel` | `{"session":1}` | Drops the serve and cancels its file transfer. `session` is checked only when present. | `R services/database_sync_service.cpp:837-846` |

The `session` value is a number the client chooses. The server echoes it in
every reply (`R services/database_sync_service.cpp:754`). The experiment sent
`"session":1` (`E receiver.cpp:109`).

The experiment's `client_info` was
`{"clientVersion":"TinyTorrent-compatibility-experiment","torrents":0,"files":0,"totalSize":0,"peersConnected":1,"databaseSharing":false}`
(`E receiver.cpp:105-106`). The server stored it and logged it
(`E server-console.log`, `PeerRegistry` line).

### Messages the client receives

`client_info` from the server has the same keys. `databaseSharing` says whether
the server will serve its index. A server that sends `false` or omits it
refuses `databaseRequest` with `"sharing disabled"` (`R domain/peer.h:19-22`,
`R services/database_sync_service.cpp:756-759`). Older servers may send
`torrentsCount` and `filesCount` instead (`R domain/peer.h:41-45`).
Example from the experiment: `{"clientVersion":"2.3.5.1","connectedAt":0,"databaseSharing":true,"files":36,"peersConnected":1,"torrents":12,"totalSize":73728}`
(`E client-info.json`).

`randomTorrents_response`: `{"torrents":[ ... ]}`. The server sends it even
when the array is empty. It sends nothing when its `p2pReplicationServer`
setting is off (`R peer/peer_api.cpp:227-240`). The server picks rows with
`seeders > 0` and category not `xxx`, in random order (`R data/torrent_repository.cpp:536-543`).
Repeats across calls are possible. Each record has these keys
(`R domain/torrent_codec.cpp:30-53`; sample `E batch.json`):

| Key | Type | Notes |
| --- | --- | --- |
| `hash` | string | 40 hex characters, lowercase on the sender; the receiver lowercases it (`R domain/torrent_codec.cpp:77`) |
| `name` | string | |
| `size` | integer | total bytes |
| `files` | integer | file count |
| `pieceLength` | integer | |
| `added` | integer | milliseconds since the Unix epoch; 0 if unknown |
| `contentType` | string | `video`, `audio`, `books`, `pictures`, `software`, `games`, `archive`, `bad`, or empty (`R domain/content.cpp:5-27`) |
| `contentCategory` | string | `movie`, `series`, `documentary`, `anime`, `music`, `ebook`, `comics`, `software`, `game`, `xxx`, or empty (`R domain/content.cpp:29-56`) |
| `seeders`, `leechers`, `completed` | integer | tracker claims |
| `trackersChecked` | integer | milliseconds since the epoch; 0 if never |
| `good`, `bad` | integer | votes |
| `info` | object | only when non-empty; scraped extras, not torrent `info` bytes |
| `files_list` | array | `[{"path":"...","size":N}]`; present in this response |

JSON key order is alphabetical in practice (`E batch.json`); do not depend on it.

### Snapshot negotiation

The client sends `databaseRequest`. The server answers with one or two
`databaseRequest_response` messages. All are Typed messages.

Refusal (`R services/database_sync_service.cpp:742-750`):

| JSON | Meaning |
| --- | --- |
| `{"session":S,"accepted":false,"reason":"...","retryAfter":N}` | `retryAfter` (seconds) appears only for some reasons |

Reasons in the source and what the server checks (`R services/database_sync_service.cpp:752-835`,
`:848-865`, `R peer/peer_api.cpp:246-251`):

| `reason` | Cause | `retryAfter` |
| --- | --- | --- |
| `sharing disabled` | server setting `databaseSharing` is off | none |
| `file transfer unavailable` | transport has no file transfer | none |
| `empty database` | no torrents stored | none |
| `too many requests` | the same peer ID asked again within 30 s | seconds left |
| `busy` | 3 transfers already running | 60 |
| `cannot prepare a database snapshot` | not enough free disk to build one | 300 |
| `no snapshot to serve` | no file to offer | 300 |
| `unsupported` | no sync service; this reply has no `session` key | none |

Accepted, snapshot must be built (sent only when the stored snapshot is stale:
none exists yet, it is older than 6 hours, or the torrent count has drifted by
more than 10 %; `R services/database_snapshot.cpp:121-133`,
`R services/database_snapshot.h:43-46`, `R app/config_store.cpp:62-63`):

`{"session":S,"accepted":true,"ready":false,"torrents":N,"format":1}`

While it builds, every 10 s the server sends (`R services/database_sync_service.cpp:1239-1259`):

`{"session":S,"processed":P,"total":T}` as `databaseProgress`.

Accepted and ready (sent in both flows; in the stale flow it is the second reply)
(`R services/database_sync_service.cpp:881-884`):

`{"session":S,"accepted":true,"ready":true,"torrents":N,"bytes":B,"snapshot":"<file name>","format":1}`

The `OFFER` (Layer 7) follows in the same moments, as the next block on the
connection (`R services/database_sync_service.cpp:886`). The experiment saw
both replies and then the offer (`E events.txt:3-5`): `ready:false` first,
then `ready:true` with `bytes` 991 and `snapshot` `snapshot-1.ratsdb`, then
`offer 1 snapshot-1.ratsdb 991`.

If the server fails to build the snapshot, it sends no failure message. It stops
the heartbeat and drops the serve (`R services/database_sync_service.cpp:992-1002`).
The client learns this only from silence.

Deadlines that Rats' own client uses, as a reference for the client's choice
(`R services/database_sync_service.cpp:36-43`): 60 s for the first answer, 3 min of
silence while the server prepares (each `databaseProgress` resets it), 3 min
from `ready:true` to the offer.

## Layer 7: file transfer (type 4)

One transfer carries the snapshot as one file. The sender is the server.
Every message is a `FileChunk` frame (`type` 4, `flags` 0, `channel` 0). The first
payload byte is the opcode. All integers are big-endian
(`L subsystems/file_transfer.h:41-52`, `L subsystems/file_transfer.cpp:14-34`).

### Opcodes

| Op | Name | Payload after the opcode | Sent by | Source |
| --- | --- | --- | --- | --- |
| 1 | OFFER | `id u64`, `flags u8`, `total u64`, `name_len u16`, `name`, `file_count u32`, then `file_count` x (`path_len u16`, `path`, `size u64`) | server | `L subsystems/file_transfer.cpp:221-234` |
| 2 | RESPONSE | `id u64`, `accept u8`, optional `start_offset u64` | client | `L subsystems/file_transfer.cpp:524-526` |
| 3 | CHUNK | `id u64`, `file_index u32`, `offset u64`, data to the end of the payload | server | `L subsystems/file_transfer.cpp:371-384` |
| 4 | FILE_END | `id u64`, `file_index u32`, `sha256` 32 bytes | server | `L subsystems/file_transfer.cpp:402-407` |
| 5 | PROGRESS | `id u64`, `received u64` | client | `L subsystems/file_transfer.cpp:695` |
| 6 | COMPLETE | `id u64`, `ok u8` | client (also server on failure) | `L subsystems/file_transfer.cpp:176-178` |
| 7 | CANCEL | `id u64` | either | `L subsystems/file_transfer.cpp:1070-1082` |
| 8 | PAUSE | `id u64` | either | `L subsystems/file_transfer.cpp:927-933` |
| 9 | RESUME | `id u64` | either | `L subsystems/file_transfer.cpp:934-944` |

`flags` in OFFER: bit value 1 means "directory". `id` is chosen by the sender.
Ids are unique only per sender, so the client keys a transfer by `id` on this
connection. The snapshot offer in the experiment had `id` 1.

Payload sizes: RESPONSE without offset is 10 bytes. PROGRESS is 17. COMPLETE is
10. CANCEL, PAUSE and RESUME are 9. CHUNK has 21 bytes of header
(`1 + 8 + 4 + 8`) before the data.

### What the server offers

For the snapshot the OFFER has `flags = 0`, `file_count = 1`, one manifest entry
whose `path` equals `name` and whose `size` equals `total`
(`L subsystems/file_transfer.cpp:182-196`, `:221-234`). `name` is the bare file name
(for example `snapshot-1.ratsdb`, `E events.txt:5`). The size also appears as
`bytes` in the ready reply. The Rats app never sends a directory
offer and its own receiver rejects them (`R net/p2p_transport.cpp:162-167`).

Parser limits to apply to the untrusted OFFER (the server code does the same):
`file_count * 10` must not exceed the remaining bytes
(`L subsystems/file_transfer.cpp:832-837`). Beyond that, a client should check
`file_count == 1`, `flags == 0`, `total == size`, and `total` against its own cap.

### Accept, stream, finish

1. The client accepts by sending RESPONSE `[2][id][1]`. The sender stays
   Pending until it gets it (`L subsystems/file_transfer.cpp:851-878`). To
   refuse it sends `[2][id][0]`.
2. The sender streams CHUNKs of at most 64 KiB (`chunk_size` default; Rats keeps
   it, `R net/p2p_transport.cpp:398-407`) with `file_index` 0 and a rising
   `offset` that starts at 0. The receiver must treat any CHUNK whose `offset` is
   not exactly the number of bytes already received as an error, and any chunk
   that runs past the declared size likewise (`L subsystems/file_transfer.cpp:1013-1014`).
   The librats receiver fails the transfer in both cases and sends COMPLETE
   with `ok = 0`.
3. Window. The sender stops when `bytes sent - last acknowledged >= window_bytes`
   (4 MiB by default) and waits (`L subsystems/file_transfer.cpp:410-415`,
   `L subsystems/file_transfer.h:84`). The only thing that raises the
   acknowledged count is PROGRESS.
4. Acknowledgement. The client sends PROGRESS with the cumulative count of bytes
   received so far. librats sends it every `min(256 KiB, window/2)` bytes and
   always once the file's last byte is in (`L subsystems/file_transfer.cpp:677-695`).
   The value is a running total across all files; this transfer has one file.
5. After the last CHUNK the sender sends FILE_END with the file's SHA-256: the
   hash of the complete file content from byte 0, with no framing
   (`L subsystems/file_transfer.cpp:386-407`). The receiver compares it with its
   own running hash (`L subsystems/file_transfer.cpp:722-734`).
6. The client sends COMPLETE `[6][id][1]` when the hash matched and the file is
   stored, or `[6][id][0]` on any failure. The sender finishes the transfer only
   when COMPLETE arrives (`L subsystems/file_transfer.cpp:417-419`, `:907-913`).
   Without it the transfer idles until the sender's timeout.

The sender ignores a PROGRESS, COMPLETE or CANCEL whose `id` it does not know or
that did not come from the peer it is sending to (`L subsystems/file_transfer.cpp:152-161`).

### Timeouts in the file transfer

Rats sets both (`R net/p2p_transport.cpp:405-406`):

| Timer | Value | Counts from | Effect |
| --- | --- | --- | --- |
| Offer | 600 s | the OFFER, while no RESPONSE came | sender drops the transfer |
| Transfer idle | 300 s | the last chunk sent or PROGRESS received | sender sends COMPLETE `ok = 0` and drops it |

(`L subsystems/file_transfer.cpp:1143-1202`.) The server's serve ends and the
connection stays open. The librats defaults are 300 s for the offer and 60 s
for idle (`L subsystems/file_transfer.h:86-91`).

### Resume (optional, not needed for a first version)

A receiver that already holds the first `k` bytes of the same file can send
RESPONSE `[2][id][1][k u64]`. The sender honors it only for a single-file
offer when `0 < k < size`, then starts at `k` and counts `k` as already sent
and acknowledged (`L subsystems/file_transfer.cpp:868-873`). The sender
re-reads the first `k` bytes to continue its SHA-256, so FILE_END still covers
the whole file. The receiver must hash its own first `k` bytes too. An old sender
ignores the offset and starts at 0; the receiver must then restart
(`L subsystems/file_transfer.cpp:1003-1012`). To continue the same snapshot
file after a break, the next `databaseRequest` names it in `have`
(`R services/database_sync_service.cpp:799-812`). The experiment did not test
resume.

### PAUSE and RESUME

The Rats app exposes only send, accept, accept-resume, reject and cancel
(`R net/p2p_transport.cpp:650-713`). It never sends PAUSE or RESUME. A client
needs no support beyond ignoring them.

### The file

The received file is a `.ratsdb` snapshot. Its format is in
[Rats catalogue compatibility](14-rats-catalogue-compatibility.md#snapshot-encoding-and-admission-semantics)
and `R services/database_dump.h:20-29`. That layer is not part of this wire
protocol.

## A whole session in order

Rats' own deadlines are shown as a reference. They are not product settings.

| Step | Client action | Wait for |
| --- | --- | --- |
| 1 | Look up peers (Layer 1) and open TCP | connect result |
| 2 | Send block 1 (32 bytes) at once | block 2 within 15 s of the TCP accept |
| 3 | Check block 2 length is at least 96, run the Noise steps, send block 3 | none |
| 4 | Start the read loop. Drop unknown types. | always on |
| 5 | Send `client_info` (`databaseSharing:false`) | the server's `client_info` shows `databaseSharing` |
| 6 | Send `randomTorrents` `{"limit":N}` | `randomTorrents_response` (experiment waited 15 s: `E receiver.cpp:108`) |
| 7 | If the server shares, send `databaseRequest` `{"session":S}` | first reply in 60 s |
| 8 | On `ready:false`, keep reading `databaseProgress` | up to 3 min of silence, then `ready:true` |
| 9 | On `ready:true`, wait for OFFER | 3 min |
| 10 | On OFFER: validate, send RESPONSE accept | CHUNKs |
| 11 | Per CHUNK: write, hash, send PROGRESS every 256 KiB | FILE_END |
| 12 | On FILE_END: compare SHA-256, send COMPLETE | none |
| 13 | Close TCP. To abort earlier, send `databaseCancel` and CANCEL, then close. | none |

The experiment receiver did steps 5, 6, 7 and the file steps against a
real v2.3.5 app, in that order on one connection. The receiver closed the
connection after the transfer, and the server logged `PeerClosed`
(`E events.txt`, `E server-console.log:174-199`). It was a librats program,
not a hand-written client.

## Pinned sources against released headers

The 3.0.3 headers match the pinned sources where the wire is defined, apart
from the additions noted:

| Header | Result |
| --- | --- |
| `security/handshaker.h`, `noise_security.h`, `identity.h` | identical (prologue, peer ID) |
| `subsystems/message_json.h`, `peer/peer_id.h`, `node/identify.h`, `subsystems/ping_service.h` | identical |
| `subsystems/file_transfer.h` | identical wire comment; one extra private field (`rerun`) |
| `wire/frame.h` | same block and message header; adds types 11 and 12 and a deadline field |
| `crypto/noise.h` | adds explicit-nonce `seal`/`open` and key derivation for the UDP transport |
| `node/config.h`, `transport/connection.h`, `subsystems/dht_discovery.h` | restructured transport and a different DHT announce-port rule |

The `.cpp` files of 3.0.3 were not read. That the two versions interoperate
over TCP rests on the passing experiment.

## Crypto primitives needed

| Primitive | Use | OpenSSL mapping (my choice, not from the sources) |
| --- | --- | --- |
| X25519 | ephemeral and static keys, 3 DH operations | `EVP_PKEY_X25519`: generate, raw public key, derive |
| ChaCha20-Poly1305 (RFC 8439, 12-byte nonce, 16-byte tag) | handshake and transport AEAD | `EVP_chacha20_poly1305` |
| SHA-256 | `h`, peer ID, file hash | `EVP_sha256` |
| HMAC-SHA256 | Noise HKDF and split | `HMAC` / `EVP_MAC`. Implement the two-output HKDF directly as `L crypto/hkdf.c:219-239` does. |
| Secure random | ephemeral key, session ids | `RAND_bytes` |

Not needed: Ed25519, BLAKE2, SHA-512, SHA-1 on the peer wire, a separate
Poly1305. SHA-1 is needed once, for the DHT key, and the engine's DHT already has it.

## Size of the work

These are my estimates, not measurements.

| Part | C++ lines |
| --- | --- |
| Block reader and writer, sealed message header, dispatch | about 120 |
| Noise XX state, three small classes over OpenSSL | about 250 |
| Typed message encode and decode, session driver with deadlines | about 250 |
| File-transfer receiver (OFFER, RESPONSE, CHUNK, FILE_END, PROGRESS, COMPLETE, CANCEL) with streaming SHA-256 to disk | about 250 |
| Total for the wire client | about 900 |

This excludes a JSON parser, the `.ratsdb` reader (zlib frames; see the existing
research) and the DHT lookup, which the engine already has or owns. The
librats base library for comparison is 182 files and 1.64 MiB of source
(see the existing research).

## Unverified

1. No byte of this protocol was captured from a real connection. The layouts come
   from reading source. The experiment proves the librats library interoperates
   with v2.3.5. No independent implementation, including this Noise code, has
   talked to a Rats peer.
2. The 3.0.3 `.cpp` files were not read; only its headers. The pinned commit is
   older and the refactor changes how transports are built. Compatibility
   between 3.0.3 and v2.3.5 over TCP rests on one experiment run.
3. That the v2.3.5 release binary was built from `84884e78` rests on the local
   checkout's gitlink (`git ls-tree HEAD src/librats`) and on the existing
   research's tree comparison for the tag. The v2.3.5 tag tree is not in the
   shallow local clone, so the tag was not re-checked here.
4. A client that sends no identify (type 2) message, or no `client_info`:
   I found no code that requires either, but this was not tested against a real
   peer. The experiment sent `client_info` and librats sent identify itself.
5. A client that ignores every unsolicited request in "What the server sends
   first" (Pex, Storage, `feed`, `randomTorrents`): I found no code that
   penalizes it. Not tested.
6. The experiment log says the released app has 8 subsystems
   (`E server-console.log:157`). The attach list in the source can reach 11. I did not reconcile the
   count, so which unsolicited messages a given build sends is not exactly known.
7. How many public Rats peers are below `max_peers` (default 10) or reachable
   through the announced port is not measured.
8. Chunk size and window (64 KiB, 4 MiB) are the sender's configuration. Rats
   v2.3.5 uses the defaults (`R net/p2p_transport.cpp:398-407`). Other
   Rats-compatible senders are unknown.
9. Resume (RESPONSE with offset, `have` in `databaseRequest`) is read from source
   and was not run.
10. The OpenSSL mapping and the advice to reject an all-zero DH output are
    mine. They are not in the librats sources and were not run.
11. No known-answer test exists for this handshake. Standard `Noise_XX_25519_ChaChaPoly_SHA256`
    vectors would check the algorithm but not the Rats prologue. I did not run
    any.
