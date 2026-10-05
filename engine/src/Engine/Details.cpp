#include "Engine/State.h"
#include <libtorrent/aux_/parse_url.hpp>
#include <libtorrent/aux_/string_util.hpp>
#include <algorithm>
#include <climits>
#include <set>

namespace tiny
{
void Engine::State::Edit(std::string const& id, Json const& choices, Reply reply)
{
    if (!choices.is_object() || choices.empty())
    {
        reply(Failure("invalid_request"));
        return;
    }
    std::map<int, lt::download_priority_t> priorities;
    std::optional<std::vector<lt::announce_entry>> trackers;
    for (auto const& [key, values] : choices.items())
    {
        if (key == "priorities" && values.is_array())
        {
            for (auto const& entry : values)
            {
                if (!entry.is_object() || !entry.contains("index") || !entry.contains("priority") ||
                    !entry["index"].is_number_integer() || entry["index"] < 0 || entry["index"] > INT_MAX ||
                    !entry["priority"].is_number_integer() || entry["priority"] < 0 || entry["priority"] > 7)
                {
                    reply(Failure("invalid_priorities"));
                    return;
                }
                auto index = entry["index"].get<int>();
                auto priority = lt::download_priority_t(entry["priority"].get<std::uint8_t>());
                if (!IsChoice(priority) || !priorities.emplace(index, priority).second)
                {
                    reply(Failure("invalid_priorities"));
                    return;
                }
            }
        }
        else if (key == "trackers" && values.is_array())
        {
            trackers.emplace();
            std::set<std::string> urls;
            for (auto const& entry : values)
            {
                if (!entry.is_object() || !entry.contains("url") || !entry["url"].is_string() ||
                    !entry.contains("tier") || !entry["tier"].is_number_integer() ||
                    entry["tier"] < 0 || entry["tier"] > 255)
                {
                    reply(Failure("invalid_trackers"));
                    return;
                }
                auto url = entry["url"].get<std::string>();
                if ((!lt::aux::string_begins_no_case("http://", url) &&
                    !lt::aux::string_begins_no_case("https://", url) &&
                    !lt::aux::string_begins_no_case("udp://", url)) ||
                    !lt::aux::is_valid_tracker_url(url))
                {
                    reply(Failure("invalid_trackers"));
                    return;
                }
                if (!urls.insert(url).second) continue;
                trackers->emplace_back(url);
                trackers->back().tier = entry["tier"].get<std::uint8_t>();
            }
            std::stable_sort(trackers->begin(), trackers->end(),
                [](auto const& left, auto const& right) { return left.tier < right.tier; });
        }
        else
        {
            reply(Failure("invalid_request"));
            return;
        }
    }
    Act({id}, reply, [this, priorities = std::move(priorities), trackers = std::move(trackers)]
        (auto const& ids, Reply reply)
    {
        auto const& id = ids.front();
        auto& torrent = torrents.at(id);
        if (!priorities.empty() && torrent.priorityReply)
        {
            reply(Failure("overloaded"));
            return;
        }
        auto facts = torrent.facts;
        if (!priorities.empty())
        {
            auto metadata = torrent.handle.torrent_file();
            if (!metadata)
            {
                reply(Failure("metadata_unavailable"));
                return;
            }
            if (facts.priorities.empty())
            {
                for (auto index : metadata->layout().file_range())
                {
                    facts.priorities.push_back(DefaultPriority(metadata->layout(), index));
                }
            }
            for (auto const& [index, priority] : priorities)
            {
                if (index >= metadata->num_files())
                {
                    reply(Failure("invalid_priorities"));
                    return;
                }
                facts.priorities[index] = priority;
            }
            auto chosen = Priorities(facts.priorities, metadata);
            if (!chosen)
            {
                reply(Failure("invalid_priorities"));
                return;
            }
            facts.priorities = std::move(*chosen);
        }
        if (trackers) facts.trackers = trackers;
        auto apply = [this, id, facts, priorities, trackers, reply]
        {
            auto& torrent = torrents.at(id);
            torrent.facts = facts;
            torrent.unsaved = true;
            if (trackers)
            {
                auto current = torrent.handle.trackers();
                if (current.size() != trackers->size() || !std::equal(current.begin(), current.end(),
                    trackers->begin(), [](auto const& left, auto const& right)
                    { return left.url == right.url && left.tier == right.tier; }))
                {
                    torrent.handle.replace_trackers(*trackers);
                }
            }
            if (!priorities.empty() && torrent.handle.get_file_priorities() != facts.priorities)
            {
                torrent.priorityReply = reply;
                torrent.handle.prioritize_files(facts.priorities);
            }
            else
            {
                reply(Success());
            }
        };
        if (facts.ToJson() == torrent.facts.ToJson())
        {
            apply();
            return;
        }
        auto document = Saved();
        document.torrents.at(id) = facts;
        changes.Commit(document.ToJson(), [apply, reply](StorageOutcome outcome)
        {
            if (outcome.succeeded) apply();
            else reply(Failure("storage_failed", outcome.detail));
        });
    });
}

void Engine::State::CompletePriorities(Torrent& torrent)
{
    if (!torrent.priorityReply)
    {
        return;
    }
    if (torrent.handle.get_file_priorities() != torrent.facts.priorities)
    {
        return;
    }
    torrent.unsaved = true;
    std::exchange(torrent.priorityReply, nullptr)(Success());
}

void Engine::State::SampleHistory()
{
    auto time = std::chrono::system_clock::to_time_t(std::chrono::system_clock::now());
    auto activity = Activity();
    SpeedSample sample{time, double(activity.downloadRate), double(activity.uploadRate)};
    if (!seconds.empty() && time <= seconds.back().time)
    {
        seconds.clear();
        minutes.clear();
    }
    if (minuteCount && (time <= minute.time || time - minute.time > 2))
    {
        minute = {};
        minuteCount = 0;
    }
    if (minuteCount && time / 60 != minute.time / 60)
    {
        minutes.push_back({minute.time, minute.download / minuteCount, minute.upload / minuteCount});
        minute = {};
        minuteCount = 0;
    }
    minute.time = time;
    minute.download += sample.download;
    minute.upload += sample.upload;
    ++minuteCount;
    seconds.push_back(sample);
    while (!seconds.empty() && (seconds.size() > 300 || seconds.front().time <= time - 300))
    {
        seconds.pop_front();
    }
    while (!minutes.empty() && (minutes.size() > 1439 || minutes.front().time <= time - 86400))
    {
        minutes.pop_front();
    }
}

Json Engine::State::History(bool day) const
{
    Json samples = Json::array();
    auto append = [&samples](SpeedSample const& sample)
    {
        samples.push_back({{"time", sample.time}, {"download_rate", sample.download},
            {"upload_rate", sample.upload}});
    };
    for (auto const& sample : day ? minutes : seconds) append(sample);
    if (day && minuteCount)
    {
        append({minute.time, minute.download / minuteCount, minute.upload / minuteCount});
    }
    return {{"session_id", sessionId}, {"samples", std::move(samples)}};
}
}
