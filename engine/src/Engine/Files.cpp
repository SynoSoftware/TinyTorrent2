#include "Engine/State.h"
#include <Windows.h>
#include <Shellapi.h>
#include <ShlObj.h>
#include <libtorrent/torrent_info.hpp>
#include <libtorrent/aux_/session_impl.hpp>
#include <libtorrent/aux_/torrent.hpp>
#include <boost/asio/post.hpp>
#include <algorithm>
#include <future>
#include <set>

namespace tt
{
// Called on the payload worker after pausing every owner. The pinned native
// interface supplies the disk callback that public, untagged flush alerts lack.
void Engine::State::ReleaseFiles(std::vector<lt::torrent_handle> const& handles)
{
    for (auto const& handle : handles)
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
        ready.get();
    }
}

std::filesystem::path Engine::State::FullPath(std::filesystem::path const& path)
{
    return std::filesystem::absolute(path).lexically_normal();
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

bool Engine::State::FilesBusy() const
{
    return move.has_value() || !deletions.empty() || rename.has_value() ||
        std::any_of(torrents.begin(), torrents.end(), [](auto const& entry) { return entry.second.deleted; });
}

void Engine::State::PrepareFiles(Torrent& torrent)
{
    if (!changes.IsIdle() || torrent.deleted || torrent.restore || torrent.namePhase != NamePhase::Pending ||
        torrent.FilesBusy() || FilesBusy() ||
        std::chrono::steady_clock::now() < torrent.renameAt)
        return;
    auto metadata = torrent.handle.torrent_file();
    if (!metadata)
        return;
    if (!torrent.names)
    {
        torrent.filesPending = true;
        return;
    }
    constexpr std::ptrdiff_t limit = 8;
    auto pending = std::count_if(torrents.begin(), torrents.end(),
        [](auto const& entry) { return entry.second.namePhase == NamePhase::Preparing; });
    if (pending >= limit)
        return;
    torrent.namePhase = NamePhase::Preparing;
    torrent.needsRecheck |= torrent.facts.verifyFiles;
    torrent.ApplyIntent();
    auto prepared = std::make_shared<lt::add_torrent_params>();
    auto current = std::make_shared<lt::renamed_files>();
    prepared->ti = metadata;
    prepared->save_path = torrent.facts.savePath;
    payload.Run([this, prepared, current, handle = torrent.handle,
        appendsSuffix = torrent.facts.appendsSuffix, layout = torrent.facts.layout]
    {
        ReleaseFiles({handle});
        *current = handle.get_renamed_files();
        prepared->renamed_files = current->export_filenames(prepared->ti->layout());
        PrepareNames(*prepared, appendsSuffix, layout);
    },
        [this, id = torrent.torrentId, prepared, current](StorageOutcome outcome)
    {
        auto found = torrents.find(id);
        if (found == torrents.end())
            return;
        auto& torrent = found->second;
        if (!outcome.succeeded)
        {
            torrent.namePhase = NamePhase::Pending;
            torrent.renameAt = std::chrono::steady_clock::now() + std::chrono::seconds(30);
            log.Write("rename", id, outcome.detail);
            ContinueNames();
            return;
        }
        torrent.needsRecheck |= ApplyNames(torrent.handle, *prepared, *current, torrent.renaming);
        if (torrent.renaming.empty())
            FinishNames(torrent);
    });
}

void Engine::State::PrepareNames(lt::add_torrent_params& params, bool appendsSuffix, Layout layout)
{
    if (!params.ti)
        return;
    auto const& files = params.ti->layout();
    auto root = std::filesystem::path(Wide(params.save_path));
    for (auto index : files.file_range())
    {
        if (files.pad_file_at(index))
            continue;
        auto name = ContentPath(files, index, layout);
        auto path = root / Wide(name);
        if (auto renamed = params.renamed_files.find(index); renamed != params.renamed_files.end())
        {
            if (renamed->second == name + ".!tt" &&
                std::filesystem::symlink_status(root / Wide(renamed->second)).type() ==
                    std::filesystem::file_type::not_found &&
                std::filesystem::symlink_status(path).type() != std::filesystem::file_type::not_found)
            {
                if (name == files.file_path(index))
                    params.renamed_files.erase(renamed);
                else
                    renamed->second = name;
            }
            continue;
        }
        if (appendsSuffix && std::filesystem::symlink_status(path).type() == std::filesystem::file_type::not_found)
        {
            params.renamed_files[index] = name + ".!tt";
        }
        else if (name != files.file_path(index))
            params.renamed_files[index] = name;
    }
}

bool Engine::State::ApplyNames(lt::torrent_handle const& handle, lt::add_torrent_params const& prepared,
    lt::renamed_files const& current, std::set<lt::file_index_t>& pending)
{
    auto const& files = prepared.ti->layout();
    bool changed = false;
    for (auto index : files.file_range())
    {
        auto desired = prepared.renamed_files.find(index);
        auto name = desired == prepared.renamed_files.end() ? files.file_path(index) : desired->second;
        if (current.file_path(files, index) == name)
            continue;
        pending.insert(index);
        handle.rename_file(index, name);
        changed = true;
    }
    return changed;
}

void Engine::State::ContinueNames()
{
    for (auto& [id, torrent] : torrents)
        PrepareFiles(torrent);
}

void Engine::State::FinishNames(Torrent& torrent)
{
    torrent.namePhase = NamePhase::Ready;
    if (torrent.needsRecheck)
    {
        torrent.needsRecheck = false;
        torrent.completedFiles.clear();
        if (torrent.completionPhase == CompletionPhase::Settling && torrent.facts.finalFolder.empty())
            torrent.completionPhase = CompletionPhase::Checking;
        torrent.handle.force_recheck();
        Invalidate(torrent.torrentId);
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
    if (queryingFiles)
        return;
    struct Observation
    {
        std::string torrentId;
        lt::torrent_handle handle;
        std::uint64_t generation;
        FileNames names;
        std::vector<std::int64_t> progress;
    };
    auto observations = std::make_shared<std::vector<Observation>>();
    std::vector<lt::torrent_handle> recovering;
    auto now = std::chrono::steady_clock::now();
    for (auto const& [id, torrent] : torrents)
    {
        bool recovers = torrent.namePhase == NamePhase::Recovering;
        if (!torrent.filesPending || torrent.restore || (torrent.FilesBusy() && !recovers) ||
            now < torrent.renameAt || !torrent.handle.torrent_file())
            continue;
        observations->push_back({id, torrent.handle, torrent.generation});
        if (recovers)
            recovering.push_back(torrent.handle);
        if (observations->size() == 64)
            break;
    }
    if (observations->empty())
        return;
    queryingFiles = true;
    payload.Run([this, observations, recovering]
    {
        ReleaseFiles(recovering);
        auto collected = std::make_shared<std::promise<void>>();
        auto ready = collected->get_future();
        boost::asio::post(session->get_context(), [observations, collected]
        {
            try
            {
                for (auto& observation : *observations)
                {
                    if (!observation.handle.is_valid())
                        continue;
                    auto status = observation.handle.status(
                        lt::torrent_handle::query_torrent_file | lt::torrent_handle::query_renamed_files);
                    observation.names = {status.torrent_file.lock(), std::move(status.renamed_files)};
                    if (observation.names.metadata)
                        observation.progress = observation.handle.file_progress(lt::torrent_handle::piece_granularity);
                }
                collected->set_value();
            }
            catch (...)
            {
                collected->set_exception(std::current_exception());
            }
        });
        ready.get();
    }, [this, observations](StorageOutcome outcome)
    {
        queryingFiles = false;
        for (auto& observation : *observations)
        {
            auto found = torrents.find(observation.torrentId);
            if (found == torrents.end() || found->second.handle != observation.handle)
                continue;
            auto& torrent = found->second;
            if (!outcome.succeeded)
            {
                torrent.renameAt = std::chrono::steady_clock::now() + std::chrono::seconds(30);
                log.Write("files", torrent.torrentId, outcome.detail);
                continue;
            }
            if (torrent.generation != observation.generation ||
                (torrent.FilesBusy() && torrent.namePhase != NamePhase::Recovering) || !observation.names.metadata)
                continue;
            torrent.names = std::move(observation.names);
            CompleteFiles(torrent, observation.progress);
            if (torrent.namePhase == NamePhase::Recovering)
                torrent.namePhase = NamePhase::Pending;
        }
    });
}

void Engine::State::CompleteFiles(Torrent& torrent, lt::span<std::int64_t const> progress)
{
    if (!torrent.names)
        return;
    auto const& files = torrent.names->metadata->layout();
    if (progress.size() < files.num_files())
        return;
    for (auto index : files.file_range())
    {
        if (torrent.names->mappings.file_path(files, index) == ContentPath(files, index, torrent.facts.layout) + ".!tt" &&
            progress[static_cast<int>(index)] >= files.file_size(index))
            torrent.completedFiles.insert(index);
    }
    torrent.filesPending = false;
}

void Engine::State::FinishFiles(Torrent& torrent, std::optional<lt::file_index_t> after)
{
    if (shuttingDown || torrent.deleted || torrent.filesPending || torrent.namePhase != NamePhase::Ready || FilesBusy() || !additions.empty() ||
        !changes.IsIdle() ||
        std::chrono::steady_clock::now() < torrent.renameAt || torrent.completedFiles.empty())
        return;
    if (!NamesReady())
        return;
    auto const& files = torrent.names->metadata->layout();
    auto const& renames = torrent.names->mappings;
    for (auto next = after ? torrent.completedFiles.upper_bound(*after) : torrent.completedFiles.begin();
        next != torrent.completedFiles.end(); ++next)
    {
        auto index = *next;
        if (torrent.renaming.contains(index))
            continue;
        auto name = ContentPath(files, index, torrent.facts.layout);
        auto actual = renames.file_path(files, index);
        if (actual != name + ".!tt")
            continue;
        auto path = FullPath(std::filesystem::path(Wide(torrent.facts.savePath)) / Wide(actual));
        auto target = FullPath(std::filesystem::path(Wide(torrent.facts.savePath)) / Wide(name));
        Rename operation;
        operation.source = path;
        operation.target = target;
        operation.owners.push_back({torrent.torrentId, index, name});
        bool ready = true;
        for (auto const& [id, outside] : torrents)
        {
            if (id == torrent.torrentId)
                continue;
            if (outside.restore)
            {
                auto paths = FilePaths(outside, {}, false);
                ready &= !Overlaps(torrent.Hashes(), outside.Hashes()) &&
                    !std::binary_search(paths.begin(), paths.end(), path, PathBefore);
                continue;
            }
            if (!outside.names)
                continue;
            auto const& other = outside.names->metadata;
            auto const& mappings = outside.names->mappings;
            for (auto file : other->layout().file_range())
            {
                auto full = FullPath(Wide(mappings.file_path(other->layout(), file, outside.facts.savePath)));
                if (other->layout().pad_file_at(file) || !SamePath(full, path))
                    continue;
                ready &= outside.renaming.empty() && outside.namePhase != NamePhase::Preparing;
                auto relative = target.lexically_relative(Wide(outside.facts.savePath));
                operation.owners.push_back({id, file, Utf8((relative.empty() ? target : relative).wstring())});
            }
        }
        if (ready)
        {
            rename = std::move(operation);
            for (auto const& owner : rename->owners)
            {
                auto& member = torrents.at(owner.torrentId);
                member.moving = true;
                member.ApplyIntent();
            }
            ContinueRename();
            return;
        }
    }
}

// Moves a finished torrent's settled files to its final folder, and otherwise
// sends the Completed notice that is due.
void Engine::State::FinishDownload(Torrent& torrent)
{
    // A move that waits for another torrent's metadata tries again; other
    // move problems wait for the person.
    bool waits = torrent.moveError && !torrent.MoveBlocked();
    // A torrent that downloads again has no move to wait for.
    if (waits && !torrent.status.is_finished)
        torrent.moveError.reset();
    if (shuttingDown || torrent.deleted || torrent.restore || torrent.filesPending || torrent.completionPending ||
        torrent.completionPhase == CompletionPhase::Downloading ||
        torrent.completionPhase == CompletionPhase::Flushing ||
        torrent.completionPhase == CompletionPhase::Checking ||
        !torrent.status.is_finished || torrent.namePhase != NamePhase::Ready || torrent.FilesBusy() ||
        !torrent.completedFiles.empty() || !torrent.facts.moveDestination.empty())
        return;
    if (!torrent.facts.finalFolder.empty())
    {
        if (torrent.MoveBlocked() || FilesBusy() || !additions.empty() || !changes.IsIdle())
            return;
        auto id = torrent.torrentId;
        changes.Queue([this, id]
        {
            auto found = torrents.find(id);
            if (found == torrents.end() || found->second.deleted)
                return;
            StartMove({id}, found->second.facts.finalFolder, false, [this, id](Outcome outcome)
            {
                if (!outcome.error || *outcome.error == ErrorCode::FilesBusy)
                    return;
                auto& torrent = torrents.at(id);
                bool shown = torrent.moveError && torrent.moveError->refusal == outcome.error;
                torrent.moveError = Problem{ProblemKind::MoveFailed, outcome.detail, outcome.error};
                // A wait that now names another torrent is the same problem,
                // so the status update does not report it again.
                torrent.notifiedError = outcome.detail;
                if (!shown)
                {
                    Notify(NoticeKind::Error, torrent, outcome.detail, torrent.moveError->Code());
                }
            });
        });
        return;
    }
    if (torrent.completionPhase == CompletionPhase::Settling && settings.rechecksFinished)
    {
        torrent.completionPhase = CompletionPhase::Checking;
        Verify({torrent.torrentId}, [this, id = torrent.torrentId](Json result)
        {
            if (!result.at("ok").get<bool>())
            {
                torrents.at(id).completionPhase = CompletionPhase::Settling;
                log.Write("verify", id, "completion_failed");
            }
        });
        return;
    }
    if (torrent.completionPhase == CompletionPhase::Settling || torrent.completionPhase == CompletionPhase::Checked)
    {
        torrent.completionPhase = CompletionPhase::Idle;
        Notify(NoticeKind::Completed, torrent);
    }
}

void Engine::State::ContinueRename()
{
    if (!rename || rename->phase == RenamePhase::Moving || rename->phase == RenamePhase::Observing)
        return;
    auto const& owner = rename->owners.at(rename->current);
    auto& torrent = torrents.at(owner.torrentId);
    if (std::chrono::steady_clock::now() < torrent.renameAt)
        return;
    if (rename->phase == RenamePhase::Recovering)
    {
        rename->phase = RenamePhase::Observing;
        auto names = std::make_shared<FileNames>();
        payload.Run([this, handle = torrent.handle, names]
        {
            ReleaseFiles({handle});
            names->metadata = handle.torrent_file();
            names->mappings = handle.get_renamed_files();
        }, [this, names, generation = torrent.generation](StorageOutcome outcome)
        {
            auto const& owner = rename->owners.at(rename->current);
            auto& torrent = torrents.at(owner.torrentId);
            if (!outcome.succeeded)
            {
                torrent.renameAt = std::chrono::steady_clock::now() + std::chrono::seconds(30);
                log.Write("rename", owner.torrentId, outcome.detail);
                rename->phase = RenamePhase::Recovering;
                return;
            }
            // A late rename alert can update the names after this worker read them.
            if (torrent.generation != generation)
            {
                rename->phase = RenamePhase::Recovering;
                ContinueRename();
                return;
            }
            torrent.names = std::move(*names);
            auto const& current = *torrent.names;
            auto actual = FullPath(Wide(current.mappings.file_path(
                current.metadata->layout(), owner.index, torrent.facts.savePath)));
            torrent.renaming.erase(owner.index);
            Invalidate(owner.torrentId);
            if (SamePath(actual, rename->target))
            {
                torrent.completedFiles.erase(owner.index);
                torrent.unsaved = true;
                if (++rename->current == rename->owners.size())
                {
                    EndRename();
                    return;
                }
            }
            rename->phase = RenamePhase::Naming;
            ContinueRename();
        });
        return;
    }
    if (torrent.renaming.contains(owner.index))
        return;
    if (rename->phase == RenamePhase::Ready)
    {
        std::vector<lt::torrent_handle> handles;
        std::vector<std::pair<lt::torrent_handle, lt::file_index_t>> files;
        for (auto const& member : rename->owners)
        {
            auto handle = torrents.at(member.torrentId).handle;
            files.emplace_back(handle, member.index);
            if (std::find(handles.begin(), handles.end(), handle) == handles.end())
                handles.push_back(handle);
        }
        auto complete = std::make_shared<std::vector<bool>>(files.size(), false);
        rename->phase = RenamePhase::Moving;
        payload.Run([this, handles, files, complete, source = rename->source, target = rename->target]
        {
            ReleaseFiles(handles);
            for (auto const& handle : handles)
            {
                auto status = handle.status(lt::torrent_handle::query_save_path |
                    lt::torrent_handle::query_torrent_file | lt::torrent_handle::query_renamed_files);
                auto metadata = status.torrent_file.lock();
                if (!metadata)
                    return;
                auto progress = handle.file_progress(lt::torrent_handle::piece_granularity);
                for (std::size_t item = 0; item < files.size(); ++item)
                {
                    auto const& [owner, index] = files[item];
                    if (owner != handle)
                        continue;
                    auto path = FullPath(Wide(status.renamed_files.file_path(metadata->layout(), index, status.save_path)));
                    (*complete)[item] = static_cast<std::size_t>(static_cast<int>(index)) < progress.size() &&
                        progress[static_cast<int>(index)] >= metadata->layout().file_size(index) && SamePath(path, source);
                }
            }
            if (std::find(complete->begin(), complete->end(), false) != complete->end())
                return;
            if (!MoveFileExW(source.c_str(), target.c_str(), 0))
                throw std::runtime_error(std::to_string(GetLastError()));
        }, [this, complete](StorageOutcome outcome)
        {
            if (!outcome.succeeded)
            {
                auto const& id = rename->owners.front().torrentId;
                torrents.at(id).renameAt = std::chrono::steady_clock::now() + std::chrono::seconds(30);
                log.Write("rename", id, outcome.detail);
                EndRename();
                return;
            }
            if (std::find(complete->begin(), complete->end(), false) != complete->end())
            {
                auto source = rename->owners.front();
                for (std::size_t item = 0; item < complete->size(); ++item)
                {
                    if ((*complete)[item])
                        continue;
                    auto const& owner = rename->owners[item];
                    auto& torrent = torrents.at(owner.torrentId);
                    torrent.completedFiles.erase(owner.index);
                    torrent.filesPending = true;
                }
                EndRename();
                FinishFiles(torrents.at(source.torrentId), source.index);
                return;
            }
            rename->phase = RenamePhase::Naming;
            ContinueRename();
        });
        return;
    }
    torrent.renaming.insert(owner.index);
    torrent.handle.rename_file(owner.index, owner.name);
}

void Engine::State::EndRename()
{
    auto owners = std::move(rename->owners);
    rename.reset();
    for (auto const& owner : owners)
    {
        auto& torrent = torrents.at(owner.torrentId);
        torrent.moving = false;
        torrent.ApplyIntent();
    }
}

void Engine::State::On(lt::file_completed_alert const& alert)
{
    auto torrent = Find(alert.handle);
    if (!torrent)
        return;
    auto metadata = alert.handle.torrent_file();
    if (!metadata || metadata->layout().pad_file_at(alert.index))
        return;
    if (torrent->names)
    {
        auto const& files = torrent->names->metadata->layout();
        auto name = torrent->names->mappings.file_path(files, alert.index);
        if (name == ContentPath(files, alert.index, torrent->facts.layout) + ".!tt")
            torrent->completedFiles.insert(alert.index);
    }
    else
    {
        Invalidate(torrent->torrentId);
        torrent->filesPending = true;
    }
    // File completion is also posted by checking; the ordered state alerts
    // identify its cause even when the latest sampled status is already seeding.
    if (torrent->transferState != lt::torrent_status::downloading)
    {
        FinishFiles(*torrent);
        return;
    }
    torrent->receivedPayload = true;
    torrent->renaming.insert(alert.index);
    payload.Run([handle = alert.handle, index = alert.index]
    {
        auto status = handle.status(lt::torrent_handle::query_save_path |
            lt::torrent_handle::query_torrent_file | lt::torrent_handle::query_renamed_files);
        auto metadata = status.torrent_file.lock();
        if (!metadata)
            return;
        auto path = FullPath(Wide(status.renamed_files.file_path(metadata->layout(), index, status.save_path)));
        auto base = CreateFileW(path.c_str(), FILE_READ_ATTRIBUTES, FILE_SHARE_READ | FILE_SHARE_WRITE,
            nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (base == INVALID_HANDLE_VALUE)
            throw std::runtime_error(std::to_string(GetLastError()));
        auto stream = path.wstring() + L":Zone.Identifier";
        auto file = CreateFileW(stream.c_str(), GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        auto error = file == INVALID_HANDLE_VALUE ? GetLastError() : ERROR_SUCCESS;
        if (file != INVALID_HANDLE_VALUE)
        {
            constexpr char zone[] = "[ZoneTransfer]\r\nZoneId=3\r\n";
            DWORD written = 0;
            if (!WriteFile(file, zone, sizeof(zone) - 1, &written, nullptr))
                error = GetLastError();
            else if (written != sizeof(zone) - 1)
                error = ERROR_WRITE_FAULT;
            CloseHandle(file);
        }
        CloseHandle(base);
        if (error != ERROR_SUCCESS)
            throw std::runtime_error(std::to_string(error));
    }, [this, id = torrent->torrentId, index = alert.index](StorageOutcome outcome)
    {
        if (!outcome.succeeded)
            log.Write("mark_of_the_web", id, outcome.detail);
        if (auto found = torrents.find(id); found != torrents.end())
        {
            found->second.renaming.erase(index);
            FinishFiles(found->second);
        }
    });
}

void Engine::State::On(lt::file_renamed_alert const& alert)
{
    if (auto torrent = Find(alert.handle))
    {
        if (torrent->names)
            torrent->names->mappings.rename_file(torrent->names->metadata->layout(), alert.index, alert.new_name());
        bool expected = torrent->renaming.erase(alert.index) != 0;
        torrent->completedFiles.erase(alert.index);
        torrent->unsaved = true;
        // Open torrent and Open folder must not use the old name.
        Invalidate(torrent->torrentId);
        if (rename && rename->phase == RenamePhase::Naming &&
            rename->owners.at(rename->current).torrentId == torrent->torrentId &&
            rename->owners.at(rename->current).index == alert.index)
        {
            if (++rename->current == rename->owners.size())
                EndRename();
            else
                ContinueRename();
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
    if (auto torrent = Find(alert.handle))
    {
        bool expected = torrent->renaming.erase(alert.index) != 0;
        if (rename && rename->phase == RenamePhase::Naming &&
            rename->owners.at(rename->current).torrentId == torrent->torrentId &&
            rename->owners.at(rename->current).index == alert.index)
            rename->phase = RenamePhase::Recovering;
        if (expected && torrent->namePhase == NamePhase::Preparing)
            RecoverNames(*torrent);
        torrent->renameAt = std::chrono::steady_clock::now() + std::chrono::seconds(30);
        log.Write("rename", torrent->torrentId, alert.error.message());
        return;
    }
    for (auto const& [id, addition] : additions)
    {
        if (addition.handle == alert.handle && addition.phase == AdditionPhase::Naming &&
            addition.renaming.contains(alert.index))
        {
            Abandon(id, {ErrorCode::AddFailed, alert.error.message()});
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
    std::string const& destination, bool logical) const
{
    auto at = [&torrent, logical](std::string const& folder)
    {
        std::vector<std::filesystem::path> files;
        for (auto const& path : torrent.Paths(logical))
            files.push_back(FullPath(std::filesystem::path(Wide(folder)) / path));
        std::sort(files.begin(), files.end(), PathBefore);
        files.erase(std::unique(files.begin(), files.end(), SamePath), files.end());
        return files;
    };
    if (!destination.empty())
        return at(destination);
    auto files = at(torrent.facts.savePath);
    if (!torrent.facts.moveDestination.empty())
    {
        auto staged = at(torrent.facts.moveDestination);
        files.insert(files.end(), staged.begin(), staged.end());
        std::sort(files.begin(), files.end(), PathBefore);
        files.erase(std::unique(files.begin(), files.end(), SamePath), files.end());
    }
    return files;
}

bool Engine::State::NamesReady()
{
    bool ready = true;
    for (auto& [id, torrent] : torrents)
    {
        if (torrent.namePhase == NamePhase::Recovering)
        {
            ready = false;
            continue;
        }
        if (!torrent.restore && torrent.handle.torrent_file() && !torrent.names)
        {
            torrent.filesPending = true;
            ready = false;
        }
    }
    return ready;
}

Outcome Engine::State::FilesReady(std::vector<std::string> const& ids)
{
    if (FilesBusy() || !additions.empty())
    {
        return {ErrorCode::FilesBusy};
    }
    for (auto const& [id, torrent] : torrents)
    {
        if (torrent.restore || !torrent.handle.torrent_file())
        {
            return {ErrorCode::MetadataUnavailable, torrent.Name()};
        }
        if (torrent.FilesBusy() ||
            (Contains(ids, id) && torrent.priorityReply))
        {
            return {ErrorCode::FilesBusy};
        }
    }
    return NamesReady() ? Outcome{} : Outcome{ErrorCode::FilesBusy};
}

Engine::State::Scope Engine::State::FileScope(std::vector<std::string> const& ids) const
{
    Scope scope;
    std::vector<std::pair<std::filesystem::path, std::filesystem::path>> names;
    auto keys = [this](Torrent const& torrent)
    {
        auto paths = FilePaths(torrent);
        auto physical = FilePaths(torrent, {}, false);
        paths.insert(paths.end(), physical.begin(), physical.end());
        std::sort(paths.begin(), paths.end(), PathBefore);
        paths.erase(std::unique(paths.begin(), paths.end(), SamePath), paths.end());
        return paths;
    };
    for (auto const& id : ids)
    {
        auto const& torrent = torrents.at(id);
        // Not at an interrupted move's destination: what the move did not
        // finish there is unknown, and it can be someone else's file.
        auto files = FilePaths(torrent, torrent.facts.savePath, false);
        scope.files.insert(scope.files.end(), files.begin(), files.end());
        auto held = keys(torrent);
        scope.holds.insert(scope.holds.end(), held.begin(), held.end());
        auto physical = torrent.Paths();
        auto logical = torrent.Paths(true);
        for (auto const& folder : {torrent.facts.savePath, torrent.facts.moveDestination})
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
        for (auto const& [id, torrent] : torrents)
        {
            if (Contains(group, id))
            {
                continue;
            }
            auto files = keys(torrent);
            if (torrent.restore && !torrent.restore->ti)
            {
                for (auto const& selected : ids)
                {
                    if (Overlaps(torrent.Hashes(), torrents.at(selected).Hashes()))
                    {
                        auto held = keys(torrents.at(selected));
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
                continue;
            }
            group.push_back(id);
            // A deleted torrent still holds its files until it is removed, but
            // it is out of the list, so nobody is told it shares them.
            if (!torrent.deleted)
            {
                scope.shared.push_back(id);
            }
            used.insert(files.begin(), files.end());
            paths.insert(paths.end(), files.begin(), files.end());
            std::sort(paths.begin(), paths.end(), PathBefore);
        }
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
                {"save_path", torrent.facts.savePath}, {"folder", torrent.Folder()}});
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
    if (!metadata || destination.empty() || !FilesBusy())
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
    if (rename && holds({rename->source, rename->target}))
    {
        for (auto const& owner : rename->owners)
            names.push_back(torrents.at(owner.torrentId).Name());
    }
    if (move && holds(move->holds))
    {
        for (auto const& id : move->ids)
            names.push_back(torrents.at(id).Name());
    }
    for (auto const& deleting : deletions)
    {
        if (holds(deleting.holds))
            names.push_back(deleting.names);
    }
    return names;
}

void Engine::State::ContinueDeletion()
{
    // libtorrent destroys a removed torrent only after it releases the files,
    // so a deletion whose alerts were lost waits for that instead.
    for (auto& deletion : deletions)
    {
        if (deletion.phase == DeletionPhase::Unknown)
        {
            std::erase_if(deletion.waiting, [](auto const& handle) { return !handle.is_valid(); });
        }
    }
    for (auto deletion = deletions.begin(); deletion != deletions.end(); ++deletion)
    {
        if (deletion->waiting.empty() &&
            (deletion->phase == DeletionPhase::Waiting || deletion->phase == DeletionPhase::Unknown))
        {
            // Another removed handle can still flush writes to a shared file.
            bool held = std::any_of(deletions.begin(), deletions.end(), [&deletion](auto const& other)
            {
                return (other.phase == DeletionPhase::Saving || !other.waiting.empty()) &&
                    std::any_of(deletion->files.begin(), deletion->files.end(), [&other](auto const& file)
                    {
                        return std::binary_search(other.holds.begin(), other.holds.end(), file, PathBefore);
                    });
            });
            if (!held)
            {
                Delete(deletion);
            }
        }
    }
}

void Engine::State::Delete(std::list<Deletion>::iterator deletion)
{
    auto deleting = *deletion;
    deletion->phase = DeletionPhase::Deleting;
    payload.Run([deleting]
    {
        std::string failure;
        if (deleting.mode == DeletionMode::Recycle)
        {
            auto initialized = CoInitializeEx(nullptr, COINIT_APARTMENTTHREADED);
            IFileOperation* operation = nullptr;
            auto result = FAILED(initialized) ? initialized : CoCreateInstance(CLSID_FileOperation, nullptr,
                CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&operation));
            if (SUCCEEDED(result))
                result = operation->SetOperationFlags(FOFX_RECYCLEONDELETE | FOFX_EARLYFAILURE |
                    FOF_NOERRORUI | FOF_SILENT | FOF_NOCONFIRMATION | FOF_NO_CONNECTED_ELEMENTS);
            for (auto const& file : deleting.files)
            {
                if (FAILED(result))
                    break;
                std::error_code error;
                auto type = std::filesystem::symlink_status(file, error).type();
                if (type == std::filesystem::file_type::not_found)
                    continue;
                if (error || type == std::filesystem::file_type::directory)
                {
                    result = E_FAIL;
                    break;
                }
                IShellItem* item = nullptr;
                result = SHCreateItemFromParsingName(file.c_str(), nullptr, IID_PPV_ARGS(&item));
                if (SUCCEEDED(result))
                    result = operation->DeleteItem(item, nullptr);
                if (item)
                    item->Release();
            }
            if (SUCCEEDED(result))
                result = operation->PerformOperations();
            BOOL aborted = FALSE;
            if (SUCCEEDED(result))
                result = operation->GetAnyOperationsAborted(&aborted);
            if (operation)
                operation->Release();
            if (SUCCEEDED(initialized))
                CoUninitialize();
            if (FAILED(result) || aborted)
                throw std::runtime_error("recycle_failed");
        }
        else
        {
            for (auto const& file : deleting.files)
            {
                std::error_code error;
                std::filesystem::remove(file, error);
                if (error && failure.empty())
                    failure = Utf8(file.wstring()) + ": " + error.message();
            }
        }
        for (auto const& file : deleting.files)
        {
            auto parent = file.parent_path();
            for (auto const& root : deleting.roots)
            {
                auto relative = parent.lexically_relative(root);
                if (relative.empty() || *relative.begin() == L"..")
                {
                    continue;
                }
                for (auto folder = parent; !SamePath(folder, root); folder = folder.parent_path())
                {
                    std::error_code ignored;
                    if (!std::filesystem::remove(folder, ignored))
                    {
                        break;
                    }
                }
            }
        }
        if (!failure.empty())
        {
            throw std::runtime_error(failure);
        }
    }, [this, deletion](StorageOutcome outcome)
    {
        if (!outcome.succeeded)
        {
            log.Write("delete", "", "delete_failed");
            Notify(NoticeKind::DeleteFailed, deletion->names, outcome.detail);
        }
        deletions.erase(deletion);
    });
}

void Engine::State::On(lt::torrent_deleted_alert const& alert)
{
    for (auto& deletion : deletions)
    {
        std::erase(deletion.waiting, alert.handle);
    }
    ContinueDeletion();
}

void Engine::State::On(lt::torrent_delete_failed_alert const& alert)
{
    for (auto& deletion : deletions)
    {
        if (std::erase(deletion.waiting, alert.handle))
        {
            log.Write("delete", "", "partfile_delete_failed");
            Notify(NoticeKind::DeleteFailed, deletion.names, alert.error.message());
        }
    }
    ContinueDeletion();
}

// After libtorrent dropped alerts, each file operation that waited for one
// waits instead for a sign that comes after the drop.
void Engine::State::RecoverFiles()
{
    if (rename && rename->phase == RenamePhase::Naming)
    {
        rename->phase = RenamePhase::Recovering;
        ContinueRename();
    }
    for (auto& [id, torrent] : torrents)
    {
        if (torrent.namePhase == NamePhase::Preparing && !torrent.renaming.empty())
        {
            RecoverNames(torrent);
        }
        else
        {
            Invalidate(id);
            torrent.filesPending = true;
        }
    }
    RecoverMove();
    for (auto& deletion : deletions)
    {
        if (!deletion.waiting.empty())
        {
            deletion.phase = DeletionPhase::Unknown;
        }
    }
    ContinueDeletion();
}
}
