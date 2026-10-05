#include "Engine/State.h"
#include <libtorrent/aux_/parse_url.hpp>
#include <libtorrent/aux_/string_util.hpp>
#include <algorithm>
#include <climits>
#include <set>

namespace tt
{
void Engine::State::Edit(std::string const& id, Json const& choices, Reply reply)
{
    if (!choices.is_object() || choices.empty())
    {
        reply(Failure(ErrorCode::InvalidRequest));
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
                    !entry["priority"].is_number_integer() || entry["priority"] < 0 ||
                    entry["priority"] > static_cast<std::uint8_t>(lt::top_priority))
                {
                    reply(Failure(ErrorCode::InvalidPriorities));
                    return;
                }
                auto index = entry["index"].get<int>();
                auto priority = lt::download_priority_t(entry["priority"].get<std::uint8_t>());
                if (!IsChoice(priority) || !priorities.emplace(index, priority).second)
                {
                    reply(Failure(ErrorCode::InvalidPriorities));
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
                    reply(Failure(ErrorCode::InvalidTrackers));
                    return;
                }
                auto url = entry["url"].get<std::string>();
                if ((!lt::aux::string_begins_no_case("http://", url) &&
                    !lt::aux::string_begins_no_case("https://", url) &&
                    !lt::aux::string_begins_no_case("udp://", url)) ||
                    !lt::aux::is_valid_tracker_url(url))
                {
                    reply(Failure(ErrorCode::InvalidTrackers));
                    return;
                }
                if (!urls.insert(url).second)
                {
                    continue;
                }
                trackers->emplace_back(url);
                trackers->back().tier = entry["tier"].get<std::uint8_t>();
            }
            std::stable_sort(trackers->begin(), trackers->end(),
                [](auto const& left, auto const& right) { return left.tier < right.tier; });
        }
        else
        {
            reply(Failure(ErrorCode::InvalidRequest));
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
            reply(Failure(ErrorCode::Overloaded));
            return;
        }
        auto facts = torrent.facts;
        if (!priorities.empty())
        {
            auto metadata = torrent.handle.torrent_file();
            if (!metadata)
            {
                reply(Failure(ErrorCode::MetadataUnavailable));
                return;
            }
            if (facts.priorities.empty())
            {
                facts.priorities = DefaultPriorities(metadata->layout());
            }
            for (auto const& [index, priority] : priorities)
            {
                if (index >= metadata->num_files())
                {
                    reply(Failure(ErrorCode::InvalidPriorities));
                    return;
                }
                facts.priorities[index] = priority;
            }
            auto chosen = Priorities(facts.priorities, metadata);
            if (!chosen)
            {
                reply(Failure(ErrorCode::InvalidPriorities));
                return;
            }
            facts.priorities = std::move(*chosen);
        }
        if (trackers)
        {
            facts.trackers = trackers;
        }
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
            if (outcome.succeeded)
            {
                apply();
            }
            else
            {
                reply(Failure(ErrorCode::StorageFailed, outcome.detail));
            }
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
}
