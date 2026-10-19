#include "Engine/State.h"
#include <algorithm>

namespace tt
{
void Engine::State::StartMove(std::vector<std::string> const& ids, std::string const& destination,
    std::function<void(Outcome)> accepted)
{
    if (auto ready = FilesReady(ids); ready.error)
    {
        accepted(std::move(ready));
        return;
    }
    if (!IsAbsolute(destination))
    {
        accepted({ErrorCode::InvalidDestination});
        return;
    }
    if (!FileScope(ids).shared.empty())
    {
        accepted({ErrorCode::SharedFiles});
        return;
    }
    std::map<std::filesystem::path, std::filesystem::path, decltype(&PathBefore)> sources(PathBefore);
    std::vector<std::filesystem::path> destinations;
    Move moving;
    moving.ids = ids;
    moving.destination = destination;
    moving.recovering = std::any_of(ids.begin(), ids.end(), [this](auto const& id)
        { return !torrents.at(id).facts.moveDestination.empty(); });
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
            if (!inserted && !SamePath(known->second, source))
            {
                accepted({ErrorCode::DestinationConflict});
                return;
            }
        }
        moving.holds.insert(moving.holds.end(), from.begin(), from.end());
        moving.holds.insert(moving.holds.end(), to.begin(), to.end());
        auto logical = FilePaths(torrent, destination);
        auto origins = FilePaths(torrent, torrent.facts.savePath);
        moving.holds.insert(moving.holds.end(), logical.begin(), logical.end());
        moving.holds.insert(moving.holds.end(), origins.begin(), origins.end());
        destinations.insert(destinations.end(), to.begin(), to.end());
        destinations.insert(destinations.end(), logical.begin(), logical.end());
    }
    std::sort(destinations.begin(), destinations.end(), PathBefore);
    destinations.erase(std::unique(destinations.begin(), destinations.end(), SamePath), destinations.end());
    for (auto const& [otherId, other] : torrents)
    {
        if (Contains(ids, otherId))
        {
            continue;
        }
        auto outside = FilePaths(other);
        auto actual = FilePaths(other, {}, false);
        outside.insert(outside.end(), actual.begin(), actual.end());
        if (std::any_of(outside.begin(), outside.end(), [&destinations](auto const& path)
            { return std::binary_search(destinations.begin(), destinations.end(), path, PathBefore); }))
        {
            accepted({ErrorCode::DestinationInUse, other.Name()});
            return;
        }
    }
    auto document = Saved();
    for (auto const& id : ids)
    {
        document.torrents.at(id).moveDestination = destination;
    }
    move = std::move(moving);
    changes.Commit([document = std::move(document)] { return document.ToJson(); }, [this, accepted](Outcome outcome)
    {
        if (outcome.error)
        {
            move.reset();
            accepted(std::move(outcome));
            return;
        }
        for (auto const& id : move->ids)
        {
            auto& torrent = torrents.at(id);
            torrent.facts.moveDestination = move->destination;
            torrent.moving = true;
            torrent.moveError.reset();
            torrent.ApplyIntent();
        }
        PrepareMove();
        accepted({});
    });
}

