#include "Engine/State.h"
#include <Windows.h>
#include <libtorrent/torrent_info.hpp>
#include <algorithm>
#include <set>

namespace tt
{
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

// Compares two UTF-8 folders, such as save paths, as full paths.
bool Engine::State::SameFolder(std::string const& left, std::string const& right)
{
    return SamePath(FullPath(Wide(left)), FullPath(Wide(right)));
}

bool Engine::State::FilesBusy() const
{
    return relocation.has_value() || deletion.has_value() || rename.has_value();
}

void Engine::State::PrepareFiles(Torrent& torrent)
{
    if (torrent.namesReady || torrent.preparingNames || !torrent.renaming.empty() || FilesBusy() ||
        std::chrono::steady_clock::now() < torrent.renameAt)
        return;
    auto metadata = torrent.handle.torrent_file();
    if (!metadata)
        return;
    constexpr std::ptrdiff_t limit = 8;
    auto pending = std::count_if(torrents.begin(), torrents.end(),
        [](auto const& entry) { return entry.second.preparingNames; });
    if (pending >= limit)
        return;
    torrent.preparingNames = true;
    torrent.needsRecheck |= torrent.facts.verifyFiles;
    torrent.ApplyIntent();
    auto prepared = std::make_shared<lt::add_torrent_params>();
    prepared->ti = metadata;
    prepared->save_path = torrent.facts.savePath;
    prepared->renamed_files = torrent.handle.get_renamed_files().export_filenames(metadata->layout());
    payload.Run([prepared] { PrepareNames(*prepared); },
        [this, id = torrent.identity, prepared](StorageOutcome outcome)
    {
        auto found = torrents.find(id);
        if (found == torrents.end())
            return;
        auto& torrent = found->second;
        if (!outcome.succeeded)
        {
            torrent.preparingNames = false;
            torrent.renameAt = std::chrono::steady_clock::now() + std::chrono::seconds(30);
            diagnostics.Write("rename", id, outcome.detail);
            ContinueNames();
            return;
        }
        auto current = torrent.handle.get_renamed_files();
        for (auto index : prepared->ti->layout().file_range())
        {
            auto desired = prepared->renamed_files.find(index);
            auto name = desired == prepared->renamed_files.end() ? prepared->ti->layout().file_path(index) : desired->second;
            if (current.file_path(prepared->ti->layout(), index) != name)
            {
                torrent.needsRecheck = true;
                torrent.renaming.insert(index);
                torrent.handle.rename_file(index, name);
            }
        }
        if (torrent.renaming.empty())
            FinishNames(torrent);
    });
}

void Engine::State::ContinueNames()
{
    for (auto& [id, torrent] : torrents)
        PrepareFiles(torrent);
}

void Engine::State::FinishNames(Torrent& torrent)
{
    torrent.preparingNames = false;
    torrent.namesReady = true;
    if (torrent.needsRecheck)
    {
        torrent.needsRecheck = false;
        torrent.completedFiles.clear();
        torrent.handle.force_recheck();
    }
    torrent.ApplyIntent();
    ContinueNames();
}

void Engine::State::CompleteFiles(Torrent& torrent)
{
    auto metadata = torrent.handle.torrent_file();
    if (!metadata)
        return;
    auto progress = torrent.handle.file_progress(lt::torrent_handle::piece_granularity);
    auto mappings = torrent.handle.get_renamed_files();
    auto const& files = metadata->layout();
    for (auto index : files.file_range())
    {
        if (mappings.file_path(files, index) == files.file_path(index) + ".!tt" &&
            progress[static_cast<int>(index)] >= files.file_size(index))
            torrent.completedFiles.insert(index);
    }
}

void Engine::State::FinishFiles(Torrent& torrent)
{
    if (stopping || !torrent.namesReady || FilesBusy() || !additions.empty() ||
        std::chrono::steady_clock::now() < torrent.renameAt || torrent.completedFiles.empty())
        return;
    auto metadata = torrent.handle.torrent_file();
    auto const& files = metadata->layout();
    auto renames = torrent.handle.get_renamed_files();
    auto progress = torrent.handle.file_progress(lt::torrent_handle::piece_granularity);
    for (auto index : torrent.completedFiles)
    {
        if (torrent.renaming.contains(index))
            continue;
        if (progress[static_cast<int>(index)] < files.file_size(index))
            continue;
        auto name = files.file_path(index);
        auto actual = renames.file_path(files, index);
        if (actual != name + ".!tt")
            continue;
        auto path = FullPath(std::filesystem::path(Wide(torrent.facts.savePath)) / Wide(actual));
        auto target = FullPath(std::filesystem::path(Wide(torrent.facts.savePath)) / Wide(name));
        Rename operation;
        operation.source = path;
        operation.target = target;
        operation.owners.push_back({torrent.identity, index, name});
        bool ready = true;
        for (auto const& [id, outside] : torrents)
        {
            if (id == torrent.identity)
                continue;
            auto other = outside.handle.torrent_file();
            if (!other)
                continue;
            auto mappings = outside.handle.get_renamed_files();
            auto progress = outside.handle.file_progress(lt::torrent_handle::piece_granularity);
            for (auto file : other->layout().file_range())
            {
                auto full = FullPath(Wide(mappings.file_path(other->layout(), file, outside.facts.savePath)));
                if (other->layout().pad_file_at(file) || !SamePath(full, path))
                    continue;
                ready &= progress[static_cast<int>(file)] >= other->layout().file_size(file);
                ready &= outside.renaming.empty() && !outside.preparingNames;
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
                if (!bool(member.handle.flags() & lt::torrent_flags::paused) && !session->is_paused() &&
                    std::find(rename->waiting.begin(), rename->waiting.end(), member.handle) == rename->waiting.end())
                    rename->waiting.push_back(member.handle);
                member.ApplyIntent();
            }
            ContinueRename();
            return;
        }
    }
}

void Engine::State::ContinueRename()
{
    if (!rename || !rename->waiting.empty() || rename->phase == RenamePhase::Moving)
        return;
    auto const& owner = rename->owners.at(rename->current);
    auto& torrent = torrents.at(owner.torrentId);
    if (rename->phase == RenamePhase::Recovering)
    {
        auto metadata = torrent.handle.torrent_file();
        auto mappings = torrent.handle.get_renamed_files();
        auto actual = FullPath(Wide(mappings.file_path(metadata->layout(), owner.index, torrent.facts.savePath)));
        torrent.renaming.erase(owner.index);
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
        return;
    }
    if (std::chrono::steady_clock::now() < torrent.renameAt || torrent.renaming.contains(owner.index))
        return;
    if (rename->phase == RenamePhase::Waiting)
    {
        rename->phase = RenamePhase::Flushing;
        for (auto const& member : rename->owners)
        {
            auto handle = torrents.at(member.torrentId).handle;
            if (std::find(rename->waiting.begin(), rename->waiting.end(), handle) == rename->waiting.end())
            {
                rename->waiting.push_back(handle);
                handle.flush_cache();
            }
        }
        return;
    }
    if (rename->phase == RenamePhase::Flushing)
    {
        rename->phase = RenamePhase::Moving;
        payload.Run([source = rename->source, target = rename->target]
        {
            if (!MoveFileExW(source.c_str(), target.c_str(), 0))
                throw std::runtime_error(std::to_string(GetLastError()));
        }, [this](StorageOutcome outcome)
        {
            if (!outcome.succeeded)
            {
                auto const& id = rename->owners.front().torrentId;
                torrents.at(id).renameAt = std::chrono::steady_clock::now() + std::chrono::seconds(30);
                diagnostics.Write("rename", id, outcome.detail);
                EndRename();
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
    auto renames = alert.handle.get_renamed_files();
    auto name = renames.file_path(metadata->layout(), alert.index);
    if (name == metadata->layout().file_path(alert.index) + ".!tt")
        torrent->completedFiles.insert(alert.index);
    // File completion is also posted by checking; the ordered state alerts
    // identify its cause even when the latest sampled status is already seeding.
    if (torrent->fileState != lt::torrent_status::downloading)
    {
        FinishFiles(*torrent);
        return;
    }
    torrent->renaming.insert(alert.index);
    auto path = FullPath(std::filesystem::path(Wide(torrent->facts.savePath)) / Wide(name));
    payload.Run([path]
    {
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
    }, [this, id = torrent->identity, index = alert.index](StorageOutcome outcome)
    {
        if (!outcome.succeeded)
            diagnostics.Write("mark_of_the_web", id, outcome.detail);
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
        torrent->renaming.erase(alert.index);
        torrent->completedFiles.erase(alert.index);
        torrent->unsaved = true;
        if (rename && rename->owners.at(rename->current).torrentId == torrent->identity &&
            rename->owners.at(rename->current).index == alert.index)
        {
            if (++rename->current == rename->owners.size())
                EndRename();
            else
                ContinueRename();
        }
        if (torrent->preparingNames && torrent->renaming.empty())
            FinishNames(*torrent);
        return;
    }
    for (auto& [id, addition] : additions)
    {
        if (addition.handle == alert.handle && addition.phase == AdditionPhase::Naming)
        {
            addition.renaming.erase(alert.index);
            if (addition.renaming.empty())
                SaveAddition(id, addition.handle);
            return;
        }
    }
}

void Engine::State::On(lt::file_rename_failed_alert const& alert)
{
    if (auto torrent = Find(alert.handle))
    {
        torrent->renaming.erase(alert.index);
        torrent->preparingNames = false;
        torrent->renameAt = std::chrono::steady_clock::now() + std::chrono::seconds(30);
        diagnostics.Write("rename", torrent->identity, alert.error.message());
        return;
    }
    for (auto const& [id, addition] : additions)
    {
        if (addition.handle == alert.handle && addition.phase == AdditionPhase::Naming)
        {
            Abandon(id, {ErrorCode::AddFailed, alert.error.message()});
            return;
        }
    }
}

// The full paths of the content's files at the folder, sorted by PathBefore.
std::vector<std::filesystem::path> Engine::State::FilePaths(lt::torrent_info const& metadata,
    std::string const& folder)
{
    std::filesystem::path root = Wide(folder);
    std::vector<std::filesystem::path> files;
    for (auto const& path : Paths(metadata))
    {
        files.push_back(FullPath(root / path));
    }
    std::sort(files.begin(), files.end(), PathBefore);
    files.erase(std::unique(files.begin(), files.end(), SamePath), files.end());
    return files;
}

std::vector<std::filesystem::path> Engine::State::FilePaths(Torrent const& torrent,
    std::string const& destination, bool logical) const
{
    auto metadata = torrent.handle.torrent_file();
    if (!metadata)
    {
        return {};
    }
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

bool Engine::State::FilesReady(std::vector<std::string> const& ids, Reply const& reply) const
{
    if (FilesBusy() || !additions.empty())
    {
        reply(Failure(ErrorCode::FilesBusy));
        return false;
    }
    for (auto const& [id, torrent] : torrents)
    {
        if (!torrent.handle.torrent_file())
        {
            reply(Failure(ErrorCode::MetadataUnavailable));
            return false;
        }
        if (torrent.preparingNames || !torrent.renaming.empty() ||
            (Contains(ids, id) && torrent.priorityReply))
        {
            reply(Failure(ErrorCode::FilesBusy));
            return false;
        }
    }
    return true;
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
        auto files = FilePaths(torrent, {}, false);
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
            scope.shared.push_back(id);
            used.insert(files.begin(), files.end());
            paths.insert(paths.end(), files.begin(), files.end());
            std::sort(paths.begin(), paths.end(), PathBefore);
        }
    }
    // Only files in the original command count as kept by a deletion.
    for (auto const& file : scope.files)
    {
        if (std::any_of(names.begin(), names.end(), [&file, &used](auto const& pair)
            { return SamePath(file, pair.first) && (used.contains(pair.first) || used.contains(pair.second)); }))
        {
            scope.kept.push_back(file);
        }
    }
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
            list.push_back(Json{{"torrent_id", torrent.identity}, {"name", torrent.Name()},
                {"save_path", torrent.facts.savePath}, {"folder", torrent.Folder()}});
        }
        return list;
    };
    return {{"torrents", describe(ids)}, {"shared", describe(scope.shared)}, {"kept_files", scope.kept.size()}};
}

bool Engine::State::HoldsFiles(std::shared_ptr<lt::torrent_info const> const& metadata,
    std::string const& destination) const
{
    if (!metadata || destination.empty() || !FilesBusy())
    {
        return false;
    }
    if (rename)
    {
        for (auto const& file : FilePaths(*metadata, destination))
        {
            if (SamePath(file, rename->source) || SamePath(file, rename->target))
                return true;
        }
        return false;
    }
    auto const& held = relocation ? relocation->holds : deletion->holds;
    for (auto const& file : FilePaths(*metadata, destination))
    {
        if (std::any_of(held.begin(), held.end(), [&file](auto const& other) { return SamePath(file, other); }))
            return true;
    }
    return false;
}

void Engine::State::Move(std::vector<std::string> const& ids, std::string const& destination,
    bool useExisting, Reply reply)
{
    if (!FilesReady(ids, reply))
    {
        return;
    }
    for (auto const& id : ids)
    {
        if (!useExisting && !torrents.at(id).facts.moveDestination.empty())
        {
            reply(Failure(ProblemKind::MoveInterrupted));
            return;
        }
    }
    if (!IsAbsolute(destination))
    {
        reply(Failure(ErrorCode::InvalidDestination));
        return;
    }
    if (!FileScope(ids).shared.empty())
    {
        reply(Failure(ErrorCode::SharedFiles));
        return;
    }
    std::map<std::filesystem::path, std::filesystem::path, decltype(&PathBefore)> sources(PathBefore);
    std::set<std::filesystem::path, decltype(&PathBefore)> targets(PathBefore);
    Relocation moving;
    moving.ids = ids;
    moving.destination = destination;
    moving.usesExisting = useExisting;
    for (auto const& id : ids)
    {
        auto const& torrent = torrents.at(id);
        auto physical = torrent.Paths();
        auto from = FilePaths(torrent, torrent.facts.savePath, false);
        auto to = FilePaths(torrent, destination, false);
        for (auto const& path : physical)
        {
            auto source = FullPath(std::filesystem::path(Wide(torrent.facts.savePath)) / path);
            auto target = FullPath(std::filesystem::path(Wide(destination)) / path);
            auto [known, inserted] = sources.emplace(target, source);
            if (!useExisting && !inserted && !SamePath(known->second, source))
            {
                reply(Failure(ErrorCode::DestinationConflict));
                return;
            }
        }
        moving.holds.insert(moving.holds.end(), from.begin(), from.end());
        moving.holds.insert(moving.holds.end(), to.begin(), to.end());
        auto logical = FilePaths(torrent, destination);
        auto origins = FilePaths(torrent, torrent.facts.savePath);
        moving.holds.insert(moving.holds.end(), logical.begin(), logical.end());
        moving.holds.insert(moving.holds.end(), origins.begin(), origins.end());
        auto destinations = to;
        destinations.insert(destinations.end(), logical.begin(), logical.end());
        targets.insert(destinations.begin(), destinations.end());
        for (auto const& [otherId, other] : torrents)
        {
            if (Contains(ids, otherId))
            {
                continue;
            }
            auto outside = FilePaths(other);
            auto actual = FilePaths(other, {}, false);
            outside.insert(outside.end(), actual.begin(), actual.end());
            std::sort(outside.begin(), outside.end(), PathBefore);
            if (std::any_of(destinations.begin(), destinations.end(), [&outside](auto const& path)
                { return std::binary_search(outside.begin(), outside.end(), path, PathBefore); }))
            {
                reply(Failure(ErrorCode::DestinationInUse, other.Name()));
                return;
            }
        }
    }
    auto document = Saved();
    for (auto const& id : ids)
    {
        document.torrents.at(id).moveDestination = destination;
    }
    relocation = std::move(moving);
    changes.Commit(document.ToJson(), [this, reply, targets](StorageOutcome outcome)
    {
        if (!outcome.succeeded)
        {
            relocation.reset();
            reply(Failure(ErrorCode::StorageFailed, outcome.detail));
            return;
        }
        for (auto const& id : relocation->ids)
        {
            auto& torrent = torrents.at(id);
            torrent.facts.moveDestination = relocation->destination;
            torrent.moving = true;
            torrent.moveError.reset();
            if (!bool(torrent.handle.flags() & lt::torrent_flags::paused) && !session->is_paused())
                relocation->waiting.push_back(torrent.handle);
            torrent.ApplyIntent();
        }
        auto problem = std::make_shared<std::optional<Problem>>();
        payload.Run([targets, useExisting = relocation->usesExisting, problem]
        {
            if (useExisting)
            {
                return;
            }
            for (auto const& path : targets)
            {
                auto state = std::filesystem::symlink_status(path);
                if (state.type() != std::filesystem::file_type::not_found)
                {
                    *problem = Problem{ProblemKind::DestinationExists, Utf8(path.wstring())};
                    return;
                }
            }
        }, [this, problem](StorageOutcome outcome)
        {
            if (!outcome.succeeded)
            {
                *problem = Problem{ProblemKind::MoveFailed, outcome.detail};
            }
            if (*problem)
            {
                FinishMove(torrents.at(relocation->ids.front()).handle, *problem);
                return;
            }
            if (relocation->phase == RelocationPhase::Unknown)
            {
                return;
            }
            relocation->phase = RelocationPhase::Waiting;
            ContinueMove();
        });
        reply(Success());
    });
}

void Engine::State::ContinueMove()
{
    if (!relocation || relocation->phase != RelocationPhase::Waiting || !relocation->waiting.empty())
    {
        return;
    }
    auto& torrent = torrents.at(relocation->ids.at(relocation->current));
    auto targets = FilePaths(torrent, relocation->destination, false);
    auto alreadyMoved = [&moving = *relocation](auto const& path)
    {
        return std::any_of(moving.moved.begin(), moving.moved.end(),
            [&path](auto const& other) { return SamePath(path, other); });
    };
    auto flags = lt::move_flags_t::fail_if_exist;
    if (relocation->usesExisting || std::all_of(targets.begin(), targets.end(), alreadyMoved))
        flags = lt::move_flags_t::reset_save_path;
    else if (std::any_of(targets.begin(), targets.end(), alreadyMoved))
        flags = lt::move_flags_t::dont_replace;
    relocation->phase = RelocationPhase::Moving;
    if (flags != lt::move_flags_t::dont_replace)
    {
        torrent.handle.move_storage(relocation->destination, flags);
        return;
    }
    std::erase_if(targets, alreadyMoved);
    auto problem = std::make_shared<std::optional<Problem>>();
    auto handle = torrent.handle;
    payload.Run([targets, problem]
    {
        for (auto const& path : targets)
        {
            if (std::filesystem::symlink_status(path).type() != std::filesystem::file_type::not_found)
            {
                *problem = Problem{ProblemKind::DestinationExists, Utf8(path.wstring())};
                return;
            }
        }
    }, [this, handle, problem](StorageOutcome outcome)
    {
        if (!outcome.succeeded)
        {
            *problem = Problem{ProblemKind::MoveFailed, outcome.detail};
        }
        if (*problem)
        {
            FinishMove(handle, *problem);
        }
        else
        {
            handle.move_storage(relocation->destination, lt::move_flags_t::dont_replace);
        }
    });
}

void Engine::State::FinishMove(lt::torrent_handle const& handle, std::optional<Problem> problem)
{
    if (!relocation || torrents.at(relocation->ids.at(relocation->current)).handle != handle)
    {
        return;
    }
    if (!problem && relocation->phase == RelocationPhase::Saving)
    {
        return;
    }
    if (problem)
    {
        auto ids = relocation->ids;
        auto fail = [this, ids, problem]
        {
            for (auto const& id : ids)
            {
                auto& torrent = torrents.at(id);
                torrent.moving = false;
                torrent.moveError = problem;
                torrent.ApplyIntent();
                Notify(NoticeKind::Error, torrent, problem->detail);
            }
            diagnostics.Write("move", "", ToString(problem->kind));
            relocation.reset();
        };
        if (relocation->phase == RelocationPhase::Preparing)
        {
            relocation->phase = RelocationPhase::Saving;
            if (changes.Queue([this, ids, fail]
            {
                auto document = Saved();
                for (auto const& id : ids)
                {
                    document.torrents.at(id).moveDestination.clear();
                }
                changes.Commit(document.ToJson(), [this, ids, fail](StorageOutcome outcome)
                {
                    if (outcome.succeeded)
                    {
                        for (auto const& id : ids)
                        {
                            torrents.at(id).facts.moveDestination.clear();
                        }
                    }
                    fail();
                });
            }))
            {
                return;
            }
        }
        fail();
        return;
    }
    auto paths = FilePaths(torrents.at(relocation->ids.at(relocation->current)), relocation->destination, false);
    relocation->moved.insert(relocation->moved.end(), paths.begin(), paths.end());
    if (relocation->current + 1 < relocation->ids.size())
    {
        ++relocation->current;
        relocation->phase = RelocationPhase::Waiting;
        ContinueMove();
        return;
    }
    relocation->phase = RelocationPhase::Saving;
    if (!changes.Queue([this]
    {
        auto document = Saved();
        for (auto const& id : relocation->ids)
        {
            auto& facts = document.torrents.at(id);
            facts.savePath = relocation->destination;
            facts.moveDestination.clear();
            facts.verifyFiles = true;
        }
        changes.Commit(document.ToJson(), [this, document](StorageOutcome outcome)
        {
            if (!outcome.succeeded)
            {
                FinishMove(torrents.at(relocation->ids.at(relocation->current)).handle,
                    Problem{ProblemKind::MoveFailed, outcome.detail});
                return;
            }
            for (auto const& member : relocation->ids)
            {
                auto& moved = torrents.at(member);
                moved.facts = document.torrents.at(member);
                moved.unsaved = true;
                moved.moving = false;
                moved.completedFiles.clear();
                moved.namesReady = false;
                moved.renameAt = {};
            }
            auto ids = relocation->ids;
            relocation.reset();
            for (auto const& id : ids)
                PrepareFiles(torrents.at(id));
        });
    }))
    {
        FinishMove(handle, Problem{ProblemKind::MoveFailed, ToString(ProblemKind::StorageOverloaded)});
    }
}

void Engine::State::ContinueDeletion()
{
    if (!deletion || !deletion->waiting.empty() || deletion->phase == DeletionPhase::Deleting)
    {
        return;
    }
    auto deleting = *deletion;
    deletion->phase = DeletionPhase::Deleting;
    payload.Run([deleting]
    {
        std::string failure;
        for (auto const& file : deleting.files)
        {
            std::error_code error;
            std::filesystem::remove(file, error);
            if (error && failure.empty())
            {
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
    }, [this, names = deleting.names](StorageOutcome outcome)
    {
        if (!outcome.succeeded)
        {
            diagnostics.Write("delete", "", "delete_failed");
            Notify(NoticeKind::DeleteFailed, names, outcome.detail);
        }
        deletion.reset();
    });
}

void Engine::State::On(lt::torrent_deleted_alert const& alert)
{
    if (!deletion)
    {
        return;
    }
    std::erase(deletion->waiting, alert.handle);
    ContinueDeletion();
}

void Engine::State::On(lt::torrent_delete_failed_alert const& alert)
{
    if (!deletion || !std::erase(deletion->waiting, alert.handle))
    {
        return;
    }
    diagnostics.Write("delete", "", "partfile_delete_failed");
    Notify(NoticeKind::DeleteFailed, deletion->names, alert.error.message());
    ContinueDeletion();
}

// After libtorrent dropped alerts: finishes a move whose files already moved,
// and marks a move or deletion whose outcome was lost as uncertain.
void Engine::State::RecoverFiles()
{
    if (rename && rename->phase != RenamePhase::Moving)
    {
        rename->waiting.clear();
        if (rename->phase == RenamePhase::Naming || rename->phase == RenamePhase::Recovering)
        {
            rename->phase = RenamePhase::Recovering;
            auto handle = torrents.at(rename->owners.at(rename->current).torrentId).handle;
            rename->waiting.push_back(handle);
            handle.flush_cache();
        }
        else
        {
            rename->phase = RenamePhase::Waiting;
            ContinueRename();
        }
    }
    for (auto& [id, torrent] : torrents)
    {
        CompleteFiles(torrent);
        if (!torrent.namesReady && torrent.preparingNames && !torrent.renaming.empty())
        {
            torrent.preparingNames = false;
            torrent.renaming.clear();
        }
        torrent.fileState = lt::torrent_status::checking_resume_data;
    }
    if (relocation && relocation->phase == RelocationPhase::Moving)
    {
        auto& torrent = torrents.at(relocation->ids.at(relocation->current));
        if (!SameFolder(torrent.facts.savePath, relocation->destination) &&
            SameFolder(torrent.handle.status().save_path, relocation->destination))
        {
            FinishMove(torrent.handle, std::nullopt);
        }
        else
        {
            relocation->phase = RelocationPhase::Unknown;
            torrent.moveError = Problem{ProblemKind::MoveUncertain, {}};
            Notify(NoticeKind::Error, torrent);
        }
    }
    if (relocation && !relocation->waiting.empty() &&
        (relocation->phase == RelocationPhase::Preparing || relocation->phase == RelocationPhase::Waiting))
    {
        relocation->phase = RelocationPhase::Unknown;
        for (auto const& id : relocation->ids)
        {
            auto& torrent = torrents.at(id);
            torrent.moveError = Problem{ProblemKind::MoveUncertain, {}};
            Notify(NoticeKind::Error, torrent);
        }
    }
    if (deletion && !deletion->waiting.empty())
    {
        deletion->phase = DeletionPhase::Unknown;
        Notify(NoticeKind::DeleteFailed, deletion->names, {});
    }
}
}
