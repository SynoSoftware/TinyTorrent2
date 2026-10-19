#include "Engine/State.h"
#include <algorithm>
#include <iterator>

namespace tt
{
void Engine::State::StartMove(std::vector<std::string> const& ids, std::string const& destination, MoveStart start,
    std::function<void(Outcome)> accepted)
{
    // Checked before busy files, so a move that waits for them has a folder
    // it can use.
    if (!IsAbsolute(destination))
    {
        accepted({ErrorCode::InvalidDestination});
        return;
    }
    if (auto ready = FilesReady(ids); ready.error)
    {
        accepted(std::move(ready));
        return;
    }
    if (!FileScope(ids).shared.empty())
    {
        accepted({ErrorCode::SharedFiles});
        return;
    }
    // Each file has one destination and each destination one file: a file
    // reached at two paths would leave one member without it once the other
    // member's move renames or deletes it.
    std::map<std::filesystem::path, std::filesystem::path, decltype(&PathBefore)> sources(PathBefore);
    std::map<std::filesystem::path, std::filesystem::path, decltype(&PathBefore)> targets(PathBefore);
    std::vector<std::filesystem::path> destinations;
    Move moving;
    moving.ids = ids;
    moving.destination = destination;
    // Members whose interrupted move checked this destination.
    std::vector<std::string> continued;
    for (auto const& id : ids)
    {
        auto const& torrent = torrents.at(id);
        auto const& marker = torrent.facts.marker;
        if (marker)
            moving.recovering.push_back(id);
        if (marker && marker->stage != MoveStage::Staged && SameFolder(marker->destination, destination))
            continued.push_back(id);
        auto physical = torrent.Paths();
        auto from = FilePaths(torrent, torrent.facts.savePath, false);
        auto to = FilePaths(torrent, destination, false);
        for (auto const& path : physical)
        {
            auto source = FullPath(std::filesystem::path(Wide(torrent.facts.savePath)) / path);
            auto target = FullPath(std::filesystem::path(Wide(destination)) / path);
            auto [known, inserted] = sources.emplace(target, source);
            auto [mapped, added] = targets.emplace(source, target);
            if ((!inserted && !SamePath(known->second, source)) || (!added && !SamePath(mapped->second, target)))
            {
                accepted({ErrorCode::DestinationConflict});
                return;
            }
            moving.shared |= !inserted;
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
        auto outside = Footprint(other);
        if (std::any_of(outside.begin(), outside.end(), [&destinations](auto const& path)
            { return std::binary_search(destinations.begin(), destinations.end(), path, PathBefore); }))
        {
            accepted({ErrorCode::DestinationInUse, other.Name()});
            return;
        }
    }
    // A continued member keeps its checked destination, so a failure before
    // the next check still lets a later move keep its complete copies. A move
    // that continues on its own is saved as waiting before any file changes,
    // so an interruption of it cannot start it again at the next launch.
    auto checked = start == MoveStart::Resumed ? MoveStage::Waiting : MoveStage::Placing;
    auto stage = [continued, checked](std::string const& id)
        { return Contains(continued, id) ? checked : MoveStage::Staged; };
    move = std::move(moving);
    CommitFacts(ids, [destination, stage](std::string const& id, Facts& facts)
    {
        facts.marker = Facts::Marker{destination, stage(id)};
    }, [this, accepted](Outcome outcome, auto const&)
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
            torrent.moving = true;
            // The latest move the person asked for replaces one that waited.
            torrent.waitingMove.reset();
            torrent.moveError.reset();
            torrent.ApplyIntent();
        }
        PrepareMove();
        accepted({});
    });
}