void Engine::State::PrepareMove()
{
    std::vector<std::string> ids;
    for (auto const& id : move->ids)
        if (!torrents.at(id).deleted)
            ids.push_back(id);
    if (ids.empty())
    {
        move->phase = MovePhase::Ready;
        ContinueMove();
        return;
    }
    struct Location
    {
        std::filesystem::path source;
        std::filesystem::path destination;
        std::filesystem::path logicalSource;
        std::filesystem::path logicalDestination;
    };
    std::vector<Location> files;
    std::vector<lt::torrent_handle> handles;
    for (auto const& id : ids)
    {
        auto const& torrent = torrents.at(id);
        handles.push_back(torrent.handle);
        auto physical = torrent.Paths();
        auto logical = torrent.Paths(true);
        for (std::size_t index = 0; index < physical.size(); ++index)
        {
            auto source = std::filesystem::path(Wide(torrent.facts.savePath));
            auto destination = std::filesystem::path(Wide(move->destination));
            files.push_back({FullPath(source / physical[index]), FullPath(destination / physical[index]),
                FullPath(source / logical[index]), FullPath(destination / logical[index])});
        }
    }
    move->phase = MovePhase::Preflight;
    auto problem = std::make_shared<std::optional<Problem>>();
    auto existing = std::make_shared<std::vector<std::filesystem::path>>();
    payload.Run([this, handles, files, problem, existing]
    {
        ReleaseFiles(handles);
        auto present = [](auto const& path)
        {
            return std::filesystem::symlink_status(path).type() != std::filesystem::file_type::not_found;
        };
        // Libtorrent can leave an empty placeholder before any bytes arrive.
        auto occupied = [&present](auto const& path)
        {
            return present(path) && (!std::filesystem::is_regular_file(path) || std::filesystem::file_size(path) != 0);
        };
        for (auto const& file : files)
        {
            if (!present(file.destination) && !present(file.logicalDestination))
                continue;
            if (!SamePath(file.source, file.destination) && (occupied(file.source) || occupied(file.logicalSource)))
            {
                *problem = Problem{ProblemKind::DestinationExists, Utf8(file.destination.wstring())};
                return;
            }
            existing->push_back(file.destination);
        }
        // An empty unfinished name would mask the final file after a partial move.
        for (auto const& file : files)
        {
            if (!SamePath(file.source, file.logicalSource) && present(file.logicalDestination) &&
                present(file.source) && !occupied(file.source))
                std::filesystem::remove(file.source);
        }
    }, [this, problem, existing](StorageOutcome outcome)
    {
        if (outcome.failure)
            *problem = *outcome.failure == StorageFailure::Overloaded ?
                Problem{ProblemKind::StorageOverloaded} : Problem{ProblemKind::MoveFailed, outcome.detail};
        if (*problem)
        {
            FailMove(**problem);
            return;
        }
        move->moved = std::move(*existing);
        move->phase = MovePhase::Ready;
        ContinueMove();
    });
}

void Engine::State::ContinueMove()
{
    if (!move || move->phase == MovePhase::Staging || move->phase == MovePhase::Saving)
    {
        return;
    }
    // A worker or disk operation still owns the current member. Other deleted
    // members leave before their turn, so their files stay where they are.
    auto member = move->ids.begin() + move->current;
    if (move->phase != MovePhase::Ready)
        ++member;
    while (member != move->ids.end())
    {
        auto& torrent = torrents.at(*member);
        if (!torrent.deleted)
        {
            ++member;
            continue;
        }
        torrent.moving = false;
        torrent.facts.moveDestination.clear();
        member = move->ids.erase(member);
    }
    if (move->phase != MovePhase::Ready)
        return;
    if (move->current == move->ids.size())
    {
        move.reset();
        ContinueNames();
        return;
    }
    auto files = Relocated(torrents.at(move->ids.at(move->current)));
    auto among = [](std::vector<std::filesystem::path> const& paths, Payload::File const& file)
    {
        return std::any_of(paths.begin(), paths.end(),
            [&file](auto const& path) { return SamePath(file.destination, path); });
    };
    // A failure cannot restore a file that an earlier member renamed away.
    bool shares = std::any_of(files.begin(), files.end(),
        [&among, this](auto const& file) { return among(move->placed, file); });
    std::erase_if(files, [&among, this](auto const& file)
        { return among(move->moved, file) || among(move->placed, file); });
    // The remaining members can share files, so none of them may still use one.
    std::vector<lt::torrent_handle> handles;
    for (auto remaining = move->ids.begin() + move->current; remaining != move->ids.end(); ++remaining)
        handles.push_back(torrents.at(*remaining).handle);
    move->progress = std::make_shared<Payload::Progress>();
    move->phase = MovePhase::Copying;
    auto failure = std::make_shared<std::optional<Payload::Failure>>();
    copies.Run([this, handles, files = std::move(files), progress = move->progress, failure]
    {
        ReleaseFiles(handles);
        if (auto placed = Payload::Move(files, *progress); !placed)
            *failure = std::move(placed.error());
    }, [this, failure, shares](StorageOutcome outcome)
    {
        if (outcome.failure)
        {
            FailMove(*outcome.failure == StorageFailure::Overloaded ?
                Problem{ProblemKind::StorageOverloaded} : Problem{ProblemKind::MoveFailed, outcome.detail});
            return;
        }
        if (*failure)
        {
            auto split = shares || (*failure)->kind == MoveFailure::Split;
            FailMove(Problem{split ? ProblemKind::MoveInterrupted : ProblemKind::MoveFailed, (*failure)->detail});
            return;
        }
        move->phase = MovePhase::Moving;
        // Every file is already at the destination, so libtorrent moves only
        // its own part file, takes the new folder and checks the files there.
        torrents.at(move->ids.at(move->current)).handle.move_storage(move->destination,
            lt::move_flags_t::dont_replace);
    });
}

