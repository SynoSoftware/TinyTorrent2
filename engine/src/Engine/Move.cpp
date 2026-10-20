#include "Engine/State.h"
#include <algorithm>

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
    if (auto ready = FilesReady(ids, destination); ready.error)
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
        moving.sourceFolders.push_back(torrent.facts.savePath);
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
    // Checked after the refusals that the person can act on, so a move that
    // waits for its drives can still start once they are free. A move that
    // waited checks again, because a job can start between the drive pass
    // that gave it its turn and this start.
    if (!DrivesFree(ids, destination, start == MoveStart::Waited))
    {
        accepted({ErrorCode::FilesBusy});
        return;
    }
    // A continued member keeps its checked destination and the proof of its
    // unfinished copy, so a failure before the next check still lets a later
    // move keep its complete copies and continue the unfinished one.
    moving.worker = std::make_shared<Store>(wake);
    auto& started = moves.emplace_back(std::move(moving));
    CommitFacts(ids, [destination, continued](std::string const& id, Facts& facts)
    {
        facts.marker = Contains(continued, id) ?
            Facts::Marker{destination, MoveStage::Placing, facts.marker->copy, facts.marker->found} :
            Facts::Marker{destination};
    }, [this, &started, accepted](Outcome outcome, auto const&)
    {
        if (outcome.error)
        {
            moves.remove_if([&started](Move const& other) { return &other == &started; });
            accepted(std::move(outcome));
            return;
        }
        Unask(started.ids);
        for (auto const& id : started.ids)
        {
            auto& torrent = torrents.at(id);
            torrent.moving = true;
            // The latest move the person asked for replaces one that waited.
            torrent.waitingMove.reset();
            torrent.moveError.reset();
            torrent.ApplyIntent();
        }
        PrepareMove(started);
        accepted({});
    });
}

