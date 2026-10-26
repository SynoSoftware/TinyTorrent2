#include "Engine/State.h"
#include <Windows.h>

namespace tt
{
bool Engine::State::Retryable(lt::error_code const& error)
{
    using namespace boost::system::errc;
    if (error == no_such_file_or_directory || error == permission_denied ||
        error == operation_not_permitted || error == read_only_file_system ||
        error == no_space_on_device || error == io_error || error == device_or_resource_busy ||
        error == resource_unavailable_try_again || error == timed_out ||
        error == network_down || error == network_unreachable || error == host_unreachable ||
        error == connection_aborted || error == connection_reset || error == not_connected)
        return true;
    if (error.category() != lt::system_category())
        return false;
    switch (error.value())
    {
    case ERROR_INVALID_HANDLE:
    case ERROR_DISK_FULL:
    case ERROR_HANDLE_DISK_FULL:
    case ERROR_NOT_READY:
    case ERROR_SHARING_VIOLATION:
    case ERROR_LOCK_VIOLATION:
    case ERROR_BAD_NETPATH:
    case ERROR_NETWORK_BUSY:
    case ERROR_DEV_NOT_EXIST:
    case ERROR_BAD_NET_RESP:
    case ERROR_UNEXP_NET_ERR:
    case ERROR_NETNAME_DELETED:
    case ERROR_NETWORK_ACCESS_DENIED:
    case ERROR_BAD_NET_NAME:
    case ERROR_SEM_TIMEOUT:
    case ERROR_CONNECTION_UNAVAIL:
    case ERROR_NO_NET_OR_BAD_PATH:
    case ERROR_NO_NETWORK:
        return true;
    default:
        return false;
    }
}

bool Engine::State::Retryable(Torrent const& torrent)
{
    return (!torrent.diskError || Retryable(torrent.diskError)) &&
        (!torrent.status.errc || Retryable(torrent.status.errc)) &&
        (!torrent.recovery || !torrent.recovery->cause || Retryable(torrent.recovery->cause));
}

void Engine::State::RecoverStorage(Torrent& torrent, lt::error_code error)
{
    if (IsShuttingDown() || torrent.restore)
        return;
    if (torrent.recovery)
    {
        if (error)
            torrent.recovery->cause = error;
        return;
    }
    torrent.recovery = std::make_shared<Torrent::Recovery>();
    torrent.recovery->cause = error;
    Invalidate(torrent.torrentId);
    torrent.ApplyIntent();
    torrent.recovery->released = ReleaseStorage(torrent.handle).share();
}

void Engine::State::RetryStorage()
{
    struct Attempt
    {
        std::string torrentId;
        lt::torrent_handle handle;
        std::shared_ptr<Torrent::Recovery> recovery;
    };
    std::map<std::string, std::vector<Attempt>, PathLess> locations;
    auto entry = torrents.upper_bound(storageCursor);
    for (std::size_t visited = 0; visited < torrents.size(); ++visited)
    {
        if (entry == torrents.end())
            entry = torrents.begin();
        auto& [id, torrent] = *entry++;
        auto recovery = torrent.recovery;
        if (!recovery || torrent.restore)
            continue;
        if (recovery->phase == DiskPhase::Releasing)
        {
            if (recovery->released.wait_for(std::chrono::seconds(0)) != std::future_status::ready)
                continue;
            try
            {
                recovery->released.get();
            }
            catch (std::exception const& error)
            {
                log.Write("storage", id, error.what());
                recovery->released = ReleaseStorage(torrent.handle).share();
                continue;
            }
            recovery->phase = DiskPhase::Waiting;
            torrent.checkReads = CheckReads::None;
        }
        if (recovery->retryRequested && recovery->phase != DiskPhase::Reloading)
        {
            torrent.recovery.reset();
            torrent.ClearDiskError();
            torrent.renameAt = {};
            torrent.ApplyIntent();
            Invalidate(id);
            statusPending = true;
            continue;
        }
        if (recovery->phase == DiskPhase::Waiting && !Retryable(torrent))
        {
            torrent.recovery.reset();
            continue;
        }
        if (recovery->phase != DiskPhase::Waiting || Paused() ||
            torrent.facts.intent == Intent::Paused || torrent.FilesBusy())
            continue;
        if (locations.size() == 64 && !locations.contains(torrent.facts.savePath))
            break;
        storageCursor = id;
        recovery->phase = DiskPhase::Probing;
        locations[torrent.facts.savePath].push_back({id, torrent.handle, recovery});
    }
    for (auto& [folder, attempts] : locations)
    {
        VolumeWorker(folder).Run([folder] { return Reachable(Wide(folder)); },
            [this, folder, attempts = std::move(attempts)](std::expected<bool, StorageOutcome> ready)
        {
            for (auto const& attempt : attempts)
            {
                auto found = torrents.find(attempt.torrentId);
                if (found == torrents.end() || found->second.handle != attempt.handle ||
                    found->second.recovery != attempt.recovery)
                    continue;
                auto& torrent = found->second;
                auto& recovery = *attempt.recovery;
                recovery.phase = DiskPhase::Waiting;
                if (IsShuttingDown() || !ready || !*ready || !SameFolder(folder, torrent.facts.savePath) ||
                    Paused() || torrent.facts.intent == Intent::Paused || torrent.FilesBusy() ||
                    !Retryable(torrent))
                    continue;
                if (torrent.status.state == lt::torrent_status::checking_resume_data ||
                    (torrent.status.state == lt::torrent_status::checking_files && recovery.cause))
                {
                    RestartCheck(torrent);
                    continue;
                }
                torrent.recovery.reset();
                torrent.ClearDiskError();
                torrent.checkError.reset();
                torrent.renameAt = {};
                torrent.ApplyIntent();
                Invalidate(attempt.torrentId);
                statusPending = true;
                log.Write("storage", attempt.torrentId, "resumed");
            }
        });
    }
}

void Engine::State::ReloadStorage(lt::torrent_handle const& handle, Replacement& replacement)
{
    ReleaseFiles({handle});
    session->remove_torrent(handle);
    replacement.removed = true;
    lt::error_code error;
    // Native resume data omits the subscription to status updates.
    replacement.params.flags |= lt::torrent_flags::update_subscribe;
    replacement.handle = session->add_torrent(replacement.params, error);
    if (error)
        throw lt::system_error(error);
    replacement.statusAt = lt::clock_type::now();
    replacement.status = replacement.handle.status(routineQuery);
}

void Engine::State::ReplaceHandle(Torrent& torrent, Replacement& replacement)
{
    handles.erase(torrent.handle);
    seedQueries.erase(torrent.handle);
    torrent.handle = std::move(replacement.handle);
    torrent.status = std::move(replacement.status);
    torrent.statusAt = replacement.statusAt;
    torrent.diskError = torrent.status.errc;
    torrent.recovery.reset();
    torrent.checkError.reset();
    torrent.queries.clear();
    torrent.pendingCheckpoint.reset();
    torrent.checkpointPhase = CheckpointPhase::Idle;
    torrent.checkReads = CheckReads::None;
    torrent.checkTurn.reset();
    torrent.completion.Replaced();
    if (torrents.contains(torrent.torrentId))
        handles.emplace(torrent.handle, &torrent);
    ApplyPolicy(torrent.handle);
    if (torrent.facts.trackers)
        torrent.handle.replace_trackers(*torrent.facts.trackers);
    Invalidate(torrent.torrentId);
    statusPending = true;
    queuePending = true;
}

void Engine::State::RestartCheck(Torrent& torrent)
{
    auto id = torrent.torrentId;
    auto handle = torrent.handle;
    auto recovery = torrent.recovery;
    recovery->phase = DiskPhase::Reloading;
    auto replacement = std::make_shared<Replacement>();
    payload.Run([this, handle, replacement]
    {
        auto& params = replacement->params;
        params = handle.get_resume_data(lt::torrent_handle::save_info_dict);
        params.have_pieces.clear();
        params.verified_pieces.clear();
        params.unfinished_pieces.clear();
        params.flags &= ~(lt::torrent_flags::seed_mode | lt::torrent_flags::no_verify_files |
            lt::torrent_flags::auto_managed | lt::torrent_flags::upload_mode);
        params.flags |= lt::torrent_flags::paused;
        ReloadStorage(handle, *replacement);
    }, [this, id, handle, recovery, replacement](StorageOutcome outcome)
    {
        auto torrent = FindWorking(id);
        if (!torrent || torrent->handle != handle || torrent->recovery != recovery)
            return;
        if (outcome.failure)
        {
            recovery->phase = DiskPhase::Waiting;
            if (replacement->removed)
            {
                handles.erase(handle);
                seedQueries.erase(handle);
                torrent->restore = std::move(replacement->params);
                torrent->recovery.reset();
                torrent->pendingCheckpoint.reset();
                torrent->checkpointPhase = CheckpointPhase::Idle;
            }
            log.Write("verify", id, outcome.detail);
            return;
        }
        ReplaceHandle(*torrent, *replacement);
        torrent->renameAt = {};
        torrent->needsRecheck = false;
        torrent->unsaved = true;
        if (!IsShuttingDown())
        {
            if (torrent->diskError)
                RecoverStorage(*torrent, torrent->diskError);
            torrent->ApplyIntent();
        }
        log.Write("verify", id, "restarted");
    });
}
}