std::vector<Payload::File> Engine::State::Relocated(Torrent const& torrent) const
{
    std::filesystem::path from = Wide(torrent.facts.savePath);
    std::filesystem::path to = Wide(move->destination);
    std::vector<Payload::File> files;
    for (auto const& path : torrent.Paths())
    {
        auto source = FullPath(from / path);
        auto target = FullPath(to / path);
        if (!SamePath(source, target))
            files.push_back({std::move(source), std::move(target)});
    }
    return files;
}

void Engine::State::FinishMove(lt::torrent_handle const& handle, std::string failure)
{
    if (!move || move->phase != MovePhase::Moving ||
        torrents.at(move->ids.at(move->current)).handle != handle)
    {
        return;
    }
    // Results can outlive recovery and arrive during another move of this
    // handle. Establish its current location instead of trusting that event.
    move->phase = MovePhase::Observing;
    auto status = std::make_shared<lt::torrent_status>();
    payload.Run([this, handle, status]
    {
        ReleaseFiles({handle});
        *status = handle.status(lt::torrent_handle::query_save_path);
    }, [this, status, failure](StorageOutcome outcome)
    {
        if (outcome.failure || status->moving_storage)
        {
            if (outcome.failure)
                log.Write("move", move->ids.at(move->current), outcome.detail);
            move->phase = MovePhase::Moving;
            RecoverMove();
            return;
        }
        // The files are at the destination, but libtorrent kept the old folder.
        if (!SameFolder(status->save_path, move->destination))
        {
            FailMove(Problem{ProblemKind::MoveInterrupted, failure});
            return;
        }
        auto paths = FilePaths(torrents.at(move->ids.at(move->current)), move->destination, false);
        move->placed.insert(move->placed.end(), paths.begin(), paths.end());
        SaveMove();
    });
}

void Engine::State::SaveMove()
{
    move->phase = MovePhase::Saving;
    auto id = move->ids.at(move->current);
    auto relocate = [destination = move->destination](Facts& facts)
    {
        facts.savePath = destination;
        facts.finalFolder.clear();
        facts.moveDestination.clear();
        facts.verifyFiles = true;
    };
    // The member ends at once, and its sources go after the new folder is
    // saved. A copied source that a later member also uses stays until that
    // member's own sources go, so its old folder stays complete until then.
    auto settle = [this, id, relocate]
    {
        auto& torrent = torrents.at(id);
        std::set<std::filesystem::path, decltype(&PathBefore)> later(PathBefore);
        for (auto member = move->ids.begin() + move->current + 1; member != move->ids.end(); ++member)
        {
            for (auto const& file : Relocated(torrents.at(*member)))
                later.insert(file.source);
        }
        std::vector<std::filesystem::path> sources;
        for (auto const& file : Relocated(torrent))
        {
            if (!later.contains(file.source))
                sources.push_back(file.source);
        }
        auto origin = FullPath(Wide(torrent.facts.savePath));
        relocate(torrent.facts);
        torrent.unsaved = true;
        torrent.completedFiles.clear();
        torrent.namePhase = NamePhase::Pending;
        torrent.renameAt = {};
        EndMove(torrent, std::nullopt);
        CleanMove(std::move(sources), std::move(origin));
    };
    if (!changes.Queue([this, id, relocate, settle]
    {
        // A deleted torrent has left the document. Its new folder decides
        // only where its removal deletes its files.
        if (torrents.at(id).deleted)
        {
            settle();
            return;
        }
        auto document = Saved();
        relocate(document.torrents.at(id));
        changes.Commit([document = std::move(document)] { return document.ToJson(); }, [this, settle](Outcome outcome)
        {
            if (outcome.error)
            {
                FailMove(Problem{ProblemKind::MoveInterrupted, outcome.detail});
                return;
            }
            settle();
        });
    }))
    {
        FailMove(Problem{ProblemKind::MoveInterrupted});
    }
}

