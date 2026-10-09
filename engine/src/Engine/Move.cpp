#include "Engine/State.h"
#include <algorithm>
#include <set>

namespace tt
{
void Engine::State::StartMove(std::vector<std::string> const& ids, std::string const& destination,
    bool useExisting, std::function<void(Outcome)> accepted)
{
    if (auto ready = FilesReady(ids); ready.error)
    {
        accepted(std::move(ready));
        return;
    }
    for (auto const& id : ids)
    {
        if (!useExisting && !torrents.at(id).facts.moveDestination.empty())
        {
            accepted({ErrorCode::MoveInterrupted});
            return;
        }
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
    Move moving;
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
        auto destinations = to;
        destinations.insert(destinations.end(), logical.begin(), logical.end());
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
                accepted({ErrorCode::DestinationInUse, other.Name()});
                return;
            }
        }
    }
    auto document = Saved();
    for (auto const& id : ids)
    {
        document.torrents.at(id).moveDestination = destination;
    }
    move = std::move(moving);
    changes.Commit(document.ToJson(), [this, accepted](StorageOutcome outcome)
    {
        if (!outcome.succeeded)
        {
            move.reset();
            accepted({ErrorCode::StorageFailed, outcome.detail});
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
    std::set<std::filesystem::path, decltype(&PathBefore)> targets(PathBefore);
    std::vector<lt::torrent_handle> handles;
    for (auto const& id : ids)
    {
        auto const& torrent = torrents.at(id);
        handles.push_back(torrent.handle);
        if (move->usesExisting)
            continue;
        auto physical = FilePaths(torrent, move->destination, false);
        auto logical = FilePaths(torrent, move->destination);
        targets.insert(physical.begin(), physical.end());
        targets.insert(logical.begin(), logical.end());
    }
    move->phase = MovePhase::Preflight;
    auto problem = std::make_shared<std::optional<Problem>>();
    payload.Run([this, handles, targets, problem]
    {
        ReleaseFiles(handles);
        for (auto const& path : targets)
        {
            if (std::filesystem::symlink_status(path).type() != std::filesystem::file_type::not_found)
            {
                *problem = Problem{ProblemKind::DestinationExists, Utf8(path.wstring())};
                return;
            }
        }
    }, [this, problem](StorageOutcome outcome)
    {
        if (!outcome.succeeded)
            *problem = Problem{ProblemKind::MoveFailed, outcome.detail};
        if (*problem)
        {
            FailMove(**problem);
            return;
        }
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
    if (move->usesExisting || std::all_of(targets.begin(), targets.end(), alreadyMoved))
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
        if (!outcome.succeeded || status->moving_storage)
        {
            if (!outcome.succeeded)
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
        changes.Commit(document.ToJson(), [this, moved](StorageOutcome outcome)
        {
            if (!outcome.succeeded)
            {
                EndMove(Problem{ProblemKind::MoveFailed, outcome.detail});
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
        EndMove(Problem{ProblemKind::MoveFailed, ToString(ProblemKind::StorageOverloaded)});
    }
}

void Engine::State::FailMove(Problem problem)
{
    // Before the first disk move, failure can clear a new staged destination.
    // Use existing files may be resolving an earlier interrupted move.
    if (move->phase == MovePhase::Preflight && !move->usesExisting)
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
            changes.Commit(document.ToJson(), [this, problem](StorageOutcome outcome)
            {
                if (outcome.succeeded)
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
        else
        {
            PrepareFiles(torrent);
        }
    }
    if (problem)
    {
        log.Write("move", "", ToString(problem->kind));
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
