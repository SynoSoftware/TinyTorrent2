#include "Engine/State.h"
#include <Windows.h>
#include <algorithm>

namespace tt
{
namespace
{
// How often the engine checks whether the cause of a stopped torrent is gone.
constexpr auto stoppedCheck = std::chrono::minutes(1);
// How often a probe opens each root that holds a running torrent.
constexpr auto rootProbe = std::chrono::minutes(1);

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

std::optional<Control::Failed> Engine::State::StatusFailure(Torrent const& torrent, lt::torrent_status const& status)
{
    if (!torrent.conflict.empty())
        return std::nullopt;
    if (auto error = torrent.diskError ? torrent.diskError : status.errc)
        return Control::Failed{Problem::From(error), Retryable(error)};
    if (status.flags & lt::torrent_flags::upload_mode)
        return Control::Failed{Problem{ProblemKind::TorrentError}};
    return std::nullopt;
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
    auto existing = ExistingFolder(folder);
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

Control::Effects Engine::State::Steer(Torrent& torrent, Control::Event const& event)
{
    if (IsShuttingDown())
    {
        return {};
    }
    // A removed torrent starts no job, so its control refuses the events that
    // start one. Job results and Dropped still end its jobs, so its files are
    // released and its removal can finish.
    bool starts = std::holds_alternative<Control::Failed>(event) || std::holds_alternative<Control::Due>(event);
    if (starts && !torrents.contains(torrent.torrentId))
    {
        return {};
    }
    std::optional<FileProgress> progress;
    if (torrent.status.state != lt::torrent_status::checking_resume_data)
    {
        progress = FileProgress::Of(torrent.status);
    }
    Control::Reality reality{.progress = progress,
        .retryable = (!torrent.diskError || Retryable(torrent.diskError)) &&
            (!torrent.status.errc || Retryable(torrent.status.errc)),
        .checking = torrent.status.state == lt::torrent_status::checking_files,
        .fileWork = torrent.FileWork()};
    auto stage = [](Control const& control)
    {
        auto const& storage = control.Storage();
        return storage ? std::optional(storage->ladder.stage) : std::nullopt;
    };
    auto before = stage(torrent.control);
    auto effects = torrent.control.Decide(event, reality);
    auto const& id = torrent.torrentId;
    if (effects.clear)
    {
        torrent.ClearDiskError();
        torrent.renameAt = {};
    }
    // FinishDownload starts the move again once its error is gone. The
    // failure left no marker, so nothing else waits.
    if (effects.retryMove)
    {
        torrent.moveError.reset();
    }
    // The torrent pauses before its release, so the release ends its disk
    // work.
    if (effects.changed)
    {
        // A torrent the person stopped recovers too, so it shows its saved
        // progress instead of the error; ApplyIntent keeps it stopped.
        torrent.ApplyIntent();
        Invalidate(id);
        statusPending = true;
    }
    for (auto const& job : effects.jobs)
    {
        if (job->kind == JobKind::Release)
        {
            torrent.released = ReleaseStorage(torrent.handle).share();
        }
        else if (job->kind == JobKind::Reload)
        {
            if (!job->lost.empty())
            {
                log.Write("storage", id, "files_lost");
            }
            Reload(torrent, job);
        }
    }
    if (auto after = stage(torrent.control); after != before)
    {
        if (after == FailureStage::Stopped)
        {
            log.Write("storage", id, "stopped");
        }
        else if (after == FailureStage::Person)
        {
            log.Write("storage", id, "retry_needed");
        }
        else if (after == FailureStage::Continued)
        {
            log.Write("storage", id, "resumed");
        }
    }
    if (effects.retryMove)
    {
        log.Write("move", id, "resumed");
    }
    return effects;
}

void Engine::State::Isolate(std::string const& id, std::function<void()> const& work)
{
    try
    {
        work();
    }
    catch (std::exception const& error)
    {
        log.Write("engine", id, error.what());
    }
}

void Engine::State::RetryStorage()
{
    auto now = std::chrono::steady_clock::now();
    bool minute = !IsShuttingDown() && now >= stoppedCheckAt;
    if (minute)
    {
        stoppedCheckAt = now + stoppedCheck;
    }
    std::map<std::string, std::vector<Attempt>, PathLess> probes;
    std::map<std::string, std::vector<Attempt>, PathLess> stopped;
    std::vector<Attempt> finals;
    auto collect = [&probes, &stopped, &finals](Torrent const& torrent, Control::Effects const& effects)
    {
        for (auto const& job : effects.jobs)
        {
            Attempt attempt{torrent.torrentId, torrent.handle, job};
            if (job->kind == JobKind::Probe)
            {
                probes[torrent.facts.savePath].push_back(std::move(attempt));
            }
            else if (job->kind == JobKind::Check)
            {
                stopped[torrent.facts.savePath].push_back(std::move(attempt));
            }
            else if (job->kind == JobKind::FinalCheck)
            {
                finals.push_back(std::move(attempt));
            }
        }
    };
    // A removed torrent's files stay busy until its release ends too.
    EachWorking([this, &collect](Torrent& torrent)
    {
        if (!torrent.released.valid() ||
            torrent.released.wait_for(std::chrono::seconds(0)) != std::future_status::ready)
        {
            return;
        }
        Isolate(torrent.torrentId, [this, &torrent, &collect]
        {
            auto released = std::exchange(torrent.released, {});
            try
            {
                released.get();
            }
            catch (std::exception const& error)
            {
                log.Write("storage", torrent.torrentId, error.what());
                torrent.released = ReleaseStorage(torrent.handle).share();
                return;
            }
            torrent.checkReads = CheckReads::None;
            if (auto job = torrent.control.Running(); job && job->kind == JobKind::Release)
            {
                collect(torrent, Steer(torrent, Control::Released{job}));
            }
        });
    });
    auto entry = torrents.upper_bound(storageCursor);
    for (std::size_t visited = 0; visited < torrents.size(); ++visited)
    {
        if (entry == torrents.end())
        {
            entry = torrents.begin();
        }
        auto& [id, torrent] = *entry++;
        if (torrent.restore || !torrent.control.Failing())
        {
            continue;
        }
        // One pass probes at most 64 folders; the next starts after the last
        // torrent this one probed.
        if (torrent.control.WantsProbe() && probes.size() == 64 && !probes.contains(torrent.facts.savePath))
        {
            continue;
        }
        Isolate(id, [this, &id, &torrent, &collect, minute]
        {
            auto effects = Steer(torrent, Control::Due{minute && !stoppedChecks.contains(torrent.facts.savePath)});
            if (std::any_of(effects.jobs.begin(), effects.jobs.end(),
                [](auto const& job) { return job->kind == JobKind::Probe; }))
            {
                storageCursor = id;
            }
            collect(torrent, effects);
        });
    }
    for (auto& [folder, attempts] : probes)
    {
        Probe(folder, std::move(attempts));
    }
    for (auto& [folder, attempts] : stopped)
    {
        CheckStopped(folder, std::move(attempts));
    }
    for (auto const& attempt : finals)
    {
        CheckFinalFolder(attempt);
    }
}

void Engine::State::Probe(std::string const& folder, std::vector<Attempt> attempts)
{
    std::vector<lt::torrent_handle> handles;
    for (auto const& attempt : attempts)
    {
        handles.push_back(attempt.handle);
    }
    // Once the folder answers, which files each torrent lost; nothing while
    // it does not answer.
    VolumeWorker(folder).Run([folder, handles]
    {
        std::optional<std::vector<LostFiles>> lost;
        if (!Reachable(Wide(folder)))
        {
            return lost;
        }
        lost.emplace();
        for (auto const& handle : handles)
        {
            lost->push_back(LostData(handle));
        }
        // A location that stops answering during the search makes every file
        // look lost, so lost files count only when it still answers.
        if (std::any_of(lost->begin(), lost->end(), [](auto const& files) { return !files.empty(); }) &&
            !Reachable(Wide(folder)))
        {
            lost.reset();
        }
        return lost;
    }, [this, folder, attempts = std::move(attempts)](
        std::expected<std::optional<std::vector<LostFiles>>, StorageOutcome> lost)
    {
        bool answered = lost && *lost;
        // A drive lookup made while the folder did not answer could name no
        // drive, so the next job that asks looks it up again.
        if (auto found = drives.find(folder); answered && found != drives.end() && found->second)
        {
            drives.erase(found);
        }
        for (std::size_t item = 0; item < attempts.size(); ++item)
        {
            auto const& attempt = attempts[item];
            Isolate(attempt.torrentId, [this, &attempt, &lost, answered, item]
            {
                auto torrent = FindWorking(attempt.torrentId);
                if (!torrent || torrent->handle != attempt.handle)
                {
                    return;
                }
                Control::Probed probed{attempt.job};
                if (answered)
                {
                    probed.finding = Control::Usable{std::move((**lost)[item])};
                }
                else if (lost)
                {
                    probed.finding = Control::Absent{};
                }
                Steer(*torrent, probed);
            });
        }
    });
}

// Checks, without starting the torrents, whether the cause that stopped each
// one is gone, from the facts that the drive of their folder gives.
void Engine::State::CheckStopped(std::string const& folder, std::vector<Attempt> attempts)
{
    struct Request
    {
        lt::torrent_handle handle;
        std::filesystem::path file;
        bool write = true;
        std::uint64_t needed = 0;
    };
    std::vector<Request> requests;
    for (auto const& attempt : attempts)
    {
        auto const& torrent = Working(attempt.torrentId);
        // A seed only reads; a download needs room for what it still lacks.
        Request request{attempt.handle};
        request.write = !torrent.status.is_seeding;
        auto index = torrent.status.error_file;
        if (torrent.names && index >= lt::file_index_t(0) && index < torrent.names->metadata->layout().end_file())
        {
            request.file = FullPath(Wide(torrent.names->mappings.file_path(torrent.names->metadata->layout(),
                index, torrent.facts.savePath)));
        }
        if (request.write)
        {
            request.needed =
                static_cast<std::uint64_t>(torrent.status.total_wanted - torrent.status.total_wanted_done);
        }
        requests.push_back(std::move(request));
    }
    stoppedChecks.insert(folder);
    VolumeWorker(folder).Run([folder, requests]
    {
        std::vector<Control::Finding> found;
        for (auto const& request : requests)
        {
            Control::Finding finding = Control::Absent{};
            if (Reachable(Wide(folder)))
            {
                finding = Control::Unusable{};
                if (Usable(Wide(folder), request.file, request.write, request.needed))
                {
                    // As in a probe, lost files count only while the folder
                    // still answers.
                    auto lost = LostData(request.handle);
                    finding = lost.empty() || Reachable(Wide(folder)) ?
                        Control::Finding{Control::Usable{std::move(lost)}} : Control::Finding{Control::Absent{}};
                }
            }
            found.push_back(std::move(finding));
        }
        return found;
    }, [this, folder, attempts](std::expected<std::vector<Control::Finding>, StorageOutcome> found)
    {
        stoppedChecks.erase(folder);
        for (std::size_t item = 0; item < attempts.size(); ++item)
        {
            auto const& attempt = attempts[item];
            Isolate(attempt.torrentId, [this, &attempt, &found, item]
            {
                auto torrent = FindWorking(attempt.torrentId);
                if (!torrent || torrent->handle != attempt.handle)
                {
                    return;
                }
                // A failed check ends its job, so the next minute checks
                // again.
                Control::Checked checked{attempt.job};
                if (found)
                {
                    checked.finding = std::move((*found)[item]);
                }
                Steer(*torrent, checked);
            });
        }
    });
}

// Reads the failed move's sources on the drive of the torrent's own folder,
// then checks the final folder and its room on that folder's drive.
void Engine::State::CheckFinalFolder(Attempt const& attempt)
{
    auto const& torrent = Working(attempt.torrentId);
    auto destination = torrent.facts.finalFolder;
    VolumeWorker(torrent.facts.savePath).Run([files = Relocated(destination, torrent)]
    {
        return Payload::Sources(files);
    }, [this, attempt, destination](std::expected<std::vector<Payload::Source>, StorageOutcome> sources)
    {
        Isolate(attempt.torrentId, [this, &attempt, &destination, &sources]
        {
            auto torrent = FindWorking(attempt.torrentId);
            if (!torrent || torrent->handle != attempt.handle)
            {
                return;
            }
            if (!sources)
            {
                Steer(*torrent, Control::Checked{attempt.job});
                return;
            }
            if (!torrent->control.Owns(attempt.job))
            {
                return;
            }
            VolumeWorker(destination).Run([destination, sources = std::move(*sources)]
            {
                if (!Reachable(Wide(destination)))
                {
                    return Control::Finding{Control::Absent{}};
                }
                if (!Usable(Wide(destination), {}, true, 0) || !Payload::HasRoom(sources))
                {
                    return Control::Finding{Control::Unusable{}};
                }
                return Control::Finding{Control::Usable{}};
            }, [this, attempt](std::expected<Control::Finding, StorageOutcome> finding)
            {
                Isolate(attempt.torrentId, [this, &attempt, &finding]
                {
                    auto torrent = FindWorking(attempt.torrentId);
                    if (!torrent || torrent->handle != attempt.handle)
                    {
                        return;
                    }
                    Control::Checked checked{attempt.job};
                    if (finding)
                    {
                        checked.finding = std::move(*finding);
                    }
                    Steer(*torrent, checked);
                });
            });
        });
    });
}

void Engine::State::ProbeRoots()
{
    auto now = std::chrono::steady_clock::now();
    std::set<std::string> wanted;
    for (auto const& [id, torrent] : torrents)
    {
        if (torrent.facts.intent == Intent::Paused || torrent.control.StorageWaits())
        {
            continue;
        }
        if (auto root = NoticeRoot(torrent.facts.savePath))
        {
            wanted.insert(RootKey(Wide(*root)));
        }
    }
    // A root whose probe has not returned stays, so its notice remains after
    // its torrents are held.
    std::erase_if(rootProbes, [&wanted](auto const& entry)
        { return !entry.second.pending && !wanted.contains(entry.first); });
    for (auto const& key : wanted)
    {
        auto& probe = rootProbes[key];
        if (!probe.worker)
        {
            probe.worker = std::make_unique<Store>(wake);
        }
        if (probe.pending || (probe.at && now - *probe.at < rootProbe))
        {
            continue;
        }
        probe.pending = true;
        probe.at = now;
        probe.worker->Run([root = Wide(key)]
        {
            // The root answers once Windows returns, whatever it returns.
            auto opened = CreateFileW(root.c_str(), 0, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                nullptr, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, nullptr);
            if (opened != INVALID_HANDLE_VALUE)
            {
                CloseHandle(opened);
            }
        }, [this, key](StorageOutcome)
        {
            if (auto probe = rootProbes.find(key); probe != rootProbes.end())
            {
                probe->second.pending = false;
            }
        });
    }
}

void Engine::State::ReloadStorage(lt::torrent_handle const& handle, Replacement& replacement)
{
    session->remove_torrent(handle);
    // Native resume data omits the subscription to status updates.
    replacement.params.flags |= lt::torrent_flags::update_subscribe;
    replacement.handle = session->add_torrent(replacement.params, replacement.error);
    if (replacement.error)
    {
        return;
    }
    replacement.statusAt = lt::clock_type::now();
    replacement.status = replacement.handle.status(routineQuery);
}

void Engine::State::SwapHandle(Torrent& torrent, Replacement& replacement)
{
    handles.erase(torrent.handle);
    seedQueries.erase(torrent.handle);
    torrent.queries.clear();
    torrent.pendingCheckpoint.reset();
    torrent.checkpointPhase = CheckpointPhase::Idle;
    torrent.checkReads = CheckReads::None;
    torrent.released = {};
    torrent.checkTurn.reset();
    // The removed handle never confirms its write.
    if (torrent.cacheWrite == CacheWrite::Flushing)
    {
        torrent.cacheWrite = CacheWrite::Pending;
    }
    torrent.completion.Replaced();
    if (replacement.error)
    {
        // RestorePending adds the torrent again from these parameters.
        torrent.handle = {};
        torrent.restore = std::move(replacement.params);
    }
    else
    {
        torrent.handle = replacement.handle;
        torrent.status = std::move(replacement.status);
        torrent.statusAt = replacement.statusAt;
        torrent.diskError = torrent.status.errc;
        if (torrents.contains(torrent.torrentId))
        {
            handles.emplace(torrent.handle, &torrent);
        }
        ApplyPolicy(torrent.handle);
        if (torrent.facts.trackers)
        {
            torrent.handle.replace_trackers(*torrent.facts.trackers);
        }
    }
    Invalidate(torrent.torrentId);
    statusPending = true;
    queuePending = true;
    // Only now, with the new handle or its parameters in place, may the
    // control's event apply the torrent's running state.
    Steer(torrent, Control::Dropped{});
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

void Engine::State::Reload(Torrent& torrent, std::shared_ptr<Control::Job const> job)
{
    auto id = torrent.torrentId;
    auto handle = torrent.handle;
    auto params = std::make_shared<lt::add_torrent_params>();
    // The worker does only what waits on the drive: the resume data and the
    // release. The engine thread, which owns the handle, replaces it.
    payload.Run([this, handle, params, lost = job->lost, stop = exitStop.get_token()]
    {
        *params = handle.get_resume_data(lt::torrent_handle::save_info_dict);
        DropPieces(*params, lost);
        params->flags &= ~(lt::torrent_flags::seed_mode | lt::torrent_flags::no_verify_files |
            lt::torrent_flags::auto_managed | lt::torrent_flags::upload_mode);
        params->flags |= lt::torrent_flags::paused;
        // The paused torrent issues no disk job after this release.
        ReleaseFiles({handle});
        if (stop.stop_requested())
            throw std::runtime_error("shutting_down");
    }, [this, id, handle, job, params](StorageOutcome outcome)
    {
        auto torrent = FindWorking(id);
        if (!torrent || torrent->handle != handle || torrent->control.Running() != job)
            return;
        // Exit saves the torrent through its old handle, without the pieces
        // of the lost files, because each checkpoint drops them until a
        // reload succeeds.
        if (IsShuttingDown())
            return;
        if (outcome.failure)
        {
            log.Write("storage", id, outcome.detail);
            Steer(*torrent, Control::Reloaded{job});
            return;
        }
        // A removed torrent is not added again; its removal ends the handle.
        if (!torrents.contains(id))
        {
            Steer(*torrent, Control::Reloaded{job});
            return;
        }
        Replacement replacement{.params = std::move(*params)};
        ReloadStorage(handle, replacement);
        SwapHandle(*torrent, replacement);
        if (replacement.error)
        {
            log.Write("storage", id, replacement.error.message());
            Steer(*torrent, Control::Reloaded{job});
            return;
        }
        torrent->renameAt = {};
        torrent->unsaved = true;
        Steer(*torrent, Control::Reloaded{job, true});
        torrent->ApplyIntent();
    });
}
}
