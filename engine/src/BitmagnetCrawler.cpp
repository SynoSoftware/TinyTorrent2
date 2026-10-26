// Translated to C++ from Bitmagnet e31b30d, copyright (c) 2023 bitmagnet
// contributors (MIT); distributed notice: licenses/Bitmagnet/Bitmagnet.txt.
// StableFilter translates BoomFilters 1a82519 to C++ (Apache-2.0);
// distributed license: licenses/BoomFilters/BoomFilters.txt.
#include "BitmagnetCrawler.h"
#include <libtorrent/hex.hpp>
#include <algorithm>
#include <bit>
#include <deque>
#include <map>
#include <optional>
#include <random>
#include <utility>

namespace tt
{
namespace
{
namespace lt = libtorrent;
using Clock = std::chrono::steady_clock;
using Time = Clock::time_point;
using namespace std::chrono_literals;

lt::sha1_hash Hash(std::string const& value)
{
    lt::sha1_hash hash;
    if (value.size() == 40)
        lt::aux::from_hex(value, hash.data());
    return hash;
}

std::string Hex(lt::sha1_hash const& hash)
{
    return lt::aux::to_hex(hash.to_string());
}

std::optional<lt::udp::endpoint> Endpoint(Catalogue::Endpoint const& value)
{
    lt::error_code error;
    auto address = lt::make_address(value.address, error);
    if (error || value.port <= 0 || value.port > 65535)
        return {};
    return lt::udp::endpoint(address, static_cast<unsigned short>(value.port));
}

struct StableFilter
{
    static constexpr std::size_t cells = 10'000'000;
    std::vector<std::uint8_t> buckets = std::vector<std::uint8_t>(cells / 4);

    std::uint8_t Get(std::size_t index) const
    {
        return (buckets[index / 4] >> ((index % 4) * 2)) & 3;
    }

    void Set(std::size_t index, std::uint8_t value)
    {
        auto shift = (index % 4) * 2;
        auto& bucket = buckets[index / 4];
        bucket = static_cast<std::uint8_t>((bucket & ~(3 << shift)) | (value << shift));
    }

    // BoomFilters 1a82519: FNV-1, five double hashes and 49 aged cells at fp=.001.
    bool TestAndAdd(lt::sha1_hash const& hash, std::mt19937_64& random)
    {
        std::uint64_t value = 14695981039346656037ULL;
        for (unsigned char byte : hash.to_string())
            value = value * 1099511628211ULL ^ byte;
        auto lower = static_cast<std::uint32_t>(value);
        auto upper = static_cast<std::uint32_t>(value >> 32);
        std::array<std::size_t, 5> indices;
        bool member = true;
        for (std::size_t index = 0; index < indices.size(); ++index)
        {
            indices[index] = (std::uint64_t(lower) + std::uint64_t(upper) * index) % cells;
            member &= Get(indices[index]) != 0;
        }
        auto start = std::uniform_int_distribution<std::size_t>(0, cells - 1)(random);
        for (std::size_t offset = 0; offset < 49; ++offset)
        {
            auto index = (start + offset) % cells;
            auto current = Get(index);
            if (current) Set(index, current - 1);
        }
        for (auto index : indices) Set(index, 3);
        return member;
    }
};

template <typename T>
struct Batch
{
    std::size_t capacity;
    std::size_t maximum;
    Clock::duration interval;
    Time deadline;
    std::deque<T> input;
    std::vector<T> buffer;
    std::optional<std::vector<T>> output;

    Batch(std::size_t capacity, std::size_t maximum, Clock::duration interval)
        : capacity(capacity), maximum(maximum), interval(interval), deadline(Clock::now() + interval) {}

    bool Push(T value)
    {
        if (input.size() >= capacity) return false;
        input.push_back(std::move(value));
        return true;
    }

    void Pump(Time now)
    {
        while (!input.empty() && buffer.size() < maximum)
        {
            buffer.push_back(std::move(input.front()));
            input.pop_front();
        }
        if (!output && !buffer.empty() && (buffer.size() == maximum || now >= deadline))
        {
            output = std::exchange(buffer, {});
            deadline = now + interval;
        }
        else if (buffer.empty() && now >= deadline)
            deadline = now + interval;
    }

    std::size_t Size() const
    {
        return input.size() + buffer.size() + (output ? output->size() : 0);
    }

