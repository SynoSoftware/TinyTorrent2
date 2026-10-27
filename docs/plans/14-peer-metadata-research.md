# Multiple torrents per peer contact

Research, 2026-10-26. This records protocol and source findings for a possible
future capability; it does not authorize implementation or change the catalogue
contract. No crawler, benchmark or executable was run for this investigation.

## Finding

An ordinary BitTorrent connection supplies one torrent's metadata. A cooperating
catalogue peer can supply many torrents over one persistent connection, but that
requires an additional protocol supported at both ends. Rats Search implements
such catalogue transfer. Its exported records are useful for search, but are
not the original, hash-verifiable torrent metadata that TinyTorrent retains.

Therefore, “ask a contact for everything it knows” is feasible for cooperating
indexers. It is not an unimplemented BEP 9 request that our crawler could send
to every existing BitTorrent peer.

## What the standard protocols actually return

| Mechanism | Information per contact | Can it return other torrents' complete metadata on the established connection? |
| --- | --- | --- |
| BEP 3 peer handshake | Selects one infohash for a TCP/uTP peer connection. Both ends must agree. | No standard torrent-switch operation. Sharing one listening port does not mean sharing one connection across torrents. |
| BEP 9 `ut_metadata` | The selected torrent's info dictionary, split into 16 KiB pieces. Requests identify a piece number, not another hash. | No. Multiple outstanding requests fetch parts of the same dictionary. The result is not a complete `.torrent` file. |
| BEP 5 DHT | UDP queries return nodes or peer endpoints for a requested hash. | No torrent metadata. A DHT node is not an inventory of its host's downloaded torrents. |
| BEP 51 `sample_infohashes` | A batch of hashes stored by that DHT node, plus node contacts and sampling interval. | Hash discovery only. Packet size bounds the random sample; the advertised interval governs later sampling. It does not promise a full dump. |
| BEP 11 `ut_pex` | Added/dropped peer endpoints for the current torrent. | More potential sources for this torrent, not a list of the client's other torrents. |
| BEP 38 `similar` / `collections` | Hints embedded inside or outside torrent info, identifying potentially related torrents/collections. | No transfer protocol. Related hashes still need their own retrieval. BEP 9 does not carry hints placed outside the info dictionary. |

