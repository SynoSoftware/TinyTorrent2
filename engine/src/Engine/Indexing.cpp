#include "Engine/State.h"
#include "Strings.h"
#include <libtorrent/bdecode.hpp>
#include <libtorrent/entry.hpp>
#include <libtorrent/hex.hpp>
#include <libtorrent/load_torrent.hpp>
#include <libtorrent/read_resume_data.hpp>
#include <algorithm>
#include <bit>
#include <cmath>
#include <limits>
#include <stdexcept>

namespace tt
{
namespace
{
struct PreparedSource
{
    lt::add_torrent_params params;
    Json selection;
};

std::int64_t Now()
{
    return std::chrono::system_clock::to_time_t(std::chrono::system_clock::now());
}

std::string Hex(lt::sha1_hash const& hash)
{
    return lt::aux::to_hex(hash.to_string());
}

bool IsHash(std::string const& hash)
{
    return (hash.size() == 40 || hash.size() == 64) &&
        hash.find_first_not_of("0123456789abcdef") == std::string::npos;
}

lt::sha1_hash Lookup(std::string const& hash)
{
    if (!IsHash(hash))
        throw std::invalid_argument("Invalid catalogue identity");
    lt::sha1_hash result;
    lt::aux::from_hex(std::string_view(hash).substr(0, 40), result.data());
    return result;
}

template <typename T>
Json Optional(std::optional<T> const& value)
{
    return value ? Json(*value) : Json(nullptr);
}

Json File(Catalogue::File const& file)
{
    return {{"index", file.index}, {"name", file.name}, {"path", file.path},
        {"size", file.size}, {"padding", file.padding}, {"symlink", file.symlink}, {"root", file.root}};
}

Json Observation(Catalogue::Observation const& observation)
{
    return {{"seeds", Optional(observation.seeds)}, {"peers", Optional(observation.peers)},
        {"observed", observation.observed}, {"responses", observation.responses}};
}

Json Source(Catalogue::Source const& source)
{
    return {{"hash", source.hash}, {"v1", Optional(source.v1)}, {"v2", Optional(source.v2)},
        {"name", source.name}, {"size", source.size}, {"count", source.count},
        {"admitted", source.admitted}, {"seen", Optional(source.seen)},
        {"availability", Observation(source.availability)}, {"verified", source.verified},
        {"file", File(source.file)}};
}

Json EncodeStatus(Catalogue::Status const& status)
{
    return {{"exists", status.exists}, {"torrents", status.torrents}, {"files", status.files},
        {"rejected", status.rejected}, {"pending", status.pending}, {"allocated", status.allocated},
        {"ready", status.ready}, {"nodes", status.nodes},
        {"reusable", status.reusable}, {"rebuilding", status.rebuilding}, {"rebuilt", status.rebuilt},
        {"projecting", status.projecting},
        {"generation", status.generation}};
}

Json EncodeRemoval(Catalogue::Removal const& removal)
{
    return {{"token", removal.token}, {"torrents", removal.torrents}, {"files", removal.files},
        {"bytes", removal.bytes}, {"removed", removal.removed}, {"more", removal.more}};
}

Json Text(Catalogue::Text const& text)
{
    return {{"value", text.value}, {"exclude", text.exclude}};
}

Json Range(Catalogue::Range const& range)
{
    return {{"minimum", Optional(range.minimum)}, {"maximum", Optional(range.maximum)}};
}

Catalogue::Range ReadRange(Json const& value)
{
    if (!value.is_object())
        throw std::invalid_argument("Invalid range");
    Catalogue::Range range;
    if (auto bound = value.find("minimum"); bound != value.end() && !bound->is_null())
        range.minimum = bound->get<std::int64_t>();
    if (auto bound = value.find("maximum"); bound != value.end() && !bound->is_null())
        range.maximum = bound->get<std::int64_t>();
    if ((range.minimum && *range.minimum < 0) || (range.maximum && *range.maximum < 0) ||
        (range.minimum && range.maximum && *range.minimum > *range.maximum))
        throw std::invalid_argument("Invalid range bounds");
    return range;
}

Catalogue::Text ReadText(Json const& value)
{
    if (!value.is_object())
        throw std::invalid_argument("Invalid text condition");
    Catalogue::Text text{value.value("value", std::string()), value.value("exclude", false)};
    if (text.value.size() > 4096)
        throw std::invalid_argument("Text condition too long");
    return text;
}

template <typename Value>
std::function<void(std::expected<Value, StorageOutcome>)> ReplyWith(Reply reply,
    std::function<Json(Value const&)> encode)
{
    return [reply = std::move(reply), encode = std::move(encode)](std::expected<Value, StorageOutcome> result)
    {
        if (!result)
        {
            auto error = result.error().ToOutcome(ErrorCode::StorageFailed);
            reply(Failure(*error.error, error.detail));
        }
        else
            reply(Success(encode(*result)));
    };
}

lt::udp::endpoint Endpoint(Catalogue::Node const& node)
{
    return {lt::make_address(node.address), static_cast<unsigned short>(node.port)};
}

}

std::optional<std::int64_t> Engine::State::Estimate(std::span<unsigned char const> bloom)
{
    if (bloom.size() != 256)
        return std::nullopt;
    unsigned bits = 0;
    for (auto byte : bloom)
        bits += std::popcount(byte);
    if (bits == 2048)
        return std::nullopt;
    auto estimate = std::log1p(-double(bits) / 2048.0) /
        (2.0 * std::log1p(-1.0 / 2048.0));
    return static_cast<std::int64_t>(std::llround(estimate));
}
Json Engine::State::IndexRules(std::vector<Catalogue::Rule> const& rules)
{
    auto result = Json::array();
    for (auto const& rule : rules)
        result.push_back({{"rule_id", rule.ruleId}, {"name", rule.name}, {"enabled", rule.enabled},
            {"title", Text(rule.title)}, {"size", Range(rule.size)}, {"count", Range(rule.count)},
            {"quantifier", rule.quantifier == FileQuantifier::Every ? "every" :
                rule.quantifier == FileQuantifier::None ? "none" : "any"},
            {"filename", Text(rule.filename)}, {"path", Text(rule.path)},
            {"extension", Text(rule.extension)}, {"file_size", Range(rule.fileSize)}});
    return result;
}

std::optional<std::vector<Catalogue::Rule>> Engine::State::ReadIndexRules(Json const& value)
{
    try
    {
        if (!value.is_array() || value.size() > 256)
            return std::nullopt;
        std::vector<Catalogue::Rule> rules;
        std::set<std::string> identities;
        for (auto const& item : value)
        {
            Catalogue::Rule rule;
            rule.ruleId = item.at("rule_id").get<std::string>();
            rule.name = item.value("name", std::string());
            if (rule.ruleId.empty() || rule.ruleId.size() > 128 || rule.name.size() > 256 ||
                !identities.insert(rule.ruleId).second)
                return std::nullopt;
            rule.enabled = item.value("enabled", true);
            rule.title = ReadText(item.value("title", Json::object()));
            rule.size = ReadRange(item.value("size", Json::object()));
            rule.count = ReadRange(item.value("count", Json::object()));
            rule.filename = ReadText(item.value("filename", Json::object()));
            rule.path = ReadText(item.value("path", Json::object()));
            rule.extension = ReadText(item.value("extension", Json::object()));
            if (rule.extension.value.find_first_of("*?/\\:") != std::string::npos)
                return std::nullopt;
            rule.fileSize = ReadRange(item.value("file_size", Json::object()));
            auto hasRange = [](Catalogue::Range const& range) { return range.minimum || range.maximum; };
            if (rule.title.value.empty() && !hasRange(rule.size) && !hasRange(rule.count) &&
                rule.filename.value.empty() && rule.path.value.empty() && rule.extension.value.empty() && !hasRange(rule.fileSize))
                return std::nullopt;
            auto quantifier = item.value("quantifier", std::string("any"));
            if (quantifier != "any" && quantifier != "every" && quantifier != "none")
                return std::nullopt;
            rule.quantifier = quantifier == "every" ? FileQuantifier::Every :
                quantifier == "none" ? FileQuantifier::None : FileQuantifier::Any;
            rules.push_back(std::move(rule));
        }
        return rules;
    }
    catch (std::exception const&)
    {
        return std::nullopt;
    }
}

Catalogue::Settings Engine::State::IndexSettings() const
{
    return {std::int64_t(settings.indexBudgetMib) * 1024 * 1024,
        settings.indexStaleHours, settings.indexRules};
}

void Engine::State::ConfigureIndex()
{
    if (!session || IsLoading() || IsShuttingDown())
        return;
    if (!settings.indexing)
        StopIndex();
    StopBitmagnet();
    crawlerSaveFailed = false;
    auto config = IndexSettings();
    auto create = settings.indexing;
    indexOpening = true;
    indexConfigured = false;
    auto version = ++indexVersion;
    struct OpenedIndex { Catalogue::Status status; Json crawler; };
    indexWork.Run([this, config = std::move(config), create]() mutable
    {
        catalogue.Open(create);
        catalogue.Configure(std::move(config));
        auto path = directory / L"crawler.json";
        auto crawler = std::filesystem::exists(path) ? Json::parse(Store::Read(path)) : Json::object();
        return OpenedIndex{catalogue.ReadStatus(), std::move(crawler)};
    }, [this, version](std::expected<OpenedIndex, StorageOutcome> result)
    {
        if (version != indexVersion)
            return;
        indexOpening = false;
        indexConfigured = result.has_value();
        if (result)
        {
            indexReady = result->status.exists;
            indexRebuilding = result->status.rebuilding;
            crawlerConfig = std::move(result->crawler);
        }
        indexError = result ? std::string() : result.error().detail;
        if (!result)
            StopIndex();
    });
}

void Engine::State::StopIndex()
{
    StopBitmagnet();
    scrapes.clear();
    indexDiscoveries.clear();
}

void Engine::State::IngestMetadata(Torrent const& torrent)
{
    if (torrent.restore)
    {
        IngestMetadata(*torrent.restore, {.metainfoPath = torrent.facts.torrentFile, .snapshot = true});
        return;
    }
    lt::add_torrent_params params;
    params.ti = torrent.names ? torrent.names->metadata : torrent.handle.torrent_file();
    params.comment = torrent.comment;
    params.created_by = torrent.creator;
    params.creation_date = torrent.created;
    for (auto const& tracker : torrent.Trackers())
    {
        params.trackers.push_back(tracker.url);
        params.tracker_tiers.push_back(tracker.tier);
    }
    IngestMetadata(params, {.metainfoPath = torrent.facts.torrentFile, .resumeFile = ResumeFile(torrent.torrentId)});
}

void Engine::State::IngestMetadata(lt::add_torrent_params const& params, MetadataSource source)
{
    if (!params.ti || !settings.indexing || !indexReady || !indexConfigured || IsShuttingDown())
        return;
    auto hashes = Hashes(params.ti->info_hashes());
    if (hashes.empty())
        return;
    auto key = hashes.back();
    bool snapshot = source.snapshot;
    auto version = indexVersion;
    auto saved = ingested.find(key);
    auto pending = ingesting.find(key);
    if ((saved != ingested.end() && (!snapshot || saved->second)) ||
        (pending != ingesting.end() && (!snapshot || pending->second)))
        return;
    ingesting[key] = snapshot;
    indexWork.Run([this, params = params, source = std::move(source)]() mutable
    {
        if (!source.resumeFile.empty())
        {
            try
            {
                auto bytes = Store::Read(source.resumeFile);
                auto saved = lt::read_resume_data(lt::span<char const>(bytes.data(), bytes.size()));
                if (saved.ti && saved.ti->info_hashes() == params.ti->info_hashes())
                {
                    params = std::move(saved);
                    source.snapshot = true;
                }
            }
            catch (std::exception const&)
            {
                // A transfer can precede its first saved checkpoint.
            }
        }
        std::vector<char> original;
        if (!source.metainfoPath.empty())
        {
            try
            {
                auto bytes = Store::Read(Wide(source.metainfoPath));
                lt::load_torrent_limits limits;
                limits.max_buffer_size = static_cast<int>(bytes.size());
                limits.max_decode_depth = 200;
                auto originalParams = lt::load_torrent_buffer(bytes, limits);
                if (originalParams.ti->info_hashes() == params.ti->info_hashes())
                {
                    original.assign(bytes.begin(), bytes.end());
                    source.snapshot = true;
                }
            }
            catch (std::exception const&)
            {
                // The source may have been removed or replaced after Add;
                // the transfer's validated metadata remains authoritative.
            }
        }
        catalogue.Admit(params, {}, original);
        return source.snapshot;
    }, [this, key, version, snapshot](std::expected<bool, StorageOutcome> result)
    {
        if (auto pending = ingesting.find(key); pending != ingesting.end() && pending->second == snapshot)
            ingesting.erase(pending);
        if (result && version == indexVersion)
            ingested[key] = ingested[key] || *result;
        else if (!result)
            indexError = result.error().detail;
    });
}

void Engine::State::MaintainIndex()
{
    auto now = std::chrono::steady_clock::now();
    if (!indexReady || IsShuttingDown() || now - indexAt < std::chrono::seconds(1))
        return;
    indexAt = now;
    if (!indexMaintaining)
    {
        indexMaintaining = true;
        bool cleanup = settings.indexing && indexConfigured && !indexRebuilding &&
            now - cleanupAt >= std::chrono::seconds(30);
        if (cleanup)
            cleanupAt = now;
        auto discoveries = std::make_shared<std::vector<std::string>>(std::exchange(indexDiscoveries, {}));
        auto candidates = std::make_shared<std::vector<Catalogue::Pending>>(std::exchange(crawlerDiscoveries, {}));
        auto nodes = std::make_shared<std::vector<Catalogue::Node>>(std::exchange(crawlerNodes, {}));
        indexWork.Run([this, rebuild = indexRebuilding, cleanup, discoveries, candidates, nodes]
        {
            if (!candidates->empty()) catalogue.Discover(*candidates, Now());
            catalogue.SaveNodes(*nodes);
            if (!discoveries->empty())
                catalogue.Discover(*discoveries, Now());
            auto status = rebuild ? catalogue.Rebuild(1024) : catalogue.ProjectPending(1024);
            if (cleanup)
                catalogue.Evict(Now());
            return status;
        },
            [this, version = indexVersion, discoveries, candidates, nodes](std::expected<Catalogue::Progress, StorageOutcome> result)
        {
            indexMaintaining = false;
            if (result)
                indexRebuilding = result->rebuilding;
            // A backlog publishes again at once instead of a second later. Each
            // slice is short, so a search queued behind it still answers quickly.
            if (result && (result->projecting || result->rebuilding))
                indexAt = {};
            if (result && result->published > 0)
            {
                auto second = std::chrono::duration_cast<std::chrono::seconds>(
                    std::chrono::steady_clock::now().time_since_epoch());
                auto& bucket = indexPublications[second.count() % indexPublications.size()];
                if (bucket.first != second)
                    bucket = {second, 0};
                bucket.second += result->published;
            }
            // Refused work has not run; a failed job may have saved part of its batch.
            if (!result && result.error().failure == StorageFailure::Overloaded)
            {
                if (version == indexVersion && settings.indexing && !IsShuttingDown())
                {
                    indexDiscoveries.insert(indexDiscoveries.end(), discoveries->begin(), discoveries->end());
                    crawlerDiscoveries.insert(crawlerDiscoveries.end(), std::make_move_iterator(candidates->begin()),
                        std::make_move_iterator(candidates->end()));
                    crawlerNodes.insert(crawlerNodes.end(), std::make_move_iterator(nodes->begin()),
                        std::make_move_iterator(nodes->end()));
                }
            }
            else if (!result)
                indexError = result.error().detail;
        });
    }
    if (settings.indexing && indexConfigured)
    {
        int visited = 0;
        for (auto const& [id, torrent] : torrents)
        {
            auto metadata = torrent.restore ? torrent.restore->ti : torrent.names ? torrent.names->metadata : nullptr;
            if (!metadata || ingesting.size() >= 8)
                continue;
            auto hashes = Hashes(metadata->info_hashes());
            if (hashes.empty() || ingested.contains(hashes.back()) || ingesting.contains(hashes.back()))
                continue;
            IngestMetadata(torrent);
            if (++visited == 8)
                break;
        }
    }
    for (auto it = scrapes.begin(); it != scrapes.end();)
    {
        if (it->second.pending == 0 || now >= it->second.deadline)
        {
            auto& scrape = it->second;
            if (scrape.responses > 0)
            {
                Catalogue::Observation observation{Estimate(scrape.seeds), Estimate(scrape.peers), Now(), scrape.responses};
                if (observation.seeds || observation.peers)
                    indexWork.Run([this, hash = scrape.hash, observation] { catalogue.Observe({{hash, observation}}); },
                        [](StorageOutcome) {});
            }
            it = scrapes.erase(it);
        }
        else
            ++it;
    }
    std::erase_if(scrapeAt, [now](auto const& item) { return item.second <= now; });
}

void Engine::State::On(lt::dht_stats_alert const& alert)
{
    if (!alert.nid.is_all_zeros() && alert.local_endpoint.address().is_v4()) crawlerNodeId = alert.nid;
    if (!settings.indexing || !indexReady || !indexConfigured || !settings.dht ||
        indexRebuilding || IsShuttingDown() || Paused() || alert.nid.is_all_zeros())
        return;
    session->dht_live_nodes(alert.nid);
}

void Engine::State::On(lt::dht_live_nodes_alert const& alert)
{
    if (!settings.indexing || !indexReady || !indexConfigured || IsShuttingDown())
        return;
    auto nodes = alert.nodes();
    if (bitmagnet)
    {
        std::vector<Catalogue::Node> contacts;
        for (auto const& [id, endpoint] : nodes)
            contacts.push_back({endpoint.address().to_string(), endpoint.port(), Hex(id), Now()});
        bitmagnet->Seed(std::move(contacts));
    }
}

void Engine::State::ScrapeCatalogue(Catalogue::Entry const& entry)
{
    if (entry.isPrivate)
        return;
    auto const& source = entry.torrent;
    auto now = std::chrono::steady_clock::now();
    if (!settings.indexing || !indexConfigured || !settings.dht || Paused() || IsShuttingDown() || !indexReady ||
        indexRebuilding || scrapes.size() >= (Activity().downloading ? 1 : 4) || scrapes.contains(source.hash) || now < scrapeAt[source.hash] ||
        (source.availability.observed && Now() - source.availability.observed < 900))
        return;
    if (scrapeAt.size() > 4096)
        return;
    scrapeAt[source.hash] = now + std::chrono::minutes(15);
    indexWork.Run([this] { return catalogue.Nodes(64, (std::numeric_limits<std::int64_t>::max)()); },
        [this, hash = source.hash](std::expected<std::vector<Catalogue::Node>, StorageOutcome> nodes)
    {
        if (!nodes || nodes->empty() || !settings.indexing || !indexConfigured || indexRebuilding || Paused() || IsShuttingDown() ||
            scrapes.size() >= (Activity().downloading ? 1 : 4))
            return;
        auto requestId = ++scrapeRequest;
        auto [found, inserted] = scrapes.emplace(hash, Scrape{requestId, hash, std::vector<unsigned char>(256),
            std::vector<unsigned char>(256), 0, 0,
            std::chrono::steady_clock::now() + std::chrono::seconds(20)});
        if (!inserted)
            return;
        found->second.nodes = std::move(*nodes);
        SendScrape(found->second);
    });
}

void Engine::State::SendScrape(Scrape& scrape)
{
    if (!settings.indexing || !indexConfigured || !settings.dht || Paused() || IsShuttingDown())
        return;
    auto target = Lookup(scrape.hash);
    std::sort(scrape.nodes.begin(), scrape.nodes.end(), [target](auto const& left, auto const& right)
        { return (Lookup(left.nodeId) ^ target) < (Lookup(right.nodeId) ^ target); });
    while (!scrape.nodes.empty() && scrape.pending < 4 && scrape.requested < 64)
    {
        auto node = scrape.nodes.front();
        scrape.nodes.erase(scrape.nodes.begin());
        auto endpoint = Endpoint(node);
        auto key = endpoint.address().to_string() + ":" + std::to_string(endpoint.port());
        if (!scrape.visited.insert(key).second)
            continue;
        lt::entry request;
        request["q"] = "get_peers";
        request["a"]["info_hash"] = target.to_string();
        request["a"]["scrape"] = 1;
        ++scrape.pending;
        ++scrape.requested;
        // The cookie is never dereferenced; a monotonic value prevents a late
        // response from attaching to a later scrape at a reused map address.
        session->dht_direct_request(endpoint, request,
            lt::client_data_t(reinterpret_cast<Scrape*>(scrape.request)));
    }
}

void Engine::State::On(lt::dht_direct_response_alert const& alert)
{
    if (auto request = alert.userdata.get<BitmagnetCrawler>())
    {
        auto found = crawlerRequests.find(reinterpret_cast<std::uintptr_t>(request));
        if (found != crawlerRequests.end())
        {
            if (bitmagnet) bitmagnet->Response(found->second.request, alert.response());
            crawlerRequests.erase(found);
        }
        return;
    }
    auto requestId = reinterpret_cast<std::uintptr_t>(alert.userdata.get<Scrape>());
    auto found = std::find_if(scrapes.begin(), scrapes.end(), [requestId](auto const& item)
        { return item.second.request == requestId; });
    if (found == scrapes.end())
        return;
    auto& scrape = found->second;
    --scrape.pending;
    auto reply = alert.response();
    if (reply.type() != lt::bdecode_node::dict_t)
        return;
    auto response = reply.dict_find_dict("r");
    if (!response)
    {
        SendScrape(scrape);
        return;
    }
    if (response)
    {
        auto addNodes = [&scrape](lt::bdecode_node const& compact, bool ipv6)
        {
            if (!compact)
                return;
            auto bytes = compact.string_value();
            std::size_t width = ipv6 ? 38 : 26;
            if (bytes.size() % width != 0)
                return;
            for (std::size_t offset = 0; offset + width <= bytes.size() && scrape.nodes.size() < 128; offset += width)
            {
                auto data = bytes.data() + offset;
                lt::sha1_hash id(data);
                lt::address address;
                if (ipv6)
                {
                    lt::address_v6::bytes_type raw;
                    std::copy_n(reinterpret_cast<unsigned char const*>(data + 20), 16, raw.begin());
                    address = lt::address_v6(raw);
                }
                else
                {
                    lt::address_v4::bytes_type raw;
                    std::copy_n(reinterpret_cast<unsigned char const*>(data + 20), 4, raw.begin());
                    address = lt::address_v4(raw);
                }
                int port = static_cast<unsigned char>(data[width - 2]) * 256 +
                    static_cast<unsigned char>(data[width - 1]);
                if (!port || address.is_unspecified() || address.is_multicast() || address.is_loopback())
                    continue;
                scrape.nodes.push_back({address.to_string(), port, Hex(id), 0});
            }
        };
        addNodes(response.dict_find_string("nodes"), false);
        addNodes(response.dict_find_string("nodes6"), true);
    }
    auto seeds = response.dict_find_string("BFsd");
    auto peers = response.dict_find_string("BFpe");
    if (seeds && peers && seeds.string_length() == 256 && peers.string_length() == 256)
    {
        for (int index = 0; index < 256; ++index)
        {
            scrape.seeds[index] |= static_cast<unsigned char>(seeds.string_ptr()[index]);
            scrape.peers[index] |= static_cast<unsigned char>(peers.string_ptr()[index]);
        }
        ++scrape.responses;
    }
    SendScrape(scrape);
}

void Engine::State::IndexCommand(std::string const& command, Json const& request,
    std::string const& connectionId, Reply reply)
{
    if (command == "catalogue_status")
    {
        indexWork.Run([this] { return catalogue.ReadStatus(); },
            [this, reply](std::expected<Catalogue::Status, StorageOutcome> result)
        {
            if (!result)
            {
                auto error = result.error().ToOutcome(ErrorCode::StorageFailed);
                reply(Failure(*error.error, error.detail));
                return;
            }
            auto data = EncodeStatus(*result);
            data["enabled"] = settings.indexing;
            data["error"] = indexError;
            auto condition = catalogue.ReadCondition();
            data["error_kind"] = condition == CatalogueCondition::Damaged ? "damaged" :
                condition == CatalogueCondition::Unavailable ? "unavailable" : "none";
            data["sampled"] = indexSampled;
            data["crawler_config"] = crawlerConfig;
            data["crawler_stats"] = BitmagnetStatus();
            data["rats"] = RatsStatus();
            data["acquiring"] = data["crawler_stats"]["fetching"];
            data["probing"] = data["crawler_stats"]["probing"];
            data["queued"] = bitmagnet ? data["crawler_stats"]["queued"] : Json(0);
            if (bitmagnet)
            {
                data["acquiring_limit"] = data["crawler_stats"]["fetching_limit"];
                data["probing_limit"] = data["crawler_stats"]["probing_limit"];
            }
            auto second = std::chrono::duration_cast<std::chrono::seconds>(
                std::chrono::steady_clock::now().time_since_epoch());
            std::int64_t published = 0;
            for (auto const& [at, count] : indexPublications)
                if (at <= second && second - at < std::chrono::seconds(60))
                    published += count;
            data["torrents_per_second"] = published / 60.0;
            data["budget_mib"] = settings.indexBudgetMib;
            data["stale_hours"] = settings.indexStaleHours;
            data["directory"] = Utf8(directory.wstring());
            reply(Success(std::move(data)));
        });
        return;
    }
    if (command == "catalogue_rules")
    {
        auto action = request.value("action", std::string("get"));
        if (action == "get")
            reply(Success({{"rules", IndexRules(settings.indexRules)}}));
        else if (action == "save")
            Configure({{"index_rules", request.at("rules")}}, [this, reply](Outcome result)
            {
                reply(result.error ? Failure(*result.error, result.detail) :
                    Success({{"rules", IndexRules(settings.indexRules)}}));
            });
        else if (action == "preview")
        {
            auto rules = ReadIndexRules(request.at("rules"));
            if (!rules)
                throw std::invalid_argument("Invalid exclusion rules");
            auto config = IndexSettings();
            config.rules = std::move(*rules);
            indexWork.Run([this, config = std::move(config)] { return catalogue.PreviewPolicy(config); },
                ReplyWith<Catalogue::PolicyPreview>(reply, [](auto const& value)
                {
                    return Json{{"suppressed", value.suppressed}, {"retained", value.retained},
                        {"released", value.released}};
                }));
        }
        else
            reply(Failure(ErrorCode::InvalidRequest));
        return;
    }
    if (!indexReady)
    {
        reply(Failure(indexOpening ? ErrorCode::Starting : ErrorCode::MetadataUnavailable, indexError));
        return;
    }
    if (command == "catalogue_query")
    {
        Catalogue::Query query;
        auto target = request.value("target", std::string("files"));
        if (target != "files" && target != "torrents")
            throw std::invalid_argument("Invalid search target");
        query.target = target == "files" ? CatalogueTarget::Files : CatalogueTarget::Torrents;
        auto sort = request.value("sort", std::string("name"));
        if (sort != "name" && sort != "size" && sort != "seeds" && sort != "peers" && sort != "seen")
            throw std::invalid_argument("Invalid search order");
        query.sort = sort == "size" ? CatalogueSort::Size : sort == "seeds" ? CatalogueSort::Seeds :
            sort == "peers" ? CatalogueSort::Peers : sort == "seen" ? CatalogueSort::Seen : CatalogueSort::Name;
        query.descending = request.value("descending", false);
        query.text = request.value("text", std::string());
        query.extension = request.value("extension", std::string());
        query.torrent = request.value("torrent", std::string());
        query.size = ReadRange(request.value("size", Json::object()));
        query.limit = std::clamp(request.value("limit", 100), 1, 200);
        if (query.text.size() > 4096 || query.extension.size() > 256 || query.torrent.size() > 4096)
            throw std::invalid_argument("Invalid query");
        if (auto cursor = request.find("cursor"); cursor != request.end() && !cursor->is_null())
            query.cursor = Catalogue::Cursor{cursor->at("entry").get<std::int64_t>(),
                cursor->at("file").get<int>(), cursor->at("watermark").get<std::int64_t>(),
                cursor->at("generation").get<std::int64_t>(), cursor->value("value", std::string())};
        indexWork.Run([this, query = std::move(query)] { return catalogue.Search(query); },
            ReplyWith<Catalogue::Page>(reply, [](auto const& page)
            {
                auto hits = Json::array();
                for (auto const& hit : page.hits)
                {
                    auto sources = Json::array();
                    for (auto const& source : hit.sources)
                        sources.push_back(Source(source));
                    hits.push_back({{"matched", Source(hit.matched)}, {"preferred", Source(hit.preferred)},
                        {"sources", std::move(sources)}, {"aliases", hit.aliases}, {"sources_limited", hit.sourcesLimited}});
                }
                Json next = nullptr;
                if (page.next)
                    next = {{"entry", page.next->entry}, {"file", page.next->file},
                        {"watermark", page.next->watermark}, {"generation", page.next->generation}, {"value", page.next->value}};
                return Json{{"hits", std::move(hits)}, {"next", next}, {"limited", page.limited},
                    {"exhaustive", page.exhaustive}, {"visited", page.visited},
                    {"watermark", page.watermark}, {"generation", page.generation}};
            }));
    }
    else if (command == "catalogue_detail")
    {
        auto hash = request.at("hash").get<std::string>();
        if (!IsHash(hash))
            throw std::invalid_argument("Invalid catalogue identity");
        auto offset = std::max(0, request.value("offset", 0));
        auto limit = std::clamp(request.value("limit", 200), 0, 500);
        auto query = request.value("query", std::string());
        if (query.size() > 4096)
            throw std::invalid_argument("File query too long");
        indexWork.Run([this, hash, offset, limit, query] { return catalogue.Detail(hash, offset, limit, query); },
            [this, reply](std::expected<Catalogue::Entry, StorageOutcome> result)
        {
            if (!result)
            {
                reply(Failure(ErrorCode::MetadataUnavailable, result.error().detail));
                return;
            }
            auto files = Json::array();
            for (auto const& file : result->files)
                files.push_back(File(file));
            reply(Success({{"torrent", Source(result->torrent)}, {"files", std::move(files)},
                {"comment", result->comment}, {"creator", result->creator}, {"created", result->created},
                {"offset", result->offset}, {"next", result->next},
                {"selectable_count", result->selectableCount}, {"more", result->more}}));
            ScrapeCatalogue(*result);
        });
    }
    else if (command == "catalogue_sources")
    {
        auto hash = request.at("hash").get<std::string>();
        auto parent = request.value("parent", std::string());
        auto index = request.at("index").get<int>();
        if (!IsHash(hash) || parent.size() > 4096 || index < 0)
            throw std::invalid_argument("Invalid file source");
        auto offset = std::max(0, request.value("offset", 0));
        auto limit = std::clamp(request.value("limit", 100), 1, 200);
        auto watermark = request.value("watermark", std::int64_t(0));
        auto text = request.value("text", std::string());
        auto generation = request.value("generation", std::int64_t(0));
        if (text.size() > 4096)
            throw std::invalid_argument("Source query too long");
        Catalogue::Source source;
        source.hash = hash;
        source.file.index = index;
        Catalogue::Query query;
        query.torrent = parent;
        query.text = text;
        query.cursor = Catalogue::Cursor{.watermark = watermark, .generation = generation};
        indexWork.Run([this, source = std::move(source), query = std::move(query), offset, limit]
        {
            return catalogue.Sources(source, query, offset, limit);
        }, ReplyWith<Catalogue::SourcePage>(reply, [](auto const& page)
        {
            auto sources = Json::array();
            for (auto const& source : page.sources)
                sources.push_back(Source(source));
            return Json{{"sources", std::move(sources)}, {"aliases", page.aliases},
                {"offset", page.offset}, {"next", page.next}, {"more", page.more}, {"limited", page.limited}};
        }));
    }
    else if (command == "catalogue_prepare")
        PrepareCatalogue(request, connectionId, std::move(reply));
    else if (command == "catalogue_preview")
    {
        auto kind = request.value("kind", std::string("excluded"));
        if (kind != "excluded" && kind != "clear")
            throw std::invalid_argument("Invalid removal scope");
        auto scope = kind == "clear" ? CatalogueRemoval::Clear : CatalogueRemoval::Excluded;
        indexWork.Run([this, scope] { return catalogue.Preview(scope); },
            ReplyWith<Catalogue::Removal>(reply, EncodeRemoval));
    }
    else if (command == "catalogue_remove")
    {
        auto token = request.at("token").get<std::string>();
        if (token.empty() || token.size() > 128)
            throw std::invalid_argument("Invalid removal token");
        indexWork.Run([this, token] { return catalogue.Remove(token); },
            ReplyWith<Catalogue::Removal>(reply, EncodeRemoval));
    }
    else if (command == "catalogue_removal_rows")
    {
        auto token = request.at("token").get<std::string>();
        auto after = request.value("after", std::int64_t(0));
        auto limit = std::clamp(request.value("limit", 100), 1, 200);
        if (token.empty() || token.size() > 128 || after < 0)
            throw std::invalid_argument("Invalid removal preview");
        indexWork.Run([this, token, after, limit] { return catalogue.PreviewRows(token, after, limit); },
            ReplyWith<Catalogue::RemovalPage>(reply, [](auto const& page)
            {
                auto entries = Json::array();
                for (auto const& entry : page.entries)
                    entries.push_back({{"torrent", Source(entry.torrent)}, {"reasons", entry.reasons}});
                return Json{{"entries", std::move(entries)}, {"next", page.next}, {"more", page.more}};
            }));
    }
    else if (command == "catalogue_rebuild")
    {
        bool cancel = request.value("cancel", false);
        if (!cancel)
        {
            StopIndex();
            indexRebuilding = true;
        }
        indexWork.Run([this, cancel]
        {
            if (cancel)
                catalogue.CancelRebuild();
            else
                catalogue.Rebuild(4);
            return catalogue.ReadStatus();
        }, [this, reply, cancel](std::expected<Catalogue::Status, StorageOutcome> result)
        {
            indexRebuilding = !cancel && result && result->rebuilding;
            if (result)
                reply(Success(EncodeStatus(*result)));
            else
                reply(Failure(ErrorCode::StorageFailed, result.error().detail));
        });
    }
    else
        reply(Failure(ErrorCode::UnknownCommand));
}

void Engine::State::PrepareCatalogue(Json const& request, std::string const& connectionId, Reply reply)
{
    auto hash = request.at("hash").get<std::string>();
    if (!IsHash(hash))
        throw std::invalid_argument("Invalid catalogue identity");
    auto index = request.contains("index") ? std::optional(request.at("index").get<int>()) : std::nullopt;
    auto root = request.value("root", std::string());
    auto length = request.value("length", std::int64_t(-1));
    Catalogue::Query scope;
    if (auto value = request.find("scope"); value != request.end())
    {
        scope.text = value->value("text", std::string());
        scope.torrent = value->value("torrent", std::string());
        if (scope.text.size() > 4096 || scope.torrent.size() > 4096)
            throw std::invalid_argument("Catalogue source query too long");
        auto const& cursor = value->at("cursor");
        scope.cursor = Catalogue::Cursor{cursor.value("entry", std::int64_t(0)),
            cursor.value("file", -1), cursor.at("watermark").get<std::int64_t>(),
            cursor.at("generation").get<std::int64_t>(), cursor.value("value", std::string())};
    }
    if (root.size() > 64 || (index && *index < 0) || previews.size() + parsing.size() >= 256)
    {
        reply(Failure(ErrorCode::InvalidRequest));
        return;
    }
    auto patterns = settings.excludes ? settings.patterns : std::string();
    auto preview = std::make_shared<Preview>();
    preview->previewId = NewId();
    preview->connectionId = connectionId;
    parsing.push_back(preview);
    indexWork.Run([this, hash, index, root, length, request, patterns, scope = std::move(scope)]
    {
        PreparedSource prepared;
        prepared.params = catalogue.Metadata(hash, scope);
        if (!prepared.params.ti)
            throw std::runtime_error("Catalogue metadata unavailable");
        auto const& files = prepared.params.ti->layout();
        if (index)
        {
            auto file = lt::file_index_t(*index);
            if (*index >= files.num_files() || files.pad_file_at(file) ||
                (length >= 0 && length != files.file_size(file)) ||
                (!root.empty() && root != lt::aux::to_hex(files.root(file).to_string())))
                throw std::runtime_error("Catalogue source changed");
        }
        if (index || request.contains("choices"))
        {
            auto choices = request.value("choices", Json::array());
            if (!choices.is_array() || choices.size() > static_cast<std::size_t>(files.num_files()))
                throw std::invalid_argument("Invalid catalogue file choices");
            if (index && choices.empty())
                choices.push_back({{"index", *index}, {"priority", 4}});
            auto baseline = DefaultPriorities(files, patterns);
            std::set<int> indices;
            for (auto& choice : choices)
            {
                auto chosen = choice.at("index").get<int>();
                auto number = choice.at("priority").get<int>();
                if (number < 0 || number > 7)
                    throw std::invalid_argument("Invalid catalogue file priority");
                auto priority = lt::download_priority_t(static_cast<std::uint8_t>(number));
                if (chosen < 0 || chosen >= files.num_files() || files.pad_file_at(lt::file_index_t(chosen)) ||
                    !IsChoice(priority) || !indices.insert(chosen).second)
                    throw std::invalid_argument("Invalid catalogue file choice");
                choice["size"] = files.file_size(lt::file_index_t(chosen));
                choice["baseline"] = static_cast<std::uint8_t>(baseline[chosen]);
            }
            auto priority = request.value("default_priority", 0);
            if (priority < 0 || priority > 7 ||
                !IsChoice(lt::download_priority_t(static_cast<std::uint8_t>(priority))))
                throw std::invalid_argument("Invalid default priority");
            prepared.selection = {{"default_priority", priority}, {"choices", std::move(choices)}};
        }
        return prepared;
    }, [this, preview, index, request, reply](std::expected<PreparedSource, StorageOutcome> result)
    {
        std::erase(parsing, preview);
        if (preview->cancelled)
            return;
        if (IsShuttingDown())
        {
            reply(Failure(ErrorCode::ShuttingDown));
            return;
        }
        if (!result)
        {
            reply(Failure(ErrorCode::MetadataUnavailable, result.error().detail));
            return;
        }
        preview->params = std::move(result->params);
        preview->params.info_hashes = preview->params.ti->info_hashes();
        if (Removing(preview->InfoHashes()))
        {
            reply(Failure(ErrorCode::TorrentRemoving));
            return;
        }
        auto saved = Merge(*preview);
        if (!saved)
        {
            auto identity = preview->previewId;
            saved = &previews.emplace(std::move(identity), std::move(*preview)).first->second;
        }
        auto complete = [reply, selection = std::move(result->selection)](Json response) mutable
        {
            if (response.at("ok").get<bool>() && !selection.is_null())
                response["data"]["selection"] = std::move(selection);
            reply(std::move(response));
        };
        DescribePreview(*saved, request.value("destination", settings.AdditionFolder()),
            std::move(complete), 0, index ? 200 : 0);
    });
}
}
