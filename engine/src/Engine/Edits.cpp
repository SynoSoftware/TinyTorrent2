#include "Engine/State.h"
#include <libtorrent/aux_/parse_url.hpp>
#include <libtorrent/aux_/string_util.hpp>
#include <algorithm>
#include <climits>
#include <set>

namespace tt
{
void Engine::State::Edit(std::string const& id, Json const& choices, std::function<void(Outcome)> completion)
{
    if (!choices.is_object() || choices.empty())
    {
        completion({ErrorCode::InvalidRequest});
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
                    completion({ErrorCode::InvalidPriorities});
                    return;
                }
                auto index = entry["index"].get<int>();
                auto priority = lt::download_priority_t(entry["priority"].get<std::uint8_t>());
                if (!IsChoice(priority) || !priorities.emplace(index, priority).second)
                {
                    completion({ErrorCode::InvalidPriorities});
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
                    completion({ErrorCode::InvalidTrackers});
                    return;
                }
                auto url = entry["url"].get<std::string>();
                if ((!lt::aux::string_begins_no_case("http://", url) &&
                    !lt::aux::string_begins_no_case("https://", url) &&
                    !lt::aux::string_begins_no_case("udp://", url)) ||
                    !lt::aux::is_valid_tracker_url(url))
                {
                    completion({ErrorCode::InvalidTrackers});
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
            completion({ErrorCode::InvalidRequest});
            return;
        }
    }
    Act({id}, completion, [this, priorities = std::move(priorities), trackers = std::move(trackers)]
        (auto const& ids, auto done)
    {
        CommitEdit(ids.front(), priorities, trackers, done);
    });
}

void Engine::State::CommitEdit(std::string const& id,
    std::map<int, lt::download_priority_t> const& priorities,
    std::optional<std::vector<lt::announce_entry>> const& trackers, std::function<void(Outcome)> completion)
{
    auto& torrent = torrents.at(id);
    if (torrent.restore)
    {
        completion({ErrorCode::AliasConflict, torrent.conflict});
        return;
    }
    if (!priorities.empty() && torrent.priorityCompletion)
    {
        completion({ErrorCode::Overloaded});
        return;
    }
    auto facts = torrent.facts;
    if (!priorities.empty())
    {
        auto metadata = torrent.handle.torrent_file();
        if (!metadata)
        {
            completion({ErrorCode::MetadataUnavailable});
            return;
        }
        if (facts.priorities.empty())
        {
            facts.priorities = DefaultPriorities(metadata->layout(), facts.skipPatterns);
        }
        for (auto const& [index, priority] : priorities)
        {
            if (index >= metadata->num_files())
            {
                completion({ErrorCode::InvalidPriorities});
                return;
            }
            facts.priorities[index] = priority;
        }
        auto chosen = Priorities(facts.priorities, metadata);
        if (!chosen)
        {
            completion({ErrorCode::InvalidPriorities});
            return;
        }
        facts.priorities = std::move(*chosen);
    }
    bool hasPriorities = !priorities.empty();
    CommitFacts({id}, [hasPriorities, chosen = std::move(facts.priorities), trackers](std::string const&, Facts& saved)
    {
        if (hasPriorities)
        {
            saved.priorities = chosen;
        }
        if (trackers)
        {
            saved.trackers = trackers;
        }
    }, [this, id, hasPriorities, hasTrackers = trackers.has_value(), completion](Outcome outcome, auto const&)
    {
        if (outcome.error)
        {
            completion(std::move(outcome));
            return;
        }
        auto& torrent = torrents.at(id);
        torrent.unsaved = true;
        if (hasTrackers)
        {
            torrent.handle.replace_trackers(*torrent.facts.trackers);
        }
        // Changing which files download can move data between files, so it
        // waits for the torrent's busy files; the choice is already saved.
        if (hasPriorities && torrent.FilesBusy())
        {
            torrent.prioritiesPending = true;
            completion({});
        }
        else if (hasPriorities)
        {
            torrent.priorityCompletion = completion;
            torrent.handle.prioritize_files(torrent.facts.priorities);
            Invalidate(id);
            QueryPriorities(torrent);
        }
        else
        {
            completion({});
        }
    });
}

void Engine::State::QueryPriorities(Torrent& torrent)
{
    if (!torrent.restore && (torrent.priorityCompletion || torrent.piecesPending) && torrent.handle.torrent_file())
    {
        Query(torrent, DetailKind::Priorities);
    }
}
}