    std::vector<T> Drain()
    {
        std::vector<T> values;
        if (output) values = std::move(*output);
        values.insert(values.end(), std::make_move_iterator(buffer.begin()), std::make_move_iterator(buffer.end()));
        values.insert(values.end(), std::make_move_iterator(input.begin()), std::make_move_iterator(input.end()));
        output.reset();
        input.clear();
        buffer.clear();
        return values;
    }
};
}

struct BitmagnetCrawler::State : std::enable_shared_from_this<State>
{
    struct Node
    {
        lt::sha1_hash id;
        lt::udp::endpoint endpoint;
        Time responded;
        Time eligible;
    };

    struct Ready
    {
        Candidate candidate;
        std::vector<lt::tcp::endpoint> peers;
    };

    struct Answer
    {
        std::vector<Node> nodes;
        std::vector<Candidate> samples;
        std::vector<lt::tcp::endpoint> peers;
        std::optional<Observation> observation;
        std::optional<Time> forwarding;
        std::size_t nextNode = 0;
        std::size_t nextSample = 0;
    };

    struct Query
    {
        CrawlerQuery kind;
        Node node;
        Candidate candidate;
        std::optional<Time> deadline;
        std::optional<Answer> answer;
    };

    struct Metadata
    {
        Candidate candidate;
        std::optional<std::vector<char>> info;
    };

    struct Limiter
    {
        double tokens = 4;
        Time updated;
        Time expires;
    };

    struct Producer
    {
        std::vector<Node> pending;
        std::size_t next = 0;
        Time due;
    };

    Config config;
    MetadataRequester& requester;
    Callbacks callbacks;
    Statistics statistics;
    std::mt19937_64 random{std::random_device{}()};
    lt::sha1_hash origin;
    lt::sha1_hash target;
    std::map<lt::sha1_hash, Node> nodes;
    std::map<std::string, lt::sha1_hash> addresses;
    std::array<unsigned, 160> buckets{};
    StableFilter ignored;
    std::array<std::deque<Query>, 5> queues;
    std::map<std::uintptr_t, Query> queries;
    std::map<std::string, Limiter> limiters;
    std::deque<Ready> ready;
    std::map<std::uintptr_t, Metadata> metadata;
    Batch<Node> discovered;
    Batch<Candidate> triage;
    Batch<Acquired> retained{1000, 1000, 1min};
    Batch<Observation> observations{1000, 1000, 1min};
    std::optional<Selection> selection;
    std::vector<Candidate> classified;
    std::size_t nextNode = 0;
    std::size_t nextMissing = 0;
    std::size_t nextScrape = 0;
    std::size_t nextClassified = 0;
    bool classifying = false;
    bool persisting = false;
    bool stopped = false;
    std::uintptr_t sequence = 0;
    Time rotateAt = Clock::now() + 10s;
    std::array<Producer, 3> producers;
    Time bootstrapAt{};

    State(Config value, MetadataRequester& requester, Callbacks callbacks, lt::sha1_hash localId)
        : config(std::move(value)), requester(requester), callbacks(std::move(callbacks)),
          origin(localId),
          discovered(100 * config.scalingFactor, 10, 10ms),
          triage(10 * config.scalingFactor, 1000, 20s)
    {
        target = RandomHash();
        producers[Slot(CrawlerQuery::Ping)].due = Clock::now() + 10s;
    }

    lt::sha1_hash RandomHash()
    {
        lt::sha1_hash hash;
        for (int index = 0; index < 20; ++index)
            hash[index] = static_cast<char>(random());
        return hash;
    }

    static std::size_t Slot(CrawlerQuery kind)
    {
        return static_cast<std::size_t>(kind);
    }

    std::size_t Capacity(CrawlerQuery kind) const
    {
        return (kind == CrawlerQuery::Ping ? 1 : 10) * config.scalingFactor;
    }

    std::size_t Concurrency(CrawlerQuery kind) const
    {
        return (kind == CrawlerQuery::Peers || kind == CrawlerQuery::Scrape ? 20 :
            kind == CrawlerQuery::Ping ? 1 : 10) * config.scalingFactor;
    }

    std::size_t FetchingLimit() const
    {
        return 40 * config.scalingFactor;
    }

    std::size_t Active(CrawlerQuery kind) const
    {
        return std::count_if(queries.begin(), queries.end(), [kind](auto const& item)
            { return item.second.kind == kind; });
    }

