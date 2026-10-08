#include "Engine/State.h"
#include <algorithm>
#include <climits>

namespace tt
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
    {"queue", Command::Queue},
    {"piece_order", Command::PieceOrder},
    {"speed_limit", Command::SpeedLimit},
    {"check_proxy", Command::CheckProxy},
    {"connection_test", Command::ConnectionTest}};

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

std::vector<std::string> TorrentIds(Json const& request)
{
    return request.at("torrent_ids").get<std::vector<std::string>>();
}
}

// Why the engine refuses commands now: it is shutting down or starting, or
// startup failed and left no session.
std::optional<ErrorCode> Engine::State::Refusal() const
{
    if (shuttingDown)
    {
        return ErrorCode::ShuttingDown;
    }
    if (startup != Startup::Ready)
    {
        return ErrorCode::Starting;
    }
    if (!session)
    {
        return ErrorCode::StorageFailed;
    }
    return std::nullopt;
}

// Reads each request into typed values; the operations it calls never see
// the request.
void Engine::State::Execute(Json const& request, std::string const& connectionId, Reply reply)
{
    auto command = Parse(commands, request.at("command").get<std::string>());
    if (command == Command::Snapshot)
    {
        reply(Success(Snapshot()));
        return;
    }
    if (auto refusal = Refusal())
    {
        reply(Failure(*refusal));
        return;
    }
    if (!command)
    {
        reply(Failure(ErrorCode::UnknownCommand));
        return;
    }
    switch (*command)
    {
    case Command::Snapshot:
        // Answered above, even while shutting down or loading.
        break;
    case Command::Settings:
        Configure(request.at("changes"), reply);
        break;
    case Command::ConnectionTest:
    {
        auto action = request.at("action").get<std::string>();
        if (action == "start")
        {
            if (!StartConnectionTest(connectionId))
            {
                reply(Failure(ErrorCode::InvalidRequest));
                break;
            }
        }
        else if (action == "cancel" || action == "release")
            ReleaseConnectionTest(connectionId, action == "cancel");
        else
        {
            reply(Failure(ErrorCode::InvalidRequest));
            break;
        }
        reply(Success(ConnectionTestSnapshot()));
        break;
    }
    case Command::CheckProxy:
    {
        // The proxy is read as the settings command reads it, so a proxy the
        // engine would refuse to save is refused here too.
        auto checked = settings.With(request.at("proxy"));
        if (!checked || checked->proxy.type == ProxyType::None || checked->proxy.host.empty() ||
            checked->proxy.port == 0)
        {
            reply(Failure(ErrorCode::InvalidRequest));
            return;
        }
        auto checkId = NewId();
        requestedCheck = RequestedCheck{checkId};
        CheckProxy(checked->proxy, [this, checkId](std::optional<ProxyCheck> check)
        {
            if (!requestedCheck || requestedCheck->checkId != checkId)
            {
                return;
            }
            if (check)
            {
                requestedCheck->result = check;
            }
            else
            {
                requestedCheck.reset();
            }
        });
        reply(Success({{"check_id", checkId}}));
        break;
    }
    case Command::SessionPause:
        PauseSession(request.at("paused").get<bool>(), [reply](Outcome outcome)
        {
            reply(outcome.error ? Failure(*outcome.error, outcome.detail) : Success());
        });
        break;
    case Command::Preview:
        Inspect(request.at("source").get<std::string>(), connectionId,
            [this, destination = request.value("destination", ""), reply](Outcome outcome, Preview* preview)
        {
            reply(outcome.error ? Failure(*outcome.error, outcome.detail) :
                Success(Describe(*preview, destination)));
        });
        break;
    case Command::PreviewDetail:
    {
        auto preview = FindPreview(request.at("preview_id").get<std::string>(), connectionId);
        if (!preview)
        {
            reply(Failure(ErrorCode::PreviewExpired));
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
            return preview.previewId == id && preview.connectionId == connectionId;
        });
        reply(Success());
        break;
    }
    case Command::Add:
    {
        auto preview = FindPreview(request.at("preview_id").get<std::string>(), connectionId);
        if (!preview)
        {
            reply(Failure(ErrorCode::PreviewExpired));
            return;
        }
        Addition::Choices choices;
        choices.destination = request.at("destination").get<std::string>();
        choices.intent = request.value("paused", !settings.startsDownload) ? Intent::Paused : Intent::Resumed;
        choices.priorities = ReadPriorities(request.value("priorities", Json::array()));
        choices.sequential = request.value("sequential", false);
        choices.firstLast = request.value("first_last", false);
        choices.queueTop = request.value("queue_top", settings.queueTop);
        Add(*preview, std::move(choices), [reply](Outcome outcome, Added added)
        {
            reply(outcome.error ? Failure(*outcome.error, outcome.detail) :
                Success({{"torrent_id", added.torrentId}, {"duplicate", added.kind == AdditionKind::Duplicate}}));
        });
        break;
    }
    case Command::MergeTrackers:
    {
        auto preview = FindPreview(request.at("preview_id").get<std::string>(), connectionId);
        if (!preview)
        {
            reply(Failure(ErrorCode::PreviewExpired));
            return;
        }
        MergeTrackers(*preview, request.at("torrent_id").get<std::string>(), reply);
        break;
    }
    case Command::Torrent:
    {
        auto found = torrents.find(request.at("torrent_id").get<std::string>());
        if (found == torrents.end() || found->second.deleted)
        {
            reply(Failure(ErrorCode::TorrentRemoved));
            return;
        }
        if (found->second.restore)
        {
            reply(Failure(ProblemKind::AliasConflict, found->second.conflict));
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
            reply(Failure(ErrorCode::InvalidRequest));
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
            if (torrents.at(ids.front()).restore)
            {
                reply(Failure(ProblemKind::AliasConflict));
                return;
            }
            torrents.at(ids.front()).handle.force_reannounce();
            reply(Success());
        });
        break;
    case Command::History:
    {
        auto range = request.at("range").get<std::string>();
        if (range != "five_minutes" && range != "day")
        {
            reply(Failure(ErrorCode::InvalidRequest));
            return;
        }
        reply(Success(History(range == "day")));
        break;
    }
    case Command::Pause:
        Act(TorrentIds(request), reply, [this](auto const& ids, Reply reply)
        {
            SetIntent(ids, Intent::Paused, reply);
        });
        break;
    case Command::Resume:
        Act(TorrentIds(request), reply, [this](auto const& ids, Reply reply)
        {
            SetIntent(ids, Intent::Resumed, reply);
        });
        break;
    case Command::Force:
        Act(TorrentIds(request), reply, [this](auto const& ids, Reply reply)
        {
            SetIntent(ids, Intent::Forced, reply);
        });
        break;
    case Command::Verify:
        Act(TorrentIds(request), reply, [this](auto const& ids, Reply reply)
        {
            Verify(ids, reply);
        });
        break;
    case Command::Remove:
        Act(TorrentIds(request), reply, [this](auto const& ids, Reply reply)
        {
            Remove(ids, reply);
        });
        break;
    case Command::FileScope:
        Act(TorrentIds(request), reply, [this](auto const& ids, Reply reply)
        {
            reply(Success(Describe(ids, FileScope(ids))));
        }, BusyFiles::Accepted);
        break;
    case Command::Move:
        Act(TorrentIds(request), reply, [this, destination = request.at("destination").get<std::string>(),
            useExisting = request.value("use_existing", false)](auto const& ids, Reply reply)
        {
            StartMove(ids, destination, useExisting, [reply](Outcome outcome)
            {
                reply(outcome.error ? Failure(*outcome.error, outcome.detail) : Success());
            });
        });
        break;
    case Command::DeleteFiles:
    {
        auto mode = request.value("deletion", settings.deletion == DeletionMode::Recycle ? "recycle" : "permanent");
        if (mode != "recycle" && mode != "permanent")
        {
            reply(Failure(ErrorCode::InvalidRequest));
            return;
        }
        Act(TorrentIds(request), reply, [this, mode](auto const& ids, Reply reply)
        {
            Remove(ids, reply, true, mode == "recycle" ? DeletionMode::Recycle : DeletionMode::Permanent);
        }, BusyFiles::Accepted);
        break;
    }
    case Command::Queue:
    {
        // A queue command names either a direction or the torrent to move
        // before; a null torrent moves them to the end.
        auto move = request.contains("before_torrent_id") ? QueueMove::Before :
            Parse(moves, request.value("direction", ""));
        if (!move)
        {
            reply(Failure(ErrorCode::InvalidRequest));
            return;
        }
        auto target = request.value("before_torrent_id", Json());
        auto before = target.is_null() ? std::string() : target.get<std::string>();
        Act(TorrentIds(request), reply, [this, move = *move, before](auto const& ids, Reply reply)
        {
            Queue(ids, move, before, reply);
        });
        break;
    }
    case Command::PieceOrder:
    {
        auto choice = [&request](char const* key)
        {
            return request.contains(key) ? std::optional(request.at(key).get<bool>()) : std::nullopt;
        };
        auto sequential = choice("sequential");
        auto firstLast = choice("first_last");
        if (!sequential && !firstLast)
        {
            reply(Failure(ErrorCode::InvalidRequest));
            return;
        }
        Act(TorrentIds(request), reply, [this, sequential, firstLast](auto const& ids, Reply reply)
        {
            ChangeFacts(ids, [sequential, firstLast](Facts& facts)
            {
                facts.sequential = sequential.value_or(facts.sequential);
                facts.firstLast = firstLast.value_or(facts.firstLast);
            }, reply);
        });
        break;
    }
    case Command::SpeedLimit:
    {
        auto limit = [&request](char const* key) -> std::optional<int>
        {
            if (!request.contains(key))
            {
                return std::nullopt;
            }
            auto const& value = request.at(key);
            if (!value.is_number_integer() || value < 0 || value > INT_MAX)
            {
                throw std::invalid_argument("A speed limit is not a whole number of bytes a second.");
            }
            return value.get<int>();
        };
        auto download = limit("download_limit");
        auto upload = limit("upload_limit");
        if (!download && !upload)
        {
            reply(Failure(ErrorCode::InvalidRequest));
            return;
        }
        Act(TorrentIds(request), reply, [this, download, upload](auto const& ids, Reply reply)
        {
            ChangeFacts(ids, [download, upload](Facts& facts)
            {
                facts.downloadLimit = download.value_or(facts.downloadLimit);
                facts.uploadLimit = upload.value_or(facts.uploadLimit);
            }, reply);
        });
        break;
    }
    }
}

