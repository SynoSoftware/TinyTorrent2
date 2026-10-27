#include "Engine/State.h"
#include <algorithm>
#include <climits>

namespace tt
{
namespace
{
struct CommandEntry
{
    std::string_view name;
    Command command;
    Publication publication;
};

constexpr CommandEntry commands[] = {
    {"snapshot", Command::Snapshot, Publication::None},
    {"settings", Command::Settings, Publication::Operation},
    {"reset_settings", Command::ResetSettings, Publication::Operation},
    {"session_pause", Command::SessionPause, Publication::Operation},
    {"preview", Command::Preview, Publication::None},
    {"preview_detail", Command::PreviewDetail, Publication::None},
    {"cancel_preview", Command::CancelPreview, Publication::None},
    {"add", Command::Add, Publication::Execute},
    {"merge_trackers", Command::MergeTrackers, Publication::Execute},
    {"torrent", Command::Torrent, Publication::None},
    {"edit", Command::Edit, Publication::Execute},
    {"reannounce", Command::Reannounce, Publication::Execute},
    {"history", Command::History, Publication::None},
    {"pause", Command::Pause, Publication::Execute},
    {"resume", Command::Resume, Publication::Execute},
    {"force", Command::Force, Publication::Execute},
    {"verify", Command::Verify, Publication::Execute},
    {"retry", Command::Retry, Publication::Execute},
    {"remove", Command::Remove, Publication::Execute},
    {"file_scope", Command::FileScope, Publication::None},
    {"move", Command::Move, Publication::Execute},
    {"delete_files", Command::DeleteFiles, Publication::Execute},
    {"queue", Command::Queue, Publication::Execute},
    {"piece_order", Command::PieceOrder, Publication::Execute},
    {"speed_limit", Command::SpeedLimit, Publication::Execute},
    {"check_proxy", Command::CheckProxy, Publication::Execute},
    {"connection_test", Command::ConnectionTest, Publication::Execute},
    {"turn_off", Command::TurnOff, Publication::Operation},
    {"unfinished_tasks", Command::UnfinishedTasks, Publication::Execute}};

constexpr std::pair<std::string_view, QueueMove> queueMoves[] = {
    {"up", QueueMove::Up},
    {"down", QueueMove::Down},
    {"top", QueueMove::Top},
    {"bottom", QueueMove::Bottom}};

constexpr std::pair<std::string_view, DeletionMode> deletionModes[] = {
    {"recycle", DeletionMode::Recycle},
    {"permanent", DeletionMode::Permanent}};

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

// The completion of a command whose reply carries no data.
std::function<void(Outcome)> Acknowledge(Reply reply)
{
    return [reply = std::move(reply)](Outcome outcome)
    {
        reply(outcome.error ? Failure(*outcome.error, outcome.detail) : Success());
    };
}
}

// Why the engine refuses commands now: it is shutting down or starting, or
// startup failed.
std::optional<ErrorCode> Engine::State::Refusal() const
{
    if (IsShuttingDown())
    {
        return ErrorCode::ShuttingDown;
    }
    if (IsLoading())
    {
        return ErrorCode::Starting;
    }
    if (startup == Startup::Failed)
    {
        return ErrorCode::StorageFailed;
    }
    return std::nullopt;
}

// Reads each request into typed values; the operations it calls never see
// the request.
void Engine::State::Execute(Json const& request, std::string const& connectionId, Reply reply)
{
    auto name = request.at("command").get<std::string>();
    auto entry = std::ranges::find(commands, std::string_view(name), &CommandEntry::name);
    auto command = entry == std::end(commands) ? std::nullopt : std::optional(entry->command);
    if (command == Command::Snapshot)
    {
        if (request.value("subscribe", false))
            snapshotSubscribers[connectionId] = reply;
        reply(Success(Snapshot()));
        RequestStatus();
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
    if (entry->publication != Publication::None)
    {
        reply = [this, publication = entry->publication, reply = std::move(reply)](Json response)
        {
            if (response.at("ok").get<bool>())
            {
                if (publication == Publication::Execute)
                {
                    PublishSnapshot();
                    RequestStatus();
                }
                if (!snapshotSubscribers.empty())
                    response["data"]["snapshot_sequence"] = snapshotSequence;
            }
            reply(std::move(response));
        };
    }
    switch (*command)
    {
    case Command::Snapshot:
        // Answered above, even while shutting down or loading.
        break;
    case Command::Settings:
        Configure(request.at("changes"), [this, reply](Outcome outcome)
        {
            reply(outcome.error ? Failure(*outcome.error, outcome.detail) : Success(settings.ToJson()));
        });
        break;
    case Command::ResetSettings:
        ResetSettings([this, reply](Outcome outcome)
        {
            reply(outcome.error ? Failure(*outcome.error, outcome.detail) : Success(settings.ToJson()));
        });
        break;
    case Command::ConnectionTest:
    {
        auto action = request.at("action").get<std::string>();
        if (action == "start")
        {
            auto profile = request.value("profile", std::string("quick"));
            if ((profile != "quick" && profile != "longer") || !StartConnectionTest(connectionId, profile == "longer"))
            {
                reply(Failure(ErrorCode::InvalidRequest));
                break;
            }
        }
        else if (action == "cancel" || action == "release")
            ReleaseConnectionTest(connectionId, action == "cancel" ? ConnectionPhase::Cancelled : ConnectionPhase::Completed);
        else
        {
            reply(Failure(ErrorCode::InvalidRequest));
            break;
        }
        ++snapshotSequence;
        reply(Success(ConnectionTestSnapshot()));
        break;
    }
    case Command::CheckProxy:
    {
        // The proxy is read as the settings command reads it, so a proxy the
        // engine would refuse to save is refused here too.
        auto checked = settings.With(request.at("proxy"));
        if (!checked || checked->proxy.type == ProxyType::None || !checked->proxy.IsComplete())
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
        PauseSession(request.at("paused").get<bool>(), Acknowledge(reply));
        break;
    case Command::UnfinishedTasks:
    {
        auto action = request.at("action").get<std::string>();
        if (action != "continue" && action != "abandon")
        {
            reply(Failure(ErrorCode::InvalidRequest));
            break;
        }
        Answer(action == "continue" ? TaskAnswer::Continue : TaskAnswer::Abandon, Acknowledge(reply));
        break;
    }
    case Command::TurnOff:
    {
        auto word = request.at("switch").get<std::string>();
        std::vector<ToastSwitch> toasts;
        if (word == "all")
        {
            toasts = AllToastSwitches();
        }
        else if (auto toast = Parse(toastSwitches, word))
        {
            toasts.push_back(*toast);
        }
        else
        {
            reply(Failure(ErrorCode::InvalidRequest));
            break;
        }
        TurnOff(toasts, Acknowledge(reply));
        break;
    }
    case Command::Preview:
        Inspect(request.at("source").get<std::string>(), connectionId,
            [this, destination = request.value("destination", ""), reply](Outcome outcome, Preview* preview)
        {
            if (outcome.error)
                reply(Failure(*outcome.error, outcome.detail));
            else
                DescribePreview(*preview, destination, reply);
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
        DescribePreview(*preview, request.value("destination", ""), reply,
            request.value("file_offset", 0), request.value("file_limit", 200),
            request.value("file_query", std::string()));
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
        choices.sequential = request.value("sequential", false);
        choices.firstLast = request.value("first_last", false);
        choices.queueTop = request.value("queue_top", settings.queueTop);
        auto overrides = request.value("choices", Json::array());
        if (!overrides.is_array())
            throw std::invalid_argument("Invalid file choices");
        bool hasDefault = request.contains("default_priority") && !request.at("default_priority").is_null();
        if (hasDefault || !overrides.empty())
        {
            if (!preview->params.ti)
            {
                reply(Failure(ErrorCode::MetadataUnavailable));
                return;
            }
            auto const& files = preview->params.ti->layout();
            std::optional<lt::download_priority_t> fallback;
            auto readPriority = [](Json const& value)
            {
                auto number = value.get<int>();
                if (number < 0 || number > 7 || !IsChoice(lt::download_priority_t(static_cast<std::uint8_t>(number))))
                    throw std::invalid_argument("Invalid file priority");
                return lt::download_priority_t(static_cast<std::uint8_t>(number));
            };
            if (hasDefault)
                fallback = readPriority(request.at("default_priority"));
            if (overrides.size() > static_cast<std::size_t>(files.num_files()))
                throw std::invalid_argument("Invalid file choices");
            std::set<int> indices;
            std::vector<std::pair<int, lt::download_priority_t>> selected;
            for (auto const& choice : overrides)
            {
                auto index = choice.at("index").get<int>();
                auto priority = readPriority(choice.at("priority"));
                if (index < 0 || index >= files.num_files() || !indices.insert(index).second)
                    throw std::invalid_argument("Invalid file choice");
                selected.emplace_back(index, priority);
            }
            auto metadata = preview->params.ti;
            auto id = preview->previewId;
            sources.Run([metadata, choices = std::move(choices), selected = std::move(selected), fallback,
                patterns = settings.excludes ? settings.patterns : std::string()]() mutable
            {
                choices.priorities = fallback ? std::vector<lt::download_priority_t>(metadata->num_files(), *fallback) :
                    DefaultPriorities(metadata->layout(), patterns);
                for (auto const& [index, priority] : selected)
                    choices.priorities[index] = priority;
                return std::move(choices);
            }, [this, id, connectionId, reply](std::expected<Addition::Choices, StorageOutcome> prepared)
            {
                auto preview = FindPreview(id, connectionId);
                if (!preview)
                    reply(Failure(ErrorCode::PreviewExpired));
                else if (!prepared)
                    reply(Failure(ErrorCode::InvalidPriorities, prepared.error().detail));
                else
                    Add(*preview, std::move(*prepared), [reply](Outcome outcome, Added added)
                    {
                        reply(outcome.error ? Failure(*outcome.error, outcome.detail) :
                            Success({{"torrent_id", added.torrentId}, {"duplicate", added.kind == AdditionKind::Duplicate}}));
                    });
            });
            break;
        }
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
        MergeTrackers(*preview, request.at("torrent_id").get<std::string>(), Acknowledge(reply));
        break;
    }
    case Command::Torrent:
    {
        if (request.value("release", false))
        {
            ReleaseReading(connectionId);
            reply(Success());
            break;
        }
        std::optional<TorrentView> view;
        if (request.contains("view"))
        {
            view = Parse(views, request.at("view").get<std::string>());
            if (!view)
            {
                reply(Failure(ErrorCode::InvalidRequest));
                return;
            }
        }
        Reading::Read read{connectionId, request.at("torrent_id").get<std::string>(), view,
            request.value("include_files", false), reply};
        if (request.contains("context"))
        {
            if (!view || !request.at("context").is_number_integer())
            {
                reply(Failure(ErrorCode::InvalidRequest));
                return;
            }
            read.context = request.at("context").get<std::int64_t>();
        }
        ReadDetail(std::move(read));
        break;
    }
    case Command::Edit:
        Edit(request.at("torrent_id").get<std::string>(), request.at("changes"), Acknowledge(reply));
        break;
    case Command::Reannounce:
        Act({request.at("torrent_id").get<std::string>()}, Acknowledge(reply), [this](auto const& ids, auto done)
        {
            if (torrents.at(ids.front()).restore)
            {
                done({ErrorCode::AliasConflict});
                return;
            }
            torrents.at(ids.front()).handle.force_reannounce();
            done({});
        });
        break;
    case Command::History:
    {
        auto range = request.at("range").get<std::string>();
        if ((range != "five_minutes" && range != "day") ||
            (request.contains("torrent_id") && request.contains("torrent_ids")))
        {
            reply(Failure(ErrorCode::InvalidRequest));
            return;
        }
        if (request.value("release_detail", false))
            ReleaseReading(connectionId);
        auto const* history = &this->history;
        Json data{{"session_id", sessionId}};
        if (request.contains("torrent_id"))
        {
            auto id = request.at("torrent_id").get<std::string>();
            auto found = torrents.find(id);
            if (found == torrents.end())
            {
                reply(Failure(ErrorCode::TorrentRemoved));
                return;
            }
            history = &found->second.history;
            data["torrent_id"] = id;
        }
        if (request.contains("torrent_ids"))
        {
            auto ids = request.at("torrent_ids").get<std::vector<std::string>>();
            if (ids.empty())
            {
                reply(Failure(ErrorCode::InvalidRequest));
                return;
            }
            std::sort(ids.begin(), ids.end());
            ids.erase(std::unique(ids.begin(), ids.end()), ids.end());
            std::vector<SpeedHistory const*> sources;
            for (auto const& id : ids)
            {
                auto found = torrents.find(id);
                if (found == torrents.end())
                {
                    reply(Failure(ErrorCode::TorrentRemoved));
                    return;
                }
                sources.push_back(&found->second.history);
            }
            data["torrent_ids"] = ids;
            data["samples"] = this->history.Sum(range == "day", sources);
        }
        else
            data["samples"] = history->Read(range == "day");
        reply(Success(std::move(data)));
        break;
    }
    case Command::Pause:
        Act(TorrentIds(request), Acknowledge(reply), [this](auto const& ids, auto done)
        {
            SetIntent(ids, Intent::Paused, done);
        });
        break;
    case Command::Resume:
        Act(TorrentIds(request), Acknowledge(reply), [this](auto const& ids, auto done)
        {
            SetIntent(ids, Intent::Resumed, done);
        });
        break;
    case Command::Force:
        Act(TorrentIds(request), Acknowledge(reply), [this](auto const& ids, auto done)
        {
            SetIntent(ids, Intent::Forced, done);
        });
        break;
    case Command::Verify:
        Act(TorrentIds(request), Acknowledge(reply), [this](auto const& ids, auto done)
        {
            Verify(ids, done);
        });
        break;
    case Command::Retry:
        Act(TorrentIds(request), Acknowledge(reply), [this](auto const& ids, auto done)
        {
            Retry(ids, done);
        });
        break;
    case Command::Remove:
    case Command::DeleteFiles:
    {
        // One deletion mode serves the downloaded files and the .torrent files.
        auto mode = Parse(deletionModes, request.value("deletion", std::string("recycle")));
        if (!mode)
        {
            reply(Failure(ErrorCode::InvalidRequest));
            return;
        }
        auto files = *command == Command::DeleteFiles ? mode : std::nullopt;
        auto torrentFiles = request.value("torrent_files", false) ? mode : std::nullopt;
        auto kept = std::make_shared<std::optional<std::size_t>>();
        Act(TorrentIds(request), [reply, kept](Outcome outcome)
        {
            reply(outcome.error ? Failure(*outcome.error, outcome.detail) :
                Success({{"kept_files", *kept ? Json(**kept) : Json()}}));
        }, [this, files, torrentFiles, kept](auto const& ids, auto done)
        {
            Remove(ids, [kept, done](Outcome outcome, std::optional<std::size_t> count)
            {
                *kept = count;
                done(std::move(outcome));
            }, files, torrentFiles);
        });
        break;
    }
    case Command::FileScope:
    {
        auto ids = TorrentIds(request);
        Act(ids, [this, ids, reply](Outcome outcome)
        {
            if (outcome.error)
            {
                reply(Failure(*outcome.error, outcome.detail));
                return;
            }
            auto scope = Describe(ids, FileScope(ids));
            if (!NamesReady())
                scope["kept_files"] = nullptr;
            reply(Success(std::move(scope)));
        }, [](auto const&, auto done) { done({}); });
        break;
    }
    case Command::Move:
        Act(TorrentIds(request), Acknowledge(reply), [this, destination = request.at("destination").get<std::string>()](auto const& ids, auto done)
        {
            StartMove(ids, destination, MoveStart::Requested, [this, ids, destination, done](Outcome outcome)
            {
                // The move waits its turn and replaces an earlier problem.
                if (DelaysMove(outcome))
                {
                    auto turn = ++turns;
                    for (auto const& id : ids)
                    {
                        auto& torrent = torrents.at(id);
                        torrent.waitingMove = WaitingMove{destination, turn};
                        torrent.moveError.reset();
                        if (outcome.error == ErrorCode::MetadataUnavailable)
                            RefuseMove(torrent, outcome);
                    }
                    done({});
                    return;
                }
                done(std::move(outcome));
            });
        });
        break;
    case Command::Queue:
    {
        // A queue command names either a direction or the torrent to move
        // before; a null torrent moves them to the end.
        auto target = request.value("before_torrent_id", Json());
        auto move = !request.contains("before_torrent_id") ? Parse(queueMoves, request.value("direction", "")) :
            std::optional(target.is_null() ? QueueMove::Bottom : QueueMove::Before);
        if (!move)
        {
            reply(Failure(ErrorCode::InvalidRequest));
            return;
        }
        auto before = *move == QueueMove::Before ? target.get<std::string>() : std::string();
        Act(TorrentIds(request), Acknowledge(reply), [this, move = *move, before](auto const& ids, auto done)
        {
            Queue(ids, move, before, done);
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
        Act(TorrentIds(request), Acknowledge(reply), [this, sequential, firstLast](auto const& ids, auto done)
        {
            ChangeFacts(ids, [sequential, firstLast](Facts& facts)
            {
                facts.sequential = sequential.value_or(facts.sequential);
                facts.firstLast = firstLast.value_or(facts.firstLast);
            }, done);
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
        Act(TorrentIds(request), Acknowledge(reply), [this, download, upload](auto const& ids, auto done)
        {
            ChangeFacts(ids, [download, upload](Facts& facts)
            {
                facts.downloadLimit = download.value_or(facts.downloadLimit);
                facts.uploadLimit = upload.value_or(facts.uploadLimit);
            }, done);
        });
        break;
    }
    }
}

void Engine::State::MergeTrackers(Preview& preview, std::string const& torrentId,
    std::function<void(Outcome)> completion)
{
    UpdatePreview(preview);
    if (FindDuplicate(preview.InfoHashes()) != torrentId)
    {
        completion({ErrorCode::TorrentRemoved});
        return;
    }
    Act({torrentId}, completion, [this, urls = preview.params.trackers](auto const& ids, auto done)
    {
        auto trackers = torrents.at(ids.front()).Trackers();
        for (auto const& url : Missing(urls, Urls(trackers)))
        {
            trackers.emplace_back(url);
        }
        CommitEdit(ids.front(), {}, trackers, done);
    });
}

// Runs the action after the changes queued before it, once every listed
// torrent still exists. Its completion first invalidates their detail. Busy
// files never refuse an action: one that changes files waits for them after
// its completion, because the person should not have to try again.
void Engine::State::Act(std::vector<std::string> ids, std::function<void(Outcome)> completion, Action action)
{
    completion = [this, ids, finish = std::move(completion)](Outcome outcome)
    {
        for (auto const& id : ids)
        {
            Invalidate(id);
        }
        finish(std::move(outcome));
    };
    if (ids.empty() || ids.size() > torrentLimit ||
        std::set<std::string>(ids.begin(), ids.end()).size() != ids.size())
    {
        completion({ErrorCode::InvalidTorrents});
        return;
    }
    if (!changes.Queue([this, ids, completion, action]
    {
        for (auto const& id : ids)
        {
            auto found = torrents.find(id);
            if (found == torrents.end())
            {
                completion({ErrorCode::TorrentRemoved});
                return;
            }
        }
        action(ids, completion);
    }))
    {
        completion({ErrorCode::Overloaded});
    }
}

void Engine::State::Verify(std::vector<std::string> const& ids, std::function<void(Outcome)> completion)
{
    for (auto const& id : ids)
    {
        if (torrents.at(id).restore || !torrents.at(id).handle.torrent_file())
        {
            completion({ErrorCode::MetadataUnavailable});
            return;
        }
    }
    for (auto const& id : ids)
    {
        auto& torrent = torrents.at(id);
        // The person answered a stopped torrent, so recovery may retry again.
        torrent.storageRetry = StorageRetry::Automatic;
        // A recheck queues behind the reads of a stalled check, so it waits
        // until they return; meanwhile the stalled check continues and can
        // stall again.
        if (torrent.recovery || torrent.FilesBusy() || torrent.checkReads == CheckReads::Stalled)
            torrent.needsRecheck = true;
        else
            Recheck(torrent);
        if (torrent.checkError || torrent.moveError)
        {
            torrent.checkError.reset();
            torrent.AnswerMoveProblem();
            torrent.ApplyIntent();
        }
    }
    statusPending = true;
    ApplyQueue();
    completion({});
}

void Engine::State::Retry(std::vector<std::string> const& ids, std::function<void(Outcome)> completion)
{
    for (auto const& id : ids)
    {
        auto& torrent = torrents.at(id);
        torrent.checkError.reset();
        torrent.ClearDiskError();
        // FinishDownload starts a failed move to the final folder again.
        if (torrent.moveRetry == StorageRetry::Stopped || torrent.moveRetry == StorageRetry::Person)
            torrent.AnswerMoveProblem();
        torrent.ApplyIntent();
    }
    statusPending = true;
    completion({});
}

void Engine::State::Recheck(Torrent& torrent)
{
    // libtorrent ignores a recheck while it checks resume data, such as one
    // that runs again when its disk answers, so it waits for that check.
    if (torrent.status.state == lt::torrent_status::checking_resume_data)
    {
        torrent.needsRecheck = true;
        return;
    }
    torrent.completion.Rechecked();
    torrent.completedFiles.clear();
    torrent.handle.force_recheck();
    Invalidate(torrent.torrentId);
}

void Engine::State::ApplyWaiting(Torrent& torrent)
{
    if (torrent.restore || torrent.recovery || torrent.FilesBusy() || torrent.namePhase != NamePhase::Ready)
        return;
    if (torrent.prioritiesPending)
    {
        torrent.prioritiesPending = false;
        torrent.handle.prioritize_files(torrent.facts.priorities);
        Invalidate(torrent.torrentId);
    }
    if (torrent.needsRecheck && torrent.checkReads == CheckReads::None)
    {
        torrent.needsRecheck = false;
        Recheck(torrent);
        statusPending = true;
        queuePending = true;
    }
}

void Engine::State::SetIntent(std::vector<std::string> const& ids, Intent intent,
    std::function<void(Outcome)> completion)
{
    if (intent != Intent::Paused)
    {
        for (auto const& id : ids)
        {
            if ((torrents.at(id).restore || !torrents.at(id).conflict.empty()) &&
                FindDuplicate(torrents.at(id).status.info_hashes, id))
            {
                completion({ErrorCode::AliasConflict});
                return;
            }
        }
    }
    // A finished torrent that starts again ignores its seed limits. Whether it
    // is finished is read now, because the edit runs again after the write.
    std::vector<std::string> finished;
    for (auto const& id : ids)
    {
        if (intent != Intent::Paused && torrents.at(id).status.is_finished)
        {
            finished.push_back(id);
        }
    }
    CommitFacts(ids, [intent, finished](std::string const& id, Facts& facts)
    {
        facts.intent = intent;
        if (Contains(finished, id))
        {
            facts.ignoresSeedLimits = true;
        }
    }, [this, ids, intent, completion](Outcome outcome, auto const&)
    {
        if (outcome.error)
        {
            completion(std::move(outcome));
            return;
        }
        for (auto const& id : ids)
        {
            auto& torrent = torrents.at(id);
            torrent.AnswerMoveProblem();
            if (intent != Intent::Paused)
            {
                torrent.conflict.clear();
                torrent.checkError.reset();
                torrent.ClearDiskError();
            }
            // A later force goes after the checks forced before it.
            if (intent == Intent::Forced && torrent.CheckWaits())
                torrent.checkTurn->turn = ++turns;
            torrent.ApplyIntent();
        }
        statusPending = true;
        completion({});
    });
}

void Engine::State::ChangeFacts(std::vector<std::string> const& ids,
    std::function<void(Facts&)> const& change, std::function<void(Outcome)> completion)
{
    std::map<std::string, bool> firstLast;
    for (auto const& id : ids)
    {
        firstLast.emplace(id, torrents.at(id).facts.firstLast);
    }
    CommitFacts(ids, [change](std::string const&, Facts& facts) { change(facts); },
        [this, firstLast, completion](Outcome outcome, std::vector<std::string> const& changed)
    {
        if (outcome.error)
        {
            completion(std::move(outcome));
            return;
        }
        for (auto const& id : changed)
        {
            auto& torrent = torrents.at(id);
            torrent.ApplyIntent();
            // The file_prio_alert after ApplyIntent raises the end pieces only
            // while firstLast is on, so turning it off lowers them here.
            if (firstLast.at(id) && !torrent.facts.firstLast)
            {
                torrent.piecesPending = true;
                Invalidate(id);
                QueryPriorities(torrent);
            }
        }
        completion({});
    });
}
}