    bool Enqueue(Query query)
    {
        auto& queue = queues[Slot(query.kind)];
        if (queue.size() >= Capacity(query.kind)) return false;
        queue.push_back(std::move(query));
        return true;
    }

    unsigned Bucket(lt::sha1_hash const& id) const
    {
        auto distance = id ^ origin;
        unsigned bucket = 0;
        for (unsigned char value : distance.to_string())
        {
            if (value) return bucket + std::countl_zero(value);
            bucket += 8;
        }
        return bucket;
    }

    void Drop(lt::sha1_hash const& id)
    {
        auto found = nodes.find(id);
        if (found == nodes.end()) return;
        --buckets[Bucket(id)];
        addresses.erase(found->second.endpoint.address().to_string());
        nodes.erase(found);
    }

    void Drop(Query const& query)
    {
        auto found = addresses.find(query.node.endpoint.address().to_string());
        if (query.kind == CrawlerQuery::Peers || query.kind == CrawlerQuery::Scrape)
        {
            if (found != addresses.end()) Drop(found->second);
        }
        else Drop(query.node.id);
    }

    void Responded(Node node, Time now)
    {
        if (node.id.is_all_zeros() || node.id == origin) return;
        node.responded = now;
        auto found = nodes.find(node.id);
        if (found != nodes.end())
        {
            if (found->second.endpoint != node.endpoint)
                addresses.erase(found->second.endpoint.address().to_string());
            node.eligible = std::max(node.eligible, found->second.eligible);
            found->second = node;
        }
        else
        {
            auto bucket = Bucket(node.id);
            if (buckets[bucket] >= 80)
            {
                auto distance = node.id ^ origin;
                auto closer = std::count_if(nodes.begin(), nodes.end(), [&](auto const& item)
                    { return !(distance < (item.first ^ origin)); });
                if (closer >= 80) return;
            }
            ++buckets[bucket];
            nodes.emplace(node.id, node);
        }
        addresses[node.endpoint.address().to_string()] = node.id;
    }

    bool Allow(Node const& node, Time now)
    {
        auto key = node.endpoint.address().to_string();
        auto found = limiters.find(key);
        if (found == limiters.end() || now >= found->second.expires)
        {
            if (found != limiters.end()) limiters.erase(found);
            if (limiters.size() >= 1000)
            {
                auto oldest = std::min_element(limiters.begin(), limiters.end(), [](auto const& left, auto const& right)
                    { return left.second.updated < right.second.updated; });
                limiters.erase(oldest);
            }
            found = limiters.emplace(key, Limiter{4, now, now + 20s}).first;
        }
        auto& limiter = found->second;
        limiter.tokens = std::min(4., limiter.tokens + std::chrono::duration<double>(now - limiter.updated).count());
        limiter.updated = now;
        if (limiter.tokens < 1) return false;
        limiter.tokens -= 1;
        return true;
    }

    void Send(std::uintptr_t requestId, Query& query, Time now)
    {
        if (!Allow(query.node, now)) return;
        lt::entry request;
        switch (query.kind)
        {
        case CrawlerQuery::Ping: request["q"] = "ping"; break;
        case CrawlerQuery::Find: request["q"] = "find_node"; break;
        case CrawlerQuery::Sample: request["q"] = "sample_infohashes"; break;
        case CrawlerQuery::Peers:
        case CrawlerQuery::Scrape: request["q"] = "get_peers"; break;
        }
        if (query.kind == CrawlerQuery::Find || query.kind == CrawlerQuery::Sample)
            request["a"]["target"] = target.to_string();
        if (query.kind == CrawlerQuery::Peers || query.kind == CrawlerQuery::Scrape)
            request["a"]["info_hash"] = Hash(query.candidate.hash).to_string();
        if (query.kind == CrawlerQuery::Scrape) request["a"]["scrape"] = 1;
        query.deadline = now + config.queryTimeout;
        if (query.kind == CrawlerQuery::Peers) ++statistics.peerQueries;
        callbacks.send(requestId, query.node.endpoint, std::move(request));
    }