void Engine::State::MergeTrackers(Preview& preview, std::string const& torrentId, Reply reply)
{
    UpdatePreview(preview);
    if (FindDuplicate(preview.InfoHashes()) != torrentId)
    {
        reply(Failure(ErrorCode::TorrentRemoved));
        return;
    }
    if (!changes.Queue([this, torrentId, urls = preview.params.trackers, reply]
    {
        if (!torrents.contains(torrentId) || torrents.at(torrentId).deleted)
        {
            reply(Failure(ErrorCode::TorrentRemoved));
            return;
        }
        if (torrents.at(torrentId).restore)
        {
            reply(Failure(ProblemKind::AliasConflict));
            return;
        }
        auto trackers = torrents.at(torrentId).handle.trackers();
        for (auto const& url : Missing(urls, Urls(trackers)))
        {
            trackers.emplace_back(url);
        }
        CommitEdit(torrentId, {}, trackers, reply);
    }))
    {
        reply(Failure(ErrorCode::Overloaded));
    }
}

// Runs the action after the changes queued before it, once every listed
// torrent still exists.
void Engine::State::Act(std::vector<std::string> ids, Reply reply, Action action, BusyFiles busy)
{
    if (ids.empty() || ids.size() > torrentLimit ||
        std::set<std::string>(ids.begin(), ids.end()).size() != ids.size())
    {
        reply(Failure(ErrorCode::InvalidTorrents));
        return;
    }
    if (!changes.Queue([this, ids, reply, action, busy]
    {
        for (auto const& id : ids)
        {
            auto found = torrents.find(id);
            if (found == torrents.end() || found->second.deleted)
            {
                reply(Failure(ErrorCode::TorrentRemoved));
                return;
            }
            if (busy == BusyFiles::Refused && found->second.FilesBusy())
            {
                reply(Failure(ErrorCode::FilesBusy));
                return;
            }
        }
        action(ids, reply);
    }))
    {
        reply(Failure(ErrorCode::Overloaded));
    }
}

