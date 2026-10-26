#pragma once

#include "Enums.h"
#include <libtorrent/io_context.hpp>
#include <libtorrent/sha1_hash.hpp>
#include <libtorrent/socket.hpp>
#include <chrono>
#include <cstdint>
#include <functional>
#include <memory>
#include <optional>
#include <string>
#include <vector>

namespace tt
{
class MetadataRequester
{
public:
    struct Config
    {
        std::chrono::milliseconds requestTimeout{6000};
        int keyMutexSize = 1000;
    };

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

    struct Attempt
    {
        libtorrent::tcp::endpoint peer;
        std::optional<MetadataStage> failure;
        int error = 0;
        bool connected = false;
        bool metadataSupported = false;
        std::chrono::microseconds waiting{};
        std::chrono::microseconds elapsed{};
        std::uint64_t received = 0;
    };

    struct Result
    {
        std::uintptr_t requestId = 0;
        libtorrent::sha1_hash hash;
        // SHA-1-verified BEP 9 bytes; the catalogue worker parses the info dictionary.
        std::vector<char> info;
        std::vector<Attempt> attempts;
        std::optional<MetadataStage> failure;
        std::chrono::microseconds elapsed{};
    };

    MetadataRequester(libtorrent::io_context& context, Config config, Route route,
        std::function<void()> wake);
    ~MetadataRequester();
    MetadataRequester(MetadataRequester const&) = delete;
    MetadataRequester& operator=(MetadataRequester const&) = delete;

    // Owner-thread calls. The session context outlives this requester; Cancel
    // permanently stops it, and Active reaches zero after cancellation drains.
    void Request(std::uintptr_t requestId, libtorrent::sha1_hash hash,
        std::vector<libtorrent::tcp::endpoint> peers);
    void Cancel();
    std::vector<Result> Drain();
    std::size_t Active() const;

private:
    struct State;
    std::shared_ptr<State> state_;
};
}
