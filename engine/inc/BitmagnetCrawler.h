#pragma once

#include "Catalogue.h"
#include "MetadataRequester.h"
#include <libtorrent/bdecode.hpp>
#include <libtorrent/entry.hpp>
#include <array>
#include <chrono>
#include <cstdint>
#include <functional>
#include <memory>
#include <string>
#include <vector>

namespace tt
{
class BitmagnetCrawler
{
public:
    struct Config
    {
        unsigned scalingFactor = 10;
        std::vector<std::string> bootstrapNodes{
            "router.utorrent.com:6881", "router.bittorrent.com:6881",
            "dht.transmissionbt.com:6881", "dht.aelitis.com:6881",
            "router.silotis.us:6881", "dht.libtorrent.org:25401"};
        std::chrono::milliseconds reseedBootstrapNodesInterval{60000};
        unsigned saveFilesThreshold = 100;
        bool savePieces = false;
        std::chrono::milliseconds rescrapeThreshold{2592000000LL};
        std::chrono::milliseconds queryTimeout{4000};
    };

    struct Candidate
    {
        std::string hash;
        Catalogue::Endpoint source;
    };

    struct Acquired
    {
        Candidate candidate;
        std::vector<char> info;
    };

    struct Selection
    {
        std::vector<Candidate> missing;
        std::vector<Candidate> scrape;
    };

    struct Observation
    {
        Candidate candidate;
        std::array<std::uint8_t, 256> seeds;
        std::array<std::uint8_t, 256> peers;
    };

    struct Statistics
    {
        std::uint64_t discovered = 0;
        std::uint64_t selected = 0;
        std::uint64_t peerQueries = 0;
        std::uint64_t peerPositive = 0;
        std::uint64_t metadataRequests = 0;
        std::uint64_t metadataReceived = 0;
        std::uint64_t metadataFailed = 0;
        std::uint64_t metadataBytes = 0;
        std::uint64_t connections = 0;
        std::uint64_t connected = 0;
        std::uint64_t metadataSupported = 0;
        std::array<std::uint64_t, 8> failures{};
        std::array<std::uint64_t, 6> latencies{};
        std::uint64_t limiterMicros = 0;
        std::uint64_t limiterMaxMicros = 0;
        std::size_t fetching = 0;
        std::size_t probing = 0;
        std::size_t fetchingLimit = 0;
        std::size_t probingLimit = 0;
        std::size_t peakFetching = 0;
        std::size_t peakProbing = 0;
        std::size_t queued = 0;
        std::size_t nodes = 0;
    };

    struct Callbacks
    {
        std::function<void(std::uintptr_t, libtorrent::udp::endpoint, libtorrent::entry)> send;
        std::function<void(std::vector<Candidate>, std::function<void(Selection)>)> triage;
        std::function<void(std::vector<Acquired>, std::function<void()>)> persist;
        std::function<void(std::vector<std::string>, Catalogue::Endpoint)> discover;
        std::function<void(std::vector<Catalogue::Node>)> nodes;
        std::function<void(std::vector<Observation>)> observe;
        std::function<void(std::vector<std::string>)> bootstrap;
    };

    // Call on the engine actor; callbacks and their completions use that actor too.
    BitmagnetCrawler(Config config, MetadataRequester& requester, Callbacks callbacks,
        libtorrent::sha1_hash origin);
    ~BitmagnetCrawler();
    BitmagnetCrawler(BitmagnetCrawler const&) = delete;
    BitmagnetCrawler& operator=(BitmagnetCrawler const&) = delete;

    void Tick();
    void Seed(std::vector<Catalogue::Node> nodes);
    void Response(std::uintptr_t requestId, libtorrent::bdecode_node response);
    void Stop();
    Statistics Stats() const;

private:
    struct State;
    std::shared_ptr<State> state_;
};
}