void Engine::State::Verify(std::vector<std::string> const& ids, Reply reply)
{
    for (auto const& id : ids)
    {
        if (torrents.at(id).restore || !torrents.at(id).handle.torrent_file())
        {
            reply(Failure(ErrorCode::MetadataUnavailable));
            return;
        }
    }
    for (auto const& id : ids)
    {
        torrents.at(id).receivedPayload = false;
        torrents.at(id).completedFiles.clear();
        torrents.at(id).handle.force_recheck();
    }
    reply(Success());
}

// Removes the torrents from the list. With deleteData it also deletes their
// files; a torrent that CanRemove refuses leaves the list at once and is
// removed when CanRemove allows it.
void Engine::State::Remove(std::vector<std::string> const& ids, Reply reply, bool deleteData, DeletionMode mode)
{
    std::vector<std::string> ready;
    std::vector<std::string> later;
    for (auto const& id : ids)
    {
        (!deleteData || CanRemove(torrents.at(id)) ? ready : later).push_back(id);
    }
    std::size_t kept = 0;
    auto deletion = deleteData ? PrepareDeletion(ready, mode) : deletions.end();
    if (deleteData)
    {
        kept = FileScope(ids).kept.size();
    }
    auto removed = [&ids](std::string const& id) { return Contains(ids, id); };
    auto document = Saved();
    std::erase_if(document.torrents, [&removed](auto const& entry) { return removed(entry.first); });
    std::erase_if(document.queueOrder, removed);
    changes.Commit(document.ToJson(),
        [this, ready, later, order = document.queueOrder, deletion, kept, reply, mode](StorageOutcome outcome)
    {
        if (!outcome.succeeded)
        {
            if (deletion != deletions.end())
            {
                deletions.erase(deletion);
                ContinueDeletion();
            }
            reply(Failure(ErrorCode::StorageFailed, outcome.detail));
            return;
        }
        queueOrder = order;
        RemoveHandles(ready, deletion);
        for (auto const& id : later)
        {
            auto& torrent = torrents.at(id);
            torrent.deleted = true;
            torrent.deletionMode = mode;
            torrent.ApplyIntent();
        }
        // A move takes its torrents one at a time, so a deleted one still
        // waiting its turn leaves the move and its files are deleted where they
        // are. A move that is saving its end, success or failure, has no
        // torrent waiting.
        if (!later.empty() && move && move->phase != MovePhase::Saving)
        {
            for (auto const& id : later)
            {
                auto& members = move->ids;
                auto member = std::find(members.begin() + move->current + 1, members.end(), id);
                if (member == members.end())
                {
                    continue;
                }
                members.erase(member);
                auto& torrent = torrents.at(id);
                std::erase(move->waiting, torrent.handle);
                torrent.moving = false;
                torrent.facts.moveDestination.clear();
            }
            ContinueMove();
        }
        reply(Success({{"kept_files", kept}}));
    });
}

