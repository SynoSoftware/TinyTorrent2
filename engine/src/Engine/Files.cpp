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
    return relocation.has_value() || deletion.has_value();
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
    std::string const& destination) const
{
    auto metadata = torrent.handle.torrent_file();
    if (!metadata)
    {
        return {};
    }
    if (!destination.empty())
    {
        return FilePaths(*metadata, destination);
    }
    auto files = FilePaths(*metadata, torrent.facts.savePath);
    if (!torrent.facts.moveDestination.empty())
    {
        auto staged = FilePaths(*metadata, torrent.facts.moveDestination);
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
        if (Contains(ids, id) && torrent.priorityReply)
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
    for (auto const& id : ids)
    {
        auto files = FilePaths(torrents.at(id));
        scope.files.insert(scope.files.end(), files.begin(), files.end());
    }
    std::sort(scope.files.begin(), scope.files.end(), PathBefore);
    scope.files.erase(std::unique(scope.files.begin(), scope.files.end(), SamePath), scope.files.end());
    auto paths = scope.files;
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
            auto files = FilePaths(torrent);
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
            paths.insert(paths.end(), files.begin(), files.end());
            std::sort(paths.begin(), paths.end(), PathBefore);
        }
    }
    // Only files in the original command count as kept by a deletion.
    for (auto const& file : scope.files)
    {
        if (used.contains(file))
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
    Relocation moving;
    moving.ids = ids;
    moving.destination = destination;
    moving.usesExisting = useExisting;
    for (auto const& id : ids)
    {
        auto const& torrent = torrents.at(id);
        auto from = FilePaths(torrent, torrent.facts.savePath);
        auto to = FilePaths(torrent, destination);
        for (std::size_t index = 0; index < to.size(); ++index)
        {
            auto [known, inserted] = sources.emplace(to[index], from[index]);
            if (!useExisting && !inserted && !SamePath(known->second, from[index]))
            {
                reply(Failure(ErrorCode::DestinationConflict));
                return;
            }
        }
        moving.holds.insert(moving.holds.end(), from.begin(), from.end());
        moving.holds.insert(moving.holds.end(), to.begin(), to.end());
        for (auto const& [otherId, other] : torrents)
        {
            if (Contains(ids, otherId))
            {
                continue;
            }
            auto outside = FilePaths(other);
            if (std::any_of(to.begin(), to.end(), [&outside](auto const& path)
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
    changes.Commit(document.ToJson(), [this, reply, sources](StorageOutcome outcome)
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
        payload.Run([sources, useExisting = relocation->usesExisting, problem]
        {
            if (useExisting)
            {
                return;
            }
            for (auto const& [path, source] : sources)
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
    auto targets = FilePaths(torrent, relocation->destination);
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
    auto paths = FilePaths(torrents.at(relocation->ids.at(relocation->current)), relocation->destination);
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
                moved.handle.force_recheck();
                moved.ApplyIntent();
            }
            relocation.reset();
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
