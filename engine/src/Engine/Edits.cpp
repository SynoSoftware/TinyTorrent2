#include "Engine/State.h"
#include <libtorrent/aux_/parse_url.hpp>
#include <libtorrent/aux_/string_util.hpp>
#include <algorithm>
#include <climits>
#include <set>

namespace tt
{
lt::download_priority_t Engine::State::ResolvedChoice::Apply(lt::download_priority_t current) const
{
    if (auto const* priority = std::get_if<lt::download_priority_t>(&action))
    {
        return *priority;
    }
    return current == lt::dont_download ? lt::default_priority : current;
}

std::expected<Engine::State::EditChoices, ErrorCode> Engine::State::ReadChoices(Json const& changes)
{
    if (!changes.is_object() || changes.empty())
    {
        return std::unexpected(ErrorCode::InvalidRequest);
    }
    // Reads a file choice's action, `priority` or `wanted: true`; nothing
    // when it has neither or both, and an invalid priority when it is not
    // one a person picks.
    auto readAction = [](Json const& entry, bool& invalid) -> std::optional<FileAction>
    {
        if (entry.contains("priority") == entry.contains("wanted"))
        {
            return std::nullopt;
        }
        if (entry.contains("wanted"))
        {
            if (entry["wanted"] != true)
            {
                return std::nullopt;
            }
            return Want{};
        }
        auto const& value = entry["priority"];
        if (!value.is_number_integer() || value < 0 || value > static_cast<std::uint8_t>(lt::top_priority) ||
            !IsChoice(lt::download_priority_t(value.get<std::uint8_t>())))
        {
            invalid = true;
            return std::nullopt;
        }
        return lt::download_priority_t(value.get<std::uint8_t>());
    };
    EditChoices choices;
    std::vector<FileChoice> folders;
    std::optional<FileChoice> query;
    std::vector<FileChoice> files;
    std::set<int> indexes;
    for (auto const& [key, values] : changes.items())
    {
        if ((key == "files" || key == "folders") && values.is_array())
        {
            for (auto const& entry : values)
            {
                bool invalid = false;
                auto action = entry.is_object() ? readAction(entry, invalid) : std::nullopt;
                if (!action)
                {
                    return std::unexpected(invalid ? ErrorCode::InvalidPriorities : ErrorCode::InvalidRequest);
                }
                if (key == "folders")
                {
                    if (!entry.contains("path") || !entry["path"].is_string())
                    {
                        return std::unexpected(ErrorCode::InvalidRequest);
                    }
                    folders.push_back({Tree::Folder{entry["path"].get<std::string>()}, *action});
                    continue;
                }
                if (!entry.contains("index") || !entry["index"].is_number_integer() || entry["index"] < 0 ||
                    entry["index"] > INT_MAX)
                {
                    return std::unexpected(ErrorCode::InvalidPriorities);
                }
                auto index = entry["index"].get<int>();
                if (!indexes.insert(index).second)
                {
                    return std::unexpected(ErrorCode::InvalidPriorities);
                }
                files.push_back({lt::file_index_t(index), *action});
            }
        }
        else if (key == "query" && values.is_object() && values.contains("text") && values["text"].is_string())
        {
            bool invalid = false;
            auto action = readAction(values, invalid);
            if (!action)
            {
                return std::unexpected(invalid ? ErrorCode::InvalidPriorities : ErrorCode::InvalidRequest);
            }
            query = FileChoice{Search(values["text"].get<std::string>()), *action};
        }
        else if (key == "trackers" && values.is_array())
        {
            choices.trackers.emplace();
            std::set<std::string> urls;
            for (auto const& entry : values)
            {
                if (!entry.is_object() || !entry.contains("url") || !entry["url"].is_string() ||
                    !entry.contains("tier") || !entry["tier"].is_number_integer() ||
                    entry["tier"] < 0 || entry["tier"] > 255)
                {
                    return std::unexpected(ErrorCode::InvalidTrackers);
                }
                auto url = entry["url"].get<std::string>();
                if ((!lt::aux::string_begins_no_case("http://", url) &&
                    !lt::aux::string_begins_no_case("https://", url) &&
                    !lt::aux::string_begins_no_case("udp://", url)) ||
                    !lt::aux::is_valid_tracker_url(url))
                {
                    return std::unexpected(ErrorCode::InvalidTrackers);
                }
                if (!urls.insert(url).second)
                {
                    continue;
                }
                choices.trackers->emplace_back(url);
                choices.trackers->back().tier = entry["tier"].get<std::uint8_t>();
            }
            std::stable_sort(choices.trackers->begin(), choices.trackers->end(),
                [](auto const& left, auto const& right) { return left.tier < right.tier; });
        }
        else
        {
            return std::unexpected(ErrorCode::InvalidRequest);
        }
    }
    // A query applies to what the search shows, so the browser sends it alone.
    if (query && (!folders.empty() || !files.empty()))
    {
        return std::unexpected(ErrorCode::InvalidRequest);
    }
    // Folders apply before files, so a file chosen after its folder keeps its
    // own choice.
    choices.files = std::move(folders);
    if (query)
    {
        choices.files.push_back(std::move(*query));
    }
    std::move(files.begin(), files.end(), std::back_inserter(choices.files));
    return choices;
}

void Engine::State::Edit(std::string const& torrentId, EditChoices choices, std::vector<std::string> loaded,
    std::function<void(Outcome, Json)> completion)
{
    auto commit = [this, torrentId, trackers = std::move(choices.trackers), loaded = std::move(loaded), completion]
        (std::vector<ResolvedChoice> files)
    {
        bool chosen = !files.empty();
        Act({torrentId}, [this, torrentId, chosen, loaded, completion](Outcome outcome)
        {
            auto found = torrents.find(torrentId);
            if (outcome.error || !chosen || found == torrents.end())
            {
                completion(std::move(outcome), Json::object());
                return;
            }
            auto listing = Listed(found->second);
            if (!listing)
            {
                completion(listing.error(), Json::object());
                return;
            }
            DescribeChoices(std::move(*listing), loaded, completion);
        }, [this, files = std::move(files), trackers](auto const& ids, auto done)
        {
            CommitEdit(ids.front(), files, trackers, done);
        });
    };
    if (choices.files.empty())
    {
        commit({});
        return;
    }
    auto found = torrents.find(torrentId);
    if (found == torrents.end())
    {
        completion({ErrorCode::TorrentRemoved}, {});
        return;
    }
    auto listing = Listed(found->second);
    if (!listing)
    {
        completion(listing.error(), {});
        return;
    }
    Resolve(std::move(*listing), std::move(choices.files),
        [commit, completion](std::expected<std::vector<ResolvedChoice>, Outcome> resolved)
    {
        if (!resolved)
        {
            completion(resolved.error(), {});
            return;
        }
        commit(std::move(*resolved));
    });
}

void Engine::State::Edit(Preview& preview, EditChoices choices, std::vector<std::string> loaded,
    std::function<void(Outcome, Json)> completion)
{
    if (choices.trackers)
    {
        completion({ErrorCode::InvalidRequest}, {});
        return;
    }
    auto listing = Listed(preview);
    if (!listing)
    {
        completion(listing.error(), {});
        return;
    }
    Resolve(std::move(*listing), std::move(choices.files),
        [this, previewId = preview.previewId, loaded = std::move(loaded), completion]
        (std::expected<std::vector<ResolvedChoice>, Outcome> resolved)
    {
        if (!resolved)
        {
            completion(resolved.error(), {});
            return;
        }
        // The connection that opened the preview can close meanwhile.
        auto found = previews.find(previewId);
        if (found == previews.end())
        {
            completion({ErrorCode::PreviewExpired}, {});
            return;
        }
        auto& preview = found->second;
        auto chosen = ApplyChoices(*resolved, preview.priorities, preview.params.ti);
        if (!chosen)
        {
            completion({ErrorCode::InvalidPriorities}, {});
            return;
        }
        preview.priorities = std::move(*chosen);
        auto listing = Listed(preview);
        if (!listing)
        {
            completion(listing.error(), {});
            return;
        }
        DescribeChoices(std::move(*listing), loaded, completion);
    });
}

void Engine::State::Resolve(Listing listing, std::vector<FileChoice> choices,
    std::function<void(std::expected<std::vector<ResolvedChoice>, Outcome>)> completion)
{
    trees.Run([listing = std::move(listing), choices = std::move(choices)]() mutable
    {
        // Only folder and query choices need the tree of every path.
        std::optional<Tree> tree;
        auto count = listing.metadata->num_files();
        std::vector<ResolvedChoice> resolved;
        for (auto const& choice : choices)
        {
            if (auto const* file = std::get_if<lt::file_index_t>(&choice.target))
            {
                if (static_cast<int>(*file) >= count)
                {
                    return std::optional<std::vector<ResolvedChoice>>();
                }
                resolved.push_back({{*file}, choice.action});
                continue;
            }
            if (!tree)
            {
                tree.emplace(std::move(listing));
            }
            auto files = tree->Files(choice.target);
            if (!files)
            {
                return std::optional<std::vector<ResolvedChoice>>();
            }
            resolved.push_back({std::move(*files), choice.action});
        }
        return std::optional(std::move(resolved));
    }, [completion](std::expected<std::optional<std::vector<ResolvedChoice>>, StorageOutcome> resolved)
    {
        if (!resolved)
        {
            completion(std::unexpected(resolved.error().ToOutcome(ErrorCode::InvalidRequest)));
        }
        else if (!*resolved)
        {
            completion(std::unexpected(Outcome{ErrorCode::InvalidPriorities}));
        }
        else
        {
            completion(std::move(**resolved));
        }
    });
}

std::optional<std::vector<lt::download_priority_t>> Engine::State::ApplyChoices(
    std::vector<ResolvedChoice> const& choices, std::vector<lt::download_priority_t> priorities,
    std::shared_ptr<lt::torrent_info const> const& metadata)
{
    for (auto const& choice : choices)
    {
        for (auto file : choice.files)
        {
            auto& priority = priorities[static_cast<int>(file)];
            priority = choice.Apply(priority);
        }
    }
    return Priorities(std::move(priorities), metadata);
}

void Engine::State::DescribeChoices(Listing listing, std::vector<std::string> loaded,
    std::function<void(Outcome, Json)> completion)
{
    trees.Run([listing = std::move(listing), loaded = std::move(loaded)]() mutable
    {
        Tree tree(std::move(listing));
        auto data = tree.Totals();
        data["folders"] = tree.Values(loaded);
        return data;
    }, [completion](std::expected<Json, StorageOutcome> described)
    {
        if (!described)
            completion(described.error().ToOutcome(ErrorCode::InvalidRequest), {});
        else
            completion({}, std::move(*described));
    });
}

void Engine::State::CommitEdit(std::string const& id, std::vector<ResolvedChoice> const& choices,
    std::optional<std::vector<lt::announce_entry>> const& trackers, std::function<void(Outcome)> completion)
{
    auto& torrent = torrents.at(id);
    if (torrent.HeldByAlias())
    {
        completion({ErrorCode::AliasConflict, torrent.conflict});
        return;
    }
    if (!choices.empty() && torrent.priorityCompletion)
    {
        completion({ErrorCode::Overloaded});
        return;
    }
    auto facts = torrent.facts;
    if (!choices.empty())
    {
        auto metadata = torrent.Metadata();
        if (!metadata)
        {
            completion({ErrorCode::MetadataUnavailable});
            return;
        }
        auto chosen = ApplyChoices(choices, torrent.ChosenPriorities(metadata), metadata);
        if (!chosen)
        {
            completion({ErrorCode::InvalidPriorities});
            return;
        }
        facts.priorities = std::move(*chosen);
    }
    bool hasPriorities = !choices.empty();
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
        // Its add applies the saved trackers and, through ApplyIntent, the
        // saved priorities, which its parameters also carry.
        if (torrent.waiting)
        {
            if (hasPriorities)
            {
                torrent.waiting->params.file_priorities = torrent.facts.priorities;
            }
            completion({});
            return;
        }
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
            torrent.prioritiesPending = false;
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
    if (!torrent.waiting && (torrent.priorityCompletion || torrent.piecesPending) && torrent.handle.torrent_file())
    {
        Query(torrent, DetailKind::Priorities);
    }
}
}
