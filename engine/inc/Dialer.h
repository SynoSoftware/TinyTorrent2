#pragma once

#include "Enums.h"
#include <libtorrent/io_context.hpp>
#include <libtorrent/socket.hpp>
#include <boost/asio/steady_timer.hpp>
#include <chrono>
#include <functional>
#include <memory>
#include <optional>
#include <string>
#include <vector>

namespace tt
{
// Opens one outgoing TCP connection through the saved proxy and network
// adapter, so every engine-owned peer connection follows the confirmed route.
// Run every call on the context's thread.
class Dialer : public std::enable_shared_from_this<Dialer>
{
public:
    struct Adapter
    {
        unsigned ipv4Index = 0;
        unsigned ipv6Index = 0;
        std::vector<std::string> addresses;
    };

    struct Route
    {
        ProxyType proxy = ProxyType::None;
        std::vector<libtorrent::tcp::endpoint> endpoints;
        std::string username;
        std::string password;
        std::optional<Adapter> adapter;
        bool allowed = true;
    };

    enum class Failure
    {
        // The route refused this connection: no usable proxy or adapter address.
        Route,
        // The peer or the proxy did not connect or refused the tunnel.
        Connect,
    };

    // `error` is timed_out when the deadline ended the attempt.
    using Done = std::function<void(std::optional<Failure> failure, libtorrent::error_code error)>;

    // False when the route permits no connection at all, such as a proxy whose
    // address did not resolve.
    static bool Usable(Route const& route);

    Dialer(libtorrent::io_context& context, std::shared_ptr<Route const> route);

    // Connects to the IPv4 `peer` before `deadline`, trying each proxy endpoint
    // in turn. `done` runs once, unless Close runs first.
    void Connect(libtorrent::tcp::endpoint peer, std::chrono::steady_clock::time_point deadline, Done done);
    // Abandons the attempt without calling `done`.
    void Close();
    // The connected socket, valid after `done` reported success.
    libtorrent::tcp::socket& Socket();

private:
    bool Bind(libtorrent::tcp::endpoint const& physical, libtorrent::error_code& error);
    void Dial();
    void Retry(Failure failure, libtorrent::error_code error);
    void Tunnel();
    void Http();
    void Socks();
    void Write(std::vector<char> bytes, std::function<void()> next);
    void Read(std::size_t size, std::function<void()> next);
    void Finish(std::optional<Failure> failure, libtorrent::error_code error = {});

    std::shared_ptr<Route const> route_;
    libtorrent::tcp::socket socket_;
    boost::asio::steady_timer timer_;
    libtorrent::tcp::endpoint peer_;
    std::chrono::steady_clock::time_point deadline_;
    Done done_;
    std::size_t physical_ = 0;
    std::uint64_t attempt_ = 0;
    bool expired_ = false;
    bool closed_ = false;
    std::vector<char> frame_;
    std::string response_;
};
}
