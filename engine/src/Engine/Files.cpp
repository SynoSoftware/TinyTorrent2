#include "Engine/State.h"
#include <Windows.h>
#include <libtorrent/torrent_info.hpp>
#include <libtorrent/disk_buffer_holder.hpp>
#include <libtorrent/aux_/session_impl.hpp>
#include <libtorrent/aux_/torrent.hpp>
#include <boost/asio/post.hpp>
#include <boost/asio/error.hpp>
#include <algorithm>
#include <future>
#include <set>
#include <system_error>

namespace tt
{
namespace
{
// Marks the file of an unfinished download until its rename to its final
// name.
constexpr char unfinishedSuffix[] = ".!tt";
// How long a failed rename, or a failed read of the names before one, waits
// before it runs again.
constexpr auto renameRetry = std::chrono::seconds(30);

// Marks a downloaded file as coming from the internet, as browsers do, so
// Windows warns before it runs. The mark reaches the disk before this
// returns, so a rename after it cannot outlast it in a crash. Returns the
// Windows error, or ERROR_SUCCESS.
DWORD WriteMark(std::filesystem::path const& path)
{
    // Opening the stream would create a missing file.
    auto base = CreateFileW(path.c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (base == INVALID_HANDLE_VALUE)
        return GetLastError();
    auto stream = path.wstring() + L":Zone.Identifier";
    auto file = CreateFileW(stream.c_str(), GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
        nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    auto error = file == INVALID_HANDLE_VALUE ? GetLastError() : ERROR_SUCCESS;
    if (file != INVALID_HANDLE_VALUE)
    {
        constexpr char zone[] = "[ZoneTransfer]\r\nZoneId=3\r\n";
        DWORD written = 0;
        if (!WriteFile(file, zone, sizeof(zone) - 1, &written, nullptr) || !FlushFileBuffers(file))
            error = GetLastError();
        else if (written != sizeof(zone) - 1)
            error = ERROR_WRITE_FAULT;
        CloseHandle(file);
    }
    CloseHandle(base);
    return error;
}
}

// Called on a worker after pausing every owner. The pinned native interface
// supplies the disk callback that public, untagged flush alerts lack.
void Engine::State::ReleaseFiles(std::vector<lt::torrent_handle> const& handles)
{
    for (auto const& handle : handles)
        ReleaseStorage(handle).get();
}

std::future<void> Engine::State::ReleaseStorage(lt::torrent_handle const& handle)
{
    auto released = std::make_shared<std::promise<void>>();
    auto ready = released->get_future();
    boost::asio::post(session->get_context(), [session = session->native_handle(), handle, released]
    {
        try
        {
            auto torrent = handle.native_handle();
            if (!torrent || !torrent->has_storage())
            {
                released->set_value();
                return;
            }
            session->disk_thread().async_release_files(torrent->storage(), [torrent, released]
            {
                released->set_value();
            });
            session->disk_thread().submit_jobs();
        }
        catch (...)
        {
            released->set_exception(std::current_exception());
        }
    });
    return ready;
}

std::optional<std::vector<std::byte>> Engine::State::ReadPiece(lt::torrent_handle handle, lt::piece_index_t piece)
{
    struct Read
    {
        std::vector<std::byte> bytes;
        std::promise<void> done;
        std::exception_ptr failure;
        int pending = 1;
        bool present = true;

        void Finish()
        {
            if (--pending == 0)
                done.set_value();
        }
    };
    auto read = std::make_shared<Read>();
    auto ready = read->done.get_future();
    boost::asio::post(session->get_context(), [native = session->native_handle(), handle, piece, read]
    {
        try
        {
            auto torrent = handle.native_handle();
            if (!torrent || !torrent->has_storage())
                throw lt::system_error(lt::errors::invalid_torrent_handle);
            auto const& metadata = torrent->torrent_file();
            read->bytes.resize(metadata.piece_size(piece));
            constexpr int block = 16 * 1024;
            for (int offset = 0; offset < static_cast<int>(read->bytes.size()); offset += block)
            {
                auto count = std::min(block, static_cast<int>(read->bytes.size()) - offset);
                ++read->pending;
                try
                {
                    native->disk_thread().async_read(torrent->storage(), {piece, offset, count},
                        [torrent, read, offset, count](lt::disk_buffer_holder bytes, lt::storage_error const& error)
                    {
                        if (error)
                        {
                            read->present = false;
                            if (error.ec != boost::system::errc::no_such_file_or_directory &&
                                error.ec != lt::errors::file_too_short && error.ec != boost::asio::error::eof)
                                read->failure = std::make_exception_ptr(lt::system_error(error.ec));
                        }
                        else
                            std::memcpy(read->bytes.data() + offset, bytes.data(), count);
                        read->Finish();
                    });
                }
                catch (...)
                {
                    --read->pending;
                    throw;
                }
            }
        }
        catch (...)
        {
            read->failure = std::current_exception();
        }
        native->disk_thread().submit_jobs();
        read->Finish();
    });
    ready.get();
    if (read->failure)
        std::rethrow_exception(read->failure);
    if (!read->present)
        return std::nullopt;
    return std::move(read->bytes);
}

// Called on a worker: runs `work` on libtorrent's network thread, waits for
// it and rethrows what it throws.
void Engine::State::SyncCall(lt::session& session, std::function<void()> work)
{
    // Shared, because the worker can return from get() while the network
    // thread is still inside set_value.
    auto done = std::make_shared<std::promise<void>>();
    auto ready = done->get_future();
    boost::asio::post(session.get_context(), [work = std::move(work), done]
    {
        try
        {
            work();
            done->set_value();
        }
        catch (...)
        {
            done->set_exception(std::current_exception());
        }
    });
    ready.get();
}

std::filesystem::path Engine::State::FullPath(std::filesystem::path const& path)
{
    auto full = std::filesystem::absolute(path).lexically_normal();
    // A folder typed with a trailing separator names the same folder, and
    // comparisons and folder pruning must find it equal. A root keeps its
    // separator.
    return full.has_filename() || !full.has_relative_path() ? full : full.parent_path();
}

// Windows compares file names without case.
bool Engine::State::PathBefore(std::filesystem::path const& left, std::filesystem::path const& right)
{
    return CompareStringOrdinal(left.c_str(), -1, right.c_str(), -1, TRUE) == CSTR_LESS_THAN;
}

bool Engine::State::SamePath(std::filesystem::path const& left, std::filesystem::path const& right)
{
    return !PathBefore(left, right) && !PathBefore(right, left);
}

bool Engine::State::PathLess::operator()(std::string const& left, std::string const& right) const
{
    return PathBefore(FullPath(Wide(left)), FullPath(Wide(right)));
}

// Compares two UTF-8 folders, such as save paths, as full paths.
bool Engine::State::SameFolder(std::string const& left, std::string const& right)
{
    return SamePath(FullPath(Wide(left)), FullPath(Wide(right)));
}

bool Engine::State::FoldersOverlap(std::string const& left, std::string const& right)
{
    auto inside = [](std::filesystem::path folder, std::filesystem::path const& outer)
    {
        while (true)
        {
            if (SamePath(folder, outer))
                return true;
            if (!folder.has_relative_path())
                return false;
            folder = folder.parent_path();
        }
    };
    auto first = FullPath(Wide(left));
    auto second = FullPath(Wide(right));
    return inside(first, second) || inside(second, first);
}

bool Engine::State::FilesBusy(std::string const& root) const
{
    return RenamesOn(root) || fileWork.Holds([this, &root](auto const& folder) { return RootOf(folder) == root; },
        false);
}

bool Engine::State::DeletingOrRenaming(std::string const& root) const
{
    return RenamesOn(root) || fileWork.Holds([this, &root](auto const& folder) { return RootOf(folder) == root; },
        true);
}

bool Engine::State::RenamesOn(std::string const& root) const
{
    return renames.contains(root) || std::any_of(renames.begin(), renames.end(),
        [this, &root](auto const& entry) { return RootOf(Utf8(entry.second.source.wstring())) == root; });
}

bool Engine::State::Adding(std::string const& root) const
{
    // One that waits for its folder's lookup has no disk work yet, and its
    // path's root may not be where it saves.
    return std::any_of(additions.begin(), additions.end(), [this, &root](auto const& entry)
    {
        return entry.second.phase != AdditionPhase::Locating && RootOf(entry.second.facts.savePath) == root;
    });
}

void Engine::State::ContinueNames()
{
    // A move keeps each member busy until that member's own move ends, so a
    // member that has ended prepares its names while the rest of its group
    // moves. The move preflight keeps the move's paths apart from every other
    // torrent's.
    if (IsShuttingDown() || !changes.IsIdle())
        return;
    constexpr std::ptrdiff_t limit = 8;
    auto pending = std::count_if(torrents.begin(), torrents.end(),
        [](auto const& entry) { return entry.second.namePhase == NamePhase::Preparing; });
    auto now = std::chrono::steady_clock::now();
    for (auto& [id, torrent] : torrents)
    {
        // A deletion or rename can change the files among which the names
        // are chosen, so it holds the names on its own root.
        if (torrent.restore || torrent.control.Recovers() || torrent.namePhase != NamePhase::Pending ||
            torrent.FilesBusy() || now < torrent.renameAt || DeletingOrRenaming(RootOf(torrent.facts.savePath)))
            continue;
        auto metadata = torrent.handle.torrent_file();
        if (!metadata)
            continue;
        if (!torrent.names)
        {
            torrent.filesPending = true;
            continue;
        }
        if (pending >= limit)
            continue;
        ++pending;
        torrent.namePhase = NamePhase::Preparing;
        torrent.needsRecheck |= torrent.facts.verifyFiles;
        torrent.ApplyIntent();
        lt::add_torrent_params params;
        params.ti = metadata;
        params.save_path = torrent.facts.savePath;
        // On its drive's worker, so a share that does not answer holds up only
        // its own torrents' names.
        VolumeWorker(torrent.facts.savePath).Run([this, params = std::move(params), handle = torrent.handle,
            appendsSuffix = torrent.facts.appendsSuffix, layout = torrent.facts.layout]() mutable
        {
            ReleaseFiles({handle});
            auto current = handle.get_renamed_files();
            params.renamed_files = current.export_filenames(params.ti->layout());
            PrepareNames(params, appendsSuffix, layout);
            auto renames = Renames(params, current);
            return NameChoice{std::move(params), std::move(renames)};
        }, [this, id](std::expected<NameChoice, StorageOutcome> choice)
        {
            auto found = FindWorking(id);
            if (!found || found->namePhase != NamePhase::Preparing)
                return;
            auto& torrent = *found;
            if (!choice)
            {
                FailNames(torrent, choice.error());
                return;
            }
            torrent.needsRecheck |= ApplyNames(torrent.handle, choice->renames, torrent.renaming);
            if (torrent.renaming.empty())
                FinishNames(torrent);
        });
    }
}

void Engine::State::FailNames(Torrent& torrent, StorageOutcome const& failure)
{
    torrent.namePhase = NamePhase::Pending;
    torrent.renameAt = std::chrono::steady_clock::now() + renameRetry;
    if (failure.cause)
    {
        torrent.nameError = Problem::From(failure.cause);
        Steer(torrent, Control::Failed{*torrent.nameError, Retryable(failure.cause)});
    }
    log.Write("rename", torrent.torrentId, failure.detail);
    ContinueNames();
}

bool Engine::State::IsUnfinished(std::string const& actual, std::string const& name)
{
    return SamePath(FullPath(Wide(actual)), FullPath(Wide(name + unfinishedSuffix)));
}

void Engine::State::PrepareNames(lt::add_torrent_params& params, bool appendsSuffix, Layout layout)
{
    if (!params.ti)
        return;
    auto const& files = params.ti->layout();
    auto root = std::filesystem::path(Wide(params.save_path));
    // Windows reports the files of a drive or share that does not answer as
    // missing, which would give each the unfinished suffix, so preparation
    // fails unless the folder answers before and after it reads them.
    CheckReachable(root);
    for (auto index : files.file_range())
    {
        if (files.pad_file_at(index))
            continue;
        auto name = ContentPath(files, index, layout);
        auto path = root / Wide(name);
        if (auto renamed = params.renamed_files.find(index); renamed != params.renamed_files.end())
        {
            if (IsUnfinished(renamed->second, name) &&
                std::filesystem::symlink_status(root / Wide(renamed->second)).type() ==
                    std::filesystem::file_type::not_found &&
                std::filesystem::symlink_status(path).type() != std::filesystem::file_type::not_found)
            {
                if (SamePath(FullPath(Wide(name)), FullPath(Wide(files.file_path(index)))))
                    params.renamed_files.erase(renamed);
                else
                    renamed->second = name;
            }
            continue;
        }
        if (appendsSuffix && std::filesystem::symlink_status(path).type() == std::filesystem::file_type::not_found)
        {
            params.renamed_files[index] = name + unfinishedSuffix;
        }
        else if (!SamePath(FullPath(Wide(name)), FullPath(Wide(files.file_path(index)))))
            params.renamed_files[index] = name;
    }
    CheckReachable(root);
}

std::vector<std::pair<lt::file_index_t, std::string>> Engine::State::Renames(lt::add_torrent_params const& prepared,
    lt::renamed_files const& current)
{
    auto const& files = prepared.ti->layout();
    std::vector<std::pair<lt::file_index_t, std::string>> renames;
    for (auto index : files.file_range())
    {
        auto desired = prepared.renamed_files.find(index);
        auto name = desired == prepared.renamed_files.end() ? files.file_path(index) : desired->second;
        if (!SamePath(FullPath(Wide(current.file_path(files, index))), FullPath(Wide(name))))
            renames.emplace_back(index, std::move(name));
    }
    return renames;
}

bool Engine::State::ApplyNames(lt::torrent_handle const& handle,
    std::vector<std::pair<lt::file_index_t, std::string>> const& renames, std::set<lt::file_index_t>& pending)
{
    for (auto const& [index, name] : renames)
    {
        pending.insert(index);
        handle.rename_file(index, name);
    }
    return !renames.empty();
}

void Engine::State::FinishNames(Torrent& torrent)
{
    torrent.namePhase = NamePhase::Ready;
    if (auto cause = std::exchange(torrent.nameError, std::nullopt))
        Steer(torrent, Control::Cleared{*cause});
    if (torrent.needsRecheck && !torrent.control.Recovers())
    {
        if (torrent.facts.finalFolder.empty())
            torrent.completion.BeginCheck();
        Recheck(torrent);
        queuePending = true;
    }
    torrent.ApplyIntent();
    ContinueNames();
}

void Engine::State::RecoverNames(Torrent& torrent)
{
    torrent.namePhase = NamePhase::Recovering;
    torrent.renaming.clear();
    torrent.filesPending = true;
    Invalidate(torrent.torrentId);
}

void Engine::State::QueryFiles()
{
    struct Observation
    {
        std::string torrentId;
        lt::torrent_handle handle;
        std::uint64_t generation;
        Layout layout;
        FileNames names;
        std::vector<std::int64_t> progress;
        // The files that hold all their bytes under their unfinished name;
        // nothing when libtorrent's progress does not cover every file.
        std::optional<std::set<lt::file_index_t>> completed;
    };
    // One job per root, on its worker, so a share that does not answer holds
    // up only the names of its own torrents.
    struct Group
    {
        std::string folder;
        std::shared_ptr<std::vector<Observation>> observations = std::make_shared<std::vector<Observation>>();
        std::vector<lt::torrent_handle> recovering;
    };
    std::map<std::string, Group> groups;
    std::size_t count = 0;
    auto now = std::chrono::steady_clock::now();
    EachWorking([this, &groups, &count, now](Torrent const& torrent)
    {
        bool recovers = torrent.namePhase == NamePhase::Recovering;
        if (count == 64 || !torrent.filesPending || torrent.restore ||
            (torrent.FilesBusy() && !recovers) || now < torrent.renameAt || !torrent.handle.torrent_file())
            return;
        auto root = RootOf(torrent.facts.savePath);
        if (queryingFiles.contains(root))
            return;
        auto& group = groups[root];
        group.folder = torrent.facts.savePath;
        group.observations->push_back({torrent.torrentId, torrent.handle, torrent.generation, torrent.facts.layout});
        ++count;
        if (recovers)
            group.recovering.push_back(torrent.handle);
    });
    for (auto& [root, group] : groups)
    {
        queryingFiles.insert(root);
        VolumeWorker(group.folder).Run([this, observations = group.observations, recovering = group.recovering]
        {
            ReleaseFiles(recovering);
            SyncCall(*session, [observations]
            {
                for (auto& observation : *observations)
                {
                    if (!observation.handle.is_valid())
                        continue;
                    auto status = observation.handle.status(
                        lt::torrent_handle::query_torrent_file | lt::torrent_handle::query_renamed_files);
                    observation.names = {status.torrent_file.lock(), std::move(status.renamed_files)};
                    if (observation.names.metadata)
                        observation.progress =
                            observation.handle.file_progress(lt::torrent_handle::piece_granularity);
                }
            });
            for (auto& observation : *observations)
            {
                if (observation.names.metadata)
                    observation.completed = Completed(observation.names, observation.progress, observation.layout);
            }
        }, [this, root = root, observations = group.observations](StorageOutcome outcome)
        {
            queryingFiles.erase(root);
            for (auto& observation : *observations)
            {
                auto found = FindWorking(observation.torrentId);
                if (!found || found->handle != observation.handle)
                    continue;
                auto& torrent = *found;
                if (outcome.failure)
                {
                    torrent.renameAt = std::chrono::steady_clock::now() + renameRetry;
                    log.Write("files", torrent.torrentId, outcome.detail);
                    continue;
                }
                if (torrent.generation != observation.generation ||
                    (torrent.FilesBusy() && torrent.namePhase != NamePhase::Recovering) || !observation.names.metadata)
                    continue;
                torrent.names = std::move(observation.names);
                if (observation.completed)
                {
                    torrent.completedFiles.insert(observation.completed->begin(), observation.completed->end());
                    torrent.filesPending = false;
                }
                if (torrent.namePhase == NamePhase::Recovering)
                    torrent.namePhase = NamePhase::Pending;
            }
        });
    }
}

std::optional<std::set<lt::file_index_t>> Engine::State::Completed(FileNames const& names,
    std::vector<std::int64_t> const& progress, Layout layout)
{
    auto const& files = names.metadata->layout();
    if (progress.size() < static_cast<std::size_t>(files.num_files()))
        return std::nullopt;
    std::set<lt::file_index_t> completed;
    for (auto index : files.file_range())
    {
        if (progress[static_cast<int>(index)] >= files.file_size(index) &&
            IsUnfinished(names.mappings.file_path(files, index), ContentPath(files, index, layout)))
            completed.insert(index);
    }
    return completed;
}

void Engine::State::FinishFiles(Torrent& torrent, std::optional<lt::file_index_t> after)
{
    // A removed torrent's file alerts still reach this, but it starts no
    // rename of its own. While a storage failure holds the torrent, a failed
    // rename runs again only when the ladder or the person continues it.
    // Other file work and additions hold the renames only on their own roots,
    // and only the torrents whose files can lie under this folder need their
    // names, which say whether one of their files takes the final name.
    auto root = RootOf(torrent.facts.savePath);
    if (IsShuttingDown() || torrent.restore || torrent.control.Holds() || !torrents.contains(torrent.torrentId) ||
        torrent.pathWork || torrent.filesPending ||
        torrent.namePhase != NamePhase::Ready ||
        torrent.completedFiles.empty() || std::chrono::steady_clock::now() < torrent.renameAt ||
        Adding(root) || !changes.IsIdle() || FilesBusy(root))
        return;
    if (!NamesReady(torrent.facts.savePath))
        return;
    auto const& files = torrent.names->metadata->layout();
    auto const& mapped = torrent.names->mappings;
    for (auto next = after ? torrent.completedFiles.upper_bound(*after) : torrent.completedFiles.begin();
        next != torrent.completedFiles.end();)
    {
        auto index = *next++;
        if (torrent.renaming.contains(index) || torrent.marking.contains(index))
            continue;
        auto name = ContentPath(files, index, torrent.facts.layout);
        auto actual = mapped.file_path(files, index);
        // A dropped rename alert leaves the entry of a file that already has
        // its final name, and FinishDownload waits until no entry is left.
        if (!IsUnfinished(actual, name))
        {
            torrent.completedFiles.erase(index);
            continue;
        }
        auto path = FullPath(std::filesystem::path(Wide(torrent.facts.savePath)) / Wide(actual));
        auto target = FullPath(std::filesystem::path(Wide(torrent.facts.savePath)) / Wide(name));
        Rename operation;
        operation.source = path;
        operation.target = target;
        operation.owners.push_back({torrent.torrentId, index, name});
        bool ready = true;
        EachWorking([this, &torrent, &path, &target, &ready, &operation](Torrent const& outside)
        {
            if (outside.torrentId == torrent.torrentId)
                return;
            if (outside.restore)
            {
                auto paths = FilePaths(outside, std::nullopt, false);
                ready &= !Overlaps(torrent.Hashes(), outside.Hashes()) &&
                    !std::binary_search(paths.begin(), paths.end(), path, PathBefore);
                return;
            }
            if (!outside.names)
                return;
            auto const& other = outside.names->metadata;
            auto const& mappings = outside.names->mappings;
            for (auto file : other->layout().file_range())
            {
                auto full = FullPath(Wide(mappings.file_path(other->layout(), file, outside.facts.savePath)));
                if (other->layout().pad_file_at(file) || !SamePath(full, path))
                    continue;
                ready &= !outside.pathWork && outside.renaming.empty() && outside.marking.empty() &&
                    outside.namePhase != NamePhase::Preparing;
                auto relative = target.lexically_relative(Wide(outside.facts.savePath));
                operation.owners.push_back(
                    {outside.torrentId, file, Utf8((relative.empty() ? target : relative).wstring())});
            }
        });
        if (ready && torrent.cacheWrite != CacheWrite::Written)
        {
            if (torrent.cacheWrite == CacheWrite::Pending)
            {
                torrent.cacheWrite = CacheWrite::Flushing;
                torrent.ApplyIntent();
                torrent.handle.flush_cache();
            }
            return;
        }
        if (ready)
        {
            // Later downloads write new data, which the next rename waits for.
            torrent.cacheWrite = CacheWrite::Pending;
            auto const& started = renames.emplace(root, std::move(operation)).first->second;
            for (auto const& owner : started.owners)
            {
                auto& member = Working(owner.torrentId);
                member.pathWork = PathWork::Rename;
                member.ApplyIntent();
            }
            ContinueRename(root);
            return;
        }
    }
}

// Moves a finished torrent's settled files to its final folder, and otherwise
// sends the Completed toast that is due.
void Engine::State::FinishDownload(Torrent& torrent)
{
    if (IsShuttingDown() || torrent.restore || torrent.control.Recovers() || torrent.filesPending ||
        torrent.completion.NeedsSample() || torrent.completion.IsWorking() || !torrent.status.is_finished || torrent.namePhase != NamePhase::Ready || torrent.FilesBusy() ||
        !torrent.completedFiles.empty() || torrent.facts.marker || torrent.waitingMove || torrent.unfinished)
        return;
    if (!torrent.facts.finalFolder.empty())
    {
        // The move waits its turn for its drives and busy files, as a move
        // the person asks for does.
        if (!torrent.MoveBlocked())
            torrent.waitingMove = WaitingMove{.destination = torrent.facts.finalFolder, .turn = ++turns, .final = true};
        return;
    }
    if (settings.rechecksFinished && torrent.completion.BeginCheck())
    {
        Verify({torrent.torrentId}, [this, id = torrent.torrentId](Outcome outcome)
        {
            if (outcome.error)
            {
                if (auto found = torrents.find(id); found != torrents.end())
                    found->second.completion.FailCheck();
                log.Write("verify", id, "completion_failed");
            }
        }, Requester::Engine);
        return;
    }
    if (torrent.completion.Finish())
    {
        Notify(ToastKind::Completed, torrent);
    }
}

void Engine::State::ContinueRename(std::string const& root)
{
    auto found = renames.find(root);
    if (found == renames.end())
        return;
    auto& rename = found->second;
    if (rename.phase == RenamePhase::Moving || rename.phase == RenamePhase::Observing)
        return;
    auto const& owner = rename.owners.at(rename.current);
    auto& torrent = Working(owner.torrentId);
    if (std::chrono::steady_clock::now() < torrent.renameAt)
        return;
    if (rename.phase == RenamePhase::Recovering)
    {
        rename.phase = RenamePhase::Observing;
        VolumeWorker(torrent.facts.savePath).Run([this, handle = torrent.handle]
        {
            ReleaseFiles({handle});
            return FileNames{handle.torrent_file(), handle.get_renamed_files()};
        }, [this, root, generation = torrent.generation](std::expected<FileNames, StorageOutcome> names)
        {
            auto& rename = renames.at(root);
            auto const& owner = rename.owners.at(rename.current);
            auto& torrent = Working(owner.torrentId);
            if (!names)
            {
                torrent.renameAt = std::chrono::steady_clock::now() + renameRetry;
                log.Write("rename", owner.torrentId, names.error().detail);
                rename.phase = RenamePhase::Recovering;
                return;
            }
            // A late rename alert can update the names after this worker read them.
            if (torrent.generation != generation)
            {
                rename.phase = RenamePhase::Recovering;
                ContinueRename(root);
                return;
            }
            torrent.names = std::move(*names);
            auto const& current = *torrent.names;
            auto actual = FullPath(Wide(current.mappings.file_path(
                current.metadata->layout(), owner.index, torrent.facts.savePath)));
            torrent.renaming.erase(owner.index);
            Invalidate(owner.torrentId);
            if (SamePath(actual, rename.target))
            {
                torrent.completedFiles.erase(owner.index);
                torrent.unsaved = true;
                if (++rename.current == rename.owners.size())
                {
                    EndRename(root);
                    return;
                }
            }
            rename.phase = RenamePhase::Naming;
            ContinueRename(root);
        });
        return;
    }
    if (torrent.renaming.contains(owner.index))
        return;
    if (rename.phase == RenamePhase::Ready)
    {
        std::vector<lt::torrent_handle> handles;
        std::vector<std::pair<lt::torrent_handle, lt::file_index_t>> files;
        for (auto const& member : rename.owners)
        {
            auto handle = Working(member.torrentId).handle;
            files.emplace_back(handle, member.index);
            if (std::find(handles.begin(), handles.end(), handle) == handles.end())
                handles.push_back(handle);
        }
        rename.phase = RenamePhase::Moving;
        // Which owners' files were complete, and the error of the mark that
        // the file gets before its rename.
        struct Renamed
        {
            std::vector<bool> complete;
            DWORD marked = ERROR_SUCCESS;
        };
        // On its drive's worker, so a share that stops answering holds up only
        // its own folders' work, not every torrent's checkpoints and names.
        VolumeWorker(Utf8(rename.source.parent_path().wstring())).Run(
            [this, handles, files, source = rename.source, target = rename.target]
        {
            ReleaseFiles(handles);
            Renamed renamed{std::vector<bool>(files.size(), false)};
            auto& complete = renamed.complete;
            for (auto const& handle : handles)
            {
                auto status = handle.status(lt::torrent_handle::query_save_path |
                    lt::torrent_handle::query_torrent_file | lt::torrent_handle::query_renamed_files);
                auto metadata = status.torrent_file.lock();
                if (!metadata)
                    return renamed;
                auto progress = handle.file_progress(lt::torrent_handle::piece_granularity);
                for (std::size_t item = 0; item < files.size(); ++item)
                {
                    auto const& [owner, index] = files[item];
                    if (owner != handle)
                        continue;
                    auto path = FullPath(Wide(status.renamed_files.file_path(metadata->layout(), index, status.save_path)));
                    complete[item] = static_cast<std::size_t>(static_cast<int>(index)) < progress.size() &&
                        progress[static_cast<int>(index)] >= metadata->layout().file_size(index) && SamePath(path, source);
                }
            }
            if (std::find(complete.begin(), complete.end(), false) != complete.end())
                return renamed;
            renamed.marked = WriteMark(source);
            if (!MoveFileExW(source.c_str(), target.c_str(), 0))
            {
                auto error = GetLastError();
                CheckReachable(source.parent_path());
                throw std::system_error(static_cast<int>(error), std::system_category(), Utf8(target.wstring()));
            }
            return renamed;
        }, [this, root](std::expected<Renamed, StorageOutcome> renamed)
        {
            auto& rename = renames.at(root);
            auto complete = renamed.transform([](Renamed const& value) { return value.complete; });
            // A drive without streams, such as FAT32, cannot hold the mark, so
            // the file still takes its final name.
            if (renamed && renamed->marked != ERROR_SUCCESS)
                log.Write("mark_of_the_web", rename.owners.front().torrentId, std::to_string(renamed->marked));
            if (!complete)
            {
                auto const& id = rename.owners.front().torrentId;
                auto& torrent = Working(id);
                torrent.renameAt = std::chrono::steady_clock::now() + renameRetry;
                auto const& cause = complete.error().cause;
                log.Write("rename", id, complete.error().detail);
                // A rename whose folder does not answer, or whose error no
                // wait fixes, is a storage failure, so the row shows it with
                // Retry. Any other error, such as a player that holds the
                // file open, tries again after renameRetry without an error.
                if (cause == Unreachable() || (cause && !Retryable(cause)))
                    Steer(torrent, Control::Failed{Problem::From(cause), Retryable(cause)});
                EndRename(root);
                return;
            }
            if (std::find(complete->begin(), complete->end(), false) != complete->end())
            {
                auto source = rename.owners.front();
                for (std::size_t item = 0; item < complete->size(); ++item)
                {
                    if ((*complete)[item])
                        continue;
                    auto const& owner = rename.owners[item];
                    auto& torrent = Working(owner.torrentId);
                    torrent.completedFiles.erase(owner.index);
                    torrent.filesPending = true;
                }
                EndRename(root);
                FinishFiles(Working(source.torrentId), source.index);
                return;
            }
            rename.phase = RenamePhase::Naming;
            ContinueRename(root);
        });
        return;
    }
    torrent.renaming.insert(owner.index);
    torrent.handle.rename_file(owner.index, owner.name);
}

void Engine::State::EndRename(std::string const& root)
{
    auto found = renames.find(root);
    auto owners = std::move(found->second.owners);
    renames.erase(found);
    for (auto const& owner : owners)
    {
        auto& torrent = Working(owner.torrentId);
        torrent.pathWork.reset();
        // A torrent that joined the abandonments during the rename gives its
        // files back next, so no turn sees it there without path work.
        if (Abandoning(owner.torrentId))
            torrent.pathWork = PathWork::Abandon;
        torrent.ApplyIntent();
    }
}

std::optional<std::string> Engine::State::Renaming(Torrent const& torrent, lt::file_index_t index) const
{
    for (auto const& [root, rename] : renames)
    {
        auto const& owner = rename.owners.at(rename.current);
        if (rename.phase == RenamePhase::Naming && owner.torrentId == torrent.torrentId && owner.index == index)
            return root;
    }
    return std::nullopt;
}

void Engine::State::On(lt::file_completed_alert const& alert)
{
    auto torrent = FindWorking(alert.handle);
    if (!torrent)
        return;
    auto metadata = alert.handle.torrent_file();
    if (!metadata || metadata->layout().pad_file_at(alert.index))
        return;
    // A file that keeps its unfinished name is marked before its rename, so
    // a crash before the rename leaves it to be marked at the next start.
    bool renames = false;
    if (torrent->names)
    {
        auto const& files = torrent->names->metadata->layout();
        auto name = torrent->names->mappings.file_path(files, alert.index);
        renames = IsUnfinished(name, ContentPath(files, alert.index, torrent->facts.layout));
        if (renames)
            torrent->completedFiles.insert(alert.index);
    }
    else
    {
        Invalidate(torrent->torrentId);
        torrent->filesPending = true;
    }
    // File completion is also posted by checking; the ordered state alerts
    // identify its cause even when the latest sampled status is already seeding.
    bool downloaded = torrent->completion.FileFinished();
    if (!downloaded || renames)
    {
        FinishFiles(*torrent);
        return;
    }
    Mark(*torrent, alert.index);
}

void Engine::State::Mark(Torrent& torrent, lt::file_index_t index)
{
    // From Exit on, a finished file waits for the next start, and Exit's
    // second save records it.
    if (IsShuttingDown())
    {
        waitingMarks[torrent.torrentId].insert(index);
        return;
    }
    torrent.marking.insert(index);
    VolumeWorker(torrent.facts.savePath).Run([handle = torrent.handle, index, stop = exitStop.get_token()]
    {
        // Exit stops the mark before it writes, as other file work stops
        // before its next file.
        if (stop.stop_requested())
            return false;
        auto status = handle.status(lt::torrent_handle::query_save_path |
            lt::torrent_handle::query_torrent_file | lt::torrent_handle::query_renamed_files);
        auto metadata = status.torrent_file.lock();
        if (!metadata)
            return true;
        auto path = FullPath(Wide(status.renamed_files.file_path(metadata->layout(), index, status.save_path)));
        if (auto error = WriteMark(path); error != ERROR_SUCCESS)
            throw std::runtime_error(std::to_string(error));
        return true;
    }, [this, id = torrent.torrentId, index](std::expected<bool, StorageOutcome> written)
    {
        if (!written)
            log.Write("mark_of_the_web", id, written.error().detail);
        if (auto found = FindWorking(id))
        {
            found->marking.erase(index);
            if (written && !*written)
                waitingMarks[id].insert(index);
            FinishFiles(*found);
        }
    });
}

void Engine::State::MarkWaiting(Torrent& torrent)
{
    auto found = waitingMarks.find(torrent.torrentId);
    if (found == waitingMarks.end() || torrent.restore)
        return;
    auto metadata = torrent.handle.torrent_file();
    if (!metadata)
        return;
    for (auto index : found->second)
    {
        if (index < metadata->layout().end_file())
            Mark(torrent, index);
    }
    waitingMarks.erase(found);
}

std::map<std::string, std::set<lt::file_index_t>> Engine::State::UnwrittenMarks() const
{
    auto unwritten = waitingMarks;
    std::erase_if(unwritten, [this](auto const& entry) { return !torrents.contains(entry.first); });
    for (auto const& [id, torrent] : torrents)
    {
        if (!torrent.marking.empty())
            unwritten[id].insert(torrent.marking.begin(), torrent.marking.end());
    }
    return unwritten;
}

void Engine::State::On(lt::file_renamed_alert const& alert)
{
    if (auto torrent = FindWorking(alert.handle))
    {
        if (torrent->names)
            torrent->names->mappings.rename_file(torrent->names->metadata->layout(), alert.index, alert.new_name());
        bool expected = torrent->renaming.erase(alert.index) != 0;
        torrent->completedFiles.erase(alert.index);
        torrent->unsaved = true;
        // Open torrent and Open folder must not use the old name.
        Invalidate(torrent->torrentId);
        if (auto root = Renaming(*torrent, alert.index))
        {
            auto& rename = renames.at(*root);
            if (++rename.current == rename.owners.size())
                EndRename(*root);
            else
                ContinueRename(*root);
        }
        if (expected && torrent->namePhase == NamePhase::Preparing && torrent->renaming.empty())
            FinishNames(*torrent);
        return;
    }
    for (auto& [id, addition] : additions)
    {
        if (addition.handle == alert.handle && addition.phase == AdditionPhase::Naming)
        {
            bool expected = addition.renaming.erase(alert.index) != 0;
            if (expected && addition.renaming.empty())
                SaveAddition(id, addition.handle);
            return;
        }
    }
}

void Engine::State::On(lt::file_rename_failed_alert const& alert)
{
    if (auto torrent = FindWorking(alert.handle))
    {
        bool expected = torrent->renaming.erase(alert.index) != 0;
        if (auto root = Renaming(*torrent, alert.index))
            renames.at(*root).phase = RenamePhase::Recovering;
        if (expected && torrent->namePhase == NamePhase::Preparing)
            RecoverNames(*torrent);
        torrent->renameAt = std::chrono::steady_clock::now() + renameRetry;
        // As for the rename of a finished file: only a folder that does not
        // answer, or an error that no wait fixes, is a storage failure.
        if (alert.error == Unreachable() || !Retryable(alert.error))
            Steer(*torrent, Control::Failed{Problem::From(alert.error), Retryable(alert.error)});
        log.Write("rename", torrent->torrentId, alert.error.message());
        return;
    }
    for (auto& [id, addition] : additions)
    {
        if (addition.handle == alert.handle && addition.phase == AdditionPhase::Naming &&
            addition.renaming.contains(alert.index))
        {
            // The torrent joins the list and chooses its names again once its
            // folder answers, as any torrent in the list does.
            if (alert.error == Unreachable())
            {
                addition.named = false;
                SaveAddition(id, addition.handle);
            }
            else
            {
                Abandon(id, {ErrorCode::AddFailed, alert.error.message()});
            }
            return;
        }
    }
}

// The full paths of the content's files at the folder, sorted by PathBefore.
std::vector<std::filesystem::path> Engine::State::FilePaths(lt::torrent_info const& metadata,
    std::string const& folder, Layout layout)
{
    std::filesystem::path root = Wide(folder);
    std::vector<std::filesystem::path> files;
    for (auto index : metadata.layout().file_range())
    {
        if (!metadata.layout().pad_file_at(index))
            files.push_back(FullPath(root / Wide(ContentPath(metadata.layout(), index, layout))));
    }
    std::sort(files.begin(), files.end(), PathBefore);
    files.erase(std::unique(files.begin(), files.end(), SamePath), files.end());
    return files;
}

std::vector<std::filesystem::path> Engine::State::FilePaths(Torrent const& torrent,
    std::optional<std::string> const& folder, bool logical) const
{
    auto at = [&torrent, logical](std::string const& root)
    {
        std::vector<std::filesystem::path> files;
        for (auto const& path : torrent.Paths(logical))
            files.push_back(FullPath(std::filesystem::path(Wide(root)) / path));
        std::sort(files.begin(), files.end(), PathBefore);
        files.erase(std::unique(files.begin(), files.end(), SamePath), files.end());
        return files;
    };
    if (folder)
        return at(*folder);
    auto files = at(torrent.facts.savePath);
    if (torrent.facts.marker)
    {
        auto staged = at(torrent.facts.marker->destination);
        files.insert(files.end(), staged.begin(), staged.end());
        std::sort(files.begin(), files.end(), PathBefore);
        files.erase(std::unique(files.begin(), files.end(), SamePath), files.end());
    }
    return files;
}

std::vector<std::filesystem::path> Engine::State::Footprint(Torrent const& torrent) const
{
    auto paths = FilePaths(torrent);
    auto physical = FilePaths(torrent, std::nullopt, false);
    paths.insert(paths.end(), physical.begin(), physical.end());
    std::sort(paths.begin(), paths.end(), PathBefore);
    paths.erase(std::unique(paths.begin(), paths.end(), SamePath), paths.end());
    return paths;
}

bool Engine::State::NamesReady(std::string const& folder) const
{
    bool ready = true;
    EachWorking([&folder, &ready](Torrent const& torrent)
    {
        ready &= torrent.NamesKnown() || !(FoldersOverlap(torrent.facts.savePath, folder) ||
            (torrent.facts.marker && FoldersOverlap(torrent.facts.marker->destination, folder)));
    });
    return ready;
}

bool Engine::State::NamesReadyFor(std::vector<std::string> const& ids) const
{
    return std::all_of(ids.begin(), ids.end(), [this](std::string const& id)
    {
        auto const& torrent = Working(id);
        auto const& marker = torrent.facts.marker;
        return NamesReady(torrent.facts.savePath) && (!marker || NamesReady(marker->destination));
    });
}

Outcome Engine::State::FilesReady(std::vector<std::string> const& ids, std::string const& destination) const
{
    // Other torrents' renames and name preparation delay the move where they
    // can reach the move's files: under the destination, or under a member's
    // save folder, where the shared-files refusal reads the other torrents'
    // file names; an addition there has no names yet. A deletion changes only
    // its own files, which the move can meet only at the destination.
    auto folders = Folders(ids, destination);
    auto overlaps = [&folders](std::string const& folder)
    {
        return std::any_of(folders.begin(), folders.end(), [&folder](auto const& used)
            { return FoldersOverlap(folder, used); });
    };
    auto reaches = [&ids, &overlaps](Torrent const& torrent)
    {
        return Contains(ids, torrent.torrentId) || overlaps(torrent.facts.savePath) ||
            (torrent.facts.marker && overlaps(torrent.facts.marker->destination));
    };
    // Only the parts that remain still change files; a part on a root that
    // does not answer holds no other root's moves.
    for (auto const& deletion : deletions)
    {
        for (auto const& [root, part] : deletion.parts)
        {
            auto folders = part.job->Folders();
            if (std::any_of(folders.begin(), folders.end(), [&destination](auto const& folder)
                { return FoldersOverlap(folder, destination); }))
                return {ErrorCode::FilesBusy, deletion.names};
        }
    }
    for (auto const& [root, rename] : renames)
    {
        for (auto const& owner : rename.owners)
        {
            auto torrent = FindWorking(owner.torrentId);
            if (!torrent || reaches(*torrent))
                return {ErrorCode::FilesBusy, torrent ? torrent->Name() : std::string()};
        }
    }
    for (auto const& [id, addition] : additions)
    {
        if (overlaps(addition.facts.savePath))
            return {ErrorCode::FilesBusy, addition.params.ti ? addition.params.ti->name() : addition.params.name};
    }
    // A member's path work ends when its files are in the new folder, but it
    // stays in its move until the whole move ends.
    for (auto const& moving : moves)
    {
        auto member = std::find_if(ids.begin(), ids.end(), [&moving](auto const& id) { return Contains(moving.ids, id); });
        if (member != ids.end())
            return {ErrorCode::FilesBusy, Working(*member).Name()};
    }
    Outcome refusal;
    EachWorking([&ids, &destination, &reaches, &refusal](Torrent const& torrent)
    {
        if (refusal.error)
            return;
        bool own = Contains(ids, torrent.torrentId);
        // A torrent in another move does not hold this move back, because
        // moves share no drive and so never reach the same files.
        bool elsewhere = torrent.pathWork && !own;
        // Files whose names are unknown lie under their torrent's save folder,
        // so only a save folder that overlaps the destination can hold one
        // the move would write.
        bool unknown = torrent.restore || !torrent.handle.torrent_file();
        // One that waits for its add is added by itself, so the move waits
        // for it as for busy files.
        if (torrent.AwaitsAdd() && (own || FoldersOverlap(torrent.facts.savePath, destination)))
            refusal = Outcome{ErrorCode::FilesBusy, torrent.Name()};
        else if (unknown && (own || FoldersOverlap(torrent.facts.savePath, destination)))
            refusal = Outcome{ErrorCode::MetadataUnavailable, torrent.Name()};
        else if ((((torrent.FilesBusy() && !elsewhere) || !torrent.NamesKnown()) && reaches(torrent)) ||
            (own && torrent.priorityCompletion))
            refusal = Outcome{ErrorCode::FilesBusy, torrent.Name()};
    });
    return refusal;
}

Engine::State::Scope Engine::State::FileScope(std::vector<std::string> const& ids) const
{
    Scope scope;
    std::vector<std::pair<std::filesystem::path, std::filesystem::path>> names;
    for (auto const& id : ids)
    {
        auto const& torrent = Working(id);
        // Not at an interrupted move's destination: what the move did not
        // finish there is unknown, and it can be someone else's file.
        auto files = FilePaths(torrent, torrent.facts.savePath, false);
        scope.files.insert(scope.files.end(), files.begin(), files.end());
        auto held = Footprint(torrent);
        scope.holds.insert(scope.holds.end(), held.begin(), held.end());
        auto physical = torrent.Paths();
        auto logical = torrent.Paths(true);
        std::vector<std::string> folders{torrent.facts.savePath};
        if (torrent.facts.marker)
            folders.push_back(torrent.facts.marker->destination);
        for (auto const& folder : folders)
        {
            if (folder.empty())
                continue;
            auto root = std::filesystem::path(Wide(folder));
            for (std::size_t index = 0; index < physical.size(); ++index)
                names.emplace_back(FullPath(root / physical[index]), FullPath(root / logical[index]));
        }
    }
    std::sort(scope.files.begin(), scope.files.end(), PathBefore);
    scope.files.erase(std::unique(scope.files.begin(), scope.files.end(), SamePath), scope.files.end());
    std::sort(scope.holds.begin(), scope.holds.end(), PathBefore);
    scope.holds.erase(std::unique(scope.holds.begin(), scope.holds.end(), SamePath), scope.holds.end());
    auto paths = scope.holds;
    std::set<std::filesystem::path, decltype(&PathBefore)> used(PathBefore);
    auto group = ids;
    for (std::size_t previous = 0; previous != group.size();)
    {
        previous = group.size();
        EachWorking([this, &ids, &group, &paths, &used, &scope](Torrent const& torrent)
        {
            auto const& id = torrent.torrentId;
            if (Contains(group, id))
            {
                return;
            }
            auto files = Footprint(torrent);
            if (torrent.restore && !torrent.restore->ti)
            {
                for (auto const& selected : ids)
                {
                    if (Overlaps(torrent.Hashes(), Working(selected).Hashes()))
                    {
                        auto held = Footprint(Working(selected));
                        files.insert(files.end(), held.begin(), held.end());
                    }
                }
            }
            bool overlaps = false;
            for (auto const& file : files)
            {
                if (std::binary_search(paths.begin(), paths.end(), file, PathBefore))
                {
                    overlaps = true;
                    used.insert(file);
                }
            }
            if (!overlaps)
            {
                return;
            }
            group.push_back(id);
            // A removed torrent still holds its files, but it is out of the
            // list, so nobody is told it shares them.
            if (torrents.contains(id))
            {
                scope.shared.push_back(id);
            }
            used.insert(files.begin(), files.end());
            paths.insert(paths.end(), files.begin(), files.end());
            std::sort(paths.begin(), paths.end(), PathBefore);
        });
    }
    std::set<std::filesystem::path, decltype(&PathBefore)> retained(PathBefore);
    for (auto const& [physical, logical] : names)
    {
        if (used.contains(physical) || used.contains(logical))
        {
            retained.insert(physical);
        }
    }
    // Only files in the original command count as kept by a deletion.
    std::set_intersection(scope.files.begin(), scope.files.end(), retained.begin(), retained.end(),
        std::back_inserter(scope.kept), PathBefore);
    return scope;
}

Json Engine::State::Describe(std::vector<std::string> const& ids, Scope const& scope) const
{
    auto describe = [this](std::vector<std::string> const& members)
    {
        Json list = Json::array();
        for (auto const& id : members)
        {
            auto const& torrent = torrents.at(id);
            list.push_back(Json{{"torrent_id", torrent.torrentId}, {"name", torrent.Name()},
                {"save_path", torrent.facts.savePath}, {"folder", torrent.Folder()},
                {"torrent_file", torrent.facts.torrentFile}});
        }
        return list;
    };
    return {{"torrents", describe(ids)}, {"shared", describe(scope.shared)}, {"kept_files", scope.kept.size()}};
}

// The torrents of the running file operations that hold a file this content
// would use at the destination.
std::vector<std::string> Engine::State::Holders(std::shared_ptr<lt::torrent_info const> const& metadata,
    std::string const& destination) const
{
    std::vector<std::string> names;
    if (!metadata || destination.empty() || (renames.empty() && moves.empty() && deletions.empty()))
    {
        return names;
    }
    auto files = FilePaths(*metadata, destination, settings.layout);
    auto holds = [&files](std::vector<std::filesystem::path> const& held)
    {
        return std::any_of(files.begin(), files.end(), [&held](auto const& file)
        {
            return std::any_of(held.begin(), held.end(), [&file](auto const& other) { return SamePath(file, other); });
        });
    };
    for (auto const& [root, rename] : renames)
    {
        if (!holds({rename.source, rename.target}))
            continue;
        for (auto const& owner : rename.owners)
            names.push_back(Working(owner.torrentId).Name());
    }
    // A member leaves the session once its own move has ended; the deletion
    // of its sources names it while that runs.
    for (auto const& moving : moves)
    {
        if (!holds(moving.holds))
            continue;
        for (auto const& id : moving.ids)
        {
            if (auto found = FindWorking(id))
                names.push_back(found->Name());
        }
    }
    // A removal that keeps the files holds them only until libtorrent lets go
    // and never changes them, so it shares nothing with new content.
    for (auto const& deleting : deletions)
    {
        if (deleting.mode && holds(deleting.holds))
            names.push_back(deleting.names);
    }
    return names;
}

// After libtorrent dropped alerts, each file operation that waited for one
// waits instead for a sign that comes after the drop.
void Engine::State::RecoverFiles()
{
    std::vector<std::string> recovering;
    for (auto& [root, rename] : renames)
    {
        if (rename.phase == RenamePhase::Naming)
        {
            rename.phase = RenamePhase::Recovering;
            recovering.push_back(root);
        }
    }
    for (auto const& root : recovering)
        ContinueRename(root);
    EachWorking([this](Torrent& torrent)
    {
        if (torrent.namePhase == NamePhase::Preparing && !torrent.renaming.empty())
        {
            RecoverNames(torrent);
        }
        else
        {
            Invalidate(torrent.torrentId);
            torrent.filesPending = true;
        }
    });
    RecoverMoves();
    RecoverDeletions();
}
}