void Engine::State::StartWaitingMove(std::vector<std::string> const& ids, std::string const& destination)
{
    changes.Queue([this, ids, destination]
    {
        std::vector<std::string> present;
        for (auto const& id : ids)
        {
            auto found = torrents.find(id);
            if (found != torrents.end() && found->second.waitingMove &&
                SameFolder(found->second.waitingMove->destination, destination))
                present.push_back(id);
        }
        if (present.empty())
            return;
        StartMove(present, destination, MoveStart::Waited, [this, present](Outcome outcome)
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

void Engine::State::PrepareMove(Move& moving)
{
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
    for (auto const& id : moving.ids)
    {
        auto const& torrent = Working(id);
        handles.push_back(torrent.handle);
        auto physical = torrent.Paths();
        auto logical = torrent.Paths(true);
        // Only a continued member starts with its destination checked.
        bool continued = torrent.facts.marker->stage != MoveStage::Staged;
        for (std::size_t index = 0; index < physical.size(); ++index)
        {
            auto source = std::filesystem::path(Wide(torrent.facts.savePath));
            auto destination = std::filesystem::path(Wide(moving.destination));
            files.push_back({FullPath(source / physical[index]), FullPath(destination / physical[index]),
                FullPath(source / logical[index]), FullPath(destination / logical[index]), continued});
        }
    }
    moving.phase = MovePhase::Preflight;
    // The files already at the destination, and of those the ones that no
    // copy of the torrent's own files put there.
    struct Destination
    {
        std::vector<std::filesystem::path> existing;
        std::vector<std::filesystem::path> found;
    };
    using Checked = std::expected<Destination, Problem>;
    moving.worker->Run([this, handles, files]() -> Checked
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
        Destination destination;
        std::vector<std::filesystem::path> stale;
        for (auto const& file : files)
        {
            // A continued member's unfinished copy is continued; see
            // Payload::Move.
            if (auto partial = Payload::Partial(file.destination); present(partial) && !file.continued)
                stale.push_back(partial);
            if (!present(file.destination) && !present(file.logicalDestination))
                continue;
            if (SamePath(file.source, file.destination))
            {
                destination.existing.push_back(file.destination);
                continue;
            }
            // A link can give one file two paths. Moving it would delete the
            // only copy as its source, so the move stops and says why.
            std::error_code ignored;
            if (std::filesystem::equivalent(file.source, file.destination, ignored) ||
                std::filesystem::equivalent(file.logicalSource, file.logicalDestination, ignored))
                return std::unexpected(Problem{ProblemKind::SameLocation, Utf8(file.destination.wstring())});
            bool sourced = occupied(file.source) || occupied(file.logicalSource);
            // An interrupted move's complete copy stays. Any other file beside
            // a source that holds data belongs to someone else.
            bool alone = SamePath(file.destination, file.logicalDestination) || !present(file.logicalDestination);
            if (file.continued && alone && Payload::IsCopy({file.source, file.destination}))
            {
                destination.existing.push_back(file.destination);
                continue;
            }
            if (sourced)
                return std::unexpected(Problem{ProblemKind::DestinationExists, Utf8(file.destination.wstring())});
            // A file put there by the person or found after a drive letter
            // changed, so only verification can say what it holds.
            destination.existing.push_back(file.destination);
            destination.found.push_back(file.destination);
        }
        // An empty unfinished name would mask the final file after a partial move.
        for (auto const& file : files)
        {
            if (!SamePath(file.source, file.logicalSource) && present(file.logicalDestination) &&
                present(file.source) && !occupied(file.source))
                stale.push_back(file.source);
        }
        Payload::Delete(stale);
        std::sort(destination.found.begin(), destination.found.end(), PathBefore);
        return destination;
    }, [this, &moving](std::expected<Checked, StorageOutcome> checked)
    {
        if (IsShuttingDown())
            return;
        if (!checked)
        {
            FailMove(moving, Problem::From(checked.error(), ProblemKind::MoveFailed));
            return;
        }
        if (!*checked)
        {
            FailMove(moving, checked->error());
            return;
        }
        moving.moved = std::move((*checked)->existing);
        moving.found = std::move((*checked)->found);
        // The checked destination is saved before any file changes, so the
        // next launch may continue this move and keep its complete copies.
        // Its found files are saved with it, so abandoning the move leaves
        // them.
        if (!changes.Queue([this, &moving]
        {
            // ContinueMove dropped the members removed during the preflight.
            std::map<std::string, std::vector<std::string>> found;
            for (auto const& id : moving.ids)
            {
                auto paths = FilePaths(Working(id), moving.destination, false);
                auto logical = FilePaths(Working(id), moving.destination);
                paths.insert(paths.end(), logical.begin(), logical.end());
                for (auto const& path : paths)
                {
                    if (std::binary_search(moving.found.begin(), moving.found.end(), path, PathBefore))
                        found[id].push_back(Utf8(path.wstring()));
                }
            }
            CommitFacts(moving.ids, [found](std::string const& id, Facts& facts)
            {
                if (!facts.marker || facts.marker->stage != MoveStage::Staged)
                    return;
                facts.marker->stage = MoveStage::Placing;
                if (auto paths = found.find(id); paths != found.end())
                    facts.marker->found = paths->second;
            }, [this, &moving](Outcome outcome, auto const&)
            {
                if (outcome.error)
                {
                    FailMove(moving, Problem{ProblemKind::MoveFailed, outcome.detail});
                    return;
                }
                moving.phase = MovePhase::Ready;
                ContinueMove(moving);
            });
        }))
        {
            FailMove(moving, Problem{ProblemKind::StorageOverloaded});
        }
    });
}

bool Engine::State::StopsAtExit(Move const& moving) const
{
    return IsShuttingDown() && (moving.current >= moving.ids.size() || torrents.contains(moving.ids[moving.current]));
}

void Engine::State::ContinueMoves()
{
    // A move that ends leaves the list, and only that move.
    for (auto moving = moves.begin(); moving != moves.end();)
        ContinueMove(*moving++);
}

void Engine::State::ContinueMove(Move& moving)
{
    if (moving.phase == MovePhase::Staging || moving.phase == MovePhase::Saving)
    {
        return;
    }
    // From its copy on, a worker or disk operation owns the current member.
    // Other removed members leave before their turn, so their files stay
    // where they are and their removal does not wait for the move.
    auto member = moving.ids.begin() + moving.current;
    if (moving.phase != MovePhase::Ready && moving.phase != MovePhase::Preflight)
        ++member;
    while (member != moving.ids.end())
    {
        if (torrents.contains(*member))
        {
            ++member;
            continue;
        }
        auto& torrent = Working(*member);
        torrent.moving = false;
        torrent.facts.marker.reset();
        member = moving.ids.erase(member);
    }
    if (moving.phase != MovePhase::Ready)
        return;
    if (moving.current == moving.ids.size())
    {
        moves.remove_if([&moving](Move const& other) { return &other == &moving; });
        ContinueNames();
        return;
    }
    // Exit keeps the move where it is, and the saved task continues it.
    if (IsShuttingDown())
        return;
    auto const& current = Working(moving.ids.at(moving.current));
    auto files = Relocated(moving, current);
    if (auto const& marker = current.facts.marker; marker && marker->copy)
    {
        for (auto& file : files)
        {
            if (SamePath(file.destination, Wide(marker->copy->path)))
                file.proven = marker->copy->bytes;
        }
    }
    std::set<std::filesystem::path, decltype(&PathBefore)> placed(moving.placed.begin(), moving.placed.end(),
        PathBefore);
    std::set<std::filesystem::path, decltype(&PathBefore)> moved(moving.moved.begin(), moving.moved.end(),
        PathBefore);
    // A failure cannot restore a file that an earlier member renamed away, and
    // it leaves a file that was already at the destination there.
    bool split = std::erase_if(files, [&placed, &moved](auto const& file)
        { return moved.contains(file.destination) || placed.contains(file.destination); }) != 0;
    // The remaining members can share files, so none of them may still use one.
    std::vector<lt::torrent_handle> handles;
    for (auto remaining = moving.ids.begin() + moving.current; remaining != moving.ids.end(); ++remaining)
        handles.push_back(Working(*remaining).handle);
    moving.progress = std::make_shared<Payload::Progress>();
    moving.sampled = 0;
    moving.phase = MovePhase::Copying;
    moving.worker->Run([this, handles, files = std::move(files), progress = moving.progress]
    {
        ReleaseFiles(handles);
        progress->placing = true;
        return Payload::Move(files, *progress);
    }, [this, &moving, split](std::expected<std::expected<void, Payload::Failure>, StorageOutcome> placed)
    {
        // Exit cancelled the copy and keeps what it placed for the saved
        // task, or the copy finished as Exit began.
        if (StopsAtExit(moving))
            return;
        if (!placed)
        {
            FailMove(moving, Problem::From(placed.error(), ProblemKind::MoveFailed));
            return;
        }
        if (!*placed)
        {
            auto kind = split || placed->error().kind == MoveFailure::Split ? ProblemKind::MoveInterrupted :
                moving.progress->cancel == MoveCancel::Undo ? ProblemKind::DriveStalled : ProblemKind::MoveFailed;
            FailMove(moving, Problem{kind, placed->error().detail});
            return;
        }
        moving.phase = MovePhase::Moving;
        // Every file is already at the destination, so libtorrent only takes
        // the new folder, without checking the files there. It keeps a part
        // file for skipped files, which only dont_replace moves, and that
        // mode checks every file it finds at the destination.
        auto const& handle = Working(moving.ids.at(moving.current)).handle;
        auto priorities = handle.get_file_priorities();
        bool skips = std::find(priorities.begin(), priorities.end(), lt::dont_download) != priorities.end();
        handle.move_storage(moving.destination,
            skips ? lt::move_flags_t::dont_replace : lt::move_flags_t::reset_save_path_unchecked);
    });
}

std::vector<Payload::File> Engine::State::Relocated(Move const& moving, Torrent const& torrent)
{
    std::filesystem::path from = Wide(torrent.facts.savePath);
    std::filesystem::path to = Wide(moving.destination);
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
    auto found = std::find_if(moves.begin(), moves.end(), [this, &handle](Move const& moving)
        { return moving.phase == MovePhase::Moving && Working(moving.ids.at(moving.current)).handle == handle; });
    if (found == moves.end() || StopsAtExit(*found))
    {
        return;
    }
    auto& moving = *found;
    // Results can outlive recovery and arrive during another move of this
    // handle. Establish its current location instead of trusting that event.
    moving.phase = MovePhase::Observing;
    moving.worker->Run([this, handle]
    {
        ReleaseFiles({handle});
        return handle.status(lt::torrent_handle::query_save_path);
    }, [this, &moving, handle, failure](std::expected<lt::torrent_status, StorageOutcome> status)
    {
        if (StopsAtExit(moving))
            return;
        if (!status || status->moving_storage)
        {
            if (!status)
                log.Write("move", moving.ids.at(moving.current), status.error().detail);
            moving.phase = MovePhase::Moving;
            // Asks for another acknowledgement, as RecoverMoves does.
            handle.flush_cache();
            return;
        }
        // The files are at the destination, but libtorrent kept the old folder.
        if (!SameFolder(status->save_path, moving.destination))
        {
            FailMove(moving, Problem{ProblemKind::MoveInterrupted, failure});
            return;
        }
        if (moving.shared)
        {
            auto paths = FilePaths(Working(moving.ids.at(moving.current)), moving.destination, false);
            moving.placed.insert(moving.placed.end(), paths.begin(), paths.end());
        }
        SaveMove(moving);
    });
}

void Engine::State::SaveMove(Move& moving)
{
    moving.phase = MovePhase::Saving;
    auto id = moving.ids.at(moving.current);
    // The member ends at once, and its sources go after the new folder is
    // saved. A copied source that a later member also uses stays until that
    // member's own sources go, so its old folder stays complete until then.
    std::set<std::filesystem::path, decltype(&PathBefore)> later(PathBefore);
    for (auto member = moving.ids.begin() + moving.current + 1; moving.shared && member != moving.ids.end(); ++member)
    {
        for (auto const& file : Relocated(moving, Working(*member)))
            later.insert(file.source);
    }
    std::vector<std::filesystem::path> sources;
    for (auto const& file : Relocated(moving, Working(id)))
    {
        if (!later.contains(file.source))
            sources.push_back(file.source);
    }
    // The files a move places are the ones the torrent already had, so only
    // files found at the destination, or an interrupted move's files that can
    // be in two folders, need verification.
    auto paths = FilePaths(Working(id), moving.destination, false);
    bool verifies = Contains(moving.recovering, id) || std::any_of(paths.begin(), paths.end(), [&moving](auto const& path)
        { return std::binary_search(moving.found.begin(), moving.found.end(), path, PathBefore); });
    // The old files are a task from the save of the new folder on, so a cut
    // before their deletion leaves it to the next start.
    moving.cleaning = Task::Deletion{{id}, DeletionMode::Permanent, sources,
        {FullPath(Wide(Working(id).facts.savePath))}, Working(id).Name(), true};
    auto relocate = [destination = moving.destination, verifies](std::string const&, Facts& facts)
    {
        facts.savePath = destination;
        facts.finalFolder.clear();
        facts.marker.reset();
        facts.verifyFiles |= verifies;
    };
    auto settle = [this, &moving, id, sources = std::move(sources), origin = FullPath(Wide(Working(id).facts.savePath))]
    {
        auto& torrent = Working(id);
        torrent.unsaved = true;
        // Without a recheck to report them, the finished files are read again
        // at the new folder.
        torrent.completedFiles.clear();
        torrent.filesPending = true;
        torrent.namePhase = NamePhase::Pending;
        torrent.renameAt = {};
        EndMove(torrent, std::nullopt);
        CleanMove(moving, sources, origin);
    };
    if (!changes.Queue([this, &moving, id, relocate, settle]
    {
        // A removed torrent has left the document. Its new folder decides
        // only where its removal deletes its files.
        if (!torrents.contains(id))
        {
            relocate(id, Working(id).facts);
            settle();
            return;
        }
        CommitFacts({id}, relocate, [this, &moving, settle](Outcome outcome, auto const&)
        {
            if (outcome.error)
            {
                moving.cleaning.reset();
                FailMove(moving, Problem{ProblemKind::MoveInterrupted, outcome.detail});
                return;
            }
            settle();
        });
    }))
    {
        moving.cleaning.reset();
        FailMove(moving, Problem{ProblemKind::MoveInterrupted});
    }
}

// The sources are deleted only after the new folder is saved, so a crash never
// leaves a saved folder whose files are partly gone. A failure only leaves
// files behind, because the torrent is complete at its destination.
void Engine::State::CleanMove(Move& moving, std::vector<std::filesystem::path> sources, std::filesystem::path origin)
{
    moving.phase = MovePhase::Cleaning;
    if (StopsAtExit(moving))
        return;
    // A removed member's old files have no task, so Exit lets their deletion
    // finish.
    auto stop = torrents.contains(moving.ids.at(moving.current)) ? exitStop.get_token() : std::stop_token();
    moving.worker->Run([sources = std::move(sources), origin = std::move(origin), stop]
    {
        Erase(sources, DeletionMode::Permanent, {origin}, stop);
    }, [this, &moving](StorageOutcome outcome)
    {
        // Exit stopped the deletion, and the saved task finishes it.
        if (StopsAtExit(moving))
            return;
        moving.cleaning.reset();
        if (outcome.failure)
            log.Write("move", moving.ids.at(moving.current), outcome.detail);
        ++moving.current;
        moving.phase = MovePhase::Ready;
        ContinueMove(moving);
    });
}

void Engine::State::FailMove(Move& moving, Problem problem)
{
    // A failed preflight reaches every member; a later failure the current
    // one and every waiting member that shares a file with a failed one,
    // because moving that member would take the file from the failed
    // member's folder. The rest of the group still moves.
    auto first = moving.ids.begin() + moving.current;
    auto last = moving.phase == MovePhase::Preflight ? moving.ids.end() : first + 1;
    std::set<std::filesystem::path, decltype(&PathBefore)> held(PathBefore);
    for (auto member = first; member != last; ++member)
    {
        auto paths = FilePaths(Working(*member), moving.destination, false);
        held.insert(paths.begin(), paths.end());
    }
    for (auto member = last; member != moving.ids.end();)
    {
        // A member removed while this move saved leaves at its turn instead.
        auto paths = !torrents.contains(*member) ? std::vector<std::filesystem::path>{} :
            FilePaths(Working(*member), moving.destination, false);
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
    // A kept marker waits for the person, and is no unfinished task:
    // continuing a failed move on its own would repeat the failure.
    auto clears = [&moving, interrupted = problem.kind == ProblemKind::MoveInterrupted](std::string const& id)
        { return !interrupted && !Contains(moving.recovering, id); };
    // The failure deleted the unfinished copies, so their proof goes too.
    auto settle = [](Facts& facts, bool clear)
    {
        if (clear)
            facts.marker.reset();
        else if (facts.marker)
        {
            facts.marker->failed = true;
            facts.marker->copy.reset();
        }
    };
    // A marker whose clearing was not saved stays, and waits like a kept one.
    auto end = [this, &moving, failed, problem, clears, settle](bool saved)
    {
        for (auto const& id : failed)
        {
            settle(Working(id).facts, clears(id) && saved);
            EndMove(Working(id), problem);
        }
        moving.current += failed.size();
        moving.phase = MovePhase::Ready;
        ContinueMove(moving);
    };
    moving.phase = MovePhase::Saving;
    if (!changes.Queue([this, failed, clears, settle, end]
    {
        auto document = Saved();
        for (auto const& id : failed)
        {
            if (torrents.contains(id))
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
    if (!torrent.moving)
        return std::nullopt;
    for (auto const& moving : moves)
    {
        auto current = moving.ids.begin() + moving.current;
        auto member = std::find(current, moving.ids.end(), torrent.torrentId);
        if (member == moving.ids.end())
            continue;
        if (member != current || !moving.progress)
            return 0.0;
        auto total = moving.progress->total.load();
        return total == 0 ? 0.0 : double(moving.progress->done.load()) / double(total);
    }
    return std::nullopt;
}

void Engine::State::SampleMoves(std::chrono::steady_clock::duration elapsed)
{
    for (auto& moving : moves)
    {
        auto copied = moving.progress ? moving.progress->copied.load() : 0;
        moving.rate = double(copied - moving.sampled) / std::chrono::duration<double>(elapsed).count();
        moving.sampled = copied;
    }
}

void Engine::State::RecoverMoves()
{
    for (auto const& moving : moves)
    {
        // Releasing files queues behind the move. Its acknowledgement lets
        // recovery observe the outcome without repeating the move.
        if (moving.phase == MovePhase::Moving)
            Working(moving.ids.at(moving.current)).handle.flush_cache();
    }
}

}
