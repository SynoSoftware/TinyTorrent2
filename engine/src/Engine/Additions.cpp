#include "Engine/State.h"
#include <libtorrent/torrent_info.hpp>
#include <algorithm>
#include <chrono>

namespace tt
{
// Writes no payload until ApplyIntent applies the saved file choices.
void Engine::State::Guard(lt::add_torrent_params& params)
{
    params.flags &= ~(lt::torrent_flags::auto_managed | lt::torrent_flags::share_mode |
        lt::torrent_flags::seed_mode);
    params.flags |= lt::torrent_flags::default_dont_download | lt::torrent_flags::duplicate_is_error;
    params.piece_priorities.clear();
    params.file_priorities.assign(params.ti ? params.ti->num_files() : 0, lt::dont_download);
}

// The person's file priorities, with defaults supplied and padding unwanted.
// A magnet without metadata keeps the all-files choice until metadata arrives.
// Returns nothing when the choice is invalid.
std::optional<std::vector<lt::download_priority_t>> Engine::State::Priorities(
    std::vector<lt::download_priority_t> chosen, std::shared_ptr<lt::torrent_info const> const& metadata)
{
    if (!metadata)
    {
        return std::vector<lt::download_priority_t>{};
    }
    auto const& files = metadata->layout();
    if (chosen.empty())
    {
        chosen = DefaultPriorities(files);
    }
    if (chosen.size() != static_cast<std::size_t>(files.num_files()))
    {
        return std::nullopt;
    }
    for (auto index : files.file_range())
    {
        if (files.pad_file_at(index))
        {
            chosen[static_cast<int>(index)] = lt::dont_download;
        }
    }
    for (auto priority : chosen)
    {
        if (!IsChoice(priority))
        {
            return std::nullopt;
        }
    }
    return chosen;
}

// The priorities a person can choose for a file: skip, low, normal or high.
bool Engine::State::IsChoice(lt::download_priority_t priority)
{
    return priority == lt::dont_download || priority == lt::low_priority ||
        priority == lt::default_priority || priority == lt::top_priority;
}

// Starts adding the previewed content. The preview ends here, and its
// guarded torrent, if any, becomes the new torrent.
void Engine::State::Add(Preview& preview, Addition::Choices choices, std::function<void(Outcome, Added)> completion)
{
    UpdatePreview(preview);
    // A preview opened before the removal began is confirmed here.
    if (Removing(preview.InfoHashes()))
    {
        completion({ErrorCode::TorrentRemoving}, {});
        return;
    }
    if (auto duplicate = FindDuplicate(preview.InfoHashes()))
    {
        completion({}, {AdditionKind::Duplicate, *duplicate});
        return;
    }
    if (!IsAbsolute(choices.destination))
    {
        completion({ErrorCode::InvalidDestination}, {});
        return;
    }
    auto patterns = settings.excludes ? settings.patterns : std::string();
    if (choices.priorities.empty() && preview.params.ti)
        choices.priorities = DefaultPriorities(preview.params.ti->layout(), patterns);
    auto chosen = Priorities(std::move(choices.priorities), preview.params.ti);
    if (!chosen || (preview.params.ti && std::none_of(chosen->begin(), chosen->end(),
        [](auto priority) { return priority != lt::dont_download; })))
    {
        completion({ErrorCode::InvalidPriorities}, {});
        return;
    }
    Addition addition;
    addition.torrentId = NewId();
    addition.params = preview.params;
    addition.facts.appendsSuffix = settings.appendsSuffix;
    addition.facts.layout = settings.layout;
    addition.facts.skipPatterns = std::move(patterns);
    addition.facts.torrentFile = preview.torrentFile;
    addition.facts.savePath = settings.SavePath(choices.destination);
    if (addition.facts.savePath != choices.destination)
    {
        addition.facts.finalFolder = std::move(choices.destination);
    }
    addition.params.save_path = addition.facts.savePath;
    Guard(addition.params);
    addition.params.flags |= lt::torrent_flags::paused;
    addition.facts.intent = choices.intent;
    addition.facts.sequential = choices.sequential;
    addition.facts.firstLast = choices.firstLast;
    addition.facts.added = std::chrono::system_clock::to_time_t(std::chrono::system_clock::now());
    addition.facts.priorities = std::move(*chosen);
    addition.facts.hashes = Hashes(preview.InfoHashes());
    addition.queueTop = choices.queueTop;
    addition.watch = std::move(choices.watch);
    addition.completion = std::move(completion);
    auto& pending = additions.emplace(addition.torrentId, std::move(addition)).first->second;
    if (preview.handle.is_valid())
    {
        pending.handle = preview.handle;
        pending.phase = AdditionPhase::Moving;
        pending.handle.pause();
        pending.handle.move_storage(pending.facts.savePath, lt::move_flags_t::reset_save_path);
    }
    else
    {
        VolumeWorker(pending.facts.savePath).Run([params = pending.params, appendsSuffix = pending.facts.appendsSuffix,
            layout = pending.facts.layout]() mutable
        {
            PrepareNames(params, appendsSuffix, layout);
            return std::move(params);
        }, [this, id = pending.torrentId](std::expected<lt::add_torrent_params, StorageOutcome> prepared)
        {
            auto found = additions.find(id);
            // Exit adds the torrent without these names.
            if (found == additions.end() || found->second.params.userdata.get<Addition>())
            {
                return;
            }
            // A torrent whose folder does not answer joins the list and
            // chooses its names once the folder answers, as any torrent in
            // the list does.
            if (!prepared && prepared.error().cause != Unreachable())
            {
                Abandon(id, prepared.error().ToOutcome(ErrorCode::AddFailed));
                return;
            }
            auto& addition = found->second;
            if (prepared)
                addition.params = std::move(*prepared);
            addition.params.userdata = &addition;
            session->async_add_torrent(addition.params);
        });
    }
    previews.erase(previews.find(preview.previewId));
}

void Engine::State::PrepareAddition(std::string const& id, lt::torrent_handle handle)
{
    auto& addition = additions.at(id);
    addition.handle = handle;
    ApplyPolicy(handle);
    addition.phase = AdditionPhase::Naming;
    auto params = addition.params;
    if (!params.ti)
        params.ti = handle.torrent_file();
    // A rename or removal can change the destination's files while the names
    // are chosen, so the torrent then joins the list without them and
    // ContinueNames chooses them once that work ends. Exit does not wait for
    // the drive's worker that chooses them, so during Exit the torrent joins
    // without them too, and the next start chooses them. It stays paused until
    // then, so it writes nothing under a wrong name.
    if (!params.ti || DeletingOrRenaming(RootOf(addition.facts.savePath)) || IsShuttingDown())
    {
        SaveAddition(id, handle);
        return;
    }
    VolumeWorker(addition.facts.savePath).Run([this, params = std::move(params), handle,
        appendsSuffix = addition.facts.appendsSuffix, layout = addition.facts.layout]() mutable
    {
        ReleaseFiles({handle});
        auto current = handle.get_renamed_files();
        params.renamed_files = current.export_filenames(params.ti->layout());
        PrepareNames(params, appendsSuffix, layout);
        return NameChoice{std::move(params), std::move(current)};
    }, [this, id](std::expected<NameChoice, StorageOutcome> choice)
    {
        auto found = additions.find(id);
        if (found == additions.end() || found->second.phase != AdditionPhase::Naming)
            return;
        if (!choice)
        {
            if (choice.error().cause == Unreachable())
                SaveAddition(id, found->second.handle);
            else
                Abandon(id, choice.error().ToOutcome(ErrorCode::AddFailed));
            return;
        }
        auto& addition = found->second;
        addition.params.ti = choice->params.ti;
        addition.params.renamed_files = choice->params.renamed_files;
        ApplyNames(addition.handle, choice->params, choice->current, addition.renaming);
        addition.named = true;
        if (addition.renaming.empty())
            SaveAddition(id, addition.handle);
    });
}

// Previews and adds a source as the window does when the person accepts the
// defaults. The source has its own connection, so its preview merges with no
// other and ends with the addition.
void Engine::State::AddSource(std::string source, std::function<void(Outcome, Added)> completion,
    std::optional<std::string> watchStamp)
{
    auto connectionId = NewId();
    auto watch = watchStamp ? std::optional(Addition::Watch{source, std::move(*watchStamp)}) : std::nullopt;
    auto finish = [this, connectionId, watch, completion](Outcome outcome, Added added)
    {
        Disconnect(connectionId);
        if (outcome.error || !watch || added.kind == AdditionKind::New)
            completion(std::move(outcome), std::move(added));
        else
            RecordWatch(watch->source, watch->stamp, [completion, added](Outcome outcome)
            {
                completion(std::move(outcome), added);
            });
    };
    Inspect(std::move(source), connectionId, [this, finish, watch](Outcome outcome, Preview* preview)
    {
        if (outcome.error)
        {
            finish(std::move(outcome), {});
            return;
        }
        auto duplicate = FindDuplicate(preview->InfoHashes());
        if (duplicate && settings.duplicates == DuplicatePolicy::Merge && CanMerge(*preview))
        {
            MergeTrackers(*preview, *duplicate, [finish, duplicate](Outcome outcome)
            {
                if (outcome.error)
                    finish(std::move(outcome), {});
                else
                    finish({}, {AdditionKind::Duplicate, *duplicate});
            });
            return;
        }
        Addition::Choices choices;
        choices.destination = watch && !settings.watchDestination.empty() ?
            settings.watchDestination : settings.AdditionFolder();
        choices.intent = settings.startsDownload ? Intent::Resumed : Intent::Paused;
        choices.queueTop = settings.queueTop;
        choices.watch = watch;
        Add(*preview, std::move(choices), finish);
    });
}

void Engine::State::RecordWatch(std::string source, std::string stamp, std::function<void(Outcome)> completion)
{
    if (!changes.Queue([this, source, stamp, completion]
    {
        auto found = watchedSources.find(source);
        if (found != watchedSources.end() && found->second == stamp)
        {
            completion({});
            return;
        }
        auto document = Saved();
        document.watchedSources[source] = stamp;
        changes.Commit([document = std::move(document)] { return document.ToJson(); },
            [this, source, stamp, completion](Outcome outcome)
        {
            if (outcome.error)
            {
                completion(std::move(outcome));
                return;
            }
            watchedSources[source] = stamp;
            completion({});
        });
    }))
        completion({ErrorCode::Overloaded});
}

// Writes the resume file before membership lists the torrent, because
// startup fails when a listed torrent has no resume file.
void Engine::State::SaveAddition(std::string id, lt::torrent_handle handle)
{
    auto& addition = additions.at(id);
    addition.handle = handle;
    addition.phase = AdditionPhase::Saving;
    auto params = addition.params;
    params.userdata = lt::client_data_t{};
    WriteResume(id, std::move(params), [this, id](StorageOutcome outcome)
    {
        if (outcome.failure)
        {
            log.Write("add", id, "storage_failed");
            Abandon(id, outcome.ToOutcome(ErrorCode::StorageFailed));
            return;
        }
        if (!changes.Queue([this, id] { CommitAddition(id); }))
        {
            Abandon(id, {ErrorCode::Overloaded});
        }
    });
}

void Engine::State::CommitAddition(std::string const& id)
{
    auto const& addition = additions.at(id);
    auto document = Saved();
    document.torrents.emplace(id, addition.facts);
    if (addition.watch)
        document.watchedSources[addition.watch->source] = addition.watch->stamp;
    document.settings.lastFolder = addition.facts.finalFolder.empty() ?
        addition.facts.savePath : addition.facts.finalFolder;
    if (addition.queueTop)
    {
        document.queueOrder.insert(document.queueOrder.begin(), id);
    }
    else
    {
        document.queueOrder.push_back(id);
    }
    auto order = document.queueOrder;
    auto folder = document.settings.lastFolder;
    auto watched = document.watchedSources;
    changes.Commit([document = std::move(document)] { return document.ToJson(); },
        [this, id, order = std::move(order), folder = std::move(folder), watched = std::move(watched)](Outcome outcome)
    {
        if (outcome.error)
        {
            Abandon(id, std::move(outcome));
            return;
        }
        auto found = additions.find(id);
        auto& torrent = Install(id, found->second.handle, found->second.facts, found->second.params);
        if (found->second.named)
            torrent.namePhase = NamePhase::Ready;
        torrent.queueReport = QueueReport::AwaitingFiles;
        queueOrder = order;
        settings.lastFolder = folder;
        watchedSources = watched;
        ApplyQueue();
        torrent.ApplyIntent();
        log.Write("add", id, "saved");
        auto completion = std::move(found->second.completion);
        additions.erase(found);
        completion({}, {AdditionKind::New, id});
    });
}

// Ends an addition that will not be saved: removes its torrent and
// reports why.
void Engine::State::Abandon(std::string id, Outcome outcome, Added added)
{
    auto found = additions.find(id);
    auto completion = std::move(found->second.completion);
    if (found->second.handle.is_valid())
    {
        session->remove_torrent(found->second.handle);
    }
    // Membership never lists this torrent, so nothing else removes its resume
    // file.
    if (found->second.phase == AdditionPhase::Saving)
    {
        store.Run([file = ResumeFile(id)] { std::filesystem::remove(file); },
            [this, id](StorageOutcome cleanup)
        {
            if (cleanup.failure)
            {
                log.Write("add", id, "metadata_cleanup_failed");
            }
        });
    }
    additions.erase(found);
    completion(std::move(outcome), std::move(added));
}

std::optional<std::string> Engine::State::MovingAddition(lt::torrent_handle const& handle) const
{
    for (auto const& [id, addition] : additions)
    {
        if (addition.phase == AdditionPhase::Moving && addition.handle == handle)
        {
            return id;
        }
    }
    return std::nullopt;
}

// After libtorrent dropped alerts: saves each addition whose torrent was
// added or moved, and abandons the others.
void Engine::State::RecoverAdditions()
{
    struct Observation
    {
        std::string id;
        AdditionPhase phase;
        Addition const* identity;
        lt::torrent_handle handle;
        std::string savePath;
        bool moving = false;
    };
    auto observations = std::make_shared<std::vector<Observation>>();
    std::vector<std::string> lost;
    for (auto const& [id, addition] : additions)
    {
        if (addition.phase == AdditionPhase::Naming)
        {
            if (!addition.renaming.empty())
                lost.push_back(id);
        }
        else if (addition.phase != AdditionPhase::Saving &&
            (addition.phase != AdditionPhase::Adding || addition.params.userdata.get<Addition>()))
        {
            observations->push_back({id, addition.phase, &addition, addition.handle});
        }
    }
    for (auto const& id : lost)
        Abandon(id, {ErrorCode::RecoveryRequired});
    if (observations->empty())
        return;

    // A removed torrent stays in the session until its removal ends.
    std::set<lt::torrent_handle> accepted;
    EachWorking([&accepted](Torrent const& torrent) { accepted.insert(torrent.handle); });
    // The job asks only libtorrent's network thread and touches no drive, so
    // it runs on the save worker, which a share that does not answer cannot
    // hold.
    store.Run([session = session.get(), observations, accepted = std::move(accepted)]
    {
        SyncCall(*session, [session, observations, accepted]
        {
            // Accepted torrents can retain an address reused by a later addition.
            std::map<Addition const*, lt::torrent_handle> added;
            for (auto const& handle : session->get_torrents())
            {
                if (accepted.contains(handle))
                    continue;
                if (auto identity = handle.userdata().get<Addition>())
                    added.emplace(identity, handle);
            }
            for (auto& observation : *observations)
            {
                if (observation.phase == AdditionPhase::Adding)
                {
                    if (auto found = added.find(observation.identity); found != added.end())
                        observation.handle = found->second;
                }
                else if (observation.handle.is_valid())
                {
                    auto status = observation.handle.status(lt::torrent_handle::query_save_path);
                    observation.savePath = std::move(status.save_path);
                    observation.moving = status.moving_storage;
                }
            }
        });
    }, [this, observations](StorageOutcome outcome)
    {
        for (auto const& observation : *observations)
        {
            auto found = additions.find(observation.id);
            if (found == additions.end() || found->second.phase != observation.phase ||
                (observation.phase == AdditionPhase::Moving && found->second.handle != observation.handle))
                continue;
            auto& addition = found->second;
            if (observation.phase == AdditionPhase::Adding)
                addition.handle = observation.handle;
            if (outcome.failure || !observation.handle.is_valid())
            {
                Abandon(observation.id, {ErrorCode::RecoveryRequired, outcome.detail});
                continue;
            }
            if (observation.moving)
                continue;
            if (observation.phase == AdditionPhase::Moving &&
                !SameFolder(observation.savePath, addition.params.save_path))
                Abandon(observation.id, {ErrorCode::RecoveryRequired});
            else
                PrepareAddition(observation.id, observation.handle);
        }
    });
}

void Engine::State::On(lt::add_torrent_alert const& alert)
{
    auto pointer = alert.params.userdata.get<Addition>();
    if (!pointer)
    {
        return;
    }
    auto found = std::find_if(additions.begin(), additions.end(),
        [pointer](auto const& entry) { return &entry.second == pointer; });
    if (found == additions.end() || found->second.handle.is_valid())
    {
        return;
    }
    if (!alert.error)
    {
        PrepareAddition(found->first, alert.handle);
        return;
    }
    if (Removing(alert.params.info_hashes))
    {
        Abandon(found->first, {ErrorCode::TorrentRemoving});
    }
    else if (auto duplicate = FindDuplicate(alert.params.info_hashes))
    {
        Abandon(found->first, {}, {AdditionKind::Duplicate, *duplicate});
    }
    else
    {
        Abandon(found->first, {ErrorCode::AddFailed, alert.error.message()});
    }
}

void Engine::State::On(lt::storage_moved_alert const& alert)
{
    if (auto id = MovingAddition(alert.handle))
    {
        PrepareAddition(*id, alert.handle);
        return;
    }
    FinishMove(alert.handle, {});
}

void Engine::State::On(lt::storage_moved_failed_alert const& alert)
{
    if (auto id = MovingAddition(alert.handle))
    {
        Abandon(*id, {ErrorCode::AddFailed, alert.error.message()});
        return;
    }
    FinishMove(alert.handle, alert.error.message());
}
}