    static std::vector<Node> ReadNodes(lt::bdecode_node const& response)
    {
        auto value = response.dict_find_string_value("nodes");
        std::vector<Node> nodes;
        for (std::size_t offset = 0; offset + 26 <= value.size(); offset += 26)
        {
            auto bytes = reinterpret_cast<unsigned char const*>(value.data() + offset);
            lt::address_v4::bytes_type address{bytes[20], bytes[21], bytes[22], bytes[23]};
            auto port = static_cast<unsigned short>((bytes[24] << 8) | bytes[25]);
            if (port) nodes.push_back({lt::sha1_hash(value.data() + offset), {lt::address_v4(address), port}});
        }
        return nodes;
    }

    static std::vector<lt::tcp::endpoint> ReadPeers(lt::bdecode_node const& response)
    {
        std::vector<lt::tcp::endpoint> peers;
        auto values = response.dict_find_list("values");
        if (!values) return peers;
        for (int index = 0; index < values.list_size(); ++index)
        {
            auto item = values.list_at(index);
            if (item.type() != lt::bdecode_node::string_t || item.string_length() != 6) continue;
            auto bytes = reinterpret_cast<unsigned char const*>(item.string_ptr());
            lt::address_v4::bytes_type address{bytes[0], bytes[1], bytes[2], bytes[3]};
            auto port = static_cast<unsigned short>((bytes[4] << 8) | bytes[5]);
            if (port) peers.emplace_back(lt::address_v4(address), port);
        }
        return peers;
    }

    void Response(std::uintptr_t requestId, lt::bdecode_node response)
    {
        auto found = queries.find(requestId);
        if (found == queries.end() || found->second.answer || stopped) return;
        auto& query = found->second;
        auto reply = response.type() == lt::bdecode_node::dict_t ? response.dict_find_dict("r") : lt::bdecode_node{};
        auto id = reply ? reply.dict_find_string_value("id") : lt::string_view{};
        if (!reply || id.size() != 20)
        {
            Drop(query);
            queries.erase(found);
            return;
        }
        auto now = Clock::now();
        if (query.kind == CrawlerQuery::Ping && !query.node.id.is_all_zeros() &&
            query.node.id != lt::sha1_hash(id.data()))
        {
            Drop(query.node.id);
            queries.erase(found);
            return;
        }
        if (query.kind != CrawlerQuery::Find && query.kind != CrawlerQuery::Sample || query.node.id.is_all_zeros())
            query.node.id = lt::sha1_hash(id.data());
        Responded(query.node, now);
        Answer answer;
        answer.nodes = ReadNodes(reply);
        if (query.kind == CrawlerQuery::Sample)
        {
            auto bytes = reply.dict_find_string_value("samples");
            std::vector<std::string> samples;
            for (std::size_t offset = 0; offset + 20 <= bytes.size(); offset += 20)
            {
                lt::sha1_hash hash(bytes.data() + offset);
                auto value = Hex(hash);
                samples.push_back(value);
                if (!ignored.TestAndAdd(hash, random))
                    answer.samples.push_back({std::move(value), {query.node.endpoint.address().to_string(), query.node.endpoint.port()}});
            }
            statistics.discovered += samples.size();
            if (!samples.empty() && callbacks.discover)
                callbacks.discover(std::move(samples), {query.node.endpoint.address().to_string(), query.node.endpoint.port()});
                auto maximum = std::chrono::duration_cast<std::chrono::seconds>(Time::max() - now - 5min).count();
                auto interval = std::clamp<std::int64_t>(reply.dict_find_int_value("interval"), 0, maximum);
            auto node = nodes.find(query.node.id);
            if (node != nodes.end())
            {
                // Honor the advertised BEP 51 interval rather than Bitmagnet's shortening.
                node->second.eligible = now + std::chrono::seconds(interval) + (answer.samples.empty() ? 5min : 0min);
                if (callbacks.nodes)
                {
                    auto seconds = std::chrono::duration_cast<std::chrono::seconds>(node->second.eligible - now).count();
                    auto eligible = std::chrono::system_clock::to_time_t(std::chrono::system_clock::now()) + seconds;
                    callbacks.nodes({{query.node.endpoint.address().to_string(), query.node.endpoint.port(), Hex(query.node.id), eligible}});
                }
            }
        }
        if (query.kind == CrawlerQuery::Peers)
        {
            answer.peers = ReadPeers(reply);
            if (!answer.peers.empty()) ++statistics.peerPositive;
        }
        if (query.kind == CrawlerQuery::Scrape)
        {
            auto seeds = reply.dict_find_string_value("BFsd");
            auto peers = reply.dict_find_string_value("BFpe");
            if (seeds.size() == 256 && peers.size() == 256)
            {
                Observation observation{query.candidate};
                std::copy(seeds.begin(), seeds.end(), observation.seeds.begin());
                std::copy(peers.begin(), peers.end(), observation.peers.begin());
                answer.observation = std::move(observation);
            }
        }
        query.answer = std::move(answer);
    }