// Starts the moves the person asked for while files were busy, the torrents
// with one destination together, once the files are free.
void Engine::State::MoveWaiting()
{
    if (startup != Startup::Ready || IsShuttingDown() || !changes.IsIdle())
        return;
    auto waits = [](Torrent const& torrent) { return !torrent.deleted && torrent.waitingMove; };
    auto first = std::find_if(torrents.begin(), torrents.end(), [&waits](auto const& entry)
        { return waits(entry.second); });
    if (first == torrents.end())
        return;
    auto destination = *first->second.waitingMove;
    std::vector<std::string> ids;
    for (auto const& [id, torrent] : torrents)
    {
        if (waits(torrent) && SameFolder(*torrent.waitingMove, destination))
            ids.push_back(id);
    }
    auto ready = FilesReady(ids);
    if (ready.error == ErrorCode::MetadataUnavailable)
    {
        for (auto const& id : ids)
            RefuseMove(torrents.at(id), ready);
    }
    if (DelaysMove(ready))
        return;
    changes.Queue([this, waits, ids, destination]
    {
        std::vector<std::string> present;
        for (auto const& id : ids)
        {
            auto found = torrents.find(id);
            if (found != torrents.end() && waits(found->second) && SameFolder(*found->second.waitingMove, destination))
                present.push_back(id);
        }
        if (present.empty())
            return;
        StartMove(present, destination, MoveStart::Requested, [this, present](Outcome outcome)
        {
            if (DelaysMove(outcome))
                return;
            for (auto const& id : present)
            {
                auto found = torrents.find(id);
                if (found == torrents.end())
                    continue;
                found->second.waitingMove.reset();
                if (outcome.error)
                    RefuseMove(found->second, outcome);
            }
        });
    });
}

bool Engine::State::DelaysMove(Outcome const& outcome)
{
    return outcome.error == ErrorCode::FilesBusy || outcome.error == ErrorCode::MetadataUnavailable;
}

// Shows on the torrent, and once as a toast, why a move that the person is
// not waiting on could not start.
void Engine::State::RefuseMove(Torrent& torrent, Outcome const& outcome)
{
    bool shown = torrent.moveError && torrent.moveError->refusal == outcome.error;
    torrent.moveError = Problem{ProblemKind::MoveFailed, outcome.detail, outcome.error};
    // A wait that now names another torrent is the same problem, so the
    // status update does not report it again.
    torrent.notifiedError = outcome.detail;
    if (!shown)
        Notify(ToastKind::Error, torrent, outcome.detail, torrent.moveError->Code());
}