Primary specifications: [BEP 3](https://www.bittorrent.org/beps/bep_0003.html),
[BEP 9](https://www.bittorrent.org/beps/bep_0009.html),
[BEP 5](https://www.bittorrent.org/beps/bep_0005.html),
[BEP 51](https://www.bittorrent.org/beps/bep_0051.html),
[BEP 11](https://www.bittorrent.org/beps/bep_0011.html),
[BEP 38](https://www.bittorrent.org/beps/bep_0038.html).

Reconnecting to the same IP/port with a different handshake hash can retrieve a
second torrent **if that client serves it**. The successful first exchange does
not establish that fact, and the standard protocol supplies no full inventory.

## Optional extensions and deployed alternatives

### Negotiated peer-wire extensions

[BEP 10](https://www.bittorrent.org/beps/bep_0010.html) can negotiate a custom
extension that lists hashes and addresses metadata requests by hash. That could
carry several torrents on the existing connection. Both peers must implement
it; advertising a new extension alone cannot make ordinary clients answer.
Repeated extension handshakes change capabilities, not the torrent selected by
the original handshake. Libtorrent exposes
[plugin hooks](https://www.libtorrent.org/reference-Plugins.html) for such custom
networks. This establishes technical feasibility, not deployed interoperability.

Several similarly named features do something else:

- `lt_tex` is **tracker exchange**, specified by the deferred
  [BEP 28](https://www.bittorrent.org/beps/bep_0028.html). Its `added` list contains
  tracker URLs for the current torrent. Historical libtorrent support is not
  evidence of torrent-catalogue exchange. BEP 35 is
  [Torrent Signing](https://www.bittorrent.org/beps/bep_0035.html).
- `lt_donthave` withdraws a piece previously advertised by the peer;
  [libtorrent's extension description](https://www.libtorrent.org/extension_protocol.html#dont-have)
  specifies a piece index. `share_mode` controls downloading/uploading to improve
  a torrent's share ratio, not disclosure of a client's catalogue. Local sources:
  `3rdParty/libtorrent/src/bt_peer_connection.cpp:1939` and
  `3rdParty/libtorrent/include/libtorrent/torrent_flags.hpp:61`.
- Azureus/Vuze messaging supports negotiated messages, but the inspected
  BiglyBT `AZ_METADATA` carries `msg_type`, `piece`, `total_size` and bytes,
  without another torrent selector. `AZ_REQUEST_HINT` identifies payload
  piece/offset/length. Neither supplies a catalogue. This is a source finding
  about these messages, not a claim that no plugin could add one.
  [AZMetaData.java](https://github.com/BiglySoftware/BiglyBT/blob/35fbc1ebf56fd69acc3f72b8f51270a9d46f9e3e/core/src/com/biglybt/core/peermanager/messaging/azureus/AZMetaData.java#L174),
  [AZRequestHint.java](https://github.com/BiglySoftware/BiglyBT/blob/35fbc1ebf56fd69acc3f72b8f51270a9d46f9e3e/core/src/com/biglybt/core/peermanager/messaging/azureus/AZRequestHint.java#L131).

### Rats Search: actual bulk catalogue transfer

The local reference is pinned at `032d297a62e895dd253d6df9095a6f8068f49f91`.
Its `databaseRequest` / `databaseRequest_response` messages negotiate a snapshot,
then `sendFile(peerId, snapshot)` transfers the `.ratsdb` dump. The serving peer
must advertise database sharing; the current configuration defaults it on, but
users can disable it and older clients may not support it. No adoption or
reachable-peer count was measured.

This is the separate Noise-bound `rats-search/4` protocol, not `ut_metadata` on
the BitTorrent socket. JSON requests and file transfer attach to the same
librats node. The pinned librats transfer sends `MessageType::FileChunk` through
the existing peer transport rather than establishing a torrent-specific
connection for every exported record. The transport may use reliable UDP, TCP
or a relayed path; “one peer session” should not be misreported as necessarily
one direct TCP socket.

The important data distinction is visible in the serializer: the dump includes
torrent hashes, titles, sizes, file paths/sizes, piece length, categories,
availability/votes and optional scraped `info` extras. That `info` is not the
original BitTorrent info dictionary. The domain and codec do not retain its raw
bytes or v1 piece-hash string. Thus this can populate filename search in bulk,
but cannot satisfy TinyTorrent's promise to preserve hash-verifiable metadata
for download preparation and local rebuilding without reacquisition. Transfer
SHA-256 verifies the received dump, not every claimed torrent's identity.

Evidence:

- `3rdParty/references/rats-search/src/services/database_sync_service.cpp:756`
  gates serving; `:886` sends the snapshot.
- `src/net/p2p_transport.cpp:301` configures the protocol/security; `:393`
  attaches file transfer; `:650` delegates `sendFile` to librats.
- `src/services/database_dump.cpp:109`, `src/domain/torrent_codec.cpp:29` and
  `src/domain/torrent.h:23` establish exported data scope. Paths in this list
  are under the Rats reference above.
- [Pinned Rats source](https://github.com/librats/rats-search/tree/032d297a62e895dd253d6df9095a6f8068f49f91/src),
  [upstream API documentation](https://github.com/librats/rats-search/blob/032d297a62e895dd253d6df9095a6f8068f49f91/docs/API.md#database-replication).
- The local librats submodule is unpopulated. Its pinned
  [file_transfer.h:387](https://github.com/librats/librats/blob/84884e78018aaad975b62d62b507f20ee527765b/src/librats/subsystems/file_transfer.h#L387)
  was read from upstream; it sends file frames through the peer transport.

### BitComet: catalogue lists, then separate retrieval

BitComet's official documentation describes exchanging chosen shared torrents'
title, size and infohash with other enabled BitComet clients. It explicitly says
the list excludes torrent contents/file listings; selecting an entry retrieves
its `.torrent` through DHT from a sharer, possibly a different one. This is useful
bulk discovery, not proof of multiple complete metadata records on the original
connection. Its public description does not establish a wire format TinyTorrent
can immediately reuse, current adoption, or compatible third-party support.
[Official Torrent Exchange documentation](https://wiki.bitcomet.com/torrent_exchange).

## Our requester and the practical implication

Our `engine/src/MetadataRequester.cpp:571` writes one handshake hash and checks
the response matches it. At `:638` it reads `ut_metadata`/size, then `:654`
queues every metadata-piece request together. It already avoids a round trip
per metadata block. At `:701` it verifies SHA-1; `Finish` at `:164` closes the
socket. Failed endpoint attempts close and advance to another peer (`:175`,
`:190`). There is no connection cache, torrent-switch or catalogue extension.

This matches pinned Bitmagnet's relevant strategy: one request/hash/endpoint,
new TCP connection, deferred close, all metadata pieces requested before reading
the result. See
`3rdParty/references/bitmagnet/internal/protocol/metainfo/metainforequester/requester.go:86`
and `:266`, also
[pinned upstream requester](https://github.com/bitmagnet-io/bitmagnet/blob/e31b30d7351c269dc801876f92ebc59d31fee545/internal/protocol/metainfo/metainforequester/requester.go#L86).

Keeping an ordinary successful socket open would not reveal another torrent to
request. PEX could add sources for the current hash, but does not multiply
catalogue records per successful contact; its cost/benefit remains unmeasured.
A custom catalogue session could amortize connection setup and peer lookup
across many records, provided cooperating peers have useful unique records.
That gain is plausible, not benchmark evidence.

The practical next investigation, if bulk exchange is pursued, is a bounded
compatibility check against an actual sharing Rats peer or a documented export:
which records include the complete verified metadata our catalogue requires,
and how many unique useful records survive validation? The inspected format
already answers that Rats' current searchable dump alone is insufficient for
the complete-metadata requirement. Catalogue import remains a candidate future
capability; this finding does not justify replacing the working public-DHT
crawler or inventing an unsupported peer request.