std::list<Engine::State::Deletion>::iterator Engine::State::PrepareDeletion(std::vector<std::string> const& ids,
    DeletionMode mode)
{
    if (ids.empty())
    {
        return deletions.end();
    }
    auto scope = FileScope(ids);
    Deletion deleting;
    deleting.mode = mode;
    deleting.holds = std::move(scope.holds);
    for (auto const& file : scope.files)
    {
        if (!std::binary_search(scope.kept.begin(), scope.kept.end(), file, PathBefore))
        {
            deleting.files.push_back(file);
        }
    }
    for (auto const& id : ids)
    {
        auto const& torrent = torrents.at(id);
        deleting.roots.push_back(FullPath(Wide(torrent.facts.savePath)));
        if (!deleting.names.empty())
        {
            deleting.names += ", ";
        }
        deleting.names += torrent.Name();
    }
    return deletions.insert(deletions.end(), std::move(deleting));
}

// Membership has already committed; cleanup failure must not restore it.
void Engine::State::RemoveHandles(std::vector<std::string> const& ids, std::list<Deletion>::iterator deletion)
{
    if (ids.empty())
    {
        return;
    }
    std::vector<std::filesystem::path> files;
    for (auto const& id : ids)
    {
        auto found = torrents.find(id);
        if (found->second.priorityReply)
        {
            std::exchange(found->second.priorityReply, nullptr)(Failure(ErrorCode::TorrentRemoved));
        }
        if (!found->second.restore)
        {
            if (deletion != deletions.end())
            {
                session->remove_torrent(found->second.handle, lt::session::delete_partfile);
                deletion->waiting.push_back(found->second.handle);
            }
            else
            {
                session->remove_torrent(found->second.handle);
            }
            handles.erase(found->second.handle);
        }
        torrents.erase(found);
        files.push_back(ResumeFile(id));
    }
    if (deletion != deletions.end())
    {
        deletion->phase = DeletionPhase::Waiting;
        ContinueDeletion();
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
            log.Write("remove", "", "metadata_cleanup_failed");
        }
    });
}