    bool Forward(Query& query, Time now)
    {
        auto& answer = *query.answer;
        while (answer.nextSample < answer.samples.size())
        {
            if (!triage.Push(answer.samples[answer.nextSample])) return false;
            ++answer.nextSample;
        }
        if (!answer.forwarding) answer.forwarding = now + 1s;
        while (answer.nextNode < answer.nodes.size())
        {
            if (!discovered.Push(answer.nodes[answer.nextNode]))
            {
                if (query.kind == CrawlerQuery::Find || now < *answer.forwarding) return false;
                break;
            }
            ++answer.nextNode;
        }
        if (query.kind == CrawlerQuery::Peers && !answer.peers.empty())
        {
            if (ready.size() >= 10 * config.scalingFactor) return false;
            ready.push_back({std::move(query.candidate), std::move(answer.peers)});
        }
        if (answer.observation && !observations.Push(*answer.observation)) return false;
        return true;
    }

    void Dispatch(Time now)
    {
        for (std::size_t slot = 0; slot < queues.size(); ++slot)
        {
            auto kind = static_cast<CrawlerQuery>(slot);
            auto active = Active(kind);
            while (!queues[slot].empty() && active < Concurrency(kind))
            {
                auto query = std::move(queues[slot].front());
                queues[slot].pop_front();
                auto node = nodes.find(query.node.id);
                if (kind == CrawlerQuery::Ping && node != nodes.end() && node->second.responded > now - 15min) continue;
                if (kind == CrawlerQuery::Sample && node != nodes.end() &&
                    (node->second.eligible >= now || node->second.responded >= now - 5s)) continue;
                queries.emplace(++sequence, std::move(query));
                ++active;
            }
        }
        for (auto it = queries.begin(); it != queries.end();)
        {
            auto& query = it->second;
            if (query.answer)
            {
                if (Forward(query, now)) it = queries.erase(it);
                else ++it;
            }
            else if (query.deadline && now >= *query.deadline)
            {
                Drop(query);
                it = queries.erase(it);
            }
            else
            {
                auto current = it++;
                if (!current->second.deadline) Send(current->first, current->second, now);
            }
        }
    }

    void Schedule(Time now)
    {
        if (now >= rotateAt)
        {
            target = RandomHash();
            rotateAt = now + 10s;
        }
        if (now >= bootstrapAt)
        {
            if (callbacks.bootstrap) callbacks.bootstrap(config.bootstrapNodes);
            // The pinned factory uses ten minutes, ignoring its declared config value.
            bootstrapAt = now + 10min;
        }
        for (auto kind : {CrawlerQuery::Ping, CrawlerQuery::Find, CrawlerQuery::Sample})
        {
            auto& producer = producers[Slot(kind)];
            auto interval = kind == CrawlerQuery::Ping ? 10s : 1s;
            if (producer.pending.empty() && now >= producer.due)
            {
                for (auto const& [id, node] : nodes)
                {
                    if (kind == CrawlerQuery::Sample)
                    {
                        if (node.eligible >= now || node.responded >= now - 5s) continue;
                        producer.pending.push_back(node);
                        if (producer.pending.size() == 60) break;
                    }
                    else if (node.responded < now - (kind == CrawlerQuery::Ping ? 15min : 5s))
                        producer.pending.push_back(node);
                }
                if (kind != CrawlerQuery::Sample)
                {
                    std::sort(producer.pending.begin(), producer.pending.end(),
                        [](Node const& left, Node const& right) { return left.responded < right.responded; });
                    if (kind == CrawlerQuery::Find && producer.pending.size() > 10)
                        producer.pending.resize(10);
                }
                if (producer.pending.empty()) producer.due = now + interval;
            }
            while (producer.next < producer.pending.size())
            {
                if (!Enqueue({kind, producer.pending[producer.next]})) break;
                ++producer.next;
            }
            if (!producer.pending.empty() && producer.next == producer.pending.size())
            {
                producer.pending.clear();
                producer.next = 0;
                producer.due = now + interval;
            }
        }
    }

