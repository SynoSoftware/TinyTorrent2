#pragma once

#include "Dialer.h"
#include "Rats/Session.h"
#include <libtorrent/io_context.hpp>
#include <libtorrent/socket.hpp>
#include <cstdint>
#include <functional>
#include <memory>
#include <optional>
#include <string>
#include <vector>

namespace tt::rats
{
// The DHT key that Rats Search peers announce themselves under: the SHA-1 of
// "rats-search/4", in hex.
inline constexpr char rendezvous[] = "278903a7e96d10ceec7ddefb8b9bcdd36305346c";

// One connection to a Rats Search peer. It dials through the saved route and
// runs a Session on the libtorrent network thread; the owner thread reads its
// events and decides what to ask for.
class Connection
{
public:
    struct Update
    {
        std::vector<Event> events;
        // Set once the connection has ended; no event follows it.
        std::optional<std::string> closed;
    };

    Connection(libtorrent::io_context& context, std::shared_ptr<Dialer::Route const> route,
        libtorrent::tcp::endpoint peer, std::function<void()> wake);
    // Ends the connection at once, without sending what is still queued.
    ~Connection();
    Connection(Connection const&) = delete;
    Connection& operator=(Connection const&) = delete;

    // Owner-thread calls. Each runs on the network thread in call order, and
    // a call the Session refuses ends the connection.
    void RequestBatch(int limit);
    void RequestSnapshot();
    void Acknowledge(std::int64_t bytes);
    void Cancel();
    // Ends the connection gracefully after the queued bytes are sent, so a
    // COMPLETE that the last Acknowledge produced reaches the peer. The
    // connection ends when the peer closes its side; destroy it to stop
    // waiting.
    void Close();
    Update Drain();

private:
    struct State;
    std::shared_ptr<State> state_;
};
}
