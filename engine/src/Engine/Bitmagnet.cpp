#include "Engine/State.h"
#include "Strings.h"
#include <libtorrent/bdecode.hpp>
#include <libtorrent/hex.hpp>
#include <libtorrent/load_torrent.hpp>
#include <libtorrent/torrent_info.hpp>
#include <algorithm>
#include <charconv>
#include <cmath>
#include <cstdlib>
#include <ctime>
#include <limits>
#include <stdexcept>

namespace tt
{
namespace
{
std::chrono::milliseconds Duration(Json const& object, char const* key,
    std::chrono::milliseconds fallback)
{
    if (!object.contains(key)) return fallback;
    auto text = object.at(key).get<std::string>();
    double total = 0;
    auto next = text.c_str();
    while (*next)
    {
        char* end = nullptr;
        auto number = std::strtod(next, &end);
        if (end == next || number < 0 || !std::isfinite(number))
            throw std::invalid_argument("Invalid crawler duration");
        std::string_view units(end);
        double scale;
        if (units.starts_with("ms")) { scale = 1; next = end + 2; }
        else if (units.starts_with("s")) { scale = 1000; next = end + 1; }
        else if (units.starts_with("m")) { scale = 60000; next = end + 1; }
        else if (units.starts_with("h")) { scale = 3600000; next = end + 1; }
        else throw std::invalid_argument("Invalid crawler duration unit");
        total += number * scale;
    }
    if (!std::isfinite(total) || total < 1 || total > 315360000000.0)
        throw std::invalid_argument("Crawler duration out of range");
    return std::chrono::milliseconds(static_cast<std::int64_t>(total));
}

struct Configuration
{
    BitmagnetCrawler::Config crawler;
    MetadataRequester::Config requester;
    // Rats Search peers to try besides those the DHT finds.
    std::vector<lt::tcp::endpoint> ratsPeers;
    Json resolved;
};

// The port after the last colon of "host:port", or nothing when there is no
// host or the port is not 1 to 65535.
std::optional<unsigned short> Port(std::string const& node)
{
    auto split = node.rfind(':');
    if (split == std::string::npos || split == 0)
        return std::nullopt;
    int port = 0;
    auto first = node.data() + split + 1;
    auto last = node.data() + node.size();
    auto parsed = std::from_chars(first, last, port);
    if (parsed.ec != std::errc{} || parsed.ptr != last || port < 1 || port > 65535)
        return std::nullopt;
    return static_cast<unsigned short>(port);
}

Configuration ConfigurationFor(Json const& input)
{
    Configuration result;
    auto crawler = input.value("dht_crawler", Json::object());
    auto requester = input.value("metainfo_requester", Json::object());
    auto server = input.value("dht_server", Json::object());
    auto rats = input.value("rats", Json::object());
    auto& c = result.crawler;
    c.scalingFactor = crawler.value("scaling_factor", c.scalingFactor);
    if (c.scalingFactor == 0 || c.scalingFactor > 1000)
        throw std::invalid_argument("Invalid Bitmagnet scaling_factor");
    c.bootstrapNodes = crawler.value("bootstrap_nodes", c.bootstrapNodes);
    for (auto const& node : c.bootstrapNodes)
    {
        if (!Port(node))
            throw std::invalid_argument("Invalid Bitmagnet bootstrap node");
    }
    auto ratsPeers = rats.value("peers", std::vector<std::string>{});
    for (auto const& peer : ratsPeers)
    {
        lt::error_code error;
        auto port = Port(peer);
        auto address = port ? lt::make_address(peer.substr(0, peer.rfind(':')), error) : lt::address{};
        if (!port || error || !address.is_v4())
            throw std::invalid_argument("Invalid Rats peer");
        result.ratsPeers.emplace_back(address, *port);
    }
    c.reseedBootstrapNodesInterval = Duration(crawler, "reseed_bootstrap_nodes_interval", c.reseedBootstrapNodesInterval);
    c.saveFilesThreshold = crawler.value("save_files_threshold", c.saveFilesThreshold);
    c.savePieces = crawler.value("save_pieces", c.savePieces);
    c.rescrapeThreshold = Duration(crawler, "rescrape_threshold", c.rescrapeThreshold);
    c.queryTimeout = Duration(server, "query_timeout", c.queryTimeout);
    result.requester.requestTimeout = Duration(requester, "request_timeout", result.requester.requestTimeout);
    result.requester.keyMutexSize = requester.value("key_mutex_size", result.requester.keyMutexSize);
    if (result.requester.keyMutexSize < 1)
        throw std::invalid_argument("Invalid Bitmagnet key_mutex_size");
    auto duration = [](auto value) { return std::to_string(value.count()) + "ms"; };
    result.resolved = {
        {"dht_crawler", {{"scaling_factor", c.scalingFactor}, {"bootstrap_nodes", c.bootstrapNodes},
            {"reseed_bootstrap_nodes_interval", duration(c.reseedBootstrapNodesInterval)},
            {"save_files_threshold", c.saveFilesThreshold}, {"save_pieces", c.savePieces},
            {"rescrape_threshold", duration(c.rescrapeThreshold)}}},
        {"metainfo_requester", {{"request_timeout", duration(result.requester.requestTimeout)},
            {"key_mutex_size", result.requester.keyMutexSize}}},
        {"dht_server", {{"port", server.value("port", 3334)}, {"query_timeout", duration(c.queryTimeout)}}},
        {"rats", {{"peers", ratsPeers}}},
        {"adapters", {{"dht_socket", "shared_session"}, {"retention", "all_files_and_exact_info"},
            {"metadata_transport", "plaintext_tcp4"},
            {"unused_reference_keys", {"dht_server.port", "dht_crawler.save_files_threshold", "dht_crawler.save_pieces",
                "dht_crawler.reseed_bootstrap_nodes_interval", "metainfo_requester.key_mutex_size"}}}}};
    return result;
}

}

void Engine::State::StopBitmagnet()
{
    StopRats();
    if (!bitmagnet && !metadataRequester && !bitmagnetStarting) return;
    ++bitmagnetVersion;
    bitmagnetStarting = false;
    crawlerRequests.clear();
    if (bitmagnet)
    {
        bitmagnet->Stop();
        bitmagnetStatistics = bitmagnet->Stats();
    }
    if (metadataRequester) metadataRequester->Cancel();
    bitmagnet.reset();
    metadataRequester.reset();
}

void Engine::State::ContinueBitmagnet()
{
    if (!session || !indexReady || !indexConfigured || !settings.indexing || !settings.dht ||
        indexRebuilding || IsShuttingDown() || Paused() || crawlerSaveFailed)
    {
        StopBitmagnet();
        return;
    }
    auto now = std::chrono::steady_clock::now();
    std::erase_if(crawlerRequests, [now](auto const& request) { return request.second.deadline <= now; });
    if (now - nodeRefreshAt >= std::chrono::seconds(crawlerNodeId ? 30 : 1))
    {
        nodeRefreshAt = now;
        session->post_dht_stats();
    }
    if (!crawlerNodeId) return;
    if (bitmagnet) { bitmagnet->Tick(); return; }
    if (bitmagnetStarting) return;
    if (settings.encryption == Encryption::Required || settings.transport == Transport::Utp ||
        (applied && (applied->connection.family == IpFamily::Ipv6 || applied->rates.download || applied->rates.upload)))
    {
        indexError = Utf8(Strings(language).Text("error", "crawler_unavailable"));
        return;
    }
    Dialer::Route route;
    std::optional<NetworkAdapter> adapter;
    if (!settings.networkAdapter.empty())
    {
        adapter = FindAdapter(settings.networkAdapter);
        if (!adapter) return;
        route.adapter = Dialer::Adapter{adapter->ipv4Index, adapter->ipv6Index, adapter->addresses};
    }
    route.proxy = settings.proxy.type;
    route.username = settings.proxy.username;
    route.password = settings.proxy.password;
    if (route.proxy == ProxyType::None) { StartBitmagnet(std::move(route)); return; }
    bitmagnetStarting = true;
    auto version = bitmagnetVersion;
    auto proxy = settings.proxy;
    auto stop = checkStop.get_token();
    checks.Run([proxy, adapter, stop] { return ResolveProxy(proxy, adapter, stop); },
        [this, version, route = std::move(route)](std::expected<std::vector<lt::tcp::endpoint>, StorageOutcome> result) mutable
    {
        if (version != bitmagnetVersion) return;
        bitmagnetStarting = false;
        if (!result || result->empty())
        {
            indexError = result ? Utf8(Strings(language).Text("error", "crawler_proxy_unavailable")) : result.error().detail;
            return;
        }
        route.endpoints = std::move(*result);
        if (settings.indexing && !Paused() && !IsShuttingDown()) StartBitmagnet(std::move(route));
    });
}

void Engine::State::StartBitmagnet(Dialer::Route route)
{
    try
    {
        auto configuration = ConfigurationFor(crawlerConfig);
        configuration.resolved["adapters"]["reference_metadata_workers"] = 40 * configuration.crawler.scalingFactor;
        configuration.resolved["adapters"]["metadata_connection_budget"] = "independent_reference";
        configuration.resolved["adapters"]["shared_transfer_connection_limit"] = settings.connections;
        configuration.resolved["adapters"]["shared_transfer_outgoing_rate"] = settings.outgoingRate;
        crawlerConfig = configuration.resolved;
        auto version = bitmagnetVersion;
        ratsRoute = std::make_shared<Dialer::Route const>(route);
        ListRats(configuration.ratsPeers);
        metadataRequester = std::make_unique<MetadataRequester>(session->get_context(),
            configuration.requester, std::move(route), wake);
        BitmagnetCrawler::Callbacks callbacks;
        callbacks.send = [this, timeout = configuration.crawler.queryTimeout]
            (std::uintptr_t request, lt::udp::endpoint endpoint, lt::entry packet)
        {
            auto cookie = ++crawlerRequest;
            crawlerRequests.emplace(cookie, CrawlerRequest{request, std::chrono::steady_clock::now() + timeout});
            session->dht_direct_request(endpoint, packet,
                lt::client_data_t(reinterpret_cast<BitmagnetCrawler*>(cookie)));
        };
        callbacks.bootstrap = [this](std::vector<std::string> nodes)
        {
            for (auto const& node : nodes)
            {
                auto split = node.rfind(':');
                if (split == std::string::npos) continue;
                session->add_dht_node({node.substr(0, split), std::stoi(node.substr(split + 1))});
            }
            session->post_dht_stats();
        };
        callbacks.search = [this](std::string const& hash)
        {
            lt::sha1_hash target;
            lt::aux::from_hex(hash, target.data());
            session->dht_get_peers(target);
        };
        callbacks.nodes = [this](std::vector<Catalogue::Node> nodes)
        {
            crawlerNodes.insert(crawlerNodes.end(), std::make_move_iterator(nodes.begin()), std::make_move_iterator(nodes.end()));
        };
        callbacks.discover = [this](std::vector<std::string> hashes, Catalogue::Endpoint source)
        {
            indexSampled += hashes.size();
            for (auto& hash : hashes)
                crawlerDiscoveries.push_back({std::move(hash), source});
        };
        auto rescrape = std::chrono::duration_cast<std::chrono::seconds>(configuration.crawler.rescrapeThreshold).count();
        callbacks.triage = [this, version, rescrape](std::vector<BitmagnetCrawler::Candidate> candidates,
            std::function<void(BitmagnetCrawler::Selection)> complete)
        {
            std::vector<Catalogue::Pending> pending;
            std::map<std::string, BitmagnetCrawler::Candidate> routes;
            for (auto& candidate : candidates)
            {
                pending.push_back({candidate.hash});
                routes.emplace(candidate.hash, std::move(candidate));
            }
            indexWork.Run([this, pending = std::move(pending), rescrape]
                { return catalogue.Triage(pending, std::time(nullptr), rescrape); },
                [this, version, routes = std::move(routes), complete = std::move(complete)]
                (std::expected<Catalogue::Selection, StorageOutcome> selected) mutable
            {
                BitmagnetCrawler::Selection result;
                if (selected && version == bitmagnetVersion)
                {
                    for (auto const& pending : selected->missing)
                        result.missing.push_back(std::move(routes.at(pending.lookup)));
                    for (auto const& pending : selected->scrape)
                        result.scrape.push_back(std::move(routes.at(pending.lookup)));
                }
                else if (!selected) indexError = selected.error().detail;
                complete(std::move(result));
            });
        };
        callbacks.persist = [this](std::vector<BitmagnetCrawler::Acquired> acquired, std::function<void()> complete)
        {
            crawlerBatches.push_back(std::make_shared<CrawlerBatch>(
                CrawlerBatch{std::move(acquired), 0, std::move(complete)}));
        };
        callbacks.observe = [this](std::vector<BitmagnetCrawler::Observation> observations)
        {
            indexWork.Run([this, observations = std::move(observations)]
            {
                std::vector<Catalogue::Observed> batch;
                batch.reserve(observations.size());
                for (auto const& value : observations)
                    batch.push_back({value.candidate.hash,
                        {Estimate(value.seeds), Estimate(value.peers), std::time(nullptr), 1}});
                catalogue.Observe(batch);
            },
                [this](StorageOutcome outcome) { if (outcome.failure) indexError = outcome.detail; });
        };
        bitmagnet = std::make_unique<BitmagnetCrawler>(std::move(configuration.crawler),
            *metadataRequester, std::move(callbacks), *crawlerNodeId);
        indexWork.Run([this] { return catalogue.Nodes(128, (std::numeric_limits<std::int64_t>::max)()); },
            [this, version](std::expected<std::vector<Catalogue::Node>, StorageOutcome> nodes)
        {
            if (nodes && version == bitmagnetVersion && bitmagnet) bitmagnet->Seed(std::move(*nodes));
        });
        indexError.clear();
    }
    catch (std::exception const&)
    {
        StopBitmagnet();
        indexError = Utf8(Strings(language).Text("error", "crawler_config_invalid"));
    }
}

void Engine::State::SaveCrawler()
{
    if (crawlerSaving || crawlerSaveFailed || crawlerBatches.empty()) return;
    auto batch = crawlerBatches.front();
    if (batch->next == batch->values.size())
    {
        crawlerBatches.pop_front();
        batch->complete();
        return;
    }
    struct Progress
    {
        std::size_t processed = 0;
        std::uint64_t valid = 0, retained = 0, invalid = 0;
    };
    auto progress = std::make_shared<Progress>();
    crawlerSaving = true;
    indexWork.Run([this, batch, progress]
    {
        auto end = std::min(batch->next + 8, batch->values.size());
        for (auto index = batch->next; index < end; ++index)
        {
            auto const& value = batch->values[index];
            lt::add_torrent_params params;
            lt::error_code error;
            lt::load_torrent_limits limits;
            limits.max_buffer_size = 10 * 1024 * 1024;
            limits.max_decode_depth = 200;
            auto info = lt::bdecode(lt::span<char const>(value.info.data(), value.info.size()),
                error, nullptr, limits.max_decode_depth, limits.max_decode_tokens);
            if (!error)
                params.ti = std::make_shared<lt::torrent_info>(info, error, limits, lt::from_info_section);
            if (error || !params.ti || !params.ti->info_hashes().has_v1() ||
                lt::aux::to_hex(params.ti->info_hashes().v1.to_string()) != value.candidate.hash)
                ++progress->invalid;
            else
            {
                if (catalogue.Admit(params, value.candidate.hash, {}, true).accepted) ++progress->retained;
                ++progress->valid;
            }
            ++progress->processed;
        }
    }, [this, batch, progress](StorageOutcome outcome)
    {
        crawlerSaving = false;
        batch->next += progress->processed;
        bitmagnetValidated += progress->valid;
        bitmagnetRetained += progress->retained;
        bitmagnetInvalid += progress->invalid;
        if (outcome.failure && outcome.failure != StorageFailure::Overloaded)
        {
            crawlerSaveFailed = true;
            StopBitmagnet();
            indexError = Utf8(Strings(language).Text("error", "crawler_storage_failed"));
        }
    });
}

Json Engine::State::BitmagnetStatus() const
{
    auto stats = bitmagnet ? bitmagnet->Stats() : bitmagnetStatistics;
    return {{"discovered", stats.discovered}, {"selected", stats.selected},
        {"peer_queries", stats.peerQueries}, {"peer_positive", stats.peerPositive},
        {"metadata_requests", stats.metadataRequests}, {"metadata_received", stats.metadataReceived},
        {"metadata_failed", stats.metadataFailed}, {"metadata_bytes", stats.metadataBytes},
        {"connections_attempted", stats.connections}, {"connections_established", stats.connected},
        {"metadata_supported", stats.metadataSupported}, {"validated", bitmagnetValidated},
        {"retained", bitmagnetRetained}, {"invalid", bitmagnetInvalid},
        {"fetching", stats.fetching}, {"probing", stats.probing}, {"queued", stats.queued}, {"nodes", stats.nodes},
        {"fetching_limit", stats.fetchingLimit}, {"probing_limit", stats.probingLimit},
        {"failure_stages", stats.failures}, {"attempt_latency_buckets", stats.latencies},
        {"limiter_wait_us", stats.limiterMicros}, {"limiter_max_wait_us", stats.limiterMaxMicros},
        {"jobs_by_route", stats.jobsByRoute}, {"received_by_route", stats.receivedByRoute},
        {"requests_heard", stats.requestsHeard}, {"announces_heard", stats.announcesHeard},
        {"searches", stats.searches}, {"searching", stats.searching},
        {"peak_fetching", stats.peakFetching}, {"peak_probing", stats.peakProbing},
        {"persistence_batches", crawlerBatches.size()}, {"storage_failed", crawlerSaveFailed}};
}
}
