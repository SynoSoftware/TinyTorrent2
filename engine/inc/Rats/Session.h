#pragma once

#include "Rats/Snapshot.h"
#include <cstdint>
#include <memory>
#include <optional>
#include <span>
#include <string>
#include <variant>
#include <vector>

namespace tt::rats
{
// The peer's identity once the handshake ends: the SHA-256 of its static
// public key, in lowercase hex.
struct Identified
{
    std::string peerId;
};

// The peer's own statistics, sent on connect and again when they change.
struct Info
{
    std::int64_t torrents = 0;
    // Whether the peer serves its whole index.
    bool sharing = false;
};

// The valid records of one randomTorrents answer, possibly none.
struct Batch
{
    std::vector<Record> records;
};

// The peer is building its snapshot. `total` is the torrent count it will hold.
struct Preparing
{
    std::int64_t processed = 0;
    std::int64_t total = 0;
};

// The snapshot is built and its offer follows.
struct Prepared
{
    std::int64_t bytes = 0;
};

struct Refused
{
    std::string reason;
    std::optional<std::int64_t> retryAfter;
};

// The peer offers its snapshot, and the session has accepted.
struct Offered
{
    std::int64_t bytes = 0;
};

// The next bytes of the `.ratsdb` file, in order. Pass them to a Snapshot.
struct Data
{
    std::vector<char> bytes;
};

// The sender's SHA-256 matched the file. Keep the connection open until the
// last Acknowledge returns, because that call may send the closing COMPLETE.
struct Finished
{
};

// The snapshot transfer ended without a file. The connection stays usable.
struct Failed
{
    std::string reason;
};

using Event = std::variant<Identified, Info, Batch, Preparing, Prepared, Refused, Offered, Data, Finished,
    Failed>;

// The initiator side of one connection to a Rats Search peer over TCP, from the
// Noise handshake to a received snapshot. The session has no socket, timer or
// clock: the host feeds it received bytes and sends the bytes it returns.
//
// Send every returned byte vector in order, none dropped. Each sealed block
// uses the next nonce, so a block that never leaves breaks everything after it.
// Everything the peer sends is untrusted. A protocol violation throws
// std::runtime_error, and the host must then close the connection. A call that
// the current state does not allow throws std::logic_error. Use one thread.
class Session
{
public:
    // Generates fresh keys.
    Session();
    ~Session();
    Session(Session const&) = delete;
    Session& operator=(Session const&) = delete;

    // The first handshake block. Call once, before Receive.
    std::vector<char> Open();
    // Consumes received bytes, appends the bytes to send to `output`, and
    // returns what happened. When the handshake ends, the returned bytes include
    // our client_info, which says we do not share an index.
    std::vector<Event> Receive(std::span<char const> input, std::vector<char>& output);
    // True once the handshake is done.
    bool Ready() const;
    // Asks for up to `limit` random records, clamped to 1..10.
    std::vector<char> RequestBatch(int limit);
    // Asks for the peer's snapshot. Accepts the peer's offer when it comes.
    std::vector<char> RequestSnapshot();
    // Tells the session that the host has stored the first `bytes` bytes of the
    // snapshot, counted from the start. The sender's window is 4 MiB, so the
    // host bounds the memory it uses by acknowledging only what it has stored.
    // Receive throws std::runtime_error when the sender delivers more than the
    // window plus 256 KiB beyond the acknowledged bytes.
    // Sends PROGRESS every 256 KiB and at the end, and COMPLETE once the file
    // is verified and fully acknowledged. Throws std::invalid_argument for more
    // bytes than Data events delivered.
    std::vector<char> Acknowledge(std::int64_t bytes);
    // Abandons the snapshot request or transfer, if there is one.
    std::vector<char> Cancel();

private:
    struct State;
    std::unique_ptr<State> state_;
};
}
