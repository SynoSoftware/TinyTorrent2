#include "Engine/State.h"
#include <Windows.h>

namespace tt
{
namespace
{
// How often the engine checks whether the cause of a stopped torrent is gone.
constexpr auto stoppedCheck = std::chrono::minutes(1);

// The files of one torrent that lost the data they held.
using LostFiles = std::vector<lt::file_index_t>;
}

bool Engine::State::Retryable(lt::error_code const& error)
{
    // A missing, absent or full location can come back.
    auto kind = Problem::From(error).kind;
    if (kind == ProblemKind::DownloadPathMissing || kind == ProblemKind::DownloadPathUnavailable ||
        kind == ProblemKind::DiskFull)
        return true;
    // So can access that another program holds, and I/O that failed once.
    using namespace boost::system::errc;
    if (error == permission_denied || error == operation_not_permitted || error == read_only_file_system ||
        error == io_error || error == device_or_resource_busy || error == resource_unavailable_try_again ||
        error == timed_out || error == connection_aborted || error == connection_reset)
        return true;
    if (error.category() != lt::system_category())
        return false;
    switch (error.value())
    {
    case ERROR_INVALID_HANDLE:
    case ERROR_SHARING_VIOLATION:
    case ERROR_LOCK_VIOLATION:
    case ERROR_SEM_TIMEOUT:
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

bool Engine::State::Recoverable(lt::error_code const& error, lt::torrent_flags_t flags)
{
    return error ? Retryable(error) : bool(flags & lt::torrent_flags::upload_mode);
}

std::vector<lt::file_index_t> Engine::State::LostData(lt::torrent_handle const& handle)
{
    std::vector<lt::file_index_t> lost;
    auto status = handle.status(lt::torrent_handle::query_save_path |
        lt::torrent_handle::query_torrent_file | lt::torrent_handle::query_renamed_files);
    auto metadata = status.torrent_file.lock();
    if (!metadata)
        return lost;
    auto const& layout = metadata->layout();
    auto progress = handle.file_progress(lt::torrent_handle::piece_granularity);
    // A skipped file keeps the pieces it shares with its neighbours in the
    // part file, not in its own file, and the torrent wants none of its own
    // data.
    auto priorities = handle.get_file_priorities();
    for (auto index : layout.file_range())
    {
        auto item = static_cast<std::size_t>(static_cast<int>(index));
        if (item >= progress.size() || progress[item] <= 0 || layout.pad_file_at(index) ||
            (item < priorities.size() && priorities[item] == lt::dont_download))
            continue;
        auto path = FullPath(Wide(status.renamed_files.file_path(layout, index, status.save_path)));
        WIN32_FILE_ATTRIBUTE_DATA data{};
        if (!GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &data))
        {
            auto error = GetLastError();
            if (error == ERROR_FILE_NOT_FOUND || error == ERROR_PATH_NOT_FOUND)
                lost.push_back(index);
            continue;
        }
        auto size = (std::int64_t(data.nFileSizeHigh) << 32) | data.nFileSizeLow;
        if (size < progress[item])
            lost.push_back(index);
    }
    return lost;
}

bool Engine::State::Usable(std::filesystem::path const& folder, std::filesystem::path const& file, bool write,
    std::uint64_t needed)
{
    // libtorrent creates the folders it lacks, so the nearest existing one
    // takes the file.
    auto existing = AnsweringFolder(folder);
    if (!existing)
        return false;
    ULARGE_INTEGER available{};
    if (needed > 0 && (!GetDiskFreeSpaceExW(existing->c_str(), &available, nullptr, nullptr) ||
        available.QuadPart < needed))
        return false;
    if (!file.empty() && GetFileAttributesW(file.c_str()) != INVALID_FILE_ATTRIBUTES)
    {
        auto opened = CreateFileW(file.c_str(), write ? GENERIC_READ | GENERIC_WRITE : GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL,
            nullptr);
        if (opened == INVALID_HANDLE_VALUE)
            return false;
        CloseHandle(opened);
        return true;
    }
    if (!write)
        return true;
    // Only creating a file shows that the folder accepts new files; the empty
    // file goes at once.
    wchar_t created[MAX_PATH];
    if (!GetTempFileNameW(existing->c_str(), L"tt", 0, created))
        return false;
    DeleteFileW(created);
    return true;
}

void Engine::State::RecoverStorage(Torrent& torrent, lt::error_code error)
{
    if (IsShuttingDown() || torrent.restore || torrent.StorageStopped())
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
        // A torrent the person stopped recovers too, so it shows its saved
        // progress instead of the error; ApplyIntent keeps it stopped.
        if (IsShuttingDown() || recovery->phase != DiskPhase::Waiting || torrent.FilesBusy())
            continue;
        if (locations.size() == 64 && !locations.contains(torrent.facts.savePath))
            break;
        storageCursor = id;
        recovery->phase = DiskPhase::Probing;
        locations[torrent.facts.savePath].push_back({id, torrent.handle, recovery});
    }
    for (auto& [folder, attempts] : locations)
    {
        std::vector<lt::torrent_handle> handles;
        for (auto const& attempt : attempts)
            handles.push_back(attempt.handle);
        // Once the folder answers, which files each torrent lost; nothing
        // while it does not answer.
        VolumeWorker(folder).Run([folder, handles]
        {
            std::optional<std::vector<LostFiles>> lost;
            if (Reachable(Wide(folder)))
            {
                lost.emplace();
                for (auto const& handle : handles)
                    lost->push_back(LostData(handle));
            }
            return lost;
        }, [this, folder, attempts = std::move(attempts)](
            std::expected<std::optional<std::vector<LostFiles>>, StorageOutcome> lost)
        {
            bool answered = lost && *lost;
            bool absent = lost && !*lost;
            // A drive lookup made while the folder did not answer could name
            // no drive, so the next job that asks looks it up again.
            if (auto found = drives.find(folder); answered && found != drives.end() && found->second)
                drives.erase(found);
            for (std::size_t item = 0; item < attempts.size(); ++item)
            {
                auto const& attempt = attempts[item];
                auto found = torrents.find(attempt.torrentId);
                if (found == torrents.end() || found->second.handle != attempt.handle ||
                    found->second.recovery != attempt.recovery)
                    continue;
                auto& torrent = found->second;
                auto& recovery = *attempt.recovery;
                recovery.phase = DiskPhase::Waiting;
                recovery.absent = absent;
                // Waiting fixes a folder that does not answer, so its next
                // resume is automatic again.
                if (absent)
                    torrent.storageRetry = StorageRetry::Automatic;
                if (IsShuttingDown() || !answered || !SameFolder(folder, torrent.facts.savePath))
                    continue;
                // Windows reports an absent share with many codes, so only an
                // answering folder shows that waiting cannot fix the error.
                // Neither can it fix the same error again before the torrent
                // got further than at its last resume, so the torrent stops:
                // CheckStopped continues it once its cause is gone, and after
                // a resume of its own only the person does. An error no check
                // can judge waits for the person at once.
                bool again = !torrent.retriedProgress ||
                    !FileProgress::Of(torrent.status).Beyond(*torrent.retriedProgress);
                if (!Retryable(torrent) || (again && torrent.storageRetry == StorageRetry::Resumed))
                    StopStorage(torrent, StorageRetry::Person);
                else if (again && torrent.storageRetry == StorageRetry::Retried)
                    StopStorage(torrent, StorageRetry::Stopped);
                else if (!torrent.FilesBusy())
                    ContinueStorage(torrent, std::move((**lost)[item]), StorageRetry::Retried);
            }
        });
    }
}

void Engine::State::StopStorage(Torrent& torrent, StorageRetry retry)
{
    torrent.storageRetry = retry;
    torrent.recovery.reset();
    Invalidate(torrent.torrentId);
    statusPending = true;
    log.Write("storage", torrent.torrentId, retry == StorageRetry::Person ? "retry_needed" : "stopped");
}

void Engine::State::ContinueStorage(Torrent& torrent, std::vector<lt::file_index_t> lost, StorageRetry retry)
{
    torrent.storageRetry = retry;
    // A check that a disk error stopped reads the files again, so it finds
    // lost data itself.
    if (torrent.status.state == lt::torrent_status::checking_files && (torrent.diskError || torrent.status.errc))
    {
        Reload(torrent, {});
        return;
    }
    if (!lost.empty())
    {
        log.Write("storage", torrent.torrentId, "files_lost");
        Reload(torrent, std::move(lost));
        return;
    }
    torrent.retriedProgress = FileProgress::Of(torrent.status);
    torrent.recovery.reset();
    torrent.ClearDiskError();
    torrent.checkError.reset();
    torrent.renameAt = {};
    torrent.ApplyIntent();
    Invalidate(torrent.torrentId);
    statusPending = true;
    log.Write("storage", torrent.torrentId, "resumed");
}

void Engine::State::CheckStopped()
{
    auto now = std::chrono::steady_clock::now();
    if (IsShuttingDown() || now < stoppedCheckAt)
        return;
    stoppedCheckAt = now + stoppedCheck;
    struct Attempt
    {
        std::string torrentId;
        lt::torrent_handle handle;
        // The files that a failed move to the final folder places; otherwise
        // the storage of the torrent's own folder is checked.
        std::optional<std::vector<Payload::File>> move;
        std::filesystem::path file;
        bool write = true;
        std::uint64_t needed = 0;
    };
    std::map<std::string, std::vector<Attempt>, PathLess> folders;
    for (auto& [id, torrent] : torrents)
    {
        if (torrent.restore || torrent.recovery || torrent.FilesBusy())
            continue;
        if (torrent.moveError && torrent.moveRetry == StorageRetry::Stopped)
        {
            folders[torrent.facts.finalFolder].push_back({id, torrent.handle,
                Relocated(torrent.facts.finalFolder, torrent)});
            continue;
        }
        // A stop whose problem other work cleared has ended, so the next
        // failure gets its automatic retry again.
        bool stopped = torrent.storageRetry == StorageRetry::Stopped || torrent.storageRetry == StorageRetry::Person;
        if (stopped && !torrent.StorageProblem())
            torrent.storageRetry = StorageRetry::Automatic;
        if (torrent.storageRetry != StorageRetry::Stopped || !torrent.StorageProblem())
            continue;
        // A seed only reads; a download needs room for what it still lacks.
        bool write = !torrent.status.is_seeding;
        std::filesystem::path file;
        auto index = torrent.status.error_file;
        if (torrent.names && index >= lt::file_index_t(0) && index < torrent.names->metadata->layout().end_file())
            file = FullPath(Wide(torrent.names->mappings.file_path(torrent.names->metadata->layout(), index,
                torrent.facts.savePath)));
        folders[torrent.facts.savePath].push_back({id, torrent.handle, std::nullopt, std::move(file), write,
            write ? static_cast<std::uint64_t>(torrent.status.total_wanted - torrent.status.total_wanted_done) : 0});
    }
    for (auto& [folder, attempts] : folders)
    {
        // A folder whose last check has not returned is not checked again.
        if (!stoppedChecks.insert(folder).second)
            continue;
        struct Found
        {
            bool usable = false;
            LostFiles lost;
        };
        VolumeWorker(folder).Run([folder, attempts]
        {
            std::vector<Found> found;
            for (auto const& attempt : attempts)
            {
                Found result{Usable(Wide(folder), attempt.file, attempt.write, attempt.needed) &&
                    (!attempt.move || Payload::HasRoom(*attempt.move))};
                if (result.usable && !attempt.move)
                    result.lost = LostData(attempt.handle);
                found.push_back(std::move(result));
            }
            return found;
        }, [this, folder, attempts](std::expected<std::vector<Found>, StorageOutcome> found)
        {
            stoppedChecks.erase(folder);
            if (!found || IsShuttingDown())
                return;
            for (std::size_t item = 0; item < attempts.size(); ++item)
            {
                auto const& attempt = attempts[item];
                auto torrent = FindWorking(attempt.torrentId);
                if (!torrent || torrent->handle != attempt.handle || !(*found)[item].usable ||
                    torrent->recovery || torrent->FilesBusy())
                    continue;
                if (attempt.move)
                {
                    if (torrent->moveRetry != StorageRetry::Stopped)
                        continue;
                    // FinishDownload starts the move again once its error is
                    // gone. The failure left no marker, so nothing else waits.
                    torrent->moveError.reset();
                    torrent->moveRetry = StorageRetry::Resumed;
                    torrent->ApplyIntent();
                    Invalidate(attempt.torrentId);
                    log.Write("move", attempt.torrentId, "resumed");
                }
                else if (torrent->storageRetry == StorageRetry::Stopped)
                    ContinueStorage(*torrent, std::move((*found)[item].lost), StorageRetry::Resumed);
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

// libtorrent cannot forget single pieces, so the torrent reloads from its
// resume data without the pieces of the lost files. It downloads only those
// again, instead of checking every file, which can take hours. A check that a
// disk error stopped resumes the same way: its resume data holds the pieces
// checked before the failed one, and libtorrent continues the check after
// them.
void Engine::State::DropPieces(lt::add_torrent_params& params, std::vector<lt::file_index_t> const& files)
{
    if (files.empty() || !params.ti)
        return;
    auto const& layout = params.ti->layout();
    for (auto file : files)
    {
        auto size = layout.file_size(file);
        if (size == 0)
            continue;
        auto last = layout.map_file(file, size - 1, 1).piece;
        for (auto piece = layout.map_file(file, 0, 1).piece; piece <= last; ++piece)
        {
            if (piece < params.have_pieces.end_index())
                params.have_pieces.clear_bit(piece);
            if (piece < params.verified_pieces.end_index())
                params.verified_pieces.clear_bit(piece);
            params.unfinished_pieces.erase(piece);
        }
    }
    // Seed mode claims every piece.
    params.flags &= ~lt::torrent_flags::seed_mode;
}

void Engine::State::Reload(Torrent& torrent, std::vector<lt::file_index_t> lost)
{
    auto id = torrent.torrentId;
    auto handle = torrent.handle;
    if (!torrent.recovery)
        torrent.recovery = std::make_shared<Torrent::Recovery>();
    auto recovery = torrent.recovery;
    recovery->phase = DiskPhase::Reloading;
    recovery->lost = lost;
    auto replacement = std::make_shared<Replacement>();
    payload.Run([this, handle, replacement, lost = std::move(lost), stop = exitStop.get_token()]
    {
        auto& params = replacement->params;
        params = handle.get_resume_data(lt::torrent_handle::save_info_dict);
        DropPieces(params, lost);
        params.flags &= ~(lt::torrent_flags::seed_mode | lt::torrent_flags::no_verify_files |
            lt::torrent_flags::auto_managed | lt::torrent_flags::upload_mode);
        params.flags |= lt::torrent_flags::paused;
        // The release can wait on a drive that stopped responding. Exit waits
        // for this reload only briefly and then saves the torrent through its
        // old handle, without the pieces of the lost files, so once Exit has
        // started, the handle stays. ReloadStorage then releases again at once.
        ReleaseFiles({handle});
        if (stop.stop_requested())
            throw std::runtime_error("shutting_down");
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
            log.Write("storage", id, outcome.detail);
            return;
        }
        ReplaceHandle(*torrent, *replacement);
        // A reload changes the torrent's progress, so the reloaded torrent
        // measures it once it has read its resume data.
        torrent->retriedProgress.reset();
        torrent->renameAt = {};
        torrent->unsaved = true;
        if (!IsShuttingDown())
        {
            if (torrent->diskError)
                RecoverStorage(*torrent, torrent->diskError);
            torrent->ApplyIntent();
        }
        log.Write("storage", id, "resumed");
    });
}
}
