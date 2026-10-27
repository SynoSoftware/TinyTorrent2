#include "Engine/State.h"
#include <libtorrent/hex.hpp>
#include <algorithm>
#include <ctime>
#include <sstream>

namespace tt
{
namespace
{
using Clock = std::chrono::steady_clock;
// A Rats node serves only ten peers, so a client takes few of its places.
constexpr std::size_t ratsConnections = 2;
constexpr auto ratsLookup = std::chrono::minutes(10);
constexpr auto ratsRetry = std::chrono::hours(1);
constexpr std::int64_t ratsSnapshotSeconds = 7 * 24 * 3600;
// How long the host waits for the peer before it gives up on it.
constexpr auto ratsQuiet = std::chrono::seconds(60);
constexpr auto ratsClosing = std::chrono::seconds(5);
// How long a connection may last, so a busy peer cannot keep one of the few
// places. A batch needs the handshake and one answer; a snapshot also waits
// while the peer builds it.
constexpr auto ratsBatchLimit = std::chrono::minutes(2);
constexpr auto ratsSnapshotLimit = std::chrono::minutes(30);
// The most records one connection adds, so a peer cannot fill the catalogue
// with invented torrents in one snapshot.
constexpr std::int64_t ratsRecordLimit = 250000;
constexpr std::size_t ratsCandidateLimit = 1000;
constexpr int ratsBatch = 10;

struct Stored
{
    Catalogue::Receipt receipt;
    std::vector<Catalogue::Claim> rest;
};

struct Decoded
{
    std::vector<Catalogue::Claim> claims;
    std::int64_t consumed = 0;
};

Catalogue::Claim ClaimOf(rats::Record record)
{
    Catalogue::Claim claim{std::move(record.hash), std::move(record.title), record.size, {}, {}};
    claim.files.reserve(record.files.size());
    for (auto& file : record.files)
        claim.files.push_back({std::move(file.path), file.size});
    if (record.checked)
        claim.availability = {record.seeders, record.leechers, *record.checked, 1};
    return claim;
}

// Whether a peer at this address can be reached over the internet. A DHT
// reply names whatever addresses it likes, and a private or reserved one
// would only take a connection place.
bool Public(lt::address_v4 const& address)
{
    struct Range
    {
        std::uint32_t network;
        int bits;
    };
    static constexpr Range reserved[] = {
        {0x00000000, 8},  // this network
        {0x0a000000, 8},  // private
        {0x64400000, 10}, // carrier-grade NAT
        {0x7f000000, 8},  // loopback
        {0xa9fe0000, 16}, // link-local
        {0xac100000, 12}, // private
        {0xc0000000, 24}, // protocol assignments
        {0xc0000200, 24}, // documentation
        {0xc0a80000, 16}, // private
        {0xc6120000, 15}, // benchmarking
        {0xc6336400, 24}, // documentation
        {0xcb007100, 24}, // documentation
        {0xe0000000, 3},  // multicast, reserved and broadcast
    };
    auto value = address.to_uint();
    return std::ranges::none_of(reserved, [value](Range range)
        { return value >> (32 - range.bits) == range.network >> (32 - range.bits); });
}
}

void Engine::State::FoundRats(std::vector<lt::tcp::endpoint> const& peers)
{
    if (!ratsRoute)
        return;
    for (auto const& peer : peers)
    {
        if (peer.address().is_v4() && Public(peer.address().to_v4()) && peer.port() != 0 &&
            ratsCandidates.size() < ratsCandidateLimit)
            ratsCandidates.try_emplace(peer);
    }
}

void Engine::State::ListRats(std::vector<lt::tcp::endpoint> const& peers)
{
    if (!ratsRoute)
        return;
    for (auto const& peer : peers)
        ratsCandidates[peer].listed = true;
}

void Engine::State::StopRats()
{
    ratsPeers.clear();
    ratsRoute.reset();
}

Engine::State::RatsPeer* Engine::State::FindRats(std::uint64_t id)
{
    auto found = std::ranges::find(ratsPeers, id, &RatsPeer::id);
    return found == ratsPeers.end() ? nullptr : &*found;
}

void Engine::State::ContinueRats()
{
    if (!bitmagnet || !ratsRoute)
        return;
    auto now = Clock::now();
    if (now >= ratsLookupAt)
    {
        ratsLookupAt = now + ratsLookup;
        lt::sha1_hash key;
        lt::aux::from_hex(rats::rendezvous, key.data());
        session->dht_get_peers(key);
    }
    for (auto& peer : ratsPeers)
        ContinueRats(peer);
    for (auto const& peer : ratsPeers)
    {
        // An address that never finished the handshake is not a Rats peer, so
        // it gives its place in the list to the next address the DHT finds.
        auto candidate = ratsCandidates.find(peer.endpoint);
        if (!peer.connection && peer.peerId.empty() && candidate != ratsCandidates.end() &&
            !candidate->second.listed)
            ratsCandidates.erase(candidate);
    }
    std::erase_if(ratsPeers, [](RatsPeer const& peer)
    {
        return !peer.connection && !peer.storing && peer.claims.empty() && peer.received.empty() &&
            !(peer.snapshot && peer.snapshot->Pending());
    });
    for (auto candidate = ratsCandidates.begin();
        ratsPeers.size() < ratsConnections && candidate != ratsCandidates.end(); ++candidate)
    {
        if (candidate->second.retry > now)
            continue;
        candidate->second.retry = now + ratsRetry;
        RatsPeer peer;
        peer.id = ++ratsPeer;
        peer.endpoint = candidate->first;
        peer.connection = std::make_unique<rats::Connection>(session->get_context(), ratsRoute, peer.endpoint, wake);
        peer.deadline = now + ratsQuiet;
        peer.expires = now + ratsBatchLimit;
        ratsPeers.push_back(std::move(peer));
    }
}

void Engine::State::ContinueRats(RatsPeer& peer)
{
    using Stage = RatsPeer::Stage;
    if (peer.connection)
    {
        auto update = peer.connection->Drain();
        for (auto& event : update.events)
            OnRats(peer, std::move(event));
        // What arrived before the end is still stored.
        if (update.closed)
        {
            std::ostringstream endpoint;
            endpoint << peer.endpoint;
            log.Write("rats", endpoint.str(), *update.closed);
            peer.connection.reset();
        }
    }
    auto now = Clock::now();
    auto id = peer.id;
    if (peer.storing)
        return;
    if (!peer.claims.empty())
    {
        peer.storing = true;
        indexWork.Run([this, claims = std::exchange(peer.claims, {})]() mutable
        {
            auto receipt = catalogue.Receive(claims);
            claims.erase(claims.begin(), claims.begin() + receipt.next);
            return Stored{receipt, std::move(claims)};
        }, [this, id](std::expected<Stored, StorageOutcome> result)
        {
            auto peer = FindRats(id);
            if (!peer)
                return;
            peer->storing = false;
            if (!result && result.error().failure != StorageFailure::Overloaded)
                indexError = result.error().detail;
            if (result && !result->receipt.full)
            {
                ratsReceived += result->receipt.stored;
                peer->stored += result->receipt.stored;
                peer->claims = std::move(result->rest);
            }
            // A full catalogue, or a peer that has added its share, gets no more.
            if (!result || result->receipt.full || peer->stored >= ratsRecordLimit)
            {
                peer->claims.clear();
                peer->received.clear();
                peer->snapshot.reset();
                EndRats(*peer);
            }
        });
        return;
    }
    if (!peer.received.empty() || (peer.snapshot && peer.snapshot->Pending()))
    {
        peer.storing = true;
        indexWork.Run([snapshot = peer.snapshot, bytes = std::exchange(peer.received, {})]
        {
            snapshot->Append(bytes);
            Decoded decoded;
            for (auto& record : snapshot->Read())
                decoded.claims.push_back(ClaimOf(std::move(record)));
            decoded.consumed = snapshot->Consumed();
            return decoded;
        }, [this, id](std::expected<Decoded, StorageOutcome> result)
        {
            auto peer = FindRats(id);
            if (!peer)
                return;
            peer->storing = false;
            // A peer that sends something other than a snapshot gets no more time.
            if (!result)
            {
                peer->received.clear();
                peer->snapshot.reset();
                EndRats(*peer);
                return;
            }
            peer->decoded = result->consumed;
            peer->claims = std::move(result->claims);
        });
        return;
    }
    if (!peer.connection)
        return;
    if (peer.stage != Stage::Closing && peer.acknowledged < peer.decoded)
    {
        peer.connection->Acknowledge(peer.decoded);
        peer.acknowledged = peer.decoded;
        peer.deadline = now + ratsQuiet;
    }
    // The last Acknowledge may send COMPLETE, and Close sends it before closing.
    if (peer.stage == Stage::Storing)
        EndRats(peer);
    else if (peer.stage == Stage::Closing)
    {
        if (now >= peer.deadline)
            peer.connection.reset();
    }
    else if (now >= peer.deadline || now >= peer.expires)
        EndRats(peer);
}

void Engine::State::OnRats(RatsPeer& peer, rats::Event event)
{
    using Stage = RatsPeer::Stage;
    if (peer.stage == Stage::Closing)
        return;
    // Only an answer to what was asked moves the deadline, so a peer cannot
    // keep the connection with messages it sends unasked.
    auto now = Clock::now();
    if (auto* identified = std::get_if<rats::Identified>(&event))
    {
        // The same peer can answer at two addresses.
        bool connected = std::ranges::any_of(ratsPeers, [&](RatsPeer const& other)
            { return other.id != peer.id && other.peerId == identified->peerId; });
        peer.peerId = std::move(identified->peerId);
        if (connected)
            EndRats(peer);
        else
            peer.deadline = now + ratsQuiet;
    }
    else if (auto* info = std::get_if<rats::Info>(&event))
    {
        if (peer.stage != Stage::Opening)
            return;
        peer.deadline = now + ratsQuiet;
        // The catalogue's sync record is keyed by the peer's own key, which it
        // can change at will; its address it cannot.
        auto requested = ratsSnapshots.find(peer.endpoint.address());
        bool recent = requested != ratsSnapshots.end() &&
            now - requested->second < std::chrono::seconds(ratsSnapshotSeconds);
        if (!info->sharing || recent)
        {
            peer.connection->RequestBatch(ratsBatch);
            peer.stage = Stage::Batch;
            return;
        }
        peer.stage = Stage::Deciding;
        indexWork.Run([this, peerId = peer.peerId] { return catalogue.ReadSync(peerId); },
            [this, id = peer.id](std::expected<std::optional<Catalogue::Sync>, StorageOutcome> sync)
        {
            auto peer = FindRats(id);
            if (!peer || !peer->connection || peer->stage != Stage::Deciding)
                return;
            auto now = Clock::now();
            if (sync && (!*sync || std::time(nullptr) - (*sync)->requested >= ratsSnapshotSeconds))
            {
                peer->connection->RequestSnapshot();
                peer->stage = Stage::Snapshot;
                peer->expires = now + ratsSnapshotLimit;
                std::erase_if(ratsSnapshots, [now](auto const& entry)
                    { return now - entry.second >= std::chrono::seconds(ratsSnapshotSeconds); });
                ratsSnapshots[peer->endpoint.address()] = now;
            }
            else
            {
                peer->connection->RequestBatch(ratsBatch);
                peer->stage = Stage::Batch;
            }
            peer->deadline = now + ratsQuiet;
        });
    }
    else if (auto* batch = std::get_if<rats::Batch>(&event))
    {
        if (peer.stage != Stage::Batch)
            return;
        for (auto& record : batch->records)
            peer.claims.push_back(ClaimOf(std::move(record)));
        peer.stage = Stage::Storing;
    }
    else if (std::holds_alternative<rats::Preparing>(event) || std::holds_alternative<rats::Prepared>(event))
    {
        if (peer.stage == Stage::Snapshot)
            peer.deadline = now + ratsQuiet;
    }
    else if (std::holds_alternative<rats::Refused>(event) && peer.stage == Stage::Snapshot)
    {
        // A prompt refusal, such as a busy peer's, uses none of the address's
        // week. A late one may follow a long wait while the peer claimed to
        // prepare, so it counts.
        auto requested = ratsSnapshots.find(peer.endpoint.address());
        if (requested != ratsSnapshots.end() && now - requested->second < ratsQuiet)
            ratsSnapshots.erase(requested);
        peer.connection->RequestBatch(ratsBatch);
        peer.stage = Stage::Batch;
        peer.deadline = now + ratsQuiet;
        peer.expires = now + ratsBatchLimit;
    }
    else if (std::holds_alternative<rats::Offered>(event))
    {
        peer.snapshot = std::make_shared<rats::Snapshot>();
        peer.deadline = now + ratsQuiet;
        // A transfer that fails is not tried again for a week either, so a
        // peer with a broken snapshot is not downloaded every hour.
        indexWork.Run([this, sync = Catalogue::Sync{peer.peerId, std::time(nullptr)}] { catalogue.SaveSync(sync); },
            [this](StorageOutcome outcome) { if (outcome.failure) indexError = outcome.detail; });
    }
    else if (auto* data = std::get_if<rats::Data>(&event))
    {
        peer.received.insert(peer.received.end(), data->bytes.begin(), data->bytes.end());
        peer.deadline = now + ratsQuiet;
    }
    else if (std::holds_alternative<rats::Finished>(event))
        peer.stage = Stage::Storing;
    else if (std::holds_alternative<rats::Failed>(event))
        EndRats(peer);
}

void Engine::State::EndRats(RatsPeer& peer)
{
    if (!peer.connection || peer.stage == RatsPeer::Stage::Closing)
        return;
    if (peer.stage == RatsPeer::Stage::Snapshot)
        peer.connection->Cancel();
    peer.connection->Close();
    peer.stage = RatsPeer::Stage::Closing;
    peer.deadline = Clock::now() + ratsClosing;
}

Json Engine::State::RatsStatus() const
{
    return {{"peers", ratsCandidates.size()}, {"connections", ratsPeers.size()}, {"received", ratsReceived}};
}
}