    void Classify(Time now)
    {
        triage.Pump(now);
        if (selection)
        {
            while (nextMissing < selection->missing.size())
            {
                auto candidate = selection->missing[nextMissing];
                auto endpoint = Endpoint(candidate.source);
                if (endpoint && !Enqueue({CrawlerQuery::Peers, {{}, *endpoint}, std::move(candidate)})) return;
                ++nextMissing;
                ++statistics.selected;
            }
            while (nextScrape < selection->scrape.size())
            {
                auto candidate = selection->scrape[nextScrape];
                auto endpoint = Endpoint(candidate.source);
                if (endpoint && !Enqueue({CrawlerQuery::Scrape, {{}, *endpoint}, std::move(candidate)})) return;
                ++nextScrape;
            }
            selection.reset();
            classifying = false;
        }
        if (classifying || !triage.output) return;
        auto values = std::exchange(triage.output, {});
        std::map<std::string, Candidate> unique;
        for (auto& value : *values) unique.try_emplace(value.hash, std::move(value));
        std::vector<Candidate> batch;
        for (auto& [hash, value] : unique) batch.push_back(std::move(value));
        classifying = true;
        std::weak_ptr<State> weak = shared_from_this();
        callbacks.triage(std::move(batch), [weak](Selection value)
        {
            if (auto state = weak.lock(); state && !state->stopped)
            {
                state->selection = std::move(value);
                state->nextMissing = 0;
                state->nextScrape = 0;
            }
        });
    }

    void Discover(Time now)
    {
        discovered.Pump(now);
        if (!discovered.output) return;
        auto& values = *discovered.output;
        while (nextNode < values.size())
        {
            auto& node = values[nextNode];
            if (addresses.contains(node.endpoint.address().to_string())) { ++nextNode; continue; }
            std::array<CrawlerQuery, 3> kinds{CrawlerQuery::Find, CrawlerQuery::Sample, CrawlerQuery::Ping};
            std::shuffle(kinds.begin(), kinds.end(), random);
            bool queued = false;
            for (auto kind : kinds)
                if (Enqueue({kind, node})) { queued = true; break; }
            if (!queued) return;
            ++nextNode;
        }
        nextNode = 0;
        discovered.output.reset();
    }

    void Acquire()
    {
        Receive();
        for (auto it = metadata.begin(); it != metadata.end();)
        {
            if (it->second.info && retained.input.size() < retained.capacity)
            {
                retained.Push({std::move(it->second.candidate), std::move(*it->second.info)});
                it = metadata.erase(it);
            }
            else ++it;
        }
        while (!ready.empty() && metadata.size() < FetchingLimit())
        {
            auto work = std::move(ready.front());
            ready.pop_front();
            auto requestId = ++sequence;
            auto hash = Hash(work.candidate.hash);
            metadata.emplace(requestId, Metadata{std::move(work.candidate)});
            ++statistics.metadataRequests;
            requester.Request(requestId, hash, std::move(work.peers));
        }
    }

    void Persist(Time now)
    {
        retained.Pump(now);
        observations.Pump(now);
        if (observations.output)
        {
            if (callbacks.observe)
                callbacks.observe(std::move(*observations.output));
            observations.output.reset();
        }
        while (nextClassified < classified.size())
        {
            auto candidate = classified[nextClassified];
            auto endpoint = Endpoint(candidate.source);
            if (endpoint && !Enqueue({CrawlerQuery::Scrape, {{}, *endpoint}, std::move(candidate)})) return;
            ++nextClassified;
        }
        classified.clear();
        nextClassified = 0;
        if (persisting || !retained.output) return;
        auto batch = std::move(*retained.output);
        retained.output.reset();
        std::vector<Candidate> candidates;
        for (auto const& value : batch) candidates.push_back(value.candidate);
        persisting = true;
        std::weak_ptr<State> weak = shared_from_this();
        callbacks.persist(std::move(batch), [weak, candidates = std::move(candidates)]() mutable
        {
            if (auto state = weak.lock(); state && !state->stopped)
            {
                state->classified = std::move(candidates);
                state->persisting = false;
            }
        });
    }

