#include "Engine/State.h"
#include <algorithm>

namespace tt
{
namespace
{
// Bounds the wait for the moves to stop, pending priority edits, torrent
// pauses and the final transfer sample, so that a lost acknowledgement or a
// drive that stopped responding cannot keep Exit from saving well within the
// 10 seconds after which the desktop ends the process.
constexpr auto phaseTimeout = std::chrono::seconds(2);
}

void Engine::State::Shutdown(std::function<void(ShutdownEnd end)> report)
{
    if (shutdown)
    {
        return;
    }
    if (connectionTest)
        ReleaseConnectionTest(connectionTest->connectionId, ConnectionPhase::Cancelled);
    // Taken before the cancel, which ends the jobs that the tasks name.
    bool ready = startup == Startup::Ready;
    if (ready)
        exitTasks = Tasks();
    StartFinalSave();
    CancelFileWork();
    StopIndex();
    if (session)
    {
        // Let cancelled DHT callbacks drain while checkpoints are saved.
        lt::settings_pack pack;
        pack.set_bool(lt::settings_pack::enable_dht, false);
        session->apply_settings(std::move(pack));
    }
    if (crawlerBatches.empty() || crawlerSaveFailed)
    {
        catalogue.Interrupt();
        indexWork.Interrupt();
    }
    FailReading(Failure(ErrorCode::ShuttingDown));
    Discard([](Preview const&) { return true; });
    log.Write("shutdown", "", "requested");
    shutdown = std::move(report);
    // The tasks are saved before anything waits, so an end at 10 seconds
    // loses none of them.
    if (ready && !changes.Queue([this]
    {
        changes.Commit([document = Saved()] { return document.ToJson(); }, [this](Outcome outcome)
        {
            if (outcome.error)
                FailFinalSave(outcome.detail);
        });
    }))
        FailFinalSave();
}

void Engine::State::CancelFileWork()
{
    for (auto const& moving : moves)
    {
        auto none = MoveCancel::None;
        if (moving.progress && StopsAtExit(moving))
            moving.progress->cancel.compare_exchange_strong(none, MoveCancel::Keep);
    }
    exitStop.request_stop();
}

void Engine::State::StartFinalSave()
{
    saveFailure.reset();
    // Set first, so a hash save that is refused at once fails this attempt.
    shutdownPhase = ShutdownPhase::Draining;
    phaseStarted.reset();
    for (auto& [id, torrent] : torrents)
    {
        if (torrent.hashError)
        {
            RecordHashes(torrent);
        }
    }
}

// Records that a save during shutdown failed; it does nothing before
// shutdown. A failure without a cause keeps a cause already recorded.
void Engine::State::FailFinalSave(std::optional<std::string> cause)
{
    if (!IsShuttingDown())
    {
        return;
    }
    if (cause)
    {
        saveFailure = std::move(cause);
    }
    else if (!saveFailure)
    {
        saveFailure.emplace();
    }
}

// Shutdown waits for queued changes and additions and for the moves to stop,
// saves the proofs of their copies, pauses every torrent, saves every torrent
// a final time, and then waits for the file work that may still use the
// session.
void Engine::State::ContinueShutdown()
{
    if (!shutdown)
    {
        return;
    }
    if (!session)
    {
        if (startup == Startup::Failed && store.IsIdle() && log.IsFlushed())
        {
            Finish();
        }
        return;
    }
    // A save that fails now would fail again, so Exit ends as after a crash.
    // The tasks and markers saved before it still hold the work.
    if (saveFailure)
    {
        EndAbruptly("save_failed");
        return;
    }
    if (!changes.IsIdle() || !additions.empty())
    {
        return;
    }
    // A move has stopped once its worker is idle, or once its copy waits,
    // parked, for the process to end. The move of a removed torrent has no
    // task, so Exit waits for it to finish its current member.
    auto stopped = [this](Move const& moving)
    {
        return StopsAtExit(moving) && (moving.worker->IsIdle() || (moving.progress && moving.progress->parked));
    };
    auto finishing = [this](Move const& moving) { return !StopsAtExit(moving); };
    auto now = std::chrono::steady_clock::now();
    if (!phaseStarted)
    {
        phaseStarted = now;
    }
    bool expired = now - *phaseStarted >= phaseTimeout;
    if (expired && !indexWork.IsIdle() && (crawlerBatches.empty() || crawlerSaveFailed))
    {
        catalogue.Interrupt();
        indexWork.Interrupt();
        sources.Interrupt();
    }
    if (shutdownPhase == ShutdownPhase::Draining)
    {
        if (std::any_of(torrents.begin(), torrents.end(), [](auto const& entry)
            { return entry.second.recovery && entry.second.recovery->phase == DiskPhase::Reloading; }))
            return;
        if ((!expired && !std::all_of(moves.begin(), moves.end(), stopped)) ||
            std::any_of(moves.begin(), moves.end(), finishing))
        {
            return;
        }
        if (RecordCopies())
        {
            return;
        }
        bool pending = false;
        for (auto& [id, torrent] : torrents)
        {
            QueryPriorities(torrent);
            if (!torrent.priorityCompletion)
            {
                continue;
            }
            if (!expired)
            {
                pending = true;
                continue;
            }
            std::exchange(torrent.priorityCompletion, nullptr)({ErrorCode::RecoveryRequired});
            log.Write("edit", id, "recovery_required");
        }
        if (pending)
        {
            return;
        }
        shutdownPhase = ShutdownPhase::Pausing;
        phaseStarted = now;
        expired = false;
        // A check confirms its pause only after its reads return, which a
        // drive that stopped responding may never do.
        if (!session->is_paused())
        {
            for (auto const& [id, torrent] : torrents)
            {
                if (!torrent.restore && !bool(torrent.handle.flags() & lt::torrent_flags::paused) &&
                    torrent.status.state != lt::torrent_status::checking_files)
                {
                    pausing.push_back(torrent.handle);
                }
            }
        }
        session->pause();
    }
    if (shutdownPhase == ShutdownPhase::Pausing)
    {
        if (!pausing.empty() && !expired)
        {
            return;
        }
        for (auto const& [id, torrent] : torrents)
        {
            if (torrent.checkpointPhase != CheckpointPhase::Idle)
            {
                return;
            }
        }
        if (!store.IsIdle())
        {
            return;
        }
        // The torrents left in `pausing` did not confirm their pause in time;
        // their resume data still records what they have.
        for (auto const& handle : pausing)
        {
            log.Write("shutdown", handles.at(handle)->torrentId, "pause_timeout");
        }
        shutdownPhase = ShutdownPhase::Sampling;
        phaseStarted = now;
        expired = false;
        transfersRequested = lt::clock_type::now();
        session->post_session_stats();
        log.Write("shutdown", "", "checkpoint");
        for (auto& [id, torrent] : torrents)
        {
            torrent.unsaved = true;
            torrent.retryAt = {};
        }
    }
    if (shutdownPhase == ShutdownPhase::Sampling)
    {
        if (!expired)
            return;
        // The transfers keep the totals of the last sample.
        log.Write("shutdown", "", "sample_timeout");
        shutdownPhase = ShutdownPhase::Saving;
    }
    if (shutdownPhase == ShutdownPhase::Saving)
    {
        CheckpointUnsaved();
        bool pending = std::any_of(torrents.begin(), torrents.end(), [](auto const& entry)
            { return entry.second.checkpointPhase != CheckpointPhase::Idle || entry.second.unsaved; });
        if (pending || !store.IsIdle())
        {
            return;
        }
        shutdownPhase = ShutdownPhase::Ending;
        log.Write("shutdown", "", "saved");
    }
    // A payload, move or abandon job may still use the session, so it ends
    // first. A parked copy waits for the process to end, so nothing waits for
    // it.
    for (auto const& moving : moves)
    {
        if (moving.progress && moving.progress->parked)
            moving.worker->Detach();
    }
    auto abandoned = [](Abandonment const& abandonment)
        { return !abandonment.worker || abandonment.worker->IsIdle(); };
    if (!std::all_of(moves.begin(), moves.end(), stopped) ||
        !std::all_of(abandonments.begin(), abandonments.end(), abandoned) || !payload.IsIdle() ||
        !store.IsIdle() || !indexWork.IsIdle() || !sources.IsIdle() || !log.IsFlushed() ||
        (!crawlerBatches.empty() && !crawlerSaveFailed))
    {
        return;
    }
    // libtorrent's teardown waits for a stalled check's reads, which may
    // never return, so the saved engine ends at once instead.
    if (StalledCheck())
    {
        EndAbruptly("teardown_skipped");
        return;
    }
    // Reporting first lets the desktop leave the screen before the teardown
    // waits on trackers.
    Finish();
    session.reset();
}

void Engine::State::Finish()
{
    auto report = std::exchange(shutdown, nullptr);
    report(ShutdownEnd::Saved);
}

void Engine::State::EndAbruptly(std::string const& reason)
{
    log.Write("shutdown", "", reason);
    std::exchange(shutdown, nullptr)(ShutdownEnd::Abrupt);
}
}