// A deleted torrent is removed once libtorrent has finished its own move or
// rename, which it cannot stop halfway, and once no addition runs, because
// FileScope cannot see the files an addition shares.
bool Engine::State::CanRemove(Torrent const& torrent) const
{
    return !torrent.FilesBusy() && additions.empty();
}

void Engine::State::RemoveDeferred()
{
    // A pending membership change may have kept files for these handles.
    if (!changes.IsIdle())
    {
        return;
    }
    for (auto mode : {DeletionMode::Recycle, DeletionMode::Permanent})
    {
        std::vector<std::string> ready;
        for (auto const& [id, torrent] : torrents)
            if (torrent.deleted && torrent.deletionMode == mode && CanRemove(torrent))
                ready.push_back(id);
        RemoveHandles(ready, PrepareDeletion(ready, mode));
    }
}

void Engine::State::SetIntent(std::vector<std::string> const& ids, Intent intent, Reply reply)
{
    if (intent != Intent::Paused)
    {
        RestorePending();
        for (auto const& id : ids)
        {
            if (torrents.at(id).restore || (!torrents.at(id).conflict.empty() &&
                !FindDuplicate(torrents.at(id).status.info_hashes, id).empty()))
            {
                reply(Failure(ProblemKind::AliasConflict));
                return;
            }
        }
    }
    auto document = Saved();
    std::map<std::string, Facts> next;
    for (auto const& id : ids)
    {
        auto& facts = document.torrents.at(id);
        facts.intent = intent;
        if (intent != Intent::Paused && torrents.at(id).status.is_finished)
        {
            facts.ignoresSeedLimits = true;
        }
        next.emplace(id, facts);
    }
    changes.Commit(document.ToJson(), reply, [this, ids, intent, next]
    {
        for (auto const& id : ids)
        {
            auto& torrent = torrents.at(id);
            torrent.facts = next.at(id);
            if (intent != Intent::Paused)
            {
                if (torrent.facts.moveDestination.empty())
                {
                    torrent.moveError.reset();
                }
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

void Engine::State::ChangeFacts(std::vector<std::string> const& ids,
    std::function<void(Facts&)> const& change, Reply reply)
{
    auto document = Saved();
    std::map<std::string, Facts> next;
    for (auto const& id : ids)
    {
        auto& facts = document.torrents.at(id);
        change(facts);
        if (facts.ToJson() != torrents.at(id).facts.ToJson())
        {
            next.emplace(id, facts);
        }
    }
    if (next.empty())
    {
        reply(Success());
        return;
    }
    changes.Commit(document.ToJson(), reply, [this, next]
    {
        for (auto const& [id, facts] : next)
        {
            auto& torrent = torrents.at(id);
            auto firstLast = torrent.facts.firstLast;
            torrent.facts = facts;
            torrent.ApplyIntent();
            // The file_prio_alert after ApplyIntent raises the end pieces only
            // while firstLast is on, so turning it off lowers them here.
            if (firstLast && !torrent.facts.firstLast)
            {
                torrent.PrioritizePieces();
            }
        }
        return Success();
    });
}
}