// A crash, power loss or forced exit interrupts a move the person asked for,
// so the engine finishes it at the next launch. It tries once: a move that
// itself ends the engine would otherwise repeat at every launch.
void Engine::State::ResumeMove()
{
    if (startup != Startup::Ready || IsShuttingDown() || !changes.IsIdle())
        return;
    // A waiting move the person asked for replaces the interrupted one.
    auto waits = [](Torrent const& torrent)
        { return !torrent.deleted && !torrent.restore && !torrent.waitingMove && torrent.ResumesMove(); };
    auto first = std::find_if(torrents.begin(), torrents.end(), [&waits](auto const& entry)
        { return waits(entry.second); });
    if (first == torrents.end())
        return;
    auto destination = first->second.facts.marker->destination;
    std::vector<std::string> ids;
    for (auto const& [id, torrent] : torrents)
    {
        if (waits(torrent) && SameFolder(torrent.facts.marker->destination, destination))
            ids.push_back(id);
    }
    // A refusal shows on the torrents, as a move to the final folder does. A
    // delayed move tries again at a later tick without queuing a change each
    // time.
    // Any other refusal waits for the person: Waiting is set at once, so this
    // run does not try again, and saved, so the next launch does not either.
    auto refuse = [this, ids](Outcome const& outcome)
    {
        if (outcome.error == ErrorCode::FilesBusy)
            return;
        bool delays = outcome.error == ErrorCode::MetadataUnavailable;
        for (auto const& id : ids)
        {
            auto found = torrents.find(id);
            if (found == torrents.end())
                continue;
            auto& torrent = found->second;
            RefuseMove(torrent, outcome);
            if (!delays && torrent.facts.marker)
                torrent.facts.marker->stage = MoveStage::Waiting;
        }
        if (delays)
            return;
        changes.Queue([this]
        {
            changes.Commit([document = Saved()] { return document.ToJson(); }, [](Outcome) {});
        });
        log.Write("move", ids.front(), "resume_refused");
    };
    if (auto ready = FilesReady(ids); DelaysMove(ready))
    {
        refuse(ready);
        return;
    }
    changes.Queue([this, waits, ids, refuse]
    {
        if (!std::all_of(ids.begin(), ids.end(), [this, &waits](auto const& id)
            { auto found = torrents.find(id); return found != torrents.end() && waits(found->second); }))
            return;
        StartMove(ids, torrents.at(ids.front()).facts.marker->destination, MoveStart::Resumed,
            [refuse](Outcome outcome)
        {
            if (outcome.error)
                refuse(outcome);
        });
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
        bool continued;
    };
    std::vector<Location> files;
    std::vector<lt::torrent_handle> handles;
    for (auto const& id : ids)
    {
        auto const& torrent = torrents.at(id);
        handles.push_back(torrent.handle);
        auto physical = torrent.Paths();
        auto logical = torrent.Paths(true);
        // Only a continued member starts with its destination checked.
        bool continued = torrent.facts.marker->stage != MoveStage::Staged;
        for (std::size_t index = 0; index < physical.size(); ++index)
        {
            auto source = std::filesystem::path(Wide(torrent.facts.savePath));
            auto destination = std::filesystem::path(Wide(move->destination));
            files.push_back({FullPath(source / physical[index]), FullPath(destination / physical[index]),
                FullPath(source / logical[index]), FullPath(destination / logical[index]), continued});
        }
    }
    move->phase = MovePhase::Preflight;
    using Checked = std::expected<std::vector<std::filesystem::path>, Problem>;
    payload.Run([this, handles, files]() -> Checked
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
        std::vector<std::filesystem::path> existing;
        std::vector<std::filesystem::path> stale;
        for (auto const& file : files)
        {
            if (auto partial = Payload::Partial(file.destination); present(partial))
                stale.push_back(partial);
            if (!present(file.destination) && !present(file.logicalDestination))
                continue;
            bool sourced = occupied(file.source) || occupied(file.logicalSource);
            // An interrupted move's complete copy stays. Any other file beside
            // a source that holds data belongs to someone else.
            bool alone = SamePath(file.destination, file.logicalDestination) || !present(file.logicalDestination);
            if (file.continued && alone && Payload::IsCopy({file.source, file.destination}))
            {
                existing.push_back(file.destination);
                continue;
            }
            if (!SamePath(file.source, file.destination) && sourced)
                return std::unexpected(Problem{ProblemKind::DestinationExists, Utf8(file.destination.wstring())});
            existing.push_back(file.destination);
        }
        // An empty unfinished name would mask the final file after a partial move.
        for (auto const& file : files)
        {
            if (!SamePath(file.source, file.logicalSource) && present(file.logicalDestination) &&
                present(file.source) && !occupied(file.source))
                stale.push_back(file.source);
        }
        Payload::Delete(stale);
        return existing;
    }, [this](std::expected<Checked, StorageOutcome> checked)
    {
        if (!checked)
        {
            FailMove(Problem::From(checked.error(), ProblemKind::MoveFailed));
            return;
        }
        if (!*checked)
        {
            FailMove(checked->error());
            return;
        }
        move->moved = std::move(**checked);
        // The checked destination is saved before any file changes, so the
        // next launch may continue this move and keep its complete copies.
        if (!changes.Queue([this]
        {
            std::vector<std::string> members;
            std::copy_if(move->ids.begin(), move->ids.end(), std::back_inserter(members),
                [this](std::string const& id) { return !torrents.at(id).deleted; });
            CommitFacts(members, [](std::string const&, Facts& facts)
            {
                if (facts.marker && facts.marker->stage == MoveStage::Staged)
                    facts.marker->stage = MoveStage::Placing;
            }, [this](Outcome outcome, auto const&)
            {
                if (outcome.error)
                {
                    FailMove(Problem{ProblemKind::MoveFailed, outcome.detail});
                    return;
                }
                move->phase = MovePhase::Ready;
                ContinueMove();
            });
        }))
        {
            FailMove(Problem{ProblemKind::StorageOverloaded});
        }
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
        torrent.facts.marker.reset();
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
    std::set<std::filesystem::path, decltype(&PathBefore)> placed(move->placed.begin(), move->placed.end(),
        PathBefore);
    std::set<std::filesystem::path, decltype(&PathBefore)> moved(move->moved.begin(), move->moved.end(),
        PathBefore);
    // A failure cannot restore a file that an earlier member renamed away, and
    // it leaves a file that was already at the destination there.
    bool split = std::erase_if(files, [&placed, &moved](auto const& file)
        { return moved.contains(file.destination) || placed.contains(file.destination); }) != 0;
    // The remaining members can share files, so none of them may still use one.
    std::vector<lt::torrent_handle> handles;
    for (auto remaining = move->ids.begin() + move->current; remaining != move->ids.end(); ++remaining)
        handles.push_back(torrents.at(*remaining).handle);
    move->progress = std::make_shared<Payload::Progress>();
    move->sampled = 0;
    move->phase = MovePhase::Copying;
    copies.Run([this, handles, files = std::move(files), progress = move->progress]
    {
        ReleaseFiles(handles);
        return Payload::Move(files, *progress);
    }, [this, split](std::expected<std::expected<void, Payload::Failure>, StorageOutcome> placed)
    {
        if (!placed)
        {
            FailMove(Problem::From(placed.error(), ProblemKind::MoveFailed));
            return;
        }
        if (!*placed)
        {
            auto interrupted = split || placed->error().kind == MoveFailure::Split;
            FailMove(Problem{interrupted ? ProblemKind::MoveInterrupted : ProblemKind::MoveFailed, placed->error().detail});
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
    payload.Run([this, handle]
    {
        ReleaseFiles({handle});
        return handle.status(lt::torrent_handle::query_save_path);
    }, [this, failure](std::expected<lt::torrent_status, StorageOutcome> status)
    {
        if (!status || status->moving_storage)
        {
            if (!status)
                log.Write("move", move->ids.at(move->current), status.error().detail);
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
        if (move->shared)
        {
            auto paths = FilePaths(torrents.at(move->ids.at(move->current)), move->destination, false);
            move->placed.insert(move->placed.end(), paths.begin(), paths.end());
        }
        SaveMove();
    });
}

void Engine::State::SaveMove()
{
    move->phase = MovePhase::Saving;
    auto id = move->ids.at(move->current);
    // The member ends at once, and its sources go after the new folder is
    // saved. A copied source that a later member also uses stays until that
    // member's own sources go, so its old folder stays complete until then.
    std::set<std::filesystem::path, decltype(&PathBefore)> later(PathBefore);
    for (auto member = move->ids.begin() + move->current + 1; move->shared && member != move->ids.end(); ++member)
    {
        for (auto const& file : Relocated(torrents.at(*member)))
            later.insert(file.source);
    }
    std::vector<std::filesystem::path> sources;
    for (auto const& file : Relocated(torrents.at(id)))
    {
        if (!later.contains(file.source))
            sources.push_back(file.source);
    }
    auto relocate = [destination = move->destination](std::string const&, Facts& facts)
    {
        facts.savePath = destination;
        facts.finalFolder.clear();
        facts.marker.reset();
        facts.verifyFiles = true;
    };
    auto settle = [this, id, sources = std::move(sources), origin = FullPath(Wide(torrents.at(id).facts.savePath))]
    {
        auto& torrent = torrents.at(id);
        torrent.unsaved = true;
        torrent.completedFiles.clear();
        torrent.namePhase = NamePhase::Pending;
        torrent.renameAt = {};
        EndMove(torrent, std::nullopt);
        CleanMove(sources, origin);
    };
    if (!changes.Queue([this, id, relocate, settle]
    {
        // A deleted torrent has left the document. Its new folder decides
        // only where its removal deletes its files.
        if (torrents.at(id).deleted)
        {
            relocate(id, torrents.at(id).facts);
            settle();
            return;
        }
        CommitFacts({id}, relocate, [this, settle](Outcome outcome, auto const&)
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
    // A failed preflight reaches every member; a later failure the current
    // one and every waiting member that shares a file with a failed one,
    // because moving that member would take the file from the failed
    // member's folder. The rest of the group still moves.
    auto first = move->ids.begin() + move->current;
    auto last = move->phase == MovePhase::Preflight ? move->ids.end() : first + 1;
    std::set<std::filesystem::path, decltype(&PathBefore)> held(PathBefore);
    for (auto member = first; member != last; ++member)
    {
        auto paths = FilePaths(torrents.at(*member), move->destination, false);
        held.insert(paths.begin(), paths.end());
    }
    for (auto member = last; member != move->ids.end();)
    {
        auto paths = torrents.at(*member).deleted ? std::vector<std::filesystem::path>{} :
            FilePaths(torrents.at(*member), move->destination, false);
        if (std::none_of(paths.begin(), paths.end(), [&held](auto const& path) { return held.contains(path); }))
        {
            ++member;
            continue;
        }
        held.insert(paths.begin(), paths.end());
        // Failed members stay together after the current one. The new paths
        // can reach a member already passed, so the search starts again.
        std::rotate(last, member, member + 1);
        member = ++last;
    }
    std::vector<std::string> failed(first, last);
    // Unless the move was interrupted, the files are complete in their save
    // folder, so the new marker clears. A member that recovers an interrupted
    // move keeps its marker, because its files can still be in two folders.
    // A kept marker waits for the person: continuing a failed move on its own
    // would repeat the failure.
    auto clears = [this, interrupted = problem.kind == ProblemKind::MoveInterrupted](std::string const& id)
        { return !interrupted && !Contains(move->recovering, id); };
    auto settle = [](Facts& facts, bool clear)
    {
        if (clear)
        {
            facts.marker.reset();
        }
        else if (facts.marker && facts.marker->stage == MoveStage::Placing)
        {
            facts.marker->stage = MoveStage::Waiting;
        }
    };
    // A marker whose clearing was not saved stays, and waits like a kept one.
    auto end = [this, failed, problem, clears, settle](bool saved)
    {
        for (auto const& id : failed)
        {
            settle(torrents.at(id).facts, clears(id) && saved);
            EndMove(torrents.at(id), problem);
        }
        move->current += failed.size();
        move->phase = MovePhase::Ready;
        ContinueMove();
    };
    move->phase = MovePhase::Saving;
    if (!changes.Queue([this, failed, clears, settle, end]
    {
        auto document = Saved();
        for (auto const& id : failed)
        {
            if (!torrents.at(id).deleted)
                settle(document.torrents.at(id), clears(id));
        }
        changes.Commit([document = std::move(document)] { return document.ToJson(); }, [end](Outcome outcome)
        {
            end(!outcome.error);
        });
    }))
    {
        end(false);
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
    Notify(ToastKind::Error, torrent, problem->detail, problem->Code());
    log.Write("move", torrent.torrentId, ToString(problem->kind));
}

std::optional<double> Engine::State::MoveProgress(Torrent const& torrent) const
{
    if (torrent.MoveWaits())
        return 0.0;
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

void Engine::State::SampleMove(std::chrono::steady_clock::duration elapsed)
{
    if (!move)
        return;
    auto copied = move->progress ? move->progress->copied.load() : 0;
    move->rate = double(copied - move->sampled) / std::chrono::duration<double>(elapsed).count();
    move->sampled = copied;
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