// The sources are deleted only after the new folder is saved, so a crash never
// leaves a saved folder whose files are partly gone. A failure only leaves
// files behind, because the torrent is complete at its destination.
void Engine::State::CleanMove(std::vector<std::filesystem::path> sources, std::filesystem::path origin)
{
    move->phase = MovePhase::Cleaning;
    copies.Run([sources = std::move(sources), origin = std::move(origin)]
    {
        Erase(sources, DeletionMode::Permanent, {origin});
    }, [this](StorageOutcome outcome)
    {
        if (outcome.failure)
            log.Write("move", move->ids.at(move->current), outcome.detail);
        ++move->current;
        move->phase = MovePhase::Ready;
        ContinueMove();
    });
}

void Engine::State::FailMove(Problem problem)
{
    // A failed preflight reaches every member; a later failure only the
    // current one, and the rest of the group still moves.
    auto first = move->ids.begin() + move->current;
    std::vector<std::string> failed(first, move->phase == MovePhase::Preflight ? move->ids.end() : first + 1);
    auto end = [this, failed, problem]
    {
        for (auto const& id : failed)
            EndMove(torrents.at(id), problem);
        move->current += failed.size();
        move->phase = MovePhase::Ready;
        ContinueMove();
    };
    move->phase = MovePhase::Saving;
    // Unless the move was interrupted, the files are complete in their save
    // folder, so the new marker clears. A move that recovers an interrupted
    // one keeps its markers, because its files can still be in two folders.
    if (problem.kind == ProblemKind::MoveInterrupted || move->recovering || !changes.Queue([this, failed, end]
    {
        auto document = Saved();
        for (auto const& id : failed)
        {
            if (!torrents.at(id).deleted)
            {
                document.torrents.at(id).moveDestination.clear();
            }
        }
        changes.Commit([document = std::move(document)] { return document.ToJson(); }, [this, failed, end](Outcome outcome)
        {
            if (!outcome.error)
            {
                for (auto const& id : failed)
                {
                    torrents.at(id).facts.moveDestination.clear();
                }
            }
            end();
        });
    }))
    {
        end();
    }
}

void Engine::State::EndMove(Torrent& torrent, std::optional<Problem> problem)
{
    torrent.moving = false;
    torrent.moveError = problem;
    if (!problem)
    {
        ContinueNames();
        return;
    }
    torrent.ApplyIntent();
    torrent.notifiedError = problem->detail;
    Notify(NoticeKind::Error, torrent, problem->detail, problem->Code());
    log.Write("move", torrent.torrentId, ToString(problem->kind));
}

std::optional<double> Engine::State::MoveProgress(Torrent const& torrent) const
{
    if (!move || !torrent.moving)
        return std::nullopt;
    auto current = move->ids.begin() + move->current;
    auto member = std::find(current, move->ids.end(), torrent.torrentId);
    if (member == move->ids.end())
        return std::nullopt;
    if (member != current || !move->progress)
        return 0.0;
    auto total = move->progress->total.load();
    return total == 0 ? 0.0 : double(move->progress->done.load()) / double(total);
}

void Engine::State::RecoverMove()
{
    if (!move || move->phase != MovePhase::Moving)
        return;
    // Releasing files queues behind the move. Its acknowledgement lets
    // recovery observe the outcome without repeating the move.
    torrents.at(move->ids.at(move->current)).handle.flush_cache();
}

}
