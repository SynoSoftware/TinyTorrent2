#include "Engine/State.h"
#include <algorithm>

namespace tiny
{
namespace
{
constexpr std::pair<std::string_view, Command> commands[] = {
    {"snapshot", Command::Snapshot},
    {"settings", Command::Settings},
    {"session_pause", Command::SessionPause},
    {"preview", Command::Preview},
    {"preview_detail", Command::PreviewDetail},
    {"cancel_preview", Command::CancelPreview},
    {"add", Command::Add},
    {"merge_trackers", Command::MergeTrackers},
    {"torrent", Command::Torrent},
    {"edit", Command::Edit},
    {"reannounce", Command::Reannounce},
    {"history", Command::History},
    {"pause", Command::Pause},
    {"resume", Command::Resume},
    {"force", Command::Force},
    {"verify", Command::Verify},
    {"remove", Command::Remove},
    {"file_scope", Command::FileScope},
    {"move", Command::Move},
    {"delete_files", Command::DeleteFiles},
    {"queue", Command::Queue}};

constexpr std::pair<std::string_view, QueueMove> moves[] = {
    {"up", QueueMove::Up},
    {"down", QueueMove::Down},
    {"top", QueueMove::Top},
    {"bottom", QueueMove::Bottom},
    {"before", QueueMove::Before}};

constexpr std::pair<std::string_view, TorrentView> views[] = {
    {"general", TorrentView::General},
    {"files", TorrentView::Files},
    {"peers", TorrentView::Peers},
    {"trackers", TorrentView::Trackers},
    {"pieces", TorrentView::Pieces}};

std::vector<std::string> TargetIds(Json const& request)
{
    return request.at("torrent_ids").get<std::vector<std::string>>();
}
}

// Reads each request into typed values; the operations it calls never see
// the request.
void Engine::State::Execute(Json const& request, Reply reply)
{
    auto command = Parse(commands, request.at("command").get<std::string>());
    if (command == Command::Snapshot)
    {
        reply(Success(Snapshot()));
        return;
    }
    if (stopping)
    {
        reply(Failure("stopping"));
        return;
    }
    // Without a session after loading, startup failed.
    if (loading || !session)
    {
        reply(Failure(loading ? "starting" : "storage_failed"));
        return;
    }
    if (!command)
    {
        reply(Failure("unknown_command"));
        return;
    }
    auto connection = request.value("connection_id", "");
    switch (*command)
    {
    case Command::Snapshot:
        // Answered above, even while stopping or loading.
        break;
    case Command::Settings:
        Configure(request.at("changes"), reply);
        break;
    case Command::SessionPause:
        PauseSession(request.at("paused").get<bool>(), reply);
        break;
    case Command::Preview:
        Inspect(request.at("source").get<std::string>(), connection, request.value("destination", ""), reply);
        break;
    case Command::PreviewDetail:
    {
        auto preview = FindPreview(request.at("preview_id").get<std::string>(), connection);
        if (!preview)
        {
            reply(Failure("preview_expired"));
            return;
        }
        UpdatePreview(*preview);
        reply(Success(Describe(*preview, request.value("destination", ""))));
        break;
    }
    case Command::CancelPreview:
    {
        auto id = request.at("preview_id").get<std::string>();
        Discard([&](Preview const& preview)
        {
            return preview.identity == id && preview.connection == connection;
        });
        reply(Success());
        break;
    }
    case Command::Add:
    {
        auto preview = FindPreview(request.at("preview_id").get<std::string>(), connection);
        if (!preview)
        {
            reply(Failure("preview_expired"));
            return;
        }
        Add(*preview, request.at("destination").get<std::string>(),
            ReadPriorities(request.value("priorities", Json::array())), request.value("paused", false),
            reply);
        break;
    }
    case Command::MergeTrackers:
    {
        auto preview = FindPreview(request.at("preview_id").get<std::string>(), connection);
        if (!preview)
        {
            reply(Failure("preview_expired"));
            return;
        }
        MergeTrackers(*preview, request.at("torrent_id").get<std::string>(), reply);
        break;
    }
    case Command::Torrent:
    {
        auto found = torrents.find(request.at("torrent_id").get<std::string>());
        if (found == torrents.end())
        {
            reply(Failure("torrent_removed"));
            return;
        }
        if (!request.contains("view"))
        {
            reply(Success(found->second.Describe()));
            break;
        }
        auto view = Parse(views, request.at("view").get<std::string>());
        if (!view)
        {
            reply(Failure("invalid_request"));
            return;
        }
        auto data = found->second.Describe(*view, request.value("include_files", false));
        data["session_id"] = sessionId;
        reply(Success(std::move(data)));
        break;
    }
    case Command::Edit:
        Edit(request.at("torrent_id").get<std::string>(), request.at("changes"), reply);
        break;
    case Command::Reannounce:
        Act({request.at("torrent_id").get<std::string>()}, reply, [this](auto const& ids, Reply reply)
        {
            torrents.at(ids.front()).handle.force_reannounce();
            reply(Success());
        });
        break;
    case Command::History:
    {
        auto range = request.at("range").get<std::string>();
        if (range != "five_minutes" && range != "day")
        {
            reply(Failure("invalid_request"));
            return;
        }
        reply(Success(History(range == "day")));
        break;
    }
    case Command::Pause:
        Act(TargetIds(request), reply, [this](auto const& ids, Reply reply)
        {
            SetIntent(ids, Intent::Paused, reply);
        });
        break;
    case Command::Resume:
        Act(TargetIds(request), reply, [this](auto const& ids, Reply reply)
        {
            SetIntent(ids, Intent::Resumed, reply);
        });
        break;
    case Command::Force:
        Act(TargetIds(request), reply, [this](auto const& ids, Reply reply)
        {
            SetIntent(ids, Intent::Forced, reply);
        });
        break;
    case Command::Verify:
        Act(TargetIds(request), reply, [this](auto const& ids, Reply reply)
        {
            Verify(ids, reply);
        });
        break;
    case Command::Remove:
        Act(TargetIds(request), reply, [this](auto const& ids, Reply reply)
        {
            Remove(ids, reply);
        });
        break;
    case Command::FileScope:
        Act(TargetIds(request), reply, [this](auto const& ids, Reply reply)
        {
            if (FilesReady(ids, reply)) reply(Success(FileScope(ids)));
        });
        break;
    case Command::Move:
        Act(TargetIds(request), reply, [this, destination = request.at("destination").get<std::string>(),
            useExisting = request.value("use_existing", false)](auto const& ids, Reply reply)
        {
            Move(ids, destination, useExisting, reply);
        });
        break;
    case Command::DeleteFiles:
        Act(TargetIds(request), reply, [this](auto const& ids, Reply reply)
        {
            if (!FilesReady(ids, reply)) return;
            for (auto const& id : ids)
            {
                if (!torrents.at(id).facts.moveDestination.empty())
                {
                    reply(Failure("move_interrupted"));
                    return;
                }
            }
            Remove(ids, reply, true);
        });
        break;
    case Command::Queue:
    {
        // A queue command names either a direction or the torrent to move
        // before; a null torrent moves them to the end.
        auto move = request.contains("before_torrent_id") ? QueueMove::Before :
            Parse(moves, request.value("direction", ""));
        if (!move)
        {
            reply(Failure("invalid_request"));
            return;
        }
        auto target = request.value("before_torrent_id", Json());
        auto before = target.is_null() ? std::string() : target.get<std::string>();
        Act(TargetIds(request), reply, [this, move = *move, before](auto const& ids, Reply reply)
        {
            Queue(ids, move, before, reply);
        });
        break;
    }
    }
}

void Engine::State::MergeTrackers(Preview& preview, std::string const& id, Reply reply)
{
    UpdatePreview(preview);
    if (Duplicate(preview.InfoHashes()) != id)
    {
        reply(Failure("torrent_removed"));
        return;
    }
    if (!changes.Queue([this, id, urls = preview.params.trackers, reply]
    {
        if (!torrents.contains(id))
        {
            reply(Failure("torrent_removed"));
            return;
        }
        auto trackers = torrents.at(id).handle.trackers();
        for (auto const& url : Missing(urls, Urls(trackers)))
        {
            trackers.emplace_back(url);
        }
        auto document = Saved();
        document.torrents.at(id).trackers = trackers;
        changes.Commit(document.ToJson(), reply, [this, id, trackers]
        {
            auto& torrent = torrents.at(id);
            torrent.facts.trackers = trackers;
            torrent.handle.replace_trackers(trackers);
            torrent.unsaved = true;
            return Success();
        });
    }))
    {
        reply(Failure("overloaded"));
    }
}

// Runs the action after the changes queued before it, once every listed
// torrent still exists.
void Engine::State::Act(std::vector<std::string> ids, Reply reply, Action action)
{
    if (ids.empty() || ids.size() > targetLimit)
    {
        reply(Failure("invalid_targets"));
        return;
    }
    if (!changes.Queue([this, ids, reply, action]
    {
        for (auto const& id : ids)
        {
            if (!torrents.contains(id))
            {
                reply(Failure("torrent_removed"));
                return;
            }
            if (torrents.at(id).moving)
            {
                reply(Failure("files_busy"));
                return;
            }
        }
        action(ids, reply);
    }))
    {
        reply(Failure("overloaded"));
    }
}

void Engine::State::Verify(std::vector<std::string> const& ids, Reply reply)
{
    for (auto const& id : ids)
    {
        if (!torrents.at(id).handle.torrent_file())
        {
            reply(Failure("metadata_unavailable"));
            return;
        }
    }
    for (auto const& id : ids)
    {
        torrents.at(id).receivedPayload = false;
        torrents.at(id).handle.force_recheck();
    }
    reply(Success());
}

void Engine::State::Remove(std::vector<std::string> const& ids, Reply reply, bool deleteData)
{
    std::optional<Deletion> deleting;
    std::size_t kept = 0;
    if (deleteData)
    {
        deleting.emplace();
        std::vector<std::filesystem::path> outside;
        for (auto const& [id, torrent] : torrents)
        {
            auto paths = FilePaths(torrent);
            auto& collection = Contains(ids, id) ? deleting->holds : outside;
            collection.insert(collection.end(), paths.begin(), paths.end());
        }
        std::sort(outside.begin(), outside.end(), PathBefore);
        std::sort(deleting->holds.begin(), deleting->holds.end(), PathBefore);
        auto& held = deleting->holds;
        held.erase(std::unique(held.begin(), held.end(), [](auto const& left, auto const& right)
            { return !PathBefore(left, right) && !PathBefore(right, left); }), held.end());
        for (auto const& path : held)
        {
            if (std::binary_search(outside.begin(), outside.end(), path, PathBefore)) ++kept;
            else deleting->files.push_back(path);
        }
        for (auto const& id : ids)
        {
            auto const& torrent = torrents.at(id);
            deleting->roots.push_back(FullPath(Wide(torrent.facts.savePath)));
            if (!deleting->names.empty()) deleting->names += ", ";
            deleting->names += torrent.Name();
        }
    }
    auto removed = [&ids](std::string const& id) { return Contains(ids, id); };
    auto document = Saved();
    std::erase_if(document.torrents, [&removed](auto const& entry) { return removed(entry.first); });
    std::erase_if(document.queueOrder, removed);
    if (deleting) deletion = deleting;
    auto complete = [this, deleteData, reply](Json outcome)
    {
        if (deleteData && !outcome.at("ok").get<bool>()) deletion.reset();
        reply(std::move(outcome));
    };
    changes.Commit(document.ToJson(), complete, [this, ids, order = document.queueOrder, deleting, kept]
    {
        if (deleting) deletion = deleting;
        queueOrder = order;
        std::vector<std::filesystem::path> files;
        for (auto const& id : ids)
        {
            auto found = torrents.find(id);
            if (found == torrents.end())
            {
                continue;
            }
            if (found->second.priorityReply)
            {
                std::exchange(found->second.priorityReply, nullptr)(Failure("torrent_removed"));
            }
            if (deleting)
            {
                deletion->waiting.push_back(found->second.handle);
                session->remove_torrent(found->second.handle, lt::session::delete_partfile);
            }
            else session->remove_torrent(found->second.handle);
            handles.erase(found->second.handle);
            torrents.erase(found);
            files.push_back(ResumeFile(id));
        }
        store.Run([files]
        {
            for (auto const& file : files)
            {
                std::filesystem::remove(file);
            }
        }, [this](StorageOutcome removed)
        {
            if (!removed.succeeded)
            {
                diagnostics.Write("remove", "", "metadata_cleanup_failed");
            }
        });
        return Success({{"kept_files", kept}});
    });
}

void Engine::State::SetIntent(std::vector<std::string> const& ids, Intent intent, Reply reply)
{
    if (intent != Intent::Paused)
    {
        for (auto const& id : ids)
        {
            if (!torrents.at(id).conflict.empty() &&
                !Duplicate(torrents.at(id).handle.info_hashes(), id).empty())
            {
                reply(Failure("alias_conflict"));
                return;
            }
        }
    }
    auto document = Saved();
    std::vector<std::string> seeds;
    for (auto const& id : ids)
    {
        document.torrents.at(id).intent = intent;
        if (intent != Intent::Paused && torrents.at(id).status.is_finished)
        {
            document.torrents.at(id).ignoresSeedLimits = true;
            seeds.push_back(id);
        }
    }
    changes.Commit(document.ToJson(), reply, [this, ids, intent, seeds]
    {
        for (auto const& id : ids)
        {
            auto& torrent = torrents.at(id);
            torrent.facts.intent = intent;
            if (Contains(seeds, id))
            {
                torrent.facts.ignoresSeedLimits = true;
            }
            if (intent != Intent::Paused)
            {
                if (torrent.facts.moveDestination.empty()) torrent.moveError.reset();
                torrent.conflict.clear();
                torrent.handle.clear_error();
                torrent.handle.unset_flags(lt::torrent_flags::upload_mode);
                torrent.diskError.clear();
            }
            torrent.ApplyIntent();
        }
        return Success();
    });
}
}
