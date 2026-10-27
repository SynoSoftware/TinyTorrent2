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
    torrent.notifiedError = torrent.moveError;
    if (!shown)
        Notify(ToastKind::Error, torrent, outcome.detail, torrent.moveError->Code());
}

void Engine::State::PrepareMove(Move& moving)
{
    struct Location
    {
        std::string torrentId;
        std::filesystem::path source;
        std::filesystem::path destination;
        std::filesystem::path logicalSource;
        std::filesystem::path logicalDestination;
        std::uintmax_t size;
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
        auto const& layout = torrent.names->metadata->layout();
        std::size_t position = 0;
        for (auto index : layout.file_range())
        {
            if (layout.pad_file_at(index))
                continue;
            auto source = std::filesystem::path(Wide(torrent.facts.savePath));
            auto destination = std::filesystem::path(Wide(moving.destination));
            files.push_back({id, FullPath(source / physical[position]), FullPath(destination / physical[position]),
                FullPath(source / logical[position]), FullPath(destination / logical[position]),
                static_cast<std::uintmax_t>(layout.file_size(index)), continued});
            ++position;
        }
    }
    moving.phase = MovePhase::Preflight;
    // The files already at the destination, and of those the ones that no
    // copy of the torrent's own files put there.
    struct Destination
    {
        std::vector<std::filesystem::path> existing;
        std::vector<std::filesystem::path> found;
        std::set<std::string> checks;
        std::map<std::string, Problem> problems;
    };
    moving.worker->Run([this, handles, files]() -> Destination
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
            {
                destination.problems.emplace(file.torrentId,
                    Problem{ProblemKind::SameLocation, Utf8(file.destination.wstring())});
                continue;
            }
            bool sourced = occupied(file.source) || occupied(file.logicalSource);
            // A continued move already established which copies it placed.
            bool alone = SamePath(file.destination, file.logicalDestination) || !present(file.logicalDestination);
            if (file.continued && alone && Payload::IsCopy({file.source, file.destination}))
            {
                destination.existing.push_back(file.destination);
                continue;
            }
            auto const& target = present(file.logicalDestination) ? file.logicalDestination : file.destination;
            if (!std::filesystem::is_regular_file(target) || std::filesystem::file_size(target) != file.size)
            {
                destination.problems.emplace(file.torrentId,
                    Problem{ProblemKind::DestinationExists, Utf8(target.wstring())});
                continue;
            }
            // Pre-existing files stay if this move is later abandoned.
            destination.existing.push_back(file.destination);
            destination.found.push_back(file.destination);
            if (!sourced)
                destination.checks.insert(file.torrentId);
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
    }, [this, &moving](std::expected<Destination, StorageOutcome> checked)
    {
        if (IsShuttingDown())
            return;
        if (!checked)
        {
            FailMove(moving, Problem::From(checked.error(), ProblemKind::MoveFailed));
            return;
        }
        moving.moved = std::move(checked->existing);
        moving.found = std::move(checked->found);
        moving.checks = std::move(checked->checks);
        moving.problems = std::move(checked->problems);
        // Fail before placing anything a failed member still uses.
        std::stable_partition(moving.ids.begin(), moving.ids.end(), [&moving](auto const& id)
            { return moving.problems.contains(id); });
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
    if (auto problem = moving.problems.find(current.torrentId); problem != moving.problems.end())
    {
        FailMove(moving, problem->second);
        return;
    }
    auto files = Relocated(moving.destination, current);
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
    std::vector<Payload::File> contents;
    auto logical = current.Paths(true);
    for (auto const& path : current.Paths())
    {
        auto source = FullPath(std::filesystem::path(Wide(current.facts.savePath)) / path);
        auto target = FullPath(std::filesystem::path(Wide(moving.destination)) / path);
        bool existing = !SamePath(source, target) && (moved.contains(target) || placed.contains(target));
        contents.push_back({std::move(source), std::move(target), 0, existing});
    }
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
    moving.worker->Run([this, handles, files = std::move(files), contents = std::move(contents), logical,
        origin = current.facts.savePath, destination = moving.destination, progress = moving.progress]
        () mutable -> std::expected<Payload::Reconciled, Payload::Failure>
    {
        ReleaseFiles(handles);
        progress->placing = true;
        Payload::Reconciled result;
        if (std::any_of(contents.begin(), contents.end(), [](auto const& file) { return file.existing; }))
        {
            for (std::size_t index = 0; index < contents.size(); ++index)
            {
                auto& file = contents[index];
                auto source = FullPath(std::filesystem::path(Wide(origin)) / logical[index]);
                auto target = FullPath(std::filesystem::path(Wide(destination)) / logical[index]);
                if (!std::filesystem::exists(file.source) && std::filesystem::exists(source))
                    file.source = std::move(source);
                if (std::filesystem::exists(target))
                    file.destination = std::move(target);
            }
            auto handle = handles.front();
            auto metadata = handle.torrent_file();
            Payload::Pieces pieces{metadata, metadata->v2() ? handle.piece_layers() :
                std::vector<std::vector<lt::sha256_hash>>{},
                [this, handle](lt::piece_index_t piece) { return ReadPiece(handle, piece); }};
            auto merged = Payload::Reconcile(pieces, contents, *progress);
            if (!merged)
                return std::unexpected(merged.error());
            result = *merged;
            ReleaseFiles(handles);
        }
        auto placed = Payload::Move(files, *progress);
        if (!placed)
            return std::unexpected(placed.error());
        return result;
    }, [this, &moving, split](std::expected<std::expected<Payload::Reconciled, Payload::Failure>, StorageOutcome> placed)
    {
        // Exit cancelled the copy and keeps what it placed for the saved
        // task, or the copy finished as Exit began.
        if (IsShuttingDown())
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
        moving.pieces = std::move((**placed).pieces);
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

std::vector<Payload::File> Engine::State::Relocated(std::string const& destination, Torrent const& torrent)
{
    std::filesystem::path from = Wide(torrent.facts.savePath);
    std::filesystem::path to = Wide(destination);
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
    if (found == moves.end() || IsShuttingDown())
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
        if (IsShuttingDown())
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
        if (moving.pieces)
            ReloadMove(moving);
        else
            SaveMove(moving);
    });
}

void Engine::State::ReloadMove(Move& moving)
{
    auto id = moving.ids.at(moving.current);
    auto handle = Working(id).handle;
    auto replacement = std::make_shared<Replacement>();
    moving.phase = MovePhase::Saving;
    moving.worker->Run([this, handle, replacement, pieces = *moving.pieces, logical = Working(id).Paths(true),
        stop = exitStop.get_token()]
    {
        auto& params = replacement->params;
        params = handle.get_resume_data(lt::torrent_handle::save_info_dict);
        params.have_pieces = pieces;
        params.verified_pieces.clear();
        params.unfinished_pieces.clear();
        std::size_t position = 0;
        for (auto index : params.ti->layout().file_range())
        {
            if (params.ti->layout().pad_file_at(index))
                continue;
            auto const& name = logical[position++];
            if (!std::filesystem::exists(std::filesystem::path(Wide(params.save_path)) / name))
                continue;
            auto path = Utf8(name.wstring());
            if (path == params.ti->layout().file_path(index))
                params.renamed_files.erase(index);
            else
                params.renamed_files[index] = std::move(path);
        }
        params.flags &= ~(lt::torrent_flags::seed_mode | lt::torrent_flags::auto_managed);
        params.flags |= lt::torrent_flags::paused | lt::torrent_flags::no_verify_files;
        // Exit pauses and saves the torrent through its handle, so once Exit
        // has started the handle stays, and the saved task continues the move.
        ReleaseFiles({handle});
        if (stop.stop_requested())
            throw std::runtime_error("shutting_down");
        ReloadStorage(handle, *replacement);
        params.flags &= ~lt::torrent_flags::no_verify_files;
    }, [this, &moving, id, handle, replacement](StorageOutcome outcome)
    {
        if (outcome.failure)
        {
            if (replacement->removed)
            {
                auto& torrent = Working(id);
                handles.erase(handle);
                seedQueries.erase(handle);
                auto& params = replacement->params;
                params.save_path = torrent.facts.savePath;
                params.have_pieces.clear();
                params.flags &= ~lt::torrent_flags::no_verify_files;
                torrent.restore = std::move(params);
                torrent.pendingCheckpoint.reset();
                torrent.checkpointPhase = CheckpointPhase::Idle;
            }
            if (!IsShuttingDown())
                FailMove(moving, Problem::From(outcome, ProblemKind::MoveInterrupted));
            return;
        }
        auto& torrent = Working(id);
        ReplaceHandle(torrent, *replacement);
        torrent.ApplyIntent();
        // The writer queues this after older checkpoints. The old handle no
        // longer answers alerts, so no stale piece map can follow this one.
        WriteResume(id, std::move(replacement->params), [this, &moving](StorageOutcome outcome)
        {
            // The saved task continues the move: a save of the new folder
            // during Exit would leave the old files without a task.
            if (IsShuttingDown())
                return;
            if (outcome.failure)
            {
                FailMove(moving, Problem::From(outcome, ProblemKind::MoveInterrupted));
                return;
            }
            SaveMove(moving);
        });
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
        for (auto const& file : Relocated(moving.destination, Working(*member)))
            later.insert(file.source);
    }
    std::vector<std::filesystem::path> sources;
    for (auto const& file : Relocated(moving.destination, Working(id)))
    {
        if (!later.contains(file.source))
            sources.push_back(file.source);
    }
    bool verifies = Contains(moving.recovering, id) || moving.checks.contains(id);
    // The old files are a task from the save of the new folder on, so a cut
    // before their deletion leaves it to the next start.
    moving.cleaning = Task::Deletion{{id}, DeletionMode::Permanent, sources,
        {FullPath(Wide(Working(id).facts.savePath))}, Working(id).Name(), true};
    auto relocate = [destination = moving.destination, verifies, reconciled = moving.pieces.has_value()]
        (std::string const&, Facts& facts)
    {
        facts.savePath = destination;
        facts.finalFolder.clear();
        facts.marker.reset();
        facts.verifyFiles = !reconciled && (facts.verifyFiles || verifies);
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
        EndMove(torrent, std::nullopt, false);
        CleanMove(moving, sources, origin);
    };
    if (!changes.Queue([this, &moving, id, relocate, settle]
    {
        // A removed torrent has left the document. Its new folder decides
        // only where its removal deletes its files. The save before the
        // sources go replaces the saved task that gives its files back with
        // the one that deletes the sources.
        if (!torrents.contains(id))
        {
            relocate(id, Working(id).facts);
            changes.Commit([document = Saved()] { return document.ToJson(); }, [this, id, settle](Outcome outcome)
            {
                if (outcome.error)
                    log.Write("move", id, outcome.detail);
                settle();
            });
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
    if (IsShuttingDown())
        return;
    moving.worker->Run([sources = std::move(sources), origin = std::move(origin), stop = exitStop.get_token()]
    {
        Erase(sources, DeletionMode::Permanent, {origin}, stop);
    }, [this, &moving](StorageOutcome outcome)
    {
        // Exit stopped the deletion, and the saved task finishes it.
        if (IsShuttingDown())
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
            auto& torrent = Working(id);
            settle(torrent.facts, clears(id) && saved);
            // Only a failure that left the files complete where they were can
            // run again by itself.
            EndMove(torrent, problem, !torrent.facts.marker && SameFolder(moving.destination, torrent.facts.finalFolder));
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

void Engine::State::EndMove(Torrent& torrent, std::optional<Problem> problem, bool finalFolder)
{
    torrent.moving = false;
    torrent.moveError = problem;
    if (!problem)
    {
        torrent.moveRetry = StorageRetry::Automatic;
        // The names are prepared again at the new folder.
        torrent.nameError.reset();
        ContinueNames();
        return;
    }
    torrent.ApplyIntent();
    torrent.notifiedError = problem;
    log.Write("move", torrent.torrentId, ToString(problem->kind));
    // A move to the final folder is the engine's own work, so its failure
    // waits for the destination to answer and runs once more, as a storage
    // error does, and the message bar reports it while it lasts.
    if (finalFolder)
    {
        torrent.moveRetry = torrent.moveRetry == StorageRetry::Resumed ? StorageRetry::Person : StorageRetry::Stopped;
        Invalidate(torrent.torrentId);
        return;
    }
    Notify(ToastKind::Error, torrent, problem->detail, problem->Code());
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
