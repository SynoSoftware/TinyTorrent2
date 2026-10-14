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
        if (move->ids.empty())
            EndMove(std::nullopt);
        else
            SaveMove();
        return;
    }
    auto& torrent = torrents.at(move->ids.at(move->current));
    auto targets = FilePaths(torrent, move->destination, false);
    auto alreadyMoved = [&moving = *move](auto const& path)
    {
        return std::any_of(moving.moved.begin(), moving.moved.end(),
            [&path](auto const& other) { return SamePath(path, other); });
    };
    auto flags = lt::move_flags_t::fail_if_exist;
    if (std::all_of(targets.begin(), targets.end(), alreadyMoved))
        flags = lt::move_flags_t::reset_save_path;
    else if (std::any_of(targets.begin(), targets.end(), alreadyMoved))
        flags = lt::move_flags_t::dont_replace;
    move->phase = MovePhase::Moving;
    torrent.handle.move_storage(move->destination, flags);
}

void Engine::State::FinishMove(lt::torrent_handle const& handle, std::optional<Problem> problem)
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
    }, [this, status, problem](StorageOutcome outcome)
    {
        if (outcome.failure || status->moving_storage)
        {
            if (outcome.failure)
                log.Write("move", move->ids.at(move->current), outcome.detail);
            move->phase = MovePhase::Moving;
            RecoverMove();
            return;
        }
        if (!SameFolder(status->save_path, move->destination))
        {
            FailMove(problem.value_or(Problem{ProblemKind::MoveFailed, {}}));
            return;
        }
        auto paths = FilePaths(torrents.at(move->ids.at(move->current)), move->destination, false);
        move->moved.insert(move->moved.end(), paths.begin(), paths.end());
        ++move->current;
        move->phase = MovePhase::Ready;
        ContinueMove();
    });
}

void Engine::State::SaveMove()
{
    move->phase = MovePhase::Saving;
    if (!changes.Queue([this]
    {
        auto moved = [destination = move->destination](Facts& facts)
        {
            facts.savePath = destination;
            facts.finalFolder.clear();
            facts.moveDestination.clear();
            facts.verifyFiles = true;
        };
        auto document = Saved();
        for (auto const& id : move->ids)
        {
            if (!torrents.at(id).deleted)
            {
                moved(document.torrents.at(id));
            }
        }
        changes.Commit([document = std::move(document)] { return document.ToJson(); }, [this, moved](Outcome outcome)
        {
            if (outcome.error)
            {
                EndMove(outcome.error == ErrorCode::Overloaded ?
                    Problem{ProblemKind::StorageOverloaded} : Problem{ProblemKind::MoveFailed, outcome.detail});
                return;
            }
            for (auto const& id : move->ids)
            {
                auto& torrent = torrents.at(id);
                moved(torrent.facts);
                torrent.unsaved = true;
                torrent.completedFiles.clear();
                torrent.namePhase = NamePhase::Pending;
                torrent.renameAt = {};
            }
            EndMove(std::nullopt);
        });
    }))
    {
        EndMove(Problem{ProblemKind::StorageOverloaded});
    }
}

void Engine::State::FailMove(Problem problem)
{
    // Before the first disk move, failure can clear a new staged destination.
    // An earlier interrupted move must keep its recovery marker.
    if (move->phase == MovePhase::Preflight && !move->recovering)
    {
        move->phase = MovePhase::Saving;
        if (changes.Queue([this, problem]
        {
            auto document = Saved();
            for (auto const& id : move->ids)
            {
                if (!torrents.at(id).deleted)
                {
                    document.torrents.at(id).moveDestination.clear();
                }
            }
            changes.Commit([document = std::move(document)] { return document.ToJson(); }, [this, problem](Outcome outcome)
            {
                if (!outcome.error)
                {
                    for (auto const& id : move->ids)
                    {
                        torrents.at(id).facts.moveDestination.clear();
                    }
                }
                EndMove(problem);
            });
        }))
        {
            return;
        }
    }
    EndMove(problem);
}

void Engine::State::EndMove(std::optional<Problem> problem)
{
    auto ids = std::move(move->ids);
    move.reset();
    for (auto const& id : ids)
    {
        auto& torrent = torrents.at(id);
        torrent.moving = false;
        torrent.moveError = problem;
        if (problem)
        {
            torrent.ApplyIntent();
            torrent.notifiedError = problem->detail;
            Notify(NoticeKind::Error, torrent, problem->detail, problem->Code());
        }
    }
    if (problem)
    {
        log.Write("move", "", ToString(problem->kind));
    }
    else
    {
        ContinueNames();
    }
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
