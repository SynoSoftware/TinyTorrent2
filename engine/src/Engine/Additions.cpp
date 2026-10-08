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
    if (FilesBusy())
    {
        completion({ErrorCode::FilesBusy}, {});
        return;
    }
    auto duplicate = FindDuplicate(preview.InfoHashes());
    if (!duplicate.empty())
    {
        completion({}, {AdditionKind::Duplicate, duplicate});
        return;
    }
    if (!IsAbsolute(choices.destination))
    {
        completion({ErrorCode::InvalidDestination}, {});
        return;
    }
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
        auto prepared = std::make_shared<lt::add_torrent_params>(pending.params);
        payload.Run([prepared, appendsSuffix = pending.facts.appendsSuffix] { PrepareNames(*prepared, appendsSuffix); },
            [this, id = pending.torrentId, prepared](StorageOutcome outcome)
        {
            auto found = additions.find(id);
            if (found == additions.end())
                return;
            if (!outcome.succeeded)
            {
                Abandon(id, {ErrorCode::AddFailed, outcome.detail});
                return;
            }
            auto& addition = found->second;
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
    addition.phase = AdditionPhase::Naming;
    auto prepared = std::make_shared<lt::add_torrent_params>(addition.params);
    if (!prepared->ti)
        prepared->ti = handle.torrent_file();
    if (!prepared->ti)
    {
        SaveAddition(id, handle);
        return;
    }
    prepared->renamed_files = handle.get_renamed_files().export_filenames(prepared->ti->layout());
    payload.Run([prepared, appendsSuffix = addition.facts.appendsSuffix] { PrepareNames(*prepared, appendsSuffix); }, [this, id, prepared](StorageOutcome outcome)
    {
        auto found = additions.find(id);
        if (found == additions.end() || found->second.phase != AdditionPhase::Naming)
            return;
        if (!outcome.succeeded)
        {
            Abandon(id, {ErrorCode::AddFailed, outcome.detail});
            return;
        }
        auto& addition = found->second;
        addition.params.ti = prepared->ti;
        addition.params.renamed_files = prepared->renamed_files;
        ApplyNames(addition.handle, *prepared, addition.renaming);
        if (addition.renaming.empty())
            SaveAddition(id, addition.handle);
    });
}

// Previews and adds a source as the window does when the person accepts the
// defaults. The source has its own connection, so its preview merges with no
// other and ends with the addition.
void Engine::State::AddSource(std::string source, std::function<void(Outcome, Added)> completion)
{
    auto connectionId = NewId();
    auto finish = [this, connectionId, completion](Outcome outcome, Added added)
    {
        Disconnect(connectionId);
        completion(std::move(outcome), std::move(added));
    };
    Inspect(std::move(source), connectionId, [this, finish](Outcome outcome, Preview* preview)
    {
        if (outcome.error)
        {
            finish(std::move(outcome), {});
            return;
        }
        Addition::Choices choices;
        choices.destination = settings.destination;
        Add(*preview, std::move(choices), finish);
    });
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
        if (!outcome.succeeded)
        {
            log.Write("add", id, "storage_failed");
            Abandon(id, {ErrorCode::StorageFailed, outcome.detail});
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
    if (addition.queueTop)
    {
        document.queueOrder.insert(document.queueOrder.begin(), id);
    }
    else
    {
        document.queueOrder.push_back(id);
    }
    changes.Commit(document.ToJson(), [this, id, order = document.queueOrder](StorageOutcome outcome)
    {
        if (!outcome.succeeded)
        {
            Abandon(id, {ErrorCode::StorageFailed, outcome.detail});
            return;
        }
        auto found = additions.find(id);
        auto& torrent = Install(id, found->second.handle, found->second.facts, found->second.params);
        queueOrder = order;
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
            [this, id](StorageOutcome removed)
        {
            if (!removed.succeeded)
            {
                log.Write("add", id, "metadata_cleanup_failed");
            }
        });
    }
    additions.erase(found);
    completion(std::move(outcome), std::move(added));
}

std::string Engine::State::MovingAddition(lt::torrent_handle const& handle) const
{
    for (auto const& [id, addition] : additions)
    {
        if (addition.phase == AdditionPhase::Moving && addition.handle == handle)
        {
            return id;
        }
    }
    return {};
}

// After libtorrent dropped alerts: saves each addition whose torrent was
// added or moved, and abandons the others.
void Engine::State::RecoverAdditions()
{
    auto live = session->get_torrents();
    std::vector<std::string> lost;
    for (auto& [id, addition] : additions)
    {
        if (addition.phase == AdditionPhase::Naming)
        {
            if (!addition.renaming.empty())
                lost.push_back(id);
            continue;
        }
        if (addition.phase == AdditionPhase::Saving)
        {
            continue;
        }
        if (addition.phase == AdditionPhase::Moving)
        {
            // The move finished when the torrent already saves to its destination.
            if (addition.handle.is_valid() &&
                SameFolder(addition.handle.status().save_path, addition.params.save_path))
            {
                PrepareAddition(id, addition.handle);
            }
            else
            {
                lost.push_back(id);
            }
            continue;
        }
        // An accepted torrent keeps the address of the addition that created
        // it, and a later addition can reuse that address.
        auto found = std::find_if(live.begin(), live.end(),
            [this, &addition](lt::torrent_handle const& handle)
            { return handle.userdata().get<Addition>() == &addition && !Find(handle); });
        if (found != live.end())
        {
            PrepareAddition(id, *found);
        }
        else
        {
            lost.push_back(id);
        }
    }
    for (auto const& id : lost)
    {
        Abandon(id, {ErrorCode::RecoveryRequired});
    }
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
    auto duplicate = FindDuplicate(alert.params.info_hashes);
    if (duplicate.empty())
    {
        Abandon(found->first, {ErrorCode::AddFailed, alert.error.message()});
    }
    else
    {
        Abandon(found->first, {}, {AdditionKind::Duplicate, duplicate});
    }
}

void Engine::State::On(lt::storage_moved_alert const& alert)
{
    if (move)
    {
        FinishMove(alert.handle, std::nullopt);
        return;
    }
    auto id = MovingAddition(alert.handle);
    if (!id.empty())
    {
        PrepareAddition(id, alert.handle);
    }
}

void Engine::State::On(lt::storage_moved_failed_alert const& alert)
{
    if (move)
    {
        if (move->phase != MovePhase::Moving && move->phase != MovePhase::Unknown)
        {
            return;
        }
        auto kind = alert.error == boost::system::errc::file_exists ?
            ProblemKind::DestinationExists : ProblemKind::MoveFailed;
        FinishMove(alert.handle, Problem{kind, alert.error.message()});
        return;
    }
    auto id = MovingAddition(alert.handle);
    if (!id.empty())
    {
        Abandon(id, {ErrorCode::AddFailed, alert.error.message()});
    }
}
}