    void Tick()
    {
        if (stopped) return;
        auto now = Clock::now();
        Acquire();
        Persist(now);
        Classify(now);
        Discover(now);
        Schedule(now);
        Dispatch(now);
        statistics.peakFetching = std::max(statistics.peakFetching, metadata.size());
        statistics.peakProbing = std::max(statistics.peakProbing, Active(CrawlerQuery::Peers));
    }

    void Stop()
    {
        if (stopped) return;
        Receive();
        stopped = true;
        requester.Cancel();
        auto batch = retained.Drain();
        for (auto& [requestId, work] : metadata)
            if (work.info) batch.push_back({std::move(work.candidate), std::move(*work.info)});
        metadata.clear();
        if (!batch.empty()) callbacks.persist(std::move(batch), [] {});
        auto estimates = observations.Drain();
        if (!estimates.empty() && callbacks.observe)
            callbacks.observe(std::move(estimates));
        queries.clear();
        for (auto& queue : queues) queue.clear();
        ready.clear();
    }

    void Receive()
    {
        for (auto& result : requester.Drain())
        {
            auto found = metadata.find(result.requestId);
            if (found == metadata.end()) continue;
            statistics.connections += result.attempts.size();
            for (auto const& attempt : result.attempts)
            {
                statistics.connected += attempt.connected;
                statistics.metadataSupported += attempt.metadataSupported;
                if (attempt.failure) ++statistics.failures[static_cast<std::size_t>(*attempt.failure)];
                constexpr std::array<std::chrono::microseconds, 5> bounds{100ms, 500ms, 1s, 3s, 6s};
                auto bucket = std::lower_bound(bounds.begin(), bounds.end(), attempt.elapsed) - bounds.begin();
                ++statistics.latencies[bucket];
                auto waiting = static_cast<std::uint64_t>(attempt.waiting.count());
                statistics.limiterMicros += waiting;
                statistics.limiterMaxMicros = std::max(statistics.limiterMaxMicros, waiting);
            }
            if (result.failure || result.info.empty())
            {
                ++statistics.metadataFailed;
                metadata.erase(found);
                continue;
            }
            ++statistics.metadataReceived;
            statistics.metadataBytes += result.info.size();
            found->second.info = std::move(result.info);
        }
    }
};

BitmagnetCrawler::BitmagnetCrawler(Config config, MetadataRequester& requester, Callbacks callbacks,
    lt::sha1_hash origin)
    : state_(std::make_shared<State>(std::move(config), requester, std::move(callbacks), origin)) {}

BitmagnetCrawler::~BitmagnetCrawler()
{
    Stop();
}

void BitmagnetCrawler::Tick()
{
    state_->Tick();
}

void BitmagnetCrawler::Seed(std::vector<Catalogue::Node> nodes)
{
    if (state_->stopped) return;
    auto now = Clock::now();
    auto seconds = std::chrono::system_clock::to_time_t(std::chrono::system_clock::now());
    for (auto const& node : nodes)
    {
        auto endpoint = Endpoint({node.address, node.port});
        if (!endpoint) continue;
        State::Node value{Hash(node.nodeId), *endpoint, {}, now + std::chrono::seconds(std::max<std::int64_t>(0, node.eligible - seconds))};
        if (value.id.is_all_zeros()) state_->Enqueue({CrawlerQuery::Ping, std::move(value)});
        else state_->Responded(std::move(value), now - 6s);
    }
}

void BitmagnetCrawler::Response(std::uintptr_t requestId, lt::bdecode_node response)
{
    state_->Response(requestId, response);
}

void BitmagnetCrawler::Stop()
{
    if (state_) state_->Stop();
}

BitmagnetCrawler::Statistics BitmagnetCrawler::Stats() const
{
    auto result = state_->statistics;
    result.fetching = state_->metadata.size();
    result.probing = state_->Active(CrawlerQuery::Peers);
    result.fetchingLimit = state_->FetchingLimit();
    result.probingLimit = state_->Concurrency(CrawlerQuery::Peers);
    result.queued = state_->triage.Size() + state_->ready.size();
    if (state_->selection) result.queued += state_->selection->missing.size() - state_->nextMissing;
    result.queued += state_->queues[State::Slot(CrawlerQuery::Peers)].size();
    result.nodes = state_->nodes.size();
    return result;
}
}
